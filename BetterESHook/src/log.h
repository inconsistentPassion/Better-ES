#pragma once
#ifndef LOG_H
#define LOG_H

// File logger — writes to %TEMP%\es_hook_log.txt
// Use this instead of std::cout since the DLL runs inside the game process
// and its stdout is invisible.
void LogInit();       // Call once at startup
void Log(const char* fmt, ...);  // printf-style logging

#endif
