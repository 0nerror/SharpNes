using System;

namespace SharpNes.Core;

/// <summary>
/// Mapper 7 (AxROM) - 32KB PRG switching with single-screen mirroring
/// Used by: Battletoads, Marble Madness, Wizards & Warriors, A Nightmare on Elm Street
/// </summary>
public sealed class Mapper7_AxROM : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly int _prgBanks;   // 16KB banks from iNES header
    private readonly int _prgBankMask; // Mask for 32KB bank selection
    private readonly int _lastBank;    // Last valid 32KB bank number
    private int _prgBank;
    private int _mirrorMode; // 0 = lower, 1 = upper

    public int MirrorMode => _mirrorMode; // 0 or 1 for single-screen

    public Mapper7_AxROM(byte[] prgRom, byte[] chrRomOrRam, int prgBanks, int chrBanks)
    {
        _prg = prgRom ?? throw new ArgumentNullException(nameof(prgRom));
        _chr = chrRomOrRam ?? throw new ArgumentNullException(nameof(chrRomOrRam));
        _prgBanks = prgBanks;

        // Calculate number of 32KB banks (prgBanks is 16KB count, so divide by 2)
        int num32KBanks = Math.Max(1, prgBanks / 2);
        _lastBank = num32KBanks - 1;

        // Find power-of-2 mask for bank selection
        int mask = 1;
        while (mask <= _lastBank) mask <<= 1;
        _prgBankMask = mask - 1;

        // Start with LAST bank selected - this is critical!
        // The reset vector at $FFFC-$FFFD must read valid code.
        // AxROM games typically have their startup code in the last bank.
        _prgBank = _lastBank;
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
            // AxROM has bus conflicts - the value written is AND'd with ROM data
            // Games are designed to write to addresses where ROM matches intended value
            int offset = _prgBank * 0x8000 + (addr - 0x8000);
            if (offset < _prg.Length)
            {
                data &= _prg[offset];
            }

            // Bits 0-2: PRG bank (32KB), masked to actual ROM size
            _prgBank = (data & 0x07) & _prgBankMask;
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
