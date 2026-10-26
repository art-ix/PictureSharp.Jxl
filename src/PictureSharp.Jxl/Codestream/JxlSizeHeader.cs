using static PictureSharp.Formats.Jxl.Codestream.JxlU32;

namespace PictureSharp.Formats.Jxl.Codestream;

// Size, preview and animation headers. Ported from libjxl v0.12.0 lib/jxl/headers.cc (BSD-3-Clause).
internal static class JxlAspectRatio
{
    private static ReadOnlySpan<byte> Numerators => [1, 12, 4, 3, 16, 5, 2];
    private static ReadOnlySpan<byte> Denominators => [1, 10, 3, 2, 9, 4, 1];

    /// <summary>The width for a non-zero ratio code: <c>height × ratio</c>, truncated.</summary>
    public static uint Width(uint ratio, uint height) =>
        (uint)((ulong)height * Numerators[(int)ratio - 1] / Denominators[(int)ratio - 1]);
}

/// <summary>The image dimensions. Heights are 1..2^30; widths derived from an aspect ratio reach 2^31.</summary>
internal sealed record JxlSizeHeader(uint Width, uint Height)
{
    public static JxlSizeHeader Read(ref JxlBitReader reader)
    {
        bool small = reader.ReadBool();
        uint height = small ? (reader.ReadBits(5) + 1) * 8 : Dimension(ref reader);
        uint ratio = reader.ReadBits(3);
        uint width = ratio != 0 ? JxlAspectRatio.Width(ratio, height) : small ? (reader.ReadBits(5) + 1) * 8 : Dimension(ref reader);
        return new JxlSizeHeader(width, height);
    }

    private static uint Dimension(ref JxlBitReader reader) =>
        JxlFields.U32(ref reader, BitsOffset(9, 1), BitsOffset(13, 1), BitsOffset(18, 1), BitsOffset(30, 1));
}

/// <summary>The dimensions of the preview frame.</summary>
internal sealed record JxlPreviewHeader(uint Width, uint Height)
{
    public static JxlPreviewHeader Read(ref JxlBitReader reader)
    {
        bool div8 = reader.ReadBool();
        uint height = Dimension(ref reader, div8);
        uint ratio = reader.ReadBits(3);
        uint width = ratio != 0 ? JxlAspectRatio.Width(ratio, height) : Dimension(ref reader, div8);
        return new JxlPreviewHeader(width, height);
    }

    private static uint Dimension(ref JxlBitReader reader, bool div8) => div8
        ? JxlFields.U32(ref reader, Val(16), Val(32), BitsOffset(5, 1), BitsOffset(9, 33)) * 8
        : JxlFields.U32(ref reader, BitsOffset(6, 1), BitsOffset(8, 65), BitsOffset(10, 321), BitsOffset(12, 1345));
}

/// <summary>Animation timing: ticks per second as a fraction, loop count (0 = forever) and timecode presence.</summary>
internal sealed record JxlAnimationHeader(uint TicksNumerator, uint TicksDenominator, uint Loops, bool HaveTimecodes)
{
    public static JxlAnimationHeader Read(ref JxlBitReader reader) => new(
        JxlFields.U32(ref reader, Val(100), Val(1000), BitsOffset(10, 1), BitsOffset(30, 1)),
        JxlFields.U32(ref reader, Val(1), Val(1001), BitsOffset(8, 1), BitsOffset(10, 1)),
        JxlFields.U32(ref reader, Val(0), Bits32(3), Bits32(16), Bits32(32)),
        reader.ReadBool());
}
