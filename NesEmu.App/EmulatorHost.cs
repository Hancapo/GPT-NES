using System.Diagnostics;
using NAudio.Wave;
using NesEmu.Core;
using NesEmu.Core.Cartridge;

namespace NesEmu.App;

public sealed class EmulatorHost : IDisposable
{
    private static readonly TimeSpan FrameDuration = TimeSpan.FromSeconds(1.0 / 60.0988138974405);
    private static readonly TimeSpan ResumeBufferedDuration = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan MinimumBufferedDuration = TimeSpan.FromMilliseconds(45);

    private readonly Func<ControllerState> _inputProvider;
    private readonly Action<uint[]> _frameCallback;
    private readonly MidiOutputService _midiOutput;
    private readonly BufferedWaveProvider _audioBuffer;
    private readonly WaveOutEvent _waveOut;
    private readonly float[] _audioSampleScratch = new float[4096];
    private byte[] _audioByteScratch = new byte[4096 * sizeof(float)];
    private readonly uint[] _videoFrameScratch = new uint[NesVideoConstants.PixelsPerFrame];

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

        _audioBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(NesConsole.AudioSampleRate, 1))
        {
            BufferDuration = TimeSpan.FromMilliseconds(250),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        _waveOut = new WaveOutEvent
        {
            DesiredLatency = 100
        };
        _waveOut.Init(_audioBuffer);
    }

    public bool IsPaused => _paused;

    public bool IsStopped => _stopped;

    public string? LoadedRomPath { get; private set; }

    public bool HasRomLoaded => _console is not null;

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

            if (bufferedDuration >= MinimumBufferedDuration && remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken);
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

            Buffer.BlockCopy(_audioSampleScratch, 0, _audioByteScratch, 0, byteCount);
            _audioBuffer.AddSamples(_audioByteScratch, 0, byteCount);
        }
    }

    private void PrimePlaybackIfReady()
    {
        if (_playbackPrimed || _audioBuffer.BufferedDuration < ResumeBufferedDuration)
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
            return bufferedDuration >= ResumeBufferedDuration ? bufferedDuration : ResumeBufferedDuration;
        }

        return bufferedDuration;
    }
}
