# BetterES Performance Fixes — Patch Summary

## Latency Budget

| Fix | Latency Cost | What It Solves |
|-----|-------------|----------------|
| WndProc hook | **0ms** | SDL/ES focus detection via WM messages |
| DWM occlusion fix | **0ms** | DWM stops compositing → Present blocks |
| SDL swap hook (paced) | **~0ms physics, 33ms visual** | Present blocks on DWM when unfocused |
| Thread priority (all threads) | **0ms** | E-core scheduling, reduced timeslice |
| Spike compensation (improved) | **0-10ms during stalls only** | Sustained DWM throttling |

**Total constant latency: ~0ms.** The swap hook adds 33ms *visual* latency (window refreshes at 30fps instead of 60fps when unfocused) but physics and audio are unaffected.

## Files

```
BetterESHook/src/
├── perf_fixes.h          # NEW — header
├── perf_fixes.cpp         # NEW — fixes 1-4 implementation
├── hooks.cpp              # MODIFIED — improved spike compensation
├── dllmain.cpp            # MODIFIED — call InstallPerfFixes()
├── dllmain_perf_patch.diff    # Patch for dllmain.cpp
├── hooks_spike_patch.diff     # Patch for hooks.cpp spike comp
└── cmake_perf_patch.diff      # Patch for CMakeLists.txt
```

## Apply

```bash
cd Better-ES/BetterESHook

# Apply all patches
git apply src/dllmain_perf_patch.diff
git apply src/hooks_spike_patch.diff
git apply src/cmake_perf_patch.diff

# Build
cmake -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
```

## Tuning (in perf_fixes.cpp)

```cpp
// Swap pacing — lower = less visual latency, more DWM overhead
static constexpr double SWAP_PACE_INTERVAL_US = 33000.0;  // 30fps
// Set to 16000.0 for 60fps (minimal visual lag, more DWM cost)
// Set to 50000.0 for 20fps (max performance, visible choppiness)

// Spike detection — lower = more sensitive
static constexpr long long SPIKE_DETECT_THRESHOLD_US = 300;  // 300μs
// Set to 200 for aggressive detection (more CPU overhead)
// Set to 500 for relaxed detection (may miss DWM stalls)

// Spike compensation cap
static constexpr int SPIKE_MAX_EXTRA_STEPS = 100;  // 10ms max
// Set to 50 for less burst latency (may crackle on sustained stalls)
// Set to 150 for more headroom (up to 15ms burst latency)
```
