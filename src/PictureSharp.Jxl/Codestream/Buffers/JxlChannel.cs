namespace PictureSharp.Formats.Jxl.Codestream.Buffers;

/// <summary>A plane of 32-bit samples; its storage is allocated on first access, after transforms fixed its size.</summary>
internal sealed class JxlChannel
{
    private int[]? pixels;

    public JxlChannel(int width, int height, int hShift = 0, int vShift = 0, JxlBufferScope? buffers = null)
    {
        if (width < 0 || height < 0) throw JxlDescriptor.Corrupt("Invalid channel dimensions.");
        Buffers = buffers;
        Width = width;
        Height = height;
        HShift = hShift;
        VShift = vShift;
    }

    internal JxlBufferScope? Buffers { get; }
    public JxlChannel(int width, int height, int[] samples) : this(width, height)
    {
        if ((long)width * height > samples.Length) throw new ArgumentException("Insufficient channel samples.", nameof(samples));
        pixels = samples;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>log2 of the horizontal downsampling; -1 for meta channels such as a palette.</summary>
    public int HShift { get; set; }

    public int VShift { get; set; }

    public bool IsEmpty => Width == 0 || Height == 0;

    public int[] Pixels => pixels ??= (long)Width * Height <= Array.MaxLength
        ? Buffers is null ? new int[Width * Height] : Buffers.Rent<int>(Width * Height)
        : throw JxlDescriptor.Unsupported($"a {Width}x{Height} channel is too large.");

    public Span<int> Row(int y) => Pixels.AsSpan(y * Width, Width);

    /// <summary>Changes the size and drops the samples, as MetaApply does before anything is decoded.</summary>
    public void Resize(int width, int height)
    {
        if (width < 0 || height < 0) throw JxlDescriptor.Corrupt("Invalid channel dimensions.");
        Width = width;
        Height = height;
        pixels = null;
    }

    public bool SameShape(JxlChannel other) =>
        Width == other.Width && Height == other.Height && HShift == other.HShift && VShift == other.VShift;
}
