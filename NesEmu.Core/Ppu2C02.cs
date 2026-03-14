using NesEmu.Core.Cartridge;

namespace NesEmu.Core;

public sealed class Ppu2C02
{
    private const int OpenBusDecayCycles = 341 * 262 * 110;

    private readonly CartridgeImage _cartridge;
    private readonly byte[] _vram = new byte[0x1000];
    private readonly byte[] _paletteRam = new byte[0x20];
    private readonly byte[] _oam = new byte[0x100];
    private readonly SpriteState[] _sprites = new SpriteState[8];
    private readonly SpriteState[] _nextSprites = new SpriteState[8];
    private readonly uint[] _frameBuffer = new uint[NesVideoConstants.PixelsPerFrame];

    private byte _control;
    private byte _mask;
    private byte _status;
    private byte _oamAddress;
    private byte _openBus;
    private byte _readBuffer;
    private bool _writeToggle;

    private ushort _vramAddress;
    private ushort _tempVramAddress;
    private byte _fineX;

    private ushort _backgroundPatternLow;
    private ushort _backgroundPatternHigh;
    private ushort _backgroundAttributeLow;
    private ushort _backgroundAttributeHigh;

    private byte _nextTileId;
    private byte _nextTileAttribute;
    private byte _nextTilePatternLow;
    private byte _nextTilePatternHigh;

    private int _cycle;
    private int _scanline;
    private bool _oddFrame;
    private int _spriteEvalOamBaseAddress;
    private int _spriteCount;
    private int _nextSpriteCount;
    private bool _frameCompleted;
    private int _openBusDecayCounter;
    private bool _nmiEdgePending;
    private bool _nmiOutput;
    private bool _suppressVBlankForFrame;
    private bool _forceSpriteXToZeroOnNextScanline;

    public Ppu2C02(CartridgeImage cartridge)
    {
        _cartridge = cartridge;
        _scanline = 261;
    }

    public ReadOnlySpan<uint> FrameBuffer => _frameBuffer;

    public byte[] Oam => _oam;

    public void Reset()
    {
        _control = 0;
        _mask = 0;
        _status = 0;
        _oamAddress = 0;
        _openBus = 0;
        _readBuffer = 0;
        _writeToggle = false;
        _vramAddress = 0;
        _tempVramAddress = 0;
        _fineX = 0;
        _backgroundPatternLow = 0;
        _backgroundPatternHigh = 0;
        _backgroundAttributeLow = 0;
        _backgroundAttributeHigh = 0;
        _cycle = 0;
        _scanline = 261;
        _oddFrame = false;
        _spriteCount = 0;
        _nextSpriteCount = 0;
        _frameCompleted = false;
        _openBusDecayCounter = 0;
        _nmiEdgePending = false;
        _nmiOutput = false;
        _suppressVBlankForFrame = false;
        _forceSpriteXToZeroOnNextScanline = false;
        Array.Clear(_oam);
        Array.Clear(_vram);
        Array.Clear(_paletteRam);
        Array.Clear(_sprites);
        Array.Clear(_nextSprites);
    }

    public bool ConsumeFrameCompleted()
    {
        var value = _frameCompleted;
        _frameCompleted = false;
        return value;
    }

    public bool ConsumeNmi()
    {
        var value = _nmiEdgePending;
        _nmiEdgePending = false;
        return value;
    }

    public byte CpuRead(ushort address)
    {
        address = (ushort)(0x2000 | (address & 0x0007));

        return address switch
        {
            0x2002 => ReadStatus(),
            0x2004 => ReadOamData(),
            0x2007 => ReadData(),
            _ => _openBus
        };
    }

    public void CpuWrite(ushort address, byte value)
    {
        LatchOpenBus(value);
        address = (ushort)(0x2000 | (address & 0x0007));

        switch (address)
        {
            case 0x2000:
                WriteControl(value);
                break;
            case 0x2001:
                _mask = value;
                break;
            case 0x2003:
                _oamAddress = value;
                break;
            case 0x2004:
                WriteOamData(value);
                break;
            case 0x2005:
                WriteScroll(value);
                break;
            case 0x2006:
                WriteAddress(value);
                break;
            case 0x2007:
                WriteData(value);
                break;
        }
    }

    public void WriteOamDma(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < 256; i++)
        {
            _oam[_oamAddress++] = data[i];
        }
    }

    public void Clock()
    {
        DecayOpenBus();

        var renderingEnabled = IsRenderingEnabled;
        var visibleScanline = _scanline is >= 0 and < 240;
        var prerenderScanline = _scanline == 261;

        if ((visibleScanline || prerenderScanline) && _cycle == 65 && renderingEnabled)
        {
            _spriteEvalOamBaseAddress = _oamAddress;
            EvaluateSpritesForNextScanline(prerenderScanline ? 0 : _scanline + 1);
        }

        if ((visibleScanline || prerenderScanline) && renderingEnabled)
        {
            if (_cycle is >= 1 and <= 256 || _cycle is >= 321 and <= 336)
            {
                ShiftBackgroundRegisters();
                if (visibleScanline && _cycle <= 256)
                {
                    RenderPixel();
                }

                switch ((_cycle - 1) & 0x07)
                {
                    case 0:
                        LoadBackgroundRegisters();
                        _nextTileId = ReadPpu((ushort)(0x2000 | (_vramAddress & 0x0FFF)));
                        break;
                    case 2:
                        _nextTileAttribute = FetchAttributeBits();
                        break;
                    case 4:
                        _nextTilePatternLow = ReadPpu((ushort)(BackgroundPatternBase + (_nextTileId * 16) + FineY));
                        break;
                    case 6:
                        _nextTilePatternHigh = ReadPpu((ushort)(BackgroundPatternBase + (_nextTileId * 16) + FineY + 8));
                        break;
                    case 7:
                        IncrementHorizontalScroll();
                        break;
                }

                StepSpriteShifters();
            }

            if (_cycle == 256)
            {
                IncrementVerticalScroll();
            }
            else if (_cycle == 257)
            {
                _oamAddress = 0;
                LoadNextSpritePatterns();
                LoadBackgroundRegisters();
                CopyHorizontalScrollBits();
            }
            else if (prerenderScanline && _cycle is >= 280 and <= 304)
            {
                CopyVerticalScrollBits();
            }
            else if (_cycle is 338 or 340)
            {
                _nextTileId = ReadPpu((ushort)(0x2000 | (_vramAddress & 0x0FFF)));
            }
        }
        else if (visibleScanline && _cycle is >= 1 and <= 256)
        {
            RenderPixel();
        }

        if (prerenderScanline && _cycle == 1)
        {
            _status &= 0x1F;
            _suppressVBlankForFrame = false;
            UpdateNmiOutput();
        }

        if ((visibleScanline || prerenderScanline) && _cycle == 339)
        {
            _forceSpriteXToZeroOnNextScanline = !renderingEnabled;
        }

        if (_scanline == 241 && _cycle == 1)
        {
            if (!_suppressVBlankForFrame)
            {
                _status |= 0x80;
                UpdateNmiOutput();
            }
        }

        AdvanceCounters();
    }

    private void AdvanceCounters()
    {
        if (_scanline == 261 && _cycle == 339 && _oddFrame && IsRenderingEnabled)
        {
            _cycle = 0;
            _scanline = 0;
            _oddFrame = false;
            PromoteNextSprites();
            _frameCompleted = true;
            return;
        }

        _cycle++;

        if (_cycle > 340)
        {
            _cycle = 0;
            _scanline++;

            if (_scanline is >= 0 and < 240)
            {
                PromoteNextSprites();
            }

            if (_scanline > 261)
            {
                _scanline = 0;
                _oddFrame = !_oddFrame;
                PromoteNextSprites();
                _frameCompleted = true;
            }
        }
    }

    private byte ReadStatus()
    {
        var suppressVBlankStart = _scanline == 241 && _cycle == 1;
        var value = (byte)((_openBus & 0x1F) | (_status & 0xE0));

        if (suppressVBlankStart)
        {
            value &= 0x7F;
            _suppressVBlankForFrame = true;
        }

        _status &= 0x7F;
        _writeToggle = false;
        UpdateNmiOutput();
        LatchOpenBus(value);
        return value;
    }

    private byte ReadOamData()
    {
        var value = ReadCurrentOamData();
        LatchOpenBus(value);
        return _openBus;
    }

    private void WriteOamData(byte value)
    {
        if (IsRenderingActiveScanline)
        {
            _oamAddress = (byte)((_oamAddress + 4) & 0xFC);
            return;
        }

        _oam[_oamAddress++] = value;
    }

    private byte ReadData()
    {
        var address = _vramAddress;
        IncrementDataAddressForAccess();

        if (address >= 0x3F00)
        {
            var value = (byte)((_openBus & 0xC0) | (ReadPpu(address) & 0x3F));
            _readBuffer = ReadPpu((ushort)(address - 0x1000));
            LatchOpenBus(value);
            return value;
        }

        var buffered = _readBuffer;
        _readBuffer = ReadPpu(address);
        LatchOpenBus(buffered);
        return buffered;
    }

    private void WriteControl(byte value)
    {
        _control = value;
        _tempVramAddress = (ushort)((_tempVramAddress & 0xF3FF) | ((value & 0x03) << 10));
        UpdateNmiOutput();
    }

    private void WriteScroll(byte value)
    {
        if (!_writeToggle)
        {
            _fineX = (byte)(value & 0x07);
            _tempVramAddress = (ushort)((_tempVramAddress & 0xFFE0) | (value >> 3));
            _writeToggle = true;
            return;
        }

        _tempVramAddress = (ushort)((_tempVramAddress & 0x8C1F) | ((value & 0x07) << 12) | ((value & 0xF8) << 2));
        _writeToggle = false;
    }

    private void WriteAddress(byte value)
    {
        if (!_writeToggle)
        {
            _tempVramAddress = (ushort)((_tempVramAddress & 0x00FF) | ((value & 0x3F) << 8));
            _writeToggle = true;
            return;
        }

        _tempVramAddress = (ushort)((_tempVramAddress & 0xFF00) | value);
        _vramAddress = _tempVramAddress;
        _writeToggle = false;
    }

    private void WriteData(byte value)
    {
        WritePpu(_vramAddress, value);
        IncrementDataAddressForAccess();
    }

    private void IncrementDataAddress()
    {
        _vramAddress += (ushort)((_control & 0x04) != 0 ? 32 : 1);
    }

    private void IncrementDataAddressForAccess()
    {
        if (IsRenderingActiveScanline)
        {
            IncrementHorizontalScroll();
            IncrementVerticalScroll();
            return;
        }

        IncrementDataAddress();
    }

    private void RenderPixel()
    {
        var x = _cycle - 1;
        var y = _scanline;

        var leftColumn = x < 8;
        var showBackground = ShowBackground && (!leftColumn || (_mask & 0x02) != 0);
        var showSprites = ShowSprites && (!leftColumn || (_mask & 0x04) != 0);

        byte backgroundPixel = 0;
        byte backgroundPalette = 0;

        if (showBackground)
        {
            var mask = (ushort)(0x8000 >> _fineX);
            var low = (_backgroundPatternLow & mask) != 0 ? 1 : 0;
            var high = (_backgroundPatternHigh & mask) != 0 ? 1 : 0;
            backgroundPixel = (byte)((high << 1) | low);

            var attrLow = (_backgroundAttributeLow & mask) != 0 ? 1 : 0;
            var attrHigh = (_backgroundAttributeHigh & mask) != 0 ? 1 : 0;
            backgroundPalette = (byte)((attrHigh << 1) | attrLow);
        }

        byte spritePixel = 0;
        byte spritePalette = 0;
        var spriteBehindBackground = false;
        var spriteZeroHitPossible = false;

        if (showSprites)
        {
            GetSpritePixel(ref spritePixel, ref spritePalette, ref spriteBehindBackground, ref spriteZeroHitPossible);
        }

        byte paletteIndex;

        if (backgroundPixel == 0 && spritePixel == 0)
        {
            paletteIndex = ReadPalette(0);
        }
        else if (backgroundPixel == 0)
        {
            paletteIndex = ReadPalette((byte)(0x10 + (spritePalette << 2) + spritePixel));
        }
        else if (spritePixel == 0)
        {
            paletteIndex = ReadPalette((byte)((backgroundPalette << 2) + backgroundPixel));
        }
        else
        {
            if (spriteZeroHitPossible && x < 255)
            {
                _status |= 0x40;
            }

            paletteIndex = spriteBehindBackground
                ? ReadPalette((byte)((backgroundPalette << 2) + backgroundPixel))
                : ReadPalette((byte)(0x10 + (spritePalette << 2) + spritePixel));
        }

        _frameBuffer[y * NesVideoConstants.Width + x] = NesPalette.GetArgb32(paletteIndex, _mask);
    }

    private void GetSpritePixel(ref byte spritePixel, ref byte spritePalette, ref bool spriteBehindBackground, ref bool spriteZeroHitPossible)
    {
        for (var i = 0; i < _spriteCount; i++)
        {
            ref var sprite = ref _sprites[i];
            if (sprite.XCounter != 0)
            {
                continue;
            }

            var low = (sprite.PatternLow & 0x80) != 0 ? 1 : 0;
            var high = (sprite.PatternHigh & 0x80) != 0 ? 1 : 0;
            var pixel = (byte)((high << 1) | low);

            if (pixel == 0)
            {
                continue;
            }

            spritePixel = pixel;
            spritePalette = (byte)(sprite.Attributes & 0x03);
            spriteBehindBackground = (sprite.Attributes & 0x20) != 0;
            spriteZeroHitPossible = sprite.IsSpriteZero;
            return;
        }
    }

    private void ShiftBackgroundRegisters()
    {
        if (IsRenderingEnabled)
        {
            _backgroundPatternLow <<= 1;
            _backgroundPatternHigh = (ushort)((_backgroundPatternHigh << 1) | 0x0001);
            _backgroundAttributeLow <<= 1;
            _backgroundAttributeHigh = (ushort)((_backgroundAttributeHigh << 1) | 0x0001);
        }
    }

    private void StepSpriteShifters()
    {
        if (!IsRenderingEnabled || _cycle is < 1 or > 256)
        {
            return;
        }

        for (var i = 0; i < _spriteCount; i++)
        {
            ref var sprite = ref _sprites[i];
            if (sprite.XCounter > 0)
            {
                sprite.XCounter--;
                continue;
            }

            sprite.PatternLow <<= 1;
            sprite.PatternHigh <<= 1;
        }
    }

    private void LoadBackgroundRegisters()
    {
        _backgroundPatternLow = (ushort)((_backgroundPatternLow & 0xFF00) | _nextTilePatternLow);
        _backgroundPatternHigh = (ushort)((_backgroundPatternHigh & 0xFF00) | _nextTilePatternHigh);
        _backgroundAttributeLow = (ushort)((_backgroundAttributeLow & 0xFF00) | ((_nextTileAttribute & 0x01) != 0 ? 0xFF : 0x00));
        _backgroundAttributeHigh = (ushort)((_backgroundAttributeHigh & 0xFF00) | ((_nextTileAttribute & 0x02) != 0 ? 0xFF : 0x00));
    }

    private byte FetchAttributeBits()
    {
        var address = (ushort)(0x23C0 | (_vramAddress & 0x0C00) | ((_vramAddress >> 4) & 0x38) | ((_vramAddress >> 2) & 0x07));
        var attribute = ReadPpu(address);
        var shift = (byte)(((_vramAddress >> 4) & 4) | (_vramAddress & 2));
        return (byte)((attribute >> shift) & 0x03);
    }

    private void IncrementHorizontalScroll()
    {
        if ((_vramAddress & 0x001F) == 31)
        {
            _vramAddress &= 0xFFE0;
            _vramAddress ^= 0x0400;
        }
        else
        {
            _vramAddress++;
        }
    }

    private void IncrementVerticalScroll()
    {
        if ((_vramAddress & 0x7000) != 0x7000)
        {
            _vramAddress += 0x1000;
            return;
        }

        _vramAddress &= 0x8FFF;
        var y = (ushort)((_vramAddress & 0x03E0) >> 5);
        if (y == 29)
        {
            y = 0;
            _vramAddress ^= 0x0800;
        }
        else if (y == 31)
        {
            y = 0;
        }
        else
        {
            y++;
        }

        _vramAddress = (ushort)((_vramAddress & 0xFC1F) | (y << 5));
    }

    private void CopyHorizontalScrollBits()
    {
        _vramAddress = (ushort)((_vramAddress & 0xFBE0) | (_tempVramAddress & 0x041F));
    }

    private void CopyVerticalScrollBits()
    {
        _vramAddress = (ushort)((_vramAddress & 0x841F) | (_tempVramAddress & 0x7BE0));
    }

    private void EvaluateSpritesForNextScanline(int targetScanline)
    {
        Array.Clear(_nextSprites);
        _nextSpriteCount = 0;
        var spriteHeight = SpriteHeight;
        var overflow = false;
        var spriteZeroIndex = (_oamAddress & 0x03) == 0 ? (_oamAddress >> 2) & 0x3F : -1;

        for (var i = 0; i < 64; i++)
        {
            var spriteIndex = (spriteZeroIndex + 64 + i) & 0x3F;
            if (spriteZeroIndex < 0)
            {
                spriteIndex = i;
            }

            var spriteBase = spriteIndex * 4;
            var spriteY = _oam[spriteBase];
            var row = targetScanline - spriteY - 1;
            if (row < 0 || row >= spriteHeight)
            {
                continue;
            }

            if (_nextSpriteCount == 8)
            {
                overflow = true;
                break;
            }

            var tileIndex = _oam[spriteBase + 1];
            var attributes = _oam[spriteBase + 2];
            var x = _oam[spriteBase + 3];

            _nextSprites[_nextSpriteCount] = new SpriteState
            {
                TileIndex = tileIndex,
                Attributes = attributes,
                XCounter = x,
                Row = (byte)row,
                IsSpriteZero = spriteIndex == spriteZeroIndex
            };

            _nextSpriteCount++;
        }

        if (overflow)
        {
            _status |= 0x20;
        }
        else
        {
            _status &= unchecked((byte)~0x20);
        }
    }

    private void LoadNextSpritePatterns()
    {
        for (var i = 0; i < _nextSpriteCount; i++)
        {
            ref var sprite = ref _nextSprites[i];
            var (low, high) = FetchSpritePattern(sprite.TileIndex, sprite.Attributes, sprite.Row);
            sprite.PatternLow = low;
            sprite.PatternHigh = high;
        }
    }

    private void PromoteNextSprites()
    {
        Array.Clear(_sprites);
        Array.Copy(_nextSprites, _sprites, _nextSprites.Length);
        _spriteCount = _nextSpriteCount;

        if (_forceSpriteXToZeroOnNextScanline)
        {
            for (var i = 0; i < _spriteCount; i++)
            {
                _sprites[i].XCounter = 0;
            }

            _forceSpriteXToZeroOnNextScanline = false;
        }

        _nextSpriteCount = 0;
        Array.Clear(_nextSprites);
    }

    private (byte Low, byte High) FetchSpritePattern(byte tileIndex, byte attributes, int row)
    {
        var flipVertical = (attributes & 0x80) != 0;
        var flipHorizontal = (attributes & 0x40) != 0;
        var effectiveRow = flipVertical ? SpriteHeight - 1 - row : row;

        ushort address;
        if (SpriteHeight == 16)
        {
            var bank = (ushort)((tileIndex & 0x01) * 0x1000);
            var tile = (byte)(tileIndex & 0xFE);
            if (effectiveRow > 7)
            {
                tile++;
                effectiveRow -= 8;
            }

            address = (ushort)(bank + tile * 16 + effectiveRow);
        }
        else
        {
            address = (ushort)(SpritePatternBase + tileIndex * 16 + effectiveRow);
        }

        var low = ReadPpu(address);
        var high = ReadPpu((ushort)(address + 8));

        if (flipHorizontal)
        {
            low = ReverseBits(low);
            high = ReverseBits(high);
        }

        return (low, high);
    }

    private byte ReadPpu(ushort address)
    {
        address &= 0x3FFF;
        byte value;

        if (address < 0x2000)
        {
            value = _cartridge.PpuRead(address);
            _cartridge.OnPpuAddressAccess(address);
            return value;
        }

        if (address < 0x3F00)
        {
            value = _vram[MirrorNametableAddress(address)];
            _cartridge.OnPpuAddressAccess(address);
            return value;
        }

        value = _paletteRam[MirrorPaletteAddress(address)];
        _cartridge.OnPpuAddressAccess(address);
        return value;
    }

    private void WritePpu(ushort address, byte value)
    {
        address &= 0x3FFF;

        if (address < 0x2000)
        {
            _cartridge.PpuWrite(address, value);
            _cartridge.OnPpuAddressAccess(address);
            return;
        }

        if (address < 0x3F00)
        {
            _vram[MirrorNametableAddress(address)] = value;
            _cartridge.OnPpuAddressAccess(address);
            return;
        }

        _paletteRam[MirrorPaletteAddress(address)] = (byte)(value & 0x3F);
        _cartridge.OnPpuAddressAccess(address);
    }

    private int MirrorNametableAddress(ushort address)
    {
        var offset = (address - 0x2000) & 0x0FFF;
        var table = offset / 0x0400;
        var inner = offset & 0x03FF;

        return _cartridge.Mirroring switch
        {
            MirroringMode.Horizontal => (table >> 1) * 0x0400 + inner,
            MirroringMode.Vertical => (table & 0x01) * 0x0400 + inner,
            MirroringMode.SingleScreenLower => inner,
            MirroringMode.SingleScreenUpper => 0x0400 + inner,
            MirroringMode.FourScreen => offset,
            _ => inner
        };
    }

    private static int MirrorPaletteAddress(ushort address)
    {
        var index = (address - 0x3F00) & 0x1F;
        return index switch
        {
            0x10 => 0x00,
            0x14 => 0x04,
            0x18 => 0x08,
            0x1C => 0x0C,
            _ => index
        };
    }

    private byte ReadPalette(byte paletteIndex)
    {
        var value = _paletteRam[MirrorPaletteAddress((ushort)(0x3F00 + paletteIndex))];
        if ((_mask & 0x01) != 0)
        {
            value &= 0x30;
        }

        return value;
    }

    private byte ReadCurrentOamData()
    {
        if (_scanline is >= 0 and < 240 && IsRenderingEnabled)
        {
            if (_cycle is >= 1 and <= 64)
            {
                return 0xFF;
            }

            if (_cycle is >= 65 and <= 256)
            {
                var offset = (_cycle - 65) / 2;
                var address = (byte)(_spriteEvalOamBaseAddress + offset);
                return MaskOamDataForRead(address, _oam[address]);
            }

            if (_cycle is >= 257 and <= 320)
            {
                return 0xFF;
            }
        }

        return MaskOamDataForRead(_oamAddress, _oam[_oamAddress]);
    }

    private static byte MaskOamDataForRead(int address, byte value) =>
        (address & 0x03) == 0x02 ? (byte)(value & 0xE3) : value;

    private void LatchOpenBus(byte value)
    {
        _openBus = value;
        _openBusDecayCounter = OpenBusDecayCycles;
    }

    private void DecayOpenBus()
    {
        if (_openBusDecayCounter <= 0)
        {
            return;
        }

        _openBusDecayCounter--;
        if (_openBusDecayCounter == 0)
        {
            _openBus = 0;
        }
    }

    private void UpdateNmiOutput()
    {
        var output = ((_control & 0x80) != 0) && ((_status & 0x80) != 0);
        if (!_nmiOutput && output)
        {
            _nmiEdgePending = true;
        }

        _nmiOutput = output;
    }

    private bool IsRenderingEnabled => (_mask & 0x18) != 0;

    private bool IsRenderingActiveScanline => IsRenderingEnabled && (_scanline is >= 0 and < 240 || _scanline == 261);

    private bool ShowBackground => (_mask & 0x08) != 0;

    private bool ShowSprites => (_mask & 0x10) != 0;

    private int BackgroundPatternBase => (_control & 0x10) != 0 ? 0x1000 : 0x0000;

    private int SpritePatternBase => (_control & 0x08) != 0 ? 0x1000 : 0x0000;

    private int SpriteHeight => (_control & 0x20) != 0 ? 16 : 8;

    private int FineY => (_vramAddress >> 12) & 0x07;

    private static byte ReverseBits(byte value)
    {
        value = (byte)(((value & 0xF0) >> 4) | ((value & 0x0F) << 4));
        value = (byte)(((value & 0xCC) >> 2) | ((value & 0x33) << 2));
        value = (byte)(((value & 0xAA) >> 1) | ((value & 0x55) << 1));
        return value;
    }

    private struct SpriteState
    {
        public byte TileIndex;
        public byte PatternLow;
        public byte PatternHigh;
        public byte Attributes;
        public byte XCounter;
        public byte Row;
        public bool IsSpriteZero;
    }
}
