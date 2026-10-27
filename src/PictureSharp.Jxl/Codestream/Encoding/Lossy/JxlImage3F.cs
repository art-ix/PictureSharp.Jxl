namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>Three float planes of the same size without margins, row by row (Image3F in libjxl).</summary>
internal sealed class JxlImage3F
{
    public JxlImage3F(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > Array.MaxLength)
        {
            throw JxlDescriptor.Unsupported($"a {width}x{height} image is too large to encode.");
        }

        Width = width;
        Height = height;
        Planes = [new float[width * height], new float[width * height], new float[width * height]];
    }

    public int Width { get; }

    public int Height { get; }

    public float[][] Planes { get; }

    public Span<float> Row(int c, int y) => Planes[c].AsSpan(y * Width, Width);

    public JxlImage3F Clone()
    {
        var copy = new JxlImage3F(Width, Height);
        for (int c = 0; c < 3; c++) Planes[c].CopyTo(copy.Planes[c], 0);
        return copy;
    }

    /// <summary>
    /// A copy of the top-left <paramref name="width"/>×<paramref name="height"/> pixels extended to whole 8×8 blocks
    /// by repeating the last column and row (PadImageToBlockMultipleInPlace).
    /// </summary>
    public JxlImage3F PaddedToBlocks(int width, int height)
    {
        var padded = new JxlImage3F((width + 7) & ~7, (height + 7) & ~7);
        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < padded.Height; y++)
            {
                Span<float> target = padded.Row(c, y);
                Row(c, Math.Min(y, height - 1))[..width].CopyTo(target);
                target[width..].Fill(target[width - 1]);
            }
        }

        return padded;
    }
}
