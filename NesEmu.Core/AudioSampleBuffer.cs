namespace NesEmu.Core;

public sealed class AudioSampleBuffer
{
    private readonly object _lock = new();
    private readonly float[] _samples;
    private readonly int _sampleRate;
    private readonly int _transitionSampleCount;

    private int _readIndex;
    private int _count;
    private bool _renderStarved = true;
    private int _resumeFadeRemaining;
    private float _resumeFadeStart;
    private int _underrunFadeRemaining;
    private float _underrunFadeStart;
    private float _lastRenderedSample;

    public AudioSampleBuffer(int sampleRate, TimeSpan capacity, TimeSpan? transitionDuration = null)
    {
        _sampleRate = sampleRate;
        CapacitySamples = Math.Max(1, (int)Math.Ceiling(capacity.TotalSeconds * sampleRate));
        _samples = new float[CapacitySamples];

        var transition = transitionDuration ?? TimeSpan.FromMilliseconds(2);
        _transitionSampleCount = Math.Max(8, (int)Math.Ceiling(transition.TotalSeconds * sampleRate));
    }

    public int CapacitySamples { get; }

    public int BufferedSamples
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    public TimeSpan BufferedDuration => TimeSpan.FromSeconds((double)BufferedSamples / _sampleRate);

    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        lock (_lock)
        {
            if (samples.Length >= CapacitySamples)
            {
                samples = samples[^CapacitySamples..];
                _readIndex = 0;
                _count = 0;
            }

            var overflow = Math.Max(0, (_count + samples.Length) - CapacitySamples);
            if (overflow > 0)
            {
                _readIndex = (_readIndex + overflow) % CapacitySamples;
                _count -= overflow;
            }

            var writeIndex = (_readIndex + _count) % CapacitySamples;
            var remaining = samples.Length;
            var sourceOffset = 0;

            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, CapacitySamples - writeIndex);
                samples.Slice(sourceOffset, chunk).CopyTo(_samples.AsSpan(writeIndex, chunk));
                sourceOffset += chunk;
                remaining -= chunk;
                writeIndex = (writeIndex + chunk) % CapacitySamples;
            }

            _count += samples.Length;
        }
    }

    public int ReadAvailable(Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        lock (_lock)
        {
            return ReadAvailableLocked(destination);
        }
    }

    public void Render(Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        lock (_lock)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                if (_count > 0)
                {
                    var sample = DequeueSampleLocked();
                    sample = ApplyResumeFadeLocked(sample);
                    _lastRenderedSample = sample;
                    destination[i] = sample;
                    continue;
                }

                destination[i] = RenderUnderrunLocked();
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            ClearLocked();
        }
    }

    public void ResetRendering()
    {
        lock (_lock)
        {
            _renderStarved = true;
            _resumeFadeRemaining = 0;
            _underrunFadeRemaining = 0;
            _resumeFadeStart = 0.0f;
            _underrunFadeStart = 0.0f;
            _lastRenderedSample = 0.0f;
        }
    }

    private int ReadAvailableLocked(Span<float> destination)
    {
        var count = Math.Min(destination.Length, _count);
        for (var i = 0; i < count; i++)
        {
            destination[i] = DequeueSampleLocked();
        }

        return count;
    }

    private float DequeueSampleLocked()
    {
        var sample = _samples[_readIndex];
        _readIndex = (_readIndex + 1) % CapacitySamples;
        _count--;
        return sample;
    }

    private float ApplyResumeFadeLocked(float sample)
    {
        if (_renderStarved)
        {
            _renderStarved = false;
            _underrunFadeRemaining = 0;
            _resumeFadeRemaining = _transitionSampleCount;
            _resumeFadeStart = _lastRenderedSample;
        }

        if (_resumeFadeRemaining <= 0)
        {
            return sample;
        }

        var step = _transitionSampleCount - _resumeFadeRemaining + 1;
        var amount = step / (float)_transitionSampleCount;
        _resumeFadeRemaining--;
        return Lerp(_resumeFadeStart, sample, amount);
    }

    private float RenderUnderrunLocked()
    {
        if (!_renderStarved)
        {
            _renderStarved = true;
            _resumeFadeRemaining = 0;
            _underrunFadeRemaining = _transitionSampleCount;
            _underrunFadeStart = _lastRenderedSample;
        }

        if (_underrunFadeRemaining > 0)
        {
            var amount = _underrunFadeRemaining / (float)_transitionSampleCount;
            _underrunFadeRemaining--;
            _lastRenderedSample = _underrunFadeStart * amount;
            return _lastRenderedSample;
        }

        _lastRenderedSample = 0.0f;
        return 0.0f;
    }

    private void ClearLocked()
    {
        _readIndex = 0;
        _count = 0;
        _renderStarved = true;
        _resumeFadeRemaining = 0;
        _underrunFadeRemaining = 0;
        _resumeFadeStart = 0.0f;
        _underrunFadeStart = 0.0f;
        _lastRenderedSample = 0.0f;
    }

    private static float Lerp(float start, float end, float amount)
    {
        return start + ((end - start) * amount);
    }
}
