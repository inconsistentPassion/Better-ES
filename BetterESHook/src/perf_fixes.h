#pragma once
#ifndef PERF_FIXES_H
#define PERF_FIXES_H

// Zero-latency (mostly) performance fixes for unfocused/minimized ES.
//
// Fixes:
//   1. WndProc hook     — fake always-focused state for SDL/ES
//   2. DWM occlusion    — prevent DWM from stopping window compositing
//   3. SDL swap hook    — paced present at 30fps when unfocused
//                        (physics unaffected, only visual refresh rate changes)
//   4. Thread priority  — boost ALL ES threads to HIGHEST
//   5. Spike compensation — see hooks_spike_patch.diff (in hooks.cpp)
//
// Tuning constants are at the top of perf_fixes.cpp.
// See PATCH_README.md for full documentation.

void InstallPerfFixes();

#endif
