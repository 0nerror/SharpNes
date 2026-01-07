using System;

namespace SharpNes.Core;

public sealed class Bus
{
    public Ppu Ppu { get; }
    public Apu Apu { get; }
    public NesController Controller1 { get; } = new();
    public NesController Controller2 { get; } = new();

    private readonly byte[] _ram = new byte[2048];
    private Cartridge? _cart;

    // Callback to sync PPU to current CPU time before reading time-sensitive PPU registers.
    public Action? SyncPpuCallback { get; set; }

    // OAM DMA state
    public bool OamDmaRequested { get; private set; }
    public byte OamDmaPage { get; private set; }

    public Bus()
    {
        Ppu = new Ppu();
        Apu = new Apu();
        // Set up DMC memory reader
        Apu.SetMemoryReader(addr => CpuRead(addr));
    }

    public Bus(Ppu ppu, Apu apu, Cartridge cart)
    {
        Ppu = ppu;
        Apu = apu;
        _cart = cart;
    }

    public void InsertCartridge(Cartridge cart)
    {
        _cart = cart;
        // Wire up PPU to read/write CHR ROM/RAM through cartridge
        Ppu.SetCartridge(
            addr =>
            {
                if (_cart.PpuRead(addr, out byte data))
                    return data;
                return 0;
            },
            (addr, data) =>
            {
                _cart.PpuWrite(addr, data);
            },
            () => _cart.GetMirrorMode()
        );
        // Wire up scanline counter for MMC3
        Ppu.SetScanlineCallback(() => _cart.NotifyScanline());
    }

    public bool MapperIrqPending => _cart?.IrqPending() ?? false;

    public void AcknowledgeMapperIrq() => _cart?.AcknowledgeIrq();

    public byte CpuRead(ushort addr)
    {
        return CpuReadInternal(addr, peek: false);
    }

    public byte CpuPeek(ushort addr)
    {
        return CpuReadInternal(addr, peek: true);
    }

    private byte CpuReadInternal(ushort addr, bool peek)
    {
        // 2KB internal RAM mirrored
        if (addr <= 0x1FFF)
        {
            return _ram[addr & 0x07FF];
        }

        // PPU registers mirrored
        if (addr >= 0x2000 && addr <= 0x3FFF)
        {
            // Sync PPU to current time before reading time-sensitive registers like $2002
            if (!peek && (addr & 0x0007) == 0x0002)
            {
                SyncPpuCallback?.Invoke();
            }
            return Ppu.CpuReadRegister(addr, peek);
        }

        // APU / I/O
        if (addr >= 0x4000 && addr <= 0x401F)
        {
            if (addr == 0x4015)
            {
                return Apu.CpuRead(addr);
            }
            // Controller reads
            if (addr == 0x4016)
            {
                return peek ? (byte)(Controller1.GetState() & 1) : Controller1.Read();
            }
            if (addr == 0x4017)
            {
                return peek ? (byte)(Controller2.GetState() & 1) : Controller2.Read();
            }
            return 0;
        }

        // Cartridge space
        if (_cart != null && _cart.CpuRead(addr, out var data))
            return data;

        return 0;
    }

    public void CpuWrite(ushort addr, byte value)
    {
        if (addr <= 0x1FFF)
        {
            _ram[addr & 0x07FF] = value;
            return;
        }

        if (addr >= 0x2000 && addr <= 0x3FFF)
        {
            Ppu.CpuWriteRegister(addr, value);
            return;
        }

        if (addr >= 0x4000 && addr <= 0x401F)
        {
            if (addr == 0x4014)
            {
                // OAM DMA - request DMA transfer
                OamDmaRequested = true;
                OamDmaPage = value;
                return;
            }
            if (addr <= 0x4013 || addr == 0x4015 || addr == 0x4017)
            {
                Apu.CpuWrite(addr, value);
                return;
            }
            // Controller strobe ($4016)
            if (addr == 0x4016)
            {
                Controller1.Write(value);
                Controller2.Write(value);
                return;
            }
            return;
        }

        if (_cart != null)
            _cart.CpuWrite(addr, value);
    }

    // Execute OAM DMA transfer (called by main loop when requested)
    public int ExecuteOamDma()
    {
        if (!OamDmaRequested)
            return 0;

        OamDmaRequested = false;

        // Copy 256 bytes from CPU memory page to OAM
        byte[] pageData = new byte[256];
        ushort baseAddr = (ushort)(OamDmaPage << 8);

        for (int i = 0; i < 256; i++)
        {
            pageData[i] = CpuRead((ushort)(baseAddr + i));
        }

        Ppu.OamDma(pageData, 0);

        // OAM DMA takes 513 or 514 CPU cycles
        return 513;
    }

    // Expose RAM for debugging
    public byte[] GetRam() => _ram;
}
