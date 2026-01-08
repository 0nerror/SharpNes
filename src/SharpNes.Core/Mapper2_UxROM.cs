using System;
using System.IO;

namespace SharpNes.Core;

public sealed class Mapper2_UxROM : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly int _prgBanks;
    private int _selectedBank;

    public Mapper2_UxROM(byte[] prgRom, byte[] chrRomOrRam, int prgBanks, int chrBanks)
    {
        _prg = prgRom ?? throw new ArgumentNullException(nameof(prgRom));
        _chr = chrRomOrRam ?? throw new ArgumentNullException(nameof(chrRomOrRam));
        _prgBanks = prgBanks;
        _selectedBank = 0;
    }

    public bool CpuRead(ushort addr, out byte data)
    {
        data = 0;

        if (addr >= 0x8000 && addr <= 0xBFFF)
        {
            // Switchable 16KB bank
            int offset = _selectedBank * 0x4000 + (addr - 0x8000);
            data = _prg[offset];
            return true;
        }

        if (addr >= 0xC000)
        {
            // Fixed to last 16KB bank
            int lastBank = _prgBanks - 1;
            int offset = lastBank * 0x4000 + (addr - 0xC000);
            data = _prg[offset];
            return true;
        }

        return false;
    }

    public bool CpuWrite(ushort addr, byte data)
    {
        if (addr >= 0x8000)
        {
            // Bank select - only lower bits matter depending on ROM size
            _selectedBank = data % _prgBanks;
            return true;
        }

        return false;
    }

    public bool PpuRead(ushort addr, out byte data)
    {
        data = 0;

        if (addr <= 0x1FFF)
        {
            data = _chr[addr];
            return true;
        }

        return false;
    }

    public bool PpuWrite(ushort addr, byte data)
    {
        if (addr <= 0x1FFF)
        {
            // UxROM uses CHR RAM
            _chr[addr] = data;
            return true;
        }

        return false;
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(_selectedBank);
        writer.Write(_chr);  // CHR RAM
    }

    public void LoadState(BinaryReader reader)
    {
        _selectedBank = reader.ReadInt32();
        reader.Read(_chr, 0, _chr.Length);
    }
}
