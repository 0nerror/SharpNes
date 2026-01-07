using System;

namespace NesEmu.Core;

/// <summary>
/// Mapper 7 (AxROM) - 32KB PRG switching with single-screen mirroring
/// Used by: Battletoads, Marble Madness, Wizards & Warriors
/// </summary>
public sealed class Mapper7_AxROM : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly int _prgBanks;
    private int _prgBank;
    private int _mirrorMode; // 0 = lower, 1 = upper

    public int MirrorMode => _mirrorMode; // 0 or 1 for single-screen

    public Mapper7_AxROM(byte[] prgRom, byte[] chrRomOrRam, int prgBanks, int chrBanks)
    {
        _prg = prgRom ?? throw new ArgumentNullException(nameof(prgRom));
        _chr = chrRomOrRam ?? throw new ArgumentNullException(nameof(chrRomOrRam));
        _prgBanks = prgBanks;
        _prgBank = 0;
        _mirrorMode = 0;
    }

    public bool CpuRead(ushort addr, out byte data)
    {
        data = 0;

        if (addr >= 0x8000)
        {
            // 32KB switchable bank
            int offset = _prgBank * 0x8000 + (addr - 0x8000);
            if (offset < _prg.Length)
                data = _prg[offset];
            return true;
        }

        return false;
    }

    public bool CpuWrite(ushort addr, byte data)
    {
        if (addr >= 0x8000)
        {
            // Bits 0-2: PRG bank (32KB)
            _prgBank = data & 0x07;
            // Bit 4: Mirroring (0=lower, 1=upper)
            _mirrorMode = (data >> 4) & 0x01;
            return true;
        }

        return false;
    }

    public bool PpuRead(ushort addr, out byte data)
    {
        data = 0;

        if (addr <= 0x1FFF)
        {
            // 8KB CHR RAM (no banking)
            data = _chr[addr];
            return true;
        }

        return false;
    }

    public bool PpuWrite(ushort addr, byte data)
    {
        if (addr <= 0x1FFF)
        {
            // CHR RAM
            _chr[addr] = data;
            return true;
        }

        return false;
    }
}
