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

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) =>
        JxlImageInfoMapper.Map(JxlHeadersReader.Read(JxlContainer.Parse(data).Codestream), options);

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
        PixelType pixelType = options.PreserveBitDepth && (headers.Metadata.BitDepth.BitsPerSample > 8
            || headers.Metadata.ExtraChannels.Any(channel => channel.Type == JxlExtraChannelType.Alpha && channel.BitDepth.BitsPerSample > 8))
            ? PixelType.Rgba64 : PixelType.Rgba32;
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
        JxlDecodedImage decoded = session.Decode();
        Image image = Write(decoded, pixelType, flip, context);
        try
        {
            // The last layer on the still-image path is the image itself, before the core post-processes it.
            if (layers is not null && layers.Count < frames!.Count)
            {
                layers.Add(image.Clone(), decoded.ColorEncoding, session.IccProfile, options);
            }

            if (!options.IgnoreMetadata)
            {
                // The profile only when the samples are in it: XYB with an unsupported profile, or with ToSrgb, is
                // rendered as sRGB.
                JxlMetadataReader.ApplyColour(image.Metadata, decoded.ColorEncoding, session.IccProfile, options);
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

    private static Image Write(JxlDecodedImage decoded, PixelType pixelType, JxlPixelWriter.DitherFlip flip, ImageDecodeContext context) =>
        pixelType == PixelType.Rgba64
            ? JxlPixelWriter.WriteWide(decoded, context.CancellationToken, context.Options.MaxDegreeOfParallelism)
            : JxlPixelWriter.Write(decoded, context.CancellationToken, context.Options.MaxDegreeOfParallelism, flip);
}
