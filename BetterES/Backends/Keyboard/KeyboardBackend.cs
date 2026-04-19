using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BetterES.Core;

namespace BetterES.Backends.Keyboard
{
    /// <summary>
    /// Engine Simulator backend:
    ///   - DLL injection for bi-directional memory bridge (MMF)
    ///   - Keyboard fallback (PostMessage WM_KEYDOWN/WM_KEYUP)
    /// </summary>
    public sealed class KeyboardBackend : IEngineBackend
    {
        public string Name => "Keyboard + MMF Link";

        // ── Win32: DLL Injection ──────────────────────────────────────

        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint a, bool b, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr h, string p);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string n);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr a, uint s, uint t, uint p);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] b, uint s, out int w);
        [DllImport("kernel32.dll")] private static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr a, uint s, IntPtr fp, IntPtr p, uint c, out IntPtr tid);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] private static extern bool GetExitCodeThread(IntPtr h, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(IntPtr h, IntPtr a, uint s, uint t);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        // ── Win32: Window/Keyboard ────────────────────────────────────

        [DllImport("user32.dll")] private static extern IntPtr GetMainWindow(int pid);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const uint PROCESS_ALL_ACCESS = 0x001F0FFF;
        private const uint MEM_COMMIT_RESERVE = 0x00003000;
        private const uint PAGE_READWRITE = 4;

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;

        // ── State ─────────────────────────────────────────────────────

        public IntPtr Hwnd { get; private set; }
        private Action<string>? _log;

        private BetterES.Services.BridgeMemoryManager? _mmf;

        private double _latestRpm;
        private double _maxRpm;
        private double _advance;
        private double _torqueLbft;
        private int _currentGear = 0;
        private double _vehicleSpeed;
        private readonly object _rpmLock = new object();

        // Extended telemetry (raw values from MMF)
        private double _manifoldPressure = 1.01325;
        private double _afrRaw;
        private double _intakeFlowRateRaw;
        private double _engineTemperature;
        private double _clutchPosition;
        private int _cylinderCount;
        private double _tps;
        private string _engineName = "";

        public BetterES.Services.BridgeMethodStatus BridgeMethod { get; private set; } = BetterES.Services.BridgeMethodStatus.Uninitialized;

        // Smoothed values
        private double _smoothAfr;
        private double _smoothIntakeFlow;
        private readonly List<double> _afrSamples = new();
        private readonly List<double> _flowSamples = new();
        private const int SmoothWindowSize = 10;

        // Threads
        private Thread? _throttleThread;
        private volatile bool _throttleRunning;
        private volatile bool _throttleKeyHeld;
        private readonly object _throttleLock = new object();

        private Thread? _rpmReaderThread;
        private volatile bool _rpmReaderRunning;

        // ── IEngineBackend ────────────────────────────────────────────

        public bool Initialize(int processId, Action<string> log)
        {
            _log = log;

            log("═══════════════════════════════════════");
            log("Starting MMF Bridge Connection...");
            log("═══════════════════════════════════════");

            log($"Step 1/4: Finding Engine Simulator window (PID: {processId})...");
            Hwnd = FindMainWindow(processId);
            if (Hwnd == IntPtr.Zero)
            {
                log("✗ ERROR: Could not find Engine Simulator window.");
                return false;
            }
            log($"✓ Found Engine Simulator window handle: 0x{Hwnd:X}");

            log($"\nStep 2/4: Injecting DLL into process {processId}...");
            string? dllPath = FindDll("betteres_hook.dll");
            if (dllPath == null || !InjectDll(processId, dllPath))
            {
                log("✗ ERROR: DLL injection failed!");
                return false;
            }
            log("✓ DLL injected successfully");

            log($"\nStep 3/4: Mapping Shared Memory Bridge...");
            _mmf = new BetterES.Services.BridgeMemoryManager();
            if (!_mmf.Initialize())
            {
                log("✗ ERROR: Shared memory initialization failed!");
                return false;
            }
            log("✓ Shared memory bridge established");

            log("\nStep 4/4: Starting background services...");
            _rpmReaderRunning = true;
            _rpmReaderThread = new Thread(RpmReaderLoop) { IsBackground = true, Name = "Telemetry-Reader" };
            _rpmReaderThread.Start();
            
            _throttleRunning = true;
            _throttleThread = new Thread(ThrottleHoldLoop) { IsBackground = true, Name = "Keyboard-Throttle" };
            _throttleThread.Start();

            log("✓ Backend ready (MMF Bi-Directional)");
            return true;
        }

        public void StartEngine(Action<string> log)
        {
            log("Starting engine via MMF sequence...");
            SendIgnitionCommand(true);
            SendStarterCommand(true);
        }

        public void StopEngine()
        {
            SendIgnitionCommand(false);
        }

        public double? ReadRpm() { lock (_rpmLock) { return _latestRpm > 0 ? _latestRpm : null; } }
        public double? ReadMaxRpm() { lock (_rpmLock) { return _maxRpm > 0 ? _maxRpm : null; } }
        public double? ReadAdvance() { lock (_rpmLock) { return _advance; } }
        public double? ReadTorque() { lock (_rpmLock) { return _torqueLbft > 0 ? _torqueLbft : null; } }
        public double ReadSpeed() { lock (_rpmLock) { return _vehicleSpeed; } }
        public int ReadGear() { lock (_rpmLock) { return _currentGear; } }
        public double? ReadBoost() { return null; }
        public double? ReadThrottle() { return CleanTps; }

        public double ManifoldPressure { get { lock (_rpmLock) { return _manifoldPressure; } } }
        public double Afr { get { lock (_rpmLock) { return _smoothAfr > 0 ? _smoothAfr : _afrRaw; } } }
        public double IntakeFlowRate { get { lock (_rpmLock) { return _smoothIntakeFlow > 0 ? _smoothIntakeFlow : _intakeFlowRateRaw; } } }
        public double EngineTemperature { get { lock (_rpmLock) { return _engineTemperature; } } }
        public double ClutchPosition { get { lock (_rpmLock) { return _clutchPosition; } } }
        public int CylinderCount { get { lock (_rpmLock) { return _cylinderCount; } } }
        public double CleanTps { get { lock (_rpmLock) { return _tps; } } }
        public string EngineName { get { lock (_rpmLock) { return _engineName; } } }

        public void SetThrottle(double throttle)
        {
            lock (_throttleLock)
            {
                _throttleKeyHeld = throttle > 0.1;
            }
        }

        public bool SendThrottleCommand(double throttle)
        {
            throttle = Math.Clamp(throttle, 0.0, 1.0);
            if (_mmf != null)
            {
                _mmf.Update(0, throttle, 0);
                return true;
            }
            SetThrottle(throttle);
            return false;
        }

        public bool SendIgnitionCommand(bool enabled)
        {
            if (_mmf != null)
            {
                _mmf.SetCommandBit(0, enabled);
                _mmf.IncrementSeq(0);
                return true;
            }
            KeyPress(VK_I, 120);
            return false;
        }

        public bool SendStarterCommand(bool enabled)
        {
            if (_mmf != null)
            {
                _mmf.SetCommandBit(1, enabled);
                _mmf.IncrementSeq(1);
                return true;
            }
            if (enabled) { KeyDown(VK_S); } else { KeyUp(VK_S); }
            return false;
        }

        public bool SendDynoCommand(bool enabled)
        {
            if (_mmf != null)
            {
                _mmf.SetCommandBit(3, enabled);
                _mmf.IncrementSeq(4); // Trigger Dyno sequence
                return true;
            }
            KeyPress(VK_D, 120);
            return false;
        }

        public bool SendTargetRpm(double rpm)
        {
            if (_mmf != null)
            {
                _mmf.WriteCommand((ref BetterES.Services.BridgeData d) => {
                    d.TargetRpm = rpm;
                    if (rpm > 0) d.CommandBits |= (uint)(1 << 4);
                    else d.CommandBits &= ~(uint)(1 << 4);
                });
                return true;
            }
            return false;
        }

        public void DisableTargetRpm() => SendTargetRpm(0);

        public bool SendTimingCommand(bool timingEnabled, double advanceOffset,
            bool revLimiterEnabled, double revLimitRpm, double cutTimeMs,
            bool ignitionCutEnabled, double cutPercent)
        {
            if (_mmf != null)
            {
                _mmf.WriteCommand((ref BetterES.Services.BridgeData d) => {
                    d.TimingEnabled = (byte)(timingEnabled ? 1 : 0);
                    d.AdvanceOffset = advanceOffset;
                    d.RevLimiterEnabled = (byte)(revLimiterEnabled ? 1 : 0);
                    d.RevLimitRpm = revLimitRpm;
                    d.RevLimiterCutTime = cutTimeMs / 1000.0;
                    d.IgnitionCutEnabled = (byte)(ignitionCutEnabled ? 1 : 0);
                    d.IgnitionCutPercent = cutPercent;
                });
                return true;
            }
            return false;
        }

        public bool SendGearChange(int gear)
        {
            if (_mmf != null)
            {
                _mmf.WriteCommand((ref BetterES.Services.BridgeData d) => {
                    d.TargetGear = gear - 1; // ES (-1=N, 0=1st)
                    d.GearSeq++;
                });
                return true;
            }
            return false;
        }

        public bool SendFuelMixture(double targetAfr)
        {
            if (_mmf != null)
            {
                _mmf.WriteCommand((ref BetterES.Services.BridgeData d) => {
                    d.TargetAfr = targetAfr;
                    // Bit 6 is now controlled by explicit toggle for better reliability
                });
                return true;
            }
            return false;
        }

        public void SendAfrToggle(bool enabled)
        {
            _mmf?.SetCommandBit(6, enabled);
        }

        public bool SendFuelCut(bool enabled)
        {
            if (_mmf != null)
            {
                _mmf.SetCommandBit(2, enabled);
                return true;
            }
            return false;
        }

        public bool SendBridgeMode(bool enabled, int method = 0)
        {
            if (_mmf != null)
            {
                _mmf.WriteCommand((ref BetterES.Services.BridgeData d) => {
                    if (enabled) d.CommandBits |= (uint)(1 << 5);
                    else d.CommandBits &= ~(uint)(1 << 5);
                    d.CommandMethod = (uint)method;
                });
                return true;
            }
            return false;
        }

        public void SetBridgeSmoothing(bool enabled) { }

        public bool SendBridgeData(double rpm, double throttle, double manifoldPressure)
        {
            if (_mmf != null)
            {
                _mmf.Update(rpm, throttle, manifoldPressure);
                return true;
            }
            return false;
        }

        private void RpmReaderLoop()
        {
            while (_rpmReaderRunning)
            {
                if (_mmf == null) { Thread.Sleep(100); continue; }
                try
                {
                    var state = _mmf.ReadState();
                    lock (_rpmLock)
                    {
                        _latestRpm = state.ActualRpm;
                        _maxRpm = state.ActualMaxRpm;
                        _advance = state.ActualAdvance;
                        _manifoldPressure = state.ActualBoost;
                        _afrRaw = state.ActualAfr;
                        _intakeFlowRateRaw = state.ActualIntakeFlow;
                        _engineTemperature = state.ActualTemp;
                        _currentGear = state.ActualGear + 1;
                        _tps = state.Throttle;
                        
                        _torqueLbft = state.ActualTorque;
                        _vehicleSpeed = state.ActualSpeed;
                        _clutchPosition = state.ActualClutch;
                        _cylinderCount = state.ActualCylinders;

                        BridgeMethod = (Services.BridgeMethodStatus)state.BridgeMethod;

                        UpdateSmoothValues(_afrRaw, _intakeFlowRateRaw);
                    }
                }
                catch { }
                Thread.Sleep(10);
            }
        }

        private void UpdateSmoothValues(double afr, double flow)
        {
            _afrSamples.Add(afr);
            if (_afrSamples.Count > SmoothWindowSize) _afrSamples.RemoveAt(0);
            if (_afrSamples.Count > 0) _smoothAfr = _afrSamples.Average();

            _flowSamples.Add(flow);
            if (_flowSamples.Count > SmoothWindowSize) _flowSamples.RemoveAt(0);
            if (_flowSamples.Count > 0) _smoothIntakeFlow = _flowSamples.Average();
        }

        public void Dispose()
        {
            _log?.Invoke("\n── Disposing backend...");
            _throttleRunning = false;
            _throttleThread?.Join(2000);
            KeyUp(VK_R);

            _rpmReaderRunning = false;
            _rpmReaderThread?.Join(2000);

            _mmf?.Dispose();
            _mmf = null;
            _log?.Invoke("✓ Backend disposed successfully");
        }

        // ── Keyboard helpers ──────────────────────────────────────────

        public const int VK_I = 0x49;
        public const int VK_S = 0x53;
        public const int VK_D = 0x44;
        public const int VK_R = 0x52;

        private void KeyDown(int vk) => PostMessage(Hwnd, WM_KEYDOWN, (IntPtr)vk, (IntPtr)0x00000001);
        private void KeyUp(int vk) => PostMessage(Hwnd, WM_KEYUP, (IntPtr)vk, unchecked((IntPtr)0xC0000001));
        private void KeyPress(int vk, int holdMs = 100) { KeyDown(vk); Thread.Sleep(holdMs); KeyUp(vk); }

        private void ThrottleHoldLoop()
        {
            bool wasHeld = false;
            while (_throttleRunning)
            {
                bool shouldHold;
                lock (_throttleLock) { shouldHold = _throttleKeyHeld; }
                if (shouldHold && !wasHeld) { KeyDown(VK_R); wasHeld = true; }
                else if (!shouldHold && wasHeld) { KeyUp(VK_R); wasHeld = false; }
                Thread.Sleep(50);
            }
            if (wasHeld) KeyUp(VK_R);
        }

        // ── Window finding & Injection ────────────────────────────────

        private static IntPtr FindMainWindow(int processId)
        {
            IntPtr result = IntPtr.Zero;
            EnumWindows((hWnd, lParam) => {
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out int pid);
                if (pid == processId) { result = hWnd; return false; }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private static string? FindDll(string name)
        {
            string[] paths = {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", name),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name),
                Path.Combine(Environment.CurrentDirectory, name)
            };
            return paths.FirstOrDefault(File.Exists);
        }

        private bool InjectDll(int pid, string dllPath)
        {
            string full = Path.GetFullPath(dllPath);
            IntPtr hProc = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
            if (hProc == IntPtr.Zero) return false;

            IntPtr loadLib = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryA");
            byte[] pathBytes = Encoding.ASCII.GetBytes(full + '\0');
            IntPtr mem = VirtualAllocEx(hProc, IntPtr.Zero, (uint)pathBytes.Length, MEM_COMMIT_RESERVE, PAGE_READWRITE);
            WriteProcessMemory(hProc, mem, pathBytes, (uint)pathBytes.Length, out _);
            IntPtr thread = CreateRemoteThread(hProc, IntPtr.Zero, 0, loadLib, mem, 0, out _);

            if (thread == IntPtr.Zero) { CloseHandle(hProc); return false; }
            WaitForSingleObject(thread, 5000);
            GetExitCodeThread(thread, out uint exitCode);
            VirtualFreeEx(hProc, mem, 0, 0x8000);
            CloseHandle(thread);
            CloseHandle(hProc);
            return exitCode != 0;
        }
    }
}
