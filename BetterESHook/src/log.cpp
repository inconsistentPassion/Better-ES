#define _CRT_SECURE_NO_WARNINGS
#include "log.h"
#include <Windows.h>
#include <cstdarg>
#include <cstdio>
#include <ctime>

static FILE *g_logFile = nullptr;

void LogInit() {
  char path[MAX_PATH];
  GetTempPathA(MAX_PATH, path);
  strcat_s(path, "es_hook_log.txt");
  g_logFile = fopen(path, "w");
  if (g_logFile) {
    fprintf(g_logFile, "=== es_hook log started ===\n");
    fflush(g_logFile);
  }
}

void Log(const char *fmt, ...) {
  if (!g_logFile)
    return;
  va_list args;
  va_start(args, fmt);
  vfprintf(g_logFile, fmt, args);
  fprintf(g_logFile, "\n");
  fflush(g_logFile); // Flush every line so we see output even on crash
  va_end(args);
}
