using NesEmu.Core;
using NesEmu.Core.Cartridge;

namespace NesEmu.Tests;

public sealed class PpuAccuracyTests
{
    [Fact]
    public void PaletteRamReadsUseSixBitStorageAndOpenBusUpperBits()
    {
        var romPath = CreateTestRom();
        try
        {
            var ppu = new Ppu2C02(CartridgeImage.Load(romPath));

            SetPpuAddress(ppu, 0x3F1F);
            ppu.CpuWrite(0x2007, 0xFF);

            SetPpuAddress(ppu, 0x3F1F);
            ppu.CpuWrite(0x2002, 0x00);
            Assert.Equal(0x3F, ppu.CpuRead(0x2007));

            SetPpuAddress(ppu, 0x3F1F);
            ppu.CpuWrite(0x2002, 0xFF);
            Assert.Equal(0xFF, ppu.CpuRead(0x2007));
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    [Fact]
    public void ReadingStatusAtVBlankStartSuppressesFlagAndNmi()
    {
        var romPath = CreateTestRom();
        try
        {
            var ppu = new Ppu2C02(CartridgeImage.Load(romPath));
            ppu.CpuWrite(0x2000, 0x80);

            AdvancePpuClocks(ppu, 341 + (241 * 341) + 1);

            var status = ppu.CpuRead(0x2002);
            Assert.Equal(0x00, status & 0x80);

            for (var i = 0; i < 4; i++)
            {
                AdvancePpuClocks(ppu, 3);
                Assert.False(ppu.ConsumeNmi());
            }

            AdvancePpuClocks(ppu, 24);
            Assert.Equal(0x00, ppu.CpuRead(0x2002) & 0x80);
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    [Fact]
    public void ReadingStatusOneTickBeforeVBlankSuppressesTheUpcomingNmiSample()
    {
        var romPath = CreateTestRom();
        try
        {
            var ppu = new Ppu2C02(CartridgeImage.Load(romPath));
            ppu.CpuWrite(0x2000, 0x80);

            AdvancePpuClocks(ppu, 341 + (241 * 341));

            Assert.True(ppu.IsNmiLineLowOnUpcomingCpuSample);

            var status = ppu.CpuRead(0x2002);
            Assert.Equal(0x00, status & 0x80);
            Assert.False(ppu.IsNmiLineLowOnUpcomingCpuSample);

            AdvancePpuClocks(ppu, 6);
            Assert.False(ppu.ConsumeNmi());
            Assert.Equal(0x00, ppu.CpuRead(0x2002) & 0x80);
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    [Fact]
    public void SpriteZeroHitUsesTheCorrectBackgroundPixelAlignment()
    {
        var romPath = CreateAlignmentTestRom();
        try
        {
            var missPpu = new Ppu2C02(CartridgeImage.Load(romPath));
            SetPpuAddress(missPpu, 0x2002);
            missPpu.CpuWrite(0x2007, 0x01);
            ResetScroll(missPpu);
            missPpu.Oam[0] = 0x02;
            missPpu.Oam[1] = 0x00;
            missPpu.Oam[2] = 0x00;
            missPpu.Oam[3] = 0x13;
            missPpu.CpuWrite(0x2001, 0x18);

            AdvancePpuClocks(missPpu, 341 * 10);

            Assert.Equal(0x00, missPpu.CpuRead(0x2002) & 0x40);

            var hitPpu = new Ppu2C02(CartridgeImage.Load(romPath));
            SetPpuAddress(hitPpu, 0x2002);
            hitPpu.CpuWrite(0x2007, 0x01);
            ResetScroll(hitPpu);
            hitPpu.Oam[0] = 0x02;
            hitPpu.Oam[1] = 0x00;
            hitPpu.Oam[2] = 0x00;
            hitPpu.Oam[3] = 0x14;
            hitPpu.CpuWrite(0x2001, 0x18);

            AdvancePpuClocks(hitPpu, 341 * 10);

            Assert.Equal(0x40, hitPpu.CpuRead(0x2002) & 0x40);
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    [Fact]
    public void MisalignedOamCanTreatTheFirstEvaluatedSpriteAsSpriteZero()
    {
        var romPath = CreateAlignmentTestRom();
        try
        {
            var ppu = new Ppu2C02(CartridgeImage.Load(romPath));
            Array.Fill(ppu.Oam, (byte)0xFF);

            SetPpuAddress(ppu, 0x2002);
            ppu.CpuWrite(0x2007, 0x01);
            ResetScroll(ppu);

            ppu.Oam[0x81] = 0x00;
            ppu.Oam[0x82] = 0x00;
            ppu.Oam[0x83] = 0x00;
            ppu.Oam[0x84] = 0x14;

            ppu.CpuWrite(0x2001, 0x18);
            AdvancePpuClocks(ppu, 341);
            ppu.CpuWrite(0x2003, 0x81);
            AdvancePpuClocks(ppu, 341 * 5);

            Assert.Equal(0x40, ppu.CpuRead(0x2002) & 0x40);
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    [Fact]
    public void MisalignedOamOffsetTwoCanStillTriggerSpriteOverflowAfterRealignment()
    {
        var romPath = CreateTestRom();
        try
        {
            var ppu = new Ppu2C02(CartridgeImage.Load(romPath));
            Array.Fill(ppu.Oam, (byte)0xFF);

            byte[] misalignedOffsetTwo =
            {
                0xFF, 0xFF,
                0x00, 0xE3, 0x10, 0x00,
                0x00, 0xE3, 0x20, 0x00,
                0x00, 0xE3, 0x30, 0x00,
                0x00, 0xE3, 0x40, 0x00,
                0x00, 0xE3, 0x50, 0x00,
                0x00, 0xE3, 0x60, 0x00,
                0x00, 0xE3, 0x70, 0x00,
                0x00, 0xE3, 0x00, 0x80,
                0x00
            };

            Array.Copy(misalignedOffsetTwo, ppu.Oam, misalignedOffsetTwo.Length);

            ppu.CpuWrite(0x2001, 0x18);
            AdvancePpuClocks(ppu, 341);
            ppu.CpuWrite(0x2003, 0x02);
            AdvancePpuClocks(ppu, 341 * 3);

            Assert.Equal(0x20, ppu.CpuRead(0x2002) & 0x20);
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    [Fact]
    public void EightSpritesOnOneScanlineDoNotSetOverflow()
    {
        var romPath = CreateTestRom();
        try
        {
            var ppu = new Ppu2C02(CartridgeImage.Load(romPath));
            FillOam(ppu, 0xFF);
            for (var i = 0; i < 8; i++)
            {
                var baseIndex = i * 4;
                ppu.Oam[baseIndex] = 0x00;
                ppu.Oam[baseIndex + 1] = 0x00;
                ppu.Oam[baseIndex + 2] = 0x00;
                ppu.Oam[baseIndex + 3] = (byte)(i * 8);
            }

            ppu.CpuWrite(0x2001, 0x18);
            AdvancePpuClocks(ppu, 341 * 4);

            Assert.Equal(0x00, ppu.CpuRead(0x2002) & 0x20);
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    [Fact]
    public void SpriteOverflowClearsOnTheNextFrameWhenOnlyEightSpritesRemain()
    {
        var romPath = CreateTestRom();
        try
        {
            var ppu = new Ppu2C02(CartridgeImage.Load(romPath));
            FillOam(ppu, 0xFF);
            for (var i = 0; i < 9; i++)
            {
                var baseIndex = i * 4;
                ppu.Oam[baseIndex] = 0x00;
                ppu.Oam[baseIndex + 1] = 0x00;
                ppu.Oam[baseIndex + 2] = 0x00;
                ppu.Oam[baseIndex + 3] = (byte)(i * 8);
            }

            ppu.CpuWrite(0x2001, 0x18);
            AdvancePpuClocks(ppu, 341 * 4);
            Assert.Equal(0x20, ppu.CpuRead(0x2002) & 0x20);

            ppu.Oam[0] = 0xFF;
            AdvancePpuClocks(ppu, 341 * 262);

            Assert.Equal(0x00, ppu.CpuRead(0x2002) & 0x20);
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    [Fact]
    public void BackgroundPaletteSelectionRemainsStableAcrossTheScanline()
    {
        var romPath = CreateSolidBackgroundTestRom();
        try
        {
            var ppu = new Ppu2C02(CartridgeImage.Load(romPath));

            SetPpuAddress(ppu, 0x3F00);
            ppu.CpuWrite(0x2007, 0x00);
            ppu.CpuWrite(0x2007, 0x01);

            SetPpuAddress(ppu, 0x3F09);
            ppu.CpuWrite(0x2007, 0x21);

            ResetScroll(ppu);
            ppu.CpuWrite(0x2001, 0x0A);
            AdvancePpuClocks(ppu, 341 * 2);

            var frame = ppu.FrameBuffer;
            var reference = frame[64];

            Assert.Equal(reference, frame[8]);
            Assert.Equal(reference, frame[128]);
            Assert.Equal(reference, frame[248]);
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    [Fact]
    public void DummySpriteFetchesStillClockMmc3Irq()
    {
        var romPath = CreateMmc3TestRom();
        try
        {
            var cartridge = CartridgeImage.Load(romPath);
            var ppu = new Ppu2C02(cartridge);

            cartridge.CpuWrite(0xC000, 0x00);
            cartridge.CpuWrite(0xC001, 0x00);
            cartridge.CpuWrite(0xE001, 0x00);

            ppu.CpuWrite(0x2000, 0x08);
            ppu.CpuWrite(0x2001, 0x10);

            AdvancePpuClocks(ppu, 341);

            Assert.True(cartridge.IrqPending);
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    private static void AdvancePpuClocks(Ppu2C02 ppu, int count)
    {
        for (var i = 0; i < count; i++)
        {
            ppu.Clock();
        }
    }

    private static void FillOam(Ppu2C02 ppu, byte value)
    {
        Array.Fill(ppu.Oam, value);
    }

    private static void SetPpuAddress(Ppu2C02 ppu, ushort address)
    {
        ppu.CpuWrite(0x2006, (byte)(address >> 8));
        ppu.CpuWrite(0x2006, (byte)address);
    }

    private static void ResetScroll(Ppu2C02 ppu)
    {
        SetPpuAddress(ppu, 0x2000);
        ppu.CpuWrite(0x2005, 0x00);
        ppu.CpuWrite(0x2005, 0x00);
    }

    private static string CreateTestRom()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nesemu-ppu-accuracy-{Guid.NewGuid():N}.nes");
        using var stream = File.Create(path);

        Span<byte> header = stackalloc byte[16];
        header[0] = (byte)'N';
        header[1] = (byte)'E';
        header[2] = (byte)'S';
        header[3] = 0x1A;
        header[4] = 1;
        header[5] = 1;
        stream.Write(header);

        stream.Write(new byte[0x4000]);

        var chr = new byte[0x2000];
        chr[0] = 0x3C;
        stream.Write(chr);

        return path;
    }

    private static string CreateAlignmentTestRom()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nesemu-ppu-alignment-{Guid.NewGuid():N}.nes");
        using var stream = File.Create(path);

        Span<byte> header = stackalloc byte[16];
        header[0] = (byte)'N';
        header[1] = (byte)'E';
        header[2] = (byte)'S';
        header[3] = 0x1A;
        header[4] = 1;
        header[5] = 1;
        stream.Write(header);

        stream.Write(new byte[0x4000]);

        var chr = new byte[0x2000];
        chr[0x00] = 0x80;
        chr[0x08] = 0x80;
        for (var row = 0; row < 8; row++)
        {
            chr[0x10 + row] = 0xFF;
            chr[0x18 + row] = 0xFF;
        }

        chr[0x10 + 3] = 0xEF;
        chr[0x18 + 3] = 0xEF;
        stream.Write(chr);

        return path;
    }

    private static string CreateSolidBackgroundTestRom()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nesemu-ppu-solid-bg-{Guid.NewGuid():N}.nes");
        using var stream = File.Create(path);

        Span<byte> header = stackalloc byte[16];
        header[0] = (byte)'N';
        header[1] = (byte)'E';
        header[2] = (byte)'S';
        header[3] = 0x1A;
        header[4] = 1;
        header[5] = 1;
        stream.Write(header);

        stream.Write(new byte[0x4000]);

        var chr = new byte[0x2000];
        for (var row = 0; row < 8; row++)
        {
            chr[row] = 0xFF;
        }

        stream.Write(chr);
        return path;
    }

    private static string CreateMmc3TestRom()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nesemu-ppu-mmc3-{Guid.NewGuid():N}.nes");
        using var stream = File.Create(path);

        Span<byte> header = stackalloc byte[16];
        header[0] = (byte)'N';
        header[1] = (byte)'E';
        header[2] = (byte)'S';
        header[3] = 0x1A;
        header[4] = 2;
        header[5] = 1;
        header[6] = 0x40;
        stream.Write(header);

        stream.Write(new byte[0x8000]);
        stream.Write(new byte[0x2000]);

        return path;
    }
}
