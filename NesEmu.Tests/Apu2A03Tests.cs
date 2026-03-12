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

    [Fact]
    public void DmcDirectLoad_ContributesToMixedOutput()
    {
        var apu = new Apu2A03(NesConsole.AudioSampleRate);

        Assert.Equal(0.0f, apu.GetCurrentRawSample(), 6);

        apu.WriteRegister(0x4011, 0x40);

        Assert.True(apu.GetCurrentRawSample() > 0.0f);
    }

    [Fact]
    public void Reading4015_ReportsDmcActiveBitBeforeSingleByteSampleIsFetched()
    {
        var apu = new Apu2A03(NesConsole.AudioSampleRate, _ => 0xAA);
        apu.WriteRegister(0x4012, 0x00);
        apu.WriteRegister(0x4013, 0x00);
        apu.WriteRegister(0x4015, 0x10);

        Assert.Equal(0x10, apu.ReadStatus() & 0x10);

        apu.Clock();

        Assert.Equal(0x00, apu.ReadStatus() & 0x10);
    }

    [Fact]
    public void DmcFetchesSampleBytesFromConfiguredAddress_AndCanRaiseIrq()
    {
        ushort? fetchedAddress = null;
        var apu = new Apu2A03(NesConsole.AudioSampleRate, address =>
        {
            fetchedAddress = address;
            return 0x00;
        });

        apu.WriteRegister(0x4010, 0x80);
        apu.WriteRegister(0x4012, 0x40);
        apu.WriteRegister(0x4013, 0x00);
        apu.WriteRegister(0x4015, 0x10);
        apu.Clock();

        Assert.Equal((ushort)0xD000, fetchedAddress);
        Assert.True(apu.IrqPending);
        Assert.Equal(0x80, apu.ReadStatus() & 0x80);

        apu.WriteRegister(0x4015, 0x00);

        Assert.False(apu.IrqPending);
        Assert.Equal(0x00, apu.ReadStatus() & 0x80);
    }
}
