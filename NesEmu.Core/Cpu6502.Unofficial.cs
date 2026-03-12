namespace NesEmu.Core;

public sealed partial class Cpu6502
{
    // XAA/LXA are unstable on real NMOS parts. This matches a common 6502 approximation.
    private const byte UnofficialImmediateMask = 0xEE;

    private int ExecuteLax(byte value, int cycles)
    {
        Accumulator = value;
        X = value;
        SetZeroAndNegative(value);
        return cycles;
    }

    private int ExecuteSlo(ushort address, int cycles)
    {
        var result = ReadModifyWrite(address, ShiftLeft);
        Accumulator |= result;
        SetZeroAndNegative(Accumulator);
        return cycles;
    }

    private int ExecuteRla(ushort address, int cycles)
    {
        var result = ReadModifyWrite(address, RotateLeft);
        Accumulator &= result;
        SetZeroAndNegative(Accumulator);
        return cycles;
    }

    private int ExecuteSre(ushort address, int cycles)
    {
        var result = ReadModifyWrite(address, ShiftRight);
        Accumulator ^= result;
        SetZeroAndNegative(Accumulator);
        return cycles;
    }

    private int ExecuteRra(ushort address, int cycles)
    {
        var result = ReadModifyWrite(address, RotateRight);
        return ExecuteAdc(result, cycles);
    }

    private int ExecuteSax(ushort address, int cycles) { Write(address, (byte)(Accumulator & X)); return cycles; }

    private int ExecuteDcp(ushort address, int cycles)
    {
        var result = ReadModifyWrite(address, DecrementValue);
        CompareValues(Accumulator, result);
        return cycles;
    }

    private int ExecuteIsc(ushort address, int cycles)
    {
        var result = ReadModifyWrite(address, IncrementValue);
        return ExecuteSbc(result, cycles);
    }

    private int ExecuteAnc(byte value, int cycles)
    {
        Accumulator &= value;
        SetZeroAndNegative(Accumulator);
        SetFlag(CpuStatusFlags.Carry, (Accumulator & 0x80) != 0);
        return cycles;
    }

    private int ExecuteAlr(byte value, int cycles)
    {
        Accumulator &= value;
        Accumulator = ShiftRight(Accumulator);
        return cycles;
    }

    private int ExecuteArr(byte value, int cycles)
    {
        var carryIn = GetFlag(CpuStatusFlags.Carry) ? 0x80 : 0;
        var anded = (byte)(Accumulator & value);
        Accumulator = (byte)((anded >> 1) | carryIn);
        SetZeroAndNegative(Accumulator);
        SetFlag(CpuStatusFlags.Carry, (Accumulator & 0x40) != 0);
        SetFlag(CpuStatusFlags.Overflow, ((Accumulator >> 6) & 0x01) != ((Accumulator >> 5) & 0x01));
        return cycles;
    }

    private int ExecuteAne(byte value, int cycles)
    {
        Accumulator = (byte)(((Accumulator | UnofficialImmediateMask) & X) & value);
        SetZeroAndNegative(Accumulator);
        return cycles;
    }

    private int ExecuteLxa(byte value, int cycles)
    {
        Accumulator = (byte)((Accumulator | UnofficialImmediateMask) & value);
        X = Accumulator;
        SetZeroAndNegative(Accumulator);
        return cycles;
    }

    private int ExecuteAxs(byte value, int cycles)
    {
        var masked = (byte)(Accumulator & X);
        var result = masked - value;
        SetFlag(CpuStatusFlags.Carry, masked >= value);
        X = (byte)result;
        SetZeroAndNegative(X);
        return cycles;
    }

    private int ExecuteLas(byte value, int cycles)
    {
        var result = (byte)(value & StackPointer);
        Accumulator = result;
        X = result;
        StackPointer = result;
        SetZeroAndNegative(result);
        return cycles;
    }

    private int ExecuteSha((ushort BaseAddress, ushort Address) target, int cycles)
    {
        var value = (byte)(Accumulator & X & HighBytePlusOne(target.BaseAddress));
        Write(target.Address, value);
        return cycles;
    }

    private int ExecuteShx((ushort BaseAddress, ushort Address) target, int cycles)
    {
        Write(target.Address, (byte)(X & HighBytePlusOne(target.BaseAddress)));
        return cycles;
    }

    private int ExecuteShy((ushort BaseAddress, ushort Address) target, int cycles)
    {
        Write(target.Address, (byte)(Y & HighBytePlusOne(target.BaseAddress)));
        return cycles;
    }

    private int ExecuteTas((ushort BaseAddress, ushort Address) target, int cycles)
    {
        StackPointer = (byte)(Accumulator & X);
        Write(target.Address, (byte)(StackPointer & HighBytePlusOne(target.BaseAddress)));
        return cycles;
    }

    private ushort ImmediateAddress() => ProgramCounter++;
    private ushort ZeroPageAddress() => Read(ProgramCounter++);

    private ushort ZeroPageXAddress()
    {
        var baseAddress = Read(ProgramCounter++);
        DummyRead(baseAddress);
        return (byte)(baseAddress + X);
    }

    private ushort ZeroPageYAddress()
    {
        var baseAddress = Read(ProgramCounter++);
        DummyRead(baseAddress);
        return (byte)(baseAddress + Y);
    }

    private ushort AbsoluteAddress()
    {
        var address = ReadWord(ProgramCounter);
        ProgramCounter += 2;
        return address;
    }

    private ushort AbsoluteXAddress()
    {
        var baseAddress = AbsoluteAddress();
        return (ushort)(baseAddress + X);
    }

    private ushort AbsoluteYAddress()
    {
        var baseAddress = AbsoluteAddress();
        return (ushort)(baseAddress + Y);
    }

    private ushort AbsoluteXAddress(out int extraCycle)
    {
        var baseAddress = AbsoluteAddress();
        var address = (ushort)(baseAddress + X);
        extraCycle = IsPageCrossed(baseAddress, address) ? 1 : 0;
        if (extraCycle != 0) DummyRead(IndexedPageWrappedAddress(baseAddress, address));
        return address;
    }

    private ushort AbsoluteYAddress(out int extraCycle)
    {
        var baseAddress = AbsoluteAddress();
        var address = (ushort)(baseAddress + Y);
        extraCycle = IsPageCrossed(baseAddress, address) ? 1 : 0;
        if (extraCycle != 0) DummyRead(IndexedPageWrappedAddress(baseAddress, address));
        return address;
    }

    private ushort AbsoluteXModifyAddress() => AbsoluteXWriteAddressWithBase().Address;
    private ushort AbsoluteYModifyAddress() => AbsoluteYWriteAddressWithBase().Address;

    private (ushort BaseAddress, ushort Address) AbsoluteXWriteAddressWithBase()
    {
        var baseAddress = AbsoluteAddress();
        var address = (ushort)(baseAddress + X);
        DummyRead(IndexedPageWrappedAddress(baseAddress, address));
        return (baseAddress, address);
    }

    private (ushort BaseAddress, ushort Address) AbsoluteYWriteAddressWithBase()
    {
        var baseAddress = AbsoluteAddress();
        var address = (ushort)(baseAddress + Y);
        DummyRead(IndexedPageWrappedAddress(baseAddress, address));
        return (baseAddress, address);
    }

    private ushort IndexedIndirectAddress()
    {
        var baseAddress = Read(ProgramCounter++);
        DummyRead(baseAddress);
        var zp = (byte)(baseAddress + X);
        return ReadZeroPageWord(zp);
    }

    private ushort IndirectIndexedAddress(out int extraCycle)
    {
        var zp = Read(ProgramCounter++);
        var baseAddress = ReadZeroPageWord(zp);
        var address = (ushort)(baseAddress + Y);
        extraCycle = IsPageCrossed(baseAddress, address) ? 1 : 0;
        if (extraCycle != 0) DummyRead(IndexedPageWrappedAddress(baseAddress, address));
        return address;
    }

    private ushort IndirectIndexedModifyAddress() => IndirectIndexedModifyAddressWithBase().Address;

    private (ushort BaseAddress, ushort Address) IndirectIndexedModifyAddressWithBase()
    {
        var zp = Read(ProgramCounter++);
        var baseAddress = ReadZeroPageWord(zp);
        var address = (ushort)(baseAddress + Y);
        DummyRead(IndexedPageWrappedAddress(baseAddress, address));
        return (baseAddress, address);
    }

    private (ushort BaseAddress, ushort Address) IndirectIndexedWriteAddressWithBase()
    {
        var zp = Read(ProgramCounter++);
        var baseAddress = ReadZeroPageWord(zp);
        var address = (ushort)(baseAddress + Y);
        if (IsPageCrossed(baseAddress, address)) DummyRead(IndexedPageWrappedAddress(baseAddress, address));
        return (baseAddress, address);
    }

    private ushort ReadWord(ushort address)
    {
        var lo = Read(address);
        var hi = Read((ushort)(address + 1));
        return (ushort)(lo | (hi << 8));
    }

    private ushort ReadWordBug(ushort address)
    {
        var lo = Read(address);
        var hiAddress = (ushort)((address & 0xFF00) | (byte)(address + 1));
        var hi = Read(hiAddress);
        return (ushort)(lo | (hi << 8));
    }

    private ushort ReadWordFromStack()
    {
        var lo = Pop();
        var hi = Pop();
        return (ushort)(lo | (hi << 8));
    }

    private ushort ReadZeroPageWord(byte address)
    {
        var lo = Read(address);
        var hi = Read((byte)(address + 1));
        return (ushort)(lo | (hi << 8));
    }

    private byte ReadModifyWrite(ushort address, Func<byte, byte> operation)
    {
        var value = Read(address);
        Write(address, value);
        var result = operation(value);
        Write(address, result);
        return result;
    }

    private void DummyRead(ushort address) => Read(address);
    private void DummyReadStack() => DummyRead((ushort)(0x0100 | StackPointer));
    private byte Read(ushort address) => _bus.CpuRead(address);
    private void Write(ushort address, byte value) => _bus.CpuWrite(address, value);

    private static ushort IndexedPageWrappedAddress(ushort baseAddress, ushort indexedAddress) =>
        (ushort)((baseAddress & 0xFF00) | (indexedAddress & 0x00FF));

    private static byte HighBytePlusOne(ushort baseAddress) => (byte)(((baseAddress >> 8) + 1) & 0xFF);
    private static bool IsPageCrossed(ushort previous, ushort current) => (previous & 0xFF00) != (current & 0xFF00);
}
