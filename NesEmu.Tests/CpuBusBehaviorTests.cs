using NesEmu.Core;
using NesEmu.Core.Cartridge;

namespace NesEmu.Tests;

public sealed class CpuBusBehaviorTests
{
    [Fact]
    public void UnmappedCpuReads_ReturnCurrentOpenBusValue()
    {
        using var context = CreateConsole();
        var console = context.Console;

        console.CpuWrite(0x4017, 0xAB);

        Assert.Equal(0xAB, console.CpuRead(0x5000));
    }

    [Fact]
    public void Writing4015_UpdatesOpenBus()
    {
        using var context = CreateConsole();
        var console = context.Console;

        console.CpuWrite(0x4015, 0x5A);

        Assert.Equal(0x5A, console.CpuRead(0x5000));
    }

    [Fact]
    public void Reading4015_DoesNotReplaceCpuOpenBus_AndBit5StaysOpenBus()
    {
        using var context = CreateConsole();
        var console = context.Console;

        console.CpuWrite(0x4015, 0x01);
        console.CpuWrite(0x4003, 0x08);
        console.CpuWrite(0x4017, 0x20);

        var status = console.CpuRead(0x4015);

        Assert.Equal(0x21, status);
        Assert.Equal(0x20, console.CpuRead(0x5000));
    }

    [Fact]
    public void ControllerReads_UseOpenBusForUpperBits()
    {
        using var context = CreateConsole();
        var console = context.Console;

        console.CpuWrite(0x4017, 0xE0);

        Assert.Equal(0xE0, console.CpuRead(0x4016));
        Assert.Equal(0xE0, console.CpuRead(0x5000));
    }

    [Fact]
    public void ConsecutiveControllerReads_DoNotAdvanceLatchTwice()
    {
        using var context = CreateConsole();
        var console = context.Console;

        console.SetControllerState(0, new ControllerState(A: true, B: true, Select: false, Start: false, Up: false, Down: false, Left: false, Right: false));
        console.CpuWrite(0x4016, 0x01);
        console.CpuWrite(0x4016, 0x00);

        Assert.Equal(0x01, console.CpuRead(0x4016) & 0x01);
        Assert.Equal(0x01, console.CpuRead(0x4016) & 0x01);

        _ = console.CpuRead(0x5000);

        Assert.Equal(0x01, console.CpuRead(0x4016) & 0x01);
    }

    private static TestConsoleContext CreateConsole()
    {
        var romPath = CreateTestRom(mapperId: 0, prgBanks: 1, chrBanks: 1);
        var cartridge = CartridgeImage.Load(romPath);
        return new TestConsoleContext(new NesConsole(cartridge), romPath);
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

    private sealed class TestConsoleContext(NesConsole console, string romPath) : IDisposable
    {
        public NesConsole Console { get; } = console;

        public void Dispose()
        {
            Console.Dispose();
            File.Delete(romPath);
        }
    }
}
