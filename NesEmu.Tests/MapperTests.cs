using NesEmu.Core;
using NesEmu.Core.Cartridge;

namespace NesEmu.Tests;

public sealed class MapperTests
{
    [Theory]
    [InlineData(3, 2, 4, false)]
    [InlineData(4, 8, 8, false)]
    [InlineData(7, 4, 0, false)]
    [InlineData(9, 8, 16, false)]
    [InlineData(10, 8, 16, false)]
    [InlineData(11, 4, 8, false)]
    [InlineData(13, 2, 0, false)]
    [InlineData(34, 4, 1, false)]
    [InlineData(66, 4, 4, false)]
    [InlineData(71, 8, 0, false)]
    [InlineData(94, 8, 0, false)]
    [InlineData(140, 4, 8, false)]
    [InlineData(180, 8, 0, false)]
    [InlineData(4, 8, 8, true)]
    public void NewlySupportedMapperHeaders_LoadWithoutThrowing(int mapperId, int prgBanks, int chrBanks, bool fourScreen)
    {
        var romPath = CreateTestRom(mapperId, prgBanks, chrBanks, fourScreen);
        try
        {
            var cartridge = CartridgeImage.Load(romPath);
            Assert.NotNull(cartridge);
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    [Fact]
    public void Mapper003_SwitchesChrBank()
    {
        var mapper = new Mapper003(CreatePrg32Banks(1), CreateChr8Banks(4), CreatePrgRam(), MirroringMode.Horizontal, chrWritable: false);

        Assert.Equal(0x00, mapper.PpuRead(0x0000));

        mapper.CpuWrite(0x8000, 2);

        Assert.Equal(0x02, mapper.PpuRead(0x0000));
    }

    [Fact]
    public void Mapper004_SwitchesPrgBanks_AndRaisesIrq()
    {
        var mapper = new Mapper004(CreatePrg8Banks(8), CreateChr1Banks(8), CreatePrgRam(), MirroringMode.Horizontal, chrWritable: false);
        mapper.Reset();

        mapper.CpuWrite(0x8000, 0x06);
        mapper.CpuWrite(0x8001, 0x03);
        mapper.CpuWrite(0x8000, 0x07);
        mapper.CpuWrite(0x8001, 0x04);

        Assert.Equal(0x03, mapper.CpuRead(0x8000));
        Assert.Equal(0x04, mapper.CpuRead(0xA000));
        Assert.Equal(0x06, mapper.CpuRead(0xC000));
        Assert.Equal(0x07, mapper.CpuRead(0xE000));

        mapper.CpuWrite(0xC000, 0x01);
        mapper.CpuWrite(0xC001, 0x00);
        mapper.CpuWrite(0xE001, 0x00);

        ClockMmc3Scanline(mapper);
        Assert.False(mapper.IrqPending);

        ClockMmc3Scanline(mapper);
        Assert.True(mapper.IrqPending);
    }

    [Fact]
    public void Mapper004_FourScreenIgnoresMirroringWrites()
    {
        var mapper = new Mapper004(CreatePrg8Banks(8), CreateChr1Banks(8), CreatePrgRam(), MirroringMode.FourScreen, chrWritable: false);

        mapper.CpuWrite(0xA000, 0x01);

        Assert.Equal(MirroringMode.FourScreen, mapper.Mirroring);
    }

    [Fact]
    public void Mapper004_MirroringWritesUseMmc3Semantics()
    {
        var mapper = new Mapper004(CreatePrg8Banks(8), CreateChr1Banks(8), CreatePrgRam(), MirroringMode.Horizontal, chrWritable: false);

        mapper.CpuWrite(0xA000, 0x00);
        Assert.Equal(MirroringMode.Vertical, mapper.Mirroring);

        mapper.CpuWrite(0xA000, 0x01);
        Assert.Equal(MirroringMode.Horizontal, mapper.Mirroring);
    }

    [Fact]
    public void Mapper004_RequiresThreeCpuClockEdgesWithA12LowBeforeReclockingIrq()
    {
        var mapper = new Mapper004(CreatePrg8Banks(8), CreateChr1Banks(8), CreatePrgRam(), MirroringMode.Horizontal, chrWritable: false);
        mapper.Reset();

        mapper.OnPpuAddressAccess(0x1000, 0);
        mapper.OnCpuClock();

        mapper.CpuWrite(0xC000, 0x00);
        mapper.CpuWrite(0xC001, 0x00);
        mapper.CpuWrite(0xE001, 0x00);

        mapper.OnPpuAddressAccess(0x0FFF, 1);
        mapper.OnCpuClock();
        mapper.OnCpuClock();
        mapper.OnPpuAddressAccess(0x1000, 6);
        Assert.False(mapper.IrqPending);

        mapper.OnPpuAddressAccess(0x0FFF, 12);
        mapper.OnCpuClock();
        mapper.OnCpuClock();
        mapper.OnCpuClock();
        mapper.OnPpuAddressAccess(0x1000, 20);
        Assert.True(mapper.IrqPending);
    }

    [Fact]
    public void Mapper009_LatchesChrBanksAfterTriggerReads()
    {
        var mapper = new Mapper009(CreatePrg8Banks(8), CreateChr4Banks(8), CreatePrgRam(), MirroringMode.Vertical, chrWritable: false);
        mapper.Reset();
        mapper.CpuWrite(0xB000, 1);
        mapper.CpuWrite(0xC000, 2);
        mapper.CpuWrite(0xD000, 3);
        mapper.CpuWrite(0xE000, 4);

        Assert.Equal(0x01, mapper.PpuRead(0x0000));

        mapper.OnPpuAddressAccess(0x0FE8);
        Assert.Equal(0x02, mapper.PpuRead(0x0000));

        mapper.OnPpuAddressAccess(0x1FE8);
        Assert.Equal(0x04, mapper.PpuRead(0x1000));
    }

    [Fact]
    public void Mapper010_SwitchesPrgAndChrBanks()
    {
        var mapper = new Mapper010(CreatePrg16Banks(4), CreateChr4Banks(8), CreatePrgRam(), MirroringMode.Vertical, chrWritable: false);
        mapper.Reset();
        mapper.CpuWrite(0xA000, 1);
        mapper.CpuWrite(0xB000, 2);
        mapper.CpuWrite(0xC000, 3);

        Assert.Equal(0x01, mapper.CpuRead(0x8000));
        Assert.Equal(0x02, mapper.PpuRead(0x0000));

        mapper.OnPpuAddressAccess(0x0FE8);
        Assert.Equal(0x03, mapper.PpuRead(0x0000));
    }

    [Fact]
    public void Mapper011_UsesSharedRegisterForPrgAndChrBanks()
    {
        var mapper = new Mapper011(CreateConflictSafePrg32Banks(4), CreateChr8Banks(8), CreatePrgRam(), MirroringMode.Horizontal, chrWritable: false);

        mapper.CpuWrite(0x8000, 0x32);

        Assert.Equal(0x02, mapper.CpuRead(0x8000));
        Assert.Equal(0x03, mapper.PpuRead(0x0000));
    }

    [Fact]
    public void Mapper013_SwitchesUpperChrRamWindow()
    {
        var mapper = new Mapper013(CreateConflictSafePrg32Banks(1), new byte[0x4000], CreatePrgRam(), MirroringMode.Horizontal, chrWritable: true);

        mapper.CpuWrite(0x8000, 2);
        mapper.PpuWrite(0x1000, 0x5A);

        mapper.CpuWrite(0x8000, 1);
        Assert.Equal(0x00, mapper.PpuRead(0x1000));

        mapper.CpuWrite(0x8000, 2);
        Assert.Equal(0x5A, mapper.PpuRead(0x1000));
    }

    [Fact]
    public void Mapper034_SupportsBnromAndNinaBanking()
    {
        var bnrom = new Mapper034(CreateConflictSafePrg32Banks(4), CreateChr8Banks(1), CreatePrgRam(), MirroringMode.Horizontal, chrWritable: false);
        bnrom.CpuWrite(0x8000, 2);
        Assert.Equal(0x02, bnrom.CpuRead(0x8000));

        var nina = new Mapper034(CreatePrg32Banks(4), CreateChr4Banks(8), CreatePrgRam(), MirroringMode.Vertical, chrWritable: false);
        nina.CpuWrite(0x7FFD, 1);
        nina.CpuWrite(0x7FFE, 3);
        nina.CpuWrite(0x7FFF, 4);

        Assert.Equal(0x01, nina.CpuRead(0x8000));
        Assert.Equal(0x03, nina.PpuRead(0x0000));
        Assert.Equal(0x04, nina.PpuRead(0x1000));
    }

    [Fact]
    public void Mapper066_SwitchesPrgAndChrBanks()
    {
        var mapper = new Mapper066(CreateConflictSafePrg32Banks(4), CreateChr8Banks(4), CreatePrgRam(), MirroringMode.Horizontal, chrWritable: false);

        mapper.CpuWrite(0x8000, 0x21);

        Assert.Equal(0x02, mapper.CpuRead(0x8000));
        Assert.Equal(0x01, mapper.PpuRead(0x0000));
    }

    [Fact]
    public void Mapper071_SwitchesPrgBankAndSingleScreenMirroring()
    {
        var mapper = new Mapper071(CreateConflictSafePrg16Banks(8), CreateChr8Banks(1), CreatePrgRam(), chrWritable: false);

        mapper.CpuWrite(0x8000, 0x10);
        mapper.CpuWrite(0xC000, 3);

        Assert.Equal(MirroringMode.SingleScreenUpper, mapper.Mirroring);
        Assert.Equal(0x03, mapper.CpuRead(0x8000));
    }

    [Fact]
    public void Mapper094_SelectsBankFromUpperBits()
    {
        var mapper = new Mapper094(CreateConflictSafePrg16Banks(8), CreateChr8Banks(1), CreatePrgRam(), MirroringMode.Horizontal, chrWritable: false);

        mapper.CpuWrite(0x8000, 0x14);

        Assert.Equal(0x05, mapper.CpuRead(0x8000));
    }

    [Fact]
    public void Mapper140_UsesRegisterIn6000Range()
    {
        var mapper = new Mapper140(CreatePrg32Banks(4), CreateChr8Banks(8), CreatePrgRam(), MirroringMode.Horizontal, chrWritable: false);

        mapper.CpuWrite(0x6000, 0x27);

        Assert.Equal(0x02, mapper.CpuRead(0x8000));
        Assert.Equal(0x07, mapper.PpuRead(0x0000));
    }

    [Fact]
    public void Mapper180_SwitchesUpperPrgBank()
    {
        var mapper = new Mapper180(CreatePrg16Banks(8), CreateChr8Banks(1), CreatePrgRam(), MirroringMode.Vertical, chrWritable: false);

        mapper.CpuWrite(0x8000, 3);

        Assert.Equal(0x00, mapper.CpuRead(0x8000));
        Assert.Equal(0x03, mapper.CpuRead(0xC000));
    }

    [Fact]
    public void Ppu_FourScreenMirroringKeepsNametablesIndependent()
    {
        var romPath = CreateTestRom(mapperId: 4, prgBanks: 8, chrBanks: 8, fourScreen: true);
        try
        {
            var cartridge = CartridgeImage.Load(romPath);
            var ppu = new Ppu2C02(cartridge);

            WriteVram(ppu, 0x2000, 0x11);
            WriteVram(ppu, 0x2400, 0x22);
            WriteVram(ppu, 0x2800, 0x33);
            WriteVram(ppu, 0x2C00, 0x44);

            Assert.Equal(0x11, ReadVram(ppu, 0x2000));
            Assert.Equal(0x22, ReadVram(ppu, 0x2400));
            Assert.Equal(0x33, ReadVram(ppu, 0x2800));
            Assert.Equal(0x44, ReadVram(ppu, 0x2C00));
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    [Fact]
    public void Ppu_Mapper004MirroringWriteSelectsDistinctLeftAndRightNametables()
    {
        var romPath = CreateTestRom(mapperId: 4, prgBanks: 8, chrBanks: 8, fourScreen: false);
        try
        {
            var cartridge = CartridgeImage.Load(romPath);
            var ppu = new Ppu2C02(cartridge);

            cartridge.CpuWrite(0xA000, 0x00);

            WriteVram(ppu, 0x2000, 0x11);
            WriteVram(ppu, 0x2400, 0x22);
            WriteVram(ppu, 0x2800, 0x33);
            WriteVram(ppu, 0x2C00, 0x44);

            Assert.Equal(0x33, ReadVram(ppu, 0x2000));
            Assert.Equal(0x44, ReadVram(ppu, 0x2400));
            Assert.Equal(0x33, ReadVram(ppu, 0x2800));
            Assert.Equal(0x44, ReadVram(ppu, 0x2C00));
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    private static void ClockMmc3Scanline(Mapper004 mapper)
    {
        mapper.OnPpuAddressAccess(0x0FFF, 0);
        mapper.OnCpuClock();
        mapper.OnCpuClock();
        mapper.OnCpuClock();
        mapper.OnPpuAddressAccess(0x1000, 12);
    }

    private static void WriteVram(Ppu2C02 ppu, ushort address, byte value)
    {
        ppu.CpuWrite(0x2006, (byte)(address >> 8));
        ppu.CpuWrite(0x2006, (byte)address);
        ppu.CpuWrite(0x2007, value);
    }

    private static byte ReadVram(Ppu2C02 ppu, ushort address)
    {
        ppu.CpuWrite(0x2006, (byte)(address >> 8));
        ppu.CpuWrite(0x2006, (byte)address);
        _ = ppu.CpuRead(0x2007);
        ppu.CpuWrite(0x2006, (byte)(address >> 8));
        ppu.CpuWrite(0x2006, (byte)address);
        return ppu.CpuRead(0x2007);
    }

    private static byte[] CreatePrgRam() => new byte[0x2000];

    private static byte[] CreatePrg8Banks(int bankCount) => CreateBankedBytes(bankCount, 0x2000);

    private static byte[] CreatePrg16Banks(int bankCount) => CreateBankedBytes(bankCount, 0x4000);

    private static byte[] CreatePrg32Banks(int bankCount) => CreateBankedBytes(bankCount, 0x8000);

    private static byte[] CreateConflictSafePrg16Banks(int bankCount) => CreateBankedBytes(bankCount, 0x4000, firstBankFill: 0xFF);

    private static byte[] CreateConflictSafePrg32Banks(int bankCount) => CreateBankedBytes(bankCount, 0x8000, firstBankFill: 0xFF);

    private static byte[] CreateChr1Banks(int bankCount) => CreateBankedBytes(bankCount, 0x0400);

    private static byte[] CreateChr4Banks(int bankCount) => CreateBankedBytes(bankCount, 0x1000);

    private static byte[] CreateChr8Banks(int bankCount) => CreateBankedBytes(bankCount, 0x2000);

    private static byte[] CreateBankedBytes(int bankCount, int bankSize, byte? firstBankFill = null)
    {
        var data = new byte[bankCount * bankSize];
        for (var bank = 0; bank < bankCount; bank++)
        {
            var fill = bank == 0 && firstBankFill.HasValue ? firstBankFill.Value : (byte)bank;
            Array.Fill(data, fill, bank * bankSize, bankSize);
        }

        return data;
    }

    private static string CreateTestRom(int mapperId, int prgBanks, int chrBanks, bool fourScreen)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nesemu-mapper-test-{Guid.NewGuid():N}.nes");
        using var stream = File.Create(path);

        Span<byte> header = stackalloc byte[16];
        header[0] = (byte)'N';
        header[1] = (byte)'E';
        header[2] = (byte)'S';
        header[3] = 0x1A;
        header[4] = (byte)prgBanks;
        header[5] = (byte)chrBanks;
        header[6] = (byte)(((mapperId & 0x0F) << 4) | (fourScreen ? 0x08 : 0x00));
        header[7] = (byte)(mapperId & 0xF0);
        stream.Write(header);

        for (var bank = 0; bank < prgBanks; bank++)
        {
            stream.Write(Enumerable.Repeat((byte)bank, 0x4000).ToArray());
        }

        for (var bank = 0; bank < chrBanks; bank++)
        {
            stream.Write(Enumerable.Repeat((byte)bank, 0x2000).ToArray());
        }

        return path;
    }
}
