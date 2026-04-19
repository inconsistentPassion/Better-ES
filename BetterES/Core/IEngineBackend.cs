using System;

namespace BetterES.Core
{
    /// <summary>
    /// Interface for Engine Simulator backends.
    /// Provides RPM reading and engine control capabilities.
    /// </summary>
    public interface IEngineBackend : IDisposable
    {
        string Name { get; }

        bool Initialize(int processId, Action<string> log);
        double? ReadRpm();
        double? ReadMaxRpm();
        double? ReadTorque();
        int ReadGear();
        double ReadSpeed();

        // ── Turbo telemetry ───────────────────────────────────────────
        /// <summary>Boost pressure in bar. Null if unavailable.</summary>
        double? ReadBoost();
        /// <summary>Throttle position 0-1. Null if unavailable.</summary>
        double? ReadThrottle();
        double? ReadAdvance() { return null; }

        void SetThrottle(double throttle);
        void StartEngine(Action<string> log);
        void StopEngine();
    }
}
