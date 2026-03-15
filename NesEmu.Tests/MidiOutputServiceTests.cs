using NesEmu.App;
using NesEmu.Core;

namespace NesEmu.Tests;

public sealed class MidiOutputServiceTests
{
    [Fact]
    public void SilentFrameStopsActiveMelodicNoteImmediately()
    {
        var factory = new FakeMidiOutputPortFactory();
        using var service = new MidiOutputService(factory);

        var applied = service.TryApplySettings(new MidiOutputSettings
        {
            Enabled = true,
            DeviceIndex = 0
        }, out var error);

        Assert.True(applied, error);

        service.ProcessFrame(CreatePulse1Snapshot(audible: true, volume: 15, timerPeriod: 120, triggerVersion: 1));
        service.ProcessFrame(CreatePulse1Snapshot(audible: false, volume: 0, timerPeriod: 120, triggerVersion: 1));

        var noteEvents = factory.Port.Messages
            .Where(message => message.Channel == 1 && (message.Kind == MidiPortMessageKind.NoteOn || message.Kind == MidiPortMessageKind.NoteOff))
            .ToArray();

        Assert.Collection(
            noteEvents,
            message =>
            {
                Assert.Equal(MidiPortMessageKind.NoteOn, message.Kind);
                Assert.InRange(message.Data1, 0, 127);
            },
            message =>
            {
                Assert.Equal(MidiPortMessageKind.NoteOff, message.Kind);
                Assert.InRange(message.Data1, 0, 127);
            });
    }

    [Fact]
    public void TriggerVersionChangeRetriggersSamePitch()
    {
        var factory = new FakeMidiOutputPortFactory();
        using var service = new MidiOutputService(factory);

        var applied = service.TryApplySettings(new MidiOutputSettings
        {
            Enabled = true,
            DeviceIndex = 0
        }, out var error);

        Assert.True(applied, error);

        service.ProcessFrame(CreatePulse1Snapshot(audible: true, volume: 6, timerPeriod: 120, triggerVersion: 1));
        service.ProcessFrame(CreatePulse1Snapshot(audible: true, volume: 15, timerPeriod: 120, triggerVersion: 2));

        var noteEvents = factory.Port.Messages
            .Where(message => message.Channel == 1 && (message.Kind == MidiPortMessageKind.NoteOn || message.Kind == MidiPortMessageKind.NoteOff))
            .ToArray();

        Assert.Collection(
            noteEvents,
            message => Assert.Equal(MidiPortMessageKind.NoteOn, message.Kind),
            message => Assert.Equal(MidiPortMessageKind.NoteOff, message.Kind),
            message => Assert.Equal(MidiPortMessageKind.NoteOn, message.Kind));

        Assert.Equal(noteEvents[0].Data1, noteEvents[1].Data1);
        Assert.Equal(noteEvents[0].Data1, noteEvents[2].Data1);
    }

    [Fact]
    public void TriggerVersionChangeDoesNotRetriggerWhileExpressionKeepsFalling()
    {
        var factory = new FakeMidiOutputPortFactory();
        using var service = new MidiOutputService(factory);

        var applied = service.TryApplySettings(new MidiOutputSettings
        {
            Enabled = true,
            DeviceIndex = 0
        }, out var error);

        Assert.True(applied, error);

        service.ProcessFrame(CreatePulse1Snapshot(audible: true, volume: 15, timerPeriod: 120, triggerVersion: 1));
        service.ProcessFrame(CreatePulse1Snapshot(audible: true, volume: 10, timerPeriod: 120, triggerVersion: 2));

        var noteEvents = factory.Port.Messages
            .Where(message => message.Channel == 1 && (message.Kind == MidiPortMessageKind.NoteOn || message.Kind == MidiPortMessageKind.NoteOff))
            .ToArray();

        Assert.Collection(
            noteEvents,
            message => Assert.Equal(MidiPortMessageKind.NoteOn, message.Kind));
    }

    [Fact]
    public void ApplyingUnchangedSettingsDoesNotReopenMidiDevice()
    {
        var factory = new FakeMidiOutputPortFactory();
        using var service = new MidiOutputService(factory);
        var settings = new MidiOutputSettings
        {
            Enabled = true,
            DeviceIndex = 0
        };

        Assert.True(service.TryApplySettings(settings, out var firstError), firstError);
        Assert.True(service.TryApplySettings(settings, out var secondError), secondError);

        Assert.Equal(1, factory.OpenCount);
    }

    private static ApuTapSnapshot CreatePulse1Snapshot(bool audible, int volume, int timerPeriod, int triggerVersion)
    {
        return new ApuTapSnapshot(
            new PulseTapSnapshot(
                Enabled: true,
                Audible: audible,
                Volume: volume,
                TimerPeriod: timerPeriod,
                DutyMode: 2,
                TriggerVersion: triggerVersion),
            default,
            default,
            default,
            default);
    }

    private sealed class FakeMidiOutputPortFactory : IMidiOutputPortFactory
    {
        public FakeMidiOutputPort Port { get; } = new();
        public int OpenCount { get; private set; }

        public IReadOnlyList<MidiOutputDeviceInfo> GetDevices()
        {
            return
            [
                new MidiOutputDeviceInfo(-1, "No MIDI output"),
                new MidiOutputDeviceInfo(0, "Fake MIDI")
            ];
        }

        public IMidiOutputPort OpenDevice(int deviceIndex)
        {
            Assert.Equal(0, deviceIndex);
            OpenCount++;
            return Port;
        }
    }

    private sealed class FakeMidiOutputPort : IMidiOutputPort
    {
        private readonly List<MidiPortMessage> _messages = [];

        public string Name => "Fake MIDI";

        public IReadOnlyList<MidiPortMessage> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToArray();
                }
            }
        }

        public void SendNoteOn(int channel, int noteNumber, int velocity)
        {
            Add(MidiPortMessageKind.NoteOn, channel, noteNumber, velocity);
        }

        public void SendNoteOff(int channel, int noteNumber)
        {
            Add(MidiPortMessageKind.NoteOff, channel, noteNumber, 0);
        }

        public void SendControlChange(int channel, int controller, int value)
        {
            Add(MidiPortMessageKind.ControlChange, channel, controller, value);
        }

        public void SendProgramChange(int channel, int program)
        {
            Add(MidiPortMessageKind.ProgramChange, channel, program, 0);
        }

        public void SendPitchBend(int channel, int value)
        {
            Add(MidiPortMessageKind.PitchBend, channel, value, 0);
        }

        public void Dispose()
        {
        }

        private void Add(MidiPortMessageKind kind, int channel, int data1, int data2)
        {
            lock (_messages)
            {
                _messages.Add(new MidiPortMessage(kind, channel, data1, data2));
            }
        }
    }

    private enum MidiPortMessageKind
    {
        NoteOn,
        NoteOff,
        ControlChange,
        ProgramChange,
        PitchBend
    }

    private readonly record struct MidiPortMessage(MidiPortMessageKind Kind, int Channel, int Data1, int Data2);
}
