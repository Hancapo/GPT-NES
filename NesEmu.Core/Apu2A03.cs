namespace NesEmu.Core;

public sealed class Apu2A03
{
    private const int FourStepQuarter1CpuCycle = 7457;
    private const int FourStepHalf1CpuCycle = 14913;
    private const int FourStepQuarter2CpuCycle = 22371;
    private const int FourStepHalf2CpuCycle = 29829;
    private const int FourStepResetCpuCycle = 29830;

    private const int FiveStepQuarter1CpuCycle = 7457;
    private const int FiveStepHalf1CpuCycle = 14913;
    private const int FiveStepQuarter2CpuCycle = 22371;
    private const int FiveStepHalf2CpuCycle = 37281;
    private const int FiveStepResetCpuCycle = 37282;

    private static readonly byte[] LengthTable =
    [
        10, 254, 20, 2, 40, 4, 80, 6,
        160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22,
        192, 24, 72, 26, 16, 28, 32, 30
    ];

    private static readonly ushort[] NoisePeriods =
    [
        4, 8, 16, 32, 64, 96, 128, 160,
        202, 254, 380, 508, 762, 1016, 2034, 4068
    ];

    private static readonly byte[][] DutySequences =
    [
        [0, 1, 0, 0, 0, 0, 0, 0],
        [0, 1, 1, 0, 0, 0, 0, 0],
        [0, 1, 1, 1, 1, 0, 0, 0],
        [1, 0, 0, 1, 1, 1, 1, 1]
    ];

    private static readonly byte[] TriangleSequence =
    [
        15, 14, 13, 12, 11, 10, 9, 8,
        7, 6, 5, 4, 3, 2, 1, 0,
        0, 1, 2, 3, 4, 5, 6, 7,
        8, 9, 10, 11, 12, 13, 14, 15
    ];

    private readonly PulseChannel _pulse1 = new(onesComplementNegate: true);
    private readonly PulseChannel _pulse2 = new(onesComplementNegate: false);
    private readonly TriangleChannel _triangle = new();
    private readonly NoiseChannel _noise = new();
    private readonly DmcChannel _dmc = new();
    private readonly HighPassFilter _highPass90;
    private readonly HighPassFilter _highPass440;
    private readonly LowPassFilter _lowPass14k;

    private long _cpuCycles;
    private int _frameCounterCpuCycles;
    private bool _fiveStepMode;
    private bool _irqInhibit;
    private bool _frameIrqPending;

    public Apu2A03(int sampleRate)
    {
        _highPass90 = new HighPassFilter(90, sampleRate);
        _highPass440 = new HighPassFilter(440, sampleRate);
        _lowPass14k = new LowPassFilter(14_000, sampleRate);
    }

    public bool IrqPending => _frameIrqPending;

    public void Reset()
    {
        _pulse1.Reset();
        _pulse2.Reset();
        _triangle.Reset();
        _noise.Reset();
        _dmc.Reset();
        _cpuCycles = 0;
        _frameCounterCpuCycles = 0;
        _fiveStepMode = false;
        _irqInhibit = false;
        _frameIrqPending = false;
        _highPass90.Reset();
        _highPass440.Reset();
        _lowPass14k.Reset();
    }

    public void Clock()
    {
        _cpuCycles++;
        _frameCounterCpuCycles++;

        _triangle.ClockTimer();
        if ((_cpuCycles & 0x01) == 0)
        {
            _pulse1.ClockTimer();
            _pulse2.ClockTimer();
            _noise.ClockTimer();
        }

        ClockFrameSequencer();
    }

    public float GetCurrentSample()
    {
        return ProcessSample(GetCurrentRawSample());
    }

    public float GetCurrentRawSample()
    {
        var pulse1 = _pulse1.Output;
        var pulse2 = _pulse2.Output;
        var triangle = _triangle.Output;
        var noise = _noise.Output;

        var pulseSum = pulse1 + pulse2;
        var pulseOut = pulseSum == 0
            ? 0.0
            : 95.88 / ((8128.0 / pulseSum) + 100.0);

        var tndInput = (triangle / 8227.0) + (noise / 12241.0);
        var tndOut = tndInput == 0
            ? 0.0
            : 159.79 / ((1.0 / tndInput) + 100.0);

        return (float)(pulseOut + tndOut);
    }

    public float ProcessSample(float sample)
    {
        sample = _highPass90.Process(sample);
        sample = _highPass440.Process(sample);
        sample = _lowPass14k.Process(sample);
        sample *= 0.85f;
        return Math.Clamp(sample, -1.0f, 1.0f);
    }

    public ApuTapSnapshot CaptureTapSnapshot()
    {
        return new ApuTapSnapshot(
            new PulseTapSnapshot(
                _pulse1.Enabled,
                _pulse1.IsAudible,
                _pulse1.Volume,
                _pulse1.TimerPeriod,
                _pulse1.DutyMode,
                _pulse1.TriggerVersion),
            new PulseTapSnapshot(
                _pulse2.Enabled,
                _pulse2.IsAudible,
                _pulse2.Volume,
                _pulse2.TimerPeriod,
                _pulse2.DutyMode,
                _pulse2.TriggerVersion),
            new TriangleTapSnapshot(
                _triangle.Enabled,
                _triangle.IsAudible,
                _triangle.Volume,
                _triangle.TimerPeriod,
                _triangle.TriggerVersion),
            new NoiseTapSnapshot(
                _noise.Enabled,
                _noise.IsAudible,
                _noise.Volume,
                _noise.PeriodIndex,
                _noise.ModeFlag,
                _noise.TriggerVersion),
            new DmcTapSnapshot(
                _dmc.Enabled,
                _dmc.IsActive,
                _dmc.RateIndex,
                _dmc.OutputLevel,
                _dmc.SampleLength,
                _dmc.TriggerVersion));
    }

    public byte ReadStatus()
    {
        var status = (byte)0;
        if (_pulse1.LengthCounter > 0)
        {
            status |= 0x01;
        }

        if (_pulse2.LengthCounter > 0)
        {
            status |= 0x02;
        }

        if (_triangle.LengthCounter > 0)
        {
            status |= 0x04;
        }

        if (_noise.LengthCounter > 0)
        {
            status |= 0x08;
        }

        if (_frameIrqPending)
        {
            status |= 0x40;
        }

        _frameIrqPending = false;
        return status;
    }

    public void WriteRegister(ushort address, byte value)
    {
        switch (address)
        {
            case 0x4000:
                _pulse1.WriteControl(value);
                break;
            case 0x4001:
                _pulse1.WriteSweep(value);
                break;
            case 0x4002:
                _pulse1.WriteTimerLow(value);
                break;
            case 0x4003:
                _pulse1.WriteTimerHigh(value, LengthTable);
                break;
            case 0x4004:
                _pulse2.WriteControl(value);
                break;
            case 0x4005:
                _pulse2.WriteSweep(value);
                break;
            case 0x4006:
                _pulse2.WriteTimerLow(value);
                break;
            case 0x4007:
                _pulse2.WriteTimerHigh(value, LengthTable);
                break;
            case 0x4008:
                _triangle.WriteControl(value);
                break;
            case 0x400A:
                _triangle.WriteTimerLow(value);
                break;
            case 0x400B:
                _triangle.WriteTimerHigh(value, LengthTable);
                break;
            case 0x400C:
                _noise.WriteControl(value);
                break;
            case 0x400E:
                _noise.WritePeriod(value);
                break;
            case 0x400F:
                _noise.WriteLength(value, LengthTable);
                break;
            case 0x4010:
                _dmc.WriteControl(value);
                break;
            case 0x4011:
                _dmc.WriteDirectLoad(value);
                break;
            case 0x4012:
                _dmc.WriteSampleAddress(value);
                break;
            case 0x4013:
                _dmc.WriteSampleLength(value);
                break;
            case 0x4015:
                WriteStatus(value);
                break;
            case 0x4017:
                WriteFrameCounter(value);
                break;
        }
    }

    private void WriteStatus(byte value)
    {
        _pulse1.Enabled = (value & 0x01) != 0;
        _pulse2.Enabled = (value & 0x02) != 0;
        _triangle.Enabled = (value & 0x04) != 0;
        _noise.Enabled = (value & 0x08) != 0;
        _dmc.SetEnabled((value & 0x10) != 0);
    }

    private void WriteFrameCounter(byte value)
    {
        _fiveStepMode = (value & 0x80) != 0;
        _irqInhibit = (value & 0x40) != 0;
        _frameCounterCpuCycles = 0;

        if (_irqInhibit)
        {
            _frameIrqPending = false;
        }

        if (_fiveStepMode)
        {
            ClockQuarterFrame();
            ClockHalfFrame();
        }
    }

    private void ClockFrameSequencer()
    {
        if (_fiveStepMode)
        {
            switch (_frameCounterCpuCycles)
            {
                case FiveStepQuarter1CpuCycle:
                case FiveStepQuarter2CpuCycle:
                    ClockQuarterFrame();
                    break;
                case FiveStepHalf1CpuCycle:
                case FiveStepHalf2CpuCycle:
                    ClockQuarterFrame();
                    ClockHalfFrame();
                    break;
            }

            if (_frameCounterCpuCycles >= FiveStepResetCpuCycle)
            {
                _frameCounterCpuCycles = 0;
            }

            return;
        }

        switch (_frameCounterCpuCycles)
        {
            case FourStepQuarter1CpuCycle:
            case FourStepQuarter2CpuCycle:
                ClockQuarterFrame();
                break;
            case FourStepHalf1CpuCycle:
                ClockQuarterFrame();
                ClockHalfFrame();
                break;
            case FourStepHalf2CpuCycle:
                ClockQuarterFrame();
                ClockHalfFrame();
                if (!_irqInhibit)
                {
                    _frameIrqPending = true;
                }
                break;
        }

        if (_frameCounterCpuCycles >= FourStepResetCpuCycle)
        {
            _frameCounterCpuCycles = 0;
        }
    }

    private void ClockQuarterFrame()
    {
        _pulse1.ClockEnvelope();
        _pulse2.ClockEnvelope();
        _triangle.ClockLinearCounter();
        _noise.ClockEnvelope();
    }

    private void ClockHalfFrame()
    {
        _pulse1.ClockLengthCounter();
        _pulse2.ClockLengthCounter();
        _triangle.ClockLengthCounter();
        _noise.ClockLengthCounter();

        _pulse1.ClockSweep();
        _pulse2.ClockSweep();
    }

    private sealed class HighPassFilter
    {
        private readonly float _alpha;
        private float _previousInput;
        private float _previousOutput;

        public HighPassFilter(float cutoffHz, int sampleRate)
        {
            var dt = 1.0f / sampleRate;
            var rc = 1.0f / (2.0f * MathF.PI * cutoffHz);
            _alpha = rc / (rc + dt);
        }

        public float Process(float input)
        {
            var output = _alpha * (_previousOutput + input - _previousInput);
            _previousInput = input;
            _previousOutput = output;
            return output;
        }

        public void Reset()
        {
            _previousInput = 0;
            _previousOutput = 0;
        }
    }

    private sealed class LowPassFilter
    {
        private readonly float _alpha;
        private float _previousOutput;

        public LowPassFilter(float cutoffHz, int sampleRate)
        {
            var dt = 1.0f / sampleRate;
            var rc = 1.0f / (2.0f * MathF.PI * cutoffHz);
            _alpha = dt / (rc + dt);
        }

        public float Process(float input)
        {
            _previousOutput += _alpha * (input - _previousOutput);
            return _previousOutput;
        }

        public void Reset()
        {
            _previousOutput = 0;
        }
    }

    private sealed class PulseChannel
    {
        private readonly bool _onesComplementNegate;
        private bool _enabled;
        private int _dutyMode;
        private bool _lengthCounterHalt;
        private bool _constantVolume;
        private byte _envelopePeriod;
        private bool _envelopeStart;
        private byte _envelopeDivider;
        private byte _envelopeDecayLevel = 15;
        private bool _sweepEnabled;
        private byte _sweepDividerPeriod;
        private bool _sweepNegate;
        private byte _sweepShift;
        private bool _sweepReload;
        private byte _sweepDivider;
        private ushort _timerPeriod;
        private ushort _timerCounter;
        private byte _sequenceStep;
        private byte _lengthCounter;
        private int _triggerVersion;

        public PulseChannel(bool onesComplementNegate)
        {
            _onesComplementNegate = onesComplementNegate;
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (!_enabled)
                {
                    _lengthCounter = 0;
                }
            }
        }

        public int LengthCounter => _lengthCounter;

        public int DutyMode => _dutyMode;

        public int TimerPeriod => _timerPeriod;

        public int TriggerVersion => _triggerVersion;

        public int Volume => IsAudible ? CurrentVolume : 0;

        public bool IsAudible => Enabled && _lengthCounter > 0 && _timerPeriod >= 8 && SweepTargetPeriod <= 0x7FF && CurrentVolume > 0;

        public int Output
        {
            get
            {
                if (!Enabled || _lengthCounter == 0 || _timerPeriod < 8 || SweepTargetPeriod > 0x7FF)
                {
                    return 0;
                }

                return DutySequences[_dutyMode][_sequenceStep] == 0 ? 0 : CurrentVolume;
            }
        }

        public void Reset()
        {
            _enabled = false;
            _dutyMode = 0;
            _lengthCounterHalt = false;
            _constantVolume = false;
            _envelopePeriod = 0;
            _envelopeStart = false;
            _envelopeDivider = 0;
            _envelopeDecayLevel = 15;
            _sweepEnabled = false;
            _sweepDividerPeriod = 0;
            _sweepNegate = false;
            _sweepShift = 0;
            _sweepReload = false;
            _sweepDivider = 0;
            _timerPeriod = 0;
            _timerCounter = 0;
            _sequenceStep = 0;
            _lengthCounter = 0;
            _triggerVersion = 0;
        }

        public void WriteControl(byte value)
        {
            _dutyMode = (value >> 6) & 0x03;
            _lengthCounterHalt = (value & 0x20) != 0;
            _constantVolume = (value & 0x10) != 0;
            _envelopePeriod = (byte)(value & 0x0F);
            _envelopeStart = true;
        }

        public void WriteSweep(byte value)
        {
            _sweepEnabled = (value & 0x80) != 0;
            _sweepDividerPeriod = (byte)((value >> 4) & 0x07);
            _sweepNegate = (value & 0x08) != 0;
            _sweepShift = (byte)(value & 0x07);
            _sweepReload = true;
        }

        public void WriteTimerLow(byte value)
        {
            _timerPeriod = (ushort)((_timerPeriod & 0x0700) | value);
        }

        public void WriteTimerHigh(byte value, IReadOnlyList<byte> lengthTable)
        {
            _timerPeriod = (ushort)((_timerPeriod & 0x00FF) | ((value & 0x07) << 8));
            if (_enabled)
            {
                _lengthCounter = lengthTable[(value >> 3) & 0x1F];
            }

            _sequenceStep = 0;
            _envelopeStart = true;
            _triggerVersion++;
        }

        public void ClockTimer()
        {
            if (_timerCounter == 0)
            {
                _timerCounter = _timerPeriod;
                _sequenceStep = (byte)((_sequenceStep + 1) & 0x07);
            }
            else
            {
                _timerCounter--;
            }
        }

        public void ClockEnvelope()
        {
            if (_envelopeStart)
            {
                _envelopeStart = false;
                _envelopeDecayLevel = 15;
                _envelopeDivider = _envelopePeriod;
                return;
            }

            if (_envelopeDivider == 0)
            {
                _envelopeDivider = _envelopePeriod;
                if (_envelopeDecayLevel == 0)
                {
                    if (_lengthCounterHalt)
                    {
                        _envelopeDecayLevel = 15;
                    }
                }
                else
                {
                    _envelopeDecayLevel--;
                }
            }
            else
            {
                _envelopeDivider--;
            }
        }

        public void ClockLengthCounter()
        {
            if (!_lengthCounterHalt && _lengthCounter > 0)
            {
                _lengthCounter--;
            }
        }

        public void ClockSweep()
        {
            if (_sweepDivider == 0 && _sweepEnabled && _sweepShift != 0 && _timerPeriod >= 8 && SweepTargetPeriod <= 0x7FF)
            {
                _timerPeriod = (ushort)SweepTargetPeriod;
            }

            if (_sweepReload || _sweepDivider == 0)
            {
                _sweepDivider = _sweepDividerPeriod;
                _sweepReload = false;
            }
            else
            {
                _sweepDivider--;
            }
        }

        private int SweepTargetPeriod
        {
            get
            {
                var change = _timerPeriod >> _sweepShift;
                if (_sweepNegate)
                {
                    return _timerPeriod - change - (_onesComplementNegate ? 1 : 0);
                }

                return _timerPeriod + change;
            }
        }

        private int CurrentVolume => _constantVolume ? _envelopePeriod : _envelopeDecayLevel;
    }

    private sealed class TriangleChannel
    {
        private bool _enabled;
        private bool _controlFlag;
        private byte _linearCounterReload;
        private byte _linearCounter;
        private bool _linearReloadFlag;
        private ushort _timerPeriod;
        private ushort _timerCounter;
        private byte _sequenceStep;
        private byte _lengthCounter;
        private int _triggerVersion;

        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (!_enabled)
                {
                    _lengthCounter = 0;
                }
            }
        }

        public int LengthCounter => _lengthCounter;

        public int TimerPeriod => _timerPeriod;

        public int TriggerVersion => _triggerVersion;

        public int Volume => IsAudible ? 15 : 0;

        public bool IsAudible => Enabled && _lengthCounter > 0 && _linearCounter > 0 && _timerPeriod > 1;

        public int Output => !Enabled || _lengthCounter == 0 || _linearCounter == 0 ? 0 : TriangleSequence[_sequenceStep];

        public void Reset()
        {
            _enabled = false;
            _controlFlag = false;
            _linearCounterReload = 0;
            _linearCounter = 0;
            _linearReloadFlag = false;
            _timerPeriod = 0;
            _timerCounter = 0;
            _sequenceStep = 0;
            _lengthCounter = 0;
            _triggerVersion = 0;
        }

        public void WriteControl(byte value)
        {
            _controlFlag = (value & 0x80) != 0;
            _linearCounterReload = (byte)(value & 0x7F);
        }

        public void WriteTimerLow(byte value)
        {
            _timerPeriod = (ushort)((_timerPeriod & 0x0700) | value);
        }

        public void WriteTimerHigh(byte value, IReadOnlyList<byte> lengthTable)
        {
            _timerPeriod = (ushort)((_timerPeriod & 0x00FF) | ((value & 0x07) << 8));
            if (_enabled)
            {
                _lengthCounter = lengthTable[(value >> 3) & 0x1F];
            }

            _linearReloadFlag = true;
            _triggerVersion++;
        }

        public void ClockTimer()
        {
            if (_timerCounter == 0)
            {
                _timerCounter = _timerPeriod;
                if (_lengthCounter > 0 && _linearCounter > 0)
                {
                    _sequenceStep = (byte)((_sequenceStep + 1) & 0x1F);
                }
            }
            else
            {
                _timerCounter--;
            }
        }

        public void ClockLinearCounter()
        {
            if (_linearReloadFlag)
            {
                _linearCounter = _linearCounterReload;
            }
            else if (_linearCounter > 0)
            {
                _linearCounter--;
            }

            if (!_controlFlag)
            {
                _linearReloadFlag = false;
            }
        }

        public void ClockLengthCounter()
        {
            if (!_controlFlag && _lengthCounter > 0)
            {
                _lengthCounter--;
            }
        }
    }

    private sealed class NoiseChannel
    {
        private bool _enabled;
        private bool _lengthCounterHalt;
        private bool _constantVolume;
        private byte _envelopePeriod;
        private bool _envelopeStart;
        private byte _envelopeDivider;
        private byte _envelopeDecayLevel = 15;
        private bool _modeFlag;
        private ushort _timerPeriod;
        private ushort _timerCounter;
        private ushort _shiftRegister = 1;
        private byte _lengthCounter;
        private byte _periodIndex;
        private int _triggerVersion;

        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (!_enabled)
                {
                    _lengthCounter = 0;
                }
            }
        }

        public int LengthCounter => _lengthCounter;

        public int PeriodIndex => _periodIndex;

        public bool ModeFlag => _modeFlag;

        public int TriggerVersion => _triggerVersion;

        public int Volume => IsAudible ? CurrentVolume : 0;

        public bool IsAudible => Enabled && _lengthCounter > 0 && CurrentVolume > 0;

        public int Output => !Enabled || _lengthCounter == 0 || (_shiftRegister & 0x01) != 0 ? 0 : CurrentVolume;

        public void Reset()
        {
            _enabled = false;
            _lengthCounterHalt = false;
            _constantVolume = false;
            _envelopePeriod = 0;
            _envelopeStart = false;
            _envelopeDivider = 0;
            _envelopeDecayLevel = 15;
            _modeFlag = false;
            _timerPeriod = NoisePeriods[0];
            _timerCounter = 0;
            _shiftRegister = 1;
            _lengthCounter = 0;
            _periodIndex = 0;
            _triggerVersion = 0;
        }

        public void WriteControl(byte value)
        {
            _lengthCounterHalt = (value & 0x20) != 0;
            _constantVolume = (value & 0x10) != 0;
            _envelopePeriod = (byte)(value & 0x0F);
            _envelopeStart = true;
        }

        public void WritePeriod(byte value)
        {
            _modeFlag = (value & 0x80) != 0;
            _periodIndex = (byte)(value & 0x0F);
            _timerPeriod = NoisePeriods[_periodIndex];
        }

        public void WriteLength(byte value, IReadOnlyList<byte> lengthTable)
        {
            if (_enabled)
            {
                _lengthCounter = lengthTable[(value >> 3) & 0x1F];
            }

            _envelopeStart = true;
            _triggerVersion++;
        }

        public void ClockTimer()
        {
            if (_timerCounter == 0)
            {
                _timerCounter = _timerPeriod;
                var tap = _modeFlag ? 6 : 1;
                var feedback = (ushort)((_shiftRegister & 0x01) ^ ((_shiftRegister >> tap) & 0x01));
                _shiftRegister = (ushort)((_shiftRegister >> 1) | (feedback << 14));
            }
            else
            {
                _timerCounter--;
            }
        }

        public void ClockEnvelope()
        {
            if (_envelopeStart)
            {
                _envelopeStart = false;
                _envelopeDecayLevel = 15;
                _envelopeDivider = _envelopePeriod;
                return;
            }

            if (_envelopeDivider == 0)
            {
                _envelopeDivider = _envelopePeriod;
                if (_envelopeDecayLevel == 0)
                {
                    if (_lengthCounterHalt)
                    {
                        _envelopeDecayLevel = 15;
                    }
                }
                else
                {
                    _envelopeDecayLevel--;
                }
            }
            else
            {
                _envelopeDivider--;
            }
        }

        public void ClockLengthCounter()
        {
            if (!_lengthCounterHalt && _lengthCounter > 0)
            {
                _lengthCounter--;
            }
        }

        private int CurrentVolume => _constantVolume ? _envelopePeriod : _envelopeDecayLevel;
    }

    private sealed class DmcChannel
    {
        private bool _enabled;
        private bool _loopFlag;
        private byte _rateIndex;
        private byte _outputLevel;
        private byte _sampleAddress;
        private byte _sampleLength;
        private int _triggerVersion;

        public bool Enabled => _enabled;

        public bool IsActive => _enabled || _outputLevel > 0;

        public int RateIndex => _rateIndex;

        public int OutputLevel => _outputLevel;

        public int SampleLength => _sampleLength;

        public int TriggerVersion => _triggerVersion;

        public void Reset()
        {
            _enabled = false;
            _loopFlag = false;
            _rateIndex = 0;
            _outputLevel = 0;
            _sampleAddress = 0;
            _sampleLength = 0;
            _triggerVersion = 0;
        }

        public void WriteControl(byte value)
        {
            _loopFlag = (value & 0x40) != 0;
            _rateIndex = (byte)(value & 0x0F);
        }

        public void WriteDirectLoad(byte value)
        {
            var level = (byte)(value & 0x7F);
            if (!_enabled && level > 0 && Math.Abs(level - _outputLevel) >= 8)
            {
                _triggerVersion++;
            }

            _outputLevel = level;
        }

        public void WriteSampleAddress(byte value)
        {
            _sampleAddress = value;
        }

        public void WriteSampleLength(byte value)
        {
            _sampleLength = value;
        }

        public void SetEnabled(bool enabled)
        {
            if (!_enabled && enabled && (_sampleLength > 0 || _outputLevel > 0 || _loopFlag))
            {
                _triggerVersion++;
            }

            _enabled = enabled;
        }
    }
}
