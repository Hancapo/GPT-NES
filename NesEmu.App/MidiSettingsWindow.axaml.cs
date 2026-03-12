using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace NesEmu.App;

public partial class MidiSettingsWindow : Window
{
    private readonly IReadOnlyList<MidiOutputDeviceInfo> _devices;

    public MidiSettingsWindow()
        : this(MidiOutputSettings.CreateDefault(), [new MidiOutputDeviceInfo(-1, "Sin salida MIDI")])
    {
    }

    public MidiSettingsWindow(MidiOutputSettings settings, IReadOnlyList<MidiOutputDeviceInfo> devices)
    {
        _devices = devices;

        InitializeComponent();
        PopulateLists();
        LoadSettings(settings);
    }

    private void PopulateLists()
    {
        OutputDeviceComboBox.ItemsSource = _devices;
        Pulse1ProgramComboBox.ItemsSource = MidiCatalog.Programs;
        Pulse2ProgramComboBox.ItemsSource = MidiCatalog.Programs;
        TriangleProgramComboBox.ItemsSource = MidiCatalog.Programs;
        NoiseDrumComboBox.ItemsSource = MidiCatalog.PercussionNotes;
        DmcDrumComboBox.ItemsSource = MidiCatalog.PercussionNotes;
        UpdateVolumeLabels();
    }

    private void LoadSettings(MidiOutputSettings settings)
    {
        EnableMidiCheckBox.IsChecked = settings.Enabled;
        MusicOnlyCheckBox.IsChecked = settings.MusicOnlyFilter;
        PercussionCheckBox.IsChecked = settings.SendPercussion;

        Pulse1EnabledCheckBox.IsChecked = settings.Pulse1Enabled;
        Pulse2EnabledCheckBox.IsChecked = settings.Pulse2Enabled;
        TriangleEnabledCheckBox.IsChecked = settings.TriangleEnabled;
        NoiseEnabledCheckBox.IsChecked = settings.NoiseEnabled;
        DmcEnabledCheckBox.IsChecked = settings.DmcEnabled;
        Pulse1VolumeSlider.Value = settings.Pulse1VolumePercent;
        Pulse2VolumeSlider.Value = settings.Pulse2VolumePercent;
        TriangleVolumeSlider.Value = settings.TriangleVolumePercent;
        NoiseVolumeSlider.Value = settings.NoiseVolumePercent;
        DmcVolumeSlider.Value = settings.DmcVolumePercent;

        OutputDeviceComboBox.SelectedItem = _devices.FirstOrDefault(device => device.DeviceIndex == settings.DeviceIndex) ?? _devices.FirstOrDefault();
        Pulse1ProgramComboBox.SelectedItem = MidiCatalog.Programs.FirstOrDefault(program => program.ProgramNumber == settings.Pulse1Program) ?? MidiCatalog.Programs.First();
        Pulse2ProgramComboBox.SelectedItem = MidiCatalog.Programs.FirstOrDefault(program => program.ProgramNumber == settings.Pulse2Program) ?? MidiCatalog.Programs.First();
        TriangleProgramComboBox.SelectedItem = MidiCatalog.Programs.FirstOrDefault(program => program.ProgramNumber == settings.TriangleProgram) ?? MidiCatalog.Programs.First();
        NoiseDrumComboBox.SelectedItem = MidiCatalog.PercussionNotes.FirstOrDefault(note => note.NoteNumber == settings.NoiseDrumNote) ?? MidiCatalog.PercussionNotes.First();
        DmcDrumComboBox.SelectedItem = MidiCatalog.PercussionNotes.FirstOrDefault(note => note.NoteNumber == settings.DmcDrumNote) ?? MidiCatalog.PercussionNotes.First();
        UpdateVolumeLabels();
    }

    private void AcceptButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var settings = new MidiOutputSettings
        {
            Enabled = EnableMidiCheckBox.IsChecked == true,
            DeviceIndex = (OutputDeviceComboBox.SelectedItem as MidiOutputDeviceInfo)?.DeviceIndex ?? -1,
            MusicOnlyFilter = MusicOnlyCheckBox.IsChecked != false,
            SendPercussion = PercussionCheckBox.IsChecked != false,
            Pulse1Enabled = Pulse1EnabledCheckBox.IsChecked != false,
            Pulse2Enabled = Pulse2EnabledCheckBox.IsChecked != false,
            TriangleEnabled = TriangleEnabledCheckBox.IsChecked != false,
            NoiseEnabled = NoiseEnabledCheckBox.IsChecked != false,
            DmcEnabled = DmcEnabledCheckBox.IsChecked != false,
            Pulse1Program = (Pulse1ProgramComboBox.SelectedItem as MidiProgramOption)?.ProgramNumber ?? 80,
            Pulse2Program = (Pulse2ProgramComboBox.SelectedItem as MidiProgramOption)?.ProgramNumber ?? 81,
            TriangleProgram = (TriangleProgramComboBox.SelectedItem as MidiProgramOption)?.ProgramNumber ?? 33,
            Pulse1VolumePercent = ReadPercent(Pulse1VolumeSlider),
            Pulse2VolumePercent = ReadPercent(Pulse2VolumeSlider),
            TriangleVolumePercent = ReadPercent(TriangleVolumeSlider),
            NoiseVolumePercent = ReadPercent(NoiseVolumeSlider),
            DmcVolumePercent = ReadPercent(DmcVolumeSlider),
            NoiseDrumNote = (NoiseDrumComboBox.SelectedItem as MidiPercussionOption)?.NoteNumber ?? -1,
            DmcDrumNote = (DmcDrumComboBox.SelectedItem as MidiPercussionOption)?.NoteNumber ?? -1
        };

        Close(settings);
    }

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void VolumeSlider_OnValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateVolumeLabels();
    }

    private void UpdateVolumeLabels()
    {
        Pulse1VolumeText.Text = FormatPercent(Pulse1VolumeSlider.Value);
        Pulse2VolumeText.Text = FormatPercent(Pulse2VolumeSlider.Value);
        TriangleVolumeText.Text = FormatPercent(TriangleVolumeSlider.Value);
        NoiseVolumeText.Text = FormatPercent(NoiseVolumeSlider.Value);
        DmcVolumeText.Text = FormatPercent(DmcVolumeSlider.Value);
    }

    private static int ReadPercent(Slider slider)
    {
        return Math.Clamp((int)Math.Round(slider.Value), 0, 200);
    }

    private static string FormatPercent(double value)
    {
        return $"{Math.Clamp((int)Math.Round(value), 0, 200)}%";
    }
}
