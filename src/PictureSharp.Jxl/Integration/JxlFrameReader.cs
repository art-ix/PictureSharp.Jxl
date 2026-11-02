using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.Frames;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Integration;

/// <summary>
/// Frame-by-frame reading for <c>Image.OpenFrameReaderAsync</c> (0.8.13, docs/design/0.8.13-frame-reader.md): the
/// input is buffered (the core counts every byte against MaxInputBytes) and each call decodes one displayed frame.
/// </summary>
internal sealed class JxlFrameReader : IImageFormatFrameDecoder
{
    public static readonly JxlFrameReader Instance = new();

    public async ValueTask<ImageFrameSource> OpenAsync(Stream input, ImageFrameDecodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return JxlFrameSource.Open(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), context, cancellationToken);
    }
}

/// <summary>
/// One open sequence: the resumable decode session with its reference slots, budget and decode-wide buffers, and one
/// output frame overwritten by every call. Frames, durations, loop count and metadata are those of <c>Image.Load</c>.
/// </summary>
internal sealed class JxlFrameSource : ImageFrameSource
{
    private readonly DecoderOptions options;
    private readonly JxlDecodeSession session;
    private readonly IEnumerator<(JxlFrameHeader Frame, JxlDecodedImage Image)> frames;
    private readonly JxlWorkBudget budget;
    private readonly JxlBufferScope buffers;
    private readonly JxlColorEncoding encoding;
    private readonly JxlAnimationHeader? animation;
    private ImageFrame? output;
    private int returned;
    private bool done;
    private bool disposed;

    private JxlFrameSource(ImageFrameSequenceInfo info, DecoderOptions options, JxlDecodeSession session, JxlWorkBudget budget,
        JxlBufferScope buffers, JxlColorEncoding encoding, JxlAnimationHeader? animation) : base(info)
    {
        this.options = options;
        this.session = session;
        this.budget = budget;
        this.buffers = buffers;
        this.encoding = encoding;
        this.animation = animation;
        frames = session.Frames(animation is not null).GetEnumerator();
    }

    /// <summary>
    /// Reads the headers, the ICC profile and every frame header (FrameCount, MaxFrames) and fills the reader's metadata
    /// as <c>Image.Load</c> fills the image's.
    /// </summary>
    public static JxlFrameSource Open(ReadOnlyMemory<byte> data, ImageFrameDecodeContext context, CancellationToken cancellationToken)
    {
        DecoderOptions options = context.Options;
        JxlContainer container = JxlContainer.Parse(data, includeMetadata: !options.IgnoreMetadata, cancellationToken);
        JxlCodestream codestream = container.Codestream;
        JxlHeaders headers = JxlHeadersReader.Read(codestream);
        if (headers.Size.Width > int.MaxValue || headers.Size.Height > int.MaxValue)
            throw JxlDescriptor.Unsupported("image dimensions exceed the largest supported size.");

        int width = (int)headers.Size.Width, height = (int)headers.Size.Height;
        JxlMetadataReader.EnsureConvertible(headers.Metadata.Color, options);
        PixelType pixelType = JxlDecoder.PixelTypeFor(headers, options);
        // One canvas against MaxPixels, as the core checks after opening; checked here before the ICC and the frame scan.
        if (width > options.MaxPixels / height / (pixelType == PixelType.Rgba64 ? 2 : 1))
        {
            throw new ImageFormatException(JxlDescriptor.Format, pixelType == PixelType.Rgba64
                ? $"Image dimensions {width}x{height} at 16 bits per channel exceed the limit of {options.MaxPixels} pixels (DecoderOptions.MaxPixels; 16-bit pixels count twice)."
                : $"Image dimensions {width}x{height} exceed the limit of {options.MaxPixels} pixels (DecoderOptions.MaxPixels).");
        }

        // Frame readers never render to sRGB or apply the orientation: the core rejects ColorConversion and
        // ApplyExifOrientation before opening.
        JxlWorkBudget budget = JxlWorkBudget.Create(options.MaxPixels);
        var buffers = new JxlBufferScope();
        try
        {
            using JxlWorkBudget.Scope scope = budget.Enter();
            JxlDecodeSession session = options.IgnoreMetadata
                ? new JxlDecodeSession(codestream, headers, cancellationToken, buffers: buffers, parallelism: options.MaxDegreeOfParallelism,
                    maxFrames: options.MaxFrames)
                : new JxlDecodeSession(codestream, headers, cancellationToken, options.MaxMetadataBytes, context.ReserveMetadata,
                    buffers: buffers, parallelism: options.MaxDegreeOfParallelism, maxFrames: options.MaxFrames);
            session.Open();
            JxlAnimationHeader? animation = headers.Metadata.Animation;
            List<JxlFrameHeader> all = JxlDecodeSession.ReadFrameHeaders(codestream, headers, cancellationToken, options.MaxFrames);
            List<JxlFrameHeader> shown = animation is null ? [all[^1]] : [.. all.Where(JxlDecodeSession.IsDisplayed)];
            if (shown.Count == 0) throw JxlDescriptor.Corrupt("JPEG XL animation has no displayed frame.");

            // Every displayed frame is rendered in the encoding of the first (Image.Load reports the first frame's).
            bool gray = headers.Metadata.Color.ColorSpace == JxlColorSpace.Gray;
            JxlColorEncoding encoding = JxlColorOutput.DescribeOutput(headers.Metadata, shown[0].ColorTransform,
                JxlColorTarget.Profile(session.IccProfile, gray));
            ImageMetadata metadata = context.Metadata;
            if (!options.IgnoreMetadata)
            {
                JxlMetadataReader.ApplyColour(metadata, encoding, session.IccProfile, options);
                JxlMetadataReader.ApplyDescriptive(metadata, container.MetadataBoxes, options.MaxMetadataBytes, context.ReserveMetadata,
                    cancellationToken);
            }

            JxlMetadataReader.ApplyOrientation(metadata, headers.Metadata.Orientation);
            // Plays, 0 = forever; one frame (also with FirstFrameOnly) is no animation, as after Image.Load.
            int loops = animation is not null && shown.Count > 1 && !options.FirstFrameOnly ? (int)Math.Min(animation.Loops, int.MaxValue) : 0;
            var info = new ImageFrameSequenceInfo(width, height, pixelType) { FrameCount = shown.Count, LoopCount = loops };
            return new JxlFrameSource(info, options, session, budget, buffers, encoding, animation);
        }
        catch
        {
            buffers.Dispose();
            throw;
        }
    }

    public override ValueTask<ImageFrame?> ReadNextAsync(CancellationToken cancellationToken) => ValueTask.FromResult(ReadNext(cancellationToken));

    // Synchronous: the data is in memory, so the budget is entered and left on this thread and flows into the workers.
    private ImageFrame? ReadNext(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (done) return null;
        using JxlWorkBudget.Scope scope = budget.Enter();
        session.CancellationToken = cancellationToken;
        if (returned > 0 && options.FirstFrameOnly)
        {
            // The rest is not decoded, but its headers are read: MaxFrames and corrupt headers, as in Image.Load.
            done = true;
            session.SkipRemaining();
            return null;
        }

        if (!frames.MoveNext())
        {
            done = true;
            return null;
        }

        (JxlFrameHeader frame, JxlDecodedImage decoded) = frames.Current;
        if (decoded.ColorEncoding != encoding)
        {
            throw new InvalidOperationException("Animation frames were rendered in different colour encodings.");
        }

        output ??= new ImageFrame(Info.Width, Info.Height, Info.PixelType);
        JxlPixelWriter.WriteInto(decoded, output, cancellationToken, options.MaxDegreeOfParallelism);
        output.Duration = animation is null ? TimeSpan.Zero : JxlDecoder.Duration(frame.Duration, animation);
        returned++;
        return output;
    }

    /// <summary>Returns the buffers of the current frame and the decode-wide slots, at any point of the sequence.</summary>
    public override ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            try
            {
                frames.Dispose();
            }
            finally
            {
                buffers.Dispose();
#if JXL_FUZZ_CHECKED
                budget.Report();
#endif
            }
        }

        return base.DisposeAsync();
    }
}
