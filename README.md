# NesEmu

NesEmu is a Nintendo Entertainment System emulator written in C# with a small testable core and an Avalonia desktop frontend. The repository is also used as a practical sandbox for model-assisted engineering on timing-sensitive software.

## Repository Layout

- `NesEmu.Core`: CPU, PPU, APU, cartridge loading, mapper implementations, controller ports, and audio generation.
- `NesEmu.App`: desktop UI for loading ROMs, controlling emulation, switching render/audio backends, remapping input, and routing APU activity to MIDI output.
- `NesEmu.Tests`: xUnit coverage for core behavior, mappers, audio, MIDI, ROM smoke tests, and selected UI-facing logic.
- `Samples/HelloWorld.nes`: sample ROM used by tests.
- `Tools/BuildHelloWorldRom.py`: helper script for rebuilding the sample ROM.

## Current Capabilities

- 6502 CPU emulation, including unofficial opcode handling.
- PPU and APU emulation with 44.1 kHz audio output.
- Supported mappers: `0`, `1`, `2`, `3`, `4`, `7`, `9`, `10`, `11`, `13`, `34`, `66`, `71`, `94`, `140`, and `180`.
- Battery-backed PRG RAM persisted as `.sav` files next to the ROM when the cartridge supports it.
- Keyboard and SDL gamepad input with remappable bindings.
- Software rendering plus an optional OpenGL presentation path.
- Optional MIDI conversion of APU channels with configurable instruments, levels, percussion routing, and sync offset.

## Requirements

- .NET 10 SDK
- Windows 10 or newer for the primary desktop workflow

Notes:

- All projects target `net10.0`.
- The codebase enables preview C# language features.
- Linux x64 publishing is available on a best-effort basis.

## Build, Run, Test

From the repository root:

```powershell
dotnet build NesEmu.slnx
dotnet run --project NesEmu.App/NesEmu.App.csproj
dotnet test NesEmu.Tests/NesEmu.Tests.csproj
```

Optional Linux x64 publish:

```powershell
dotnet publish NesEmu.App/NesEmu.App.csproj -c Release -r linux-x64 --self-contained true
```

## Using The App

1. Launch the application.
2. Open a `.nes` ROM through `File > Open ROM...`.
3. Use the `Emulation` menu to pause, resume, stop, or reset the current session.
4. Open `Settings` to switch video renderer, choose audio backend and latency, remap keyboard or controller input, or configure MIDI output.

Default keyboard mapping:

- `X`: NES `A`
- `Z`: NES `B`
- `Enter`: `Start`
- `Right Shift`: `Select`
- Arrow keys: D-pad

Useful shortcuts:

- `F11` or `Alt+Enter`: toggle fullscreen
- `Esc`: exit fullscreen

## Logs And Saves

- Logs are written under the platform local application-data directory, for example `%LocalAppData%\NesEmu\logs` on Windows.
- Battery-backed cartridges save to a `.sav` file beside the ROM.

## Legal Note

- `Samples/HelloWorld.nes` is included for testing.
- If you use external ROMs, make sure you have the rights to do so in your jurisdiction.
