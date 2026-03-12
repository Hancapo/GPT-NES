using NesEmu.Core;
using NesEmu.Core.Cartridge;

namespace NesEmu.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void Mapper0_16KPrg_IsMirroredIntoUpperBank()
    {
        var romPath = CreateTestRom(mapperId: 0, prgBanks: 1, chrBanks: 1);
        try
        {
            var cartridge = CartridgeImage.Load(romPath);

            Assert.Equal(cartridge.CpuRead(0x8000), cartridge.CpuRead(0xC000));
            Assert.Equal(cartridge.CpuRead(0xBFFF), cartridge.CpuRead(0xFFFF));
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    [Fact]
    public void Mapper2_SwitchesLowerPrgBank_AndKeepsUpperFixed()
    {
        var romPath = CreateTestRom(mapperId: 2, prgBanks: 3, chrBanks: 1);
        try
        {
            var cartridge = CartridgeImage.Load(romPath);

            var fixedBefore = cartridge.CpuRead(0xC000);
            var switchBefore = cartridge.CpuRead(0x8000);

            cartridge.CpuWrite(0x8000, 1);

            var switchAfter = cartridge.CpuRead(0x8000);
            var fixedAfter = cartridge.CpuRead(0xC000);

            Assert.NotEqual(switchBefore, switchAfter);
            Assert.Equal(fixedBefore, fixedAfter);
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    [Fact]
    public void ZeldaRom_RunsMultipleFramesWithoutThrowing()
    {
        var romPath = FindRomPath();
        Assert.True(File.Exists(romPath), $"No se encontró la ROM en {romPath}");

        using var console = new NesConsole(CartridgeImage.Load(romPath));
        for (var frame = 0; frame < 180; frame++)
        {
            console.RunFrame();
        }

        Assert.Equal(NesVideoConstants.PixelsPerFrame, console.FrameBuffer.Length);
    }

    [Fact]
    public void ZeldaRom_AudioSamplesStayWithinNormalizedRange()
    {
        var romPath = FindRomPath();
        Assert.True(File.Exists(romPath), $"No se encontró la ROM en {romPath}");

        using var console = new NesConsole(CartridgeImage.Load(romPath));
        for (var frame = 0; frame < 180; frame++)
        {
            console.RunFrame();
        }

        var samples = new float[8192];
        var count = console.DrainAudioSamples(samples);

        Assert.True(count > 0, "No se generaron muestras de audio.");
        for (var i = 0; i < count; i++)
        {
            Assert.InRange(samples[i], -1.0f, 1.0f);
        }
    }

    private static string FindRomPath()
    {
        var roots = new[]
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory
        };

        foreach (var root in roots)
        {
            var directory = new DirectoryInfo(root);
            for (var depth = 0; directory is not null && depth < 6; depth++, directory = directory.Parent)
            {
                var rom = Directory.EnumerateFiles(directory.FullName, "*.nes", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (rom is not null)
                {
                    return rom;
                }
            }
        }

        return Path.Combine(Environment.CurrentDirectory, "missing.nes");
    }

    private static string CreateTestRom(int mapperId, int prgBanks, int chrBanks)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nesemu-test-{Guid.NewGuid():N}.nes");
        using var stream = File.Create(path);

        Span<byte> header = stackalloc byte[16];
        header[0] = (byte)'N';
        header[1] = (byte)'E';
        header[2] = (byte)'S';
        header[3] = 0x1A;
        header[4] = (byte)prgBanks;
        header[5] = (byte)chrBanks;
        header[6] = (byte)((mapperId & 0x0F) << 4);
        header[7] = (byte)(mapperId & 0xF0);
        stream.Write(header);

        for (var bank = 0; bank < prgBanks; bank++)
        {
            var prg = Enumerable.Repeat((byte)(bank * 0x31 + 7), 0x4000).ToArray();
            stream.Write(prg);
        }

        for (var bank = 0; bank < chrBanks; bank++)
        {
            var chr = Enumerable.Repeat((byte)(bank * 0x17 + 3), 0x2000).ToArray();
            stream.Write(chr);
        }

        return path;
    }
}
