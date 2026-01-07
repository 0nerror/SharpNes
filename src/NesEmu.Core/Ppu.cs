using System;

public sealed class Ppu
{
    // Framebuffer: 256x240 pixels, ARGB format
    public byte[] Framebuffer { get; } = new byte[256 * 240 * 4];
    public bool FrameReady { get; set; }

    // VRAM: 2KB nametable RAM (mirrored based on cartridge mirroring)
    private readonly byte[] _vram = new byte[2048];

    // Palette RAM: 32 bytes
    private readonly byte[] _paletteRam = new byte[32];

    // OAM: 256 bytes for 64 sprites
    private readonly byte[] _oam = new byte[256];

    // CHR ROM/RAM access via cartridge
    private Func<ushort, byte>? _readChr;
    private Action<ushort, byte>? _writeChr;
    private Func<int>? _getMirrorMode;
    private Action? _scanlineCallback;

    // Registers
    private byte _ppuctrl;     // $2000
    private byte _ppumask;     // $2001
    private byte _ppustatus;   // $2002
    private byte _oamaddr;     // $2003

    // Internal PPU registers for scrolling/addressing
    private ushort _v;         // Current VRAM address (15 bits)
    private ushort _t;         // Temporary VRAM address (15 bits)
    private byte _x;           // Fine X scroll (3 bits)
    private bool _w;           // Write toggle (first/second write)

    // Data buffer for $2007 reads
    private byte _ppudataBuffer;
    private byte _openBus;

    // Timing
    public int Scanline { get; private set; }
    public int Cycle { get; private set; }
    public long TotalPpuCycles { get; private set; }

    // Debug stats
    public long VBlankEvents { get; private set; }
    public long Status2002Reads { get; private set; }
    public long Status2002ReadsWithVBlank { get; private set; }

    // NMI
    private bool _nmiPending;

    // NES color palette (64 colors, RGB values)
    private static readonly uint[] NesPalette = {
        0xFF545454, 0xFF001E74, 0xFF081090, 0xFF300088, 0xFF440064, 0xFF5C0030, 0xFF540400, 0xFF3C1800,
        0xFF202A00, 0xFF083A00, 0xFF004000, 0xFF003C00, 0xFF00323C, 0xFF000000, 0xFF000000, 0xFF000000,
        0xFF989698, 0xFF084CC4, 0xFF3032EC, 0xFF5C1EE4, 0xFF8814B0, 0xFFA01464, 0xFF982220, 0xFF783C00,
        0xFF545A00, 0xFF287200, 0xFF087C00, 0xFF007628, 0xFF006678, 0xFF000000, 0xFF000000, 0xFF000000,
        0xFFECEEEC, 0xFF4C9AEC, 0xFF787CEC, 0xFFB062EC, 0xFFE454EC, 0xFFEC58B4, 0xFFEC6A64, 0xFFD48820,
        0xFFA0AA00, 0xFF74C400, 0xFF4CD020, 0xFF38CC6C, 0xFF38B4CC, 0xFF3C3C3C, 0xFF000000, 0xFF000000,
        0xFFECEEEC, 0xFFA8CCEC, 0xFFBCBCEC, 0xFFD4B2EC, 0xFFECAEEC, 0xFFECAED4, 0xFFECB4B0, 0xFFE4C490,
        0xFFCCD278, 0xFFB4DE78, 0xFFA8E290, 0xFF98E2B4, 0xFFA0D6E4, 0xFFA0A2A0, 0xFF000000, 0xFF000000,
    };

    public void SetCartridge(Func<ushort, byte> readChr, Action<ushort, byte>? writeChr, Func<int> getMirrorMode)
    {
        _readChr = readChr;
        _writeChr = writeChr;
        _getMirrorMode = getMirrorMode;
    }

    public void SetScanlineCallback(Action callback)
    {
        _scanlineCallback = callback;
    }

    public byte DebugStatusNoSideEffects()
    {
        return CpuReadRegister(0x2002, peek: true);
    }

    public byte CpuReadRegister(ushort cpuAddr, bool peek = false)
    {
        int reg = cpuAddr & 0x0007;
        byte value;

        switch (reg)
        {
            case 0x0: // $2000 PPUCTRL (write-only)
            case 0x1: // $2001 PPUMASK (write-only)
            case 0x3: // $2003 OAMADDR (write-only)
            case 0x5: // $2005 PPUSCROLL (write-only)
            case 0x6: // $2006 PPUADDR (write-only)
                value = _openBus;
                break;

            case 0x2: // $2002 PPUSTATUS
                value = (byte)((_ppustatus & 0xE0) | (_openBus & 0x1F));
                if (!peek)
                {
                    Status2002Reads++;
                    if ((_ppustatus & 0x80) != 0)
                        Status2002ReadsWithVBlank++;
                    _ppustatus &= 0x7F; // Clear VBlank flag
                    _w = false;         // Reset write toggle
                }
                break;

            case 0x4: // $2004 OAMDATA
                value = _oam[_oamaddr];
                break;

            case 0x7: // $2007 PPUDATA
                if ((_v & 0x3FFF) < 0x3F00)
                {
                    // Non-palette: return buffer, then load new value
                    value = _ppudataBuffer;
                    if (!peek)
                        _ppudataBuffer = ReadVram((ushort)(_v & 0x3FFF));
                }
                else
                {
                    // Palette: return immediately, buffer gets nametable "underneath"
                    value = ReadVram((ushort)(_v & 0x3FFF));
                    if (!peek)
                        _ppudataBuffer = ReadVram((ushort)((_v & 0x3FFF) - 0x1000));
                }
                if (!peek)
                    _v = (ushort)((_v + ((_ppuctrl & 0x04) != 0 ? 32 : 1)) & 0x7FFF);
                break;

            default:
                value = 0;
                break;
        }

        if (!peek)
            _openBus = value;

        return value;
    }

    public void CpuWriteRegister(ushort cpuAddr, byte value)
    {
        int reg = cpuAddr & 0x0007;
        _openBus = value;

        switch (reg)
        {
            case 0x0: // $2000 PPUCTRL
                _ppuctrl = value;
                _t = (ushort)((_t & 0xF3FF) | ((value & 0x03) << 10)); // Nametable select
                break;

            case 0x1: // $2001 PPUMASK
                _ppumask = value;
                break;

            case 0x2: // $2002 read-only
                break;

            case 0x3: // $2003 OAMADDR
                _oamaddr = value;
                break;

            case 0x4: // $2004 OAMDATA
                _oam[_oamaddr] = value;
                _oamaddr++;
                break;

            case 0x5: // $2005 PPUSCROLL
                if (!_w)
                {
                    // First write: X scroll
                    _t = (ushort)((_t & 0xFFE0) | (value >> 3));
                    _x = (byte)(value & 0x07);
                }
                else
                {
                    // Second write: Y scroll
                    _t = (ushort)((_t & 0x8C1F) | ((value & 0x07) << 12) | ((value & 0xF8) << 2));
                }
                _w = !_w;
                break;

            case 0x6: // $2006 PPUADDR
                if (!_w)
                {
                    // First write: high byte
                    _t = (ushort)((_t & 0x00FF) | ((value & 0x3F) << 8));
                }
                else
                {
                    // Second write: low byte, copy to v
                    _t = (ushort)((_t & 0xFF00) | value);
                    _v = _t;
                }
                _w = !_w;
                break;

            case 0x7: // $2007 PPUDATA
                WriteVram((ushort)(_v & 0x3FFF), value);
                _v = (ushort)((_v + ((_ppuctrl & 0x04) != 0 ? 32 : 1)) & 0x7FFF);
                break;
        }
    }

    // OAM DMA: copy 256 bytes to OAM
    public void OamDma(byte[] data, int srcOffset)
    {
        Array.Copy(data, srcOffset, _oam, 0, 256);
    }

    private byte ReadVram(ushort addr)
    {
        addr &= 0x3FFF;

        if (addr < 0x2000)
        {
            // Pattern tables: read from CHR ROM/RAM
            return _readChr?.Invoke(addr) ?? 0;
        }
        else if (addr < 0x3F00)
        {
            // Nametables
            return _vram[MirrorNametableAddr(addr)];
        }
        else
        {
            // Palette
            int palAddr = addr & 0x1F;
            if ((palAddr & 0x03) == 0) palAddr &= 0x0F; // Mirror $3F10/$3F14/$3F18/$3F1C to $3F00/etc
            return _paletteRam[palAddr];
        }
    }

    private void WriteVram(ushort addr, byte value)
    {
        addr &= 0x3FFF;

        if (addr < 0x2000)
        {
            // CHR RAM write (if cartridge supports it)
            _writeChr?.Invoke(addr, value);
        }
        else if (addr < 0x3F00)
        {
            // Nametables
            _vram[MirrorNametableAddr(addr)] = value;
        }
        else
        {
            // Palette
            int palAddr = addr & 0x1F;
            if ((palAddr & 0x03) == 0) palAddr &= 0x0F;
            _paletteRam[palAddr] = value;
        }
    }

    private int MirrorNametableAddr(ushort addr)
    {
        addr = (ushort)((addr - 0x2000) & 0x0FFF);
        int mode = _getMirrorMode?.Invoke() ?? 2; // Default to vertical

        switch (mode)
        {
            case 0: // One-screen, lower bank
                return addr & 0x03FF;
            case 1: // One-screen, upper bank
                return 0x0400 | (addr & 0x03FF);
            case 2: // Vertical: $2000=$2800, $2400=$2C00
                return addr & 0x07FF;
            case 3: // Horizontal: $2000=$2400, $2800=$2C00
            default:
                return ((addr & 0x0800) >> 1) | (addr & 0x03FF);
        }
    }

    public void Tick()
    {
        bool renderingEnabled = (_ppumask & 0x18) != 0;

        // Visible scanlines (0-239)
        if (Scanline < 240)
        {
            if (Cycle >= 1 && Cycle <= 256)
            {
                RenderPixel();
            }

            if (renderingEnabled)
            {
                // Increment fine Y at cycle 256
                if (Cycle == 256)
                {
                    IncrementFineY();
                }

                // Copy horizontal bits from t to v at cycle 257
                if (Cycle == 257)
                {
                    _v = (ushort)((_v & 0x7BE0) | (_t & 0x041F));
                }

                // MMC3 scanline counter - trigger at cycle 260
                if (Cycle == 260)
                {
                    _scanlineCallback?.Invoke();
                }
            }
        }

        // Pre-render scanline (261)
        if (Scanline == 261)
        {
            if (Cycle == 1)
            {
                _ppustatus &= 0x1F; // Clear VBlank, sprite 0 hit, sprite overflow
            }

            if (renderingEnabled)
            {
                // Copy horizontal bits from t to v at cycle 257
                if (Cycle == 257)
                {
                    _v = (ushort)((_v & 0x7BE0) | (_t & 0x041F));
                }

                // Copy vertical bits from t to v at cycles 280-304
                if (Cycle >= 280 && Cycle <= 304)
                {
                    _v = (ushort)((_v & 0x041F) | (_t & 0x7BE0));
                }
            }
        }

        Cycle++;
        TotalPpuCycles++;

        if (Cycle >= 341)
        {
            Cycle = 0;
            Scanline++;

            if (Scanline == 241)
            {
                EnterVBlank();
                FrameReady = true;
            }

            if (Scanline >= 262)
            {
                Scanline = 0;
            }
        }
    }

    private void IncrementFineY()
    {
        if ((_v & 0x7000) != 0x7000)
        {
            _v += 0x1000;       // Increment fine Y
        }
        else
        {
            _v &= 0x0FFF;       // Fine Y = 0
            int coarseY = (_v & 0x03E0) >> 5;
            if (coarseY == 29)
            {
                coarseY = 0;
                _v ^= 0x0800;   // Switch vertical nametable
            }
            else if (coarseY == 31)
            {
                coarseY = 0;    // Wrap without switching nametable
            }
            else
            {
                coarseY++;
            }
            _v = (ushort)((_v & 0x7C1F) | (coarseY << 5));
        }
    }

    public void TickMany(long ticks)
    {
        for (long i = 0; i < ticks; i++)
            Tick();
    }

    private void RenderPixel()
    {
        int x = Cycle - 1;
        int y = Scanline;

        byte bgColor = 0;
        int bgPixel = 0;
        bool renderBg = (_ppumask & 0x08) != 0;
        bool renderSprites = (_ppumask & 0x10) != 0;
        bool renderLeft8Bg = (_ppumask & 0x02) != 0;
        bool renderLeft8Spr = (_ppumask & 0x04) != 0;

        // Background rendering - use _v register for scroll position
        if (renderBg && (x >= 8 || renderLeft8Bg))
        {
            // Extract scroll components from _v register
            // _v format: 0yyy NNYY YYYX XXXX
            int coarseX = _v & 0x001F;
            int coarseY = (_v >> 5) & 0x001F;
            int ntSelect = (_v >> 10) & 0x03;
            int fineY = (_v >> 12) & 0x07;

            // Calculate effective X position including fine X scroll
            int effectiveX = x + _x;
            int tileOffset = effectiveX >> 3;
            int fineX = effectiveX & 7;

            // Calculate tile X with nametable switching
            int totalTileX = coarseX + tileOffset;
            int tileX = totalTileX & 0x1F;
            int tileNt = ntSelect;
            if (totalTileX >= 32)
            {
                tileNt ^= 0x01; // Switch horizontal nametable
            }

            ushort ntAddr = (ushort)(0x2000 + (tileNt << 10) + (coarseY << 5) + tileX);
            byte tileIndex = ReadVram(ntAddr);

            ushort patternBase = (ushort)((_ppuctrl & 0x10) != 0 ? 0x1000 : 0x0000);
            ushort patternAddr = (ushort)(patternBase + (tileIndex << 4) + fineY);

            byte patternLo = ReadVram(patternAddr);
            byte patternHi = ReadVram((ushort)(patternAddr + 8));

            int bit = 7 - fineX;
            bgPixel = ((patternLo >> bit) & 1) | (((patternHi >> bit) & 1) << 1);

            if (bgPixel != 0)
            {
                ushort attrAddr = (ushort)(0x23C0 + (tileNt << 10) + ((coarseY >> 2) << 3) + (tileX >> 2));
                byte attr = ReadVram(attrAddr);
                int shift = ((coarseY & 0x02) << 1) | (tileX & 0x02);
                int palette = (attr >> shift) & 0x03;
                bgColor = ReadVram((ushort)(0x3F00 + (palette << 2) + bgPixel));
            }
            else
            {
                bgColor = ReadVram(0x3F00);
            }
        }
        else
        {
            bgColor = ReadVram(0x3F00);
        }

        // Sprite rendering
        byte spriteColor = 0;
        bool spritePriority = false;
        bool spriteFound = false;

        if (renderSprites && (x >= 8 || renderLeft8Spr))
        {
            int spriteHeight = (_ppuctrl & 0x20) != 0 ? 16 : 8;
            ushort spritePatternBase = (ushort)((_ppuctrl & 0x08) != 0 ? 0x1000 : 0x0000);

            // Check all 64 sprites (in reverse order so sprite 0 has priority)
            for (int i = 63; i >= 0; i--)
            {
                int spriteY = _oam[i * 4 + 0] + 1;  // Sprite Y is actually Y-1
                int tileIndex = _oam[i * 4 + 1];
                int attributes = _oam[i * 4 + 2];
                int spriteX = _oam[i * 4 + 3];

                // Check if sprite is on this scanline
                if (y < spriteY || y >= spriteY + spriteHeight)
                    continue;

                // Check if sprite is at this X position
                if (x < spriteX || x >= spriteX + 8)
                    continue;

                int row = y - spriteY;
                int col = x - spriteX;

                // Vertical flip
                if ((attributes & 0x80) != 0)
                    row = spriteHeight - 1 - row;

                // Horizontal flip
                if ((attributes & 0x40) != 0)
                    col = 7 - col;

                // Get pattern address
                ushort patternAddr;
                if (spriteHeight == 16)
                {
                    // 8x16 sprites: bit 0 of tile selects pattern table
                    ushort base16 = (ushort)((tileIndex & 0x01) != 0 ? 0x1000 : 0x0000);
                    int tileNum = tileIndex & 0xFE;
                    if (row >= 8) { tileNum++; row -= 8; }
                    patternAddr = (ushort)(base16 + (tileNum << 4) + row);
                }
                else
                {
                    patternAddr = (ushort)(spritePatternBase + (tileIndex << 4) + row);
                }

                byte patternLo = ReadVram(patternAddr);
                byte patternHi = ReadVram((ushort)(patternAddr + 8));

                int bit = 7 - col;
                int pixel = ((patternLo >> bit) & 1) | (((patternHi >> bit) & 1) << 1);

                if (pixel != 0)
                {
                    int palette = (attributes & 0x03) + 4;  // Sprite palettes are 4-7
                    spriteColor = ReadVram((ushort)(0x3F00 + (palette << 2) + pixel));
                    spritePriority = (attributes & 0x20) != 0;  // Behind background
                    spriteFound = true;

                    // Sprite 0 hit detection
                    if (i == 0 && bgPixel != 0 && x < 255 && renderBg)
                    {
                        _ppustatus |= 0x40;  // Set sprite 0 hit flag
                    }
                }
            }
        }

        // Combine background and sprite
        byte finalColor;
        if (spriteFound && (!spritePriority || bgPixel == 0))
        {
            finalColor = spriteColor;
        }
        else
        {
            finalColor = bgColor;
        }

        // Write pixel to framebuffer
        uint color = NesPalette[finalColor & 0x3F];
        int offset = (y * 256 + x) * 4;
        Framebuffer[offset + 0] = (byte)(color & 0xFF);         // B
        Framebuffer[offset + 1] = (byte)((color >> 8) & 0xFF);  // G
        Framebuffer[offset + 2] = (byte)((color >> 16) & 0xFF); // R
        Framebuffer[offset + 3] = 0xFF;                          // A
    }

    private void EnterVBlank()
    {
        _ppustatus |= 0x80;
        if ((_ppuctrl & 0x80) != 0)
            _nmiPending = true;
        VBlankEvents++;
    }

    public bool TryConsumeNmi()
    {
        if (_nmiPending)
        {
            _nmiPending = false;
            if ((_ppustatus & 0x80) != 0)
                return true;
        }
        return false;
    }
}
