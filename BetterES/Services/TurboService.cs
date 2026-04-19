using System;
using System.Diagnostics;
using System.Threading;

namespace BetterES.Services
{
    /// <summary>
    /// Assetto Corsa turbo simulation — matches AC's engine.ini physics.
    ///
    /// AC turbo model (from engine.ini):
    ///   [TURBO_0]
    ///   MAX_BOOST=1.344       # Max boost (bar)
    ///   WASTEGATE=1.3         # Hard clamp (bar)
    ///   LAG_UP=0.996          # EMA filter for spool-up (closer to 1 = slower)
    ///   LAG_DN=0.998          # EMA filter for spool-down (closer to 1 = slower)
    ///   REFERENCE_RPM=3500    # RPM for full boost at full throttle
    ///   GAMMA=2               # Boost curve shape
    ///
    /// Physics per tick:
    ///   1. target = pow(clamp(rpm / REFERENCE_RPM, 0, 1), GAMMA)
    ///   2. spool  = spool * LAG + target * (1 - LAG)   // EMA filter
    ///   3. boost  = spool * MAX_BOOST * throttle        // throttle gates output
    ///   4. boost  = clamp(boost, 0, WASTEGATE)
    ///
    /// Key behaviors:
    ///   - Closing throttle does NOT kill spool instantly — boost decays
    ///     through turbo inertia (spool tracks RPM, not throttle)
    ///   - Below minimum RPM (~30% of REFERENCE_RPM), no boost is produced
    ///   - Wastegate prevents boost from exceeding threshold
    ///   - Power multiplier = 1 + boost * efficiency
    /// </summary>
    public class TurboService : IDisposable
    {
        private readonly LogService _log;
        private Thread? _simThread;
        private volatile bool _running;
        private const int SimTickMs = 20; // 50 Hz physics

        // ── Telemetry ─────────────────────────────────────────────
        private double _throttle;
        private double _rpm;
        private double _baseTorque;
        private readonly object _stateLock = new();

        // ── AC turbo.ini parameters ───────────────────────────────
        public double MaxBoost { get; set; } = 1.344;
        public double Wastegate { get; set; } = 1.3;
        public double ReferenceRpm { get; set; } = 3500.0;
        public double Gamma { get; set; } = 2.0;
        public double LagUp { get; set; } = 0.990;
        public double LagDown { get; set; } = 0.990;

        // ── State ─────────────────────────────────────────────────
        private double _spool;
        private double _boostBar;
        private double _boostSmooth;

        // ── Notification throttle ──────────────────────────────
        private const int NotifyIntervalMs = 50;
        private long _lastNotifyTick;
        private double _lastNotifiedBoost = double.MinValue;
        public bool IsFunctional { get; set; } = false;

        public double TurboEfficiency => 1.0;

        public event Action<double>? BoostChanged;

        public bool IsRunning => _running;
        public double CurrentBoost => _boostSmooth;
        public double CurrentMultiplier => 1.0 + CurrentBoost;
        public double CurrentSpool => _spool;

        public TurboService(LogService log) { _log = log; }

        public void Start(Action<string>? log = null)
        {
            if (_running) return;
            _spool = 0;
            _boostBar = 0;
            _boostSmooth = 0;
            _lastNotifiedBoost = double.MinValue;
            _lastNotifyTick = 0;

            _running = true;
            _simThread = new Thread(SimLoop) { IsBackground = true, Name = "TurboSim" };
            _simThread.Start();

            log?.Invoke("✓ Turbo simulation started (AC model v2)");
            _log?.Info("Turbo", "Turbo simulation started (AC model v2)");
        }

        public void Stop()
        {
            _running = false;
            _simThread?.Join(2000);
            _simThread = null;
        }

        public void UpdateTelemetry(double throttle, double rpm, double baseTorque = 0)
        {
            lock (_stateLock)
            {
                _throttle = Math.Clamp(throttle, 0, 1);
                _rpm = Math.Max(0, rpm);
                _baseTorque = Math.Max(0, baseTorque);
            }
        }

        private void StepPhysics(double throttle, double rpm, double maxB, double wg, double refRpm, double g, double lagUpSeconds, double lagDnSeconds)
        {
            double dt = 0.01; // SimLoop ticks at 10ms

            // RULE 1: Target boost must depend on throttle, airflow (RPM load proxy), and engine size
            // We use a small deadzone to ensure off-throttle is strictly 0.0
            double tNorm = Math.Max(0, throttle - 0.05);
            double engineLoad = Math.Pow(Math.Clamp(rpm / refRpm, 0.0, 1.0), g);
            double targetBoost = tNorm * engineLoad * maxB;

            // RULE 2 & 4: Lag must be time-based, and off-throttle must despool toward 0
            double currentLag = targetBoost > _spool ? lagUpSeconds : lagDnSeconds;
            if (currentLag < 0.05) currentLag = 0.05; // Prevent divide-by-zero
            
            // Integrate inertia: spool chases targetBoost over time
            _spool += (targetBoost - _spool) * (dt / currentLag);

            // RULE 3: Wastegate must bleed boost down, not clamp it
            if (_spool > wg)
            {
                // Bleed over-pressure down mechanically
                _spool += (wg - _spool) * (dt / 0.1); 
            }

            _boostBar = Math.Max(0, _spool);
        }

        private void SimLoop()
        {
            while (_running)
            {
                double throttle, rpm;
                double maxB, wg, refRpm, g, lagUp, lagDn;
                lock (_stateLock)
                {
                    throttle = _throttle;
                    rpm = _rpm;
                    maxB = MaxBoost > 0 ? MaxBoost : 1.344;
                    wg = Wastegate > 0 ? Math.Min(Wastegate, maxB) : maxB;
                    refRpm = ReferenceRpm > 0 ? ReferenceRpm : 3500;
                    g = Gamma > 0 ? Gamma : 2.0;
                    
                    // Convert original arbitrary multipliers (0.9 to 0.999) to realistic seconds for time-based lag
                    lagUp = Math.Max(0.1, (Math.Clamp(LagUp, 0.9, 0.999) - 0.9) * 20.0);
                    lagDn = Math.Max(0.1, (Math.Clamp(LagDown, 0.9, 0.999) - 0.9) * 20.0);
                }

                StepPhysics(throttle, rpm, maxB, wg, refRpm, g, lagUp, lagDn);

                // Smooth output
                _boostSmooth += (_boostBar - _boostSmooth) * 0.2;

                // Throttled event: only fire when value changed meaningfully
                // and at most every 50ms (avoids flooding UI thread)
                long now = Stopwatch.GetTimestamp();
                long elapsed = (now - _lastNotifyTick) * 1000 / Stopwatch.Frequency;
                double delta = Math.Abs(_boostSmooth - _lastNotifiedBoost);

                if (elapsed >= NotifyIntervalMs && (delta > 0.001 || (_boostSmooth == 0 && _lastNotifiedBoost != 0)))
                {
                    _lastNotifiedBoost = _boostSmooth;
                    _lastNotifyTick = now;
                    BoostChanged?.Invoke(_boostSmooth);
                }

                Thread.Sleep(SimTickMs);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
