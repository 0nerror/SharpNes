using System;

namespace SharpNes.Core;

/// <summary>
/// Mapper 9 (MMC2) - Tile-based CHR bank switching
/// Used by: Punch-Out!! (only game using this mapper)
/// </summary>
public sealed class Mapper9_MMC2 : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly int _prgBanks;

    private int _prgBank;
    private int _chrBank0FD;
    private int _chrBank0FE;
    private int _chrBank1FD;
    private int _chrBank1FE;
    private bool _latch0;  // false = $FD, true = $FE
    private bool _latch1;  // false = $FD, true = $FE
    private int _mirrorMode = 2; // Default vertical

    public int MirrorMode => _mirrorMode;

    public Mapper9_MMC2(byte[] prgRom, byte[] chrRomOrRam, int prgBanks, int chrBanks)
    {
        _prg = prgRom ?? throw new ArgumentNullException(nameof(prgRom));
        _chr = chrRomOrRam ?? throw new ArgumentNullException(nameof(chrRomOrRam));
        _prgBanks = prgBanks;
        _prgBank = 0;
        _chrBank0FD = 0;
        _chrBank0FE = 0;
        _chrBank1FD = 0;
        _chrBank1FE = 0;
        _latch0 = true;  // Start with $FE
        _latch1 = true;
    }

    public bool CpuRead(ushort addr, out byte data)
    {
        data = 0;

        if (addr >= 0x8000)
        {
            int offset;
            if (addr < 0xA000)
            {
                // $8000-$9FFF: Switchable 8KB bank
                offset = _prgBank * 0x2000 + (addr - 0x8000);
            }
            else
            {
                // $A000-$FFFF: Fixed to last three 8KB banks
                int fixedBank = (_prgBanks * 2) - 3 + ((addr - 0xA000) >> 13);
                offset = fixedBank * 0x2000 + (addr & 0x1FFF);
            }

            if (offset < _prg.Length)
                data = _prg[offset];
            return true;
        }

        return false;
    }

    public bool CpuWrite(ushort addr, byte data)
    {
        if (addr >= 0xA000 && addr <= 0xAFFF)
        {
            // PRG bank select
            _prgBank = data & 0x0F;
            return true;
        }
        if (addr >= 0xB000 && addr <= 0xBFFF)
        {
            // CHR bank 0 ($FD)
            _chrBank0FD = data & 0x1F;
            return true;
        }
        if (addr >= 0xC000 && addr <= 0xCFFF)
        {
            // CHR bank 0 ($FE)
            _chrBank0FE = data & 0x1F;
            return true;
        }
        if (addr >= 0xD000 && addr <= 0xDFFF)
        {
            // CHR bank 1 ($FD)
            _chrBank1FD = data & 0x1F;
            return true;
        }
        if (addr >= 0xE000 && addr <= 0xEFFF)
        {
            // CHR bank 1 ($FE)
            _chrBank1FE = data & 0x1F;
            return true;
        }
        if (addr >= 0xF000 && addr <= 0xFFFF)
        {
            // Mirroring
            _mirrorMode = (data & 0x01) != 0 ? 3 : 2; // Horizontal : Vertical
            return true;
        }

        return false;
    }

    public bool PpuRead(ushort addr, out byte data)
    {
        data = 0;

        if (addr <= 0x1FFF)
        {
            int bank;
            if (addr < 0x1000)
            {
                // $0000-$0FFF
                bank = _latch0 ? _chrBank0FE : _chrBank0FD;
            }
            else
            {
                // $1000-$1FFF
                bank = _latch1 ? _chrBank1FE : _chrBank1FD;
            }

            int offset = bank * 0x1000 + (addr & 0x0FFF);
            if (offset < _chr.Length)
                data = _chr[offset];

            // Update latches based on tile fetched
            // Latch switches when tiles $FD or $FE are fetched
            int tile = addr & 0x0FF8;
            if (addr < 0x1000)
            {
                if (tile == 0x0FD8) _latch0 = false;
                else if (tile == 0x0FE8) _latch0 = true;
            }
            else
            {
                if (tile == 0x1FD8) _latch1 = false;
                else if (tile == 0x1FE8) _latch1 = true;
            }

            return true;
        }

        return false;
    }

    public bool PpuWrite(ushort addr, byte data)
    {
        // MMC2 uses CHR ROM
        return false;
    }
}
