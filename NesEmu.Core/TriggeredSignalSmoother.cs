namespace NesEmu.Core;

public sealed class TriggeredSignalSmoother
{
    private readonly int _transitionSamples;

    private int _lastTriggerVersion;
    private bool _initialized;
    private int _remainingSamples;
    private float _blendStartValue;
    private float _currentValue;

    public TriggeredSignalSmoother(int sampleRate, TimeSpan transitionDuration)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        _transitionSamples = Math.Max(1, (int)Math.Ceiling(transitionDuration.TotalSeconds * sampleRate));
    }

    public float Process(float rawValue, int triggerVersion)
    {
        if (!_initialized)
        {
            _initialized = true;
            _lastTriggerVersion = triggerVersion;
            _currentValue = rawValue;
            return rawValue;
        }

        if (triggerVersion != _lastTriggerVersion)
        {
            _lastTriggerVersion = triggerVersion;
            _blendStartValue = _currentValue;
            _remainingSamples = _transitionSamples;
        }

        if (_remainingSamples <= 0)
        {
            _currentValue = rawValue;
            return rawValue;
        }

        var amount = (_transitionSamples - _remainingSamples + 1) / (float)_transitionSamples;
        _remainingSamples--;
        _currentValue = Lerp(_blendStartValue, rawValue, amount);
        return _currentValue;
    }

    public void Prime(float value, int triggerVersion = 0)
    {
        _initialized = true;
        _lastTriggerVersion = triggerVersion;
        _remainingSamples = 0;
        _blendStartValue = value;
        _currentValue = value;
    }

    public void Reset()
    {
        _lastTriggerVersion = 0;
        _initialized = false;
        _remainingSamples = 0;
        _blendStartValue = 0.0f;
        _currentValue = 0.0f;
    }

    private static float Lerp(float start, float end, float amount)
    {
        return start + ((end - start) * amount);
    }
}
