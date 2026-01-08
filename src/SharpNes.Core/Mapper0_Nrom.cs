using System;
using System.IO;

namespace SharpNes.Core;

public sealed class Mapper0_Nrom : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly int _prgBanks;
    private readonly int _chrBanks;

    public Mapper0_Nrom(byte[] prgRom, byte[] chrRomOrRam, int prgBanks, int chrBanks)
    {
        _prg = prgRom ?? throw new ArgumentNullException(nameof(prgRom));
        _chr = chrRomOrRam ?? throw new ArgumentNullException(nameof(chrRomOrRam));
        _prgBanks = prgBanks;
        _chrBanks = chrBanks;
    }

    public bool CpuRead(ushort addr, out byte data)
    {
        data = 0;

        if (addr >= 0x8000)
        {
            // NROM-128: 16KB PRG mirrored
            // NROM-256: 32KB PRG
            int mapped = addr - 0x8000;

            if (_prgBanks == 1)
            {
                mapped &= 0x3FFF; // mirror 16KB
            }
            else
            {
                mapped &= 0x7FFF; // 32KB
            }

            data = _prg[mapped];
            return true;
        }

        return false;
    }

    public bool CpuWrite(ushort addr, byte data)
    {
        // Mapper 0 typically ignores CPU writes to PRG ROM space.
        // Some boards might have PRG RAM at $6000-$7FFF, but we’ll handle that in Bus first.
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
            // Only valid if using CHR RAM (chrBanks == 0)
            if (_chrBanks == 0)
            {
                _chr[addr] = data;
                return true;
            }
        }

        return false;
    }

    public void SaveState(BinaryWriter writer)
    {
        // Save CHR RAM if present
        if (_chrBanks == 0)
        {
            writer.Write(_chr);
        }
    }

    public void LoadState(BinaryReader reader)
    {
        // Load CHR RAM if present
        if (_chrBanks == 0)
        {
            reader.Read(_chr, 0, _chr.Length);
        }
    }
}