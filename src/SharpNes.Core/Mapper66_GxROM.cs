using System;
using System.IO;

namespace SharpNes.Core;

/// <summary>
/// Mapper 66 (GxROM) - Simple PRG and CHR bank switching
/// Used by: Super Mario Bros. + Duck Hunt, Dragon Power
/// </summary>
public sealed class Mapper66_GxROM : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly int _prgBanks;
    private readonly int _chrBanks;
    private int _prgBank;
    private int _chrBank;

    public Mapper66_GxROM(byte[] prgRom, byte[] chrRomOrRam, int prgBanks, int chrBanks)
    {
        _prg = prgRom ?? throw new ArgumentNullException(nameof(prgRom));
        _chr = chrRomOrRam ?? throw new ArgumentNullException(nameof(chrRomOrRam));
        _prgBanks = prgBanks;
        _chrBanks = chrBanks;
        _prgBank = 0;
        _chrBank = 0;
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
            // Bits 4-5: PRG bank (32KB)
            _prgBank = (data >> 4) & 0x03;
            // Bits 0-1: CHR bank (8KB)
            _chrBank = data & 0x03;
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
        // GxROM uses CHR ROM
        return false;
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(_prgBank);
        writer.Write(_chrBank);
    }

    public void LoadState(BinaryReader reader)
    {
        _prgBank = reader.ReadInt32();
        _chrBank = reader.ReadInt32();
    }
}
