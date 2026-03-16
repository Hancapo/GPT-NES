namespace NesEmu.Core.Cartridge;

public sealed class CartridgeImage
{
    private readonly Mapper _mapper;
    private readonly byte[] _prgRam;

    private CartridgeImage(
        string romPath,
        byte[] prgRom,
        byte[] chrMemory,
        byte[] prgRam,
        bool hasBatteryBackedRam,
        Mapper mapper)
    {
        RomPath = romPath;
        PrgRom = prgRom;
        ChrMemory = chrMemory;
        _prgRam = prgRam;
        HasBatteryBackedRam = hasBatteryBackedRam;
        _mapper = mapper;

        if (HasBatteryBackedRam && File.Exists(SavePath))
        {
            var saveData = File.ReadAllBytes(SavePath);
            Array.Copy(saveData, _prgRam, Math.Min(saveData.Length, _prgRam.Length));
        }
    }

    public string RomPath { get; }

    public string SavePath => Path.ChangeExtension(RomPath, ".sav");

    public byte[] PrgRom { get; }

    public byte[] ChrMemory { get; }

    public bool HasBatteryBackedRam { get; }

    public MirroringMode Mirroring => _mapper.Mirroring;

    public bool SaveDirty { get; private set; }

    public static CartridgeImage Load(string romPath)
    {
        using var stream = File.OpenRead(romPath);
        Span<byte> header = stackalloc byte[16];
        stream.ReadExactly(header);

        if (header[0] != 'N' || header[1] != 'E' || header[2] != 'S' || header[3] != 0x1A)
        {
            throw new InvalidDataException("La ROM no tiene una cabecera iNES/NES 2.0 valida.");
        }

        var nes20 = (header[7] & 0x0C) == 0x08;
        var hasTrainer = (header[6] & 0x04) != 0;
        var hasBattery = (header[6] & 0x02) != 0;
        var fourScreen = (header[6] & 0x08) != 0;
        var mirroring = fourScreen
            ? MirroringMode.FourScreen
            : (header[6] & 0x01) != 0 ? MirroringMode.Vertical : MirroringMode.Horizontal;

        var mapperId = (header[6] >> 4) | (header[7] & 0xF0);
        if (nes20)
        {
            mapperId |= (header[8] & 0x0F) << 8;
        }

        var prgRomSize = nes20
            ? DecodeNes2RomSize(header[4], (byte)(header[9] & 0x0F), 0x4000)
            : header[4] * 0x4000;
        var chrRomSize = nes20
            ? DecodeNes2RomSize(header[5], (byte)(header[9] >> 4), 0x2000)
            : header[5] * 0x2000;

        var prgRamSize = nes20
            ? Math.Max(DecodeRamShift((byte)(header[10] >> 4)), DecodeRamShift((byte)(header[10] & 0x0F)))
            : hasBattery ? 0x2000 : 0x2000;
        var usesChrRam = chrRomSize == 0;
        var chrRamSize = usesChrRam
            ? (nes20 ? Math.Max(DecodeRamShift((byte)(header[11] >> 4)), DecodeRamShift((byte)(header[11] & 0x0F))) : 0x2000)
            : chrRomSize;
        if (usesChrRam && mapperId == 13)
        {
            chrRamSize = Math.Max(chrRamSize, 0x4000);
        }

        if (hasTrainer)
        {
            stream.Position += 512;
        }

        var prgRom = new byte[prgRomSize];
        stream.ReadExactly(prgRom);

        var chrMemory = new byte[Math.Max(0x2000, chrRamSize)];
        if (chrRomSize > 0)
        {
            stream.ReadExactly(chrMemory.AsSpan(0, chrRomSize));
        }

        var prgRam = new byte[Math.Max(0x2000, prgRamSize)];
        Mapper mapper = mapperId switch
        {
            0 => new Mapper000(prgRom, chrMemory, prgRam, mirroring, usesChrRam),
            1 => new Mapper001(prgRom, chrMemory, prgRam, usesChrRam),
            2 => new Mapper002(prgRom, chrMemory, prgRam, mirroring, usesChrRam),
            3 => new Mapper003(prgRom, chrMemory, prgRam, mirroring, usesChrRam),
            4 => new Mapper004(prgRom, chrMemory, prgRam, mirroring, usesChrRam),
            7 => new Mapper007(prgRom, chrMemory, prgRam, usesChrRam),
            9 => new Mapper009(prgRom, chrMemory, prgRam, mirroring, usesChrRam),
            10 => new Mapper010(prgRom, chrMemory, prgRam, mirroring, usesChrRam),
            11 => new Mapper011(prgRom, chrMemory, prgRam, mirroring, usesChrRam),
            13 => new Mapper013(prgRom, chrMemory, prgRam, mirroring, usesChrRam),
            34 => new Mapper034(prgRom, chrMemory, prgRam, mirroring, usesChrRam),
            66 => new Mapper066(prgRom, chrMemory, prgRam, mirroring, usesChrRam),
            71 => new Mapper071(prgRom, chrMemory, prgRam, usesChrRam),
            94 => new Mapper094(prgRom, chrMemory, prgRam, mirroring, usesChrRam),
            140 => new Mapper140(prgRom, chrMemory, prgRam, mirroring, usesChrRam),
            180 => new Mapper180(prgRom, chrMemory, prgRam, mirroring, usesChrRam),
            _ => throw new NotSupportedException($"Mapper {mapperId} no esta implementado.")
        };

        return new CartridgeImage(romPath, prgRom, chrMemory, prgRam, hasBattery, mapper);
    }

    public bool IrqPending => _mapper.IrqPending;

    public byte CpuRead(ushort address) => _mapper.CpuRead(address);

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and <= 0x7FFF && HasBatteryBackedRam)
        {
            SaveDirty = true;
        }

        _mapper.CpuWrite(address, value);
    }

    public byte PpuRead(ushort address) => _mapper.PpuRead(address);

    public void PpuWrite(ushort address, byte value) => _mapper.PpuWrite(address, value);

    public void OnPpuAddressAccess(ushort address, long ppuCycle = 0) => _mapper.OnPpuAddressAccess(address, ppuCycle);

    public void OnCpuClock() => _mapper.OnCpuClock();

    public void Reset() => _mapper.Reset();

    public void SaveBatteryBackedRam()
    {
        if (!HasBatteryBackedRam || !SaveDirty)
        {
            return;
        }

        File.WriteAllBytes(SavePath, _prgRam);
        SaveDirty = false;
    }

    private static int DecodeNes2RomSize(byte lsb, byte msb, int unitSize)
    {
        if (msb == 0x0F)
        {
            var exponent = lsb >> 2;
            var multiplier = (lsb & 0x03) * 2 + 1;
            return (1 << exponent) * multiplier;
        }

        return ((msb << 8) | lsb) * unitSize;
    }

    private static int DecodeRamShift(byte value) => value == 0 ? 0 : 64 << value;
}
