using NesEmu.Core;

namespace NesEmu.Tests;

public sealed class NesPaletteTests
{
    [Fact]
    public void Palette28_UsesNeutralSkinTone()
    {
        Assert.Equal(0xFFF0BC3Cu, NesPalette.GetArgb32(0x28, 0x00));
    }

    [Fact]
    public void RedEmphasis_DeemphasizesGreenAndBlueChannels()
    {
        var neutral = NesPalette.GetArgb32(0x27, 0x00);
        var emphasized = NesPalette.GetArgb32(0x27, 0x20);

        Assert.Equal(GetRed(neutral), GetRed(emphasized));
        Assert.True(GetGreen(emphasized) < GetGreen(neutral));
        Assert.True(GetBlue(emphasized) < GetBlue(neutral));
    }

    private static byte GetRed(uint color) => (byte)(color >> 16);

    private static byte GetGreen(uint color) => (byte)(color >> 8);

    private static byte GetBlue(uint color) => (byte)color;
}
