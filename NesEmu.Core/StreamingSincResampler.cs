namespace NesEmu.Core;

public sealed class StreamingSincResampler
{
    private readonly double _inputRate;
    private readonly double _outputRate;
    private readonly double _cutoff;
    private readonly int _halfKernel;
    private readonly float[] _history;

    private long _samplesWritten;
    private double _nextOutputTime;

    public StreamingSincResampler(double inputRate, double outputRate, int taps = 33, double cutoffRatio = 0.97)
    {
        if (inputRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputRate));
        }

        if (outputRate <= 0 || outputRate > inputRate)
        {
            throw new ArgumentOutOfRangeException(nameof(outputRate));
        }

        if (taps < 7 || (taps & 0x01) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(taps), "Tap count must be odd and at least 7.");
        }

        _inputRate = inputRate;
        _outputRate = outputRate;
        _cutoff = Math.Clamp(0.5 * (outputRate / inputRate) * cutoffRatio, 0.0001, 0.4999);
        _halfKernel = taps / 2;
        _history = new float[Math.Max(512, taps * 8)];
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_history);
        _samplesWritten = 0;
        _nextOutputTime = _halfKernel;
    }

    public bool AddSample(float sample, out float output)
    {
        _history[_samplesWritten % _history.Length] = sample;
        _samplesWritten++;

        if ((_samplesWritten - 1) < (_nextOutputTime + _halfKernel))
        {
            output = 0.0f;
            return false;
        }

        output = SampleAt(_nextOutputTime);
        _nextOutputTime += _inputRate / _outputRate;
        return true;
    }

    private float SampleAt(double time)
    {
        var center = (long)Math.Floor(time);
        var frac = time - center;
        var sum = 0.0;
        var weightSum = 0.0;

        for (var i = -_halfKernel; i <= _halfKernel; i++)
        {
            var sampleIndex = center + i;
            var sample = _history[PositiveModulo(sampleIndex, _history.Length)];
            var distance = i - frac;
            var weight = SincWeight(distance) * Blackman(distance);

            sum += sample * weight;
            weightSum += weight;
        }

        if (Math.Abs(weightSum) < 1e-12)
        {
            return 0.0f;
        }

        return (float)(sum / weightSum);
    }

    private double SincWeight(double distance)
    {
        var x = 2.0 * _cutoff * distance;
        if (Math.Abs(x) < 1e-12)
        {
            return 2.0 * _cutoff;
        }

        return 2.0 * _cutoff * (Math.Sin(Math.PI * x) / (Math.PI * x));
    }

    private double Blackman(double distance)
    {
        var radius = _halfKernel + 1.0;
        var normalized = (distance + radius) / (2.0 * radius);
        if (normalized < 0.0 || normalized > 1.0)
        {
            return 0.0;
        }

        return 0.42
            - (0.5 * Math.Cos(2.0 * Math.PI * normalized))
            + (0.08 * Math.Cos(4.0 * Math.PI * normalized));
    }

    private static int PositiveModulo(long value, int modulus)
    {
        var result = (int)(value % modulus);
        return result < 0 ? result + modulus : result;
    }
}
