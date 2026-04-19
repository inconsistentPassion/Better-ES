#include "log.h"
#include "hooks.h"
#include "common.h"
#include "memory.h"
#include <MinHook.h>
#include <cmath>
#include <cctype>

// ── Function pointer types ───────────────────────────────────────────

typedef __int64(__fastcall* IgnitionModuleFn)(__int64 a1, double a2);
typedef __int64(__fastcall* GasSystemResetFn)(__int64 instance, double P, double T, __int64 mix);
typedef HWND(WINAPI* GetForegroundWindowFn)();

static IgnitionModuleFn oIgnitionModule = nullptr;
static SimProcessFn oSimProcess = nullptr;
static UpdateHpAndTorqueFn oUpdateHpAndTorque = nullptr;
static RTachRenderFn oRTachRender = nullptr;
static SampleTriangleFn oSampleTriangle = nullptr;
static GetManifoldPressureFn oGetManifoldPressure = nullptr;
static AfrClusterRenderFn oAfrClusterRender = nullptr;
static SetThrottlePistonFn oSetThrottlePiston = nullptr;
static SetThrottleRotaryFn oSetThrottleRotary = nullptr;
static ChangeGearFn oChangeGear = nullptr;
static GasSystemResetFn oGasSystemReset = nullptr;
static GetForegroundWindowFn oGetForegroundWindow = nullptr;

static HWND hSimWindow = NULL;

// Focus Bypass: Tricks the simulator into thinking it's always focused
// to maintain full physics speed (10,000Hz) even in the background.
HWND WINAPI GetForegroundWindow_Hk() {
    if (hSimWindow) return hSimWindow;
    return oGetForegroundWindow();
}

// Degrees <-> radians conversion factor used by ES
static constexpr double kDeg = 0.017453292519943295; // pi/180

// Whether current engine is rotary (detected from throttle function pattern)
static bool isRotary = false;

static bool DetectRotaryEngine(uintptr_t engInst) {
    if (!engInst) return false;
    
    char nameBuf[32] = {0};
    bool localIsRotary = false;
    
    __try {
        uintptr_t nameAddress = engInst + 0x50;
        char val = *reinterpret_cast<char*>(nameAddress);
        char* namePtr = nullptr;
        if (isalnum((unsigned char)val)) namePtr = reinterpret_cast<char*>(nameAddress);
        else namePtr = *reinterpret_cast<char**>(nameAddress);
        
        if (namePtr) {
            for (int i=0; i<31; i++) {
                nameBuf[i] = std::tolower((unsigned char)namePtr[i]);
                if (namePtr[i] == '\0') break;
            }
            nameBuf[31] = '\0';
            if (strstr(nameBuf, "wankel") || strstr(nameBuf, "rotary")) {
                localIsRotary = true;
            }
        }
    } __except(EXCEPTION_EXECUTE_HANDLER) {}
    
    return localIsRotary;
}

static constexpr double toRpm(double rad_s) {
    return rad_s / 0.104719755;
}

// Rev Limiter State (forward-declared before use in ignitionModuleHk)
static double revLimiterTimer = 0.0;
static int ignitionCutCounter = 0;

static bool bridgeInitialized = false;
static double currentSmoothedRpm = 1000.0;

// Forward declaration
static void ReadClutchPosition();
static void ReadIntakeFlow();
static void ReadEngineTemperature();
static void ReadCylinderCount();

// Sequence tracking for one-shot commands
static uint16_t lastIgnitionSeq = 0;
static uint16_t lastStarterSeq = 0;
static uint16_t lastGearSeq = 0;
static uint16_t lastResetSeq = 0;
static uint16_t lastDynoSeq = 0;

// ── Per-tick bridge cache: read shared memory once, use everywhere ──
struct BridgeCache {
    SharedBridgeData raw;
    bool valid;

    // Parsed downlink fields (read once per tick)
    bool bridgeMode;
    bool bridgeActive;
    bool rpmOverride;
    bool fuelCut;
    bool useAfr;
    double targetRpm;
    double throttle;
    double targetAfr;
    double manifold;
    double turboMult;
    int method;
    // Timing
    bool timingOn;
    double advOffset;
    bool revLimOn;
    double revLimRpm;
    double revLimCut;
    bool ignCutOn;
    double ignCutPct;
};
static BridgeCache g_bridgeCache = {};

static void HandleSharedMemoryControls() {
    // Read shared memory exactly ONCE per tick
    if (!Memory::ReadBridgeMemory(g_bridgeCache.raw)) {
        g_bridgeCache.valid = false;
        return;
    }
    g_bridgeCache.valid = true;
    auto& d = g_bridgeCache.raw;

    // Parse downlink once
    g_bridgeCache.bridgeMode = (d.commandBits & (1 << 5)) != 0;
    g_bridgeCache.bridgeActive = true;
    g_bridgeCache.rpmOverride = (d.commandBits & (1 << 4)) != 0;
    g_bridgeCache.fuelCut = (d.commandBits & (1 << 2)) != 0;
    g_bridgeCache.useAfr = (d.commandBits & (1 << 6)) != 0;
    g_bridgeCache.targetRpm = d.targetRpm;
    g_bridgeCache.throttle = d.throttle;
    g_bridgeCache.targetAfr = d.targetAfr;
    g_bridgeCache.manifold = d.manifold;
    g_bridgeCache.turboMult = d.turboPowerMultiplier;
    g_bridgeCache.method = d.commandMethod;
    g_bridgeCache.timingOn = d.timingEnabled != 0;
    g_bridgeCache.advOffset = d.advanceOffset;
    g_bridgeCache.revLimOn = d.revLimiterEnabled != 0;
    g_bridgeCache.revLimRpm = d.revLimitRpm;
    g_bridgeCache.revLimCut = d.revLimiterCutTime;
    g_bridgeCache.ignCutOn = d.ignitionCutEnabled != 0;
    g_bridgeCache.ignCutPct = d.ignitionCutPercent;

    // Sync to atomics (for hooks that read them directly)
    State::bridgeMode.store(g_bridgeCache.bridgeMode);
    State::bridgeMethod.store(g_bridgeCache.method);
    State::bridgeRpm.store(g_bridgeCache.targetRpm);
    State::bridgeThrottle.store(g_bridgeCache.throttle);
    State::bridgeManifold.store(g_bridgeCache.manifold);
    State::bridgeActive.store(true);

    State::targetRpmEnabled.store(g_bridgeCache.rpmOverride);
    State::targetRpm.store(g_bridgeCache.targetRpm);
    State::targetAfr.store(g_bridgeCache.targetAfr);
    State::fuelCutEnabled.store(g_bridgeCache.fuelCut);
    State::useAfrTable.store(g_bridgeCache.useAfr);
    State::turboPowerMultiplier.store(g_bridgeCache.turboMult);

    State::timingEnabled.store(g_bridgeCache.timingOn);
    State::advanceOffset.store(g_bridgeCache.advOffset);
    State::revLimiterEnabled.store(g_bridgeCache.revLimOn);
    State::revLimitRpm.store(g_bridgeCache.revLimRpm);
    State::revLimiterCutTime.store(g_bridgeCache.revLimCut);
    State::ignitionCutEnabled.store(g_bridgeCache.ignCutOn);
    State::ignitionCutPercent.store(g_bridgeCache.ignCutPct);

    // Process One-Shots via Sequences
    if (d.ignitionSeq != lastIgnitionSeq) {
        State::targetIgnition.store((d.commandBits & (1 << 0)) != 0 ? 1 : 0);
        lastIgnitionSeq = d.ignitionSeq;
    }
    if (d.starterSeq != lastStarterSeq) {
        State::targetStarter.store((d.commandBits & (1 << 1)) != 0 ? 1 : 0);
        lastStarterSeq = d.starterSeq;
    }
    if (d.gearSeq != lastGearSeq) {
        State::targetGear.store(d.targetGear);
        lastGearSeq = d.gearSeq;
    }
    if (d.resetSeq != lastResetSeq) {
        State::dynoDiscovered.store(false);
        lastResetSeq = d.resetSeq;
    }
    if (d.dynoSeq != lastDynoSeq) {
        State::targetDyno.store((d.commandBits & (1 << 3)) != 0 ? 1 : 0);
        lastDynoSeq = d.dynoSeq;
    }

    // Uplink: write telemetry back to shared memory
    d.actualRpm = State::currentRpm.load();
    d.actualBoost = State::manifoldPressure.load();
    d.actualAfr = State::afr.load();
    d.actualIntakeFlow = State::intakeFlowRate.load();
    d.actualTemp = State::engineTemperature.load();
    d.actualMaxRpm = State::maxRpm.load();
    d.actualGear = State::currentGear.load();
    d.actualTorque = State::torqueLbft.load();
    d.actualSpeed = State::vehicleSpeed.load();
    d.actualClutch = State::clutchPosition.load();
    d.actualCylinders = State::cylinderCount.load();
    d.actualAdvance = State::currentAdvance.load();
    d.bridgeMethod = (uint32_t)State::bridgeMethod.load();

    uint32_t status = 0;
    if (State::running.load()) status |= (1 << 0);
    d.statusBits = status;

    Memory::WriteBridgeMemory(d);
}

// ── Hooks ────────────────────────────────────────────────────────────

__int64 __fastcall ignitionModuleHk(__int64 a1, double a2) {
    if (State::attached.load()) {
        State::ignitionInstance.store(a1);

        // Capture the ignition advance function instance (offset 0x58 — same as ES-Studio)
        uintptr_t ignFnInst = 0;
        if (Memory::SafeReadUintptr(a1, 0x58, ignFnInst) && ignFnInst) {
            State::ignitionFunctionInstance.store(ignFnInst);
        }

        uintptr_t crankshaftPtr = 0;
        if (Memory::SafeReadUintptr(a1, 0x60, crankshaftPtr) && crankshaftPtr) {
            double velocity = 0;
            if (Memory::SafeReadDouble(crankshaftPtr, 0x30, velocity)) {
                double rpm = toRpm(std::fabs(velocity));
                State::currentRpm.store(rpm);

                // ── Bridge Mode Ignition Force ───────────────────────
                // Read bridge state once (cached from simProcessInternal)
                if (g_bridgeCache.valid && g_bridgeCache.bridgeMode) {
                    // Force ignition ON every sub-tick to prevent stalls
                    Memory::SafeWriteBool(a1, 0x50, true);
                    // Skip rev limiter and timing cuts while in bridge mode
                    return oIgnitionModule(a1, a2);
                }

                // ── Rev limiter ───────────────────────────────────────
                if (State::timingEnabled.load() && State::revLimiterEnabled.load()) {
                    double limitRpm = State::revLimitRpm.load();
                    if (rpm > limitRpm) {
                        revLimiterTimer = State::revLimiterCutTime.load();
                    }
                    if (revLimiterTimer > 0.0) {
                        revLimiterTimer -= a2;
                        if (revLimiterTimer < 0.0) revLimiterTimer = 0.0;
                        // Kill ignition for this frame
                        bool ignEnabled = false;
                        Memory::SafeReadBool(a1, 0x50, ignEnabled);
                        if (ignEnabled) {
                            Memory::SafeWriteBool(a1, 0x50, false);
                        }
                        return oIgnitionModule(a1, a2);
                    }
                }

                // ── Ignition cut (partial cut) ────────────────────────
                if (State::timingEnabled.load() && State::ignitionCutEnabled.load()) {
                    double cutPct = State::ignitionCutPercent.load();
                    if (cutPct >= 100.0) {
                        // Hard cut: kill ignition entirely this frame
                        bool ignEnabled = false;
                        Memory::SafeReadBool(a1, 0x50, ignEnabled);
                        if (ignEnabled) {
                            Memory::SafeWriteBool(a1, 0x50, false);
                        }
                        return oIgnitionModule(a1, a2);
                    } else if (cutPct > 0.0) {
                        // Soft cut: skip every N-th firing based on cut percentage
                        ignitionCutCounter++;
                        // e.g. 50% cut => skip every other frame
                        int cutDivisor = (int)std::round(100.0 / cutPct);
                        if (cutDivisor < 1) cutDivisor = 1;
                        if (ignitionCutCounter % cutDivisor == 0) {
                            bool ignEnabled = false;
                            Memory::SafeReadBool(a1, 0x50, ignEnabled);
                            if (ignEnabled) {
                                Memory::SafeWriteBool(a1, 0x50, false);
                            }
                            return oIgnitionModule(a1, a2);
                        }
                    }
                }
            }
        }

        // Read max RPM (redline) from ignition instance +0x88
        double maxRpmRad = 0;
        if (Memory::SafeReadDouble(a1, 0x88, maxRpmRad) && maxRpmRad > 0) {
            State::maxRpm.store(toRpm(maxRpmRad));
        }

        // Read speed from speed instance (set by rTachRenderHk)
        uintptr_t spdInst = State::speedInstance.load();
        if (spdInst) {
            double v1 = 0, v2 = 0;
            if (Memory::SafeReadDouble(spdInst, 0x298, v1) &&
                Memory::SafeReadDouble(spdInst, 0x30, v2)) {
                State::vehicleSpeed.store(std::abs(v1 * v2));
            }
        }
    }
    return oIgnitionModule(a1, a2);
}


// ── RPM Throttle Controller ──────────────────────────────────────────
// Runs every sim frame (~2000Hz). Adjusts throttle to match target RPM.
// Inspired by ES-Studio's idle helper approach.

static int rpmDbgCounter = 0;

static void DiscoverDynoOffsets(uintptr_t simInst) {
    if (State::dynoDiscovered.load()) return;
    
    if (!simInst) {
        // Sim not ready yet - return without setting discovered flag so we retry next frame
        return;
    }

    uintptr_t dynoBaseOffset = 0xE1 - 0xD9; // 0x08
    uintptr_t dynoBase = simInst + dynoBaseOffset;
    
    double ks = 0, kd = 0, maxTorque = 0;
    bool ok = true;
    ok &= Memory::SafeReadDouble(dynoBase, 0xC0, ks);
    ok &= Memory::SafeReadDouble(dynoBase, 0xC8, kd);
    ok &= Memory::SafeReadDouble(dynoBase, 0xD0, maxTorque);
    
    // As long as we can read the memory reasonably safely, trust the offset.
    // The previous strict checks occasionally caused false-negatives.
    if (ok) {
        State::dynoBaseOffset.store(dynoBaseOffset);
        Log("[DYNO BRIDGE] Discovery OK — using dyno hold (base offset 0x%llX). ks=%.1f kd=%.1f mt=%.0f", 
            (unsigned long long)dynoBaseOffset, ks, kd, maxTorque);
    } else {
        if (State::bridgeMethod.load() == 1) {
            State::bridgeMethod.store(3); // Dyno Hold -> Failed
            Log("[DYNO BRIDGE] ERROR: Dyno Hold is unavailable. Memory read failed.");
        }
        Log("[DYNO BRIDGE] Discovery FAILED — using direct velocity fallback");
    }
    State::dynoDiscovered.store(true);
}

static void DynoHoldOverride(double targetRpm, uintptr_t simInst) {
    uintptr_t baseOffset = State::dynoBaseOffset.load();
    if (!baseOffset || !simInst) return;
    
    uintptr_t dynoBase = simInst + baseOffset;
    double rad_s = targetRpm * 0.104719755;
    
    // Write constants ONCE via sentinel, only update speed every tick
    static bool constantsWritten = false;
    if (!constantsWritten) {
        Memory::SafeWriteBool(simInst, 0xE1, true);           // Enable Dyno natively in simulator
        Memory::SafeWriteDouble(dynoBase, 0xC0, 1000.0);      // m_ks (Stiffness: high to lock securely to target)
        Memory::SafeWriteDouble(dynoBase, 0xC8, 5.0);         // m_kd (Damping: low to prevent lag/molasses effect)
        Memory::SafeWriteBool(dynoBase, 0xD8, true);          // m_hold
        Memory::SafeWriteBool(dynoBase, 0xD9, true);          // m_enabled
        constantsWritten = true;
        Log("[DYNO BRIDGE] Constants written once — only speed updates now");
    }
    Memory::SafeWriteDouble(dynoBase, 0xB8, rad_s);       // m_rotationSpeed (only changing value)
}

static void RpmDirectOverride(double targetRpm) {
    if (targetRpm <= 0) return;
    uintptr_t ignInst = State::ignitionInstance.load();
    if (!ignInst) return;
    uintptr_t crankshaftPtr = 0;
    if (!Memory::SafeReadUintptr(ignInst, 0x60, crankshaftPtr) || !crankshaftPtr) return;
    
    double rad_s = targetRpm * 0.104719755;
    Memory::SafeWriteDouble(crankshaftPtr, 0x30, rad_s);
}

static __int64 SafeSetThrottle(uintptr_t engInst, double throt, bool isRot) {
    if (!engInst) return 0;
    __int64 res = 0;
    if (isRot) {
        __try { res = oSetThrottleRotary(engInst, throt); }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
    } else {
        __try { res = oSetThrottlePiston(engInst, throt); }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    return res;
}

static void simProcessInternal(__int64 a1, float a2) {
    static uintptr_t lastEngInst = 0;
    static int tickCounter = 0;
    tickCounter++;

    // 2. Refresh pointers from this specialized App instance
    uintptr_t simInst = 0, engInst = 0;
    if (!Memory::SafeReadUintptr(a1, 0x1618, simInst)) simInst = 0;
    if (!Memory::SafeReadUintptr(a1, 0x1600, engInst)) engInst = 0;

    if (a1) State::appInstance.store(a1);
    if (simInst) State::simulatorInstance.store(simInst);
    if (engInst) {
        State::engineInstance.store(engInst);
        
        // Cache Engine Name detection
        if (engInst != lastEngInst) {
            isRotary = DetectRotaryEngine(engInst);
            lastEngInst = engInst;
            Log("[HOOK] New engine instance: 0x%llX (Type: %s)", 
                (unsigned long long)engInst, isRotary ? "Rotary" : "Piston");
        }
    }

    // ── Tick rate management ──────────────────────────────────────
    // Bridge processing at 10kHz — audio synthesis needs smooth state updates.
    // Extended telemetry throttled to 100Hz (temperature/intake don't change slowly).
    bool telemetryTick = (tickCounter % 100 == 0);

    // Read shared memory bridge every tick — HandleSharedMemoryControls also applies commands
    // and syncs state, not just reads data. Throttling here causes RPM stepping.
    HandleSharedMemoryControls();

    // Read intake flow every tick for 20Hz gauge updates
    ReadIntakeFlow();

    // Read clutch position every tick (changes quickly)
    ReadClutchPosition();

    // Read cylinder count once on first tick
    static bool cylindersRead = false;
    if (!cylindersRead) {
        ReadCylinderCount();
        cylindersRead = true;
    }

    // Read current gear via transmission instance
    if (simInst) {
        uintptr_t transInst = 0;
        if (Memory::SafeReadUintptr(simInst, 0x520, transInst) && transInst) {
            State::transmissionInstance.store(transInst);
            int32_t gear = -1;
            if (Memory::SafeReadInt32(transInst, 0x348, gear)) {
                State::currentGear.store(gear);
            }
        }
    }

    // 100Hz: Extended telemetry (temperature/intake don't change at 10kHz)
    if (telemetryTick) {
        ReadEngineTemperature();
    }

    // ── Apply Commands (every tick for responsiveness) ────────────
    
    // 0. Ignition state (one-shot, cheap — atomic compare)
    int tIgn = State::targetIgnition.load();
    if (tIgn != -1) {
        uintptr_t ignInst = State::ignitionInstance.load();
        if (ignInst) {
            Memory::SafeWriteBool(ignInst, 0x50, tIgn == 1);
            State::targetIgnition.store(-1);
        }
    }

    // 1. Starter & Dyno state (one-shot, cheap)
    if (simInst) {
        int tDyno = State::targetDyno.load();
        if (tDyno != -1) {
            Memory::SafeWriteBool(simInst, 0xE1, tDyno == 1);
            State::targetDyno.store(-1);
        }

        int tStart = State::targetStarter.load();
        if (tStart != -1) {
            Memory::SafeWriteBool(simInst, 0x1C0, tStart == 1);
            State::targetStarter.store(-1);
        }
    }

    // 2. Throttle & RPM Override / Bridge Mode — use cached bridge data
    if (engInst) {
        double tThrot;
        bool doOverride;
        doOverride = State::throttleOverride.load();
        tThrot = State::targetThrottle.load();

        if (g_bridgeCache.valid && g_bridgeCache.bridgeMode) {
            double bRpm = g_bridgeCache.targetRpm;
            double bThrot = g_bridgeCache.throttle;

            if (!bridgeInitialized) {
                currentSmoothedRpm = State::currentRpm.load();
                bridgeInitialized = true;
                State::dynoDiscovered.store(false);
            }
            
            DiscoverDynoOffsets(simInst);
            int method = g_bridgeCache.method;
            
            if (method == 1 && State::dynoBaseOffset.load() != 0) {
                DynoHoldOverride(bRpm, simInst);
            } else {
                RpmDirectOverride(bRpm);
            }

            if (bThrot >= 0.0) {
                SafeSetThrottle(engInst, bThrot, isRotary);
            }

            if (simInst) {
                Memory::SafeWriteBool(simInst, 0x1C1, false);
            }
        }
        else if (g_bridgeCache.valid && g_bridgeCache.rpmOverride) {
            if (!bridgeInitialized) {
                currentSmoothedRpm = State::currentRpm.load();
                bridgeInitialized = true;
            }
            if (doOverride) {
                SafeSetThrottle(engInst, tThrot, isRotary);
            }
            RpmDirectOverride(g_bridgeCache.targetRpm);

            if (simInst) {
                Memory::SafeWriteBool(simInst, 0x1C1, false);
            }
        }
        else {
            bridgeInitialized = false;
            if (doOverride) {
                SafeSetThrottle(engInst, tThrot, isRotary);
            }
        }
    }

    // 3. Gear Change (One-shot command)
    int tGear = State::targetGear.exchange(-2);
    if (tGear != -2 && simInst) {
        uintptr_t transInst = State::transmissionInstance.load();
        if (transInst) {
            Memory::SafeWriteInt32(transInst, 0x348, tGear);
        }
    }
}

__int64 __fastcall simProcessHk(__int64 a1, float a2) {
    // ── Frame spike detection: compensate for audio gaps ──────────
    // Normal tick interval at 10kHz = 100μs. When the game stalls
    // (e.g., AC GPU spike), gaps of 500μs-10ms+ appear. During those
    // gaps no physics runs → no audio samples → pop/crackle.
    // Fix: detect large gaps and run extra physics steps via oSimProcess
    // to fill the synthesizer input buffer before the frame reads audio.
    static LARGE_INTEGER lastTick = { 0 };
    static LARGE_INTEGER freq = { 0 };
    static bool freqInit = false;

    if (!freqInit) {
        QueryPerformanceFrequency(&freq);
        QueryPerformanceCounter(&lastTick);
        freqInit = true;
    }

    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    long long gapUs = ((now.QuadPart - lastTick.QuadPart) * 1000000LL) / freq.QuadPart;
    lastTick = now;

    // Track consecutive gaps for sustained stall detection
    static int consecutiveGaps = 0;

    // Normal gap is ~100μs. If gap > 300μs, the game stalled.
    // (Lowered from 500μs to catch DWM-induced throttling earlier)
    if (gapUs > 300 && State::attached.load()) {
        consecutiveGaps++;
        int extraSteps = (int)((gapUs - 100) / 100);  // steps to compensate

        // If we've had 3+ consecutive gaps, it's sustained DWM throttling
        // (not a transient spike). Be more aggressive.
        int maxSteps = 50;  // default: 5ms max (transient spike)
        if (consecutiveGaps >= 3) {
            maxSteps = 100;  // sustained: 10ms max
        }

        if (extraSteps > maxSteps) extraSteps = maxSteps;
        for (int i = 0; i < extraSteps; i++) {
            oSimProcess(a1, a2);
        }
    } else {
        // Reset consecutive counter on clean tick
        if (gapUs <= 300) consecutiveGaps = 0;
    }

    __int64 result = oSimProcess(a1, a2);

    if (!a1 || !State::attached.load()) return result;

    __try {
        simProcessInternal(a1, a2);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        static DWORD lastReport = 0;
        DWORD now2 = GetTickCount();
        if (now2 - lastReport > 5000) {
            Log("[SEH] Caught exception in simProcessInternal! Audio/Bridge may flicker.");
            lastReport = now2;
        }
    }

    return result;
}

__int64 __fastcall rTachRenderHk(__int64 a1, __int64 a2) {
    if (State::attached.load()) {
        // ES-Studio: speedInstance = *(QWORD*)(a1[14] + 0x528)
        // a1 = rTach object, a1[14] = QWORD at offset 0x70
        uintptr_t ptr14 = 0;
        if (Memory::SafeReadUintptr(a1, 0x70, ptr14) && ptr14) {
            uintptr_t spdInst = 0;
            if (Memory::SafeReadUintptr(ptr14, 0x528, spdInst)) {
                State::speedInstance.store(spdInst);
            }
        }
    }
    return oRTachRender(a1, a2);
}

unsigned __int64* __fastcall updateHpAndTorqueHk(__int64 instance, float dt) {
    if (State::attached.load()) {
        double torque = 0, power = 0;
        // Correct offsets (swapped from previous version):
        // m_dynoPower is at 0xA0, m_dynoTorque is at 0xA8
        if (Memory::SafeReadDouble(instance, 0xA8, torque)) {
            // Engine Sim uses N·m internally. Convert to lb-ft for C# formula (HP = T*RPM/5252)
            double torqueLbft = torque * 0.73756;
            State::torqueLbft.store(torqueLbft);
            State::torqueValue.store(torqueLbft);
        }
        if (Memory::SafeReadDouble(instance, 0xA0, power)) {
            // Power in ES is in Watts. Convert to HP.
            double hp = power / 745.7;
            State::powerValue.store(hp);
        }
    }
    return oUpdateHpAndTorque(instance, dt);
}

// ── SampleTriangle hook ──────────────────────────────────────────────
// ES calls sampleTriangle(ignitionFunctionInstance, -crank_v_theta) to
// get the spark advance angle (in radians). We intercept this to:
//   1. Apply advanceOffset (in degrees) from BetterES timing page
//   2. Record the resulting advance for telemetry (MSG_ADVANCE_UPDATE)

double __fastcall sampleTriangleHk(__int64 a1, double a2) {
    double result = oSampleTriangle(a1, a2);

    if (State::attached.load() && State::timingEnabled.load()) {
        uintptr_t ignFnInst = State::ignitionFunctionInstance.load();
        if (ignFnInst && (__int64)ignFnInst == a1) {
            double offsetRad = State::advanceOffset.load() * kDeg;
            result += offsetRad;
        }
    }

    // Always track current advance for telemetry (convert rad → degrees)
    // Only update when this is called for the ignition function instance
    uintptr_t ignFnInst = State::ignitionFunctionInstance.load();
    if (ignFnInst && (__int64)ignFnInst == a1) {
        State::currentAdvance.store(result / kDeg);
    }

    return result;
}

// ── Manifold Pressure Hook ───────────────────────────────────────────
// Hook getManifoldPressure to capture intake manifold pressure (boost/vacuum).
// ES calls this internally; we intercept the return value.
double __fastcall getManifoldPressureHk(__int64 engineInst) {
    double result = oGetManifoldPressure(engineInst);
    if (State::attached.load()) {
        State::manifoldPressure.store(result);
    }
    return result;
}

// ── AFR Cluster Render Hook ──────────────────────────────────────────
// Reads live AFR from the dashboard gauge render chain.
// NOTE: This only works when AFR gauge is visible in ES UI.
//       If the gauge is hidden, this hook will not update.
__int64 __fastcall afrClusterRenderHk(__int64 a1) {
    if (State::attached.load()) {
        uintptr_t labeledGauge = 0, gauge = 0;
        if (Memory::SafeReadUintptr(a1, 0x78, labeledGauge) && labeledGauge) {
            if (Memory::SafeReadUintptr(labeledGauge, 0x70, gauge) && gauge) {
                float afr = 0;
                if (Memory::SafeReadFloat(gauge, 0x70, afr)) {
                    if (afr > 0) State::afr.store((double)afr);
                }
            }
        }
    }
    return oAfrClusterRender(a1);
}

// ── Set Throttle Piston Hook ──
// Intercepts piston engine throttle calls for proper override control.
__int64 __fastcall setThrottlePistonHk(__int64 a1, double a2) {
    if (State::attached.load()) {
        State::cleanTps.store(a2);
    }
    if ((State::throttleOverride.load() || State::bridgeMode.load()) && State::attached.load()) {
        return a1; // Return the instance to prevent Null pointer crashes in fluent calls
    }
    return oSetThrottlePiston(a1, a2);
}

// ── Set Throttle Rotary Hook ──
__int64 __fastcall setThrottleRotaryHk(__int64 a1, double a2) {
    if (State::attached.load()) {
        State::cleanTps.store(a2);
    }
    if ((State::throttleOverride.load() || State::bridgeMode.load()) && State::attached.load()) {
        return a1;
    }
    return oSetThrottleRotary(a1, a2);
}

// ── Change Gear Hook ─────────────────────────────────────────────────
// Intercepts gear changes to enable manual gear commands from BetterES.
__int64 __fastcall changeGearHk(__int64 a1, signed int a2) {
    return oChangeGear(a1, a2);
}

// ── Gas System Reset Hook ────────────────────────────────────────────
// Intercepts fuel/air mixture calculation. Allows:
//   - Fuel cut (set fuel component to 0)
//   - AFR override (modify fuel/air ratio)
struct Mix {
    double p_fuel;
    double p_inert;
    double p_o2;
};

__int64 __fastcall gasSystemResetHk(__int64 instance, double P, double T, __int64 mix) {
    if (State::attached.load()) {
        Mix* mixPtr = reinterpret_cast<Mix*>(mix);

        // Functional Turbo - modulate intake pressure
        double mult = State::turboPowerMultiplier.load();
        if (mult > 1.0) P *= mult;

        if (State::fuelCutEnabled.load()) {
            mixPtr->p_fuel = 0;
            return oGasSystemReset(instance, P, T, mix);
        }

        if (State::useAfrTable.load()) {
            double targetAfr = State::targetAfr.load();

            // Reverting to legacy ES-Studio formula (as requested)
            double target_afr = 0.8 * (13.8 * (targetAfr / 14.7)) * 4;
            double p_air = target_afr / (1 + target_afr);
            mixPtr->p_fuel = 1 - p_air;
            mixPtr->p_o2 = p_air * 0.25;     // Original high-O2 ratio (causes high idle but more power)
            mixPtr->p_inert = p_air * 0.75;  // Original inert ratio
            return oGasSystemReset(instance, P, T, mix);
        }
    }
    return oGasSystemReset(instance, P, T, mix);
}

// ── Read Clutch Position ───────────────────────────────────────
static void ReadClutchPosition() {
    uintptr_t appInst = State::appInstance.load();
    if (appInst) {
        double clutch = 0;
        if (Memory::SafeReadDouble(appInst, 0x28, clutch)) {
            State::clutchPosition.store(clutch);
        }
    }
}

// ── Read Intake Flow (every tick for 20Hz gauge updates) ─────
static void ReadIntakeFlow() {
    uintptr_t engInst = State::engineInstance.load();
    if (!engInst) return;

    // Intake flow rate: m_intakes is a contiguous array of Intake structs
    // Engine instance: 0x178 = Intake* m_intakes, 0x180 = int m_intakeCount
    {
        uintptr_t intakeArray = 0;
        int32_t intakeCount = 0;
        if (Memory::SafeReadUintptr(engInst, 0x178, intakeArray) && intakeArray &&
            Memory::SafeReadInt32(engInst, 0x180, intakeCount) && intakeCount > 0 && intakeCount < 32) {
            double totalFlow = 0;
            for (int i = 0; i < intakeCount; i++) {
                double flowRate = 0;
                uintptr_t intakeBase = intakeArray + i * 0x1A0;
                if (Memory::SafeReadDouble(intakeBase, 0xD8, flowRate)) {
                    totalFlow += flowRate;
                }
            }
            // Conversion: Internal flow unit to SCFM
            // Factor 50.0 converts from simulator's internal unit to SCFM
            State::intakeFlowRate.store(totalFlow * 50.0);
        }
    }
}

// ── Read Engine Temperature (100Hz telemetry, changes slowly) ───
static void ReadEngineTemperature() {
    uintptr_t engInst = State::engineInstance.load();
    if (!engInst) return;

    // Engine temperature: average combustion chamber temperature
    // Engine instance: 0x1C0 = CombustionChamber* m_chambers
    {
        int32_t cylCount = State::cylinderCount.load();
        uintptr_t chamberArray = 0;
        if (cylCount > 0 && cylCount < 32 &&
            Memory::SafeReadUintptr(engInst, 0x1C0, chamberArray) && chamberArray) {
            double tempSum = 0;
            int validChambers = 0;
            for (int i = 0; i < cylCount; i++) {
                double v = 0, t = 0;
                int32_t c = 0;
                uintptr_t chamberBase = chamberArray + i * 0x2A8;
                if (Memory::SafeReadDouble(chamberBase, 0x18, v) &&
                    Memory::SafeReadDouble(chamberBase, 0x20, t) &&
                    Memory::SafeReadInt32(chamberBase, 0x58, c) && c > 0 && v > 0) {
                    double denominator = (double)c * 0.5 * v * 8.31446261815324;
                    if (denominator > 0) {
                        tempSum += t / denominator;
                        validChambers++;
                    }
                }
            }
            if (validChambers > 0) {
                State::engineTemperature.store(tempSum / validChambers);
            }
        }
    }
}

// ── Read Cylinder Count (once, on first tick) ─────
static void ReadCylinderCount() {
    uintptr_t ignInst = State::ignitionInstance.load();
    if (ignInst) {
        int32_t cylCount = 0;
        if (Memory::SafeReadInt32(ignInst, 0x78, cylCount) && cylCount > 0) {
            State::cylinderCount.store(cylCount);
        }
    }
}

// ── Setup ────────────────────────────────────────────────────────────

void SetupHooks() {
    Log("=== SetupHooks starting ===");
    
    if (MH_Initialize() != MH_OK) {
        Log("[!] Failed to initialize MinHook");
        return;
    }
    Log("MinHook initialized OK");

    Log("--- Pattern Scanning ---");
    uintptr_t base = Memory::getBase();
    Log("Base address: 0x%llX", (unsigned long long)base);

    const char* ignitionPattern =
        "40 53 48 81 EC ? ? ? ? 44 0F 29 54 24 ? 48 8B D9 48 8B 49 60 "
        "44 0F 29 4C 24 ? 45 0F 57 C9 44 0F 29 6C 24 ? 44 0F 28 E9 "
        "0F 57 C9 E8 ? ? ? ?";

    const char* processPattern =
        "48 8B C4 48 89 58 10 48 89 70 18 48 89 78 20 55 41 54 41 55 "
        "41 56 41 57 48 8D 68 A1 48 81 EC ? ? ? ? 0F 29 70 C8 0F 29 78 B8 "
        "44 0F 29 40 ? 44";

    // rTachRender pattern from ES-Studio
    const char* rTachPattern =
        "48 8B C4 55 53 57 48 8D 68 A1 48 81 EC ? ? ? ? 0F 29 70 D8 0F 57 C0 0F 29 78 C8 48 8B DA 44 0F 29 40 ? 48 8B F9 44 0F 29 48 ? 45 0F 57 C9";

    // sampleTriangle pattern from ES-Studio
    const char* sampleTriPattern =
        "40 53 48 83 EC 50 0F 29 74 24 ? 48 8B D9 0F 28 F1 F2 0F 59 71 ? 0F 28 CE E8 ? ? ? ? 4C 63 53 44 48 63 D0 45 85 D2 75 0E 0F 57 C0 0F 28";

    auto ignitionMatches = Memory::FindPatternAll(ignitionPattern);
    auto processMatches = Memory::FindPatternAll(processPattern);
    auto rTachMatches = Memory::FindPatternAll(rTachPattern);
    auto sampleTriMatches = Memory::FindPatternAll(sampleTriPattern);

    Log("[+] Ignition pattern: %zu match(es)", ignitionMatches.size());
    Log("[+] SimProcess pattern: %zu match(es)", processMatches.size());
    Log("[+] RTachRender pattern: %zu match(es)", rTachMatches.size());
    Log("[+] SampleTriangle pattern: %zu match(es)", sampleTriMatches.size());

    // Circuit breaker: if any critical pattern is not unique, abort immediately
    if (ignitionMatches.size() != 1) {
        Log("[!] CRITICAL: Expected 1 ignition match, got %zu. Aborting hook setup.", ignitionMatches.size());
        MH_Uninitialize();
        return;
    }
    if (processMatches.size() != 1) {
        Log("[!] CRITICAL: Expected 1 SimProcess match, got %zu. Aborting hook setup.", processMatches.size());
        MH_Uninitialize();
        return;
    }

    uintptr_t ignitionModFunc = ignitionMatches[0];
    uintptr_t processFunc = processMatches[0];

    Log("Ignition Module: 0x%llX", (unsigned long long)ignitionModFunc);
    Log("SimProcess: 0x%llX", (unsigned long long)processFunc);

    bool ok1 = MH_CreateHook((LPVOID)ignitionModFunc, &ignitionModuleHk,
                              (LPVOID*)&oIgnitionModule) == MH_OK;
    Log("Ignition hook create: %s", ok1 ? "OK" : "FAILED");

    bool ok2 = MH_CreateHook((LPVOID)processFunc, &simProcessHk,
                              (LPVOID*)&oSimProcess) == MH_OK;
    Log("SimProcess hook create: %s", ok2 ? "OK" : "FAILED");

    // Optional: rTachRender hook (for speed reading, ES-Studio approach)
    bool ok4 = false;
    if (rTachMatches.size() == 1) {
        uintptr_t rTachFunc = rTachMatches[0];
        Log("RTachRender: 0x%llX", (unsigned long long)rTachFunc);
        ok4 = MH_CreateHook((LPVOID)rTachFunc, &rTachRenderHk,
                              (LPVOID*)&oRTachRender) == MH_OK;
        Log("RTachRender hook create: %s", ok4 ? "OK" : "FAILED");
    } else {
        Log("[!] RTachRender pattern: %zu matches (expected 1), speed will not work", rTachMatches.size());
    }

    // SampleTriangle hook (for spark advance offset + telemetry)
    if (sampleTriMatches.size() == 1) {
        uintptr_t sampleTriFunc = sampleTriMatches[0];
        Log("SampleTriangle: 0x%llX", (unsigned long long)sampleTriFunc);
        bool ok5 = MH_CreateHook((LPVOID)sampleTriFunc, &sampleTriangleHk,
                                  (LPVOID*)&oSampleTriangle) == MH_OK;
        Log("SampleTriangle hook create: %s", ok5 ? "OK" : "FAILED");
    } else {
        Log("[!] SampleTriangle pattern: %zu matches (expected 1), advance offset will not work", sampleTriMatches.size());
    }

    // Torque hook
    const char* updateHpPattern =
        "40 53 48 83 EC 40 48 8B D9 48 8B 89 ? ? ? ? 48 85 C9 0F 84 ? ? ? ? "
        "48 8B 01 0F 29 74 24 ? 0F 29 7C 24 ? 0F 57 FF F3 0F 5A F9 0F 28 C7 "
        "F2 0F 58 05 ? ? ? ? F2";
    auto updateHpMatches = Memory::FindPatternAll(updateHpPattern);
    Log("[+] UpdateHpAndTorque pattern: %zu match(es)", updateHpMatches.size());

    bool ok3 = false;
    if (updateHpMatches.size() == 1) {
        uintptr_t updateHpFunc = updateHpMatches[0];
        Log("UpdateHpAndTorque: 0x%llX", (unsigned long long)updateHpFunc);
        ok3 = MH_CreateHook((LPVOID)updateHpFunc, &updateHpAndTorqueHk,
                              (LPVOID*)&oUpdateHpAndTorque) == MH_OK;
        Log("Torque hook create: %s", ok3 ? "OK" : "FAILED");
    }

    // ── Manifold Pressure hook (from ES-Studio) ──────────────────────
    const char* manifoldPressurePattern =
        "4C 63 91 ? ? ? ? 45 33 C9 F2 0F 10 2D ? ? ? ? 48 8B D1 0F 57 D2 "
        "0F 57 DB 4D 8B C2 49 83 FA 04 0F 8C ? ? ? ? 48 8B 81 ? ? ? ? 48";
    auto manifoldMatches = Memory::FindPatternAll(manifoldPressurePattern);
    Log("[+] GetManifoldPressure pattern: %zu match(es)", manifoldMatches.size());

    bool ok6 = false;
    if (manifoldMatches.size() == 1) {
        uintptr_t manifoldFunc = manifoldMatches[0];
        Log("GetManifoldPressure: 0x%llX", (unsigned long long)manifoldFunc);
        ok6 = MH_CreateHook((LPVOID)manifoldFunc, &getManifoldPressureHk,
                              (LPVOID*)&oGetManifoldPressure) == MH_OK;
        Log("ManifoldPressure hook create: %s", ok6 ? "OK" : "FAILED");
        if (ok6) Log("  → Will read intake manifold pressure (boost/vacuum)");
    } else {
        Log("[!] ManifoldPressure pattern: %zu matches (expected 1)", manifoldMatches.size());
    }

    // ── AFR Cluster Render hook (exactly as ES-Studio) ──────────────────
    const char* afrClusterPattern =
        "48 8B C4 53 48 81 EC ? ? ? ? 0F 29 70 E8 48 8B D9 F3 0F 10 35 "
        "? ? ? ? 0F 29 78 D8 0F 28 CE 44 0F 29 40 ? F3 44 0F 10 05 "
        "? ? ? ? 44 0F 29 48 ? 41 0F 28 C0 44 0F 29 50";
    auto afrMatches = Memory::FindPatternAll(afrClusterPattern);
    Log("[+] AfrClusterRender pattern: %zu match(es)", afrMatches.size());

    bool ok7 = false;
    if (afrMatches.size() == 1) {
        uintptr_t afrFunc = afrMatches[0];
        Log("AfrClusterRender: 0x%llX", (unsigned long long)afrFunc);
        ok7 = MH_CreateHook((LPVOID)afrFunc, &afrClusterRenderHk,
                              (LPVOID*)&oAfrClusterRender) == MH_OK;
        Log("AFR hook create: %s", ok7 ? "OK" : "FAILED");
        if (ok7) Log("  → Will read live AFR from gauge");
    } else {
        Log("[!] AfrClusterRender pattern: %zu matches (expected 1)", afrMatches.size());
    }

    // ── Set Throttle Piston hook (from ES-Studio) ────────────────────
    const char* setThrottlePistonPattern =
        "48 8B 89 ? ? ? ? 48 8B 01 48 FF 60 08";
    auto setThrottlePistonMatches = Memory::FindPatternAll(setThrottlePistonPattern);
    bool ok8 = false;
    uintptr_t setThrottlePistonFunc = 0;
    
    // We must resolve rotary first so we don't accidentally match it as piston
    // ── Set Throttle Rotary hook (from ES-Studio) ────────────────────
    const char* setThrottleRotaryPattern =
        "48 8B 89 ? ? ? ? 48 8B 01 48 FF 60 08 CC CC 48 8B 81 ? ? ? ? "
        "4C 8B C1 48 8B C8 48 8B 10 48 FF 62 10 CC CC CC CC CC CC CC CC "
        "CC CC CC CC 40 53 48 83 EC 20 48 8B D9 E8 ? ? ? ?";
    auto setThrottleRotaryMatches = Memory::FindPatternAll(setThrottleRotaryPattern);
    Log("[+] SetThrottleRotary pattern: %zu match(es)", setThrottleRotaryMatches.size());

    bool ok9 = false;
    uintptr_t setThrottleRotaryFunc = 0;
    if (!setThrottleRotaryMatches.empty()) {
        setThrottleRotaryFunc = setThrottleRotaryMatches[0];
        Log("SetThrottleRotary: 0x%llX", (unsigned long long)setThrottleRotaryFunc);
        ok9 = MH_CreateHook((LPVOID)setThrottleRotaryFunc, &setThrottleRotaryHk,
                              (LPVOID*)&oSetThrottleRotary) == MH_OK;
        Log("SetThrottleRotary hook create: %s", ok9 ? "OK" : "FAILED");
    } else {
        Log("[!] SetThrottleRotary pattern: NOT FOUND");
    }

    // Now resolve piston, explicitly avoiding the rotary function
    Log("[+] SetThrottlePiston pattern: %zu match(es)", setThrottlePistonMatches.size());
    for (auto match : setThrottlePistonMatches) {
        if (match != setThrottleRotaryFunc) {
            setThrottlePistonFunc = match;
            break;
        }
    }
    // Fallback if not found distinct
    if (setThrottlePistonFunc == 0 && !setThrottlePistonMatches.empty()) {
         setThrottlePistonFunc = setThrottlePistonMatches[0];
    }

    if (setThrottlePistonFunc != 0) {
        Log("SetThrottlePiston: 0x%llX", (unsigned long long)setThrottlePistonFunc);
        ok8 = MH_CreateHook((LPVOID)setThrottlePistonFunc, &setThrottlePistonHk,
                              (LPVOID*)&oSetThrottlePiston) == MH_OK;
        Log("SetThrottlePiston hook create: %s", ok8 ? "OK" : "FAILED");
    } else {
        Log("[!] SetThrottlePiston pattern: NOT FOUND");
    }


    // ── Change Gear hook (from ES-Studio) ────────────────────────────
    const char* changeGearPattern =
        "83 FA FF 7C 0E 3B 91 ? ? ? ? 7D 06 89 91 ? ? ? ? C3";
    auto changeGearMatches = Memory::FindPatternAll(changeGearPattern);
    Log("[+] ChangeGear pattern: %zu match(es)", changeGearMatches.size());

    bool ok10 = false;
    if (changeGearMatches.size() == 1) {
        uintptr_t changeGearFunc = changeGearMatches[0];
        Log("ChangeGear: 0x%llX", (unsigned long long)changeGearFunc);
        ok10 = MH_CreateHook((LPVOID)changeGearFunc, &changeGearHk,
                              (LPVOID*)&oChangeGear) == MH_OK;
        Log("ChangeGear hook create: %s", ok10 ? "OK" : "FAILED");
    } else {
        Log("[!] ChangeGear pattern: %zu matches (expected 1)", changeGearMatches.size());
    }

    // ── Gas System Reset hook (from ES-Studio) ───────────────────────
    const char* gasSystemResetPattern =
        "F2 0F 59 49 ? 0F 28 C2 33 C0 F2 0F 59 05 ? ? ? ? F2 0F 5E C8 "
        "66 0F 6E 41 ? F3 0F E6 C0 F2 0F 11 09 F2 0F 59 05 ? ? ? ? F2";
    auto gasSystemResetMatches = Memory::FindPatternAll(gasSystemResetPattern);
    Log("[+] GasSystemReset pattern: %zu match(es)", gasSystemResetMatches.size());

    bool ok11 = false;
    if (gasSystemResetMatches.size() == 1) {
        uintptr_t gasSystemResetFunc = gasSystemResetMatches[0];
        Log("GasSystemReset: 0x%llX", (unsigned long long)gasSystemResetFunc);
        ok11 = MH_CreateHook((LPVOID)gasSystemResetFunc, &gasSystemResetHk,
                              (LPVOID*)&oGasSystemReset) == MH_OK;
        Log("GasSystemReset hook create: %s", ok11 ? "OK" : "FAILED");
        if (ok11) Log("  → Fuel cut and AFR control available");
    } else {
        Log("[!] GasSystemReset pattern: %zu matches (expected 1)", gasSystemResetMatches.size());
    }

    // ── GetForegroundWindow hook (Focus Bypass) ───────────────────
    HMODULE hUser32 = GetModuleHandleA("user32.dll");
    if (hUser32) {
        auto pGetFW = (LPVOID)GetProcAddress(hUser32, "GetForegroundWindow");
        if (pGetFW) {
            MH_CreateHook(pGetFW, (LPVOID)&GetForegroundWindow_Hk, (LPVOID*)&oGetForegroundWindow);
            Log("[+] Hooked GetForegroundWindow for Focus Bypass");
        }
    }

    // Find the Simulator window handle
    struct EnumData { HWND found; DWORD pid; };
    EnumData data = { NULL, GetCurrentProcessId() };
    EnumWindows([](HWND hwnd, LPARAM lParam) -> BOOL {
        auto d = (EnumData*)lParam;
        DWORD windowPid = 0;
        GetWindowThreadProcessId(hwnd, &windowPid);
        if (windowPid == d->pid && IsWindowVisible(hwnd)) {
            char title[256];
            GetWindowTextA(hwnd, title, 256);
            if (strstr(title, "Engine Simulator")) {
                d->found = hwnd;
                return FALSE;
            }
        }
        return TRUE;
    }, (LPARAM)&data);

    if (data.found) {
        hSimWindow = data.found;
        Log("[+] Found Simulator window: %p", hSimWindow);
    }

    if (!ok1 || !ok2) {
        Log("[!] CRITICAL: Hook creation failed!");
        MH_Uninitialize();
        State::attached.store(false);
        return;
    }

    MH_EnableHook(MH_ALL_HOOKS);

    State::attached.store(true);
    Log("=== Hooks active ===");
}

void CleanupHooks() {
    State::attached.store(false);
    State::running.store(false);

    MH_DisableHook(MH_ALL_HOOKS);
    MH_Uninitialize();

    Log("[+] Hooks removed");
}

// End of hooks.cpp
