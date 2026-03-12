using NesEmu.Core;

namespace NesEmu.Tests;

public sealed class TriangleChannelBehaviorTests
{
    [Fact]
    public void TriangleOutput_HoldsLastValueWhenSilenced()
    {
        var apu = new Apu2A03(NesConsole.AudioSampleRate);

        apu.WriteRegister(0x4015, 0x04);
        apu.WriteRegister(0x4008, 0x81);
        apu.WriteRegister(0x400A, 0x02);
        apu.WriteRegister(0x400B, 0x08);
        apu.WriteRegister(0x4017, 0x80);

        for (var i = 0; i < 64; i++)
        {
            apu.Clock();
        }

        var beforeDisable = apu.GetCurrentRawSample();
        Assert.True(beforeDisable > 0.0f);

        apu.WriteRegister(0x4015, 0x00);

        var afterDisable = apu.GetCurrentRawSample();
        Assert.Equal(beforeDisable, afterDisable, 6);

        for (var i = 0; i < 64; i++)
        {
            apu.Clock();
        }

        Assert.Equal(afterDisable, apu.GetCurrentRawSample(), 6);
    }
}
