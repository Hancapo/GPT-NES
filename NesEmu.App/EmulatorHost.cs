using System.Diagnostics;
using System.Threading;
using NAudio.Wave;
using NesEmu.Core;
using NesEmu.Core.Cartridge;

namespace NesEmu.App;

public sealed class EmulatorHost : IDisposable
{
    private const int OutputBufferCount = 2;
    private static readonly TimeSpan FrameDuration = TimeSpan.FromSeconds(1.0 / 60.0988138974405);
    private static readonly TimeSpan PreciseSleepMargin = TimeSpan.FromMilliseconds(2);

    private readonly Func<ControllerState> _inputProvider;
    private readonly Action<uint[]> _frameCallback;
    private readonly MidiOutputService _midiOutput;
    private readonly float[] _audioSampleScratch = new float[4096];
    private byte[] _audioByteScratch = new byte[4096 * sizeof(float)];
    private readonly uint[] _videoFrameScratch = new uint[NesVideoConstants.PixelsPerFrame];
    private float _masterVolumeScale = 1.0f;

    private BufferedWaveProvider _audioBuffer;
    private WaveOutEvent _waveOut;
    private AudioOutputSettings _audioSettings;
    private TimeSpan _resumeBufferedDuration;
    private TimeSpan _minimumBufferedDuration;
    private TimeSpan _targetBufferedDuration;
    private TimeSpan _maximumBufferedDuration;

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
        (_audioBuffer, _waveOut) = CreateAudioOutput(_audioSettings);
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
        var previousBuffer = _audioBuffer;
        var previousWaveOut = _waveOut;

        StopLoop();
        PausePlaybackAndFlush();

        try
        {
            ConfigureAudioTiming(normalized);
            var (newBuffer, newWaveOut) = CreateAudioOutput(normalized);
            _audioSettings = normalized;
            _audioBuffer = newBuffer;
            _waveOut = newWaveOut;
            _masterVolumeScale = normalized.MasterVolumePercent / 100.0f;
            _playbackPrimed = false;

            previousWaveOut.Stop();
            previousWaveOut.Dispose();

            if (hadLoop)
            {
                StartLoop();
            }

            return true;
        }
        catch (Exception ex)
        {
            _audioSettings = previousSettings;
            _audioBuffer = previousBuffer;
            _waveOut = previousWaveOut;
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
        _waveOut.Stop();
        _waveOut.Dispose();
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
                _midiOutput.SetPresentationLatency(TimeSpan.Zero);
                await Task.Delay(16, cancellationToken);
                nextFrameTime = stopwatch.Elapsed;
                continue;
            }

            _console.SetControllerState(0, _inputProvider());
            _console.RunFrame();
            PumpAudio();
            PrimePlaybackIfReady();
            _midiOutput.SetPresentationLatency(EstimatePresentationLatency());
            _midiOutput.ProcessFrame(_console.CaptureApuTapSnapshot());

            _console.FrameBuffer.CopyTo(_videoFrameScratch);
            _frameCallback(_videoFrameScratch);

            nextFrameTime += FrameDuration;
            var remaining = nextFrameTime - stopwatch.Elapsed;
            var bufferedDuration = _audioBuffer.BufferedDuration;

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

            var byteCount = count * sizeof(float);
            if (_audioByteScratch.Length < byteCount)
            {
                _audioByteScratch = new byte[byteCount];
            }

            ApplyMasterVolume(_audioSampleScratch.AsSpan(0, count));
            Buffer.BlockCopy(_audioSampleScratch, 0, _audioByteScratch, 0, byteCount);
            _audioBuffer.AddSamples(_audioByteScratch, 0, byteCount);
        }
    }

    private void PrimePlaybackIfReady()
    {
        if (_playbackPrimed || _audioBuffer.BufferedDuration < _resumeBufferedDuration)
        {
            return;
        }

        _waveOut.Play();
        _playbackPrimed = true;
    }

    private void PausePlaybackIfNeeded()
    {
        if (_waveOut.PlaybackState == PlaybackState.Playing)
        {
            _waveOut.Pause();
        }
    }

    private void PausePlaybackAndFlush()
    {
        PausePlaybackIfNeeded();
        ClearAudioBuffer();
        _midiOutput.SetPresentationLatency(TimeSpan.Zero);
    }

    private void ClearAudioBuffer()
    {
        if (_audioBuffer.BufferedBytes == 0)
        {
            return;
        }

        var discard = new byte[Math.Max(_audioBuffer.WaveFormat.AverageBytesPerSecond / 4, 4096)];
        while (_audioBuffer.BufferedBytes > 0)
        {
            _audioBuffer.Read(discard, 0, Math.Min(discard.Length, _audioBuffer.BufferedBytes));
        }
    }

    private TimeSpan EstimatePresentationLatency()
    {
        var bufferedDuration = _audioBuffer.BufferedDuration;
        if (!_playbackPrimed || _waveOut.PlaybackState != PlaybackState.Playing)
        {
            return bufferedDuration >= _resumeBufferedDuration ? bufferedDuration : _resumeBufferedDuration;
        }

        return bufferedDuration;
    }

    private void ConfigureAudioTiming(AudioOutputSettings settings)
    {
        var latency = Math.Clamp(settings.OutputLatencyMilliseconds, 40, 240);
        var minimum = Math.Max(32, (int)Math.Round(latency * 0.65));
        var target = Math.Max(minimum + 10, (int)Math.Round(latency * 0.90));
        var resume = Math.Max(target + 8, latency);
        var maximum = Math.Max(resume + 72, latency * 2);

        _minimumBufferedDuration = TimeSpan.FromMilliseconds(minimum);
        _targetBufferedDuration = TimeSpan.FromMilliseconds(target);
        _resumeBufferedDuration = TimeSpan.FromMilliseconds(resume);
        _maximumBufferedDuration = TimeSpan.FromMilliseconds(maximum);
    }

    private static AudioOutputSettings NormalizeAudioSettings(AudioOutputSettings settings)
    {
        return new AudioOutputSettings
        {
            OutputLatencyMilliseconds = Math.Clamp(settings.OutputLatencyMilliseconds, 40, 240),
            MasterVolumePercent = Math.Clamp(settings.MasterVolumePercent, 0, 200)
        };
    }

    private static (BufferedWaveProvider Buffer, WaveOutEvent WaveOut) CreateAudioOutput(AudioOutputSettings settings)
    {
        var normalized = NormalizeAudioSettings(settings);
        var bufferDuration = TimeSpan.FromMilliseconds(Math.Max(normalized.OutputLatencyMilliseconds * 4, 220));

        var audioBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(NesConsole.AudioSampleRate, 1))
        {
            BufferDuration = bufferDuration,
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        var waveOut = new WaveOutEvent
        {
            DesiredLatency = normalized.OutputLatencyMilliseconds,
            NumberOfBuffers = OutputBufferCount
        };

        waveOut.Init(audioBuffer);
        return (audioBuffer, waveOut);
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
