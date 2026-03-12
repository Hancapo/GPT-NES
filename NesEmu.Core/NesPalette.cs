namespace NesEmu.Core;

public static class NesPalette
{
    private const float EmphasisDeemphasisFactor = 0.82f;

    private static readonly uint[] BaseArgb32 =
    [
        0xFF747474, 0xFF24188C, 0xFF0000A8, 0xFF44009C, 0xFF8C0074, 0xFFA80010, 0xFFA40000, 0xFF7C0800,
        0xFF402C00, 0xFF004400, 0xFF005000, 0xFF003C14, 0xFF183C5C, 0xFF000000, 0xFF000000, 0xFF000000,
        0xFFBCBCBC, 0xFF0070EC, 0xFF2038EC, 0xFF8000F0, 0xFFBC00BC, 0xFFE40058, 0xFFD82800, 0xFFC84C0C,
        0xFF887000, 0xFF009400, 0xFF00A800, 0xFF009038, 0xFF008088, 0xFF000000, 0xFF000000, 0xFF000000,
        0xFFFCFCFC, 0xFF3CBCFC, 0xFF5C94FC, 0xFFCC88FC, 0xFFF478FC, 0xFFFC74B4, 0xFFFC7460, 0xFFFC9838,
        0xFFF0BC3C, 0xFF80D010, 0xFF4CDC48, 0xFF58F898, 0xFF00E8D8, 0xFF787878, 0xFF000000, 0xFF000000,
        0xFFFCFCFC, 0xFFA8E4FC, 0xFFC4D4FC, 0xFFD4C8FC, 0xFFFCC4FC, 0xFFFCC4D8, 0xFFFCBCB0, 0xFFFCD8A8,
        0xFFFCE4A0, 0xFFE0FCA0, 0xFFA8F078, 0xFFA8F0C0, 0xFF00FCFC, 0xFFF8D8F8, 0xFF000000, 0xFF000000
    ];

    public static ReadOnlySpan<uint> Argb32 => BaseArgb32;

    public static uint GetArgb32(byte paletteIndex, byte mask)
    {
        var color = BaseArgb32[paletteIndex & 0x3F];
        var emphasis = (byte)(mask & 0xE0);
        return emphasis == 0 ? color : ApplyEmphasis(color, emphasis);
    }

    private static uint ApplyEmphasis(uint color, byte emphasis)
    {
        var alpha = (byte)(color >> 24);
        var red = (byte)(color >> 16);
        var green = (byte)(color >> 8);
        var blue = (byte)color;

        var redFactor = 1.0f;
        var greenFactor = 1.0f;
        var blueFactor = 1.0f;

        // PPUMASK colour emphasis is implemented here as channel de-emphasis.
        if ((emphasis & 0x20) != 0)
        {
            greenFactor *= EmphasisDeemphasisFactor;
            blueFactor *= EmphasisDeemphasisFactor;
        }

        if ((emphasis & 0x40) != 0)
        {
            redFactor *= EmphasisDeemphasisFactor;
            blueFactor *= EmphasisDeemphasisFactor;
        }

        if ((emphasis & 0x80) != 0)
        {
            redFactor *= EmphasisDeemphasisFactor;
            greenFactor *= EmphasisDeemphasisFactor;
        }

        return ((uint)alpha << 24)
            | ((uint)ScaleChannel(red, redFactor) << 16)
            | ((uint)ScaleChannel(green, greenFactor) << 8)
            | ScaleChannel(blue, blueFactor);
    }

    private static byte ScaleChannel(byte value, float factor)
    {
        return (byte)Math.Clamp((int)MathF.Round(value * factor), 0, 255);
    }
}
