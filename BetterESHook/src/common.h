#pragma once
#ifndef COMMON_H
#define COMMON_H

#include <Windows.h>
#include <atomic>
#include <mutex>

// ── Shared state between hook thread and pipe server thread ──────────

namespace State {
    // Engine Simulator instance pointers (set by hooks)
    extern std::atomic<uintptr_t> appInstance;
    extern std::atomic<uintptr_t> simulatorInstance;
    extern std::atomic<uintptr_t> engineInstance;
    extern std::atomic<uintptr_t> ignitionInstance;
    extern std::atomic<uintptr_t> transmissionInstance;

    // Ignition function instance pointer (for sampleTriangle hook)
    extern std::atomic<uintptr_t> ignitionFunctionInstance;

    // Latest RPM from ignition hook
    extern std::atomic<double> currentRpm;

    // Max RPM (redline) from ignition instance
    extern std::atomic<double> maxRpm;

    // Torque (lb·ft) from updateHpAndTorque hook
    extern std::atomic<double> torqueLbft;

    // Current gear (-1 = Neutral, 0-5 = 1st-6th)
    extern std::atomic<int> currentGear;
    extern std::atomic<double> vehicleSpeed;
    extern std::atomic<uintptr_t> speedInstance;

    // Throttle override (written by pipe thread, applied in simProcess hook)
    // Target RPM from game (for throttle PID controller)
    extern std::atomic<double> targetRpm;
    // Timing control
    extern std::atomic<bool> timingEnabled;
    extern std::atomic<double> advanceOffset;    // degrees
    extern std::atomic<bool> revLimiterEnabled;
    extern std::atomic<double> revLimitRpm;
    extern std::atomic<double> revLimiterCutTime;  // seconds
    extern std::atomic<bool> ignitionCutEnabled;
    extern std::atomic<double> ignitionCutPercent;
    extern std::atomic<double> currentAdvance;     // last computed advance

    extern std::atomic<bool> targetRpmEnabled;

    extern std::atomic<double> targetThrottle;
    extern std::atomic<bool> throttleOverride;

    // ── Engine telemetry (from ES-Studio approach) ────────────────
    extern std::atomic<double> manifoldPressure;   // Intake manifold pressure (bar, ~1.0 = atmospheric)
    extern std::atomic<double> afr;                // Air/fuel ratio
    extern std::atomic<double> intakeFlowRate;     // Total intake airflow (m^3/s internal)
    extern std::atomic<double> engineTemperature;  // Average combustion chamber temp (K)
    extern std::atomic<double> clutchPosition;     // Clutch 0-1
    extern std::atomic<double> cleanTps;           // Raw TPS from app instance
    extern std::atomic<int> cylinderCount;         // Number of cylinders
    extern std::atomic<double> torqueValue;        // Torque from dyno (offset 0xA0)
    extern std::atomic<double> powerValue;         // Power from dyno (offset 0xA8)

    // Fuel control
    extern std::atomic<bool> fuelCutEnabled;       // Fuel cut on/off
    extern std::atomic<bool> useAfrTable;          // Use custom AFR target
    extern std::atomic<double> targetAfr;          // Target AFR (e.g. 14.7 stoich)

    // Controller state (commands from pipe)
    extern std::atomic<int> targetIgnition;   // -1 = Idle, 0 = OFF, 1 = ON
    extern std::atomic<int> targetStarter;    // -1 = Idle, 0 = OFF, 1 = ON
    extern std::atomic<int> targetDyno;       // -1 = Idle, 0 = OFF, 1 = ON
    extern std::atomic<int> targetGear;       // -2 = None

    // ── Bridge Mode ──────────────────────────────────────────
    // When enabled, game telemetry is written into ES engine state
    // every sim frame so audio synthesis uses game values.
    extern std::atomic<bool> bridgeMode;
    extern std::atomic<double> bridgeRpm;        // Game RPM
    extern std::atomic<double> bridgeThrottle;   // Game throttle 0-1
    extern std::atomic<double> bridgeManifold;   // Game manifold pressure (bar, 1.0=atm)
    extern std::atomic<bool> bridgeActive;       // True after first telemetry arrives

    extern std::atomic<int> bridgeMethod;        // 0=Uninitialized, 1=DynoHold, 2=DirectVelocity, 3=Failed
    extern std::atomic<uintptr_t> dynoBaseOffset; // Offset of Dynamometer from Simulator
    extern std::atomic<bool> dynoDiscovered;     // True if discovery was attempted

    // Flags
    extern std::atomic<bool> attached;
    extern std::atomic<bool> running;
    extern std::atomic<double> turboPowerMultiplier;
}

// ── Pipe protocol messages ───────────────────────────────────────────

#pragma pack(push, 1)

constexpr uint8_t MSG_RPM_UPDATE    = 0x01;
constexpr uint8_t MSG_MAX_RPM       = 0x02;
constexpr uint8_t MSG_TORQUE_UPDATE  = 0x03;
constexpr uint8_t MSG_GEAR_UPDATE   = 0x04;
constexpr uint8_t MSG_SPEED_UPDATE  = 0x05;
constexpr uint8_t MSG_CMD_THROTTLE  = 0x10;
constexpr uint8_t MSG_CMD_STARTER   = 0x11;
constexpr uint8_t MSG_CMD_IGNITION  = 0x12;
constexpr uint8_t MSG_CMD_DYNO      = 0x13;
constexpr uint8_t MSG_ADVANCE_UPDATE = 0x06;  // Spark advance (degrees)
constexpr uint8_t MSG_CMD_TARGET_RPM = 0x14;  // Target RPM for throttle controller
constexpr uint8_t MSG_CMD_TIMING    = 0x15;  // Timing control
constexpr uint8_t MSG_CMD_BRIDGE_MODE = 0x40;  // Enable/disable bridge mode
constexpr uint8_t MSG_CMD_BRIDGE_DATA = 0x41;   // Bridge telemetry data (RPM + throttle + manifold)
constexpr uint8_t MSG_CMD_KILL      = 0x1F;

// ── Extended control commands ──────────────────────────────────
constexpr uint8_t MSG_CMD_GEAR_CHANGE  = 0x30;  // Change gear (int32 gear)
constexpr uint8_t MSG_CMD_FUEL_MIXTURE = 0x31;  // Set fuel/air mixture (AFR)
constexpr uint8_t MSG_CMD_FUEL_CUT     = 0x32;  // Cut fuel (uint8_t on/off)

// ── Extended telemetry messages ────────────────────────────────
constexpr uint8_t MSG_MANIFOLD_PRESSURE = 0x20;  // Intake manifold pressure (bar)
constexpr uint8_t MSG_AFR               = 0x21;  // Air/fuel ratio
constexpr uint8_t MSG_INTAKE_FLOW       = 0x22;  // Total intake airflow rate
constexpr uint8_t MSG_ENGINE_TEMP       = 0x23;  // Engine temperature (K)
constexpr uint8_t MSG_CLUTCH            = 0x24;  // Clutch position (0-1)
constexpr uint8_t MSG_CYLINDER_COUNT    = 0x25;  // Cylinder count
constexpr uint8_t MSG_TPS               = 0x26;  // Throttle position (clean)
constexpr uint8_t MSG_ENGINE_LOAD       = 0x27;  // Engine load (%)
constexpr uint8_t MSG_ENGINE_NAME       = 0x28;  // Engine name (string, variable length)
constexpr uint8_t MSG_BRIDGE_STATUS     = 0x29;  // Bridge method status (uint8)

struct MsgRpmUpdate {
    uint8_t type;       // MSG_RPM_UPDATE
    double rpm;
};

struct MsgMaxRpm {
    uint8_t type;       // MSG_MAX_RPM
    double maxRpm;
};

struct MsgTorqueUpdate {
    uint8_t type;       // MSG_TORQUE_UPDATE
    double torqueLbft;
};

struct MsgSpeedUpdate {
    uint8_t type;       // MSG_SPEED_UPDATE
    double speedMps;
};

struct MsgAdvanceUpdate {
    uint8_t type;       // MSG_ADVANCE_UPDATE
    double advance;     // Spark advance in degrees
};

struct MsgGearUpdate {
    uint8_t type;       // MSG_GEAR_UPDATE
    int32_t gear;       // -1 = Neutral, 0-5 = 1st-6th
};

struct MsgCmdThrottle {
    uint8_t type;       // MSG_CMD_THROTTLE
    double throttle;    // 0.0 – 1.0
};

struct MsgCmdBool {
    uint8_t type;       // MSG_CMD_STARTER / IGNITION / DYNO
    uint8_t enabled;    // 0 or 1
};

struct MsgCmdRpm {
    uint8_t type;       // MSG_CMD_RPM
    double rpm;         // Target RPM
};

struct MsgCmdTiming {
    uint8_t type;       // MSG_CMD_TIMING
    uint8_t timingEnabled;
    double advanceOffset;    // degrees
    uint8_t revLimiterEnabled;
    double revLimitRpm;
    double cutTimeMs;
    uint8_t ignitionCutEnabled;
    double cutPercent;
};

struct MsgCmdKill {
    uint8_t type;       // MSG_CMD_KILL
};

// ── Extended telemetry structs ─────────────────────────────────
struct MsgManifoldPressure {
    uint8_t type;       // MSG_MANIFOLD_PRESSURE
    double pressure;    // bar (1.0 = atmospheric)
};

struct MsgAfr {
    uint8_t type;       // MSG_AFR
    double afr;         // air/fuel ratio
};

struct MsgIntakeFlow {
    uint8_t type;       // MSG_INTAKE_FLOW
    double flowRate;    // intake flow rate
};

struct MsgEngineTemp {
    uint8_t type;       // MSG_ENGINE_TEMP
    double temperature; // Kelvin
};

struct MsgClutch {
    uint8_t type;       // MSG_CLUTCH
    double position;    // 0.0 - 1.0
};

struct MsgCylinderCount {
    uint8_t type;       // MSG_CYLINDER_COUNT
    int32_t count;      // number of cylinders
};

struct MsgTps {
    uint8_t type;       // MSG_TPS
    double tps;         // 0.0 - 1.0
};

struct MsgEngineLoad {
    uint8_t type;       // MSG_ENGINE_LOAD
    double load;        // 0.0 - 100.0
};

// Engine name is sent as: [0x28][uint8_t length][char data...]
// Variable length, max 64 bytes total

struct MsgBridgeStatus {
    uint8_t type;       // MSG_BRIDGE_STATUS
    uint8_t method;     // 0=Uninitialized, 1=DynoHold, 2=DirectVelocity, 3=Failed
};

// ── Extended control structs ───────────────────────────────────
struct MsgCmdGearChange {
    uint8_t type;       // MSG_CMD_GEAR_CHANGE
    int32_t gear;       // target gear (-1=N, 0+=gear)
};

struct MsgCmdFuelMixture {
    uint8_t type;       // MSG_CMD_FUEL_MIXTURE
    double targetAfr;   // target AFR (e.g. 14.7 stoich)
};

struct MsgCmdFuelCut {
    uint8_t type;       // MSG_CMD_FUEL_CUT
    uint8_t enabled;    // 0 or 1
};

struct MsgCmdBridgeMode {
    uint8_t type;       // MSG_CMD_BRIDGE_MODE
    uint8_t enabled;    // 0 or 1
    uint8_t method;     // 0=Auto, 1=DynoHold, 2=DirectVelocity
};

struct MsgCmdBridgeData {
    uint8_t type;       // MSG_CMD_BRIDGE_DATA
    double rpm;         // Game RPM
    double throttle;    // Game throttle 0-1
    double manifold;    // Game manifold pressure (bar)
};

struct SharedBridgeData {
    // ── Header & Command Downlink (App -> DLL) ──
    // Grouped by size: doubles first, then uint32, uint16, uint8 — for cache locality

    // uint32 fields
    uint32_t packetId;      // Incrementing ID for sync verification
    uint32_t commandBits;   // Bitflags: 0=Ignition, 1=Starter, 2=FuelCut, 3=Dyno, 4=RpmOverride, 5=BridgeMode
    uint32_t commandMethod; // Bridge Method (1=DynoHold, 2=Direct)

    // double fields (App -> DLL)
    double targetRpm;       // Target RPM (Bridge Mode)
    double throttle;        // Target Throttle
    double manifold;        // Target Manifold Pressure (bar)
    double targetAfr;       // Target AFR for fuel mixture
    double turboPowerMultiplier; // Functional Turbo boost modifier

    // Timing control (App -> DLL)
    double advanceOffset;
    double revLimitRpm;
    double revLimiterCutTime;
    double ignitionCutPercent;

    // int32 field
    int32_t  targetGear;    // -1=N, 0+=Gears

    // uint16 fields (one-shot sequencers)
    uint16_t ignitionSeq;
    uint16_t starterSeq;
    uint16_t gearSeq;
    uint16_t resetSeq;
    uint16_t dynoSeq;

    // uint8 fields (flags)
    uint8_t  timingEnabled;
    uint8_t  revLimiterEnabled;
    uint8_t  ignitionCutEnabled;
    uint8_t  _pad1;         // Explicit padding to align next section

    // ── Telemetry Uplink (DLL -> App) ──

    // double fields (DLL -> App)
    double   actualRpm;
    double   actualBoost;
    double   actualAfr;
    double   actualIntakeFlow;
    double   actualTemp;
    double   actualMaxRpm;
    double   actualTorque;
    double   actualSpeed;
    double   actualClutch;
    double   actualAdvance;

    // uint32 fields (DLL -> App)
    uint32_t statusBits;    // bit0=Running, bit1=Stalled, bit2=RevLimiting
    uint32_t bridgeMethod;  // Resulting method inside DLL

    // int32 fields (DLL -> App)
    int32_t  actualGear;
    int32_t  actualCylinders;
};

#pragma pack(pop)

namespace Memory {
    bool OpenBridgeMemory();
    bool ReadBridgeMemory(SharedBridgeData& data);
    void CloseBridgeMemory();
}

constexpr const char* PIPE_NAME = "\\\\.\\pipe\\better-es-pipe";
constexpr int PIPE_BUFFER_SIZE = 4096;

#endif // COMMON_H
