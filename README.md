# NesEmu

NesEmu is a Nintendo Entertainment System emulator implemented in C# on top of a small, testable core and a desktop UI built with Avalonia.

This repository is not intended to teach how the NES works. It exists primarily as an experimental vehicle for evaluating the limits of frontier models when they are asked to perform demanding engineering work on a complex, stateful system such as an emulator. It also serves as a practical environment for learning how to use the OpenAI Codex desktop application in a real codebase, under conditions that require sustained debugging, refactoring, verification, and iteration.

In short, the emulator is the workload. The main objective of the project is not pedagogical accuracy as a learning resource, but the use of a nontrivial software artifact to probe model capability, workflow quality, and tool-assisted development.

## Project Scope

The repository currently contains:

- `NesEmu.Core`: the emulation core, including CPU, PPU, APU, cartridge loading, mappers, controller state handling, and audio/video sample generation.
- `NesEmu.App`: a desktop frontend for loading ROMs, running emulation, configuring audio, remapping controls, and optionally routing APU activity to MIDI output.
- `NesEmu.Tests`: automated tests covering core behavior and selected regression scenarios.
- `Samples`: small ROM assets used for testing and validation.

## Requirements

- Windows 10 or newer
- .NET 10 SDK

Notes:

- The application project targets `net10.0-windows10.0.19041.0`.
- The codebase currently uses preview language features.
- MIDI and controller support rely on Windows-specific APIs and libraries.

## Build

From the repository root:

```powershell
dotnet build NesEmu.App/NesEmu.App.csproj -c Release
```

To build the full testable stack:

```powershell
dotnet build NesEmu.Tests/NesEmu.Tests.csproj
```

## Run

To launch the desktop application:

```powershell
dotnet run --project NesEmu.App/NesEmu.App.csproj
```

You can also start the built executable directly after a Release build:

`NesEmu.App/bin/Release/net10.0-windows10.0.19041.0/NesEmu.App.exe`

## Test

To run the automated test suite:

```powershell
dotnet test NesEmu.Tests/NesEmu.Tests.csproj
```

## How To Use

1. Launch the application.
2. Open a `.nes` ROM through `File > Open ROM...` or the initial overlay button.
3. Use the transport controls to pause, stop, or reset the current session.
4. Open `Settings` to adjust audio latency and volume, remap keyboard or controller input, or enable MIDI output.

Default keyboard mapping:

- `X`: NES `A`
- `Z`: NES `B`
- `Enter`: `Start`
- `Right Shift`: `Select`
- Arrow keys: D-pad

The application also supports gamepad input and optional MIDI output routing from the APU state.

## Repository Intent

This project should be read as an engineering experiment, not as an instructional reference on NES architecture.

Its main value is in questions such as:

- How far can a frontier model go when it must reason about emulation timing, audio behavior, rendering correctness, and platform integration?
- How effectively can the Codex app support long-running, iterative debugging across multiple subsystems?
- What kinds of regressions, ambiguities, and hidden assumptions emerge when a model works on a codebase that is both technical and behavior-sensitive?

That framing matters. If the goal is to learn the NES as hardware, there are better dedicated resources. If the goal is to evaluate model-assisted software engineering on a demanding target, this repository is designed for exactly that purpose.

## Legal and Content Notes

- Bring your own ROMs unless you are using the sample assets already present in the repository.
- Ensure that any game ROMs you run are used in a manner consistent with the laws and rights applicable in your jurisdiction.

## Status

NesEmu is an active experimental project. Behavior, accuracy, UI, audio handling, and developer workflow may continue to change as the repository is used to test new ideas, debugging approaches, and Codex-driven development patterns.
