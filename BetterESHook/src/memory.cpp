#include "memory.h"
#include "common.h"
#include <Psapi.h>
#include <iostream>
#include <sstream>
#include <vector>

#pragma comment(lib, "Psapi.lib")

#define INRANGE(x, a, b) (x >= a && x <= b)
#define getBits(x) (INRANGE((x & (~0x20)), 'A', 'F') ? ((x & (~0x20)) - 'A' + 0xa) : (INRANGE(x, '0', '9') ? x - '0' : 0))
#define get_byte(x) (getBits(x[0]) << 4 | getBits(x[1]))

namespace Memory {

    uintptr_t getBase() {
        return (uintptr_t)GetModuleHandleA(NULL);
    }

    uintptr_t FindPatternIDA(const char* szSignature) {
        auto all = FindPatternAll(szSignature);
        return all.empty() ? 0 : all[0];
    }

    std::vector<uintptr_t> FindPatternAll(const char* szSignature) {
        std::vector<uintptr_t> matches;
        MODULEINFO modInfo;
        GetModuleInformation(GetCurrentProcess(), GetModuleHandleA(NULL), &modInfo, sizeof(MODULEINFO));
        uintptr_t startAddress = (uintptr_t)GetModuleHandleA(NULL);
        uintptr_t endAddress = startAddress + (uintptr_t)modInfo.SizeOfImage;
        const char* pat = szSignature;
        uintptr_t firstMatch = 0;

        for (uintptr_t pCur = startAddress; pCur < endAddress; pCur++) {
            if (!*pat) {
                // Full pattern matched
                matches.push_back(firstMatch);
                pat = szSignature;
                firstMatch = 0;
                continue;
            }
            if (*(PBYTE)pat == '?' || *(BYTE*)pCur == get_byte(pat)) {
                if (!firstMatch) firstMatch = pCur;
                if (!pat[2]) {
                    // Last byte of pattern matched
                    matches.push_back(firstMatch);
                    pat = szSignature;
                    firstMatch = 0;
                    continue;
                }
                if (*(PWORD)pat == ('??') || *(PBYTE)pat != '?') pat += 3;
                else pat += 2;
            }
            else {
                pat = szSignature;
                firstMatch = 0;
            }
        }
        return matches;
    }

    bool ValidateFunctionProlog(uintptr_t addr) {
        if (!addr) return false;
        uint8_t* p = (uint8_t*)addr;

        // Common x64 function prolog patterns:
        // 48 89 5C 24 XX       = mov [rsp+XX], rbx
        // 48 83 EC XX          = sub rsp, XX
        // 48 8B C4             = mov rax, rsp
        // 40 53                = push rbx
        // 40 55                = push rbp
        // 48 89                = various mov [rsp+...], reg
        // 4C 89                = various mov [rsp+...], rXX
        // 55                   = push rbp
        // 48 81 EC             = sub rsp, imm32

        // Check first byte is a known x64 prolog start
        if (p[0] == 0x48 || p[0] == 0x40 || p[0] == 0x4C || p[0] == 0x55 || p[0] == 0x53) {
            // Second byte should be a common follow-up
            if (p[1] == 0x89 || p[1] == 0x83 || p[1] == 0x8B || p[1] == 0x81 ||
                p[1] == 0x53 || p[1] == 0x55 || p[1] == 0x57 || p[1] == 0x56) {
                return true;
            }
        }
        return false;
    }

    void WriteLog(const char* name, uintptr_t addr) {
        std::stringstream ss;
        ss << std::hex << std::uppercase << addr;
        std::cout << "[+] " << name << " : 0x" << ss.str() << "\n";
    }

    bool SafeReadDouble(uintptr_t base, uintptr_t offset, double& out) {
        if (!base) return false;
        __try { out = *(double*)(base + offset); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    bool SafeReadFloat(uintptr_t base, uintptr_t offset, float& out) {
        if (!base) return false;
        __try { out = *(float*)(base + offset); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    bool SafeReadUintptr(uintptr_t base, uintptr_t offset, uintptr_t& out) {
        if (!base) return false;
        __try { out = *(uintptr_t*)(base + offset); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    bool SafeReadInt32(uintptr_t base, uintptr_t offset, int32_t& out) {
        if (!base) return false;
        __try { out = *(int32_t*)(base + offset); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    bool SafeReadBool(uintptr_t base, uintptr_t offset, bool& out) {
        if (!base) return false;
        __try { out = *(bool*)(base + offset); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    bool SafeWriteDouble(uintptr_t base, uintptr_t offset, double value) {
        if (!base) return false;
        __try { *(double*)(base + offset) = value; return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    bool SafeWriteBool(uintptr_t base, uintptr_t offset, bool value) {
        if (!base) return false;
        __try { *(bool*)(base + offset) = value; return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }
    bool SafeWriteInt32(uintptr_t base, uintptr_t offset, int32_t value) {
        if (!base) return false;
        __try { *(int32_t*)(base + offset) = value; return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    static HANDLE hBridgeMap = NULL;
    static void* pBridgeMemory = NULL;

    bool OpenBridgeMemory() {
        if (hBridgeMap != NULL) return true;

        hBridgeMap = OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, "BetterES_BridgeData");
        if (hBridgeMap == NULL) return false;

        pBridgeMemory = MapViewOfFile(hBridgeMap, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SharedBridgeData));
        if (pBridgeMemory == NULL) {
            CloseHandle(hBridgeMap);
            hBridgeMap = NULL;
            return false;
        }

        return true;
    }

    bool ReadBridgeMemory(SharedBridgeData& data) {
        if (pBridgeMemory == NULL) {
            if (!OpenBridgeMemory()) return false;
        }

        __try {
            memcpy(&data, pBridgeMemory, sizeof(SharedBridgeData));
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            return false;
        }
    }

    bool WriteBridgeMemory(const SharedBridgeData& data) {
        if (pBridgeMemory == NULL) {
            if (!OpenBridgeMemory()) return false;
        }

        __try {
            memcpy(pBridgeMemory, &data, sizeof(SharedBridgeData));
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            return false;
        }
    }

    void CloseBridgeMemory() {
        if (pBridgeMemory != NULL) {
            UnmapViewOfFile(pBridgeMemory);
            pBridgeMemory = NULL;
        }
        if (hBridgeMap != NULL) {
            CloseHandle(hBridgeMap);
            hBridgeMap = NULL;
        }
    }

} // namespace Memory
