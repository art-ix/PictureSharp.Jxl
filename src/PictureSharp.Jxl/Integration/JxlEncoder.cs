using PictureSharp.Formats.Jxl.Codestream.Encoding;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Integration;

/// <summary>
/// The PictureSharp SPI adapter of the lossless encoder: the root frame's samples as gray or RGB with alpha when any
/// pixel is not opaque, the colour encoding of the ICC profile or colour description, the orientation, and Exif and
/// xml boxes in a container when there are any.
/// </summary>
internal sealed class JxlEncoder : IImageFormatEncoder<JxlEncoderOptions>
{
    // Level 5 limits (ISO/IEC 18181-2 Annex A); larger images need level 10 and its jxll box.
    private const long Level5MaxDimension = 1 << 18;
    private const long Level5MaxPixels = 1 << 28;

    public static JxlEncoder Instance { get; } = new();

    public void Encode(Image image, Stream output, JxlEncoderOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        if (!output.CanWrite) throw new ArgumentException("The stream must be writable.", nameof(output));
        cancellationToken.ThrowIfCancellationRequested();
        output.Write(Encode(image, options, cancellationToken));
    }

    public static byte[] Encode(Image image, JxlEncoderOptions options, CancellationToken cancellationToken)
    {
        int width = image.Width;
        int height = image.Height;
        var pixels = new Rgba32[(long)width * height];
        image.Frames.RootFrame.CopyPixelDataTo(pixels);
        ImageMetadata metadata = image.Metadata;
        byte[]? icc = (options.Metadata & MetadataKinds.Icc) != 0 ? metadata.IccProfile : null;
        JxlColorSpace? iccSpace = icc is null ? null : JxlMetadataWriter.IccColorSpace(icc)
            ?? throw new NotSupportedException("JPEG XL images can only carry RGB or gray ICC profiles without CMYK samples.");
        bool gray = iccSpace == JxlColorSpace.Gray || (iccSpace is null && IsGray(pixels));
        if (gray && !IsGray(pixels)) throw new NotSupportedException("A gray ICC profile cannot describe an image with colour pixels.");
        bool alpha = !image.Frames.RootFrame.IsOpaque();

        int count = pixels.Length;
        int[][] color = gray ? [new int[count]] : [new int[count], new int[count], new int[count]];
        int[]? alphaPlane = alpha ? new int[count] : null;
        for (int i = 0; i < count; i++)
        {
            if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            Rgba32 pixel = pixels[i];
            color[0][i] = pixel.R;
            if (!gray)
            {
                color[1][i] = pixel.G;
                color[2][i] = pixel.B;
            }

            if (alphaPlane is not null) alphaPlane[i] = pixel.A;
        }

        pixels = [];
        var input = new JxlEncodeImage(width, height, color, alphaPlane, JxlMetadataWriter.Encoding(metadata, gray), icc,
            JxlMetadataWriter.Orientation(metadata));
        byte[] codestream = JxlCodestreamWriter.Write(input, cancellationToken);

        var boxes = new List<JxlBox>();
        if ((options.Metadata & MetadataKinds.Exif) != 0 && metadata.Exif is { } exif)
        {
            boxes.Add(JxlMetadataWriter.ExifBox(exif, options.CompressMetadata));
        }

        if ((options.Metadata & MetadataKinds.Xmp) != 0 && metadata.Xmp is { } xmp)
        {
            boxes.Add(JxlMetadataWriter.XmpBox(xmp, options.CompressMetadata));
        }

        int level = width > Level5MaxDimension || height > Level5MaxDimension || (long)width * height > Level5MaxPixels ? 10 : 5;
        return boxes.Count == 0 && level == 5 ? codestream : JxlContainerWriter.Write(codestream, boxes, level);
    }

    private static bool IsGray(ReadOnlySpan<Rgba32> pixels)
    {
        foreach (Rgba32 pixel in pixels)
        {
            if (pixel.R != pixel.G || pixel.G != pixel.B) return false;
        }

        return true;
    }
}
