namespace NesEmu.Core;

public sealed partial class Cpu6502
{
    private readonly ICpuBus _bus;
    private readonly ICpuCycleObserver? _cycleObserver;
    private bool _nmiRequested;
    private bool _nmiLineLow;
    private bool _irqLine;
    private bool _halted;
    private PendingInterrupt _pendingInterrupt;

    private enum PendingInterrupt
    {
        None,
        Irq,
        Nmi
    }

    public Cpu6502(ICpuBus bus)
    {
        _bus = bus;
        _cycleObserver = bus as ICpuCycleObserver;
        Status = CpuStatusFlags.InterruptDisable | CpuStatusFlags.Unused;
        StackPointer = 0xFD;
    }

    public byte Accumulator { get; private set; }
    public byte X { get; private set; }
    public byte Y { get; private set; }
    public byte StackPointer { get; private set; }
    public ushort ProgramCounter { get; private set; }
    public CpuStatusFlags Status { get; private set; }
    public bool UsesInlineCycleClock => _cycleObserver is not null;

    public void Reset()
    {
        StackPointer = 0xFD;
        Status = CpuStatusFlags.InterruptDisable | CpuStatusFlags.Unused;
        ProgramCounter = ReadWordRaw(0xFFFC);
        _nmiRequested = false;
        _nmiLineLow = false;
        _halted = false;
        _pendingInterrupt = PendingInterrupt.None;
    }

    public void RequestNmi() => _nmiRequested = true;
    public void SetNmiLine(bool low)
    {
        if (!_nmiLineLow && low)
        {
            _nmiRequested = true;
        }

        _nmiLineLow = low;
    }

    public void SetIrqLine(bool asserted) => _irqLine = asserted;

    public int Step()
    {
        if (_halted)
        {
            return 1;
        }

        if (_pendingInterrupt != PendingInterrupt.None)
        {
            var pendingInterrupt = _pendingInterrupt;
            _pendingInterrupt = PendingInterrupt.None;
            ServiceInterruptSequence(pendingInterrupt);
            return 7;
        }

        var opcode = Read(ProgramCounter++);
        return opcode switch
        {
            0x00 => ExecuteBrk(),
            0x01 => ExecuteOra(Read(IndexedIndirectAddress()), 6),
            0x02 => ExecuteKil(),
            0x03 => ExecuteSlo(IndexedIndirectAddress(), 8),
            0x04 => ExecuteNoOp(ZeroPageAddress(), 3),
            0x05 => ExecuteOra(Read(ZeroPageAddress()), 3),
            0x06 => ExecuteModify(ZeroPageAddress(), ShiftLeft, 5),
            0x07 => ExecuteSlo(ZeroPageAddress(), 5),
            0x08 => ExecutePhp(),
            0x09 => ExecuteOra(Read(ImmediateAddress()), 2),
            0x0A => ExecuteAccumulator(ShiftLeft, 2),
            0x0B => ExecuteAnc(Read(ImmediateAddress()), 2),
            0x0C => ExecuteNoOp(AbsoluteAddress(), 4),
            0x0D => ExecuteOra(Read(AbsoluteAddress()), 4),
            0x0E => ExecuteModify(AbsoluteAddress(), ShiftLeft, 6),
            0x0F => ExecuteSlo(AbsoluteAddress(), 6),

            0x10 => ExecuteBranch(!GetFlag(CpuStatusFlags.Negative)),
            0x11 => ExecuteOra(Read(IndirectIndexedAddress(out var p11)), 5 + p11),
            0x12 => ExecuteKil(),
            0x13 => ExecuteSlo(IndirectIndexedModifyAddress(), 8),
            0x14 => ExecuteNoOp(ZeroPageXAddress(), 4),
            0x15 => ExecuteOra(Read(ZeroPageXAddress()), 4),
            0x16 => ExecuteModify(ZeroPageXAddress(), ShiftLeft, 6),
            0x17 => ExecuteSlo(ZeroPageXAddress(), 6),
            0x18 => ExecuteFlag(CpuStatusFlags.Carry, false),
            0x19 => ExecuteOra(Read(AbsoluteYAddress(out var p19)), 4 + p19),
            0x1A => ExecuteNoOp(2),
            0x1B => ExecuteSlo(AbsoluteYModifyAddress(), 7),
            0x1C => ExecuteNoOp(AbsoluteXAddress(out var p1C), 4 + p1C),
            0x1D => ExecuteOra(Read(AbsoluteXAddress(out var p1D)), 4 + p1D),
            0x1E => ExecuteModify(AbsoluteXModifyAddress(), ShiftLeft, 7),
            0x1F => ExecuteSlo(AbsoluteXModifyAddress(), 7),

            0x20 => ExecuteJsr(),
            0x21 => ExecuteAnd(Read(IndexedIndirectAddress()), 6),
            0x22 => ExecuteKil(),
            0x23 => ExecuteRla(IndexedIndirectAddress(), 8),
            0x24 => ExecuteBit(Read(ZeroPageAddress()), 3),
            0x25 => ExecuteAnd(Read(ZeroPageAddress()), 3),
            0x26 => ExecuteModify(ZeroPageAddress(), RotateLeft, 5),
            0x27 => ExecuteRla(ZeroPageAddress(), 5),
            0x28 => ExecutePlp(),
            0x29 => ExecuteAnd(Read(ImmediateAddress()), 2),
            0x2A => ExecuteAccumulator(RotateLeft, 2),
            0x2B => ExecuteAnc(Read(ImmediateAddress()), 2),
            0x2C => ExecuteBit(Read(AbsoluteAddress()), 4),
            0x2D => ExecuteAnd(Read(AbsoluteAddress()), 4),
            0x2E => ExecuteModify(AbsoluteAddress(), RotateLeft, 6),
            0x2F => ExecuteRla(AbsoluteAddress(), 6),

            0x30 => ExecuteBranch(GetFlag(CpuStatusFlags.Negative)),
            0x31 => ExecuteAnd(Read(IndirectIndexedAddress(out var p31)), 5 + p31),
            0x32 => ExecuteKil(),
            0x33 => ExecuteRla(IndirectIndexedModifyAddress(), 8),
            0x34 => ExecuteNoOp(ZeroPageXAddress(), 4),
            0x35 => ExecuteAnd(Read(ZeroPageXAddress()), 4),
            0x36 => ExecuteModify(ZeroPageXAddress(), RotateLeft, 6),
            0x37 => ExecuteRla(ZeroPageXAddress(), 6),
            0x38 => ExecuteFlag(CpuStatusFlags.Carry, true),
            0x39 => ExecuteAnd(Read(AbsoluteYAddress(out var p39)), 4 + p39),
            0x3A => ExecuteNoOp(2),
            0x3B => ExecuteRla(AbsoluteYModifyAddress(), 7),
            0x3C => ExecuteNoOp(AbsoluteXAddress(out var p3C), 4 + p3C),
            0x3D => ExecuteAnd(Read(AbsoluteXAddress(out var p3D)), 4 + p3D),
            0x3E => ExecuteModify(AbsoluteXModifyAddress(), RotateLeft, 7),
            0x3F => ExecuteRla(AbsoluteXModifyAddress(), 7),

            0x40 => ExecuteRti(),
            0x41 => ExecuteEor(Read(IndexedIndirectAddress()), 6),
            0x42 => ExecuteKil(),
            0x43 => ExecuteSre(IndexedIndirectAddress(), 8),
            0x44 => ExecuteNoOp(ZeroPageAddress(), 3),
            0x45 => ExecuteEor(Read(ZeroPageAddress()), 3),
            0x46 => ExecuteModify(ZeroPageAddress(), ShiftRight, 5),
            0x47 => ExecuteSre(ZeroPageAddress(), 5),
            0x48 => ExecutePha(),
            0x49 => ExecuteEor(Read(ImmediateAddress()), 2),
            0x4A => ExecuteAccumulator(ShiftRight, 2),
            0x4B => ExecuteAlr(Read(ImmediateAddress()), 2),
            0x4C => ExecuteJmp(JumpAbsoluteAddress(), 3),
            0x4D => ExecuteEor(Read(AbsoluteAddress()), 4),
            0x4E => ExecuteModify(AbsoluteAddress(), ShiftRight, 6),
            0x4F => ExecuteSre(AbsoluteAddress(), 6),

            0x50 => ExecuteBranch(!GetFlag(CpuStatusFlags.Overflow)),
            0x51 => ExecuteEor(Read(IndirectIndexedAddress(out var p51)), 5 + p51),
            0x52 => ExecuteKil(),
            0x53 => ExecuteSre(IndirectIndexedModifyAddress(), 8),
            0x54 => ExecuteNoOp(ZeroPageXAddress(), 4),
            0x55 => ExecuteEor(Read(ZeroPageXAddress()), 4),
            0x56 => ExecuteModify(ZeroPageXAddress(), ShiftRight, 6),
            0x57 => ExecuteSre(ZeroPageXAddress(), 6),
            0x58 => ExecuteFlag(CpuStatusFlags.InterruptDisable, false),
            0x59 => ExecuteEor(Read(AbsoluteYAddress(out var p59)), 4 + p59),
            0x5A => ExecuteNoOp(2),
            0x5B => ExecuteSre(AbsoluteYModifyAddress(), 7),
            0x5C => ExecuteNoOp(AbsoluteXAddress(out var p5C), 4 + p5C),
            0x5D => ExecuteEor(Read(AbsoluteXAddress(out var p5D)), 4 + p5D),
            0x5E => ExecuteModify(AbsoluteXModifyAddress(), ShiftRight, 7),
            0x5F => ExecuteSre(AbsoluteXModifyAddress(), 7),

            0x60 => ExecuteRts(),
            0x61 => ExecuteAdc(Read(IndexedIndirectAddress()), 6),
            0x62 => ExecuteKil(),
            0x63 => ExecuteRra(IndexedIndirectAddress(), 8),
            0x64 => ExecuteNoOp(ZeroPageAddress(), 3),
            0x65 => ExecuteAdc(Read(ZeroPageAddress()), 3),
            0x66 => ExecuteModify(ZeroPageAddress(), RotateRight, 5),
            0x67 => ExecuteRra(ZeroPageAddress(), 5),
            0x68 => ExecutePla(),
            0x69 => ExecuteAdc(Read(ImmediateAddress()), 2),
            0x6A => ExecuteAccumulator(RotateRight, 2),
            0x6B => ExecuteArr(Read(ImmediateAddress()), 2),
            0x6C => ExecuteJmp(JumpIndirectAddress(), 5),
            0x6D => ExecuteAdc(Read(AbsoluteAddress()), 4),
            0x6E => ExecuteModify(AbsoluteAddress(), RotateRight, 6),
            0x6F => ExecuteRra(AbsoluteAddress(), 6),

            0x70 => ExecuteBranch(GetFlag(CpuStatusFlags.Overflow)),
            0x71 => ExecuteAdc(Read(IndirectIndexedAddress(out var p71)), 5 + p71),
            0x72 => ExecuteKil(),
            0x73 => ExecuteRra(IndirectIndexedModifyAddress(), 8),
            0x74 => ExecuteNoOp(ZeroPageXAddress(), 4),
            0x75 => ExecuteAdc(Read(ZeroPageXAddress()), 4),
            0x76 => ExecuteModify(ZeroPageXAddress(), RotateRight, 6),
            0x77 => ExecuteRra(ZeroPageXAddress(), 6),
            0x78 => ExecuteFlag(CpuStatusFlags.InterruptDisable, true),
            0x79 => ExecuteAdc(Read(AbsoluteYAddress(out var p79)), 4 + p79),
            0x7A => ExecuteNoOp(2),
            0x7B => ExecuteRra(AbsoluteYModifyAddress(), 7),
            0x7C => ExecuteNoOp(AbsoluteXAddress(out var p7C), 4 + p7C),
            0x7D => ExecuteAdc(Read(AbsoluteXAddress(out var p7D)), 4 + p7D),
            0x7E => ExecuteModify(AbsoluteXModifyAddress(), RotateRight, 7),
            0x7F => ExecuteRra(AbsoluteXModifyAddress(), 7),

            0x80 => ExecuteNoOp(ImmediateAddress(), 2),
            0x81 => ExecuteStore(IndexedIndirectAddress(), Accumulator, 6),
            0x82 => ExecuteNoOp(ImmediateAddress(), 2),
            0x83 => ExecuteSax(IndexedIndirectAddress(), 6),
            0x84 => ExecuteStore(ZeroPageAddress(), Y, 3),
            0x85 => ExecuteStore(ZeroPageAddress(), Accumulator, 3),
            0x86 => ExecuteStore(ZeroPageAddress(), X, 3),
            0x87 => ExecuteSax(ZeroPageAddress(), 3),
            0x88 => ExecuteDecrementY(2),
            0x89 => ExecuteNoOp(ImmediateAddress(), 2),
            0x8A => ExecuteTransferToA(X, 2),
            0x8B => ExecuteAne(Read(ImmediateAddress()), 2),
            0x8C => ExecuteStore(AbsoluteAddress(), Y, 4),
            0x8D => ExecuteStore(AbsoluteAddress(), Accumulator, 4),
            0x8E => ExecuteStore(AbsoluteAddress(), X, 4),
            0x8F => ExecuteSax(AbsoluteAddress(), 4),

            0x90 => ExecuteBranch(!GetFlag(CpuStatusFlags.Carry)),
            0x91 => ExecuteStore(IndirectIndexedAddress(out _), Accumulator, 6),
            0x92 => ExecuteKil(),
            0x93 => ExecuteSha(IndirectIndexedWriteAddressWithBase(), 6),
            0x94 => ExecuteStore(ZeroPageXAddress(), Y, 4),
            0x95 => ExecuteStore(ZeroPageXAddress(), Accumulator, 4),
            0x96 => ExecuteStore(ZeroPageYAddress(), X, 4),
            0x97 => ExecuteSax(ZeroPageYAddress(), 4),
            0x98 => ExecuteTransferToA(Y, 2),
            0x99 => ExecuteStore(AbsoluteYWriteAddressWithBase().Address, Accumulator, 5),
            0x9A => ExecuteTransferToStackPointer(X, 2),
            0x9B => ExecuteTas(AbsoluteYWriteAddressWithBase(), 5),
            0x9C => ExecuteShy(AbsoluteXWriteAddressWithBase(), 5),
            0x9D => ExecuteStore(AbsoluteXWriteAddressWithBase().Address, Accumulator, 5),
            0x9E => ExecuteShx(AbsoluteYWriteAddressWithBase(), 5),
            0x9F => ExecuteSha(AbsoluteYWriteAddressWithBase(), 5),

            0xA0 => ExecuteLoadY(Read(ImmediateAddress()), 2),
            0xA1 => ExecuteLoadA(Read(IndexedIndirectAddress()), 6),
            0xA2 => ExecuteLoadX(Read(ImmediateAddress()), 2),
            0xA3 => ExecuteLax(Read(IndexedIndirectAddress()), 6),
            0xA4 => ExecuteLoadY(Read(ZeroPageAddress()), 3),
            0xA5 => ExecuteLoadA(Read(ZeroPageAddress()), 3),
            0xA6 => ExecuteLoadX(Read(ZeroPageAddress()), 3),
            0xA7 => ExecuteLax(Read(ZeroPageAddress()), 3),
            0xA8 => ExecuteTransferToY(Accumulator, 2),
            0xA9 => ExecuteLoadA(Read(ImmediateAddress()), 2),
            0xAA => ExecuteTransferToX(Accumulator, 2),
            0xAB => ExecuteLxa(Read(ImmediateAddress()), 2),
            0xAC => ExecuteLoadY(Read(AbsoluteAddress()), 4),
            0xAD => ExecuteLoadA(Read(AbsoluteAddress()), 4),
            0xAE => ExecuteLoadX(Read(AbsoluteAddress()), 4),
            0xAF => ExecuteLax(Read(AbsoluteAddress()), 4),

            0xB0 => ExecuteBranch(GetFlag(CpuStatusFlags.Carry)),
            0xB1 => ExecuteLoadA(Read(IndirectIndexedAddress(out var pB1)), 5 + pB1),
            0xB2 => ExecuteKil(),
            0xB3 => ExecuteLax(Read(IndirectIndexedAddress(out var pB3)), 5 + pB3),
            0xB4 => ExecuteLoadY(Read(ZeroPageXAddress()), 4),
            0xB5 => ExecuteLoadA(Read(ZeroPageXAddress()), 4),
            0xB6 => ExecuteLoadX(Read(ZeroPageYAddress()), 4),
            0xB7 => ExecuteLax(Read(ZeroPageYAddress()), 4),
            0xB8 => ExecuteFlag(CpuStatusFlags.Overflow, false),
            0xB9 => ExecuteLoadA(Read(AbsoluteYAddress(out var pB9)), 4 + pB9),
            0xBA => ExecuteTransferToX(StackPointer, 2),
            0xBB => ExecuteLas(Read(AbsoluteYAddress(out var pBB)), 4 + pBB),
            0xBC => ExecuteLoadY(Read(AbsoluteXAddress(out var pBC)), 4 + pBC),
            0xBD => ExecuteLoadA(Read(AbsoluteXAddress(out var pBD)), 4 + pBD),
            0xBE => ExecuteLoadX(Read(AbsoluteYAddress(out var pBE)), 4 + pBE),
            0xBF => ExecuteLax(Read(AbsoluteYAddress(out var pBF)), 4 + pBF),

            0xC0 => ExecuteCompare(Y, Read(ImmediateAddress()), 2),
            0xC1 => ExecuteCompare(Accumulator, Read(IndexedIndirectAddress()), 6),
            0xC2 => ExecuteNoOp(ImmediateAddress(), 2),
            0xC3 => ExecuteDcp(IndexedIndirectAddress(), 8),
            0xC4 => ExecuteCompare(Y, Read(ZeroPageAddress()), 3),
            0xC5 => ExecuteCompare(Accumulator, Read(ZeroPageAddress()), 3),
            0xC6 => ExecuteModify(ZeroPageAddress(), DecrementValue, 5),
            0xC7 => ExecuteDcp(ZeroPageAddress(), 5),
            0xC8 => ExecuteIncrementY(2),
            0xC9 => ExecuteCompare(Accumulator, Read(ImmediateAddress()), 2),
            0xCA => ExecuteDecrementX(2),
            0xCB => ExecuteAxs(Read(ImmediateAddress()), 2),
            0xCC => ExecuteCompare(Y, Read(AbsoluteAddress()), 4),
            0xCD => ExecuteCompare(Accumulator, Read(AbsoluteAddress()), 4),
            0xCE => ExecuteModify(AbsoluteAddress(), DecrementValue, 6),
            0xCF => ExecuteDcp(AbsoluteAddress(), 6),

            0xD0 => ExecuteBranch(!GetFlag(CpuStatusFlags.Zero)),
            0xD1 => ExecuteCompare(Accumulator, Read(IndirectIndexedAddress(out var pD1)), 5 + pD1),
            0xD2 => ExecuteKil(),
            0xD3 => ExecuteDcp(IndirectIndexedModifyAddress(), 8),
            0xD4 => ExecuteNoOp(ZeroPageXAddress(), 4),
            0xD5 => ExecuteCompare(Accumulator, Read(ZeroPageXAddress()), 4),
            0xD6 => ExecuteModify(ZeroPageXAddress(), DecrementValue, 6),
            0xD7 => ExecuteDcp(ZeroPageXAddress(), 6),
            0xD8 => ExecuteFlag(CpuStatusFlags.Decimal, false),
            0xD9 => ExecuteCompare(Accumulator, Read(AbsoluteYAddress(out var pD9)), 4 + pD9),
            0xDA => ExecuteNoOp(2),
            0xDB => ExecuteDcp(AbsoluteYModifyAddress(), 7),
            0xDC => ExecuteNoOp(AbsoluteXAddress(out var pDC), 4 + pDC),
            0xDD => ExecuteCompare(Accumulator, Read(AbsoluteXAddress(out var pDD)), 4 + pDD),
            0xDE => ExecuteModify(AbsoluteXModifyAddress(), DecrementValue, 7),
            0xDF => ExecuteDcp(AbsoluteXModifyAddress(), 7),

            0xE0 => ExecuteCompare(X, Read(ImmediateAddress()), 2),
            0xE1 => ExecuteSbc(Read(IndexedIndirectAddress()), 6),
            0xE2 => ExecuteNoOp(ImmediateAddress(), 2),
            0xE3 => ExecuteIsc(IndexedIndirectAddress(), 8),
            0xE4 => ExecuteCompare(X, Read(ZeroPageAddress()), 3),
            0xE5 => ExecuteSbc(Read(ZeroPageAddress()), 3),
            0xE6 => ExecuteModify(ZeroPageAddress(), IncrementValue, 5),
            0xE7 => ExecuteIsc(ZeroPageAddress(), 5),
            0xE8 => ExecuteIncrementX(2),
            0xE9 => ExecuteSbc(Read(ImmediateAddress()), 2),
            0xEA => ExecuteNoOp(2),
            0xEB => ExecuteSbc(Read(ImmediateAddress()), 2),
            0xEC => ExecuteCompare(X, Read(AbsoluteAddress()), 4),
            0xED => ExecuteSbc(Read(AbsoluteAddress()), 4),
            0xEE => ExecuteModify(AbsoluteAddress(), IncrementValue, 6),
            0xEF => ExecuteIsc(AbsoluteAddress(), 6),

            0xF0 => ExecuteBranch(GetFlag(CpuStatusFlags.Zero)),
            0xF1 => ExecuteSbc(Read(IndirectIndexedAddress(out var pF1)), 5 + pF1),
            0xF2 => ExecuteKil(),
            0xF3 => ExecuteIsc(IndirectIndexedModifyAddress(), 8),
            0xF4 => ExecuteNoOp(ZeroPageXAddress(), 4),
            0xF5 => ExecuteSbc(Read(ZeroPageXAddress()), 4),
            0xF6 => ExecuteModify(ZeroPageXAddress(), IncrementValue, 6),
            0xF7 => ExecuteIsc(ZeroPageXAddress(), 6),
            0xF8 => ExecuteFlag(CpuStatusFlags.Decimal, true),
            0xF9 => ExecuteSbc(Read(AbsoluteYAddress(out var pF9)), 4 + pF9),
            0xFA => ExecuteNoOp(2),
            0xFB => ExecuteIsc(AbsoluteYModifyAddress(), 7),
            0xFC => ExecuteNoOp(AbsoluteXAddress(out var pFC), 4 + pFC),
            0xFD => ExecuteSbc(Read(AbsoluteXAddress(out var pFD)), 4 + pFD),
            0xFE => ExecuteModify(AbsoluteXModifyAddress(), IncrementValue, 7),
            0xFF => ExecuteIsc(AbsoluteXModifyAddress(), 7)
        };
    }

    private int ExecuteKil() { ProgramCounter--; _halted = true; return 1; }

    private int ExecuteBrk()
    {
        DummyRead(ProgramCounter);
        ProgramCounter++;
        Push((byte)(ProgramCounter >> 8));
        Push((byte)ProgramCounter);
        var interruptVector = SelectInterruptVector(PendingInterrupt.Irq);
        Push(GetPushedStatus(setBreakFlag: true));
        SetFlag(CpuStatusFlags.InterruptDisable, true);
        ProgramCounter = ReadInterruptVector(interruptVector);
        return 7;
    }

    private int ExecuteOra(byte value, int cycles) { Accumulator |= value; SetZeroAndNegative(Accumulator); return cycles; }
    private int ExecuteAnd(byte value, int cycles) { Accumulator &= value; SetZeroAndNegative(Accumulator); return cycles; }
    private int ExecuteEor(byte value, int cycles) { Accumulator ^= value; SetZeroAndNegative(Accumulator); return cycles; }

    private int ExecuteAdc(byte value, int cycles)
    {
        var carry = GetFlag(CpuStatusFlags.Carry) ? 1 : 0;
        var sum = Accumulator + value + carry;
        SetFlag(CpuStatusFlags.Carry, sum > 0xFF);
        var result = (byte)sum;
        SetFlag(CpuStatusFlags.Overflow, ((Accumulator ^ result) & (value ^ result) & 0x80) != 0);
        Accumulator = result;
        SetZeroAndNegative(Accumulator);
        return cycles;
    }

    private int ExecuteSbc(byte value, int cycles) => ExecuteAdc((byte)~value, cycles);

    private int ExecuteBit(byte value, int cycles)
    {
        SetFlag(CpuStatusFlags.Zero, (Accumulator & value) == 0);
        SetFlag(CpuStatusFlags.Overflow, (value & 0x40) != 0);
        SetFlag(CpuStatusFlags.Negative, (value & 0x80) != 0);
        return cycles;
    }

    private int ExecuteJsr()
    {
        var low = Read(ProgramCounter++);
        DummyReadStack();
        Push((byte)(ProgramCounter >> 8));
        Push((byte)ProgramCounter);
        PollInterrupts();
        var high = Read(ProgramCounter++);
        ProgramCounter = (ushort)(low | (high << 8));
        return 6;
    }

    private int ExecuteJmp(ushort address, int cycles) { ProgramCounter = address; return cycles; }

    private int ExecuteRti()
    {
        DummyRead(ProgramCounter);
        DummyReadStack();
        Status = SanitizeStatus(Pop());
        PollInterrupts();
        ProgramCounter = ReadWordFromStack();
        return 6;
    }

    private int ExecuteRts()
    {
        DummyRead(ProgramCounter);
        DummyReadStack();
        var returnAddress = ReadWordFromStack();
        PollInterrupts();
        DummyRead(returnAddress);
        ProgramCounter = (ushort)(returnAddress + 1);
        return 6;
    }

    private int ExecutePhp()
    {
        DummyRead(ProgramCounter);
        PollInterrupts();
        Push(GetPushedStatus(setBreakFlag: true));
        return 3;
    }

    private int ExecutePlp()
    {
        DummyRead(ProgramCounter);
        DummyReadStack();
        PollInterrupts();
        Status = SanitizeStatus(Pop());
        return 4;
    }

    private int ExecutePha()
    {
        DummyRead(ProgramCounter);
        PollInterrupts();
        Push(Accumulator);
        return 3;
    }

    private int ExecutePla()
    {
        DummyRead(ProgramCounter);
        DummyReadStack();
        PollInterrupts();
        Accumulator = Pop();
        SetZeroAndNegative(Accumulator);
        return 4;
    }

    private int ExecuteBranch(bool condition)
    {
        PollInterrupts();
        var offset = unchecked((sbyte)Read(ProgramCounter++));
        if (!condition) return 2;
        var previous = ProgramCounter;
        var target = (ushort)(ProgramCounter + offset);
        DummyRead(ProgramCounter);
        if (IsPageCrossed(previous, target))
        {
            PollInterrupts();
            DummyRead(IndexedPageWrappedAddress(previous, target));
            ProgramCounter = target;
            return 4;
        }

        ProgramCounter = target;
        return 3;
    }

    private int ExecuteFlag(CpuStatusFlags flag, bool set)
    {
        DummyRead(ProgramCounter);
        PollInterrupts();
        SetFlag(flag, set);
        return 2;
    }

    private int ExecuteNoOp(int cycles)
    {
        if (cycles == 2)
        {
            DummyRead(ProgramCounter);
            PollInterrupts();
        }

        return cycles;
    }

    private int ExecuteNoOp(ushort address, int cycles) { Read(address); return cycles; }
    private int ExecuteStore(ushort address, byte value, int cycles) { Write(address, value); return cycles; }
    private int ExecuteLoadA(byte value, int cycles) { Accumulator = value; SetZeroAndNegative(Accumulator); return cycles; }
    private int ExecuteLoadX(byte value, int cycles) { X = value; SetZeroAndNegative(X); return cycles; }
    private int ExecuteLoadY(byte value, int cycles) { Y = value; SetZeroAndNegative(Y); return cycles; }

    private int ExecuteTransferToA(byte source, int cycles) { DummyRead(ProgramCounter); PollInterrupts(); Accumulator = source; SetZeroAndNegative(Accumulator); return cycles; }
    private int ExecuteTransferToX(byte source, int cycles) { DummyRead(ProgramCounter); PollInterrupts(); X = source; SetZeroAndNegative(X); return cycles; }
    private int ExecuteTransferToY(byte source, int cycles) { DummyRead(ProgramCounter); PollInterrupts(); Y = source; SetZeroAndNegative(Y); return cycles; }
    private int ExecuteTransferToStackPointer(byte source, int cycles) { DummyRead(ProgramCounter); PollInterrupts(); StackPointer = source; return cycles; }
    private int ExecuteCompare(byte register, byte value, int cycles) { CompareValues(register, value); return cycles; }
    private int ExecuteIncrementX(int cycles) { DummyRead(ProgramCounter); PollInterrupts(); X++; SetZeroAndNegative(X); return cycles; }
    private int ExecuteIncrementY(int cycles) { DummyRead(ProgramCounter); PollInterrupts(); Y++; SetZeroAndNegative(Y); return cycles; }
    private int ExecuteDecrementX(int cycles) { DummyRead(ProgramCounter); PollInterrupts(); X--; SetZeroAndNegative(X); return cycles; }
    private int ExecuteDecrementY(int cycles) { DummyRead(ProgramCounter); PollInterrupts(); Y--; SetZeroAndNegative(Y); return cycles; }
    private int ExecuteModify(ushort address, Func<byte, byte> operation, int cycles) { ReadModifyWrite(address, operation); return cycles; }
    private int ExecuteAccumulator(Func<byte, byte> operation, int cycles) { DummyRead(ProgramCounter); PollInterrupts(); Accumulator = operation(Accumulator); return cycles; }

    private byte ShiftLeft(byte value) { SetFlag(CpuStatusFlags.Carry, (value & 0x80) != 0); value <<= 1; SetZeroAndNegative(value); return value; }
    private byte ShiftRight(byte value) { SetFlag(CpuStatusFlags.Carry, (value & 0x01) != 0); value >>= 1; SetZeroAndNegative(value); return value; }

    private byte RotateLeft(byte value)
    {
        var carryIn = GetFlag(CpuStatusFlags.Carry) ? 1 : 0;
        var carryOut = (value & 0x80) != 0;
        value = (byte)((value << 1) | carryIn);
        SetFlag(CpuStatusFlags.Carry, carryOut);
        SetZeroAndNegative(value);
        return value;
    }

    private byte RotateRight(byte value)
    {
        var carryIn = GetFlag(CpuStatusFlags.Carry) ? 0x80 : 0;
        var carryOut = (value & 0x01) != 0;
        value = (byte)((value >> 1) | carryIn);
        SetFlag(CpuStatusFlags.Carry, carryOut);
        SetZeroAndNegative(value);
        return value;
    }

    private byte IncrementValue(byte value) { value++; SetZeroAndNegative(value); return value; }
    private byte DecrementValue(byte value) { value--; SetZeroAndNegative(value); return value; }
    private void CompareValues(byte register, byte value) { var result = register - value; SetFlag(CpuStatusFlags.Carry, register >= value); SetZeroAndNegative((byte)result); }

    private void ServiceInterruptSequence(PendingInterrupt pendingInterrupt)
    {
        DummyRead(ProgramCounter);
        DummyRead(ProgramCounter);
        Push((byte)(ProgramCounter >> 8));
        Push((byte)ProgramCounter);
        var interruptVector = SelectInterruptVector(pendingInterrupt);
        Push(GetPushedStatus(setBreakFlag: false));
        SetFlag(CpuStatusFlags.InterruptDisable, true);
        ProgramCounter = ReadInterruptVector(interruptVector);
    }

    private void Push(byte value) { Write((ushort)(0x0100 | StackPointer), value); StackPointer--; }
    private byte Pop() { StackPointer++; return Read((ushort)(0x0100 | StackPointer)); }
    private bool GetFlag(CpuStatusFlags flag) => (Status & flag) != 0;

    private void PollInterrupts()
    {
        if (_pendingInterrupt == PendingInterrupt.Nmi)
        {
            return;
        }

        if (_nmiRequested)
        {
            _pendingInterrupt = PendingInterrupt.Nmi;
            return;
        }

        if (_pendingInterrupt == PendingInterrupt.None &&
            _irqLine &&
            !GetFlag(CpuStatusFlags.InterruptDisable))
        {
            _pendingInterrupt = PendingInterrupt.Irq;
        }
    }

    private PendingInterrupt SelectInterruptVector(PendingInterrupt pendingInterrupt)
    {
        if (pendingInterrupt == PendingInterrupt.Nmi)
        {
            _nmiRequested = false;
            return PendingInterrupt.Nmi;
        }

        if (_nmiRequested)
        {
            _nmiRequested = false;
            return PendingInterrupt.Nmi;
        }

        return PendingInterrupt.Irq;
    }

    private byte GetPushedStatus(bool setBreakFlag)
    {
        var pushedStatus = Status | CpuStatusFlags.Unused;
        return (byte)(setBreakFlag
            ? pushedStatus | CpuStatusFlags.Break
            : pushedStatus & ~CpuStatusFlags.Break);
    }

    private ushort ReadInterruptVector(PendingInterrupt interruptVector) =>
        interruptVector == PendingInterrupt.Nmi ? ReadWord(0xFFFA) : ReadWord(0xFFFE);

    private void SetFlag(CpuStatusFlags flag, bool enabled)
    {
        Status = enabled ? Status | flag : Status & ~flag;
        Status |= CpuStatusFlags.Unused;
    }

    private void SetZeroAndNegative(byte value)
    {
        SetFlag(CpuStatusFlags.Zero, value == 0);
        SetFlag(CpuStatusFlags.Negative, (value & 0x80) != 0);
    }

    private CpuStatusFlags SanitizeStatus(byte value) =>
        (CpuStatusFlags)((value & ~(byte)CpuStatusFlags.Break) | (byte)CpuStatusFlags.Unused);

    private void BeginCpuCycle()
    {
    }
}
