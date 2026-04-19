#pragma once
#ifndef HOOKS_H
#define HOOKS_H

typedef __int64 (__fastcall* SimProcessFn)(__int64 a1, float a2);
typedef unsigned __int64* (__fastcall* UpdateHpAndTorqueFn)(__int64 instance, float dt);
typedef __int64 (__fastcall* RTachRenderFn)(__int64 a1, __int64 a2);
typedef double (__fastcall* SampleTriangleFn)(__int64 a1, double a2);
typedef double (__fastcall* GetManifoldPressureFn)(__int64 engineInst);
typedef __int64 (__fastcall* AfrClusterRenderFn)(__int64 a1);
typedef __int64 (__fastcall* SetThrottlePistonFn)(__int64 a1, double a2);
typedef __int64 (__fastcall* SetThrottleRotaryFn)(__int64 a1, double a2);
typedef __int64 (__fastcall* ChangeGearFn)(__int64 a1, signed int a2);

void SetupHooks();
void CleanupHooks();

#endif
