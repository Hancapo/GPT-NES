namespace NesEmu.App;

public enum MidiSourceRole
{
    Auto,
    Music,
    SoundEffect,
    Ignore
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

    public MidiSourceRole Pulse1Role { get; set; } = MidiSourceRole.Auto;

    public MidiSourceRole Pulse2Role { get; set; } = MidiSourceRole.Auto;

    public MidiSourceRole TriangleRole { get; set; } = MidiSourceRole.Auto;

    public MidiSourceRole NoiseRole { get; set; } = MidiSourceRole.Auto;

    public MidiSourceRole DmcRole { get; set; } = MidiSourceRole.Auto;

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
            MidiSyncOffsetMilliseconds = MidiSyncOffsetMilliseconds,
            Pulse1Role = Pulse1Role,
            Pulse2Role = Pulse2Role,
            TriangleRole = TriangleRole,
            NoiseRole = NoiseRole,
            DmcRole = DmcRole
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

public sealed record MidiSourceRoleOption(MidiSourceRole Role, string DisplayName, string Description)
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

    public static IReadOnlyList<MidiSourceRoleOption> SourceRoles { get; } =
    [
        new MidiSourceRoleOption(
            MidiSourceRole.Auto,
            "Auto",
            "Use the default heuristic for this source."),
        new MidiSourceRoleOption(
            MidiSourceRole.Music,
            "Prefer music",
            "Bias the filter to keep this source when it looks musical."),
        new MidiSourceRoleOption(
            MidiSourceRole.SoundEffect,
            "Prefer SFX",
            "Bias the filter to be more conservative with short or abrupt events from this source."),
        new MidiSourceRoleOption(
            MidiSourceRole.Ignore,
            "Ignore",
            "Do not convert this source to MIDI output.")
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
