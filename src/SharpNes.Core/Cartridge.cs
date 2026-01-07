using System;

namespace SharpNes.Core;

public sealed class Cartridge
{
    public byte MapperNumber { get; }
    public int PrgRomBanks { get; }   // 16KB each
    public int ChrRomBanks { get; }   // 8KB each (0 means CHR RAM)
    public bool HasTrainer { get; }
    public bool VerticalMirroring { get; }
    public bool BatteryBackedRam { get; }

    public byte[] PrgRom { get; }
    public byte[] ChrRomOrRam { get; }

    public IMapper Mapper { get; }

    private Cartridge(
        byte mapperNumber,
        int prgRomBanks,
        int chrRomBanks,
        bool hasTrainer,
        bool verticalMirroring,
        bool batteryBackedRam,
        byte[] prgRom,
        byte[] chrRomOrRam,
        IMapper mapper)
    {
        MapperNumber = mapperNumber;
        PrgRomBanks = prgRomBanks;
        ChrRomBanks = chrRomBanks;
        HasTrainer = hasTrainer;
        VerticalMirroring = verticalMirroring;
        BatteryBackedRam = batteryBackedRam;
        PrgRom = prgRom;
        ChrRomOrRam = chrRomOrRam;
        Mapper = mapper;
    }

    // -----------------------------
    // CPU-side reads/writes ($4020-$FFFF typically)
    // -----------------------------
    public bool CpuRead(ushort addr, out byte data) => Mapper.CpuRead(addr, out data);

    public bool CpuWrite(ushort addr, byte data) => Mapper.CpuWrite(addr, data);

    // -----------------------------
    // PPU-side reads/writes ($0000-$1FFF typically for CHR)
    // -----------------------------
    public bool PpuRead(ushort addr, out byte data) => Mapper.PpuRead(addr, out data);

    public bool PpuWrite(ushort addr, byte data) => Mapper.PpuWrite(addr, data);

    // -----------------------------
    // Mirroring - mapper can override ROM header setting
    // -----------------------------
    public int GetMirrorMode()
    {
        int mapperMode = Mapper.MirrorMode;
        if (mapperMode >= 0)
            return mapperMode;
        // Use ROM header: vertical=2, horizontal=3
        return VerticalMirroring ? 2 : 3;
    }

    // -----------------------------
    // Scanline counter for MMC3
    // -----------------------------
    public void NotifyScanline()
    {
        if (Mapper is Mapper4_MMC3 mmc3)
        {
            mmc3.ScanlineCounter();
        }
    }

    public bool IrqPending()
    {
        if (Mapper is Mapper4_MMC3 mmc3)
        {
            return mmc3.IrqPending;
        }
        return false;
    }

    public void AcknowledgeIrq()
    {
        if (Mapper is Mapper4_MMC3 mmc3)
        {
            mmc3.AcknowledgeIrq();
        }
    }

    public static Cartridge LoadINES(byte[] romBytes)
    {
        if (romBytes.Length < 16)
            throw new ArgumentException("ROM is too small to be a valid iNES file.");

        if (romBytes[0] != (byte)'N' || romBytes[1] != (byte)'E' || romBytes[2] != (byte)'S' || romBytes[3] != 0x1A)
            throw new ArgumentException("Invalid iNES header. Missing NES\\x1A signature.");

        int prgBanks = romBytes[4];
        int chrBanks = romBytes[5];

        byte flags6 = romBytes[6];
        byte flags7 = romBytes[7];

        bool hasTrainer = (flags6 & 0b0000_0100) != 0;
        bool verticalMirroring = (flags6 & 0b0000_0001) != 0;
        bool batteryBacked = (flags6 & 0b0000_0010) != 0;

        byte mapper = (byte)(((flags7 & 0xF0)) | ((flags6 & 0xF0) >> 4));

        int offset = 16;

        if (hasTrainer)
        {
            offset += 512;
            if (romBytes.Length < offset)
                throw new ArgumentException("ROM indicates trainer, but file is truncated.");
        }

        int prgSize = prgBanks * 16 * 1024;
        int chrSize = chrBanks * 8 * 1024;

        if (romBytes.Length < offset + prgSize + chrSize)
            throw new ArgumentException("ROM file is truncated; not enough bytes for PRG/CHR.");

        byte[] prgRom = new byte[prgSize];
        Array.Copy(romBytes, offset, prgRom, 0, prgSize);
        offset += prgSize;

        byte[] chr;
        if (chrBanks == 0)
        {
            chr = new byte[8 * 1024];
        }
        else
        {
            chr = new byte[chrSize];
            Array.Copy(romBytes, offset, chr, 0, chrSize);
            offset += chrSize;
        }

        IMapper mapperImpl = mapper switch
        {
            0 => new Mapper0_Nrom(prgRom, chr, prgBanks, chrBanks),
            1 => new Mapper1_MMC1(prgRom, chr, prgBanks, chrBanks),
            2 => new Mapper2_UxROM(prgRom, chr, prgBanks, chrBanks),
            3 => new Mapper3_CNROM(prgRom, chr, prgBanks, chrBanks),
            4 => new Mapper4_MMC3(prgRom, chr, prgBanks, chrBanks),
            7 => new Mapper7_AxROM(prgRom, chr, prgBanks, chrBanks),
            9 => new Mapper9_MMC2(prgRom, chr, prgBanks, chrBanks),
            66 => new Mapper66_GxROM(prgRom, chr, prgBanks, chrBanks),
            _ => throw new NotSupportedException($"Mapper {mapper} not supported yet.")
        };

        return new Cartridge(
            mapperNumber: mapper,
            prgRomBanks: prgBanks,
            chrRomBanks: chrBanks,
            hasTrainer: hasTrainer,
            verticalMirroring: verticalMirroring,
            batteryBackedRam: batteryBacked,
            prgRom: prgRom,
            chrRomOrRam: chr,
            mapper: mapperImpl
        );
    }
}
