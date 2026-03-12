using NesEmu.Core;

namespace NesEmu.Tests;

public sealed class AudioSampleBufferTests
{
    [Fact]
    public void Write_WhenCapacityIsExceeded_KeepsNewestSamples()
    {
        var buffer = new AudioSampleBuffer(
            sampleRate: 1_000,
            capacity: TimeSpan.FromMilliseconds(5),
            transitionDuration: TimeSpan.FromMilliseconds(1));

        buffer.Write([0f, 1f, 2f, 3f, 4f, 5f, 6f]);

        Span<float> rendered = stackalloc float[5];
        var count = buffer.ReadAvailable(rendered);

        Assert.Equal(5, count);
        Assert.Equal([2f, 3f, 4f, 5f, 6f], rendered.ToArray());
    }

    [Fact]
    public void Render_WhenStartingFromSilence_FadesIntoNewAudio()
    {
        var buffer = new AudioSampleBuffer(
            sampleRate: 1_000,
            capacity: TimeSpan.FromMilliseconds(32),
            transitionDuration: TimeSpan.FromMilliseconds(8));

        buffer.Write([1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f]);

        var rendered = new float[8];
        buffer.Render(rendered);

        Assert.Equal(0.125f, rendered[0], 3);
        Assert.Equal(0.250f, rendered[1], 3);
        Assert.Equal(0.375f, rendered[2], 3);
        Assert.Equal(0.500f, rendered[3], 3);
        Assert.Equal(0.625f, rendered[4], 3);
        Assert.Equal(0.750f, rendered[5], 3);
        Assert.Equal(0.875f, rendered[6], 3);
        Assert.Equal(1.000f, rendered[7], 3);
    }

    [Fact]
    public void Render_WhenStarved_FadesOutAndSmoothlyReturns()
    {
        var buffer = new AudioSampleBuffer(
            sampleRate: 1_000,
            capacity: TimeSpan.FromMilliseconds(32),
            transitionDuration: TimeSpan.FromMilliseconds(8));

        buffer.Write([1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f]);

        var initial = new float[18];
        buffer.Render(initial);

        Assert.Equal(1.000f, initial[7], 3);
        Assert.Equal(1.000f, initial[8], 3);
        Assert.Equal(0.875f, initial[9], 3);
        Assert.Equal(0.750f, initial[10], 3);
        Assert.Equal(0.625f, initial[11], 3);
        Assert.Equal(0.500f, initial[12], 3);
        Assert.Equal(0.375f, initial[13], 3);
        Assert.Equal(0.250f, initial[14], 3);
        Assert.Equal(0.125f, initial[15], 3);
        Assert.Equal(0.000f, initial[16], 3);
        Assert.Equal(0.000f, initial[17], 3);

        buffer.Write([-1f, -1f, -1f, -1f]);

        var resumed = new float[4];
        buffer.Render(resumed);

        Assert.Equal(-0.125f, resumed[0], 3);
        Assert.Equal(-0.250f, resumed[1], 3);
        Assert.Equal(-0.375f, resumed[2], 3);
        Assert.Equal(-0.500f, resumed[3], 3);
    }
}
