# BetterES Hook - Engine Simulator DLL Injection

This is a C++ DLL that injects into Engine Simulator to provide real-time RPM, torque, and engine control capabilities via named pipe communication.

## Architecture

The hook system consists of three main components:

1. **Pattern-based Function Hooking** (MinHook)
   - Hooks into Engine Simulator's internal functions using signature scanning
   - Intercepts ignition module, simulation process, and torque calculation functions
   - Reads engine state directly from memory

2. **Named Pipe Server**
   - Runs in a background thread within the injected DLL
   - Sends real-time RPM, max RPM, and torque data to the C# application (~100Hz)
   - Receives commands for throttle, starter, ignition, and dyno control

3. **Memory Reading/Writing**
   - Safe read/write operations with exception handling
   - Accesses engine instance pointers and properties
   - Applies throttle override when commanded

## Building

### Prerequisites
- Visual Studio 2022 with C++ workload
- Windows 10/11 SDK

### Build Steps
1. Open `BetterES.sln` in Visual Studio
2. Set `BetterESHook` as the startup project
3. Build in Debug or Release configuration (x64 only)

Or build from command line:
```bash
msbuild BetterESHook\BetterESHook.vcxproj /p:Configuration=Release /p:Platform=x64
```

The output DLL will be placed in `bin\Release\betteres_hook.dll`.

## Hooked Functions

The following Engine Simulator functions are hooked:

- **Ignition Module**: Reads current RPM from crankshaft velocity and max RPM (redline)
- **SimProcess**: Captures app/simulator/engine instance pointers and applies throttle override
- **UpdateHpAndTorque**: Reads torque output in lb·ft

## Pipe Protocol

Communication uses a binary message protocol over a named pipe (`\\.\pipe\better-es-pipe`).

### Messages from Hook → Client
- `MSG_RPM_UPDATE` (0x01): Current RPM (double)
- `MSG_MAX_RPM` (0x02): Engine redline (double)
- `MSG_TORQUE_UPDATE` (0x03): Torque in lb·ft (double)

### Messages from Client → Hook
- `MSG_CMD_THROTTLE` (0x10): Set throttle 0.0-1.0 (double)
- `MSG_CMD_STARTER` (0x11): Toggle starter motor (bool)
- `MSG_CMD_IGNITION` (0x12): Toggle ignition (bool)
- `MSG_CMD_DYNO` (0x13): Toggle dyno mode (bool)
- `MSG_CMD_KILL` (0x1F): Shutdown pipe server

## Integration with BetterES

The C# application (`BetterES`) uses `KeyboardBackend.cs` (which is just an old name from EngineSimAutoRecorder and actually have no relation to keyboard now) to:
1. Inject this DLL into the Engine Simulator process
2. Connect to the named pipe
3. Read RPM/torque data in a background thread
4. Send control commands based on user input

## Troubleshooting

- **"Pipe connection timed out"**: Check that the DLL loaded successfully (use Process Explorer to verify)
- **"No RPM data"**: Verify Engine Simulator version is compatible with the pattern signatures in `hooks.cpp`
