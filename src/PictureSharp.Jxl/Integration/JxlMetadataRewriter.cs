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
        var boxes = container.IsBoxed ? JxlContainer.Boxes(data.Span) : [];
        if (boxes.Any(box => box.Type == "jbrd"))
        {
            throw new NotSupportedException("Editing the metadata of a recompressed JPEG would invalidate its JPEG reconstruction data.");
        }

        JxlCodestream codestream = container.Codestream;
        JxlHeaders headers = JxlHeadersReader.Read(codestream);
        bool iccChanged = !Same(original.IccProfile, metadata.IccProfile);
        bool colorChanged = iccChanged || original.Gamma != metadata.Gamma || original.Chromaticities != metadata.Chromaticities
            || original.SrgbRenderingIntent != metadata.SrgbRenderingIntent;
        uint orientation = JxlMetadataWriter.Orientation(metadata);
        bool exifChanged = !Same(original.Exif, metadata.Exif);
        bool xmpChanged = !Same(original.Xmp, metadata.Xmp);

        byte[]? newCodestream = null;
        if (colorChanged || orientation != headers.Metadata.Orientation)
        {
            // The frames start at the byte after the ICC stream (or the headers), as the decoder finds them.
            long frameStart = JxlFrameSectionReader.ReadIcc(codestream, headers, cancellationToken, out byte[]? icc).BitPosition / 8;
            JxlColorEncoding color = headers.Metadata.Color;
            if (colorChanged)
            {
                icc = metadata.IccProfile;
                bool gray = color.ColorSpace == JxlColorSpace.Gray;
                if (icc is not null)
                {
                    JxlColorSpace space = JxlMetadataWriter.IccColorSpace(icc)
                        ?? throw new NotSupportedException("JPEG XL images can only carry RGB or gray ICC profiles.");
                    if (space == JxlColorSpace.Gray != gray)
                    {
                        throw new NotSupportedException("An ICC profile cannot change a JPEG XL image between gray and colour.");
                    }

                    color = new JxlColorEncoding { WantIcc = true, ColorSpace = color.ColorSpace == JxlColorSpace.Xyb ? JxlColorSpace.Rgb : space };
                }
                else
                {
                    color = JxlMetadataWriter.Encoding(metadata, gray);
                }
            }

            newCodestream = JxlCodestreamWriter.Rewrite(codestream, headers,
                headers.Metadata with { Color = color, Orientation = orientation }, color.WantIcc ? icc : null, frameStart);
        }

        if (!container.IsBoxed)
        {
            byte[] stream = newCodestream ?? data.ToArray();
            var added = new List<JxlBox>();
            if (exifChanged && metadata.Exif is { } exif) added.Add(JxlMetadataWriter.ExifBox(exif, compress: false));
            if (xmpChanged && metadata.Xmp is { } xmp) added.Add(JxlMetadataWriter.XmpBox(xmp, compress: false));
            return added.Count == 0 ? stream : JxlContainerWriter.Write(stream, added);
        }

        return RewriteBoxes(data.Span, boxes, newCodestream, exifChanged ? metadata.Exif : null, exifChanged,
            xmpChanged ? metadata.Xmp : null, xmpChanged);
    }

    private static byte[] RewriteBoxes(ReadOnlySpan<byte> data, List<(string Type, string? Inner, long Start, long ContentStart, long End)> boxes,
        byte[]? codestream, byte[]? exif, bool exifChanged, byte[]? xmp, bool xmpChanged)
    {
        var output = new MemoryStream();
        bool exifWritten = !exifChanged || exif is null;
        bool xmpWritten = !xmpChanged || xmp is null;
        bool codestreamWritten = codestream is null;
        bool Compressed(string type) => boxes.Any(box => box.Type == "brob" && box.Inner == type);
        bool exifCompressed = Compressed("Exif");
        bool xmpCompressed = Compressed("xml ");
        long end = 0;
        foreach ((string type, string? inner, long start, long contentStart, long boxEnd) in boxes)
        {
            end = boxEnd;
            string kind = inner ?? type;
            bool isCodestream = type is "jxlc" or "jxlp";
            if ((kind == "Exif" && exifChanged) || (kind == "xml " && xmpChanged) || (isCodestream && codestream is not null))
            {
                // Replaced boxes: the new content takes the place of the first one of its kind.
                if (kind == "Exif" && !exifWritten)
                {
                    JxlContainerWriter.WriteBox(output, JxlMetadataWriter.ExifBox(exif!, exifCompressed));
                    exifWritten = true;
                }
                else if (kind == "xml " && !xmpWritten)
                {
                    JxlContainerWriter.WriteBox(output, JxlMetadataWriter.XmpBox(xmp!, xmpCompressed));
                    xmpWritten = true;
                }
                else if (isCodestream && !codestreamWritten)
                {
                    WriteNewBoxes(output, ref exifWritten, exif, ref xmpWritten, xmp);
                    JxlContainerWriter.WriteBox(output, new JxlBox("jxlc", codestream));
                    codestreamWritten = true;
                }

                continue;
            }

            if (isCodestream) WriteNewBoxes(output, ref exifWritten, exif, ref xmpWritten, xmp);
            output.Write(data[(int)start..(int)boxEnd]);
        }

        // Boxes after a second signature box are kept as they were.
        output.Write(data[(int)end..]);
        return output.ToArray();
    }

    // New Exif and xml boxes that replace none go before the codestream, where the encoder puts them.
    private static void WriteNewBoxes(Stream output, ref bool exifWritten, byte[]? exif, ref bool xmpWritten, byte[]? xmp)
    {
        if (!exifWritten)
        {
            JxlContainerWriter.WriteBox(output, JxlMetadataWriter.ExifBox(exif!, compress: false));
            exifWritten = true;
        }

        if (!xmpWritten)
        {
            JxlContainerWriter.WriteBox(output, JxlMetadataWriter.XmpBox(xmp!, compress: false));
            xmpWritten = true;
        }
    }

    private static bool Same(byte[]? a, byte[]? b) => (a is null) == (b is null) && (a is null || a.AsSpan().SequenceEqual(b));
}
