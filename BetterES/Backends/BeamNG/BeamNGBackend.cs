using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using BetterES.Core;

namespace BetterES.Backends.BeamNG
{
    /// <summary>
    /// BeamNG.drive telemetry backend via OutGauge UDP protocol.
    /// 
    /// BeamNG setup:
    ///   Settings → Other → Enable OutGauge
    ///   Address: 127.0.0.1
    ///   Port: 4444
    ///   Max update rate: 120
    /// </summary>
    public sealed class BeamNGBackend : IEngineBackend
    {
        public string Name => "BeamNG.drive";

        // ── OutGauge packet struct (56 bytes) ─────────────────────────

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct OutGaugePacket
        {
            public uint   Time;          // 4 bytes
            public char   Car0;          // 1 byte
            public char   Car1;          // 1 byte
            public char   Car2;          // 1 byte
            public char   Car3;          // 1 byte (null-terminated car name)
            public ushort Flags;         // 2 bytes
            public byte   Gear;          // 1 byte (0=R, 1=N, 2+=forward)
            public byte   PLID;          // 1 byte
            public float  Speed;         // 4 bytes (m/s)
            public float  RPM;           // 4 bytes
            public float  Turbo;         // 4 bytes (bar)
            public float  EngTemp;       // 4 bytes (C)
            public float  Fuel;          // 4 bytes (0-1)
            public float  OilPressure;   // 4 bytes (bar)
            public float  OilTemp;       // 4 bytes (C)
            public uint   DashLights;    // 4 bytes (bit flags)
            public uint   ShowLights;    // 4 bytes (bit flags)
            public float  Throttle;      // 4 bytes (0-1)
            public float  Brake;         // 4 bytes (0-1)
            public float  Clutch;        // 4 bytes (0-1)
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
            public string Display1;      // 16 bytes (fuel/turbo display)
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
            public string Display2;      // 16 bytes (misc display)
        }

        private const int PACKET_SIZE = 56;

        // ── State ─────────────────────────────────────────────────────

        private UdpClient? _udpClient;
        private Thread? _readThread;
        private volatile bool _running;
        private readonly int _port;

        private double _currentRpm;
        private double _maxRpm = 8000;
        private int _currentGear = -1;
        private double _speedKmh;
        private double _turboBar;
        private double _throttle;
        private readonly object _lock = new();

        private Action<string>? _log;
        private int _packetCount;
        private int _staleCount;
        private DateTime _lastPacketTime;

        public BeamNGBackend(int port = 4444)
        {
            _port = port;
        }

        // ── IEngineBackend ────────────────────────────────────────────

        public bool Initialize(int processId, Action<string> log)
        {
            _log = log;

            log("═══════════════════════════════════════");
            log($"Connecting to BeamNG via OutGauge UDP (port {_port})...");
            log("═══════════════════════════════════════");

            // Open UDP listener
            try
            {
                _udpClient = new UdpClient(_port);
                log($"✓ UDP socket bound to port {_port}");
            }
            catch (SocketException ex)
            {
                log($"✗ Failed to bind UDP port {_port}: {ex.Message}");
                log("  → Is BeamNG running with OutGauge enabled?");
                log("  → Settings → Other → Enable OutGauge → Port 4444");
                log("  → Another app may be using this port");
                return false;
            }

            // Start reader thread
            _running = true;
            _lastPacketTime = DateTime.MinValue;
            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "BeamNG-OutGauge"
            };
            _readThread.Start();
            log("✓ OutGauge listener started");

            // Wait for first packet
            log("Waiting for BeamNG telemetry data...");
            for (int i = 0; i < 50; i++) // 5 second timeout
            {
                Thread.Sleep(100);
                if (_packetCount > 0)
                {
                    log($"✓ First OutGauge packet received");
                    break;
                }
            }

            if (_packetCount == 0)
            {
                log("⚠ No data received yet.");
                log("  Make sure:");
                log("  1. BeamNG is running with a car loaded");
                log("  2. OutGauge is enabled: Settings → Other → OutGauge");
                log($"  3. Port is set to {_port}");
                log("  4. Address is 127.0.0.1");
                log("");
                log("  Continuing to listen...");
            }

            log("\n═══════════════════════════════════════");
            log("✓ BeamNG backend ready");
            log("═══════════════════════════════════════");
            return true;
        }

        public double? ReadRpm()
        {
            lock (_lock) { return _currentRpm > 0 ? _currentRpm : null; }
        }

        public double? ReadMaxRpm()
        {
            lock (_lock) { return _maxRpm > 0 ? _maxRpm : null; }
        }

        public double? ReadTorque()
        {
            return null; // OutGauge doesn't provide torque
        }

        public int ReadGear()
        {
            lock (_lock) { return _currentGear; }
        }

        public double ReadSpeed()
        {
            lock (_lock) { return _speedKmh; }
        }

        public void SetThrottle(double throttle)
        {
            // Cannot control BeamNG remotely — read-only backend
        }

        public void StartEngine(Action<string> log)
        {
            log("BeamNG backend: engine control not available (read-only)");
        }

        public void StopEngine()
        {
            // Nothing to stop
        }

        public void Dispose()
        {
            _log?.Invoke("Disposing BeamNG backend...");
            _running = false;
            _readThread?.Join(2000);
            _udpClient?.Close();
            _udpClient?.Dispose();
            _log?.Invoke("✓ BeamNG backend disposed");
        }

        // ── UDP reader loop ───────────────────────────────────────────

        private void ReadLoop()
        {
            var endpoint = new IPEndPoint(IPAddress.Any, 0);

            while (_running)
            {
                try
                {
                    if (_udpClient == null) { Thread.Sleep(100); continue; }

                    // Non-blocking check
                    if (_udpClient.Available <= 0)
                    {
                        // Check for stale connection
                        if (_packetCount > 0 &&
                            (DateTime.UtcNow - _lastPacketTime).TotalSeconds > 5)
                        {
                            _staleCount++;
                            if (_staleCount == 1)
                                _log?.Invoke("⚠ BeamNG telemetry stalled — is the sim paused?");
                        }
                        Thread.Sleep(5);
                        continue;
                    }

                    byte[] data = _udpClient.Receive(ref endpoint);
                    _lastPacketTime = DateTime.UtcNow;
                    _staleCount = 0;
                    _packetCount++;

                    if (data.Length < PACKET_SIZE) continue;

                    // Parse packet
                    var packet = ParsePacket(data);

                    lock (_lock)
                    {
                        _currentRpm = packet.RPM;
                        _speedKmh = packet.Speed * 3.6f; // m/s → km/h
                        _turboBar = packet.Turbo;
                        _throttle = packet.Throttle;

                        // Gear: 0=R, 1=N, 2=1st, 3=2nd, etc.
                        // Convert to match our convention (0=N, -1=invalid, positive=forward)
                        _currentGear = packet.Gear switch
                        {
                            0 => -1,  // Reverse → show as -1
                            1 => 0,   // Neutral
                            _ => packet.Gear - 1  // 2→1, 3→2, etc.
                        };

                        // Estimate max RPM from observed peaks
                        if (_currentRpm > _maxRpm * 0.95)
                            _maxRpm = Math.Ceiling(_currentRpm / 500) * 500;
                    }

                    // Log first packet details
                    if (_packetCount == 1)
                    {
                        string carName = new string(new[] { packet.Car0, packet.Car1, packet.Car2, packet.Car3 }).TrimEnd('\0');
                        _log?.Invoke($"  Car: {carName}");
                        _log?.Invoke($"  Initial RPM: {packet.RPM:F0}");
                        _log?.Invoke($"  Speed: {packet.Speed * 3.6:F0} km/h");
                    }
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
                {
                    break; // Socket closed during Dispose
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"BeamNG read error: {ex.Message}");
                    Thread.Sleep(500);
                }
            }
        }

        // ── Packet parser ─────────────────────────────────────────────

        private static OutGaugePacket ParsePacket(byte[] data)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                return Marshal.PtrToStructure<OutGaugePacket>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        public double? ReadBoost()
        {
            lock (_lock) { return _turboBar; }
        }

        public double? ReadThrottle()
        {
            lock (_lock) { return _throttle; }
        }

        // ── Properties for UI (backward compat) ──────────────────────

        public double TurboBar
        {
            get { lock (_lock) { return _turboBar; } }
        }

        public int PacketCount => _packetCount;
    }
}
