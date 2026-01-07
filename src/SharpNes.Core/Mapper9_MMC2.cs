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

    // false = $FD, true = $FE
    private bool _latch0;
    private bool _latch1;

    // 2 = vertical, 3 = horizontal (based on your existing code)
    private int _mirrorMode = 2;

    public int MirrorMode => _mirrorMode;

    // Cache for per-tile-row bank selection to handle per-pixel rendering
    // Key: pattern address with fine bits masked (identifies the tile row)
    // This ensures all 8 pixels of a tile row use the same bank
    private int _cachedAddr0 = -1;
    private int _cachedBank0;
    private int _cachedAddr1 = -1;
    private int _cachedBank1;

    private int _chrWriteCount = 0;

    public Mapper9_MMC2(byte[] prgRom, byte[] chrRomOrRam, int prgBanks, int chrBanks)
    {
        _prg = prgRom ?? throw new ArgumentNullException(nameof(prgRom));
        _chr = chrRomOrRam ?? throw new ArgumentNullException(nameof(chrRomOrRam));
        _prgBanks = prgBanks;

        // Debug: Log sizes
        Console.WriteLine($"MMC2: PRG size = {_prg.Length}, CHR size = {_chr.Length}");
        Console.WriteLine($"MMC2: prgBanks = {prgBanks}, chrBanks = {chrBanks}");

        // Per NESDev: "On power-up, the last CHR page is selected for all four slots"
        // CHR banks are 4KB each, so last bank = (chrSize / 4KB) - 1
        int lastChrBank = (_chr.Length / 0x1000) - 1;
        Console.WriteLine($"MMC2: Initializing CHR banks to last bank: {lastChrBank}");

        _prgBank = 0;

        _chrBank0FD = lastChrBank;
        _chrBank0FE = lastChrBank;
        _chrBank1FD = lastChrBank;
        _chrBank1FE = lastChrBank;

        // Many docs note latches power up in the "FE" state
        _latch0 = true;
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
                // MMC2 uses 8KB PRG banks; _prgBank selects the 8KB bank at $8000
                int bank = _prgBank;
                offset = bank * 0x2000 + (addr - 0x8000);
            }
            else
            {
                // $A000-$FFFF: Fixed to last three 8KB banks
                // total 8KB banks = _prgBanks * 2
                int total8kBanks = _prgBanks * 2;
                int fixedBank = (total8kBanks - 3) + ((addr - 0xA000) >> 13);
                offset = fixedBank * 0x2000 + (addr & 0x1FFF);
            }

            if ((uint)offset < (uint)_prg.Length)
                data = _prg[offset];

            return true;
        }

        return false;
    }

    public bool CpuWrite(ushort addr, byte data)
    {
        if (addr >= 0xA000 && addr <= 0xAFFF)
        {
            // Select 8KB PRG bank for $8000-$9FFF
            // Mask to 4 bits; also clamp to available banks to avoid out-of-range
            int total8kBanks = _prgBanks * 2;
            _prgBank = (data & 0x0F) % Math.Max(1, total8kBanks);
            return true;
        }

        if (addr >= 0xB000 && addr <= 0xBFFF)
        {
            int newVal = data & 0x1F;
            if (newVal != _chrBank0FD && _chrWriteCount < 50)
            {
                Console.WriteLine($"MMC2: CHR0_FD = {newVal}");
                _chrWriteCount++;
            }
            _chrBank0FD = newVal;
            return true;
        }

        if (addr >= 0xC000 && addr <= 0xCFFF)
        {
            int newVal = data & 0x1F;
            if (newVal != _chrBank0FE && _chrWriteCount < 50)
            {
                Console.WriteLine($"MMC2: CHR0_FE = {newVal}");
                _chrWriteCount++;
            }
            _chrBank0FE = newVal;
            return true;
        }

        if (addr >= 0xD000 && addr <= 0xDFFF)
        {
            int newVal = data & 0x1F;
            if (newVal != _chrBank1FD && _chrWriteCount < 50)
            {
                Console.WriteLine($"MMC2: CHR1_FD = {newVal}");
                _chrWriteCount++;
            }
            _chrBank1FD = newVal;
            return true;
        }

        if (addr >= 0xE000 && addr <= 0xEFFF)
        {
            int newVal = data & 0x1F;
            if (newVal != _chrBank1FE && _chrWriteCount < 50)
            {
                Console.WriteLine($"MMC2: CHR1_FE = {newVal}");
                _chrWriteCount++;
            }
            _chrBank1FE = newVal;
            return true;
        }

        if (addr >= 0xF000 && addr <= 0xFFFF)
        {
            // Mirroring: 0 = vertical, 1 = horizontal (based on your mapping to 2/3)
            _mirrorMode = (data & 0x01) != 0 ? 3 : 2;
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

            // Mask to get tile + row identifier (ignore bit 3 which selects plane)
            // This groups low byte ($XXX0-$XXX7) and high byte ($XXX8-$XXXF) of same row
            int rowAddr = addr & 0x1FF7;

            if (addr < 0x1000)
            {
                // Pattern table 0 ($0000-$0FFF)
                if (rowAddr != _cachedAddr0)
                {
                    _cachedAddr0 = rowAddr;
                    _cachedBank0 = _latch0 ? _chrBank0FE : _chrBank0FD;
                }
                bank = _cachedBank0;
            }
            else
            {
                // Pattern table 1 ($1000-$1FFF)
                if (rowAddr != _cachedAddr1)
                {
                    _cachedAddr1 = rowAddr;
                    _cachedBank1 = _latch1 ? _chrBank1FE : _chrBank1FD;
                }
                bank = _cachedBank1;
            }

            // Read CHR data (4KB banks)
            int offset = bank * 0x1000 + (addr & 0x0FFF);
            if ((uint)offset < (uint)_chr.Length)
            {
                data = _chr[offset];
            }

            // Update latches AFTER the read (hardware-like)
            UpdateLatches(addr);

            return true;
        }

        return false;
    }

    private void UpdateLatches(ushort addr)
    {
        // MMC2 latch mechanism:
        // Pattern fetches touching these address ranges select which CHR bank is used
        // for the corresponding 4KB region.
        //
        // The latch triggers on addresses in these ranges:
        //   $0FD8-$0FDF, $0FE8-$0FEF (pattern table 0)
        //   $1FD8-$1FDF, $1FE8-$1FEF (pattern table 1)
        //
        // We detect using addr & $1FF8.

        ushort masked = (ushort)(addr & 0x1FF8);

        if (masked == 0x0FD8)
        {
            _latch0 = false; // FD
        }
        else if (masked == 0x0FE8)
        {
            _latch0 = true; // FE
        }
        else if (masked == 0x1FD8)
        {
            _latch1 = false; // FD
        }
        else if (masked == 0x1FE8)
        {
            _latch1 = true; // FE
        }

        // DO NOT invalidate caches here - the cache should persist for the current tile row.
        // The latch change affects FUTURE tile rows, not the current one being read.
        // This is critical for per-pixel rendering to work correctly with MMC2.
    }

    public bool PpuWrite(ushort addr, byte data)
    {
        // MMC2 uses CHR ROM, not RAM (for Punch-Out!!)
        return false;
    }
}
