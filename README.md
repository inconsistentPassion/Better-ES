# BetterES 🏎️

Since I have forked from [ES-Studio](https://github.com/inconsistentPassion/ES-Studio) for [Engine Simulator](https://github.com/ange-yaghi/engine-sim) I have always wanted to make it better, so initially its called Better ES Studio, but Since its not as simple as ES-Studio, so it does not quite fit the name, Hence its name Better-ES meaning Better Engine SImulator.

BetterES is like ES-Studio, but it has a lot more features. Think of it a AIO tool for Engine Simulator — you get live gauges, tuning knobs, drag-strip timers, an engine builder, and even the ability to make your engine sing along with racing games like Assetto Corsa and BeamNG.drive.

## Features (some of it are still in testing phase, do not expect it functions well for now)

### 📊 Live Dashboard
Watch your engine in real-time with smooth, responsive gauges:
- **Tachometer** — RPM with redline marking
- **Torque & Power** — Live readings as you rev
- **Speedometer** — How fast you're going
- **Boost Gauge** — Manifold pressure and turbo boost
- **AFR Gauge** — Air-fuel ratio monitoring
- **Airflow Gauge** — How much air your engine is gulping
- **Temperature** — Engine operating temps
- **Gear & Clutch** — Current gear and clutch position

### 🎛️ Tuning & Timing
Take control of how your engine behaves without editing files:
- Adjust **spark advance** on the fly
- Set a **rev limiter** with custom cut time (great for launch control effects)
- Apply **ignition cut** — full kill or a soft partial cut
- Enable **fuel cut** or dial in a **custom AFR target**
- Override throttle and RPM directly for bench testing

### 🌉 Game Bridge (The Cool Part!)
Ever wanted your Engine Simulator engine to *actually* sound like it's powering your car in a racing game? BetterES bridges with:
- **Assetto Corsa**
- **BeamNG.drive**

Connect your game, enable Bridge Mode, and your ES engine will rev, shift, and boost in perfect sync with your in-game car. It's like giving your game a custom exhaust note that reacts to every input.

### 🌀 Functional Turbo
Build and tune a turbocharger with realistic physics:
- Set compressor maps, boost targets, and spool characteristics
- Watch boost build on the gauge as you lay into the throttle
- Works in standalone mode or tied to your game's boost readings

### 🏁 Drag Racing Tools
Run performance tests on your builds:
- **0-60 mph** times
- **Quarter mile** runs with trap speed
- See how your tuning changes affect real-world acceleration

### 🛠️ Engine Generator
Don't have an engine file handy? Build one from scratch right inside BetterES:
- Choose cylinders, displacement, bore, stroke, redline, and more
- Generate a ready-to-use `.mr` file and drop it straight into Engine Simulator

### 🎵 MIDI Player
Yes, really. Load a MIDI file and let your engine play it. Your cammed V8 can now perform Bach. It's as ridiculous and awesome as it sounds.

### 💡 Quality-of-Life Improvements over ES-Studio
- It no longer crashes the Engine Simulator when connecting / changing engines thanks to a much more sophisticated injection method.
- It has way less latency thanks to not relying on the ES-Sudio's method, Bit instead switching to a shared memory architecture. Which also increased how much data can be sent.


## How It Works (The Simple Version)

1. **Launch Engine Simulator** and load your favorite engine.
2. **Open BetterES** and click **Connect** — it'll find your running sim automatically.
3. **Use the tabs** to switch between Dashboard, Tuning, Turbo, Drag Strip, Generator, and more.
4. **(Optional)** Connect a racing game to hear your engine follow your driving in real-time.

That's it! No config files to wrestle with, no command lines — just connect and play.

## Getting Started

&gt; **Note:** You'll need Engine Simulator installed separately. BetterES is a companion app, not a replacement!

1. Download the latest release from the [Releases](../../releases) page
2. Extract and run `BetterES.exe`
3. Make sure Engine Simulator is running
4. Click **Connect** in BetterES
5. Have fun! 🚗💨

## A Quick Tour of the Pages

| Page | What It's For |
|------|---------------|
| **Home** | Your main dashboard with all the live gauges |
| **Tuning** | Timing controls, rev limiter, ignition cut |
| **Turbo** | Build and tune your turbo setup |
| **Drag** | Performance testing and timing runs |
| **Extras** | AFR targets, fuel cut, gear control |
| **Generator** | Create brand new engine files from scratch |
| **Modes** | Bridge setup for Assetto Corsa / BeamNG |
| **Timing** | Fine spark advance adjustments |
| **Logs** | See what's happening under the hood |

## Tips

- If your audio gets crackly when alt-tabbing, don't worry, its windows' fault not yours — minimizing Engine Simulator helps reduce the probability of cracking.

## Credits
- [Engine Simulator](https://github.com/ange-yaghi/engine-sim)
- [ES-Studio](https://github.com/RealIndica/ES-Studio)
- And people in Engine Sim's official discord server for ideas.

## License
See [LICENSE](LICENSE) for details.
