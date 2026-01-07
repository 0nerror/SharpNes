using System;

namespace SharpNes.Core;

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

    // ------------------------------------------------------------
    // Background fetch pipeline (shifters)
    // ------------------------------------------------------------
    private ushort _bgShiftPatternLo;
    private ushort _bgShiftPatternHi;
    private ushort _bgShiftAttrLo;
    private ushort _bgShiftAttrHi;

    private byte _bgNextTileId;
    private byte _bgNextTileAttr;
    private byte _bgNextTileLsb;
    private byte _bgNextTileMsb;

    // ------------------------------------------------------------
    // Sprite scanline evaluation (up to 8 sprites)
    // ------------------------------------------------------------
    private const int MaxSpritesPerScanline = 8;

    private readonly byte[] _sprX = new byte[MaxSpritesPerScanline];
    private readonly byte[] _sprAttr = new byte[MaxSpritesPerScanline];
    private readonly byte[] _sprIndex = new byte[MaxSpritesPerScanline];

    private readonly byte[] _sprShiftLo = new byte[MaxSpritesPerScanline];
    private readonly byte[] _sprShiftHi = new byte[MaxSpritesPerScanline];

    private int _sprCount;
    private bool _sprite0OnScanline;

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
                _t = (ushort)((_t & 0xF3FF) | ((value & 0x03) << 10)); // Nametable select bits into t
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
            // Pattern tables: read from CHR ROM/RAM (mapper sees these reads)
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

    // ------------------------------------------------------------
    // PPU ticking & rendering
    // ------------------------------------------------------------
    public void Tick()
    {
        bool renderingEnabled = (_ppumask & 0x18) != 0; // bg or sprites

        // Visible scanlines: 0-239
        // Pre-render scanline: 261
        bool isVisibleLine = Scanline >= 0 && Scanline <= 239;
        bool isPreRenderLine = Scanline == 261;
        bool isRenderLine = isVisibleLine || isPreRenderLine;

        // --------------------------------------------------------
        // Render pipeline (background fetches + sprite shifters)
        // --------------------------------------------------------
        // --------------------------------------------------------
        // Pixel output FIRST (before shifting) - only on visible scanlines, cycles 1..256
        // --------------------------------------------------------
        if (isVisibleLine && Cycle >= 1 && Cycle <= 256)
        {
            RenderPixelFromShifters();
        }

        if (renderingEnabled && isRenderLine)
        {
            // Cycles that do fetches: 1-256 and 321-336
            bool doFetch = (Cycle >= 1 && Cycle <= 256) || (Cycle >= 321 && Cycle <= 336);

            if (doFetch)
            {
                // Shift background shifters every cycle (AFTER rendering)
                UpdateBackgroundShifters();

                // Sprite shifters only update during visible portion (1-256), not prefetch (321-336)
                if (Cycle <= 256)
                    UpdateSpriteShifters();

                // Background fetch pipeline (8-cycle sequence)
                // Real NES timing:
                // cycle%8 == 1: fetch nametable byte
                // cycle%8 == 3: fetch attribute byte
                // cycle%8 == 5: fetch low pattern byte
                // cycle%8 == 7: fetch high pattern byte
                // cycle%8 == 0: load shifters into low byte, increment coarse X
                switch (Cycle & 0x07)
                {
                    case 1:
                        // Fetch nametable byte at START of 8-cycle sequence
                        _bgNextTileId = ReadVram((ushort)(0x2000 | (_v & 0x0FFF)));
                        break;

                    case 3:
                        // Attribute address
                        {
                            ushort attrAddr = (ushort)(0x23C0
                                | (_v & 0x0C00)
                                | ((_v >> 4) & 0x38)
                                | ((_v >> 2) & 0x07));

                            byte attr = ReadVram(attrAddr);

                            // Select quadrant
                            int shift = (int)(((_v >> 5) & 0x02) << 1) | (int)((_v & 0x02));
                            _bgNextTileAttr = (byte)((attr >> shift) & 0x03);
                        }
                        break;

                    case 5:
                        // Pattern low
                        {
                            ushort bgPatternBase = (ushort)((_ppuctrl & 0x10) != 0 ? 0x1000 : 0x0000); // BG = bit4
                            ushort fineY = (ushort)((_v >> 12) & 0x07);
                            ushort addr = (ushort)(bgPatternBase + (_bgNextTileId << 4) + fineY);
                            _bgNextTileLsb = ReadVram(addr);
                        }
                        break;

                    case 7:
                        // Pattern high
                        {
                            ushort bgPatternBase = (ushort)((_ppuctrl & 0x10) != 0 ? 0x1000 : 0x0000); // BG = bit4
                            ushort fineY = (ushort)((_v >> 12) & 0x07);
                            ushort addr = (ushort)(bgPatternBase + (_bgNextTileId << 4) + fineY);
                            _bgNextTileMsb = ReadVram((ushort)(addr + 8));
                        }
                        break;

                    case 0:
                        // End of 8-cycle sequence: load shifters and increment coarse X
                        LoadBackgroundShifters();
                        IncrementCoarseX();
                        break;
                }
            }

            // End of visible tile fetch region
            if (Cycle == 256)
            {
                IncrementFineY();
            }

            if (Cycle == 257)
            {
                // Copy horizontal bits from t to v
                _v = (ushort)((_v & 0x7BE0) | (_t & 0x041F));

                // Evaluate sprites for next scanline
                // On visible lines (0-239) and pre-render (261), evaluate for next scanline
                if (isVisibleLine || isPreRenderLine)
                    EvaluateSpritesForNextScanline();
            }

            // Copy vertical bits during pre-render line
            if (isPreRenderLine && Cycle >= 280 && Cycle <= 304)
            {
                _v = (ushort)((_v & 0x041F) | (_t & 0x7BE0));
            }
        }

        // --------------------------------------------------------
        // Status flags handling
        // --------------------------------------------------------
        if (isPreRenderLine && Cycle == 1)
        {
            // Clear vblank, sprite0hit, overflow
            _ppustatus &= 0x1F;
        }

        // MMC3 scanline IRQ callback - called when A12 rises during sprite fetch
        // This happens on visible scanlines (0-239) AND pre-render scanline (261)
        // when rendering is enabled. Cycle 260 approximates when sprite fetches occur.
        if ((isVisibleLine || isPreRenderLine) && renderingEnabled && Cycle == 260)
        {
            _scanlineCallback?.Invoke();
        }

        // Advance timing
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

    public void TickMany(long ticks)
    {
        for (long i = 0; i < ticks; i++)
            Tick();
    }

    // ------------------------------------------------------------
    // Background helpers
    // ------------------------------------------------------------
    private void UpdateBackgroundShifters()
    {
        bool renderBg = (_ppumask & 0x08) != 0;
        if (!renderBg) return;

        _bgShiftPatternLo <<= 1;
        _bgShiftPatternHi <<= 1;
        _bgShiftAttrLo <<= 1;
        _bgShiftAttrHi <<= 1;
    }

    private void LoadBackgroundShifters()
    {
        bool renderBg = (_ppumask & 0x08) != 0;
        if (!renderBg) return;

        _bgShiftPatternLo = (ushort)((_bgShiftPatternLo & 0xFF00) | _bgNextTileLsb);
        _bgShiftPatternHi = (ushort)((_bgShiftPatternHi & 0xFF00) | _bgNextTileMsb);

        // Attribute becomes two bits, replicated across the 8 pixels of the tile
        _bgShiftAttrLo = (ushort)((_bgShiftAttrLo & 0xFF00) | ((_bgNextTileAttr & 0x01) != 0 ? 0xFF : 0x00));
        _bgShiftAttrHi = (ushort)((_bgShiftAttrHi & 0xFF00) | ((_bgNextTileAttr & 0x02) != 0 ? 0xFF : 0x00));
    }

    private void IncrementCoarseX()
    {
        if ((_v & 0x001F) == 31)
        {
            _v &= 0xFFE0;
            _v ^= 0x0400; // switch horizontal nametable
        }
        else
        {
            _v++;
        }
    }

    private void IncrementFineY()
    {
        if ((_v & 0x7000) != 0x7000)
        {
            _v += 0x1000; // fine Y++
        }
        else
        {
            _v &= 0x0FFF; // fine Y = 0
            int coarseY = (_v & 0x03E0) >> 5;
            if (coarseY == 29)
            {
                coarseY = 0;
                _v ^= 0x0800; // switch vertical nametable
            }
            else if (coarseY == 31)
            {
                coarseY = 0;
            }
            else
            {
                coarseY++;
            }
            _v = (ushort)((_v & 0x7C1F) | (coarseY << 5));
        }
    }

    // ------------------------------------------------------------
    // Sprite helpers
    // ------------------------------------------------------------
    private void EvaluateSpritesForNextScanline()
    {
        _sprCount = 0;
        _sprite0OnScanline = false;

        bool renderSprites = (_ppumask & 0x10) != 0;
        if (!renderSprites)
            return;

        int spriteHeight = (_ppuctrl & 0x20) != 0 ? 16 : 8;

        // Clear overflow flag (we'll set it if >8)
        _ppustatus &= 0xDF;

        // We're evaluating for the NEXT scanline (sprites fetched at cycle 257 are used next line)
        // On pre-render line (261), next scanline is 0
        int nextScanline = (Scanline == 261) ? 0 : (Scanline + 1);

        for (int i = 0; i < 64; i++)
        {
            int o = i * 4;
            int spriteY = _oam[o + 0];
            int tileIndex = _oam[o + 1];
            byte attr = _oam[o + 2];
            byte spriteX = _oam[o + 3];

            // Check if sprite is visible on the next scanline
            int row = nextScanline - (spriteY + 1);
            if (row < 0 || row >= spriteHeight)
                continue;

            if (_sprCount < MaxSpritesPerScanline)
            {
                if (i == 0) _sprite0OnScanline = true;

                _sprIndex[_sprCount] = (byte)i;
                _sprX[_sprCount] = spriteX;
                _sprAttr[_sprCount] = attr;

                // Apply vertical flip to row
                if ((attr & 0x80) != 0)
                    row = spriteHeight - 1 - row;

                // Fetch pattern bytes for this sprite row (once per scanline)
                byte lo, hi;
                FetchSpritePatternBytes(tileIndex, row, spriteHeight, out lo, out hi);

                // If horizontal flip, reverse bits
                if ((attr & 0x40) != 0)
                {
                    lo = ReverseBits(lo);
                    hi = ReverseBits(hi);
                }

                _sprShiftLo[_sprCount] = lo;
                _sprShiftHi[_sprCount] = hi;

                _sprCount++;
            }
            else
            {
                // More than 8 sprites on scanline => sprite overflow
                _ppustatus |= 0x20;
                break;
            }
        }
    }

    private void FetchSpritePatternBytes(int tileIndex, int row, int spriteHeight, out byte lo, out byte hi)
    {
        if (spriteHeight == 16)
        {
            // 8x16: bit0 selects pattern table, tileIndex&FE is tile number
            ushort base16 = (ushort)((tileIndex & 0x01) != 0 ? 0x1000 : 0x0000);
            int tileNum = tileIndex & 0xFE;
            if (row >= 8)
            {
                tileNum++;
                row -= 8;
            }

            ushort addr = (ushort)(base16 + (tileNum << 4) + row);
            lo = ReadVram(addr);
            hi = ReadVram((ushort)(addr + 8));
        }
        else
        {
            // 8x8: sprite pattern table is PPUCTRL bit3 (0x08)
            ushort sprBase = (ushort)((_ppuctrl & 0x08) != 0 ? 0x1000 : 0x0000);
            ushort addr = (ushort)(sprBase + (tileIndex << 4) + row);
            lo = ReadVram(addr);
            hi = ReadVram((ushort)(addr + 8));
        }
    }

    private void UpdateSpriteShifters()
    {
        bool renderSprites = (_ppumask & 0x10) != 0;
        if (!renderSprites) return;

        for (int i = 0; i < _sprCount; i++)
        {
            if (_sprX[i] > 0)
            {
                _sprX[i]--;
            }
            else
            {
                _sprShiftLo[i] <<= 1;
                _sprShiftHi[i] <<= 1;
            }
        }
    }

    private static byte ReverseBits(byte b)
    {
        // 8-bit reverse
        b = (byte)((b & 0xF0) >> 4 | (b & 0x0F) << 4);
        b = (byte)((b & 0xCC) >> 2 | (b & 0x33) << 2);
        b = (byte)((b & 0xAA) >> 1 | (b & 0x55) << 1);
        return b;
    }

    // ------------------------------------------------------------
    // Pixel composition
    // ------------------------------------------------------------
    private void RenderPixelFromShifters()
    {
        int x = Cycle - 1;
        int y = Scanline;

        bool renderBg = (_ppumask & 0x08) != 0;
        bool renderSprites = (_ppumask & 0x10) != 0;
        bool renderLeft8Bg = (_ppumask & 0x02) != 0;
        bool renderLeft8Spr = (_ppumask & 0x04) != 0;

        byte finalColorIndex;

        // -------------------------
        // Background pixel
        // -------------------------
        int bgPixel = 0;
        int bgPalette = 0;

        if (renderBg && (x >= 8 || renderLeft8Bg))
        {
            // Use fine X to select bits from shifters
            // Shifters are aligned so that bit 15 is the current pixel
            int bit = 15 - _x;

            int p0 = (_bgShiftPatternLo >> bit) & 1;
            int p1 = (_bgShiftPatternHi >> bit) & 1;
            bgPixel = (p1 << 1) | p0;

            int a0 = (_bgShiftAttrLo >> bit) & 1;
            int a1 = (_bgShiftAttrHi >> bit) & 1;
            bgPalette = (a1 << 1) | a0;
        }

        // -------------------------
        // Sprite pixel and Sprite 0 hit detection
        // -------------------------
        int sprPixel = 0;
        int sprPalette = 0;
        bool sprPriorityBehindBg = false;
        bool sprite0Hit = false;

        if (renderSprites && (x >= 8 || renderLeft8Spr))
        {
            for (int i = 0; i < _sprCount; i++)
            {
                if (_sprX[i] != 0)
                    continue;

                int p0 = (_sprShiftLo[i] & 0x80) != 0 ? 1 : 0;
                int p1 = (_sprShiftHi[i] & 0x80) != 0 ? 1 : 0;
                int pix = (p1 << 1) | p0;

                if (pix == 0)
                    continue;

                // Check sprite 0 hit - must check even if another sprite wins priority
                // Sprite 0 hit occurs when sprite 0's opaque pixel overlaps opaque BG pixel
                if (_sprite0OnScanline && _sprIndex[i] == 0 && bgPixel != 0 && x < 255)
                {
                    sprite0Hit = true;
                }

                // First visible sprite wins for rendering (OAM priority)
                if (sprPixel == 0)
                {
                    sprPixel = pix;
                    sprPalette = (_sprAttr[i] & 0x03) + 4; // sprite palettes 4-7
                    sprPriorityBehindBg = (_sprAttr[i] & 0x20) != 0;
                }
            }
        }

        // Set sprite 0 hit flag
        if (sprite0Hit)
        {
            _ppustatus |= 0x40;
        }

        // -------------------------
        // Final mux
        // -------------------------
        int colorIndex;

        if (bgPixel == 0 && sprPixel == 0)
        {
            colorIndex = ReadVram(0x3F00) & 0x3F;
        }
        else if (bgPixel == 0 && sprPixel > 0)
        {
            colorIndex = ReadVram((ushort)(0x3F00 + (sprPalette << 2) + sprPixel)) & 0x3F;
        }
        else if (bgPixel > 0 && sprPixel == 0)
        {
            colorIndex = ReadVram((ushort)(0x3F00 + (bgPalette << 2) + bgPixel)) & 0x3F;
        }
        else
        {
            // both nonzero
            if (sprPriorityBehindBg)
                colorIndex = ReadVram((ushort)(0x3F00 + (bgPalette << 2) + bgPixel)) & 0x3F;
            else
                colorIndex = ReadVram((ushort)(0x3F00 + (sprPalette << 2) + sprPixel)) & 0x3F;
        }

        finalColorIndex = (byte)(colorIndex & 0x3F);

        // Write pixel to framebuffer
        uint color = NesPalette[finalColorIndex];
        int off = (y * 256 + x) * 4;
        Framebuffer[off + 0] = (byte)(color & 0xFF);         // B
        Framebuffer[off + 1] = (byte)((color >> 8) & 0xFF);  // G
        Framebuffer[off + 2] = (byte)((color >> 16) & 0xFF); // R
        Framebuffer[off + 3] = 0xFF;                         // A
    }

    // ------------------------------------------------------------
    // VBlank & NMI
    // ------------------------------------------------------------
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
