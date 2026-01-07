using System;

namespace NesEmu.Core;

/// <summary>
/// Mapper 3 (CNROM) - Simple CHR bank switching
/// Used by: Gradius, Tiger-Heli, Paperboy, and many others
/// </summary>
public sealed class Mapper3_CNROM : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly int _prgBanks;
    private readonly int _chrBanks;
    private int _chrBank;

    public Mapper3_CNROM(byte[] prgRom, byte[] chrRomOrRam, int prgBanks, int chrBanks)
    {
        _prg = prgRom ?? throw new ArgumentNullException(nameof(prgRom));
        _chr = chrRomOrRam ?? throw new ArgumentNullException(nameof(chrRomOrRam));
        _prgBanks = prgBanks;
        _chrBanks = chrBanks;
        _chrBank = 0;
    }

    public bool CpuRead(ushort addr, out byte data)
    {
        data = 0;

        if (addr >= 0x8000)
        {
            // PRG ROM: 16KB or 32KB, no banking
            int offset;
            if (_prgBanks == 1)
            {
                // 16KB mirrored
                offset = (addr - 0x8000) & 0x3FFF;
            }
            else
            {
                // 32KB
                offset = addr - 0x8000;
            }
            data = _prg[offset];
            return true;
        }

        return false;
    }

    public bool CpuWrite(ushort addr, byte data)
    {
        if (addr >= 0x8000)
        {
            // Select CHR bank
            _chrBank = data & 0x03;
            if (_chrBanks > 0)
                _chrBank %= _chrBanks;
            return true;
        }

        return false;
    }

    public bool PpuRead(ushort addr, out byte data)
    {
        data = 0;

        if (addr <= 0x1FFF)
        {
            int offset = _chrBank * 0x2000 + addr;
            if (offset < _chr.Length)
                data = _chr[offset];
            return true;
        }

        return false;
    }

    public bool PpuWrite(ushort addr, byte data)
    {
        // CNROM uses CHR ROM, not RAM
        return false;
    }
}
