# BetterES

Real-time engine monitoring and control for [Engine Simulator](https://www.enginesimulator.com/) via DLL injection.

## What It Does

BetterES injects a hook DLL into Engine Simulator to read engine data (RPM, torque) in real-time and send keyboard-based controls (throttle, ignition, starter, dyno). The hook communicates with the WPF app over a named pipe.

```
BetterES (C# WPF)  ◄══════ named pipe ════►  Engine Simulator (+ hook DLL)
  • UI / Gauges                                • RPM & torque reading
  • Engine controls                            • Memory function hooks
```

## Prerequisites

- Windows 10/11
- Visual Studio 2022 with **.NET Desktop Development** and **Desktop development with C++** workloads
- Engine Simulator installed

## Build & Run

```bash
git clone https://github.com/inconsistentPassion/Better-ES.git
cd Better-ES
dotnet build BetterES\BetterES.csproj -c Release
.\BetterES\bin\Release\BetterES.exe
```

Or open `BetterES.sln` in Visual Studio and press F5. The C++ hook DLL builds automatically via CMake — no manual steps needed.

## Usage

1. Launch Engine Simulator
2. Open BetterES
3. Select the Engine Simulator process from the list
4. Click **Connect** to inject the hook DLL
5. Monitor RPM and torque, use UI controls to operate the engine

## Project Structure

```
BetterES/
├── BetterES.sln
├── BetterES/                  # C# WPF application
│   ├── Backends/              # Injection + pipe communication
│   ├── Services/              # Engine data services
│   ├── View/                  # UI pages and gauges
│   └── ViewModel/             # MVVM view models
└── BetterESHook/              # C++ hook DLL (MinHook)
    ├── src/                   # Hook source code
    └── vendor/minhook/        # MinHook library
```

## Documentation

- [BUILD.md](BUILD.md) — detailed build instructions and troubleshooting
- [BetterESHook/README.md](BetterESHook/README.md) — hook DLL documentation

## Credits

Built on the injection system from [EngineSimAutoRecorder (ESAR)](https://github.com/nicoco007/EngineSimAutoRecorder). Uses [MinHook](https://github.com/TsudaKageyu/minhook), [WPF-UI](https://github.com/lepoco/wpfui), and [CommunityToolkit.MVVM](https://github.com/CommunityToolkit/dotnet).

## License

Same as the original EngineSimAutoRecorder project.
