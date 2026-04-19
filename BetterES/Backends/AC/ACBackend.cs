using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using BetterES.Core;

namespace BetterES.Backends.AC
{
    public sealed class ACBackend : IEngineBackend
    {
        public string Name => "Assetto Corsa";

        private const string PHYSICS_MMF = "acpmf_physics";
        private const string STATIC_MMF  = "acpmf_static";

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct SPageFilePhysics
        {
            public int PacketId;
            public float Gas;
            public float Brake;
            public float Fuel;
            public int Gear;
            public int Rpms;
            public float SteerAngle;
            public float SpeedKmh;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct SPageFileStatic
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 30)]
            public byte[] SMVersion;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 30)]
            public byte[] ACVersion;
            public int NumberOfSessions;
            public int NumCars;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 66)]
            public byte[] CarModel;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 66)]
            public byte[] Track;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 66)]
            public byte[] PlayerName;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 66)]
            public byte[] PlayerSurname;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 66)]
            public byte[] PlayerNick;
            public int SectorCount;
            public float MaxTorque;
            public float MaxPower;
            public int MaxRpm;
            public float MaxFuel;
        }

        private static string DecodeString(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            try {
                string full = System.Text.Encoding.Unicode.GetString(bytes);
                int len = full.IndexOf('\0');
                if (len >= 0) return full.Substring(0, len);
                return full;
            } catch { return ""; }
        }

        private MemoryMappedViewAccessor? _physicsAccessor;
        private MemoryMappedFile? _physicsMmf;
        private Thread? _readThread;
        private volatile bool _running;

        private double _currentRpm;
        private double _maxRpm = 8000;
        private int _currentGear = -1;
        private double _speedKmh;
        private double _turboBoost;
        private double _throttle;
        private readonly object _lock = new();

        private Action<string>? _log;
        private int _lastPacketId = -1;
        private int _packetCount;

        public bool Initialize(int processId, Action<string> log)
        {
            _log = log;

            log("═══════════════════════════════════════");
            log("Connecting to Assetto Corsa...");
            log("═══════════════════════════════════════");

            var acNames = new[] { "acs", "acs_x86", "AssettoCorsa", "ace" };
            var acProcess = acNames
                .SelectMany(System.Diagnostics.Process.GetProcessesByName)
                .FirstOrDefault();

            if (acProcess == null)
            {
                log("✗ Assetto Corsa process not found.");
                log("  Checked: acs.exe, acs_x86.exe, AssettoCorsa.exe");
                log("  → Launch Assetto Corsa first, then click Connect.");
                return false;
            }
            log($"✓ Found AC process: {acProcess.ProcessName} (PID: {acProcess.Id})");

            SPageFileStatic staticData = default;
            bool staticOk = false;
            int staticStructSize = Marshal.SizeOf<SPageFileStatic>();

            for (int attempt = 1; attempt <= 5; attempt++)
            {
                MemoryMappedFile? staticMmf = null;
                try
                {
                    staticMmf = MemoryMappedFile.OpenExisting(STATIC_MMF);
                    int[] trySizes = { staticStructSize, 512, 256, 128 };
                    MemoryMappedViewAccessor? staticAccessor = null;
                    foreach (int sz in trySizes)
                    {
                        try { staticAccessor = staticMmf.CreateViewAccessor(0, sz); break; }
                        catch { staticAccessor?.Dispose(); }
                    }

                    if (staticAccessor == null)
                    {
                        log($"  Cannot create view of '{STATIC_MMF}' (attempt {attempt}/5)");
                        staticMmf.Dispose();
                        if (attempt < 5) Thread.Sleep(500);
                        continue;
                    }

                    using (staticAccessor)
                    {
                        int readSize = Math.Min(staticStructSize, (int)staticAccessor.Capacity);
                        byte[] buf = new byte[readSize];
                        staticAccessor.ReadArray(0, buf, 0, readSize);
                        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
                        try { staticData = Marshal.PtrToStructure<SPageFileStatic>(handle.AddrOfPinnedObject()); }
                        finally { handle.Free(); }
                    }
                    staticMmf.Dispose();
                    staticOk = true;
                    break;
                }
                catch (FileNotFoundException)
                {
                    staticMmf?.Dispose();
                    if (attempt < 5) { log($"  Waiting for AC shared memory (attempt {attempt}/5)..."); Thread.Sleep(500); }
                }
                catch (UnauthorizedAccessException)
                {
                    staticMmf?.Dispose();
                    log($"  ✗ Access denied opening '{STATIC_MMF}' — run both as same privilege level.");
                    if (attempt < 5) Thread.Sleep(500);
                }
                catch (Exception ex)
                {
                    staticMmf?.Dispose();
                    log($"  ✗ Shared memory error: {ex.GetType().Name}: {ex.Message}");
                    if (attempt < 5) Thread.Sleep(500);
                }
            }

            if (!staticOk)
            {
                log("✗ AC shared memory not found after 5 attempts.");
                log("  → AC must be in a session (car on track), not main menu");
                return false;
            }

            lock (_lock) { _maxRpm = staticData.MaxRpm > 0 ? staticData.MaxRpm : 8000; }

            log($"✓ Connected to AC shared memory");
            log($"  Car: {DecodeString(staticData.CarModel)}");
            log($"  Track: {DecodeString(staticData.Track)}");
            log($"  Max RPM: {staticData.MaxRpm}");

            try
            {
                _physicsMmf = MemoryMappedFile.OpenExisting(PHYSICS_MMF);
                int physSize = Marshal.SizeOf<SPageFilePhysics>();
                _physicsAccessor = _physicsMmf.CreateViewAccessor(0, Math.Max(physSize, 1024));
                log($"✓ Physics shared memory opened ({_physicsAccessor.Capacity} byte view)");
            }
            catch (Exception ex)
            {
                log($"✗ Failed to open physics memory: {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            _running = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "AC-Telemetry" };
            _readThread.Start();
            log("✓ Telemetry reader started");

            Thread.Sleep(300);
            double? testRpm = ReadRpm();
            if (testRpm.HasValue)
                log($"✓ Initial RPM: {testRpm.Value:F0}");
            else
                log("⚠ No RPM yet — start the engine in AC");

            log("\n═══════════════════════════════════════");
            log("✓ AC backend ready");
            log("═══════════════════════════════════════");
            return true;
        }

        public double? ReadRpm() { lock (_lock) { return _currentRpm >= 0 ? _currentRpm : null; } }
        public double? ReadMaxRpm() { lock (_lock) { return _maxRpm > 0 ? _maxRpm : null; } }
        public double? ReadTorque() { return null; }
        public int ReadGear() { lock (_lock) { return _currentGear; } }
        public double ReadSpeed() { lock (_lock) { return _speedKmh; } }
        public double? ReadBoost() { lock (_lock) { return _turboBoost; } }
        public double? ReadThrottle() { lock (_lock) { return _throttle; } }

        public void SetThrottle(double throttle) { }
        public void StartEngine(Action<string> log) { log("AC backend: read-only"); }
        public void StopEngine() { }

        public void Dispose()
        {
            _running = false;
            _readThread?.Join(2000);
            _physicsAccessor?.Dispose();
            _physicsMmf?.Dispose();
        }

        private void ReadLoop()
        {
            // Force timer resolution for accurate sleep
            timeBeginPeriod(1);

            var sw = Stopwatch.StartNew();
            long nextMs = sw.ElapsedMilliseconds;
            const long intervalMs = 5; // 200Hz read rate

            while (_running)
            {
                try
                {
                    if (_physicsAccessor == null) { Thread.Sleep(500); continue; }

                    _physicsAccessor.Read(0, out int packetId);
                    if (packetId == 0) { Thread.Sleep(100); continue; }

                    if (packetId != _lastPacketId)
                    {
                        _physicsAccessor.Read(20, out int rawRpm);
                        _physicsAccessor.Read(16, out int rawGear);
                        _physicsAccessor.Read(28, out float rawSpeed);
                        _physicsAccessor.Read(4, out float rawGas);
                        _physicsAccessor.Read(276, out float rawBoost);

                        lock (_lock)
                        {
                            _currentRpm = rawRpm;
                            _currentGear = rawGear - 1;
                            _speedKmh = rawSpeed;
                            _throttle = rawGas;
                            _turboBoost = rawBoost;
                        }

                        _lastPacketId = packetId;

                        if (_packetCount < 5)
                        {
                            _log?.Invoke($"[AC Data] RPM={rawRpm} Gear={rawGear - 1} Speed={rawSpeed:F1}");
                            _packetCount++;
                        }
                    }
                }
                catch { }

                // Precise 5ms interval using spin-wait for accuracy
                nextMs += intervalMs;
                long remaining = nextMs - sw.ElapsedMilliseconds;
                if (remaining > 2)
                    Thread.Sleep((int)(remaining - 1));
                while (sw.ElapsedMilliseconds < nextMs)
                    Thread.SpinWait(20);
            }

            timeEndPeriod(1);
        }

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);
    }
}
