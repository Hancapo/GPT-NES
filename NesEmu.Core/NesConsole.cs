using System.Collections.Generic;
using NesEmu.Core.Cartridge;

namespace NesEmu.Core;

public sealed class NesConsole : ICpuBus, IDisposable
{
    public const double CpuFrequency = 1_789_773.0;
    public const int AudioSampleRate = 44_100;

    private readonly CartridgeImage _cartridge;
    private readonly Cpu6502 _cpu;
    private readonly Ppu2C02 _ppu;
    private readonly Apu2A03 _apu;
    private readonly byte[] _cpuRam = new byte[0x800];
    private readonly ControllerPort _controller1 = new();
    private readonly ControllerPort _controller2 = new();
    private readonly object _audioLock = new();
    private readonly Queue<float> _audioSamples = new();

    private long _cpuCycles;
    private int _dmaStallCycles;
    private double _sampleAccumulator;
    private double _sampleValueAccumulator;
    private int _sampleValueCount;

    public NesConsole(CartridgeImage cartridge)
    {
        _cartridge = cartridge;
        _ppu = new Ppu2C02(cartridge);
        _apu = new Apu2A03(AudioSampleRate);
        _cpu = new Cpu6502(this);
        Reset();
    }

    public ReadOnlySpan<uint> FrameBuffer => _ppu.FrameBuffer;

    public ApuTapSnapshot CaptureApuTapSnapshot() => _apu.CaptureTapSnapshot();

    public void Reset()
    {
        Array.Clear(_cpuRam);
        _ppu.Reset();
        _apu.Reset();
        _cpu.Reset();
        _cpuCycles = 0;
        _dmaStallCycles = 0;
        _sampleAccumulator = 0;
        _sampleValueAccumulator = 0;
        _sampleValueCount = 0;
        lock (_audioLock)
        {
            _audioSamples.Clear();
        }
    }

    public void SetControllerState(int port, ControllerState state)
    {
        switch (port)
        {
            case 0:
                _controller1.State = state;
                break;
            case 1:
                _controller2.State = state;
                break;
        }
    }

    public void RunFrame()
    {
        while (true)
        {
            Step();
            if (_ppu.ConsumeFrameCompleted())
            {
                break;
            }
        }
    }

    public int DrainAudioSamples(Span<float> destination)
    {
        lock (_audioLock)
        {
            var count = Math.Min(destination.Length, _audioSamples.Count);
            for (var i = 0; i < count; i++)
            {
                destination[i] = _audioSamples.Dequeue();
            }

            return count;
        }
    }

    public void Step()
    {
        if (_dmaStallCycles > 0)
        {
            TickHardware();
            _dmaStallCycles--;
            return;
        }

        var cycles = _cpu.Step();
        for (var i = 0; i < cycles; i++)
        {
            TickHardware();
        }
    }

    public byte CpuRead(ushort address)
    {
        address &= 0xFFFF;

        return address switch
        {
            <= 0x1FFF => _cpuRam[address & 0x07FF],
            <= 0x3FFF => _ppu.CpuRead((ushort)(0x2000 | (address & 0x0007))),
            0x4015 => _apu.ReadStatus(),
            0x4016 => _controller1.Read(),
            0x4017 => _controller2.Read(),
            >= 0x6000 => _cartridge.CpuRead(address),
            _ => 0
        };
    }

    public void CpuWrite(ushort address, byte value)
    {
        address &= 0xFFFF;

        switch (address)
        {
            case <= 0x1FFF:
                _cpuRam[address & 0x07FF] = value;
                break;
            case <= 0x3FFF:
                _ppu.CpuWrite((ushort)(0x2000 | (address & 0x0007)), value);
                break;
            case >= 0x4000 and <= 0x4013:
            case 0x4015:
            case 0x4017:
                _apu.WriteRegister(address, value);
                break;
            case 0x4014:
                StartOamDma(value);
                break;
            case 0x4016:
                _controller1.Write(value);
                _controller2.Write(value);
                break;
            default:
                if (address >= 0x6000)
                {
                    _cartridge.CpuWrite(address, value);
                }

                break;
        }
    }

    public void Dispose()
    {
        _cartridge.SaveBatteryBackedRam();
    }

    private void StartOamDma(byte page)
    {
        Span<byte> buffer = stackalloc byte[256];
        var baseAddress = (ushort)(page << 8);
        for (var i = 0; i < 256; i++)
        {
            buffer[i] = CpuRead((ushort)(baseAddress + i));
        }

        _ppu.WriteOamDma(buffer);
        _dmaStallCycles = 513 + (int)(_cpuCycles & 0x01);
    }

    private void TickHardware()
    {
        _cpuCycles++;
        _apu.Clock();
        _sampleValueAccumulator += _apu.GetCurrentRawSample();
        _sampleValueCount++;

        _sampleAccumulator += AudioSampleRate;
        while (_sampleAccumulator >= CpuFrequency)
        {
            _sampleAccumulator -= CpuFrequency;
            var averagedSample = _sampleValueCount == 0
                ? 0.0f
                : (float)(_sampleValueAccumulator / _sampleValueCount);

            lock (_audioLock)
            {
                _audioSamples.Enqueue(_apu.ProcessSample(averagedSample));
                while (_audioSamples.Count > AudioSampleRate)
                {
                    _audioSamples.Dequeue();
                }
            }

            _sampleValueAccumulator = 0;
            _sampleValueCount = 0;
        }

        _ppu.Clock();
        _ppu.Clock();
        _ppu.Clock();

        if (_ppu.ConsumeNmi())
        {
            _cpu.RequestNmi();
        }

        _cpu.SetIrqLine(_apu.IrqPending);
    }
}
