#pragma once
#ifndef MEMORY_H
#define MEMORY_H

#include <Windows.h>
#include <cstdint>
#include <vector>
#include "common.h"

namespace Memory {
    uintptr_t getBase();
    uintptr_t FindPatternIDA(const char* szSignature);

    // Returns ALL matches for a pattern (for validation)
    std::vector<uintptr_t> FindPatternAll(const char* szSignature);

    // Validate that addr points to a real function (checks common x64 prolog bytes)
    bool ValidateFunctionProlog(uintptr_t addr);

    void WriteLog(const char* name, uintptr_t addr);

    bool SafeReadDouble(uintptr_t base, uintptr_t offset, double& out);
    bool SafeReadFloat(uintptr_t base, uintptr_t offset, float& out);
    bool SafeReadUintptr(uintptr_t base, uintptr_t offset, uintptr_t& out);
    bool SafeReadInt32(uintptr_t base, uintptr_t offset, int32_t& out);
    bool SafeReadBool(uintptr_t base, uintptr_t offset, bool& out);
    bool SafeWriteDouble(uintptr_t base, uintptr_t offset, double value);
    bool SafeWriteBool(uintptr_t base, uintptr_t offset, bool value);
    bool SafeWriteInt32(uintptr_t base, uintptr_t offset, int32_t value);

    // Shared Memory Bridge (MMF)
    bool OpenBridgeMemory();
    bool ReadBridgeMemory(SharedBridgeData& data);
    bool WriteBridgeMemory(const SharedBridgeData& data);
    void CloseBridgeMemory();
}

#endif
