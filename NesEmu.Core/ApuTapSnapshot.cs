namespace NesEmu.Core;

public readonly record struct ApuTapSnapshot(
    PulseTapSnapshot Pulse1,
    PulseTapSnapshot Pulse2,
    TriangleTapSnapshot Triangle,
    NoiseTapSnapshot Noise,
    DmcTapSnapshot Dmc);

public readonly record struct PulseTapSnapshot(
    bool Enabled,
    bool Audible,
    int Volume,
    int TimerPeriod,
    int DutyMode,
    int TriggerVersion);

public readonly record struct TriangleTapSnapshot(
    bool Enabled,
    bool Audible,
    int Volume,
    int TimerPeriod,
    int TriggerVersion);

public readonly record struct NoiseTapSnapshot(
    bool Enabled,
    bool Audible,
    int Volume,
    int PeriodIndex,
    bool LoopMode,
    int TriggerVersion);

public readonly record struct DmcTapSnapshot(
    bool Enabled,
    bool Active,
    int RateIndex,
    int OutputLevel,
    int SampleLength,
    int TriggerVersion);
