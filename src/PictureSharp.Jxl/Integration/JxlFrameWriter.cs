namespace PictureSharp.Formats.Jxl.Integration;

/// <summary>
/// Frame-by-frame writing for <c>Image.CreateFrameWriter</c> (0.8.14, docs/design/0.8.14-animation-encode.md). The
/// output is written forward only; a declared single frame goes through <see cref="JxlEncoder"/> instead.
/// </summary>
internal sealed class JxlFrameWriter : IImageFormatFrameEncoder
{
    public static readonly JxlFrameWriter Instance = new();

    public ImageFrameSink Open(Stream output, ImageFrameSequenceInfo info, EncoderOptions options, ImageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(metadata);
        if (options is not JxlEncoderOptions jxlOptions)
        {
            throw new ArgumentException($"Expected {nameof(JxlEncoderOptions)}.", nameof(options));
        }

        if (!output.CanWrite) throw new ArgumentException("The stream must be writable.", nameof(output));
        var sink = new JxlFrameSink(output, info, jxlOptions, metadata);
        // Validates the options and the metadata now: nothing is written before the first frame.
        sink.Create(PixelType.Rgba32).Dispose();
        return sink;
    }
}

/// <summary>
/// One sequence being written; the encoder runs synchronously inside each call. The sequence info carries no pixel
/// type, so the bit depth is that of the first frame (8 or 16 bits). Later frames are not known, so the frames are RGB
/// (gray only with a gray ICC profile) and keep an alpha channel.
/// </summary>
internal sealed class JxlFrameSink(Stream output, ImageFrameSequenceInfo info, JxlEncoderOptions options, ImageMetadata metadata) : ImageFrameSink(info)
{
    private JxlEncoder.AnimationWriter? writer;
    private PixelType? pixelType;
    private ImageFrame? widened;
    private bool disposed;

    /// <summary>Rgba32 or Rgba64 until the first frame, then the first frame's type (16-bit sequences take 8-bit frames too).</summary>
    public override bool SupportsPixelType(PixelType type) => pixelType is null ? type is PixelType.Rgba32 or PixelType.Rgba64
        : type == pixelType || type == PixelType.Rgba32;

    internal JxlEncoder.AnimationWriter Create(PixelType type) =>
        new(output, Info.Width, Info.Height, type, Info.LoopCount, metadata, options, grayPixels: null, alpha: true);

    public override ValueTask WriteAsync(ImageFrame frame, TimeSpan duration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        pixelType ??= frame.PixelType;
        writer ??= Create(pixelType.Value);
        if (frame.PixelType == pixelType)
        {
            writer.Write(frame, duration, cancellationToken);
        }
        else
        {
            // An 8-bit frame of a 16-bit sequence, widened exactly (v * 257).
            widened ??= new ImageFrame(Info.Width, Info.Height, PixelType.Rgba64);
            ReadOnlySpan<Rgba32> source = frame.GetPixelSpan<Rgba32>();
            Span<Rgba64> target = widened.GetPixelSpan<Rgba64>();
            for (int i = 0; i < source.Length; i++)
            {
                Rgba32 p = source[i];
                target[i] = new Rgba64((ushort)(p.R * 257), (ushort)(p.G * 257), (ushort)(p.B * 257), (ushort)(p.A * 257));
            }

            writer.Write(widened, duration, cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    public override ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        (writer ?? throw new InvalidOperationException("An animation needs at least one frame.")).Complete(cancellationToken);
        return ValueTask.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            writer?.Dispose();
            widened = null;
        }

        return base.DisposeAsync();
    }
}
