using System.Diagnostics;
using System.Threading;
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
    private const int SameNoteRetriggerExpressionRise = 8;
    private const int PercussionReleaseFrames = 2;
    private static readonly TimeSpan FrameLeadCompensation = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan MaximumMidiSyncDelay = TimeSpan.FromMilliseconds(80);

    private readonly object _sync = new();
    private readonly IMidiOutputPortFactory _portFactory;
    private readonly MelodicTracker _pulse1Tracker = new(Pulse1MidiChannel);
    private readonly MelodicTracker _pulse2Tracker = new(Pulse2MidiChannel);
    private readonly MelodicTracker _triangleTracker = new(TriangleMidiChannel);
    private readonly PercussionTracker _noiseTracker = new();
    private readonly PercussionTracker _dmcTracker = new();
    private readonly List<ActivePercussionHit> _activePercussionHits = [];
    private readonly PriorityQueue<ScheduledMidiMessage, (long DueTicks, long SequenceNumber)> _scheduledMessages = new();
    private readonly AutoResetEvent _dispatchSignal = new(false);
    private readonly CancellationTokenSource _dispatchCancellation = new();
    private readonly Stopwatch _schedulerClock = Stopwatch.StartNew();
    private readonly Task _dispatchTask;

    private MidiOutputSettings _settings = MidiOutputSettings.CreateDefault();
    private IMidiOutputPort? _midiOut;
    private long _scheduledMessageSequence;
    private TimeSpan _presentationLatency = TimeSpan.Zero;
    private TimeSpan _performanceSendDelay = TimeSpan.Zero;
    private bool _disposed;

    public MidiOutputService()
        : this(new RtMidiOutputPortFactory())
    {
    }

    internal MidiOutputService(IMidiOutputPortFactory portFactory)
    {
        _portFactory = portFactory;
        _dispatchTask = Task.Run(() => DispatchLoop(_dispatchCancellation.Token));
    }

    public IReadOnlyList<MidiOutputDeviceInfo> GetDevices()
    {
        return _portFactory.GetDevices();
    }

    public MidiOutputSettings GetSettingsSnapshot()
    {
        lock (_sync)
        {
            return _settings.Clone();
        }
    }

    public bool IsOutputActive
    {
        get
        {
            lock (_sync)
            {
                return _midiOut is not null && _settings.Enabled;
            }
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

            return $"MIDI: {(_midiOut?.Name ?? ResolveDeviceName(_settings.DeviceIndex))}";
        }
    }

    public bool TryApplySettings(MidiOutputSettings settings, out string? error)
    {
        lock (_sync)
        {
            error = null;

            if (settings.IsEquivalentTo(_settings)
                && (!_settings.Enabled || _midiOut is not null))
            {
                AppLogger.Info($"Skipping MIDI reapply because settings are unchanged. Enabled={settings.Enabled}, DeviceIndex={settings.DeviceIndex}");
                return true;
            }

            AppLogger.Info($"Applying MIDI settings. Enabled={settings.Enabled}, DeviceIndex={settings.DeviceIndex}, SendPercussion={settings.SendPercussion}");

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
                _midiOut = _portFactory.OpenDevice(_settings.DeviceIndex);
                ConfigureDeviceLocked();
                AppLogger.Info($"MIDI output is active: {_midiOut.Name}");
                return true;
            }
            catch (Exception ex)
            {
                _settings.Enabled = false;
                error = ex.Message;
                AppLogger.Error("Could not apply MIDI settings.", ex);
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

    public void ClearPresentationLatency()
    {
        lock (_sync)
        {
            _presentationLatency = TimeSpan.Zero;
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
                ProcessNoisePercussionLocked(snapshot.Noise, _settings.NoiseEnabled);
                ProcessDmcPercussionLocked(snapshot.Dmc, _settings.DmcEnabled);
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
        var audible = enabled
            && levelPercent > 0
            && voice.Audible;

        ProcessMelodicVoiceLocked(
            tracker,
            audible,
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
        var audible = enabled
            && levelPercent > 0
            && voice.Audible;

        ProcessMelodicVoiceLocked(
            tracker,
            audible,
            frequency,
            noteVelocity,
            expression,
            voice.TriggerVersion);
    }

    private void ProcessMelodicVoiceLocked(
        MelodicTracker tracker,
        bool audible,
        double frequency,
        int noteVelocity,
        int expression,
        int triggerVersion)
    {
        if (!audible || frequency <= 0.0)
        {
            tracker.Candidate = null;

            if (!tracker.IsNoteOn)
            {
                tracker.NoteOnFrames = 0;
                tracker.LastObservedTriggerVersion = triggerVersion;
                return;
            }

            StopNoteLocked(tracker);
            tracker.LastObservedTriggerVersion = triggerVersion;
            return;
        }

        var target = CreatePitchTarget(frequency, expression);
        var triggerChanged = triggerVersion != 0 && triggerVersion != tracker.LastObservedTriggerVersion;
        if (tracker.IsNoteOn && tracker.CurrentNote == target.NoteNumber)
        {
            tracker.Candidate = null;
            var shouldRetrigger = triggerChanged
                && target.Expression >= tracker.CurrentExpression + SameNoteRetriggerExpressionRise;
            if (shouldRetrigger)
            {
                SendNoteOffLocked(tracker.Channel, tracker.CurrentNote);
                SendPitchBendLocked(tracker.Channel, target.PitchBend);
                SendNoteOnLocked(tracker.Channel, target.NoteNumber, noteVelocity);
                SendExpressionLocked(tracker.Channel, target.Expression);
                tracker.NoteOnFrames = 1;
            }
            else
            {
                SendPitchBendLocked(tracker.Channel, target.PitchBend);
                SendExpressionLocked(tracker.Channel, target.Expression);
                tracker.NoteOnFrames = Math.Max(tracker.NoteOnFrames + 1, 1);
            }

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

        if (tracker.Candidate.StableFrames < StableNoteFrames)
        {
            tracker.NoteOnFrames = 0;
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
        tracker.NoteOnFrames = 1;
        tracker.Candidate = null;
    }

    private void ProcessNoisePercussionLocked(NoiseTapSnapshot snapshot, bool enabled)
    {
        if (!ShouldProcessNoisePercussion(snapshot, enabled))
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
        if (!ShouldProcessDmcPercussion(snapshot, enabled))
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

    private bool ShouldProcessNoisePercussion(NoiseTapSnapshot snapshot, bool enabled)
    {
        return enabled
            && _settings.SendPercussion
            && snapshot.Enabled
            && snapshot.Audible;
    }

    private bool ShouldProcessDmcPercussion(DmcTapSnapshot snapshot, bool enabled)
    {
        return enabled
            && _settings.SendPercussion
            && snapshot.Active;
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

        var previousDelay = _performanceSendDelay;
        _performanceSendDelay = TimeSpan.Zero;

        try
        {
            ConfigureMelodicChannelLocked(Pulse1MidiChannel, _settings.Pulse1Program);
            ConfigureMelodicChannelLocked(Pulse2MidiChannel, _settings.Pulse2Program);
            ConfigureMelodicChannelLocked(TriangleMidiChannel, _settings.TriangleProgram);
            SendChannelVolumeLocked(PercussionMidiChannel, 127);
            SendExpressionLocked(PercussionMidiChannel, 127);
            SendAllNotesOffLocked(PercussionMidiChannel);
        }
        finally
        {
            _performanceSendDelay = previousDelay;
        }
    }

    private void ConfigureMelodicChannelLocked(int channel, int program)
    {
        SendPitchBendRangeLocked(channel, PitchBendRangeSemitones);
        SendChannelVolumeLocked(channel, 127);
        SendPitchBendLocked(channel, PitchBendCenter);
        _midiOut!.SendProgramChange(channel, Math.Clamp(program, 0, 127));
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
        QueueOrSendMessageLocked(new ScheduledMidiMessage(MidiCommandKind.ControlChange, channel, controller, value), immediate: _performanceSendDelay <= TimeSpan.Zero);
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
        QueueOrSendMessageLocked(new ScheduledMidiMessage(MidiCommandKind.PitchBend, channel, Math.Clamp(value, 0, 16_383), 0), immediate: _performanceSendDelay <= TimeSpan.Zero);
    }

    private void SendNoteOnLocked(int channel, int noteNumber, int velocity)
    {
        QueueOrSendMessageLocked(new ScheduledMidiMessage(MidiCommandKind.NoteOn, channel, Math.Clamp(noteNumber, 0, 127), Math.Clamp(velocity, 1, 127)), immediate: _performanceSendDelay <= TimeSpan.Zero);
    }

    private void SendNoteOffLocked(int channel, int noteNumber)
    {
        QueueOrSendMessageLocked(new ScheduledMidiMessage(MidiCommandKind.NoteOff, channel, Math.Clamp(noteNumber, 0, 127), 0), immediate: _performanceSendDelay <= TimeSpan.Zero);
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
        tracker.NoteOnFrames = 0;
        tracker.Candidate = null;
    }

    private void SilenceLocked()
    {
        ClearScheduledMessagesLocked();

        if (_midiOut is null)
        {
            return;
        }

        var previousDelay = _performanceSendDelay;
        _performanceSendDelay = TimeSpan.Zero;

        try
        {
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
        finally
        {
            _performanceSendDelay = previousDelay;
        }
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

    private void QueueOrSendMessageLocked(ScheduledMidiMessage message, bool immediate)
    {
        if (_midiOut is null)
        {
            return;
        }

        if (immediate)
        {
            SendScheduledMessageLocked(message);
            return;
        }

        var dueTicks = _schedulerClock.ElapsedTicks + (long)(_performanceSendDelay.TotalSeconds * Stopwatch.Frequency);
        _scheduledMessages.Enqueue(message, (dueTicks, _scheduledMessageSequence++));
        _dispatchSignal.Set();
    }

    private void SendScheduledMessageLocked(ScheduledMidiMessage message)
    {
        if (_midiOut is null)
        {
            return;
        }

        switch (message.Kind)
        {
            case MidiCommandKind.NoteOn:
                _midiOut.SendNoteOn(message.Channel, message.Data1, message.Data2);
                break;
            case MidiCommandKind.NoteOff:
                _midiOut.SendNoteOff(message.Channel, message.Data1);
                break;
            case MidiCommandKind.ControlChange:
                _midiOut.SendControlChange(message.Channel, message.Data1, message.Data2);
                break;
            case MidiCommandKind.ProgramChange:
                _midiOut.SendProgramChange(message.Channel, message.Data1);
                break;
            case MidiCommandKind.PitchBend:
                _midiOut.SendPitchBend(message.Channel, message.Data1);
                break;
        }
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
            lock (_sync)
            {
                if (_midiOut is null || _scheduledMessages.Count == 0)
                {
                    return;
                }

                var nowTicks = _schedulerClock.ElapsedTicks;
                if (!_scheduledMessages.TryPeek(out var nextMessage, out var duePriority) || duePriority.DueTicks > nowTicks)
                {
                    return;
                }

                _scheduledMessages.Dequeue();
                SendScheduledMessageLocked(nextMessage);
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
            if (!_scheduledMessages.TryPeek(out _, out var duePriority))
            {
                return 8;
            }

            var ticksUntilDue = duePriority.DueTicks - nowTicks;
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
        public MelodicTracker(int channel)
        {
            Channel = channel;
            Reset();
        }

        public int Channel { get; }

        public bool IsNoteOn { get; set; }

        public int CurrentNote { get; set; }

        public int CurrentPitchBend { get; set; }

        public int CurrentExpression { get; set; }

        public int NoteOnFrames { get; set; }

        public int LastObservedTriggerVersion { get; set; }

        public CandidatePitchTarget? Candidate { get; set; }

        public void Reset()
        {
            IsNoteOn = false;
            CurrentNote = -1;
            CurrentPitchBend = PitchBendCenter;
            CurrentExpression = 0;
            NoteOnFrames = 0;
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

    private enum MidiCommandKind
    {
        NoteOn,
        NoteOff,
        ControlChange,
        ProgramChange,
        PitchBend
    }

    private readonly record struct ScheduledMidiMessage(MidiCommandKind Kind, int Channel, int Data1, int Data2);
}
