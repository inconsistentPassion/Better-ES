#include "perf_fixes.h"
#include "log.h"
#include <Windows.h>
#include <dwmapi.h>
#include <TlHelp32.h>
#include <MinHook.h>

#pragma comment(lib, "dwmapi.lib")

// SDLCALL calling convention (same as __cdecl on Windows/x64)
#ifndef SDLCALL
#define SDLCALL __cdecl
#endif

// ═══════════════════════════════════════════════════════════════════════
// Tuning Constants
// ═══════════════════════════════════════════════════════════════════════

// Swap pacing: present at this interval when unfocused.
// Lower = less latency but more DWM overhead.
// 16ms = 60fps (full speed), 33ms = 30fps (half speed, recommended)
// 50ms = 20fps (minimal DWM cost)
static constexpr double SWAP_PACE_INTERVAL_US = 33000.0;  // 33ms = ~30fps

// Spike compensation thresholds
static constexpr long long SPIKE_DETECT_THRESHOLD_US = 300;   // detect gaps > 300μs
static constexpr int SPIKE_MAX_EXTRA_STEPS = 100;              // up to 10ms of extra physics
static constexpr int SPIKE_EXTRA_PER_GAP_US = 100;             // 1 extra step per 100μs gap

// Focus state check interval (ms)
static constexpr int FOCUS_CHECK_INTERVAL_MS = 50;


// ═══════════════════════════════════════════════════════════════════════
// Fix 1: WndProc Hook — Intercept Focus Messages
// ═══════════════════════════════════════════════════════════════════════
//
// Problem:  SDL/ES processes WM_ACTIVATE, WM_SETFOCUS, WM_KILLFOCUS
//           messages directly. The GetForegroundWindow hook only catches
//           one internal code path.
//
// Solution: Subclass the window to intercept these messages before SDL's
//           WndProc sees them.
//
// Latency:  0ms

static WNDPROC g_originalWndProc = NULL;

static LRESULT CALLBACK FocusWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
        case WM_ACTIVATE:
            return DefWindowProc(hwnd, msg, MAKEWPARAM(WA_ACTIVE, 0), lParam);
        case WM_ACTIVATEAPP:
            return DefWindowProc(hwnd, msg, TRUE, lParam);
        case WM_SETFOCUS:
        case WM_KILLFOCUS:
            return 0;
        case WM_NCACTIVATE:
            return DefWindowProc(hwnd, msg, TRUE, lParam);
    }
    return CallWindowProcW(g_originalWndProc, hwnd, msg, wParam, lParam);
}

static bool InstallWndProcHook() {
    HWND hTarget = NULL;
    struct EnumData { HWND found; DWORD pid; } data = { NULL, GetCurrentProcessId() };

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

    hTarget = data.found;
    if (!hTarget) {
        Log("[PERF] WndProc hook: ES window not found");
        return false;
    }

    g_originalWndProc = (WNDPROC)SetWindowLongPtrW(
        hTarget, GWLP_WNDPROC, (LONG_PTR)FocusWndProc);

    if (!g_originalWndProc) {
        Log("[PERF] WndProc hook: SetWindowLongPtrW failed (err=%lu)", GetLastError());
        return false;
    }

    Log("[PERF] WndProc hook installed — WM_ACTIVATE/WM_SETFOCUS intercepted");
    return true;
}


// ═══════════════════════════════════════════════════════════════════════
// Fix 2: DWM Occlusion Prevention
// ═══════════════════════════════════════════════════════════════════════
//
// Latency:  0ms

static bool InstallDwmFix() {
    HWND hTarget = NULL;
    struct EnumData { HWND found; DWORD pid; } data = { NULL, GetCurrentProcessId() };
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
    hTarget = data.found;

    if (!hTarget) {
        Log("[PERF] DWM fix: ES window not found");
        return false;
    }

    bool anyOk = false;
    RECT windowRect;
    GetWindowRect(hTarget, &windowRect);

    // Extended frame bounds — DWM thinks window is always partially visible
    HRESULT hr = DwmSetWindowAttribute(hTarget, DWMWA_EXTENDED_FRAME_BOUNDS,
                                         &windowRect, sizeof(RECT));
    if (SUCCEEDED(hr)) { Log("[PERF] DWM: Extended frame bounds set"); anyOk = true; }

    // Non-client rendering policy — keep window in compositing pipeline
    DWORD ncPolicy = DWMNCRP_ENABLED;
    hr = DwmSetWindowAttribute(hTarget, 2, &ncPolicy, sizeof(DWORD));
    if (SUCCEEDED(hr)) { Log("[PERF] DWM: NC rendering = ENABLED"); anyOk = true; }

    // Remove WS_EX_TOPMOST if set (interferes with DWM compositing)
    LONG_PTR exStyle = GetWindowLongPtrW(hTarget, GWL_EXSTYLE);
    if (exStyle & WS_EX_TOPMOST) {
        SetWindowPos(hTarget, HWND_NOTOPMOST, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Log("[PERF] DWM: Removed WS_EX_TOPMOST");
    }

    return anyOk;
}


// ═══════════════════════════════════════════════════════════════════════
// Fix 3: SDL_GL_SwapWindow Hook — Paced Present When Unfocused
// ═══════════════════════════════════════════════════════════════════════
//
// Problem:  SDL_GL_SwapWindow blocks on DWM when the window is unfocused
//           and behind a fullscreen app. This stalls the render loop which
//           (in ES) is coupled to the physics/audio pipeline.
//
// Solution: When unfocused, pace the swap at ~30fps instead of the native
//           rate. Between presents, return immediately — physics runs free.
//
//           This is a middle ground:
//           - "Skip entirely" (our v1): 0 DWM overhead but window looks frozen
//           - "Full speed" (original): smooth but DWM blocks → perf death
//           - "Paced 30fps": window stays responsive, DWM stays happy,
//             physics runs at full 10kHz between presents
//
// Latency:  ~0ms for physics. Audio unaffected (decoupled from render).
//           Visual latency: ~33ms between frames when unfocused.
//           This is NOT player-facing latency — the game (AC) handles visuals.

typedef void (SDLCALL* SDL_GL_SwapWindowFn)(void* window);
static SDL_GL_SwapWindowFn oSDL_GL_SwapWindow = nullptr;
static bool g_esFocused = true;

void SDLCALL SDL_GL_SwapWindow_Hk(void* window) {
    if (!g_esFocused) {
        // Paced present: only actually present once per interval
        static LARGE_INTEGER lastPresent = {0};
        static LARGE_INTEGER freq = {0};
        static bool init = false;
        if (!init) {
            QueryPerformanceFrequency(&freq);
            QueryPerformanceCounter(&lastPresent);
            init = true;
        }

        LARGE_INTEGER now;
        QueryPerformanceCounter(&now);
        double elapsedUs = ((double)(now.QuadPart - lastPresent.QuadPart) * 1000000.0)
                           / (double)freq.QuadPart;

        if (elapsedUs < SWAP_PACE_INTERVAL_US) {
            // Skip this present — physics runs without waiting
            return;
        }

        // Time to present — do it to keep DWM happy
        lastPresent = now;
        oSDL_GL_SwapWindow(window);
        return;
    }
    oSDL_GL_SwapWindow(window);
}

static bool InstallSdlSwapHook() {
    HMODULE hSDL = GetModuleHandleA("SDL2.dll");
    if (!hSDL) {
        for (int i = 0; i < 20; i++) {
            Sleep(100);
            hSDL = GetModuleHandleA("SDL2.dll");
            if (hSDL) break;
        }
    }
    if (!hSDL) {
        Log("[PERF] SDL swap hook: SDL2.dll not found");
        return false;
    }
    Log("[PERF] SDL swap hook: SDL2.dll at 0x%llX", (unsigned long long)hSDL);

    auto pSwapWindow = (LPVOID)GetProcAddress(hSDL, "SDL_GL_SwapWindow");
    if (!pSwapWindow) {
        Log("[PERF] SDL swap hook: SDL_GL_SwapWindow not found");
        return false;
    }
    Log("[PERF] SDL swap hook: SDL_GL_SwapWindow at 0x%llX", (unsigned long long)pSwapWindow);

    if (MH_CreateHook(pSwapWindow, &SDL_GL_SwapWindow_Hk,
                       (LPVOID*)&oSDL_GL_SwapWindow) != MH_OK) {
        Log("[PERF] SDL swap hook: MH_CreateHook failed");
        return false;
    }

    Log("[PERF] SDL swap hook installed (paced at %.0fms when unfocused)",
        SWAP_PACE_INTERVAL_US / 1000.0);
    return true;
}


// ═══════════════════════════════════════════════════════════════════════
// Fix 4: Full Thread Priority Boost
// ═══════════════════════════════════════════════════════════════════════
//
// Latency:  0ms

static void BoostAllThreads() {
    DWORD pid = GetCurrentProcessId();
    HANDLE hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (hSnap == INVALID_HANDLE_VALUE) return;

    HMODULE hNtdll = GetModuleHandleA("ntdll.dll");
    typedef NTSTATUS(NTAPI* NtSetInfoThreadFn)(HANDLE, ULONG, PVOID, ULONG);
    auto NtSetInfoThread = hNtdll
        ? (NtSetInfoThreadFn)GetProcAddress(hNtdll, "NtSetInformationThread")
        : nullptr;

    THREADENTRY32 te = {};
    te.dwSize = sizeof(te);
    int count = 0, throttlingDisabled = 0;

    if (Thread32First(hSnap, &te)) {
        do {
            if (te.th32OwnerProcessID != pid) continue;
            HANDLE hThread = OpenThread(
                THREAD_SET_INFORMATION | THREAD_QUERY_INFORMATION,
                FALSE, te.th32ThreadID);
            if (!hThread) continue;

            if (SetThreadPriority(hThread, THREAD_PRIORITY_HIGHEST)) count++;
            if (NtSetInfoThread) {
                LONG disable = 0;
                if (NtSetInfoThread(hThread, 37, &disable, sizeof(disable)) >= 0)
                    throttlingDisabled++;
            }
            CloseHandle(hThread);
        } while (Thread32Next(hSnap, &te));
    }
    CloseHandle(hSnap);
    Log("[PERF] Thread boost: %d threads HIGHEST, %d no throttling",
        count, throttlingDisabled);
}


// ═══════════════════════════════════════════════════════════════════════
// Fix 5: Enhanced Spike Compensation (Hook for SimProcess)
// ═══════════════════════════════════════════════════════════════════════
//
// Problem:  The existing spike compensation in simProcessHk detects gaps
//           > 500μs and caps at 50 extra steps. This handles brief GPU
//           spikes but not sustained DWM stalls (which can last 5-15ms).
//
// Solution: More aggressive detection + higher cap. Also detect sustained
//           patterns (multiple small gaps in sequence = DWM throttling,
//           not a single spike).
//
// Latency:  +0-10ms during stalls only. Constant latency = 0ms.
//
// NOTE: This is integrated into hooks.cpp's simProcessHk, not as a
// separate hook. See the patch for hooks.cpp below.

// The actual changes go into hooks.cpp. This file just documents them.
// The key parameters:

// Current (hooks.cpp):
//   gapUs > 500     → trigger
//   extraSteps > 50 → cap (5ms max)
//   (gapUs - 100) / 100 → steps

// Improved:
//   gapUs > 300     → trigger (more sensitive)
//   extraSteps > 100 → cap (10ms max)
//   (gapUs - 100) / 100 → steps (same formula)
//
// The lower threshold catches DWM-induced stalls earlier.
// The higher cap handles sustained throttling without dropping samples.


// ═══════════════════════════════════════════════════════════════════════
// Focus State Management
// ═══════════════════════════════════════════════════════════════════════

static void UpdateFocusState() {
    HWND hFg = GetForegroundWindow();
    if (!hFg) { g_esFocused = false; return; }
    DWORD fgPid = 0;
    GetWindowThreadProcessId(hFg, &fgPid);
    g_esFocused = (fgPid == GetCurrentProcessId());
}


// ═══════════════════════════════════════════════════════════════════════
// Master Installer
// ═══════════════════════════════════════════════════════════════════════

void InstallPerfFixes() {
    Log("");
    Log("╔══════════════════════════════════════════════════════╗");
    Log("║     Performance Fixes v2.0 (low-latency)            ║");
    Log("╚══════════════════════════════════════════════════════╝");

    bool fix1 = InstallWndProcHook();
    bool fix2 = InstallDwmFix();
    bool fix3 = InstallSdlSwapHook();
    BoostAllThreads();

    // Focus state polling — 50ms interval (low overhead, responsive enough)
    CreateThread(NULL, 0, [](LPVOID) -> DWORD {
        while (true) {
            Sleep(FOCUS_CHECK_INTERVAL_MS);
            UpdateFocusState();
        }
        return 0;
    }, NULL, 0, NULL);

    Log("Fixes: WndProc=%s DWM=%s SDL_Swap=%s Threads=done",
        fix1 ? "OK" : "SKIP",
        fix2 ? "OK" : "SKIP",
        fix3 ? "OK" : "SKIP");
    Log("Swap pace: %.0fms (%.0ffps) when unfocused",
        SWAP_PACE_INTERVAL_US / 1000.0,
        1000000.0 / SWAP_PACE_INTERVAL_US);
    Log("══════════════════════════════════════════════════════");
}
