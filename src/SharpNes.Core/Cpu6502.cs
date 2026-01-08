using System;
using System.IO;
using System.Text;

namespace SharpNes.Core;

public sealed class Cpu6502
{
    [Flags]
    public enum StatusFlags : byte
    {
        C = 1 << 0,
        Z = 1 << 1,
        I = 1 << 2,
        D = 1 << 3,
        B = 1 << 4,
        U = 1 << 5,
        V = 1 << 6,
        N = 1 << 7
    }

    public byte A { get; private set; }
    public byte X { get; private set; }
    public byte Y { get; private set; }
    public byte SP { get; private set; }
    public ushort PC { get; private set; }
    public StatusFlags P { get; private set; }

    private readonly Bus _bus;

    public long TotalCycles { get; private set; }
    public long NmiCount { get; private set; }
    public ushort LastNmiFrom { get; private set; }
    public ushort LastNmiTo { get; private set; }

    // Lightweight ring-buffer tracing (off by default)
    public bool EnableTrace { get; set; } = false;
    private readonly string[] _traceRing = new string[512];  // Larger buffer to catch NMI handlers
    private int _traceIndex = 0;

    public Cpu6502(Bus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    }

    public void Reset()
    {
        SP = 0xFD;
        P = StatusFlags.U | StatusFlags.I;

        byte lo = Read(0xFFFC);
        byte hi = Read(0xFFFD);
        PC = (ushort)(lo | (hi << 8));

        TotalCycles = 7;
    }

    // Minimal NMI support
    public void Nmi()
    {
        NmiCount++;

        ushort returnAddr = PC;

        // Push PC high then low
        Push((byte)((PC >> 8) & 0xFF));
        Push((byte)(PC & 0xFF));

        // Push status with B cleared, U set
        StatusFlags pushed = (P | StatusFlags.U) & ~StatusFlags.B;
        Push((byte)pushed);

        SetFlag(StatusFlags.I, true);

        byte lo = Read(0xFFFA);
        byte hi = Read(0xFFFB);
        PC = (ushort)(lo | (hi << 8));

        LastNmiFrom = returnAddr;
        LastNmiTo = PC;

        if (EnableTrace)
        {
            _traceRing[_traceIndex] =
                $"*** NMI *** from ${returnAddr:X4} -> ${PC:X4}  A=${A:X2} X=${X:X2} Y=${Y:X2} SP=${SP:X2} P=${(byte)P:X2} CYC={TotalCycles}";
            _traceIndex = (_traceIndex + 1) & (_traceRing.Length - 1);
        }

        TotalCycles += 7;
    }

    // IRQ support (maskable interrupt)
    public void Irq()
    {
        // IRQ is ignored if I flag is set
        if ((P & StatusFlags.I) != 0)
            return;

        // Push PC high then low
        Push((byte)((PC >> 8) & 0xFF));
        Push((byte)(PC & 0xFF));

        // Push status with B cleared, U set
        StatusFlags pushed = (P | StatusFlags.U) & ~StatusFlags.B;
        Push((byte)pushed);

        SetFlag(StatusFlags.I, true);

        byte lo = Read(0xFFFE);
        byte hi = Read(0xFFFF);
        PC = (ushort)(lo | (hi << 8));

        TotalCycles += 7;
    }

    public void Step()
    {
        ushort pcBefore = PC;
        byte opcode = Read(PC++);

        if (EnableTrace)
        {
            string extra = "";

            // Very small “operand peek” for debugging a few key opcodes.
            // This does NOT change emulation state; it only reads bytes at PC.
            if (opcode == 0xAD) // LDA absolute
            {
                byte lo = Read(PC);
                byte hi = Read((ushort)(PC + 1));
                ushort addr = (ushort)(lo | (hi << 8));

                // DO NOT perform a real Read($2002) here — it clears VBlank!
                if (addr == 0x2002)
                {
                    byte peek = _bus.Ppu.DebugStatusNoSideEffects();
                    extra = $" LDA ${addr:X4} -> ${peek:X2} (peek)";
                }
                else
                {
                    byte peek = Read(addr);
                    extra = $" LDA ${addr:X4} -> ${peek:X2} (peek)";
                }
            }
            else if (opcode == 0x10) // BPL relative
            {
                sbyte off = unchecked((sbyte)Read(PC));
                ushort target = (ushort)(PC + 1 + off);
                extra = $" BPL ${target:X4}";
            }

            _traceRing[_traceIndex] =
                $"PC=${pcBefore:X4} OP=${opcode:X2}{extra}  A=${A:X2} X=${X:X2} Y=${Y:X2} SP=${SP:X2} P=${(byte)P:X2} CYC={TotalCycles}";
            _traceIndex = (_traceIndex + 1) & (_traceRing.Length - 1);
        }

        ExecuteOpcode(opcode);
    }

    public string DumpTrace(int lines = 200)
    {
        if (lines <= 0) lines = 1;
        if (lines > _traceRing.Length) lines = _traceRing.Length;

        var sb = new StringBuilder();
        sb.AppendLine("---- Last CPU Trace ----");

        int mask = _traceRing.Length - 1;
        int start = (_traceIndex - lines) & mask;
        for (int i = 0; i < lines; i++)
        {
            int idx = (start + i) & mask;
            string? line = _traceRing[idx];
            if (!string.IsNullOrWhiteSpace(line))
                sb.AppendLine(line);
        }

        sb.AppendLine("------------------------");
        return sb.ToString();
    }

    // ----------------------------
    // Core helpers
    // ----------------------------
    private byte Read(ushort addr) => _bus.CpuRead(addr);
    private void Write(ushort addr, byte value) => _bus.CpuWrite(addr, value);

    private void SetFlag(StatusFlags flag, bool value)
    {
        if (value) P |= flag;
        else P &= ~flag;
    }

    private bool GetFlag(StatusFlags flag) => (P & flag) != 0;

    private void SetZN(byte value)
    {
        SetFlag(StatusFlags.Z, value == 0);
        SetFlag(StatusFlags.N, (value & 0x80) != 0);
    }

    private byte FetchByte() => Read(PC++);

    private ushort FetchWord()
    {
        byte lo = Read(PC++);
        byte hi = Read(PC++);
        return (ushort)(lo | (hi << 8));
    }

    private void Push(byte value)
    {
        Write((ushort)(0x0100 | SP), value);
        SP--;
    }

    private byte Pop()
    {
        SP++;
        return Read((ushort)(0x0100 | SP));
    }

    private ushort AddrZeroPage() => FetchByte();
    private ushort AddrZeroPageX() => (byte)(FetchByte() + X);
    private ushort AddrZeroPageY() => (byte)(FetchByte() + Y);
    private ushort AddrAbsolute() => FetchWord();
    private ushort AddrAbsoluteX() => (ushort)(FetchWord() + X);
    private ushort AddrAbsoluteY() => (ushort)(FetchWord() + Y);

    private ushort AddrIndirectX()
    {
        byte zp = (byte)(FetchByte() + X);
        byte lo = Read(zp);
        byte hi = Read((byte)(zp + 1));
        return (ushort)(lo | (hi << 8));
    }

    private ushort AddrIndirectY()
    {
        byte zp = FetchByte();
        byte lo = Read(zp);
        byte hi = Read((byte)(zp + 1));
        return (ushort)((lo | (hi << 8)) + Y);
    }

    // Compare helper: sets C, Z, N based on (reg - value)
    private void Compare(byte reg, byte value)
    {
        int result = reg - value;
        SetFlag(StatusFlags.C, reg >= value);
        SetFlag(StatusFlags.Z, reg == value);
        SetFlag(StatusFlags.N, (result & 0x80) != 0);
    }

    // ADC: Add with Carry
    private void DoAdc(byte value)
    {
        int carry = GetFlag(StatusFlags.C) ? 1 : 0;
        int sum = A + value + carry;

        // Overflow: sign of result differs from sign of both operands
        bool overflow = ((A ^ sum) & (value ^ sum) & 0x80) != 0;

        SetFlag(StatusFlags.C, sum > 0xFF);
        SetFlag(StatusFlags.V, overflow);
        A = (byte)sum;
        SetZN(A);
    }

    // SBC: Subtract with Carry (borrow)
    private void DoSbc(byte value)
    {
        // SBC is the same as ADC with inverted operand
        DoAdc((byte)~value);
    }

    private void BranchIf(bool condition)
    {
        sbyte offset = (sbyte)FetchByte();

        if (condition)
        {
            PC = (ushort)(PC + offset);
            TotalCycles += 1;
        }

        TotalCycles += 2;
    }

    private void ExecuteOpcode(byte opcode)
    {
        switch (opcode)
        {
            // SEI
            case 0x78:
                SetFlag(StatusFlags.I, true);
                TotalCycles += 2;
                break;

            // CLI
            case 0x58:
                SetFlag(StatusFlags.I, false);
                TotalCycles += 2;
                break;

            // CLD
            case 0xD8:
                SetFlag(StatusFlags.D, false);
                TotalCycles += 2;
                break;

            // LDA #imm
            case 0xA9:
                A = FetchByte();
                SetZN(A);
                TotalCycles += 2;
                break;

            // LDA zp
            case 0xA5:
                A = Read(AddrZeroPage());
                SetZN(A);
                TotalCycles += 3;
                break;

            // LDA abs
            case 0xAD:
                A = Read(AddrAbsolute());
                SetZN(A);
                TotalCycles += 4;
                break;

            // LDX #imm
            case 0xA2:
                X = FetchByte();
                SetZN(X);
                TotalCycles += 2;
                break;

            // LDY #imm
            case 0xA0:
                Y = FetchByte();
                SetZN(Y);
                TotalCycles += 2;
                break;

            // STA zp
            case 0x85:
                Write(AddrZeroPage(), A);
                TotalCycles += 3;
                break;

            // STA abs
            case 0x8D:
                Write(AddrAbsolute(), A);
                TotalCycles += 4;
                break;

            // TAX
            case 0xAA:
                X = A;
                SetZN(X);
                TotalCycles += 2;
                break;

            // TAY
            case 0xA8:
                Y = A;
                SetZN(Y);
                TotalCycles += 2;
                break;

            // TXA
            case 0x8A:
                A = X;
                SetZN(A);
                TotalCycles += 2;
                break;

            // TYA
            case 0x98:
                A = Y;
                SetZN(A);
                TotalCycles += 2;
                break;

            // TSX
            case 0xBA:
                X = SP;
                SetZN(X);
                TotalCycles += 2;
                break;

            // TXS
            case 0x9A:
                SP = X;
                TotalCycles += 2;
                break;

            // INX
            case 0xE8:
                X++;
                SetZN(X);
                TotalCycles += 2;
                break;

            // INY
            case 0xC8:
                Y++;
                SetZN(Y);
                TotalCycles += 2;
                break;

            // DEX
            case 0xCA:
                X--;
                SetZN(X);
                TotalCycles += 2;
                break;

            // DEY
            case 0x88:
                Y--;
                SetZN(Y);
                TotalCycles += 2;
                break;

            // PHA
            case 0x48:
                Push(A);
                TotalCycles += 3;
                break;

            // PLA
            case 0x68:
                A = Pop();
                SetZN(A);
                TotalCycles += 4;
                break;

            // PHP
            case 0x08:
            {
                byte pushed = (byte)(P | StatusFlags.B | StatusFlags.U);
                Push(pushed);
                TotalCycles += 3;
                break;
            }

            // PLP
            case 0x28:
            {
                byte pulled = Pop();
                P = (StatusFlags)pulled;
                P |= StatusFlags.U;
                P &= ~StatusFlags.B;
                TotalCycles += 4;
                break;
            }

            // JMP abs
            case 0x4C:
                PC = AddrAbsolute();
                TotalCycles += 3;
                break;

            // JSR abs
            case 0x20:
            {
                ushort target = AddrAbsolute();
                ushort returnAddr = (ushort)(PC - 1);
                Push((byte)((returnAddr >> 8) & 0xFF));
                Push((byte)(returnAddr & 0xFF));
                PC = target;
                TotalCycles += 6;
                break;
            }

            // RTS
            case 0x60:
            {
                byte lo = Pop();
                byte hi = Pop();
                PC = (ushort)((hi << 8) | lo);
                PC++;
                TotalCycles += 6;
                break;
            }

            // RTI
            case 0x40:
            {
                byte pulled = Pop();
                P = (StatusFlags)pulled;
                P |= StatusFlags.U;
                P &= ~StatusFlags.B;

                byte lo = Pop();
                byte hi = Pop();
                PC = (ushort)((hi << 8) | lo);

                TotalCycles += 6;
                break;
            }

            // BPL
            case 0x10:
                BranchIf(!GetFlag(StatusFlags.N));
                break;

            // BMI
            case 0x30:
                BranchIf(GetFlag(StatusFlags.N));
                break;

            // BNE
            case 0xD0:
                BranchIf(!GetFlag(StatusFlags.Z));
                break;

            // BEQ
            case 0xF0:
                BranchIf(GetFlag(StatusFlags.Z));
                break;

            // BCC - Branch if Carry Clear
            case 0x90:
                BranchIf(!GetFlag(StatusFlags.C));
                break;

            // BCS - Branch if Carry Set
            case 0xB0:
                BranchIf(GetFlag(StatusFlags.C));
                break;

            // NOP
            case 0xEA:
                TotalCycles += 2;
                break;

            // ===== CLC / SEC / CLV / SED =====
            case 0x18: // CLC
                SetFlag(StatusFlags.C, false);
                TotalCycles += 2;
                break;
            case 0x38: // SEC
                SetFlag(StatusFlags.C, true);
                TotalCycles += 2;
                break;
            case 0xB8: // CLV
                SetFlag(StatusFlags.V, false);
                TotalCycles += 2;
                break;
            case 0xF8: // SED
                SetFlag(StatusFlags.D, true);
                TotalCycles += 2;
                break;

            // ===== CMP (Compare A) =====
            case 0xC9: // CMP #imm
                Compare(A, FetchByte());
                TotalCycles += 2;
                break;
            case 0xC5: // CMP zp
                Compare(A, Read(AddrZeroPage()));
                TotalCycles += 3;
                break;
            case 0xD5: // CMP zp,X
                Compare(A, Read(AddrZeroPageX()));
                TotalCycles += 4;
                break;
            case 0xCD: // CMP abs
                Compare(A, Read(AddrAbsolute()));
                TotalCycles += 4;
                break;
            case 0xDD: // CMP abs,X
                Compare(A, Read(AddrAbsoluteX()));
                TotalCycles += 4;
                break;
            case 0xD9: // CMP abs,Y
                Compare(A, Read(AddrAbsoluteY()));
                TotalCycles += 4;
                break;
            case 0xC1: // CMP (ind,X)
                Compare(A, Read(AddrIndirectX()));
                TotalCycles += 6;
                break;
            case 0xD1: // CMP (ind),Y
                Compare(A, Read(AddrIndirectY()));
                TotalCycles += 5;
                break;

            // ===== CPX (Compare X) =====
            case 0xE0: // CPX #imm
                Compare(X, FetchByte());
                TotalCycles += 2;
                break;
            case 0xE4: // CPX zp
                Compare(X, Read(AddrZeroPage()));
                TotalCycles += 3;
                break;
            case 0xEC: // CPX abs
                Compare(X, Read(AddrAbsolute()));
                TotalCycles += 4;
                break;

            // ===== CPY (Compare Y) =====
            case 0xC0: // CPY #imm
                Compare(Y, FetchByte());
                TotalCycles += 2;
                break;
            case 0xC4: // CPY zp
                Compare(Y, Read(AddrZeroPage()));
                TotalCycles += 3;
                break;
            case 0xCC: // CPY abs
                Compare(Y, Read(AddrAbsolute()));
                TotalCycles += 4;
                break;

            // ===== AND (Logical AND) =====
            case 0x29: // AND #imm
                A &= FetchByte();
                SetZN(A);
                TotalCycles += 2;
                break;
            case 0x25: // AND zp
                A &= Read(AddrZeroPage());
                SetZN(A);
                TotalCycles += 3;
                break;
            case 0x35: // AND zp,X
                A &= Read(AddrZeroPageX());
                SetZN(A);
                TotalCycles += 4;
                break;
            case 0x2D: // AND abs
                A &= Read(AddrAbsolute());
                SetZN(A);
                TotalCycles += 4;
                break;
            case 0x3D: // AND abs,X
                A &= Read(AddrAbsoluteX());
                SetZN(A);
                TotalCycles += 4;
                break;
            case 0x39: // AND abs,Y
                A &= Read(AddrAbsoluteY());
                SetZN(A);
                TotalCycles += 4;
                break;
            case 0x21: // AND (ind,X)
                A &= Read(AddrIndirectX());
                SetZN(A);
                TotalCycles += 6;
                break;
            case 0x31: // AND (ind),Y
                A &= Read(AddrIndirectY());
                SetZN(A);
                TotalCycles += 5;
                break;

            // ===== ORA (Logical OR) =====
            case 0x09: // ORA #imm
                A |= FetchByte();
                SetZN(A);
                TotalCycles += 2;
                break;
            case 0x05: // ORA zp
                A |= Read(AddrZeroPage());
                SetZN(A);
                TotalCycles += 3;
                break;
            case 0x15: // ORA zp,X
                A |= Read(AddrZeroPageX());
                SetZN(A);
                TotalCycles += 4;
                break;
            case 0x0D: // ORA abs
                A |= Read(AddrAbsolute());
                SetZN(A);
                TotalCycles += 4;
                break;
            case 0x1D: // ORA abs,X
                A |= Read(AddrAbsoluteX());
                SetZN(A);
                TotalCycles += 4;
                break;
            case 0x19: // ORA abs,Y
                A |= Read(AddrAbsoluteY());
                SetZN(A);
                TotalCycles += 4;
                break;
            case 0x01: // ORA (ind,X)
                A |= Read(AddrIndirectX());
                SetZN(A);
                TotalCycles += 6;
                break;
            case 0x11: // ORA (ind),Y
                A |= Read(AddrIndirectY());
                SetZN(A);
                TotalCycles += 5;
                break;

            // ===== EOR (Exclusive OR) =====
            case 0x49: // EOR #imm
                A ^= FetchByte();
                SetZN(A);
                TotalCycles += 2;
                break;
            case 0x45: // EOR zp
                A ^= Read(AddrZeroPage());
                SetZN(A);
                TotalCycles += 3;
                break;
            case 0x55: // EOR zp,X
                A ^= Read(AddrZeroPageX());
                SetZN(A);
                TotalCycles += 4;
                break;
            case 0x4D: // EOR abs
                A ^= Read(AddrAbsolute());
                SetZN(A);
                TotalCycles += 4;
                break;
            case 0x5D: // EOR abs,X
                A ^= Read(AddrAbsoluteX());
                SetZN(A);
                TotalCycles += 4;
                break;
            case 0x59: // EOR abs,Y
                A ^= Read(AddrAbsoluteY());
                SetZN(A);
                TotalCycles += 4;
                break;
            case 0x41: // EOR (ind,X)
                A ^= Read(AddrIndirectX());
                SetZN(A);
                TotalCycles += 6;
                break;
            case 0x51: // EOR (ind),Y
                A ^= Read(AddrIndirectY());
                SetZN(A);
                TotalCycles += 5;
                break;

            // ===== ADC (Add with Carry) =====
            case 0x69: // ADC #imm
                DoAdc(FetchByte());
                TotalCycles += 2;
                break;
            case 0x65: // ADC zp
                DoAdc(Read(AddrZeroPage()));
                TotalCycles += 3;
                break;
            case 0x75: // ADC zp,X
                DoAdc(Read(AddrZeroPageX()));
                TotalCycles += 4;
                break;
            case 0x6D: // ADC abs
                DoAdc(Read(AddrAbsolute()));
                TotalCycles += 4;
                break;
            case 0x7D: // ADC abs,X
                DoAdc(Read(AddrAbsoluteX()));
                TotalCycles += 4;
                break;
            case 0x79: // ADC abs,Y
                DoAdc(Read(AddrAbsoluteY()));
                TotalCycles += 4;
                break;
            case 0x61: // ADC (ind,X)
                DoAdc(Read(AddrIndirectX()));
                TotalCycles += 6;
                break;
            case 0x71: // ADC (ind),Y
                DoAdc(Read(AddrIndirectY()));
                TotalCycles += 5;
                break;

            // ===== SBC (Subtract with Carry) =====
            case 0xE9: // SBC #imm
                DoSbc(FetchByte());
                TotalCycles += 2;
                break;
            case 0xE5: // SBC zp
                DoSbc(Read(AddrZeroPage()));
                TotalCycles += 3;
                break;
            case 0xF5: // SBC zp,X
                DoSbc(Read(AddrZeroPageX()));
                TotalCycles += 4;
                break;
            case 0xED: // SBC abs
                DoSbc(Read(AddrAbsolute()));
                TotalCycles += 4;
                break;
            case 0xFD: // SBC abs,X
                DoSbc(Read(AddrAbsoluteX()));
                TotalCycles += 4;
                break;
            case 0xF9: // SBC abs,Y
                DoSbc(Read(AddrAbsoluteY()));
                TotalCycles += 4;
                break;
            case 0xE1: // SBC (ind,X)
                DoSbc(Read(AddrIndirectX()));
                TotalCycles += 6;
                break;
            case 0xF1: // SBC (ind),Y
                DoSbc(Read(AddrIndirectY()));
                TotalCycles += 5;
                break;

            // ===== INC (Increment Memory) =====
            case 0xE6: // INC zp
            {
                ushort addr = AddrZeroPage();
                byte val = (byte)(Read(addr) + 1);
                Write(addr, val);
                SetZN(val);
                TotalCycles += 5;
                break;
            }
            case 0xF6: // INC zp,X
            {
                ushort addr = AddrZeroPageX();
                byte val = (byte)(Read(addr) + 1);
                Write(addr, val);
                SetZN(val);
                TotalCycles += 6;
                break;
            }
            case 0xEE: // INC abs
            {
                ushort addr = AddrAbsolute();
                byte val = (byte)(Read(addr) + 1);
                Write(addr, val);
                SetZN(val);
                TotalCycles += 6;
                break;
            }
            case 0xFE: // INC abs,X
            {
                ushort addr = AddrAbsoluteX();
                byte val = (byte)(Read(addr) + 1);
                Write(addr, val);
                SetZN(val);
                TotalCycles += 7;
                break;
            }

            // ===== DEC (Decrement Memory) =====
            case 0xC6: // DEC zp
            {
                ushort addr = AddrZeroPage();
                byte val = (byte)(Read(addr) - 1);
                Write(addr, val);
                SetZN(val);
                TotalCycles += 5;
                break;
            }
            case 0xD6: // DEC zp,X
            {
                ushort addr = AddrZeroPageX();
                byte val = (byte)(Read(addr) - 1);
                Write(addr, val);
                SetZN(val);
                TotalCycles += 6;
                break;
            }
            case 0xCE: // DEC abs
            {
                ushort addr = AddrAbsolute();
                byte val = (byte)(Read(addr) - 1);
                Write(addr, val);
                SetZN(val);
                TotalCycles += 6;
                break;
            }
            case 0xDE: // DEC abs,X
            {
                ushort addr = AddrAbsoluteX();
                byte val = (byte)(Read(addr) - 1);
                Write(addr, val);
                SetZN(val);
                TotalCycles += 7;
                break;
            }

            // ===== BIT (Bit Test) =====
            case 0x24: // BIT zp
            {
                byte val = Read(AddrZeroPage());
                SetFlag(StatusFlags.Z, (A & val) == 0);
                SetFlag(StatusFlags.V, (val & 0x40) != 0);
                SetFlag(StatusFlags.N, (val & 0x80) != 0);
                TotalCycles += 3;
                break;
            }
            case 0x2C: // BIT abs
            {
                byte val = Read(AddrAbsolute());
                SetFlag(StatusFlags.Z, (A & val) == 0);
                SetFlag(StatusFlags.V, (val & 0x40) != 0);
                SetFlag(StatusFlags.N, (val & 0x80) != 0);
                TotalCycles += 4;
                break;
            }

            // ===== BVC / BVS =====
            case 0x50: // BVC
                BranchIf(!GetFlag(StatusFlags.V));
                break;
            case 0x70: // BVS
                BranchIf(GetFlag(StatusFlags.V));
                break;

            // ===== Additional LDA addressing modes =====
            case 0xB5: // LDA zp,X
                A = Read(AddrZeroPageX());
                SetZN(A);
                TotalCycles += 4;
                break;
            case 0xBD: // LDA abs,X
                A = Read(AddrAbsoluteX());
                SetZN(A);
                TotalCycles += 4;
                break;
            case 0xB9: // LDA abs,Y
                A = Read(AddrAbsoluteY());
                SetZN(A);
                TotalCycles += 4;
                break;
            case 0xA1: // LDA (ind,X)
                A = Read(AddrIndirectX());
                SetZN(A);
                TotalCycles += 6;
                break;
            case 0xB1: // LDA (ind),Y
                A = Read(AddrIndirectY());
                SetZN(A);
                TotalCycles += 5;
                break;

            // ===== Additional LDX addressing modes =====
            case 0xA6: // LDX zp
                X = Read(AddrZeroPage());
                SetZN(X);
                TotalCycles += 3;
                break;
            case 0xB6: // LDX zp,Y
                X = Read(AddrZeroPageY());
                SetZN(X);
                TotalCycles += 4;
                break;
            case 0xAE: // LDX abs
                X = Read(AddrAbsolute());
                SetZN(X);
                TotalCycles += 4;
                break;
            case 0xBE: // LDX abs,Y
                X = Read(AddrAbsoluteY());
                SetZN(X);
                TotalCycles += 4;
                break;

            // ===== Additional LDY addressing modes =====
            case 0xA4: // LDY zp
                Y = Read(AddrZeroPage());
                SetZN(Y);
                TotalCycles += 3;
                break;
            case 0xB4: // LDY zp,X
                Y = Read(AddrZeroPageX());
                SetZN(Y);
                TotalCycles += 4;
                break;
            case 0xAC: // LDY abs
                Y = Read(AddrAbsolute());
                SetZN(Y);
                TotalCycles += 4;
                break;
            case 0xBC: // LDY abs,X
                Y = Read(AddrAbsoluteX());
                SetZN(Y);
                TotalCycles += 4;
                break;

            // ===== Additional STA addressing modes =====
            case 0x95: // STA zp,X
                Write(AddrZeroPageX(), A);
                TotalCycles += 4;
                break;
            case 0x9D: // STA abs,X
                Write(AddrAbsoluteX(), A);
                TotalCycles += 5;
                break;
            case 0x99: // STA abs,Y
                Write(AddrAbsoluteY(), A);
                TotalCycles += 5;
                break;
            case 0x81: // STA (ind,X)
                Write(AddrIndirectX(), A);
                TotalCycles += 6;
                break;
            case 0x91: // STA (ind),Y
                Write(AddrIndirectY(), A);
                TotalCycles += 6;
                break;

            // ===== STX addressing modes =====
            case 0x86: // STX zp
                Write(AddrZeroPage(), X);
                TotalCycles += 3;
                break;
            case 0x96: // STX zp,Y
                Write(AddrZeroPageY(), X);
                TotalCycles += 4;
                break;
            case 0x8E: // STX abs
                Write(AddrAbsolute(), X);
                TotalCycles += 4;
                break;

            // ===== STY addressing modes =====
            case 0x84: // STY zp
                Write(AddrZeroPage(), Y);
                TotalCycles += 3;
                break;
            case 0x94: // STY zp,X
                Write(AddrZeroPageX(), Y);
                TotalCycles += 4;
                break;
            case 0x8C: // STY abs
                Write(AddrAbsolute(), Y);
                TotalCycles += 4;
                break;

            // ===== ASL (Arithmetic Shift Left) =====
            case 0x0A: // ASL A
                SetFlag(StatusFlags.C, (A & 0x80) != 0);
                A <<= 1;
                SetZN(A);
                TotalCycles += 2;
                break;
            case 0x06: // ASL zp
            {
                ushort addr = AddrZeroPage();
                byte val = Read(addr);
                SetFlag(StatusFlags.C, (val & 0x80) != 0);
                val <<= 1;
                Write(addr, val);
                SetZN(val);
                TotalCycles += 5;
                break;
            }
            case 0x16: // ASL zp,X
            {
                ushort addr = AddrZeroPageX();
                byte val = Read(addr);
                SetFlag(StatusFlags.C, (val & 0x80) != 0);
                val <<= 1;
                Write(addr, val);
                SetZN(val);
                TotalCycles += 6;
                break;
            }
            case 0x0E: // ASL abs
            {
                ushort addr = AddrAbsolute();
                byte val = Read(addr);
                SetFlag(StatusFlags.C, (val & 0x80) != 0);
                val <<= 1;
                Write(addr, val);
                SetZN(val);
                TotalCycles += 6;
                break;
            }
            case 0x1E: // ASL abs,X
            {
                ushort addr = AddrAbsoluteX();
                byte val = Read(addr);
                SetFlag(StatusFlags.C, (val & 0x80) != 0);
                val <<= 1;
                Write(addr, val);
                SetZN(val);
                TotalCycles += 7;
                break;
            }

            // ===== LSR (Logical Shift Right) =====
            case 0x4A: // LSR A
                SetFlag(StatusFlags.C, (A & 0x01) != 0);
                A >>= 1;
                SetZN(A);
                TotalCycles += 2;
                break;
            case 0x46: // LSR zp
            {
                ushort addr = AddrZeroPage();
                byte val = Read(addr);
                SetFlag(StatusFlags.C, (val & 0x01) != 0);
                val >>= 1;
                Write(addr, val);
                SetZN(val);
                TotalCycles += 5;
                break;
            }
            case 0x56: // LSR zp,X
            {
                ushort addr = AddrZeroPageX();
                byte val = Read(addr);
                SetFlag(StatusFlags.C, (val & 0x01) != 0);
                val >>= 1;
                Write(addr, val);
                SetZN(val);
                TotalCycles += 6;
                break;
            }
            case 0x4E: // LSR abs
            {
                ushort addr = AddrAbsolute();
                byte val = Read(addr);
                SetFlag(StatusFlags.C, (val & 0x01) != 0);
                val >>= 1;
                Write(addr, val);
                SetZN(val);
                TotalCycles += 6;
                break;
            }
            case 0x5E: // LSR abs,X
            {
                ushort addr = AddrAbsoluteX();
                byte val = Read(addr);
                SetFlag(StatusFlags.C, (val & 0x01) != 0);
                val >>= 1;
                Write(addr, val);
                SetZN(val);
                TotalCycles += 7;
                break;
            }

            // ===== ROL (Rotate Left) =====
            case 0x2A: // ROL A
            {
                bool oldC = GetFlag(StatusFlags.C);
                SetFlag(StatusFlags.C, (A & 0x80) != 0);
                A = (byte)((A << 1) | (oldC ? 1 : 0));
                SetZN(A);
                TotalCycles += 2;
                break;
            }
            case 0x26: // ROL zp
            {
                ushort addr = AddrZeroPage();
                byte val = Read(addr);
                bool oldC = GetFlag(StatusFlags.C);
                SetFlag(StatusFlags.C, (val & 0x80) != 0);
                val = (byte)((val << 1) | (oldC ? 1 : 0));
                Write(addr, val);
                SetZN(val);
                TotalCycles += 5;
                break;
            }
            case 0x36: // ROL zp,X
            {
                ushort addr = AddrZeroPageX();
                byte val = Read(addr);
                bool oldC = GetFlag(StatusFlags.C);
                SetFlag(StatusFlags.C, (val & 0x80) != 0);
                val = (byte)((val << 1) | (oldC ? 1 : 0));
                Write(addr, val);
                SetZN(val);
                TotalCycles += 6;
                break;
            }
            case 0x2E: // ROL abs
            {
                ushort addr = AddrAbsolute();
                byte val = Read(addr);
                bool oldC = GetFlag(StatusFlags.C);
                SetFlag(StatusFlags.C, (val & 0x80) != 0);
                val = (byte)((val << 1) | (oldC ? 1 : 0));
                Write(addr, val);
                SetZN(val);
                TotalCycles += 6;
                break;
            }
            case 0x3E: // ROL abs,X
            {
                ushort addr = AddrAbsoluteX();
                byte val = Read(addr);
                bool oldC = GetFlag(StatusFlags.C);
                SetFlag(StatusFlags.C, (val & 0x80) != 0);
                val = (byte)((val << 1) | (oldC ? 1 : 0));
                Write(addr, val);
                SetZN(val);
                TotalCycles += 7;
                break;
            }

            // ===== ROR (Rotate Right) =====
            case 0x6A: // ROR A
            {
                bool oldC = GetFlag(StatusFlags.C);
                SetFlag(StatusFlags.C, (A & 0x01) != 0);
                A = (byte)((A >> 1) | (oldC ? 0x80 : 0));
                SetZN(A);
                TotalCycles += 2;
                break;
            }
            case 0x66: // ROR zp
            {
                ushort addr = AddrZeroPage();
                byte val = Read(addr);
                bool oldC = GetFlag(StatusFlags.C);
                SetFlag(StatusFlags.C, (val & 0x01) != 0);
                val = (byte)((val >> 1) | (oldC ? 0x80 : 0));
                Write(addr, val);
                SetZN(val);
                TotalCycles += 5;
                break;
            }
            case 0x76: // ROR zp,X
            {
                ushort addr = AddrZeroPageX();
                byte val = Read(addr);
                bool oldC = GetFlag(StatusFlags.C);
                SetFlag(StatusFlags.C, (val & 0x01) != 0);
                val = (byte)((val >> 1) | (oldC ? 0x80 : 0));
                Write(addr, val);
                SetZN(val);
                TotalCycles += 6;
                break;
            }
            case 0x6E: // ROR abs
            {
                ushort addr = AddrAbsolute();
                byte val = Read(addr);
                bool oldC = GetFlag(StatusFlags.C);
                SetFlag(StatusFlags.C, (val & 0x01) != 0);
                val = (byte)((val >> 1) | (oldC ? 0x80 : 0));
                Write(addr, val);
                SetZN(val);
                TotalCycles += 6;
                break;
            }
            case 0x7E: // ROR abs,X
            {
                ushort addr = AddrAbsoluteX();
                byte val = Read(addr);
                bool oldC = GetFlag(StatusFlags.C);
                SetFlag(StatusFlags.C, (val & 0x01) != 0);
                val = (byte)((val >> 1) | (oldC ? 0x80 : 0));
                Write(addr, val);
                SetZN(val);
                TotalCycles += 7;
                break;
            }

            // ===== JMP indirect =====
            case 0x6C: // JMP (ind)
            {
                ushort ptr = FetchWord();
                // 6502 bug: if ptr is $xxFF, high byte comes from $xx00, not $xx00+$0100
                byte lo = Read(ptr);
                byte hi = Read((ushort)((ptr & 0xFF00) | ((ptr + 1) & 0x00FF)));
                PC = (ushort)(lo | (hi << 8));
                TotalCycles += 5;
                break;
            }

            // BRK - Software interrupt
            case 0x00:
            {
                // BRK is a 2-byte instruction; PC already points to signature byte
                // Return address pushed is PC+1 (past the signature byte)
                PC++;

                // Push return address (high byte first)
                Push((byte)((PC >> 8) & 0xFF));
                Push((byte)(PC & 0xFF));

                // Push status with B and U flags set
                Push((byte)(P | StatusFlags.B | StatusFlags.U));

                // Set interrupt disable flag
                SetFlag(StatusFlags.I, true);

                // Load PC from IRQ/BRK vector at $FFFE
                byte lo = Read(0xFFFE);
                byte hi = Read(0xFFFF);
                PC = (ushort)(lo | (hi << 8));

                TotalCycles += 7;
                break;
            }

            default:
                throw new NotSupportedException($"Opcode ${opcode:X2} not implemented yet at PC=${(PC - 1):X4}");
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(A);
        writer.Write(X);
        writer.Write(Y);
        writer.Write(SP);
        writer.Write(PC);
        writer.Write((byte)P);
        writer.Write(TotalCycles);
    }

    public void LoadState(BinaryReader reader)
    {
        A = reader.ReadByte();
        X = reader.ReadByte();
        Y = reader.ReadByte();
        SP = reader.ReadByte();
        PC = reader.ReadUInt16();
        P = (StatusFlags)reader.ReadByte();
        TotalCycles = reader.ReadInt64();
    }
}
