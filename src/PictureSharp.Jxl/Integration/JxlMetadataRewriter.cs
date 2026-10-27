using PictureSharp.Formats.Jxl.Codestream.Encoding;
using PictureSharp.Formats.Jxl.Codestream.Frames;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Integration;

/// <summary>
/// EditMetadata: a copy of the file with changed metadata and the frames untouched. A changed ICC profile, colour
/// description or orientation rewrites the codestream headers; changed EXIF or XMP replaces the Exif or xml boxes
/// (compressed again when they were); every other box keeps its bytes and place.
/// </summary>
internal static class JxlMetadataRewriter
{
    public static byte[] Rewrite(ReadOnlyMemory<byte> data, ImageMetadata original, ImageMetadata metadata, ImageDecodeContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        if (original.HorizontalResolution != metadata.HorizontalResolution || original.VerticalResolution != metadata.VerticalResolution)
        {
            throw new NotSupportedException("JPEG XL files do not store a resolution.");
        }

        JxlContainer container = JxlContainer.Parse(data, includeMetadata: false, cancellationToken);
        List<JxlBoxEntry> boxes = container.IsBoxed ? JxlContainer.Boxes(data.Span) : [];
        if (boxes.Any(box => box.Type == JxlBoxType.JpegReconstruction))
        {
            throw new NotSupportedException("Editing the metadata of a recompressed JPEG would invalidate its JPEG reconstruction data.");
        }

        var changes = MetadataChanges.Between(original, metadata);
        byte[]? codestream = RewriteCodestream(container.Codestream, metadata, changes.Color, cancellationToken);
        byte[]? exif = changes.Exif ? metadata.Exif : null;
        byte[]? xmp = changes.Xmp ? metadata.Xmp : null;
        if (!container.IsBoxed)
        {
            byte[] stream = codestream ?? data.ToArray();
            JxlBox[] added = JxlMetadataWriter.DescriptiveBoxes(exif, xmp, compress: false);
            return added.Length == 0 ? stream : JxlContainerWriter.Write(stream, added);
        }

        var pending = new PendingBoxes(changes, exif, xmp, codestream, boxes);
        return RewriteBoxes(data.Span, boxes, pending);
    }

    // The new codestream when the colour or the orientation changes, else null.
    private static byte[]? RewriteCodestream(JxlCodestream codestream, ImageMetadata metadata, bool colorChanged, CancellationToken cancellationToken)
    {
        JxlHeaders headers = JxlHeadersReader.Read(codestream);
        uint orientation = JxlMetadataWriter.Orientation(metadata);
        if (!colorChanged && orientation == headers.Metadata.Orientation)
        {
            return null;
        }

        // The frames start at the byte after the ICC stream (or the headers), as the decoder finds them.
        long frameStart = JxlFrameSectionReader.ReadIcc(codestream, headers, cancellationToken, out byte[]? icc).BitPosition / 8;
        JxlColorEncoding color = headers.Metadata.Color;
        if (colorChanged)
        {
            icc = metadata.IccProfile;
            color = RewrittenColor(color, metadata);
        }

        return JxlCodestreamWriter.Rewrite(codestream, headers, headers.Metadata with { Color = color, Orientation = orientation },
            color.WantIcc ? icc : null, frameStart);
    }

    // The colour encoding of the new ICC profile or colour description; gray stays gray and colour stays colour.
    private static JxlColorEncoding RewrittenColor(JxlColorEncoding current, ImageMetadata metadata)
    {
        bool gray = current.ColorSpace == JxlColorSpace.Gray;
        if (metadata.IccProfile is not { } icc)
        {
            return JxlMetadataWriter.Encoding(metadata, gray);
        }

        JxlColorSpace space = JxlMetadataWriter.RequireIccColorSpace(icc, "JPEG XL images can only carry RGB or gray ICC profiles.");
        if (space == JxlColorSpace.Gray != gray)
        {
            throw new NotSupportedException("An ICC profile cannot change a JPEG XL image between gray and colour.");
        }

        return new JxlColorEncoding { WantIcc = true, ColorSpace = current.ColorSpace == JxlColorSpace.Xyb ? JxlColorSpace.Rgb : space };
    }

    private static byte[] RewriteBoxes(ReadOnlySpan<byte> data, List<JxlBoxEntry> boxes, PendingBoxes pending)
    {
        using var output = new MemoryStream();
        long end = 0;
        foreach (JxlBoxEntry box in boxes)
        {
            end = box.Header.End;
            if (pending.Replaces(box))
            {
                pending.WriteReplacement(output, box);
                continue;
            }

            if (box.IsCodestream) pending.WriteAdded(output);
            output.Write(data[(int)box.Header.Start..(int)end]);
        }

        // Boxes after a second signature box are kept as they were.
        output.Write(data[(int)end..]);
        return output.ToArray();
    }

    private static bool Same(byte[]? a, byte[]? b) => (a is null) == (b is null) && (a is null || a.AsSpan().SequenceEqual(b));

    private readonly record struct MetadataChanges(bool Color, bool Exif, bool Xmp)
    {
        public static MetadataChanges Between(ImageMetadata original, ImageMetadata metadata) => new(
            Color: !Same(original.IccProfile, metadata.IccProfile) || original.Gamma != metadata.Gamma
                || original.Chromaticities != metadata.Chromaticities || original.SrgbRenderingIntent != metadata.SrgbRenderingIntent,
            Exif: !Same(original.Exif, metadata.Exif),
            Xmp: !Same(original.Xmp, metadata.Xmp));
    }

    /// <summary>
    /// The boxes that still have to be written. A replacement takes the place of the first box of its kind and keeps
    /// its compression; the rest of that kind is dropped. Exif and xml boxes that replace none go before the
    /// codestream, uncompressed, where the encoder puts them.
    /// </summary>
    private sealed class PendingBoxes(MetadataChanges changes, byte[]? exif, byte[]? xmp, byte[]? codestream, List<JxlBoxEntry> boxes)
    {
        private readonly bool exifCompressed = boxes.Any(box => box.BrotliType == JxlBoxType.Exif);
        private readonly bool xmpCompressed = boxes.Any(box => box.BrotliType == JxlBoxType.Xmp);
        private byte[]? exif = exif;
        private byte[]? xmp = xmp;
        private byte[]? codestream = codestream;

        // Changed metadata and a rewritten codestream replace every box of their kind.
        private readonly bool replaceCodestream = codestream is not null;

        public bool Replaces(JxlBoxEntry box) => box.Kind switch
        {
            JxlBoxType.Exif => changes.Exif,
            JxlBoxType.Xmp => changes.Xmp,
            _ => box.IsCodestream && replaceCodestream,
        };

        public void WriteReplacement(Stream output, JxlBoxEntry box)
        {
            if (box.Kind == JxlBoxType.Exif && exif is not null)
            {
                JxlContainerWriter.WriteBox(output, JxlMetadataWriter.ExifBox(exif, exifCompressed));
                exif = null;
            }
            else if (box.Kind == JxlBoxType.Xmp && xmp is not null)
            {
                JxlContainerWriter.WriteBox(output, JxlMetadataWriter.XmpBox(xmp, xmpCompressed));
                xmp = null;
            }
            else if (box.IsCodestream && codestream is not null)
            {
                WriteAdded(output);
                JxlContainerWriter.WriteBox(output, new JxlBox(JxlBoxType.Codestream, codestream));
                codestream = null;
            }
        }

        public void WriteAdded(Stream output)
        {
            foreach (JxlBox box in JxlMetadataWriter.DescriptiveBoxes(exif, xmp, compress: false))
            {
                JxlContainerWriter.WriteBox(output, box);
            }

            (exif, xmp) = (null, null);
        }
    }
}
