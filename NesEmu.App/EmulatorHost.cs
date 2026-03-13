using System.Diagnostics;
using System.Threading;
using NesEmu.Core;
using NesEmu.Core.Cartridge;

namespace NesEmu.App;

public sealed class EmulatorHost : IDisposable
{
    private static readonly TimeSpan FrameDuration = TimeSpan.FromSeconds(1.0 / 60.0988138974405);
    private static readonly TimeSpan PreciseSleepMargin = TimeSpan.FromMilliseconds(2);

    private readonly Func<ControllerState> _inputProvider;
    private readonly Action<uint[]> _frameCallback;
    private readonly MidiOutputService _midiOutput;
    private readonly float[] _audioSampleScratch = new float[4096];
    private readonly uint[] _videoFrameScratch = new uint[NesVideoConstants.PixelsPerFrame];
    private float _masterVolumeScale = 1.0f;

    private IAudioOutput _audioOutput;
    private AudioOutputSettings _audioSettings;
    private TimeSpan _resumeBufferedDuration;
    private TimeSpan _targetBufferedDuration;
    private TimeSpan _maximumBufferedDuration;
    private bool _hasRealtimeAudioOutput;

    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private NesConsole? _console;
    private bool _disposed;
    private volatile bool _paused = true;
    private volatile bool _stopped;
    private bool _playbackPrimed;

    public EmulatorHost(Func<ControllerState> inputProvider, Action<uint[]> frameCallback, MidiOutputService midiOutput)
    {
        _inputProvider = inputProvider;
        _frameCallback = frameCallback;
        _midiOutput = midiOutput;

        _audioSettings = AudioOutputSettings.CreateDefault();
        ConfigureAudioTiming(_audioSettings);
        _masterVolumeScale = _audioSettings.MasterVolumePercent / 100.0f;
        _audioOutput = CreateAudioOutputWithFallback(_audioSettings);
        _hasRealtimeAudioOutput = _audioOutput.HasRealtimeOutput;
    }

    public bool IsPaused => _paused;

    public bool IsStopped => _stopped;

    public string? LoadedRomPath { get; private set; }

    public bool HasRomLoaded => _console is not null;

    public AudioOutputSettings GetAudioSettingsSnapshot() => _audioSettings.Clone();

    public bool TryApplyAudioSettings(AudioOutputSettings settings, out string? error)
    {
        error = null;
        if (_disposed)
        {
            error = "Audio output is not available.";
            return false;
        }

        var normalized = NormalizeAudioSettings(settings);
        var hadLoop = _loopCancellation is not null;
        var previousSettings = _audioSettings.Clone();
        var previousOutput = _audioOutput;
        var previousRealtimeAudioOutput = _hasRealtimeAudioOutput;

        StopLoop();
        PausePlaybackAndFlush();

        try
        {
            ConfigureAudioTiming(normalized);
            var newOutput = CreateAudioOutput(normalized);
            _audioSettings = normalized;
            _audioOutput = newOutput;
            _hasRealtimeAudioOutput = newOutput.HasRealtimeOutput;
            _masterVolumeScale = normalized.MasterVolumePercent / 100.0f;
            _playbackPrimed = false;

            previousOutput.Stop();
            previousOutput.Dispose();

            if (hadLoop)
            {
                StartLoop();
            }

            return true;
        }
        catch (Exception ex)
        {
            _audioSettings = previousSettings;
            _audioOutput = previousOutput;
            _hasRealtimeAudioOutput = previousRealtimeAudioOutput;
            ConfigureAudioTiming(previousSettings);
            _masterVolumeScale = previousSettings.MasterVolumePercent / 100.0f;

            if (hadLoop)
            {
                StartLoop();
            }

            error = ex.Message;
            return false;
        }
    }

    public void LoadRom(string romPath)
    {
        StopLoop();
        PausePlaybackAndFlush();
        _midiOutput.Silence();

        _console?.Dispose();
        _console = new NesConsole(CartridgeImage.Load(romPath));
        LoadedRomPath = romPath;
        _paused = false;
        _stopped = false;
        _playbackPrimed = false;
        StartLoop();
    }

    public void Reset()
    {
        _console?.Reset();
        PausePlaybackAndFlush();
        _midiOutput.Silence();
        _playbackPrimed = false;
    }

    public void TogglePause()
    {
        if (_console is null)
        {
            return;
        }

        if (_stopped)
        {
            _stopped = false;
            _paused = false;
            _playbackPrimed = false;
            return;
        }

        _paused = !_paused;

        if (_paused)
        {
            PausePlaybackAndFlush();
            _midiOutput.Silence();
        }
        else
        {
            _playbackPrimed = false;
        }
    }

    public void Stop()
    {
        if (_console is null)
        {
            return;
        }

        _console.Reset();
        _stopped = true;
        _paused = false;
        _playbackPrimed = false;
        PausePlaybackAndFlush();
        _midiOutput.Silence();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopLoop();
        _midiOutput.Silence();
        _audioOutput.Stop();
        _audioOutput.Dispose();
        _console?.Dispose();
    }

    private void StartLoop()
    {
        if (_console is null)
        {
            return;
        }

        _loopCancellation = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_loopCancellation.Token));
    }

    private void StopLoop()
    {
        if (_loopCancellation is null)
        {
            return;
        }

        _loopCancellation.Cancel();
        try
        {
            _loopTask?.Wait();
        }
        catch (AggregateException)
        {
        }
        finally
        {
            _loopCancellation.Dispose();
            _loopCancellation = null;
            _loopTask = null;
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var nextFrameTime = stopwatch.Elapsed;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_console is null || _paused || _stopped)
            {
                PausePlaybackIfNeeded();
                _midiOutput.ClearPresentationLatency();
                await Task.Delay(16, cancellationToken);
                nextFrameTime = stopwatch.Elapsed;
                continue;
            }

            _console.SetControllerState(0, _inputProvider());
            _console.RunFrame();
            PumpAudio();
            UpdatePlaybackState();

            if (_midiOutput.IsOutputActive)
            {
                _midiOutput.SetPresentationLatency(EstimatePresentationLatency());
                _midiOutput.ProcessFrame(_console.CaptureApuTapSnapshot());
            }

            _console.FrameBuffer.CopyTo(_videoFrameScratch);
            _frameCallback(_videoFrameScratch);

            nextFrameTime += FrameDuration;
            var remaining = nextFrameTime - stopwatch.Elapsed;
            var bufferedDuration = _audioOutput.BufferedDuration;

            if (!_hasRealtimeAudioOutput)
            {
                if (remaining > TimeSpan.Zero)
                {
                    await WaitForNextFrameAsync(stopwatch, nextFrameTime, cancellationToken);
                }
                else if (remaining < -FrameDuration)
                {
                    nextFrameTime = stopwatch.Elapsed;
                }

                continue;
            }

            if (bufferedDuration >= _targetBufferedDuration && remaining > TimeSpan.Zero)
            {
                await WaitForNextFrameAsync(stopwatch, nextFrameTime, cancellationToken);
            }
            else if (bufferedDuration > _maximumBufferedDuration)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);
            }
            else if (remaining < -FrameDuration)
            {
                nextFrameTime = stopwatch.Elapsed;
            }
        }
    }

    private void PumpAudio()
    {
        if (_console is null)
        {
            return;
        }

        while (true)
        {
            var count = _console.DrainAudioSamples(_audioSampleScratch);
            if (count == 0)
            {
                break;
            }

            ApplyMasterVolume(_audioSampleScratch.AsSpan(0, count));
            _audioOutput.WriteSamples(_audioSampleScratch.AsSpan(0, count));
        }
    }

    private void UpdatePlaybackState()
    {
        if (!_hasRealtimeAudioOutput)
        {
            return;
        }

        var bufferedDuration = _audioOutput.BufferedDuration;
        if (_playbackPrimed && _audioOutput.PlaybackState == AudioPlaybackState.Playing)
        {
            return;
        }

        if (bufferedDuration < _resumeBufferedDuration)
        {
            return;
        }

        _audioOutput.Play();
        _playbackPrimed = true;
    }

    private void PausePlaybackIfNeeded()
    {
        if (_audioOutput.PlaybackState == AudioPlaybackState.Playing)
        {
            _audioOutput.Pause();
        }
    }

    private void PausePlaybackAndFlush()
    {
        PausePlaybackIfNeeded();
        ClearAudioBuffer();
        _midiOutput.ClearPresentationLatency();
        _playbackPrimed = false;
    }

    private void ClearAudioBuffer()
    {
        _audioOutput.Clear();
    }

    private TimeSpan EstimatePresentationLatency()
    {
        if (!_hasRealtimeAudioOutput)
        {
            return TimeSpan.Zero;
        }

        var bufferedDuration = _audioOutput.BufferedDuration;
        if (!_playbackPrimed || _audioOutput.PlaybackState != AudioPlaybackState.Playing)
        {
            return bufferedDuration >= _resumeBufferedDuration ? bufferedDuration : _resumeBufferedDuration;
        }

        return bufferedDuration;
    }

    private void ConfigureAudioTiming(AudioOutputSettings settings)
    {
        var latency = Math.Clamp(settings.OutputLatencyMilliseconds, 40, 240);
        var frameMs = FrameDuration.TotalMilliseconds;
        var minimum = Math.Max((int)Math.Ceiling(frameMs * 1.25), (int)Math.Round(latency * 0.45));
        var target = Math.Max(minimum + (int)Math.Ceiling(frameMs), (int)Math.Round(latency * 0.72));
        var resume = Math.Max(target + (int)Math.Ceiling(frameMs * 0.5), (int)Math.Round(latency * 0.90));
        var maximum = Math.Max(resume + (int)Math.Ceiling(frameMs * 4), (int)Math.Round(latency * 1.70));

        _targetBufferedDuration = TimeSpan.FromMilliseconds(target);
        _resumeBufferedDuration = TimeSpan.FromMilliseconds(resume);
        _maximumBufferedDuration = TimeSpan.FromMilliseconds(maximum);
    }

    private static AudioOutputSettings NormalizeAudioSettings(AudioOutputSettings settings)
    {
        return new AudioOutputSettings
        {
            Backend = settings.Backend,
            OutputLatencyMilliseconds = Math.Clamp(settings.OutputLatencyMilliseconds, 40, 240),
            MasterVolumePercent = Math.Clamp(settings.MasterVolumePercent, 0, 200)
        };
    }

    private static IAudioOutput CreateAudioOutput(AudioOutputSettings settings)
    {
        var normalized = NormalizeAudioSettings(settings);
        var bufferDuration = TimeSpan.FromMilliseconds(Math.Max(normalized.OutputLatencyMilliseconds * 3, 180));
        return normalized.Backend switch
        {
            AudioBackendKind.OpenAl => new OpenAlAudioOutput(NesConsole.AudioSampleRate, bufferDuration, normalized.OutputLatencyMilliseconds),
            _ => throw new NotSupportedException($"Audio backend '{normalized.Backend}' is not supported.")
        };
    }

    private static IAudioOutput CreateAudioOutputWithFallback(AudioOutputSettings settings)
    {
        try
        {
            return CreateAudioOutput(settings);
        }
        catch
        {
            return new NullAudioOutput();
        }
    }

    private static async Task WaitForNextFrameAsync(Stopwatch stopwatch, TimeSpan targetTime, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = targetTime - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            if (remaining > PreciseSleepMargin)
            {
                await Task.Delay(remaining - PreciseSleepMargin, cancellationToken);
                continue;
            }

            Thread.SpinWait(128);
        }
    }

    private void ApplyMasterVolume(Span<float> samples)
    {
        if (Math.Abs(_masterVolumeScale - 1.0f) < 0.0001f)
        {
            return;
        }

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = Math.Clamp(samples[i] * _masterVolumeScale, -1.0f, 1.0f);
        }
    }

}
