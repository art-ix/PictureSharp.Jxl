namespace PictureSharp.Formats.Jxl.Codestream.Model;

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
internal readonly record struct JxlSizeHeader
{
    public JxlSizeHeader(uint width, uint height)
    {
        Width = width;
        Height = height;
        Validate();
    }

    public uint Width { get; }
    public uint Height { get; }

    public void Validate()
    {
        if (Width is 0 or > (1u << 31) || Height is 0 or > (1u << 30))
            throw JxlDescriptor.Corrupt("Invalid image dimensions.");
    }
}

/// <summary>The dimensions of the preview frame.</summary>
internal sealed record JxlPreviewHeader(uint Width, uint Height);

/// <summary>Animation timing: ticks per second as a fraction, loop count (0 = forever) and timecode presence.</summary>
internal sealed record JxlAnimationHeader(uint TicksNumerator, uint TicksDenominator, uint Loops, bool HaveTimecodes);
