using System;

namespace SharpNes.Core;

public sealed class Mapper1_MMC1 : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly int _prgBanks;
    private readonly int _chrBanks;
    private readonly byte[] _prgRam = new byte[8192]; // 8KB PRG RAM

    // Shift register for serial writes
    private byte _shiftRegister;
    private int _shiftCount;

    // Internal registers
    private byte _control;      // $8000-$9FFF
    private byte _chrBank0;     // $A000-$BFFF
    private byte _chrBank1;     // $C000-$DFFF
    private byte _prgBank;      // $E000-$FFFF

    // Mirroring callback (optional, for PPU to query)
    public int MirrorMode => _control & 0x03;

    public Mapper1_MMC1(byte[] prgRom, byte[] chrRomOrRam, int prgBanks, int chrBanks)
    {
        _prg = prgRom ?? throw new ArgumentNullException(nameof(prgRom));
        _chr = chrRomOrRam ?? throw new ArgumentNullException(nameof(chrRomOrRam));
        _prgBanks = prgBanks;
        _chrBanks = chrBanks;

        // Reset to default state
        _shiftRegister = 0;
        _shiftCount = 0;
        _control = 0x0C;  // PRG mode 3 (fix last bank), CHR mode 0 (8KB)
        _chrBank0 = 0;
        _chrBank1 = 0;
        _prgBank = 0;
    }

    public bool CpuRead(ushort addr, out byte data)
    {
        data = 0;

        // PRG RAM: $6000-$7FFF
        if (addr >= 0x6000 && addr <= 0x7FFF)
        {
            data = _prgRam[addr - 0x6000];
            return true;
        }

        // PRG ROM: $8000-$FFFF
        if (addr >= 0x8000)
        {
            int prgMode = (_control >> 2) & 0x03;
            int offset;

            switch (prgMode)
            {
                case 0:
                case 1:
                    // 32KB mode: switch 32KB at $8000, ignore low bit of bank
                    {
                        int bank32 = (_prgBank & 0x0E) >> 1;
                        offset = bank32 * 0x8000 + (addr - 0x8000);
                    }
                    break;

                case 2:
                    // Fix first bank at $8000, switch 16KB at $C000
                    if (addr < 0xC000)
                    {
                        offset = addr - 0x8000; // First 16KB bank (bank 0)
                    }
                    else
                    {
                        offset = (_prgBank & 0x0F) * 0x4000 + (addr - 0xC000);
                    }
                    break;

                case 3:
                default:
                    // Fix last bank at $C000, switch 16KB at $8000
                    if (addr < 0xC000)
                    {
                        offset = (_prgBank & 0x0F) * 0x4000 + (addr - 0x8000);
                    }
                    else
                    {
                        offset = (_prgBanks - 1) * 0x4000 + (addr - 0xC000);
                    }
                    break;
            }

            // Bounds check
            if (offset < _prg.Length)
                data = _prg[offset];
            return true;
        }

        return false;
    }

    public bool CpuWrite(ushort addr, byte data)
    {
        // PRG RAM: $6000-$7FFF
        if (addr >= 0x6000 && addr <= 0x7FFF)
        {
            _prgRam[addr - 0x6000] = data;
            return true;
        }

        // Mapper registers: $8000-$FFFF
        if (addr >= 0x8000)
        {
            // Reset shift register if bit 7 is set
            if ((data & 0x80) != 0)
            {
                _shiftRegister = 0;
                _shiftCount = 0;
                _control |= 0x0C; // Set PRG mode to 3
                return true;
            }

            // Shift in bit 0
            _shiftRegister |= (byte)((data & 1) << _shiftCount);
            _shiftCount++;

            // After 5 writes, transfer to internal register
            if (_shiftCount == 5)
            {
                int reg = (addr >> 13) & 0x03; // Which register (0-3)

                switch (reg)
                {
                    case 0: // $8000-$9FFF: Control
                        _control = _shiftRegister;
                        break;
                    case 1: // $A000-$BFFF: CHR bank 0
                        _chrBank0 = _shiftRegister;
                        break;
                    case 2: // $C000-$DFFF: CHR bank 1
                        _chrBank1 = _shiftRegister;
                        break;
                    case 3: // $E000-$FFFF: PRG bank
                        _prgBank = _shiftRegister;
                        break;
                }

                _shiftRegister = 0;
                _shiftCount = 0;
            }

            return true;
        }

        return false;
    }

    public bool PpuRead(ushort addr, out byte data)
    {
        data = 0;

        if (addr <= 0x1FFF)
        {
            int chrMode = (_control >> 4) & 0x01;
            int offset;

            if (chrMode == 0)
            {
                // 8KB mode: use chrBank0, ignore bit 0
                int bank8 = (_chrBank0 & 0x1E) >> 1;
                offset = bank8 * 0x2000 + addr;
            }
            else
            {
                // 4KB mode
                if (addr < 0x1000)
                {
                    offset = _chrBank0 * 0x1000 + addr;
                }
                else
                {
                    offset = _chrBank1 * 0x1000 + (addr - 0x1000);
                }
            }

            // Bounds check (handle CHR RAM case)
            if (offset < _chr.Length)
                data = _chr[offset];
            return true;
        }

        return false;
    }

    public bool PpuWrite(ushort addr, byte data)
    {
        if (addr <= 0x1FFF)
        {
            // CHR RAM write (only if using CHR RAM, i.e., chrBanks == 0)
            if (_chrBanks == 0)
            {
                _chr[addr] = data;
                return true;
            }
        }

        return false;
    }
}
