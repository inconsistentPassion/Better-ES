using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BetterES.Backends.AC;
using BetterES.Backends.BeamNG;
using BetterES.Backends.Keyboard;
using BetterES.Core;

namespace BetterES.Services
{
    public enum ConnectionState { Disconnected, Connecting, Connected, Error }
    public enum GameType { None, AssettoCorsa, BeamNG }
    public enum BridgeMode { Standalone, Bridge, Passthrough }
    public enum BridgeSource { Throttle, Turbo }
    public enum BridgeMethodStatus { Uninitialized = 0, DynoHold = 1, DirectVelocity = 2, Failed = 3 }
    public enum RpmBridgeMethod { DynoHold = 1, DirectVelocity = 2 }

    public class ConnectionService : IDisposable
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        private readonly LogService _log;
        private IEngineBackend? _esBackend;
        public IEngineBackend? EsBackend => _esBackend;
        private IEngineBackend? _gameBackend;
        private Action<string>? _logCallback;
        private BridgeMemoryManager? _bridgeMmf;

        private readonly TurboService _turboService;
        public ConnectionService(LogService log, TurboService turboService) { 
            _log = log; 
            _turboService = turboService;
        }

        public ConnectionState EsState { get; private set; } = ConnectionState.Disconnected;
        public ConnectionState GameState { get; private set; } = ConnectionState.Disconnected;
        public GameType SelectedGame { get; set; } = GameType.None;
        public int BeamNGPort { get; set; } = 4444;

        public ConnectionState State =>
            EsState == ConnectionState.Connected || GameState == ConnectionState.Connected
                ? ConnectionState.Connected
                : EsState == ConnectionState.Connecting || GameState == ConnectionState.Connecting
                    ? ConnectionState.Connecting
                    : ConnectionState.Disconnected;

        // ── Bridge Settings ──────────────────────────────────────────
        private BridgeMode _mode = BridgeMode.Bridge;
        public BridgeMode Mode 
        { 
            get => _mode; 
            set { if (_mode != value) { _mode = value; SyncBridgeSettings(); } } 
        }

        public BridgeSource Source { get; set; } = BridgeSource.Throttle;

        private RpmBridgeMethod _targetRpmBridgeMethod = RpmBridgeMethod.DynoHold;
        public RpmBridgeMethod TargetRpmBridgeMethod 
        { 
            get => _targetRpmBridgeMethod; 
            set { if (_targetRpmBridgeMethod != value) { _targetRpmBridgeMethod = value; SyncBridgeSettings(); } } 
        }
        public double ThrottleGain { get; set; } = 1.0;
        public bool RpmOverrideEnabled { get; set; } = true;
        public bool ThrottleOverrideEnabled { get; set; } = true;
        private bool _customAfrEnabled = false;
        public bool CustomAfrEnabled 
        { 
            get => _customAfrEnabled; 
            set { if (_customAfrEnabled != value) { _customAfrEnabled = value; CustomAfrChanged?.Invoke(value); } } 
        }

        // ── Telemetry ───────────────────────────────────────────────
        private string _statusMessage = "Idle";
        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                if (_statusMessage != value)
                {
                    _statusMessage = value;
                    StatusMessageChanged?.Invoke(value);
                }
            }
        }
        public double? CurrentRpm { get; private set; }
        public double? MaxRpm { get; private set; }
        public double? Torque { get; private set; }
        public int CurrentGear { get; private set; } = 0;
        public double VehicleSpeed { get; private set; }
        public double? CurrentBoost { get; private set; }
        public double? CurrentThrottle { get; private set; }
        public double? CurrentAdvance { get; private set; }
        public double? GameRpm { get; private set; }
        public double? GameMaxRpm { get; private set; }
        public int GameGear { get; private set; } = -1;
        public double GameSpeed { get; private set; }
        public double? GameThrottle { get; private set; }
        public double? GameBoost { get; private set; }
        public double? CurrentManifoldPressure { get; private set; }
        public double? CurrentAfr { get; private set; }
        public double? CurrentIntakeFlow { get; private set; }
        public double? CurrentEngineTemp { get; private set; }
        public double? CurrentClutch { get; private set; }
        public int? CurrentCylinders { get; private set; }
        public BridgeMethodStatus CurrentBridgeMethod { get; private set; } = BridgeMethodStatus.Uninitialized;

        // ── Events ──────────────────────────────────────────────────
        public event Action<string>? StatusMessageChanged;
        public event Action<ConnectionState>? StateChanged;
        public event Action<double?>? RpmChanged;
        public event Action<double?>? MaxRpmChanged;
        public event Action<double?>? TorqueChanged;
        public event Action<int>? GearChanged;
        public event Action<double>? SpeedChanged;
        public event Action<double?>? BoostChanged;
        public event Action<double?>? ThrottleChanged;
        public event Action<double?>? AdvanceChanged;
        public event Action<double?>? ManifoldPressureChanged;
        public event Action<double?>? AfrChanged;
        public event Action<double?>? IntakeFlowChanged;
        public event Action<bool>? CustomAfrChanged;
        public event Action<double?>? EngineTempChanged;
        public event Action<string>? LogMessage;
        public event Action<double?>? GameRpmChanged;
        public event Action<double?>? GameMaxRpmChanged;
        public event Action<int>? GameGearChanged;
        public event Action<double>? GameSpeedChanged;
        public event Action<double?>? GameThrottleChanged;
        public event Action<double?>? GameBoostChanged;
        public event Action<BridgeMethodStatus>? BridgeMethodChanged;
        /// <summary>
        /// Batched UI update event — fired ONCE after all individual state change events.
        /// Subscribe to this instead of individual events for single-dispatch UI updates.
        /// </summary>
        public event Action? StateUpdated;

        private void SetEsState(ConnectionState s) { 
            if (EsState != s) _logCallback?.Invoke($"ES State: {EsState} -> {s}");
            EsState = s; 
            StateChanged?.Invoke(State); 
        }
        private void SetGameState(ConnectionState s) { 
            if (GameState != s) _logCallback?.Invoke($"Game State: {GameState} -> {s}");
            GameState = s; 
            StateChanged?.Invoke(State); 
        }

        public void SyncBridgeSettings()
        {
            if (_esBackend is KeyboardBackend kb && EsState == ConnectionState.Connected)
            {
                // Only activate bridge mode in DLL if the game is ALSO connected
                bool shouldEnableBridge = (Mode == BridgeMode.Bridge) && (GameState == ConnectionState.Connected);
                kb.SendBridgeMode(shouldEnableBridge, (int)TargetRpmBridgeMethod);
            }
        }

        // ── ES Connection ───────────────────────────────────────────

        public Task<bool> ConnectEsAsync(Action<string> logCallback)
        {
            if (EsState == ConnectionState.Connected || EsState == ConnectionState.Connecting)
            {
                logCallback("ES already connected or connecting.");
                return Task.FromResult(false);
            }
            _logCallback = logCallback;
            SetEsState(ConnectionState.Connecting);

            try
            {
                logCallback("Searching for Engine Simulator process...");
                Process[] processes = Array.Empty<Process>();
                foreach (var name in new[] { "EngineSimulator", "engine-sim-app", "EngineSim", "es-app" })
                {
                    processes = Process.GetProcessesByName(name);
                    if (processes.Length > 0) break;
                }
                if (processes.Length == 0)
                    processes = Process.GetProcesses().Where(p =>
                        p.ProcessName.Contains("Engine", StringComparison.OrdinalIgnoreCase) &&
                        p.ProcessName.Contains("Sim", StringComparison.OrdinalIgnoreCase)).ToArray();

                if (processes.Length == 0)
                {
                    logCallback("ERROR: Engine Simulator not found.");
                    SetEsState(ConnectionState.Error);
                    return Task.FromResult(false);
                }

                var proc = processes[0];
                logCallback($"Found Engine Simulator (PID: {proc.Id})");

                // Initialize high-speed memory bridge
                _bridgeMmf = new BridgeMemoryManager();
                if (_bridgeMmf.Initialize())
                {
                    logCallback("[Bridge] Shared memory initialized for engine telemetry.");
                }
                else
                {
                    logCallback("[Bridge] WARNING: Failed to initialize shared memory. Audio may be stuttery.");
                }

                try { proc.PriorityClass = ProcessPriorityClass.High; logCallback("Elevated ES priority."); }
                catch { }

                _esBackend = new KeyboardBackend();
                var ok = _esBackend.Initialize(proc.Id, logCallback);
                if (ok) 
                { 
                    SetEsState(ConnectionState.Connected); 
                    SyncBridgeSettings();
                    StartMonitoringIfNeeded(); 
                }
                else SetEsState(ConnectionState.Error);
                return Task.FromResult(ok);
            }
            catch (Exception ex)
            {
                logCallback($"ES connect error: {ex.Message}");
                SetEsState(ConnectionState.Error);
                _esBackend?.Dispose(); _esBackend = null;
                return Task.FromResult(false);
            }
        }

        public Task DisconnectEsAsync()
        {
            if (EsState != ConnectionState.Connected) return Task.CompletedTask;
            _logCallback?.Invoke("Disconnecting ES...");
            try
            {
                _esBackend?.StopEngine();
                _esBackend?.Dispose();
                _esBackend = null;
                SetEsState(ConnectionState.Disconnected);
            }
            catch (Exception ex) { _logCallback?.Invoke($"ES disconnect error: {ex.Message}"); SetEsState(ConnectionState.Error); }
            return Task.CompletedTask;
        }

        // ── Game Connection ─────────────────────────────────────────

        public Task<bool> ConnectGameAsync(GameType game, Action<string> logCallback, int port = 4444)
        {
            if (GameState == ConnectionState.Connected || GameState == ConnectionState.Connecting)
            {
                logCallback("Game already connected or connecting.");
                return Task.FromResult(false);
            }
            if (game == GameType.None) return Task.FromResult(false);

            _logCallback ??= logCallback;
            SelectedGame = game;
            BeamNGPort = port;
            SetGameState(ConnectionState.Connecting);

            try
            {
                _gameBackend = game switch
                {
                    GameType.AssettoCorsa => new ACBackend(),
                    GameType.BeamNG => new BeamNGBackend(port),
                    _ => throw new ArgumentOutOfRangeException(nameof(game))
                };

                var ok = _gameBackend.Initialize(0, logCallback);

                try
                {
                    var procName = game == GameType.AssettoCorsa ? "acs" : "BetterES.drive.x64";
                    var procs = Process.GetProcessesByName(procName);
                    if (procs.Length > 0)
                    {
                        procs[0].PriorityClass = ProcessPriorityClass.High;
                        logCallback($"Elevated {procName} priority.");
                    }
                }
                catch { }

                if (ok) { SetGameState(ConnectionState.Connected); SyncBridgeSettings(); StartMonitoringIfNeeded(); }
                else { SetGameState(ConnectionState.Error); _gameBackend?.Dispose(); _gameBackend = null; }
                return Task.FromResult(ok);
            }
            catch (Exception ex)
            {
                logCallback($"Game connect error: {ex.Message}");
                SetGameState(ConnectionState.Error);
                _gameBackend?.Dispose(); _gameBackend = null;
                return Task.FromResult(false);
            }
        }

        public Task DisconnectGameAsync()
        {
            if (GameState != ConnectionState.Connected) return Task.CompletedTask;
            _logCallback?.Invoke("Disconnecting game...");
            try
            {
                _gameBackend?.StopEngine();
                _gameBackend?.Dispose();
                _gameBackend = null;
                SetGameState(ConnectionState.Disconnected);
                SyncBridgeSettings();
            }
            catch (Exception ex) { _logCallback?.Invoke($"Game disconnect error: {ex.Message}"); SetGameState(ConnectionState.Error); }
            return Task.CompletedTask;
        }

        // ── Bridge (one-click connect + forward) ────────────────────

        public async Task<bool> ConnectBridgeAsync(GameType game, Action<string> log)
        {
            log("Starting game → ES bridge...");

            if (GameState != ConnectionState.Connected)
            {
                log("[Bridge] Connecting to game...");
                if (!await ConnectGameAsync(game, log)) { log("[Bridge] ✗ Game connection failed."); return false; }
                log("[Bridge] ✓ Game connected");
            }

            if (EsState != ConnectionState.Connected)
            {
                log("[Bridge] Connecting to Engine Simulator...");
                if (!await ConnectEsAsync(log)) { log("[Bridge] ✗ ES connection failed."); return false; }
                log("[Bridge] ✓ ES connected");
            }

            // Start the engine in ES so sounds work
            if (_esBackend is KeyboardBackend kb)
            {
                log("[Bridge] Enabling ignition & dyno...");
                kb.SendIgnitionCommand(true);
                kb.SendStarterCommand(true);
                kb.SendDynoCommand(true);
                Thread.Sleep(200);

                // Enable bridge mode in the hook DLL
                log("[Bridge] Enabling bridge mode in hook DLL...");
                var method = (int)TargetRpmBridgeMethod;
                if (method == 0) method = 1; // Default to DynoHold for better stability
                kb.SendBridgeMode(true, method);
                Thread.Sleep(100);
            }

            log("✓ Bridge ACTIVE — game telemetry → ES engine state");
            return true;
        }

        public async Task DisconnectBridgeAsync()
        {
            if (_esBackend is KeyboardBackend kb)
            {
                // Disable bridge mode before disconnecting
                kb.SendBridgeMode(false);
                kb.SendThrottleCommand(0);
                kb.SendDynoCommand(false);
            }
            await DisconnectEsAsync();
            await DisconnectGameAsync();
        }

        // ── Monitoring ──────────────────────────────────────────────

        private volatile bool _monitoring;
        private bool _monitorStarted;
        private double? _lastSentRpm;
        private double? _lastSentThrottle;
        private double? _lastSentBoost;
        private DateTime _lastSentTime = DateTime.MinValue;

        private void StartMonitoringIfNeeded()
        {
            if (_monitorStarted) return;
            _monitorStarted = true;
            _monitoring = true;

            Task.Run(async () =>
            {
                while (_monitoring)
                {
                    try
                    {
                        // Read game telemetry
                        double? gRpm = null, gMaxRpm = null, gTorque = null, gBoost = null, gThrottle = null;
                        int gGear = -1; double gSpeed = 0;

                        if (_gameBackend != null)
                        {
                            gRpm = _gameBackend.ReadRpm();
                            gMaxRpm = _gameBackend.ReadMaxRpm();
                            gTorque = _gameBackend.ReadTorque();
                            gGear = _gameBackend.ReadGear();
                            gSpeed = _gameBackend.ReadSpeed();
                            gBoost = _gameBackend.ReadBoost();
                            gThrottle = _gameBackend.ReadThrottle();
                        }

                        // Read ES telemetry
                        double? eRpm = null, eMaxRpm = null, eTorque = null, eBoost = null, eThrottle = null;
                        double? eAdvance = null;
                        int eGear = -1; double eSpeed = 0;

                        if (_esBackend != null)
                        {
                            eRpm = _esBackend.ReadRpm();
                            eMaxRpm = _esBackend.ReadMaxRpm();
                            eTorque = _esBackend.ReadTorque();
                            eGear = _esBackend.ReadGear();
                            eSpeed = _esBackend.ReadSpeed();
                            eBoost = _esBackend.ReadBoost();
                            eThrottle = _esBackend.ReadThrottle();
                            eAdvance = _esBackend.ReadAdvance();

                            if (_esBackend is KeyboardBackend kb)
                            {
                                var mp = kb.ManifoldPressure; var afr = kb.Afr; var flow = kb.IntakeFlowRate;
                                var temp = kb.EngineTemperature; var clutch = kb.ClutchPosition; var cyl = kb.CylinderCount;
                                if (mp != CurrentManifoldPressure) { CurrentManifoldPressure = mp; ManifoldPressureChanged?.Invoke(mp); }
                                if (afr != CurrentAfr) { CurrentAfr = afr; AfrChanged?.Invoke(afr); }
                                if (flow != CurrentIntakeFlow) { CurrentIntakeFlow = flow; IntakeFlowChanged?.Invoke(flow); }
                                if (temp != CurrentEngineTemp && temp > 0) { CurrentEngineTemp = temp; EngineTempChanged?.Invoke(temp); }
                                if (clutch != CurrentClutch) { CurrentClutch = clutch; }
                                if (cyl != CurrentCylinders && cyl > 0) { CurrentCylinders = cyl; }
                                if (kb.BridgeMethod != CurrentBridgeMethod) { CurrentBridgeMethod = kb.BridgeMethod; BridgeMethodChanged?.Invoke(kb.BridgeMethod); }
                            }
                        }

                        // ── Update Internal Turbo Physics ────────────────────────
                        if (_turboService.IsRunning)
                        {
                            // Priority: Game values (Bridge mode) > ES values (Standalone mode)
                            double tRpm = gRpm ?? eRpm ?? 0;
                            double tThrot = gThrottle ?? eThrottle ?? 0;
                            _turboService.UpdateTelemetry(tThrot, tRpm);
                        }

                        // ── Bridge: Game telemetry → ES engine state ──────
                        if (_esBackend is KeyboardBackend kbBridge)
                        {
                            if (_gameBackend != null && GameState == ConnectionState.Connected && Mode == BridgeMode.Bridge)
                            {
                                double targetRpm = gRpm ?? 0;
                                double rawInput = (Source == BridgeSource.Turbo) ? (gBoost ?? 0) : (gThrottle ?? 0);
                                double targetThrottle = Math.Clamp(rawInput * ThrottleGain, 0.0, 1.0);
                                double oldRpm = _lastSentRpm ?? 0;

                                double targetBoost = gBoost ?? 0;
                                bool shouldSend = targetRpm != (_lastSentRpm ?? -1) 
                                               || Math.Abs(targetThrottle - (_lastSentThrottle ?? -1)) > 0.005
                                               || Math.Abs(targetBoost - (_lastSentBoost ?? -1)) > 0.05
                                               || (DateTime.Now - _lastSentTime).TotalMilliseconds > 250;

                                if (shouldSend)
                                {
                                    double mp = (gBoost.HasValue && gBoost.Value > 0) ? (1.0 + gBoost.Value) : (0.3 + targetThrottle * 0.7);
                                    double turbMult = (_turboService.IsFunctional && _turboService.IsRunning) ? _turboService.CurrentMultiplier : 1.0;

                                    kbBridge.SendBridgeData(targetRpm, targetThrottle, mp);
                                    _bridgeMmf?.WriteCommand((ref BridgeData d) => {
                                        d.TargetRpm = targetRpm;
                                        d.Throttle = targetThrottle;
                                        d.Manifold = mp;
                                        d.TurboPowerMultiplier = turbMult;
                                    });

                                    _lastSentRpm = targetRpm;
                                    _lastSentThrottle = targetThrottle;
                                    _lastSentBoost = targetBoost;
                                    _lastSentTime = DateTime.Now;
                                }

                                if (targetRpm > 0 && oldRpm <= 0)
                                {
                                    kbBridge.SendIgnitionCommand(true);
                                    kbBridge.SendStarterCommand(true);
                                    kbBridge.SendDynoCommand(true);
                                }
                            }
                            else if (_turboService.IsRunning)
                            {
                                // STANDALONE TURBO - Override manifold pressure only
                                double mp = 1.0 + _turboService.CurrentBoost;
                                double turbMult = _turboService.IsFunctional ? _turboService.CurrentMultiplier : 1.0;

                                // We only update the bridge MMF (hooked Pressure setter), not the base KeyboardBackend commands
                                // which would lock RPM/Throttle.
                                _bridgeMmf?.WriteCommand((ref BridgeData d) => {
                                    d.Manifold = mp;
                                    d.TurboPowerMultiplier = turbMult;
                                });
                            }
                            else
                            {
                                // Clear turbo override if not running
                                _bridgeMmf?.WriteCommand((ref BridgeData d) => {
                                    d.Manifold = 0;
                                    d.TurboPowerMultiplier = 1.0;
                                });
                            }
                        }

                        // ── Update ES telemetry UI ───────────────────────────────
                        var rpm = gRpm ?? eRpm; var maxRpm = eMaxRpm ?? gMaxRpm;
                        var torque = eTorque ?? gTorque; var gear = gGear >= 0 ? gGear : eGear;
                        var speed = gSpeed > 0 ? gSpeed : eSpeed; 
                        var boost = gBoost ?? (_turboService.IsRunning ? _turboService.CurrentBoost : eBoost);
                        var throt = gThrottle ?? eThrottle;

                        if (rpm != null && Math.Abs(rpm.Value - (CurrentRpm ?? -1)) > 5.0) { CurrentRpm = rpm; RpmChanged?.Invoke(rpm); }
                        if (maxRpm != null && Math.Abs(maxRpm.Value - (MaxRpm ?? -1)) > 10.0) { MaxRpm = maxRpm; MaxRpmChanged?.Invoke(maxRpm); }
                        if (torque != null && Math.Abs(torque.Value - (Torque ?? -1)) > 1.0) { Torque = torque; TorqueChanged?.Invoke(torque); }
                        if (gear != CurrentGear) { CurrentGear = gear; GearChanged?.Invoke(gear); }
                        if (Math.Abs(speed - VehicleSpeed) > 0.2) { VehicleSpeed = speed; SpeedChanged?.Invoke(speed); }
                        if (boost != null && Math.Abs(boost.Value - (CurrentBoost ?? -1)) > 0.1) { CurrentBoost = boost; BoostChanged?.Invoke(boost); }
                        if (throt != null && Math.Abs(throt.Value - (CurrentThrottle ?? -1)) > 0.01) { CurrentThrottle = throt; ThrottleChanged?.Invoke(throt); }
                        if (eAdvance != null && Math.Abs(eAdvance.Value - (CurrentAdvance ?? -1)) > 0.1) { CurrentAdvance = eAdvance; AdvanceChanged?.Invoke(eAdvance); }

                        // ── Update game telemetry UI ────────────────────────────
                        if (gRpm != null && Math.Abs(gRpm.Value - (GameRpm ?? -1)) > 2.0) { GameRpm = gRpm; GameRpmChanged?.Invoke(gRpm); }
                        if (gMaxRpm != GameMaxRpm) { GameMaxRpm = gMaxRpm; GameMaxRpmChanged?.Invoke(gMaxRpm); }
                        if (gGear != GameGear) { GameGear = gGear; GameGearChanged?.Invoke(gGear); }
                        if (Math.Abs(gSpeed - GameSpeed) > 0.5) { GameSpeed = gSpeed; GameSpeedChanged?.Invoke(gSpeed); }
                        if (gThrottle != null && Math.Abs(gThrottle.Value - (GameThrottle ?? -1)) > 0.05) { GameThrottle = gThrottle; GameThrottleChanged?.Invoke(gThrottle); }
                        if (gBoost != null && Math.Abs(gBoost.Value - (GameBoost ?? -1)) > 0.1) { GameBoost = gBoost; GameBoostChanged?.Invoke(gBoost); }

                        // Fire batched update event ONCE after all individual events
                        StateUpdated?.Invoke();

                        // High-precision delay to avoid 15ms Windows timer bottleneck
                        if (GameState == ConnectionState.Connected && Mode == BridgeMode.Bridge)
                        {
                            // Aim for 200-500Hz polling for near-zero latency
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            while (sw.ElapsedMilliseconds < 2) { System.Threading.Thread.Sleep(0); }
                        }
                        else
                        {
                            await Task.Delay(5);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logCallback?.Invoke($"Monitor error: {ex.Message}");
                        await Task.Delay(1000);
                    }
                }
            });
        }

        // ── ES → Game shared memory sync ────────────────────────────
        private bool _esToGameSyncEnabled;
        private MemoryMappedFile? _esDataMmf;
        private MemoryMappedViewAccessor? _esDataAccessor;
        private MemoryMappedFile? _acThrottleMmf;
        private MemoryMappedViewAccessor? _acThrottleAccessor;

        public void SetEsToGameSync(bool enabled)
        {
            _esToGameSyncEnabled = enabled;
            if (enabled)
            {
                try
                {
                    _esDataMmf = MemoryMappedFile.CreateOrOpen("BetterES_ES_Data", 116);
                    _esDataAccessor = _esDataMmf.CreateViewAccessor();
                    _acThrottleMmf = MemoryMappedFile.CreateOrOpen("BetterES_AC_Throttle", 8);
                    _acThrottleAccessor = _acThrottleMmf.CreateViewAccessor();
                    _logCallback?.Invoke("ES↔Game sync enabled");
                }
                catch (Exception ex)
                {
                    _logCallback?.Invoke($"ES↔Game sync failed: {ex.Message}");
                    _esToGameSyncEnabled = false;
                }
            }
            else
            {
                _esDataAccessor?.Dispose(); _esDataAccessor = null;
                _esDataMmf?.Dispose(); _esDataMmf = null;
                _acThrottleAccessor?.Dispose(); _acThrottleAccessor = null;
                _acThrottleMmf?.Dispose(); _acThrottleMmf = null;
            }
        }

        // ── Legacy API ──────────────────────────────────────────────
        public Task<bool> ConnectAsync(Action<string> logCallback) => ConnectEsAsync(logCallback);
        public Task DisconnectAsync() => DisconnectEsAsync();

        public void SetThrottle(double throttle)
        {
            if (EsState == ConnectionState.Connected) _esBackend?.SetThrottle(throttle);
        }

        public void Dispose()
        {
            _monitoring = false;
            _bridgeMmf?.Dispose();
            _esDataAccessor?.Dispose(); _esDataMmf?.Dispose();
            _acThrottleAccessor?.Dispose(); _acThrottleMmf?.Dispose();
            if (EsState == ConnectionState.Connected) DisconnectEsAsync().Wait();
            if (GameState == ConnectionState.Connected) DisconnectGameAsync().Wait();
        }
    }
}
