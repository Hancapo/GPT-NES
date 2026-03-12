using NesEmu.Core;

namespace NesEmu.Tests;

public sealed class Apu2A03Tests
{
    [Fact]
    public void FrameIrq_SetsOn29828thCpuClockInFourStepMode()
    {
        var apu = new Apu2A03(NesConsole.AudioSampleRate);
        apu.WriteRegister(0x4017, 0x00);

        for (var i = 0; i < 29827; i++)
        {
            apu.Clock();
        }

        Assert.False(apu.IrqPending);

        apu.Clock();

        Assert.True(apu.IrqPending);
    }

    [Fact]
    public void Reading4015_ClearsFrameIrqOnFollowingClock()
    {
        var apu = new Apu2A03(NesConsole.AudioSampleRate);
        apu.WriteRegister(0x4017, 0x00);

        for (var i = 0; i < 29828; i++)
        {
            apu.Clock();
        }

        Assert.Equal(0x40, apu.ReadStatus() & 0x40);
        Assert.Equal(0x40, apu.ReadStatus() & 0x40);

        apu.Clock();

        Assert.Equal(0x00, apu.ReadStatus() & 0x40);
    }
}
