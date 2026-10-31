namespace PictureSharp.Formats.Jxl.Codestream.Buffers;

/// <summary>
/// A float plane with a margin on every side, so stages can read mirrored pixels around the logical size. The
/// storage may be larger than the logical size: VarDCT writes whole blocks.
/// </summary>
internal sealed class JxlPlane
{
    public const int Margin = 4;

    public JxlPlane(int width, int height, int capacityWidth = 0, int capacityHeight = 0, JxlBufferScope? buffers = null)
    {
        if (width <= 0 || height <= 0 || capacityWidth < 0 || capacityHeight < 0)
            throw JxlDescriptor.Corrupt("Invalid plane dimensions.");
        long stride = Math.Max(width, capacityWidth) + 2L * Margin;
        long rows = Math.Max(height, capacityHeight) + 2L * Margin;
        if (stride > Array.MaxLength || rows > Array.MaxLength || stride * rows > Array.MaxLength)
            throw JxlDescriptor.Unsupported($"a {width}x{height} plane is too large.");
        Width = width;
        Height = height;
        Stride = (int)stride;
        if (buffers is null) JxlWorkBudget.Charge(stride * rows * sizeof(float));
        Data = buffers is null ? new float[stride * rows] : buffers.Rent<float>((int)(stride * rows));
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public float[] Data { get; }

    public int Offset(int x, int y) => (y + Margin) * Stride + x + Margin;

    public Span<float> Row(int y) => Data.AsSpan(Offset(0, y), Width);

    /// <summary>Copies the logical pixels row by row into <paramref name="target"/>, which may have another layout.</summary>
    public void CopyTo(JxlPlane target, CancellationToken cancellationToken = default)
    {
        for (int y = 0; y < Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Row(y).CopyTo(target.Row(y));
        }
    }

    /// <summary>A copy of the logical pixels without the spare capacity.</summary>
    public JxlPlane Clone(CancellationToken cancellationToken = default)
    {
        var copy = new JxlPlane(Width, Height);
        CopyTo(copy, cancellationToken);
        return copy;
    }

    /// <summary>Fills <paramref name="borderX"/> columns and <paramref name="borderY"/> rows around the plane by mirroring.</summary>
    public void Mirror(int borderX, int borderY, CancellationToken cancellationToken = default)
    {
        for (int y = 0; y < Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int row = Offset(0, y);
            for (int i = 0; i < borderX; i++)
            {
                Data[row - i - 1] = Data[row + MirrorIndex(-i - 1, Width)];
                Data[row + Width + i] = Data[row + MirrorIndex(Width + i, Width)];
            }
        }

        int length = Width + 2 * borderX;
        for (int y = 0; y < borderY; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Data.AsSpan(Offset(-borderX, MirrorIndex(-y - 1, Height)), length).CopyTo(Data.AsSpan(Offset(-borderX, -y - 1)));
            Data.AsSpan(Offset(-borderX, MirrorIndex(Height + y, Height)), length).CopyTo(Data.AsSpan(Offset(-borderX, Height + y)));
        }
    }

    private static int MirrorIndex(int x, int size)
    {
        while (x < 0 || x >= size)
        {
            x = x < 0 ? -x - 1 : 2 * size - 1 - x;
        }

        return x;
    }
}
