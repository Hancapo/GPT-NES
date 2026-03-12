using System.Diagnostics;
using System.Threading;
using NAudio.Midi;
using NesEmu.Core;

namespace NesEmu.App;

public sealed class MidiOutputService : IDisposable
{
    private const int Pulse1MidiChannel = 1;
    private const int Pulse2MidiChannel = 2;
    private const int TriangleMidiChannel = 3;
    private const int PercussionMidiChannel = 10;
    private const int PitchBendCenter = 8192;
    private const int PitchBendRangeSemitones = 2;
    private const int StableNoteFrames = 1;
    private const int LargeJumpStableFrames = 2;
    private const int NoteOffGraceFrames = 1;
    private const int PercussionReleaseFrames = 2;
    private const int RecentMusicWindowFrames = 30;
    private static readonly TimeSpan FrameLeadCompensation = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan MaximumMidiSyncDelay = TimeSpan.FromMilliseconds(80);

    private readonly object _sync = new();
    private readonly MelodicTracker _pulse1Tracker = new("Pulse 1", Pulse1MidiChannel);
    private readonly MelodicTracker _pulse2Tracker = new("Pulse 2", Pulse2MidiChannel);
    private readonly MelodicTracker _triangleTracker = new("Triangle", TriangleMidiChannel);
    private readonly PercussionTracker _noiseTracker = new();
    private readonly PercussionTracker _dmcTracker = new();
    private readonly List<ActivePercussionHit> _activePercussionHits = [];
    private readonly PriorityQueue<ScheduledMidiMessage, long> _scheduledMessages = new();
    private readonly AutoResetEvent _dispatchSignal = new(false);
    private readonly CancellationTokenSource _dispatchCancellation = new();
    private readonly Stopwatch _schedulerClock = Stopwatch.StartNew();
    private readonly Task _dispatchTask;

    private MidiOutputSettings _settings = MidiOutputSettings.CreateDefault();
    private MidiOut? _midiOut;
    private int _framesSinceMelodicActivity = int.MaxValue;
    private TimeSpan _presentationLatency = TimeSpan.Zero;
    private TimeSpan _performanceSendDelay = TimeSpan.Zero;
    private bool _disposed;

    public MidiOutputService()
    {
        _dispatchTask = Task.Run(() => DispatchLoop(_dispatchCancellation.Token));
    }

    public IReadOnlyList<MidiOutputDeviceInfo> GetDevices()
    {
        var devices = new List<MidiOutputDeviceInfo>
        {
            new(-1, "No MIDI output")
        };

        int deviceCount;
        try
        {
            deviceCount = MidiOut.NumberOfDevices;
        }
        catch
        {
            return devices;
        }

        for (var i = 0; i < deviceCount; i++)
        {
            try
            {
                var capabilities = MidiOut.DeviceInfo(i);
                var displayName = string.IsNullOrWhiteSpace(capabilities.ProductName)
                    ? $"MIDI Device {i + 1}"
                    : capabilities.ProductName;
                devices.Add(new MidiOutputDeviceInfo(i, displayName));
            }
            catch
            {
                devices.Add(new MidiOutputDeviceInfo(i, $"MIDI Device {i + 1}"));
            }
        }

        return devices;
    }

    public MidiOutputSettings GetSettingsSnapshot()
    {
        lock (_sync)
        {
            return _settings.Clone();
        }
    }

    public string GetStatusText()
    {
        lock (_sync)
        {
            if (!_settings.Enabled || _settings.DeviceIndex < 0)
            {
                return "MIDI disabled";
            }

            return $"MIDI: {ResolveDeviceName(_settings.DeviceIndex)}";
        }
    }

    public bool TryApplySettings(MidiOutputSettings settings, out string? error)
    {
        lock (_sync)
        {
            error = null;

            SilenceLocked();
            CloseDeviceLocked();
            ResetTrackersLocked();

            _settings = settings.Clone();
            if (!_settings.Enabled)
            {
                return true;
            }

            if (_settings.DeviceIndex < 0)
            {
                _settings.Enabled = false;
                error = "Select a MIDI output before enabling the conversion.";
                return false;
            }

            try
            {
                _midiOut = new MidiOut(_settings.DeviceIndex);
                ConfigureDeviceLocked();
                return true;
            }
            catch (Exception ex)
            {
                _settings.Enabled = false;
                error = ex.Message;
                CloseDeviceLocked();
                return false;
            }
        }
    }

    public void SetPresentationLatency(TimeSpan latency)
    {
        lock (_sync)
        {
            _presentationLatency = ClampLatency(latency);
        }
    }

    public void ProcessFrame(ApuTapSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_midiOut is null || !_settings.Enabled)
            {
                return;
            }

            _performanceSendDelay = CalculatePerformanceSendDelayLocked();
            try
            {
                ProcessPulseVoiceLocked(_pulse1Tracker, snapshot.Pulse1, _settings.Pulse1Enabled, _settings.Pulse1VolumePercent);
                ProcessPulseVoiceLocked(_pulse2Tracker, snapshot.Pulse2, _settings.Pulse2Enabled, _settings.Pulse2VolumePercent);
                ProcessTriangleVoiceLocked(_triangleTracker, snapshot.Triangle, _settings.TriangleEnabled);

                var melodicActive = _pulse1Tracker.IsNoteOn || _pulse2Tracker.IsNoteOn || _triangleTracker.IsNoteOn;
                _framesSinceMelodicActivity = melodicActive
                    ? 0
                    : Math.Min(_framesSinceMelodicActivity + 1, RecentMusicWindowFrames + 1);

                var allowPercussion = _settings.SendPercussion
                    && (!_settings.MusicOnlyFilter || _framesSinceMelodicActivity < RecentMusicWindowFrames);

                ProcessNoisePercussionLocked(snapshot.Noise, _settings.NoiseEnabled && allowPercussion);
                ProcessDmcPercussionLocked(snapshot.Dmc, _settings.DmcEnabled && allowPercussion);
                AdvancePercussionLocked();
            }
            finally
            {
                _performanceSendDelay = TimeSpan.Zero;
            }
        }
    }

    public void Silence()
    {
        lock (_sync)
        {
            SilenceLocked();
            ResetTrackersLocked();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SilenceLocked();
            _dispatchCancellation.Cancel();
            _dispatchSignal.Set();
            CloseDeviceLocked();
        }

        try
        {
            _dispatchTask.Wait();
        }
        catch (AggregateException)
        {
        }
        finally
        {
            _dispatchCancellation.Dispose();
            _dispatchSignal.Dispose();
        }
    }

    private void ProcessPulseVoiceLocked(MelodicTracker tracker, PulseTapSnapshot voice, bool enabled, int levelPercent)
    {
        var frequency = voice.Audible
            ? NesConsole.CpuFrequency / (16.0 * (voice.TimerPeriod + 1))
            : 0.0;
        var baseVelocity = ToVelocity(voice.Volume);
        var noteVelocity = ScaleByPercent(baseVelocity, levelPercent);
        var expression = ScaleByPercent(baseVelocity, levelPercent);

        ProcessMelodicVoiceLocked(
            tracker,
            enabled && voice.Audible && levelPercent > 0,
            frequency,
            noteVelocity,
            expression,
            voice.TriggerVersion);
    }

    private void ProcessTriangleVoiceLocked(MelodicTracker tracker, TriangleTapSnapshot voice, bool enabled)
    {
        var levelPercent = _settings.TriangleVolumePercent;
        var frequency = voice.Audible
            ? NesConsole.CpuFrequency / (32.0 * (voice.TimerPeriod + 1))
            : 0.0;
        var baseVelocity = Math.Max(ToVelocity(voice.Volume), 84);
        var noteVelocity = ScaleByPercent(baseVelocity, levelPercent);
        var expression = ScaleByPercent(baseVelocity, levelPercent);

        ProcessMelodicVoiceLocked(
            tracker,
            enabled && voice.Audible && levelPercent > 0,
            frequency,
            noteVelocity,
            expression,
            voice.TriggerVersion);
    }

    private void ProcessMelodicVoiceLocked(MelodicTracker tracker, bool audible, double frequency, int noteVelocity, int expression, int triggerVersion)
    {
        if (!audible || frequency <= 0.0)
        {
            tracker.Candidate = null;
            if (!tracker.IsNoteOn)
            {
                return;
            }

            tracker.SilentFrames++;
            if (!_settings.MusicOnlyFilter || tracker.SilentFrames >= NoteOffGraceFrames)
            {
                StopNoteLocked(tracker);
            }

            return;
        }

        tracker.SilentFrames = 0;
        var target = CreatePitchTarget(frequency, expression);
        if (tracker.IsNoteOn && tracker.CurrentNote == target.NoteNumber)
        {
            tracker.Candidate = null;
            SendPitchBendLocked(tracker.Channel, target.PitchBend);
            SendExpressionLocked(tracker.Channel, target.Expression);
            tracker.CurrentPitchBend = target.PitchBend;
            tracker.CurrentExpression = target.Expression;
            tracker.LastObservedTriggerVersion = triggerVersion;
            return;
        }

        if (tracker.Candidate is { } candidate && candidate.Matches(target))
        {
            candidate.StableFrames++;
            tracker.Candidate = candidate;
        }
        else
        {
            tracker.Candidate = new CandidatePitchTarget(target.NoteNumber, target.PitchBend, target.Expression, 1);
        }

        tracker.LastObservedTriggerVersion = triggerVersion;

        var requiredFrames = !_settings.MusicOnlyFilter
            ? 1
            : tracker.IsNoteOn && tracker.CurrentNote >= 0 && Math.Abs(tracker.Candidate.NoteNumber - tracker.CurrentNote) >= 7
                ? LargeJumpStableFrames
                : StableNoteFrames;

        if (tracker.Candidate.StableFrames < requiredFrames)
        {
            return;
        }

        if (tracker.IsNoteOn)
        {
            SendNoteOffLocked(tracker.Channel, tracker.CurrentNote);
        }

        SendPitchBendLocked(tracker.Channel, tracker.Candidate.PitchBend);
        SendNoteOnLocked(tracker.Channel, tracker.Candidate.NoteNumber, noteVelocity);
        SendExpressionLocked(tracker.Channel, tracker.Candidate.Expression);

        tracker.CurrentNote = tracker.Candidate.NoteNumber;
        tracker.CurrentPitchBend = tracker.Candidate.PitchBend;
        tracker.CurrentExpression = tracker.Candidate.Expression;
        tracker.IsNoteOn = true;
        tracker.Candidate = null;
    }

    private void ProcessNoisePercussionLocked(NoiseTapSnapshot snapshot, bool enabled)
    {
        if (!enabled || !snapshot.Enabled || !snapshot.Audible)
        {
            _noiseTracker.LastTriggerVersion = snapshot.TriggerVersion;
            return;
        }

        if (snapshot.TriggerVersion == _noiseTracker.LastTriggerVersion)
        {
            return;
        }

        _noiseTracker.LastTriggerVersion = snapshot.TriggerVersion;
        var velocity = ScaleByPercent(ToVelocity(snapshot.Volume), _settings.NoiseVolumePercent);
        if (velocity <= 0)
        {
            return;
        }

        TriggerPercussionLocked(ResolveNoiseDrumNote(snapshot), velocity);
    }

    private void ProcessDmcPercussionLocked(DmcTapSnapshot snapshot, bool enabled)
    {
        if (!enabled || !snapshot.Active)
        {
            _dmcTracker.LastTriggerVersion = snapshot.TriggerVersion;
            return;
        }

        if (snapshot.TriggerVersion == _dmcTracker.LastTriggerVersion)
        {
            return;
        }

        _dmcTracker.LastTriggerVersion = snapshot.TriggerVersion;
        var baseVelocity = Math.Clamp(Math.Max(snapshot.OutputLevel, 40), 1, 127);
        var velocity = ScaleByPercent(baseVelocity, _settings.DmcVolumePercent);
        if (velocity <= 0)
        {
            return;
        }

        TriggerPercussionLocked(ResolveDmcDrumNote(snapshot), velocity);
    }

    private void TriggerPercussionLocked(int noteNumber, int velocity)
    {
        if (_midiOut is null)
        {
            return;
        }

        noteNumber = Math.Clamp(noteNumber, 0, 127);
        velocity = Math.Clamp(velocity, 1, 127);

        for (var i = _activePercussionHits.Count - 1; i >= 0; i--)
        {
            if (_activePercussionHits[i].NoteNumber != noteNumber)
            {
                continue;
            }

            SendNoteOffLocked(PercussionMidiChannel, noteNumber);
            _activePercussionHits.RemoveAt(i);
        }

        SendNoteOnLocked(PercussionMidiChannel, noteNumber, velocity);
        _activePercussionHits.Add(new ActivePercussionHit(noteNumber, PercussionReleaseFrames));
    }

    private void AdvancePercussionLocked()
    {
        if (_midiOut is null)
        {
            return;
        }

        for (var i = _activePercussionHits.Count - 1; i >= 0; i--)
        {
            var hit = _activePercussionHits[i];
            hit.FramesRemaining--;
            if (hit.FramesRemaining > 0)
            {
                _activePercussionHits[i] = hit;
                continue;
            }

            SendNoteOffLocked(PercussionMidiChannel, hit.NoteNumber);
            _activePercussionHits.RemoveAt(i);
        }
    }

    private void ConfigureDeviceLocked()
    {
        if (_midiOut is null)
        {
            return;
        }

        ConfigureMelodicChannelLocked(Pulse1MidiChannel, _settings.Pulse1Program);
        ConfigureMelodicChannelLocked(Pulse2MidiChannel, _settings.Pulse2Program);
        ConfigureMelodicChannelLocked(TriangleMidiChannel, _settings.TriangleProgram);
        SendChannelVolumeLocked(PercussionMidiChannel, 127);
        SendExpressionLocked(PercussionMidiChannel, 127);
        SendAllNotesOffLocked(PercussionMidiChannel);
    }

    private void ConfigureMelodicChannelLocked(int channel, int patch)
    {
        SendPitchBendRangeLocked(channel, PitchBendRangeSemitones);
        SendChannelVolumeLocked(channel, 127);
        SendPitchBendLocked(channel, PitchBendCenter);
        _midiOut!.Send(MidiMessage.ChangePatch(Math.Clamp(patch, 0, 127), channel).RawData);
        SendExpressionLocked(channel, 127);
        SendAllNotesOffLocked(channel);
    }

    private void SendPitchBendRangeLocked(int channel, int semitones)
    {
        SendControlChangeLocked(channel, 101, 0);
        SendControlChangeLocked(channel, 100, 0);
        SendControlChangeLocked(channel, 6, semitones);
        SendControlChangeLocked(channel, 38, 0);
        SendControlChangeLocked(channel, 101, 127);
        SendControlChangeLocked(channel, 100, 127);
    }

    private void SendControlChangeLocked(int channel, int controller, int value)
    {
        QueueOrSendMessageLocked(MidiMessage.ChangeControl(controller, value, channel).RawData, immediate: _performanceSendDelay <= TimeSpan.Zero);
    }

    private void SendChannelVolumeLocked(int channel, int value)
    {
        SendControlChangeLocked(channel, 7, Math.Clamp(value, 0, 127));
    }

    private void SendExpressionLocked(int channel, int expression)
    {
        SendControlChangeLocked(channel, 11, Math.Clamp(expression, 0, 127));
    }

    private void SendPitchBendLocked(int channel, int value)
    {
        if (_midiOut is null)
        {
            return;
        }

        value = Math.Clamp(value, 0, 16_383);
        var status = 0xE0 | ((channel - 1) & 0x0F);
        var message = status | ((value & 0x7F) << 8) | (((value >> 7) & 0x7F) << 16);
        QueueOrSendMessageLocked(message, immediate: _performanceSendDelay <= TimeSpan.Zero);
    }

    private void SendNoteOnLocked(int channel, int noteNumber, int velocity)
    {
        QueueOrSendMessageLocked(MidiMessage.StartNote(noteNumber, velocity, channel).RawData, immediate: _performanceSendDelay <= TimeSpan.Zero);
    }

    private void SendNoteOffLocked(int channel, int noteNumber)
    {
        QueueOrSendMessageLocked(MidiMessage.StopNote(noteNumber, 0, channel).RawData, immediate: _performanceSendDelay <= TimeSpan.Zero);
    }

    private void StopNoteLocked(MelodicTracker tracker)
    {
        if (!tracker.IsNoteOn)
        {
            return;
        }

        SendNoteOffLocked(tracker.Channel, tracker.CurrentNote);
        SendPitchBendLocked(tracker.Channel, PitchBendCenter);

        tracker.IsNoteOn = false;
        tracker.CurrentNote = -1;
        tracker.CurrentPitchBend = PitchBendCenter;
        tracker.CurrentExpression = 0;
        tracker.SilentFrames = 0;
        tracker.Candidate = null;
    }

    private void SilenceLocked()
    {
        ClearScheduledMessagesLocked();

        if (_midiOut is null)
        {
            return;
        }

        foreach (var tracker in EnumerateMelodicTrackers())
        {
            if (tracker.IsNoteOn)
            {
                SendNoteOffLocked(tracker.Channel, tracker.CurrentNote);
            }

            SendPitchBendLocked(tracker.Channel, PitchBendCenter);
            SendAllNotesOffLocked(tracker.Channel);
        }

        for (var i = _activePercussionHits.Count - 1; i >= 0; i--)
        {
            SendNoteOffLocked(PercussionMidiChannel, _activePercussionHits[i].NoteNumber);
        }

        SendAllNotesOffLocked(PercussionMidiChannel);
        _activePercussionHits.Clear();
    }

    private void SendAllNotesOffLocked(int channel)
    {
        SendControlChangeLocked(channel, 120, 0);
        SendControlChangeLocked(channel, 123, 0);
    }

    private void ResetTrackersLocked()
    {
        foreach (var tracker in EnumerateMelodicTrackers())
        {
            tracker.Reset();
        }

        _noiseTracker.LastTriggerVersion = 0;
        _dmcTracker.LastTriggerVersion = 0;
        _activePercussionHits.Clear();
        _framesSinceMelodicActivity = int.MaxValue;
    }

    private void CloseDeviceLocked()
    {
        ClearScheduledMessagesLocked();
        _midiOut?.Dispose();
        _midiOut = null;
    }

    private TimeSpan CalculatePerformanceSendDelayLocked()
    {
        if (_presentationLatency <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var compensated = _presentationLatency - FrameLeadCompensation;
        compensated += TimeSpan.FromMilliseconds(_settings.MidiSyncOffsetMilliseconds);
        return compensated > TimeSpan.Zero ? ClampLatency(compensated) : TimeSpan.Zero;
    }

    private void QueueOrSendMessageLocked(int rawData, bool immediate)
    {
        if (_midiOut is null)
        {
            return;
        }

        if (immediate)
        {
            _midiOut.Send(rawData);
            return;
        }

        var dueTicks = _schedulerClock.ElapsedTicks + (long)(_performanceSendDelay.TotalSeconds * Stopwatch.Frequency);
        _scheduledMessages.Enqueue(new ScheduledMidiMessage(rawData), dueTicks);
        _dispatchSignal.Set();
    }

    private void ClearScheduledMessagesLocked()
    {
        _scheduledMessages.Clear();
    }

    private void DispatchLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            FlushDueMessages();

            var waitMilliseconds = GetWaitMilliseconds();
            _dispatchSignal.WaitOne(waitMilliseconds);
        }

        FlushDueMessages();
    }

    private void FlushDueMessages()
    {
        while (true)
        {
            int? rawData = null;

            lock (_sync)
            {
                if (_midiOut is null || _scheduledMessages.Count == 0)
                {
                    return;
                }

                var nowTicks = _schedulerClock.ElapsedTicks;
                if (!_scheduledMessages.TryPeek(out var nextMessage, out var dueTicks) || dueTicks > nowTicks)
                {
                    return;
                }

                _scheduledMessages.Dequeue();
                rawData = nextMessage.RawData;
            }

            if (rawData.HasValue)
            {
                lock (_sync)
                {
                    _midiOut?.Send(rawData.Value);
                }
            }
        }
    }

    private int GetWaitMilliseconds()
    {
        lock (_sync)
        {
            if (_scheduledMessages.Count == 0)
            {
                return 8;
            }

            var nowTicks = _schedulerClock.ElapsedTicks;
            if (!_scheduledMessages.TryPeek(out _, out var dueTicks))
            {
                return 8;
            }

            var ticksUntilDue = dueTicks - nowTicks;
            if (ticksUntilDue <= 0)
            {
                return 0;
            }

            var milliseconds = (int)Math.Ceiling(ticksUntilDue * 1000.0 / Stopwatch.Frequency);
            return Math.Clamp(milliseconds, 1, 8);
        }
    }

    private IEnumerable<MelodicTracker> EnumerateMelodicTrackers()
    {
        yield return _pulse1Tracker;
        yield return _pulse2Tracker;
        yield return _triangleTracker;
    }

    private int ResolveNoiseDrumNote(NoiseTapSnapshot snapshot)
    {
        if (_settings.NoiseDrumNote >= 0)
        {
            return _settings.NoiseDrumNote;
        }

        return snapshot.PeriodIndex switch
        {
            <= 2 => snapshot.LoopMode ? 44 : 42,
            <= 7 => 38,
            <= 11 => 39,
            _ => 41
        };
    }

    private int ResolveDmcDrumNote(DmcTapSnapshot snapshot)
    {
        if (_settings.DmcDrumNote >= 0)
        {
            return _settings.DmcDrumNote;
        }

        return snapshot.RateIndex switch
        {
            <= 3 => 36,
            <= 7 => 45,
            <= 11 => 38,
            _ => 42
        };
    }

    private static int ToVelocity(int volume)
    {
        return Math.Clamp(24 + (volume * 6), 1, 127);
    }

    private static int ScaleByPercent(int value, int percent)
    {
        return Math.Clamp((int)Math.Round(value * (percent / 100.0)), 0, 127);
    }

    private static TimeSpan ClampLatency(TimeSpan latency)
    {
        if (latency <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return latency <= MaximumMidiSyncDelay ? latency : MaximumMidiSyncDelay;
    }

    private static PitchTarget CreatePitchTarget(double frequency, int expression)
    {
        var midiValue = 69.0 + (12.0 * Math.Log2(frequency / 440.0));
        var roundedNote = (int)Math.Round(midiValue);
        var clampedNote = Math.Clamp(roundedNote, 0, 127);
        var semitoneOffset = Math.Clamp(midiValue - clampedNote, -PitchBendRangeSemitones, PitchBendRangeSemitones);
        var pitchBend = PitchBendCenter + (int)Math.Round((semitoneOffset / PitchBendRangeSemitones) * PitchBendCenter);
        return new PitchTarget(clampedNote, Math.Clamp(pitchBend, 0, 16_383), Math.Clamp(expression, 1, 127));
    }

    private string ResolveDeviceName(int deviceIndex)
    {
        return GetDevices().FirstOrDefault(device => device.DeviceIndex == deviceIndex)?.DisplayName ?? "Unknown device";
    }

    private sealed class MelodicTracker
    {
        public MelodicTracker(string name, int channel)
        {
            Name = name;
            Channel = channel;
            Reset();
        }

        public string Name { get; }

        public int Channel { get; }

        public bool IsNoteOn { get; set; }

        public int CurrentNote { get; set; }

        public int CurrentPitchBend { get; set; }

        public int CurrentExpression { get; set; }

        public int SilentFrames { get; set; }

        public int LastObservedTriggerVersion { get; set; }

        public CandidatePitchTarget? Candidate { get; set; }

        public void Reset()
        {
            IsNoteOn = false;
            CurrentNote = -1;
            CurrentPitchBend = PitchBendCenter;
            CurrentExpression = 0;
            SilentFrames = 0;
            LastObservedTriggerVersion = 0;
            Candidate = null;
        }
    }

    private sealed class PercussionTracker
    {
        public int LastTriggerVersion { get; set; }
    }

    private sealed record PitchTarget(int NoteNumber, int PitchBend, int Expression);

    private sealed class CandidatePitchTarget
    {
        public CandidatePitchTarget(int noteNumber, int pitchBend, int expression, int stableFrames)
        {
            NoteNumber = noteNumber;
            PitchBend = pitchBend;
            Expression = expression;
            StableFrames = stableFrames;
        }

        public int NoteNumber { get; }

        public int PitchBend { get; }

        public int Expression { get; }

        public int StableFrames { get; set; }

        public bool Matches(PitchTarget other)
        {
            return NoteNumber == other.NoteNumber
                && Math.Abs(PitchBend - other.PitchBend) <= 96
                && Math.Abs(Expression - other.Expression) <= 12;
        }
    }

    private sealed class ActivePercussionHit
    {
        public ActivePercussionHit(int noteNumber, int framesRemaining)
        {
            NoteNumber = noteNumber;
            FramesRemaining = framesRemaining;
        }

        public int NoteNumber { get; }

        public int FramesRemaining { get; set; }
    }

    private readonly record struct ScheduledMidiMessage(int RawData);
}

public sealed class MidiOutputSettings
{
    public bool Enabled { get; set; }

    public int DeviceIndex { get; set; } = -1;

    public bool MusicOnlyFilter { get; set; } = true;

    public bool SendPercussion { get; set; } = true;

    public bool Pulse1Enabled { get; set; } = true;

    public bool Pulse2Enabled { get; set; } = true;

    public bool TriangleEnabled { get; set; } = true;

    public bool NoiseEnabled { get; set; } = true;

    public bool DmcEnabled { get; set; } = true;

    public int Pulse1Program { get; set; } = 80;

    public int Pulse2Program { get; set; } = 81;

    public int TriangleProgram { get; set; } = 33;

    public int Pulse1VolumePercent { get; set; } = 100;

    public int Pulse2VolumePercent { get; set; } = 100;

    public int TriangleVolumePercent { get; set; } = 100;

    public int NoiseVolumePercent { get; set; } = 45;

    public int DmcVolumePercent { get; set; } = 40;

    public int NoiseDrumNote { get; set; } = -1;

    public int DmcDrumNote { get; set; } = -1;

    public int MidiSyncOffsetMilliseconds { get; set; }

    public static MidiOutputSettings CreateDefault() => new();

    public MidiOutputSettings Clone()
    {
        return new MidiOutputSettings
        {
            Enabled = Enabled,
            DeviceIndex = DeviceIndex,
            MusicOnlyFilter = MusicOnlyFilter,
            SendPercussion = SendPercussion,
            Pulse1Enabled = Pulse1Enabled,
            Pulse2Enabled = Pulse2Enabled,
            TriangleEnabled = TriangleEnabled,
            NoiseEnabled = NoiseEnabled,
            DmcEnabled = DmcEnabled,
            Pulse1Program = Pulse1Program,
            Pulse2Program = Pulse2Program,
            TriangleProgram = TriangleProgram,
            Pulse1VolumePercent = Pulse1VolumePercent,
            Pulse2VolumePercent = Pulse2VolumePercent,
            TriangleVolumePercent = TriangleVolumePercent,
            NoiseVolumePercent = NoiseVolumePercent,
            DmcVolumePercent = DmcVolumePercent,
            NoiseDrumNote = NoiseDrumNote,
            DmcDrumNote = DmcDrumNote,
            MidiSyncOffsetMilliseconds = MidiSyncOffsetMilliseconds
        };
    }
}

public sealed record MidiOutputDeviceInfo(int DeviceIndex, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record MidiProgramOption(int ProgramNumber, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record MidiPercussionOption(int NoteNumber, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public static class MidiCatalog
{
    public static IReadOnlyList<MidiProgramOption> Programs { get; } = BuildProgramOptions();

    public static IReadOnlyList<MidiPercussionOption> PercussionNotes { get; } =
    [
        new MidiPercussionOption(-1, "Auto"),
        new MidiPercussionOption(35, "Acoustic Bass Drum"),
        new MidiPercussionOption(36, "Bass Drum 1"),
        new MidiPercussionOption(37, "Side Stick"),
        new MidiPercussionOption(38, "Acoustic Snare"),
        new MidiPercussionOption(39, "Hand Clap"),
        new MidiPercussionOption(40, "Electric Snare"),
        new MidiPercussionOption(41, "Low Floor Tom"),
        new MidiPercussionOption(42, "Closed Hi-Hat"),
        new MidiPercussionOption(44, "Pedal Hi-Hat"),
        new MidiPercussionOption(45, "Low Tom"),
        new MidiPercussionOption(46, "Open Hi-Hat"),
        new MidiPercussionOption(49, "Crash Cymbal 1")
    ];

    private static IReadOnlyList<MidiProgramOption> BuildProgramOptions()
    {
        string[] names =
        [
            "000 Acoustic Grand Piano",
            "001 Bright Acoustic Piano",
            "002 Electric Grand Piano",
            "003 Honky-tonk Piano",
            "004 Electric Piano 1",
            "005 Electric Piano 2",
            "006 Harpsichord",
            "007 Clavinet",
            "008 Celesta",
            "009 Glockenspiel",
            "010 Music Box",
            "011 Vibraphone",
            "012 Marimba",
            "013 Xylophone",
            "014 Tubular Bells",
            "015 Dulcimer",
            "016 Drawbar Organ",
            "017 Percussive Organ",
            "018 Rock Organ",
            "019 Church Organ",
            "020 Reed Organ",
            "021 Accordion",
            "022 Harmonica",
            "023 Tango Accordion",
            "024 Acoustic Guitar (nylon)",
            "025 Acoustic Guitar (steel)",
            "026 Electric Guitar (jazz)",
            "027 Electric Guitar (clean)",
            "028 Electric Guitar (muted)",
            "029 Overdriven Guitar",
            "030 Distortion Guitar",
            "031 Guitar Harmonics",
            "032 Acoustic Bass",
            "033 Electric Bass (finger)",
            "034 Electric Bass (pick)",
            "035 Fretless Bass",
            "036 Slap Bass 1",
            "037 Slap Bass 2",
            "038 Synth Bass 1",
            "039 Synth Bass 2",
            "040 Violin",
            "041 Viola",
            "042 Cello",
            "043 Contrabass",
            "044 Tremolo Strings",
            "045 Pizzicato Strings",
            "046 Orchestral Harp",
            "047 Timpani",
            "048 String Ensemble 1",
            "049 String Ensemble 2",
            "050 SynthStrings 1",
            "051 SynthStrings 2",
            "052 Choir Aahs",
            "053 Voice Oohs",
            "054 Synth Voice",
            "055 Orchestra Hit",
            "056 Trumpet",
            "057 Trombone",
            "058 Tuba",
            "059 Muted Trumpet",
            "060 French Horn",
            "061 Brass Section",
            "062 SynthBrass 1",
            "063 SynthBrass 2",
            "064 Soprano Sax",
            "065 Alto Sax",
            "066 Tenor Sax",
            "067 Baritone Sax",
            "068 Oboe",
            "069 English Horn",
            "070 Bassoon",
            "071 Clarinet",
            "072 Piccolo",
            "073 Flute",
            "074 Recorder",
            "075 Pan Flute",
            "076 Blown Bottle",
            "077 Shakuhachi",
            "078 Whistle",
            "079 Ocarina",
            "080 Lead 1 (square)",
            "081 Lead 2 (sawtooth)",
            "082 Lead 3 (calliope)",
            "083 Lead 4 (chiff)",
            "084 Lead 5 (charang)",
            "085 Lead 6 (voice)",
            "086 Lead 7 (fifths)",
            "087 Lead 8 (bass + lead)",
            "088 Pad 1 (new age)",
            "089 Pad 2 (warm)",
            "090 Pad 3 (polysynth)",
            "091 Pad 4 (choir)",
            "092 Pad 5 (bowed)",
            "093 Pad 6 (metallic)",
            "094 Pad 7 (halo)",
            "095 Pad 8 (sweep)",
            "096 FX 1 (rain)",
            "097 FX 2 (soundtrack)",
            "098 FX 3 (crystal)",
            "099 FX 4 (atmosphere)",
            "100 FX 5 (brightness)",
            "101 FX 6 (goblins)",
            "102 FX 7 (echoes)",
            "103 FX 8 (sci-fi)",
            "104 Sitar",
            "105 Banjo",
            "106 Shamisen",
            "107 Koto",
            "108 Kalimba",
            "109 Bagpipe",
            "110 Fiddle",
            "111 Shanai",
            "112 Tinkle Bell",
            "113 Agogo",
            "114 Steel Drums",
            "115 Woodblock",
            "116 Taiko Drum",
            "117 Melodic Tom",
            "118 Synth Drum",
            "119 Reverse Cymbal",
            "120 Guitar Fret Noise",
            "121 Breath Noise",
            "122 Seashore",
            "123 Bird Tweet",
            "124 Telephone Ring",
            "125 Helicopter",
            "126 Applause",
            "127 Gunshot"
        ];

        var result = new List<MidiProgramOption>(names.Length);
        for (var i = 0; i < names.Length; i++)
        {
            result.Add(new MidiProgramOption(i, names[i]));
        }

        return result;
    }
}
