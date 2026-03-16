using NesEmu.Core.Cartridge;

namespace NesEmu.Core;

public sealed class Ppu2C02
{
    private const int OpenBusDecayCycles = 341 * 262 * 110;
    private const int MaskWriteDelay = 2;

    private readonly CartridgeImage _cartridge;
    private readonly byte[] _vram = new byte[0x1000];
    private readonly byte[] _paletteRam = new byte[0x20];
    private readonly byte[] _oam = new byte[0x100];
    private readonly SpriteState[] _sprites = new SpriteState[8];
    private readonly SpriteState[] _nextSprites = new SpriteState[8];
    private readonly uint[] _frameBuffer = new uint[NesVideoConstants.PixelsPerFrame];

    private byte _control;
    private byte _mask;
    private byte _queuedMask;
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
    private int _queuedMaskDelay;
    private bool _nmiEdgePending;
    private bool _nmiOutput;
    private bool _oamCorruptionPending;
    private bool _suppressVBlankForFrame;
    private bool _forceSpriteXToZeroOnNextScanline;
    private byte _oamCorruptionRow;
    private long _ppuCycleCount;

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
        _queuedMask = 0;
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
        _queuedMaskDelay = 0;
        _nmiEdgePending = false;
        _nmiOutput = false;
        _oamCorruptionPending = false;
        _suppressVBlankForFrame = false;
        _forceSpriteXToZeroOnNextScanline = false;
        _oamCorruptionRow = 0;
        _ppuCycleCount = 0;
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

    public bool IsNmiLineLow => _nmiOutput;

    public bool IsNmiLineLowOnUpcomingCpuSample =>
        ((_control & 0x80) != 0) &&
        !_suppressVBlankForFrame &&
        !_nmiOutput &&
        _scanline == 241 &&
        _cycle == 0;

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
                QueueMaskWrite(value);
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
        UpdateQueuedMask();
        ApplyPendingOamCorruption();

        var renderingEnabled = IsRenderingEnabled;
        var visibleScanline = _scanline is >= 0 and < 240;
        var prerenderScanline = _scanline == 261;

        if (visibleScanline && _cycle == 65 && renderingEnabled)
        {
            _spriteEvalOamBaseAddress = _oamAddress;
            EvaluateSpritesForNextScanline(_scanline + 1);
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
            else if (_cycle is >= 257 and <= 320)
            {
                _oamAddress = 0;

                if (_cycle == 257)
                {
                    LoadBackgroundRegisters();
                    ProcessSpriteFetchCycle(prerenderScanline);
                    CopyHorizontalScrollBits();
                }
                else
                {
                    ProcessSpriteFetchCycle(prerenderScanline);
                }
            }

            if (_cycle == 256)
            {
                IncrementVerticalScroll();
            }
            else if (_cycle == 257)
            {
                _oamAddress = 0;
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
        NotifyCartridgeCpuClock();
    }

    private void AdvanceCounters()
    {
        if (_scanline == 261 && _cycle == 339 && _oddFrame && IsRenderingEnabled)
        {
            _ppuCycleCount++;
            _cycle = 0;
            _scanline = 0;
            _oddFrame = false;
            PromoteNextSprites(preserveNextSprites: !IsRenderingEnabled);
            _frameCompleted = true;
            return;
        }

        _ppuCycleCount++;
        _cycle++;

        if (_cycle > 340)
        {
            _cycle = 0;
            _scanline++;

            if (_scanline is >= 0 and < 240)
            {
                PromoteNextSprites(preserveNextSprites: !IsRenderingEnabled);
            }

            if (_scanline > 261)
            {
                _scanline = 0;
                _oddFrame = !_oddFrame;
                PromoteNextSprites(preserveNextSprites: !IsRenderingEnabled);
                _frameCompleted = true;
            }
        }
    }

    private byte ReadStatus()
    {
        var suppressOneTickEarly = _scanline == 241 && _cycle == 0;
        var suppressVBlankStart = _scanline == 241 && _cycle == 1;
        var value = (byte)((_openBus & 0x1F) | (_status & 0xE0));

        if (suppressOneTickEarly)
        {
            _suppressVBlankForFrame = true;
        }
        else if (suppressVBlankStart)
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

    private void QueueMaskWrite(byte value)
    {
        _queuedMask = value;
        _queuedMaskDelay = MaskWriteDelay;
    }

    private void UpdateQueuedMask()
    {
        if (_queuedMaskDelay <= 0)
        {
            return;
        }

        _queuedMaskDelay--;
        if (_queuedMaskDelay == 0)
        {
            ApplyMask(_queuedMask);
        }
    }

    private void ApplyMask(byte value)
    {
        var wasRenderingEnabled = IsRenderingEnabled;
        _mask = value;

        if (wasRenderingEnabled && !IsRenderingEnabled)
        {
            ArmOamCorruption();
        }
    }

    private void ArmOamCorruption()
    {
        if (_scanline is not 261 and (< 0 or >= 240))
        {
            return;
        }

        if (_cycle is < 0 or > 63)
        {
            return;
        }

        _oamCorruptionPending = true;
        _oamCorruptionRow = (byte)(_cycle / 2);
    }

    private void ApplyPendingOamCorruption()
    {
        if (!_oamCorruptionPending ||
            !IsRenderingEnabled ||
            (_scanline is < 0 or >= 240) && _scanline != 261)
        {
            return;
        }

        var destination = _oamCorruptionRow * 8;
        if (destination < _oam.Length)
        {
            Array.Copy(_oam, 0, _oam, destination, Math.Min(8, _oam.Length - destination));
        }

        _oamCorruptionPending = false;
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
        _backgroundPatternLow <<= 1;
        _backgroundPatternHigh <<= 1;
        _backgroundAttributeLow <<= 1;
        _backgroundAttributeHigh <<= 1;
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
        var address = _oamAddress;
        var copyByteIndex = 0;
        var firstCandidateCanBeSpriteZero = true;
        var candidateIsSpriteZero = false;
        Span<byte> candidate = stackalloc byte[4];

        for (var step = 0; step < 96; step++)
        {
            var value = _oam[address];

            if (_nextSpriteCount < 8)
            {
                if (copyByteIndex == 0)
                {
                    candidate[0] = value;
                    candidateIsSpriteZero = firstCandidateCanBeSpriteZero;
                    firstCandidateCanBeSpriteZero = false;

                    if (!IsSpriteEvaluationValueInRange(value, targetScanline, spriteHeight))
                    {
                        address = (byte)((address + 4) & 0xFC);
                        continue;
                    }

                    copyByteIndex = 1;
                    address++;
                    continue;
                }

                candidate[copyByteIndex] = value;
                address++;

                if (copyByteIndex < 3)
                {
                    copyByteIndex++;
                    continue;
                }

                QueueNextSprite(candidate, candidateIsSpriteZero, targetScanline);
                address = IsSpriteEvaluationValueInRange(candidate[0], targetScanline, spriteHeight)
                    ? address
                    : (byte)(address & 0xFC);
                copyByteIndex = 0;
                continue;
            }

            if (IsSpriteEvaluationValueInRange(value, targetScanline, spriteHeight))
            {
                overflow = true;
                break;
            }

            address = AdvanceOverflowSearchAddress(address, out var wrapped);
            if (wrapped)
            {
                break;
            }
        }

        if (overflow)
        {
            _status |= 0x20;
        }
    }

    private void QueueNextSprite(ReadOnlySpan<byte> candidate, bool isSpriteZero, int targetScanline)
    {
        if (_nextSpriteCount >= 8)
        {
            return;
        }

        var row = targetScanline - candidate[0] - 1;
        if (row < 0 || row >= SpriteHeight)
        {
            return;
        }

        _nextSprites[_nextSpriteCount] = new SpriteState
        {
            YPosition = candidate[0],
            TileIndex = candidate[1],
            Attributes = candidate[2],
            XCounter = candidate[3],
            IsSpriteZero = isSpriteZero
        };

        _nextSpriteCount++;
    }

    private static bool IsSpriteEvaluationValueInRange(byte value, int targetScanline, int spriteHeight)
    {
        var row = targetScanline - value - 1;
        return row >= 0 && row < spriteHeight;
    }

    private static byte AdvanceOverflowSearchAddress(byte address, out bool wrapped)
    {
        var n = ((address >> 2) + 1) & 0x3F;
        var m = ((address & 0x03) + 1) & 0x03;
        wrapped = n == 0;
        return (byte)((n << 2) | m);
    }

    private void ProcessSpriteFetchCycle(bool prerenderScanline)
    {
        var slot = (_cycle - 257) >> 3;
        if (slot is < 0 or >= 8)
        {
            return;
        }

        var slotPhase = (_cycle - 257) & 0x07;
        switch (slotPhase)
        {
            case 0:
            case 2:
                ReadPpu(GetSpriteGarbageFetchAddress());
                break;
            case 4:
                FetchSpritePatternByte(slot, prerenderScanline, highPlane: false);
                break;
            case 6:
                FetchSpritePatternByte(slot, prerenderScanline, highPlane: true);
                break;
        }
    }

    private ushort GetSpriteGarbageFetchAddress()
    {
        // The first garbage fetch at dot 257 is a bus-mixed address on hardware.
        // Using the upcoming scanline's first nametable fetch keeps mapper-visible
        // accesses close to real hardware without overfitting to a half-dot model.
        return (ushort)(0x2000 | (_vramAddress & 0x0FFF));
    }

    private void FetchSpritePatternByte(int slot, bool prerenderScanline, bool highPlane)
    {
        var address = GetSpritePatternFetchAddress(slot, prerenderScanline);
        var value = ReadPpu(highPlane ? (ushort)(address + 8) : address);

        if (prerenderScanline || slot >= _nextSpriteCount)
        {
            return;
        }

        ref var sprite = ref _nextSprites[slot];
        if ((sprite.Attributes & 0x40) != 0)
        {
            value = ReverseBits(value);
        }

        if (highPlane)
        {
            sprite.PatternHigh = value;
        }
        else
        {
            sprite.PatternLow = value;
        }
    }

    private ushort GetSpritePatternFetchAddress(int slot, bool prerenderScanline)
    {
        if (!prerenderScanline && slot < _nextSpriteCount)
        {
            ref var sprite = ref _nextSprites[slot];
            var targetScanline = _scanline + 1;
            var row = (targetScanline - sprite.YPosition - 1) & 0xFF;
            return GetSpritePatternAddress(sprite.TileIndex, sprite.Attributes, row);
        }

        return GetSpritePatternAddress(0xFF, 0xFF, 0);
    }

    private void PromoteNextSprites(bool preserveNextSprites = false)
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

        if (!preserveNextSprites)
        {
            _nextSpriteCount = 0;
            Array.Clear(_nextSprites);
        }
    }

    private ushort GetSpritePatternAddress(byte tileIndex, byte attributes, int row)
    {
        var flipVertical = (attributes & 0x80) != 0;
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

        return address;
    }

    private (byte Low, byte High) FetchSpritePattern(byte tileIndex, byte attributes, int row)
    {
        var flipHorizontal = (attributes & 0x40) != 0;
        var address = GetSpritePatternAddress(tileIndex, attributes, row);
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
            _cartridge.OnPpuAddressAccess(address, _ppuCycleCount);
            value = _cartridge.PpuRead(address);
            return value;
        }

        if (address < 0x3F00)
        {
            value = _vram[MirrorNametableAddress(address)];
            return value;
        }

        value = _paletteRam[MirrorPaletteAddress(address)];
        return value;
    }

    private void WritePpu(ushort address, byte value)
    {
        address &= 0x3FFF;

        if (address < 0x2000)
        {
            _cartridge.OnPpuAddressAccess(address, _ppuCycleCount);
            _cartridge.PpuWrite(address, value);
            return;
        }

        if (address < 0x3F00)
        {
            _vram[MirrorNametableAddress(address)] = value;
            return;
        }

        _paletteRam[MirrorPaletteAddress(address)] = (byte)(value & 0x3F);
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

    private void NotifyCartridgeCpuClock()
    {
        if ((_ppuCycleCount % 3) == 0)
        {
            _cartridge.OnCpuClock();
        }
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
        public byte YPosition;
        public byte TileIndex;
        public byte PatternLow;
        public byte PatternHigh;
        public byte Attributes;
        public byte XCounter;
        public bool IsSpriteZero;
    }
}
