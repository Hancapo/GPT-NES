using NesEmu.Core;

namespace NesEmu.Tests;

public sealed class StreamingSincResamplerTests
{
    [Fact]
    public void ConstantSignal_ConvergesToUnityGain()
    {
        var resampler = new StreamingSincResampler(NesConsole.CpuFrequency, NesConsole.AudioSampleRate);
        var outputs = new List<float>();

        for (var i = 0; i < 32_768; i++)
        {
            if (resampler.AddSample(1.0f, out var output))
            {
                outputs.Add(output);
            }
        }

        Assert.NotEmpty(outputs);
        var tail = outputs.Skip(Math.Max(0, outputs.Count - 64)).ToArray();
        Assert.All(tail, sample => Assert.InRange(sample, 0.995f, 1.005f));
    }

    [Fact]
    public void Reset_ClearsHistoryAndPhase()
    {
        var resampler = new StreamingSincResampler(NesConsole.CpuFrequency, NesConsole.AudioSampleRate);

        for (var i = 0; i < 8_192; i++)
        {
            resampler.AddSample(1.0f, out _);
        }

        resampler.Reset();

        var emitted = false;
        for (var i = 0; i < 32; i++)
        {
            emitted |= resampler.AddSample(0.5f, out _);
        }

        Assert.False(emitted);
    }
}
