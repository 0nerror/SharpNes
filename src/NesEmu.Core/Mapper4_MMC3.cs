using System;

namespace NesEmu.Core;

/// <summary>
/// Mapper 4 (MMC3) - Complex mapper with scanline counter
/// Used by: Super Mario Bros 2/3, Kirby's Adventure, Ninja Gaiden, many others
/// </summary>
public sealed class Mapper4_MMC3 : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly int _prgBanks;
    private readonly int _chrBanks;
    private readonly byte[] _prgRam = new byte[8192];

    // Bank registers
    private readonly int[] _bankRegisters = new int[8];
    private int _bankSelect;
    private bool _prgBankMode;
    private bool _chrInversion;

    // Mirroring
    private int _mirrorMode = 2; // Default vertical

    // IRQ
    private byte _irqLatch;
    private byte _irqCounter;
    private bool _irqEnabled;
    private bool _irqReload;
    public bool IrqPending { get; private set; }

    public int MirrorMode => _mirrorMode;

    public Mapper4_MMC3(byte[] prgRom, byte[] chrRomOrRam, int prgBanks, int chrBanks)
    {
        _prg = prgRom ?? throw new ArgumentNullException(nameof(prgRom));
        _chr = chrRomOrRam ?? throw new ArgumentNullException(nameof(chrRomOrRam));
        _prgBanks = prgBanks;
        _chrBanks = chrBanks;

        // Initialize bank registers
        _bankRegisters[0] = 0;
        _bankRegisters[1] = 2;
        _bankRegisters[2] = 4;
        _bankRegisters[3] = 5;
        _bankRegisters[4] = 6;
        _bankRegisters[5] = 7;
        _bankRegisters[6] = 0;
        _bankRegisters[7] = 1;
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
            int bank;
            int offset = addr & 0x1FFF;

            if (addr < 0xA000)
            {
                // $8000-$9FFF
                bank = _prgBankMode ? (_prgBanks * 2 - 2) : _bankRegisters[6];
            }
            else if (addr < 0xC000)
            {
                // $A000-$BFFF
                bank = _bankRegisters[7];
            }
            else if (addr < 0xE000)
            {
                // $C000-$DFFF
                bank = _prgBankMode ? _bankRegisters[6] : (_prgBanks * 2 - 2);
            }
            else
            {
                // $E000-$FFFF - Fixed to last bank
                bank = _prgBanks * 2 - 1;
            }

            int prgOffset = bank * 0x2000 + offset;
            if (prgOffset < _prg.Length)
                data = _prg[prgOffset];
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
            bool isEven = (addr & 1) == 0;

            if (addr < 0xA000)
            {
                if (isEven)
                {
                    // $8000: Bank select
                    _bankSelect = data & 0x07;
                    _prgBankMode = (data & 0x40) != 0;
                    _chrInversion = (data & 0x80) != 0;
                }
                else
                {
                    // $8001: Bank data
                    _bankRegisters[_bankSelect] = data;
                }
            }
            else if (addr < 0xC000)
            {
                if (isEven)
                {
                    // $A000: Mirroring
                    _mirrorMode = (data & 0x01) != 0 ? 3 : 2; // Horizontal : Vertical
                }
                // $A001: PRG RAM protect (ignored)
            }
            else if (addr < 0xE000)
            {
                if (isEven)
                {
                    // $C000: IRQ latch
                    _irqLatch = data;
                }
                else
                {
                    // $C001: IRQ reload
                    _irqCounter = 0;
                    _irqReload = true;
                }
            }
            else
            {
                if (isEven)
                {
                    // $E000: IRQ disable
                    _irqEnabled = false;
                    IrqPending = false;
                }
                else
                {
                    // $E001: IRQ enable
                    _irqEnabled = true;
                }
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
            int bank = GetChrBank(addr);
            int offset = bank * 0x400 + (addr & 0x3FF);
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
            // CHR RAM support
            if (_chrBanks == 0)
            {
                _chr[addr] = data;
                return true;
            }
        }

        return false;
    }

    private int GetChrBank(ushort addr)
    {
        int region = addr >> 10; // 0-7 for 1KB regions

        if (_chrInversion)
        {
            // CHR inversion: swap $0000-$0FFF and $1000-$1FFF
            region ^= 4;
        }

        return region switch
        {
            0 => _bankRegisters[0] & 0xFE,       // R0 (2KB, bit 0 ignored)
            1 => (_bankRegisters[0] & 0xFE) + 1,
            2 => _bankRegisters[1] & 0xFE,       // R1 (2KB, bit 0 ignored)
            3 => (_bankRegisters[1] & 0xFE) + 1,
            4 => _bankRegisters[2],              // R2 (1KB)
            5 => _bankRegisters[3],              // R3 (1KB)
            6 => _bankRegisters[4],              // R4 (1KB)
            7 => _bankRegisters[5],              // R5 (1KB)
            _ => 0
        };
    }

    /// <summary>
    /// Called by PPU at the end of each visible scanline (when A12 rises)
    /// </summary>
    public void ScanlineCounter()
    {
        if (_irqCounter == 0 || _irqReload)
        {
            _irqCounter = _irqLatch;
            _irqReload = false;
        }
        else
        {
            _irqCounter--;
        }

        if (_irqCounter == 0 && _irqEnabled)
        {
            IrqPending = true;
        }
    }

    public bool AcknowledgeIrq()
    {
        bool was = IrqPending;
        IrqPending = false;
        return was;
    }
}
