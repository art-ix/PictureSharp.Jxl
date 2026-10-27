using PictureSharp.Formats.Jxl.Codestream.Frames;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Integration;

/// <summary>
/// Image.ReadMetadata without decoding frames: the file's ICC profile (decoded from the codestream) or colour
/// description, EXIF, XMP and orientation. Unlike Decode it reports the profile of XYB images rendered as sRGB.
/// Rewrite (EditMetadata) keeps the frames and is described on <see cref="JxlMetadataRewriter"/>.
/// </summary>
internal sealed class JxlMetadataCodec : IImageFormatMetadataCodec
{
    public static JxlMetadataCodec Instance { get; } = new();

    public ImageMetadata Read(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        JxlContainer container = JxlContainer.Parse(data, includeMetadata: true, context.CancellationToken);
        JxlHeaders headers = JxlHeadersReader.Read(container.Codestream);
        JxlFrameSectionReader.ReadIcc(container.Codestream, headers, context.CancellationToken, out byte[]? icc,
            context.Options.MaxMetadataBytes, context.ReserveMetadata);
        var metadata = new ImageMetadata();
        JxlMetadataReader.ApplyColour(metadata, headers.Metadata.Color, icc, context.Options);
        JxlMetadataReader.ApplyDescriptive(metadata, container.MetadataBoxes, context);
        JxlMetadataReader.ApplyOrientation(metadata, headers.Metadata.Orientation);
        return metadata;
    }

    public byte[] Rewrite(ReadOnlyMemory<byte> data, ImageMetadata original, ImageMetadata metadata, ImageDecodeContext context) =>
        JxlMetadataRewriter.Rewrite(data, original, metadata, context);
}
