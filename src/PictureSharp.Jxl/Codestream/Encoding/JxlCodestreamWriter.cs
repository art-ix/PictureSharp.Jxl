using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Metadata;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>A still image to encode losslessly: integer colour channels (gray or RGB), optional straight alpha.</summary>
internal sealed record JxlEncodeImage(int Width, int Height, int[][] Color, int[]? Alpha, JxlColorEncoding ColorEncoding, byte[]? Icc,
    uint Orientation, int BitDepth = 8, int Parallelism = 1, int Effort = JxlEncoderOptions.DefaultEffort);

/// <summary>
/// A still image to encode lossily: its XYB pixels (gray images with equal channels before conversion), optional
/// straight integer alpha, and the colour encoding the XYB pixels are rendered to.
/// </summary>
internal sealed record JxlLossyEncodeImage(int Width, int Height, JxlImage3F Xyb, int[]? Alpha, bool Gray, JxlColorEncoding ColorEncoding,
    byte[]? Icc, uint Orientation, JxlLossySettings Settings, int BitDepth = 8);

/// <summary>Writes a complete codestream: headers, the ICC profile and one lossless Modular or lossy VarDCT frame.</summary>
internal static class JxlCodestreamWriter
{
    public static byte[] Write(JxlEncodeImage image, CancellationToken cancellationToken = default)
    {
        var writer = new JxlBitWriter();
        WriteTo(image, writer, cancellationToken);
        return writer.ToArray();
    }

    public static void WriteTo(JxlEncodeImage image, JxlBitWriter writer, CancellationToken cancellationToken = default)
    {
        if (image.Color.Length is not (1 or 3)) throw new ArgumentException("Expected one or three colour channels.", nameof(image));
        bool gray = image.Color.Length == 1;
        JxlColorEncoding color = image.Icc is not null
            ? new JxlColorEncoding { WantIcc = true, ColorSpace = gray ? JxlColorSpace.Gray : JxlColorSpace.Rgb }
            : image.ColorEncoding;
        if ((color.ColorSpace == JxlColorSpace.Gray) != gray)
        {
            throw new ArgumentException("The colour encoding does not match the number of colour channels.", nameof(image));
        }

        var metadata = new JxlImageMetadata
        {
            Orientation = image.Orientation,
            Modular16BitBufferSufficient = image.BitDepth <= 12,
            BitDepth = new JxlBitDepth(false, (uint)image.BitDepth, 0),
            ExtraChannels = image.Alpha is null ? [] :
                [image.BitDepth == 8 ? JxlExtraChannelInfo.Default : JxlExtraChannelInfo.Default with { BitDepth = new JxlBitDepth(false, (uint)image.BitDepth, 0) }],
            XybEncoded = false,
            Color = color,
        };

        JxlHeaderWriter.Signature(writer);
        JxlHeaderWriter.Size(writer, new JxlSizeHeader((uint)image.Width, (uint)image.Height));
        JxlHeaderWriter.ImageMetadata(writer, metadata);
        JxlHeaderWriter.DefaultTransformData(writer);
        if (image.Icc is not null) Icc(writer, image.Icc);
        writer.ZeroPadToByte();

        var channels = new List<JxlChannel>();
        foreach (int[] plane in image.Alpha is null ? image.Color : [.. image.Color, image.Alpha])
        {
            // Encoder transforms replace channels; they do not mutate these input samples.
            var channel = new JxlChannel(image.Width, image.Height, plane);
            channels.Add(channel);
        }

        JxlModularEncoder.WriteFrame(writer, channels, image.Color.Length, cancellationToken, parallelism: image.Parallelism,
            effort: JxlModularEffort.For(image.Effort), bitDepth: image.BitDepth);
    }

    /// <summary>Writes the headers and one VarDCT frame of XYB-encoded <paramref name="image"/>.</summary>
    public static byte[] WriteLossy(JxlLossyEncodeImage image, CancellationToken cancellationToken = default)
    {
        var writer = new JxlBitWriter();
        WriteLossyTo(image, writer, cancellationToken);
        return writer.ToArray();
    }

    public static void WriteLossyTo(JxlLossyEncodeImage image, JxlBitWriter writer, CancellationToken cancellationToken = default)
    {
        JxlColorEncoding color = image.Icc is not null
            ? new JxlColorEncoding { WantIcc = true, ColorSpace = image.Gray ? JxlColorSpace.Gray : JxlColorSpace.Rgb }
            : image.ColorEncoding;
        if ((color.ColorSpace == JxlColorSpace.Gray) != image.Gray)
        {
            throw new ArgumentException("The colour encoding does not match the image.", nameof(image));
        }

        var metadata = new JxlImageMetadata
        {
            Orientation = image.Orientation,
            Modular16BitBufferSufficient = image.BitDepth <= 12,
            BitDepth = new JxlBitDepth(false, (uint)image.BitDepth, 0),
            ExtraChannels = image.Alpha is null ? [] :
                [image.BitDepth == 8 ? JxlExtraChannelInfo.Default : JxlExtraChannelInfo.Default with { BitDepth = new JxlBitDepth(false, (uint)image.BitDepth, 0) }],
            XybEncoded = true,
            Color = color,
        };

        JxlHeaderWriter.Signature(writer);
        JxlHeaderWriter.Size(writer, new JxlSizeHeader((uint)image.Width, (uint)image.Height));
        JxlHeaderWriter.ImageMetadata(writer, metadata);
        JxlHeaderWriter.DefaultTransformData(writer);
        if (image.Icc is not null) Icc(writer, image.Icc);
        writer.ZeroPadToByte();
        JxlVarDctEncoder.WriteFrame(writer, image.Xyb, image.Width, image.Height, image.Alpha, metadata, image.Settings, cancellationToken);
    }

    /// <summary>
    /// <paramref name="codestream"/> with new image metadata and ICC profile: the SizeHeader, CustomTransformData and every
    /// frame from <paramref name="frameStart"/> (the byte after the original ICC stream) are copied unchanged.
    /// </summary>
    public static byte[] Rewrite(JxlCodestream codestream, JxlHeaders headers, JxlImageMetadata metadata, byte[]? icc, long frameStart)
    {
        if (metadata.Color.WantIcc != icc is not null) throw new ArgumentException("The ICC profile does not match the colour encoding.", nameof(icc));
        var writer = new JxlBitWriter();
        CopyBits(writer, codestream, 0, headers.MetadataBits.Metadata);
        JxlHeaderWriter.ImageMetadata(writer, metadata);
        CopyBits(writer, codestream, headers.MetadataBits.TransformData, headers.MetadataBits.TransformDataEnd);
        if (icc is not null) Icc(writer, icc);
        writer.ZeroPadToByte();
        foreach (ReadOnlyMemory<byte> segment in codestream.Slice(frameStart, codestream.Length - frameStart).Segments)
        {
            writer.WriteBytes(segment.Span);
        }

        return writer.ToArray();
    }

    private static void CopyBits(JxlBitWriter writer, JxlCodestream codestream, long start, long end)
    {
        var reader = new JxlBitReader(codestream);
        reader.SkipBits((ulong)start);
        for (long remaining = end - start; remaining > 0;)
        {
            int count = (int)Math.Min(32, remaining);
            writer.Write(count, reader.ReadBits(count));
            remaining -= count;
        }
    }

    /// <summary>The encoded ICC size and its entropy-coded stream (WriteICC).</summary>
    internal static void Icc(JxlBitWriter writer, byte[] icc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] encoded = JxlIccStream.Predict(icc);
        JxlFieldWriter.U64(writer, (ulong)encoded.Length);
        var tokens = new JxlTokens();
        for (int i = 0; i < encoded.Length; i++)
        {
            if ((i & 0xffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            tokens.Add(JxlIccStream.Context(encoded, i), encoded[i]);
        }
        JxlEntropyEncoder.WriteStandalone(writer, JxlIccStream.Contexts, tokens, cancellationToken);
    }
}
