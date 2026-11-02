using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Frames;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Integration;

/// <summary>The stateless PictureSharp SPI adapter. Every decode owns its own session.</summary>
internal sealed class JxlDecoder : IImageFormatDecoder
{
    public static readonly JxlDecoder Instance = new();

    /// <summary>
    /// The header's description; for an animation also the number of displayed frames, read from every frame header and
    /// TOC without decoding the frames (MaxFrames applies to all stored frames).
    /// </summary>
    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        JxlCodestream codestream = JxlContainer.Parse(data).Codestream;
        JxlHeaders headers = JxlHeadersReader.Read(codestream);
        ImageInfo info = JxlImageInfoMapper.Map(headers, options);
        if (headers.Metadata.Animation is null) return info;
        int count = JxlDecodeSession.ReadFrameHeaders(codestream, headers, maxFrames: options.MaxFrames).Count(JxlDecodeSession.IsDisplayed);
        return info with { FrameCount = count, IsAnimated = count > 1 };
    }

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context) => Decode(data, context, layers: null);

    /// <summary>
    /// The decoded image; with <paramref name="layers"/> also the layers of a layer stack (JxlLayers), reserved with the
    /// image before decoding and written into the collector, which owns them.
    /// </summary>
    public static Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context, JxlLayerCollector? layers)
    {
        context.ThrowIfCancellationRequested();
        DecoderOptions options = context.Options;
        JxlContainer container = JxlContainer.Parse(data, includeMetadata: !options.IgnoreMetadata, context.CancellationToken);
        JxlCodestream codestream = container.Codestream;
        JxlHeaders headers = JxlHeadersReader.Read(codestream);
        if (headers.Size.Width > int.MaxValue || headers.Size.Height > int.MaxValue)
            throw JxlDescriptor.Unsupported("image dimensions exceed the largest supported size.");

        JxlMetadataReader.EnsureConvertible(headers.Metadata.Color, options);
        PixelType pixelType = PixelTypeFor(headers, options);
        context.ReservePixels((int)headers.Size.Width, (int)headers.Size.Height, pixelType);
        IReadOnlyList<JxlFrameHeader>? frames = null;
        if (layers is not null)
        {
            frames = layers.Select(codestream, headers, options, context.CancellationToken);
            foreach (JxlFrameHeader frame in frames) context.ReservePixels((int)frame.Width, (int)frame.Height, pixelType);
        }

        var flip = options.ApplyExifOrientation ? JxlPixelWriter.DitherFlip.For(headers.Metadata.Orientation) : default;
        // The ICC profile is part of the codestream: with IgnoreMetadata it is decoded (as libjxl does) within libjxl's
        // limit, but neither charged nor reported. ToSrgb renders XYB frames as sRGB in floating point, so they are
        // quantized once and get no colour description for PictureSharp to convert again.
        // Working memory beyond the canvas is charged against MaxPixels too (JxlWorkBudget).
        using JxlWorkBudget.Scope work = JxlWorkBudget.Begin(options.MaxPixels);
        using var buffers = new JxlBufferScope();
        JxlDecodeSession? session = null;
        Action<JxlFrameHeader, JxlDecodedImage>? sink = layers is null ? null
            : (frame, layer) => layers.Add(Write(layer, pixelType, flip, context), layer.ColorEncoding, session!.IccProfile, options);
        session = options.IgnoreMetadata
            ? new JxlDecodeSession(codestream, headers, context.CancellationToken, buffers: buffers,
                parallelism: options.MaxDegreeOfParallelism, maxFrames: options.MaxFrames, layerSink: sink)
            : new JxlDecodeSession(codestream, headers, context.CancellationToken, options.MaxMetadataBytes, context.ReserveMetadata,
                renderSrgb: options.ColorConversion == ColorConversionMode.ToSrgb, buffers: buffers,
                parallelism: options.MaxDegreeOfParallelism, maxFrames: options.MaxFrames, layerSink: sink);
        Image image;
        JxlColorEncoding encoding;
        if (headers.Metadata.Animation is { } animation)
        {
            (image, encoding) = DecodeAnimation(session, animation, (int)headers.Size.Width, (int)headers.Size.Height, pixelType, flip, context);
        }
        else
        {
            JxlDecodedImage decoded = session.Decode();
            image = Write(decoded, pixelType, flip, context);
            encoding = decoded.ColorEncoding;
        }

        try
        {
            // The last layer on the still-image path is the image itself, before the core post-processes it.
            if (layers is not null && layers.Count < frames!.Count)
            {
                layers.Add(image.Clone(), encoding, session.IccProfile, options);
            }

            if (!options.IgnoreMetadata)
            {
                // The profile only when the samples are in it: XYB with an unsupported profile, or with ToSrgb, is
                // rendered as sRGB.
                JxlMetadataReader.ApplyColour(image.Metadata, encoding, session.IccProfile, options);
                JxlMetadataReader.ApplyDescriptive(image.Metadata, container.MetadataBoxes, context);
            }

            // Stored orientation, as for JPEG: ApplyExifOrientation (or AutoOrient) applies it.
            JxlMetadataReader.ApplyOrientation(image.Metadata, headers.Metadata.Orientation);
        }
        catch
        {
            image.Dispose();
            throw;
        }

        return image;
    }

    // The displayed frames in order (docs/design/0.8.12-animation.md): the first becomes the image as a still does, each
    // later one is reserved, written and appended before the next is decoded. All share the first frame's encoding.
    private static (Image Image, JxlColorEncoding Encoding) DecodeAnimation(JxlDecodeSession session, JxlAnimationHeader animation,
        int width, int height, PixelType pixelType, JxlPixelWriter.DitherFlip flip, ImageDecodeContext context)
    {
        DecoderOptions options = context.Options;
        Image? image = null;
        JxlColorEncoding? encoding = null;
        try
        {
            session.DecodeAnimation((frame, decoded) =>
            {
                TimeSpan duration = Duration(frame.Duration, animation);
                if (image is null)
                {
                    image = Write(decoded, pixelType, flip, context);
                    image.Frames.RootFrame.Duration = duration;
                    encoding = decoded.ColorEncoding;
                    return !options.FirstFrameOnly;
                }

                if (decoded.ColorEncoding != encoding)
                {
                    throw new InvalidOperationException("Animation frames were rendered in different colour encodings.");
                }

                context.ReservePixels(width, height, pixelType);
                ImageFrame next = JxlPixelWriter.WriteFrame(decoded, pixelType, context.CancellationToken, options.MaxDegreeOfParallelism, flip);
                next.Duration = duration;
                image.Frames.Add(next);
                return true;
            });

            // Plays, 0 = forever; a single frame is no animation, as for PNG and GIF.
            image!.LoopCount = image.FrameCount > 1 ? (int)Math.Min(animation.Loops, int.MaxValue) : 0;
            return (image, encoding!);
        }
        catch
        {
            image?.Dispose();
            throw;
        }
    }

    /// <summary>Rgba64 for samples or alpha above 8 bits with PreserveBitDepth, otherwise Rgba32.</summary>
    internal static PixelType PixelTypeFor(JxlHeaders headers, DecoderOptions options) =>
        options.PreserveBitDepth && (headers.Metadata.BitDepth.BitsPerSample > 8
            || headers.Metadata.ExtraChannels.Any(channel => channel.Type == JxlExtraChannelType.Alpha && channel.BitDepth.BitsPerSample > 8))
            ? PixelType.Rgba64 : PixelType.Rgba32;

    /// <summary>
    /// <paramref name="ticks"/> × tps_den / tps_num seconds, rounded to the nearest 100 ns and saturated at
    /// <see cref="TimeSpan.MaxValue"/> (both tick fields are at least 1).
    /// </summary>
    internal static TimeSpan Duration(uint ticks, JxlAnimationHeader animation)
    {
        UInt128 scaled = (UInt128)ticks * animation.TicksDenominator * TimeSpan.TicksPerSecond;
        UInt128 rounded = (scaled + (animation.TicksNumerator / 2)) / animation.TicksNumerator;
        return rounded > (UInt128)TimeSpan.MaxValue.Ticks ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)rounded);
    }

    private static Image Write(JxlDecodedImage decoded, PixelType pixelType, JxlPixelWriter.DitherFlip flip, ImageDecodeContext context) =>
        pixelType == PixelType.Rgba64
            ? JxlPixelWriter.WriteWide(decoded, context.CancellationToken, context.Options.MaxDegreeOfParallelism)
            : JxlPixelWriter.Write(decoded, context.CancellationToken, context.Options.MaxDegreeOfParallelism, flip);
}
