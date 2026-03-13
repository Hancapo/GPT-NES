using RtMidi.Core;
using RtMidi.Core.Devices;
using RtMidi.Core.Enums;
using RtMidi.Core.Messages;

namespace NesEmu.App;

internal interface IMidiOutputPortFactory
{
    IReadOnlyList<MidiOutputDeviceInfo> GetDevices();

    IMidiOutputPort OpenDevice(int deviceIndex);
}

internal interface IMidiOutputPort : IDisposable
{
    string Name { get; }

    void SendNoteOn(int channel, int noteNumber, int velocity);

    void SendNoteOff(int channel, int noteNumber);

    void SendControlChange(int channel, int controller, int value);

    void SendProgramChange(int channel, int program);

    void SendPitchBend(int channel, int value);
}

internal sealed class RtMidiOutputPortFactory : IMidiOutputPortFactory
{
    public IReadOnlyList<MidiOutputDeviceInfo> GetDevices()
    {
        var devices = new List<MidiOutputDeviceInfo>
        {
            new(-1, "No MIDI output")
        };

        try
        {
            var index = 0;
            foreach (var output in MidiDeviceManager.Default.OutputDevices)
            {
                var displayName = string.IsNullOrWhiteSpace(output.Name)
                    ? $"MIDI Device {index + 1}"
                    : output.Name;
                devices.Add(new MidiOutputDeviceInfo(index, displayName));
                index++;
            }

            AppLogger.Info($"Enumerated {devices.Count - 1} MIDI output device(s).");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to enumerate MIDI output devices.", ex);
            return devices;
        }

        return devices;
    }

    public IMidiOutputPort OpenDevice(int deviceIndex)
    {
        if (deviceIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceIndex));
        }

        try
        {
            var output = MidiDeviceManager.Default.OutputDevices.ElementAtOrDefault(deviceIndex);
            if (output is null)
            {
                throw new InvalidOperationException("The selected MIDI output is no longer available.");
            }

            var device = output.CreateDevice();

            try
            {
                if (device is not IMidiDevice midiDevice || !midiDevice.Open())
                {
                    throw new InvalidOperationException("The selected MIDI output could not be opened.");
                }

                AppLogger.Info($"Opened MIDI output device {deviceIndex}: {output.Name}");
                return new RtMidiOutputPort(device, output.Name);
            }
            catch
            {
                device.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to open MIDI output device {deviceIndex}.", ex);
            throw;
        }
    }
}

internal sealed class RtMidiOutputPort : IMidiOutputPort
{
    private readonly IMidiOutputDevice _device;
    private readonly IMidiDevice _midiDevice;

    public RtMidiOutputPort(IMidiOutputDevice device, string? name = null)
    {
        _device = device;
        _midiDevice = (IMidiDevice)device;
        Name = string.IsNullOrWhiteSpace(name) ? device.Name : name;
    }

    public string Name { get; }

    public void SendNoteOn(int channel, int noteNumber, int velocity)
    {
        var message = new NoteOnMessage(ToChannel(channel), ToKey(noteNumber), Math.Clamp(velocity, 0, 127));
        _device.Send(in message);
    }

    public void SendNoteOff(int channel, int noteNumber)
    {
        var message = new NoteOffMessage(ToChannel(channel), ToKey(noteNumber), 0);
        _device.Send(in message);
    }

    public void SendControlChange(int channel, int controller, int value)
    {
        var message = new ControlChangeMessage(ToChannel(channel), Math.Clamp(controller, 0, 127), Math.Clamp(value, 0, 127));
        _device.Send(in message);
    }

    public void SendProgramChange(int channel, int program)
    {
        var message = new ProgramChangeMessage(ToChannel(channel), Math.Clamp(program, 0, 127));
        _device.Send(in message);
    }

    public void SendPitchBend(int channel, int value)
    {
        var message = new PitchBendMessage(ToChannel(channel), Math.Clamp(value, 0, 16_383));
        _device.Send(in message);
    }

    public void Dispose()
    {
        try
        {
            if (_midiDevice.IsOpen)
            {
                _midiDevice.Close();
            }
        }
        catch
        {
        }
        finally
        {
            _device.Dispose();
            AppLogger.Info($"Closed MIDI output device: {Name}");
        }
    }

    private static Channel ToChannel(int channel)
    {
        var zeroBasedChannel = Math.Clamp(channel - 1, 0, 15);
        return (Channel)zeroBasedChannel;
    }

    private static Key ToKey(int noteNumber)
    {
        return (Key)Math.Clamp(noteNumber, 0, 127);
    }
}
