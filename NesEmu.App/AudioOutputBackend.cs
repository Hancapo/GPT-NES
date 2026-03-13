using Silk.NET.OpenAL;
using Silk.NET.SDL;
using NesEmu.Core;

namespace NesEmu.App;

internal enum AudioPlaybackState
{
    Stopped,
    Paused,
    Playing
}

internal interface IAudioOutput : IDisposable
{
    bool HasRealtimeOutput { get; }

    AudioPlaybackState PlaybackState { get; }

    TimeSpan BufferedDuration { get; }

    void WriteSamples(ReadOnlySpan<float> samples);

    void Play();

    void Pause();

    void Stop();

    void Clear();
}

internal sealed class NullAudioOutput : IAudioOutput
{
    public bool HasRealtimeOutput => false;

    public AudioPlaybackState PlaybackState => AudioPlaybackState.Stopped;

    public TimeSpan BufferedDuration => TimeSpan.Zero;

    public void WriteSamples(ReadOnlySpan<float> samples)
    {
    }

    public void Play()
    {
    }

    public void Pause()
    {
    }

    public void Stop()
    {
    }

    public void Clear()
    {
    }

    public void Dispose()
    {
    }
}

internal sealed unsafe class SdlAudioOutput : IAudioOutput
{
    private const ushort AudioS16System = 0x8010;
    private const int BufferCount = 4;
    private const int BufferSampleQuantum = 256;
    private static readonly uint RequiredInitFlags = Sdl.InitAudio;

    private readonly object _sync = new();
    private readonly Sdl _sdl;
    private readonly AudioSampleBuffer _buffer;
    private readonly float[] _renderScratch;
    private readonly short[] _pcmScratch;
    private readonly int _sampleRate;
    private readonly int _chunkSampleCount;
    private readonly int _bytesPerSample;
    private readonly uint _deviceId;

    private AudioPlaybackState _playbackState = AudioPlaybackState.Stopped;
    private bool _disposed;

    public SdlAudioOutput(int sampleRate, TimeSpan bufferDuration, int latencyMilliseconds)
    {
        _sampleRate = sampleRate;
        _chunkSampleCount = ComputeChunkSampleCount(sampleRate, latencyMilliseconds);
        _renderScratch = new float[_chunkSampleCount];
        _pcmScratch = new short[_chunkSampleCount];
        _buffer = new AudioSampleBuffer(sampleRate, bufferDuration);
        _bytesPerSample = sizeof(short);
        _sdl = Sdl.GetApi();

        if ((_sdl.WasInit(RequiredInitFlags) & RequiredInitFlags) != RequiredInitFlags
            && _sdl.InitSubSystem(RequiredInitFlags) != 0)
        {
            throw new InvalidOperationException($"Initializing SDL audio failed: {_sdl.GetErrorS()}");
        }

        var desired = new AudioSpec
        {
            Freq = sampleRate,
            Format = AudioS16System,
            Channels = 1,
            Samples = (ushort)_chunkSampleCount,
            Callback = default,
            Userdata = null
        };

        AudioSpec obtained = default;
        byte* deviceName = null;
        _deviceId = _sdl.OpenAudioDevice(deviceName, 0, &desired, &obtained, 0);
        if (_deviceId == 0)
        {
            throw new InvalidOperationException($"Opening SDL audio device failed: {_sdl.GetErrorS()}");
        }

        if (obtained.Freq != sampleRate || obtained.Channels != 1 || obtained.Format != AudioS16System)
        {
            _sdl.CloseAudioDevice(_deviceId);
            throw new InvalidOperationException(
                $"SDL audio device opened with unsupported format ({obtained.Freq} Hz, {obtained.Channels} ch, 0x{obtained.Format:X4}).");
        }
    }

    public bool HasRealtimeOutput => true;

    public AudioPlaybackState PlaybackState
    {
        get
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return AudioPlaybackState.Stopped;
                }

                RefreshPlaybackStateLocked();
                return _playbackState;
            }
        }
    }

    public TimeSpan BufferedDuration
    {
        get
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return TimeSpan.Zero;
                }

                var queuedSamples = GetQueuedSampleCountLocked();
                var totalBufferedSamples = queuedSamples + _buffer.BufferedSamples;
                return TimeSpan.FromSeconds((double)totalBufferedSamples / _sampleRate);
            }
        }
    }

    public void WriteSamples(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            ThrowIfDisposedLocked();
            _buffer.Write(samples);
            PumpQueueLocked();
        }
    }

    public void Play()
    {
        lock (_sync)
        {
            ThrowIfDisposedLocked();
            PumpQueueLocked();
            if (GetQueuedSampleCountLocked() == 0)
            {
                _playbackState = AudioPlaybackState.Stopped;
                return;
            }

            _sdl.PauseAudioDevice(_deviceId, 0);
            _playbackState = AudioPlaybackState.Playing;
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _sdl.PauseAudioDevice(_deviceId, 1);
            _playbackState = GetQueuedSampleCountLocked() > 0
                ? AudioPlaybackState.Paused
                : AudioPlaybackState.Stopped;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _sdl.PauseAudioDevice(_deviceId, 1);
            _sdl.ClearQueuedAudio(_deviceId);
            _playbackState = AudioPlaybackState.Stopped;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _sdl.PauseAudioDevice(_deviceId, 1);
            _sdl.ClearQueuedAudio(_deviceId);
            _buffer.Clear();
            _buffer.ResetRendering();
            _playbackState = AudioPlaybackState.Stopped;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _sdl.PauseAudioDevice(_deviceId, 1);
            _sdl.ClearQueuedAudio(_deviceId);
            _sdl.CloseAudioDevice(_deviceId);
            _disposed = true;
        }
    }

    private void PumpQueueLocked()
    {
        while (_buffer.BufferedSamples > 0)
        {
            QueueNextChunkLocked();
        }
    }

    private void QueueNextChunkLocked()
    {
        var sampleCount = Math.Min(_chunkSampleCount, _buffer.BufferedSamples);
        if (sampleCount <= 0)
        {
            return;
        }

        var renderSpan = _renderScratch.AsSpan(0, sampleCount);
        var copiedSamples = _buffer.ReadAvailable(renderSpan);
        if (copiedSamples <= 0)
        {
            return;
        }

        ConvertToPcm16(renderSpan, _pcmScratch);

        fixed (short* pcm = _pcmScratch)
        {
            var byteCount = (uint)(copiedSamples * _bytesPerSample);
            if (_sdl.QueueAudio(_deviceId, pcm, byteCount) != 0)
            {
                throw new InvalidOperationException($"Queueing SDL audio failed: {_sdl.GetErrorS()}");
            }
        }
    }

    private int GetQueuedSampleCountLocked()
    {
        var queuedBytes = _sdl.GetQueuedAudioSize(_deviceId);
        return (int)(queuedBytes / (uint)_bytesPerSample);
    }

    private void RefreshPlaybackStateLocked()
    {
        if (_playbackState == AudioPlaybackState.Playing && GetQueuedSampleCountLocked() == 0 && _buffer.BufferedSamples == 0)
        {
            _playbackState = AudioPlaybackState.Stopped;
        }
    }

    private void ThrowIfDisposedLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(SdlAudioOutput));
    }

    private static void ConvertToPcm16(ReadOnlySpan<float> source, Span<short> destination)
    {
        for (var i = 0; i < source.Length; i++)
        {
            var clamped = Math.Clamp(source[i], -1.0f, 1.0f);
            destination[i] = clamped <= -1.0f
                ? short.MinValue
                : (short)Math.Round(clamped * short.MaxValue);
        }
    }

    private static int ComputeChunkSampleCount(int sampleRate, int latencyMilliseconds)
    {
        var targetChunk = Math.Clamp(sampleRate * latencyMilliseconds / 1000 / BufferCount, BufferSampleQuantum, 1024);
        return ((targetChunk + (BufferSampleQuantum - 1)) / BufferSampleQuantum) * BufferSampleQuantum;
    }
}

internal sealed class OpenAlAudioOutput : IAudioOutput
{
    private const int BufferCount = 4;
    private const int BufferSampleQuantum = 256;

    private readonly object _sync = new();
    private readonly AudioContext _context;
    private readonly AL _al;
    private readonly AudioSampleBuffer _buffer;
    private readonly Queue<uint> _availableBuffers = new();
    private readonly Dictionary<uint, int> _queuedSamplesByBuffer = [];
    private readonly float[] _renderScratch;
    private readonly short[] _pcmScratch;
    private readonly int _sampleRate;
    private readonly int _chunkSampleCount;
    private readonly uint _source;
    private readonly uint[] _bufferIds;

    private AudioPlaybackState _playbackState = AudioPlaybackState.Stopped;
    private int _queuedSampleCount;
    private bool _disposed;

    public OpenAlAudioOutput(int sampleRate, TimeSpan bufferDuration, int latencyMilliseconds)
    {
        _sampleRate = sampleRate;
        _chunkSampleCount = ComputeChunkSampleCount(sampleRate, latencyMilliseconds);
        _renderScratch = new float[_chunkSampleCount];
        _pcmScratch = new short[_chunkSampleCount];
        _buffer = new AudioSampleBuffer(sampleRate, bufferDuration);
        _context = new AudioContext();
        _context.MakeCurrent();
        EnsureContextProcessing();
        _al = AL.GetApi(soft: true);
        _source = _al.GenSource();
        _bufferIds = _al.GenBuffers(BufferCount);

        foreach (var bufferId in _bufferIds)
        {
            _availableBuffers.Enqueue(bufferId);
        }
    }

    public bool HasRealtimeOutput => true;

    public AudioPlaybackState PlaybackState
    {
        get
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return AudioPlaybackState.Stopped;
                }

                PumpProcessedBuffersLocked();
                return _playbackState;
            }
        }
    }

    public TimeSpan BufferedDuration
    {
        get
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return TimeSpan.Zero;
                }

                PumpProcessedBuffersLocked();
                var totalBufferedSamples = _queuedSampleCount + _buffer.BufferedSamples;
                return TimeSpan.FromSeconds((double)totalBufferedSamples / _sampleRate);
            }
        }
    }

    public void WriteSamples(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            ThrowIfDisposedLocked();
            _buffer.Write(samples);
            PumpQueueLocked();
        }
    }

    public void Play()
    {
        lock (_sync)
        {
            ThrowIfDisposedLocked();
            PumpQueueLocked();
            if (_queuedSampleCount == 0)
            {
                _playbackState = AudioPlaybackState.Stopped;
                return;
            }

            MakeContextCurrentLocked();
            if (GetSourceStateLocked() != SourceState.Playing)
            {
                _al.SourcePlay(_source);
                ThrowOnAlErrorLocked("starting OpenAL playback");
            }

            _playbackState = AudioPlaybackState.Playing;
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            MakeContextCurrentLocked();
            if (GetSourceStateLocked() == SourceState.Playing)
            {
                _al.SourcePause(_source);
                ThrowOnAlErrorLocked("pausing OpenAL playback");
            }

            _playbackState = _queuedSampleCount > 0 ? AudioPlaybackState.Paused : AudioPlaybackState.Stopped;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            ResetSourceLocked(clearBufferedSamples: false);
            _playbackState = AudioPlaybackState.Stopped;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            ResetSourceLocked(clearBufferedSamples: true);
            _playbackState = AudioPlaybackState.Stopped;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            ResetSourceLocked(clearBufferedSamples: true);

            MakeContextCurrentLocked();
            _al.DeleteSource(_source);
            _al.DeleteBuffers(_bufferIds);
            _al.Dispose();
            _context.Dispose();
            _disposed = true;
        }
    }

    private void PumpQueueLocked()
    {
        MakeContextCurrentLocked();
        PumpProcessedBuffersLocked();

        while (_availableBuffers.Count > 0 && _buffer.BufferedSamples > 0)
        {
            QueueNextBufferLocked();
        }
    }

    private void PumpProcessedBuffersLocked()
    {
        MakeContextCurrentLocked();
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out var processedCount);
        if (processedCount > 0)
        {
            var processedBuffers = new uint[processedCount];
            _al.SourceUnqueueBuffers(_source, processedBuffers);
            ThrowOnAlErrorLocked("unqueuing OpenAL buffers");

            foreach (var bufferId in processedBuffers)
            {
                if (_queuedSamplesByBuffer.Remove(bufferId, out var queuedSamples))
                {
                    _queuedSampleCount = Math.Max(0, _queuedSampleCount - queuedSamples);
                }

                _availableBuffers.Enqueue(bufferId);
            }
        }

        _playbackState = GetSourceStateLocked() switch
        {
            SourceState.Playing => AudioPlaybackState.Playing,
            SourceState.Paused => AudioPlaybackState.Paused,
            _ => AudioPlaybackState.Stopped
        };
    }

    private unsafe void QueueNextBufferLocked()
    {
        var bufferId = _availableBuffers.Dequeue();
        var renderSpan = _renderScratch.AsSpan(0, _chunkSampleCount);
        _buffer.Render(renderSpan);
        ConvertToPcm16(renderSpan, _pcmScratch);

        fixed (short* pcm = _pcmScratch)
        {
            _al.BufferData(
                bufferId,
                BufferFormat.Mono16,
                pcm,
                _chunkSampleCount * sizeof(short),
                _sampleRate);
        }

        ThrowOnAlErrorLocked("uploading OpenAL buffer data");
        _al.SourceQueueBuffers(_source, [bufferId]);
        ThrowOnAlErrorLocked("queueing OpenAL buffer");
        _queuedSamplesByBuffer[bufferId] = _chunkSampleCount;
        _queuedSampleCount += _chunkSampleCount;
    }

    private void ResetSourceLocked(bool clearBufferedSamples)
    {
        MakeContextCurrentLocked();
        _al.SourceStop(_source);
        ThrowOnAlErrorLocked("stopping OpenAL source");

        _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out var queuedBufferCount);
        if (queuedBufferCount > 0)
        {
            var queuedBuffers = new uint[queuedBufferCount];
            _al.SourceUnqueueBuffers(_source, queuedBuffers);
            ThrowOnAlErrorLocked("resetting OpenAL buffer queue");

            foreach (var bufferId in queuedBuffers)
            {
                if (!_queuedSamplesByBuffer.Remove(bufferId))
                {
                    continue;
                }

                _availableBuffers.Enqueue(bufferId);
            }
        }

        _queuedSamplesByBuffer.Clear();
        _queuedSampleCount = 0;

        if (clearBufferedSamples)
        {
            _buffer.Clear();
            _buffer.ResetRendering();
        }
    }

    private void MakeContextCurrentLocked()
    {
        _context.MakeCurrent();
        EnsureContextProcessing();
    }

    private SourceState GetSourceStateLocked()
    {
        _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out var rawSourceState);
        return Enum.IsDefined(typeof(SourceState), rawSourceState)
            ? (SourceState)rawSourceState
            : SourceState.Stopped;
    }

    private void ThrowOnAlErrorLocked(string operation)
    {
        var error = _al.GetError();
        if (error != AudioError.NoError)
        {
            throw new InvalidOperationException($"{operation} failed: {error}.");
        }
    }

    private void ThrowIfDisposedLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(OpenAlAudioOutput));
    }

    private static int ComputeChunkSampleCount(int sampleRate, int latencyMilliseconds)
    {
        var targetChunk = Math.Clamp(sampleRate * latencyMilliseconds / 1000 / BufferCount, BufferSampleQuantum, 1024);
        return ((targetChunk + (BufferSampleQuantum - 1)) / BufferSampleQuantum) * BufferSampleQuantum;
    }

    private void EnsureContextProcessing()
    {
        if (!_context.IsProcessing)
        {
            _context.Process();
        }
    }

    private static void ConvertToPcm16(ReadOnlySpan<float> source, Span<short> destination)
    {
        for (var i = 0; i < source.Length; i++)
        {
            var clamped = Math.Clamp(source[i], -1.0f, 1.0f);
            destination[i] = clamped <= -1.0f
                ? short.MinValue
                : (short)Math.Round(clamped * short.MaxValue);
        }
    }
}
