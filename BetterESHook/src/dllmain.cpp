#include "common.h"
#include "hooks.h"
#include "perf_fixes.h"
#include "hooks.h"
#include "log.h"
#include <MinHook.h>
#include <stdlib.h>

// ── State definitions ────────────────────────────────────────────────

namespace State {
    std::atomic<uintptr_t> appInstance{0};
    std::atomic<uintptr_t> simulatorInstance{0};
    std::atomic<uintptr_t> engineInstance{0};
    std::atomic<uintptr_t> ignitionInstance{0};
    std::atomic<uintptr_t> transmissionInstance{0};

    std::atomic<uintptr_t> ignitionFunctionInstance{0};

    std::atomic<double> currentRpm{0.0};
    std::atomic<double> maxRpm{0.0};
    std::atomic<double> torqueLbft{0.0};
    std::atomic<double> vehicleSpeed{0.0};
    std::atomic<uintptr_t> speedInstance{0};
    std::atomic<int> currentGear{-1};  // -1 = Neutral

    std::atomic<double> targetThrottle{0.0};
    std::atomic<bool> throttleOverride{false};

    std::atomic<bool> attached{false};
    std::atomic<double> targetRpm{0.0};
    std::atomic<bool> timingEnabled{false};
    std::atomic<double> advanceOffset{0.0};
    std::atomic<bool> revLimiterEnabled{false};
    std::atomic<double> revLimitRpm{7000.0};
    std::atomic<double> revLimiterCutTime{0.05};
    std::atomic<bool> ignitionCutEnabled{false};
    std::atomic<double> ignitionCutPercent{100.0};
    std::atomic<double> currentAdvance{0.0};
    std::atomic<bool> targetRpmEnabled{false};

    // Extended telemetry
    std::atomic<double> manifoldPressure{1.01325};  // atmospheric default
    std::atomic<double> afr{0.0};
    std::atomic<double> intakeFlowRate{0.0};
    std::atomic<double> engineTemperature{0.0};
    std::atomic<double> clutchPosition{0.0};
    std::atomic<double> cleanTps{0.0};
    std::atomic<int> cylinderCount{0};
    std::atomic<double> torqueValue{0.0};
    std::atomic<double> powerValue{0.0};

    // Extended controls
    std::atomic<bool> fuelCutEnabled{false};
    std::atomic<bool> useAfrTable{false};
    std::atomic<double> targetAfr{13.3};  // simulator default

    // Target states for synchronized application
    std::atomic<int> targetIgnition{-1};
    std::atomic<int> targetStarter{-1};
    std::atomic<int> targetDyno{-1};
    std::atomic<int> targetGear{-2};

    std::atomic<bool> bridgeMode{false};
    std::atomic<double> bridgeRpm{0.0};
    std::atomic<double> bridgeThrottle{0.0};
    std::atomic<double> bridgeManifold{1.01325};
    std::atomic<bool> bridgeActive{false};
    std::atomic<int> bridgeMethod{0};
    std::atomic<uintptr_t> dynoBaseOffset{0};
    std::atomic<bool> dynoDiscovered{false};

    std::atomic<double> turboPowerMultiplier{1.0};
    std::atomic<bool> running{true};
}

// ── Initialization thread (avoid loader lock in DllMain) ─────────────

static void SetupThread() {
    LogInit();
    Log("SetupThread started");

    // ── Performance: prevent Windows from throttling ES when unfocused ──

    // 1. Force high-resolution timer (Windows drops to 15.625ms when unfocused,
    //    which kills 10kHz physics and causes audio crackling)
    HMODULE ntdll = GetModuleHandleA("ntdll.dll");
    if (ntdll) {
        typedef NTSTATUS(NTAPI* NtSetTimerResolutionFn)(ULONG, BOOLEAN, PULONG);
        auto NtSetTimerRes = (NtSetTimerResolutionFn)GetProcAddress(ntdll, "NtSetTimerResolution");
        if (NtSetTimerRes) {
            ULONG actual;
            NtSetTimerRes(5000, TRUE, &actual);  // Request 0.5ms resolution
            Log("[PERF] Timer resolution forced to %.2f ms (requested 0.50 ms)", actual / 10000.0);
        }

        // Disable thread power throttling (Win10 1709+)
        // Prevents Windows from reducing CPU frequency for background ES threads
        typedef NTSTATUS(NTAPI* NtSetInformationThreadFn)(HANDLE, ULONG, PVOID, ULONG);
        auto NtSetInfoThread = (NtSetInformationThreadFn)GetProcAddress(ntdll, "NtSetInformationThread");
        if (NtSetInfoThread) {
            LONG disableThrottling = 0;
            NtSetInfoThread(GetCurrentThread(), 37, &disableThrottling, sizeof(disableThrottling));
            Log("[PERF] Thread power throttling disabled");
        }
    }

    // 2. Boost process priority so ES gets CPU even when AC is focused
    SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
    Log("[PERF] Process priority: HIGH, thread priority: ABOVE_NORMAL");

    // 3. Prevent display/system sleep while ES is running
    SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
    Log("[PERF] Sleep prevention active");

    Log("Waiting 500ms for game init...");
    Sleep(500);
    Log("Calling SetupHooks...");
    SetupHooks();
    Log("SetupHooks returned, attached=%d", State::attached.load() ? 1 : 0);

    // ── Performance fixes: WndProc, DWM, SDL swap, thread priority ──
    Log("Installing perf fixes...");
    InstallPerfFixes();

    // 5. Re-apply thread priority AFTER hooks are installed (hooks run in
    //    sim/render threads which inherit the DLL thread's priority briefly)
    //    Also disable power throttling for the current thread again
    if (ntdll) {
        typedef NTSTATUS(NTAPI* NtSetInformationThreadFn)(HANDLE, ULONG, PVOID, ULONG);
        auto NtSetInfoThread = (NtSetInformationThreadFn)GetProcAddress(ntdll, "NtSetInformationThread");
        if (NtSetInfoThread) {
            LONG disableThrottling = 0;
            NtSetInfoThread(GetCurrentThread(), 37, &disableThrottling, sizeof(disableThrottling));
        }
    }
}

// ── DLL Entry Point ──────────────────────────────────────────────────

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID lpReserved) {
    switch (reason) {
        case DLL_PROCESS_ATTACH: {
            DisableThreadLibraryCalls(hModule);
            // Suppress the "abort() has been called" dialog — equivalent to
            // clicking Ignore so it silently terminates instead of showing
            // the retry/debug dialog.
            _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
            CreateThread(NULL, 0, (LPTHREAD_START_ROUTINE)SetupThread, NULL, 0, NULL);
            break;
        }
        case DLL_PROCESS_DETACH: {
            State::running.store(false);
            State::attached.store(false);

            // Always disable hooks first to restore original code bytes.
            // During process termination (lpReserved != NULL), the C++ runtime
            // and threads are being destroyed, so complex cleanup will crash.
            __try {
                MH_DisableHook(MH_ALL_HOOKS);
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                // MinHook may already be partially torn down during process exit.
                // Swallow any exceptions to avoid abort().
            }

            // Only do full cleanup (thread join, MH_Uninitialize) on explicit
            // FreeLibrary. During process termination, skip it to avoid abort().
            if (lpReserved == NULL) {
                Log("[+] DLL unloaded via FreeLibrary - cleaning up");
                __try {
                    MH_Uninitialize();
                }
                __except (EXCEPTION_EXECUTE_HANDLER) {
                    // Swallow exceptions during cleanup
                }
            } else {
                Log("[+] DLL unloaded via process termination - hooks disabled, skipping cleanup");
            }
            break;
        }
    }
    return TRUE;
}
