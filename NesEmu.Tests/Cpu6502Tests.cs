using NesEmu.Core;

namespace NesEmu.Tests;

public sealed class Cpu6502Tests
{
    [Fact]
    public void Cli_DelaysPendingIrqUntilFollowingInstruction()
    {
        var (cpu, bus) = CreateCpu(
            0x8000,
            0x58,       // CLI
            0xA9, 0x12, // LDA #$12
            0xEA);      // NOP

        bus.SetVector(0xFFFE, 0x9000);
        bus.SetIrqLine(cpu, asserted: true);

        Assert.Equal(2, cpu.Step());
        Assert.Equal(2, cpu.Step());
        Assert.Equal(0x12, cpu.Accumulator);
        Assert.Equal((ushort)0x8003, cpu.ProgramCounter);

        Assert.Equal(7, cpu.Step());
        Assert.Equal((ushort)0x9000, cpu.ProgramCounter);
        Assert.Equal(0, bus.Memory[0x01FB] & (byte)CpuStatusFlags.InterruptDisable);
    }

    [Fact]
    public void Jsr_PushesReturnAddressBetweenOperandReads()
    {
        var (cpu, bus) = CreateCpu(
            0x8000,
            0x20, 0x34, 0x12); // JSR $1234

        cpu.Step();

        Assert.Collection(
            bus.Accesses,
            access => Assert.Equal(new BusAccess(false, 0x8000, 0x20), access),
            access => Assert.Equal(new BusAccess(false, 0x8001, 0x34), access),
            access => Assert.Equal(new BusAccess(true, 0x01FD, 0x80), access),
            access => Assert.Equal(new BusAccess(true, 0x01FC, 0x02), access),
            access => Assert.Equal(new BusAccess(false, 0x8002, 0x12), access));
    }

    [Fact]
    public void ReadModifyWrite_InstructionsWriteOldValueThenNewValue()
    {
        var (cpu, bus) = CreateCpu(
            0x8000,
            0x06, 0x40); // ASL $40
        bus.Memory[0x0040] = 0x41;

        cpu.Step();

        var writes = bus.Accesses.Where(static access => access.IsWrite).ToArray();
        Assert.Equal(
            [
                new BusAccess(true, 0x0040, 0x41),
                new BusAccess(true, 0x0040, 0x82)
            ],
            writes);
    }

    [Fact]
    public void TakenBranchAcrossPage_PerformsBothDummyReads()
    {
        var (cpu, bus) = CreateCpu(
            0x80FD,
            0x90, 0x02); // BCC +2 => 0x8101

        cpu.Step();

        var reads = bus.Accesses.Where(static access => !access.IsWrite).Select(static access => access.Address).ToArray();
        Assert.Equal([0x80FD, 0x80FE, 0x80FF, 0x8001], reads);
        Assert.Equal((ushort)0x8101, cpu.ProgramCounter);
    }

    [Fact]
    public void Sha_UsesHighBytePlusOneMask()
    {
        var (cpu, bus) = CreateCpu(
            0x8000,
            0xA9, 0xFF,       // LDA #$FF
            0xA2, 0x0F,       // LDX #$0F
            0xA0, 0x00,       // LDY #$00
            0x9F, 0x20, 0x05  // SHA $0520,Y
        );

        cpu.Step();
        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.Equal(0x06, bus.Memory[0x0520]);
    }

    private static (Cpu6502 Cpu, TestBus Bus) CreateCpu(ushort startAddress, params byte[] program)
    {
        var bus = new TestBus();
        bus.Load(startAddress, program);
        bus.SetVector(0xFFFC, startAddress);
        bus.SetVector(0xFFFA, 0x9000);
        bus.SetVector(0xFFFE, 0x9000);

        var cpu = new Cpu6502(bus);
        cpu.Reset();
        bus.ClearAccesses();
        return (cpu, bus);
    }

    private readonly record struct BusAccess(bool IsWrite, ushort Address, byte Value);

    private sealed class TestBus : ICpuBus
    {
        public readonly byte[] Memory = new byte[0x10000];
        public readonly List<BusAccess> Accesses = [];

        public byte CpuRead(ushort address)
        {
            var value = Memory[address];
            Accesses.Add(new BusAccess(false, address, value));
            return value;
        }

        public void CpuWrite(ushort address, byte value)
        {
            Memory[address] = value;
            Accesses.Add(new BusAccess(true, address, value));
        }

        public void Load(ushort address, params byte[] data)
        {
            for (var i = 0; i < data.Length; i++)
            {
                Memory[address + i] = data[i];
            }
        }

        public void SetVector(ushort address, ushort value)
        {
            Memory[address] = (byte)(value & 0xFF);
            Memory[address + 1] = (byte)(value >> 8);
        }

        public void ClearAccesses() => Accesses.Clear();

        public void SetIrqLine(Cpu6502 cpu, bool asserted) => cpu.SetIrqLine(asserted);
    }
}
