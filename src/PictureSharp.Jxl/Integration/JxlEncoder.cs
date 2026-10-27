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
    private const int ExtendedLevel = 10;
    private const int CancellationCheckMask = 0xFFFF;

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
        byte[]? icc = options.Metadata.HasFlag(MetadataKinds.Icc) ? metadata.IccProfile : null;
        JxlColorSpace? iccSpace = icc is null ? null
            : JxlMetadataWriter.RequireIccColorSpace(icc, "JPEG XL images can only carry RGB or gray ICC profiles without CMYK samples.");
        bool gray = iccSpace switch
        {
            null => IsGray(pixels),
            JxlColorSpace.Gray => IsGray(pixels) ? true : throw new NotSupportedException("A gray ICC profile cannot describe an image with colour pixels."),
            _ => false,
        };
        bool alpha = !image.Frames.RootFrame.IsOpaque();
        (int[][] color, int[]? alphaPlane) = SplitPlanes(pixels, gray, alpha, cancellationToken);
        pixels = [];

        var input = new JxlEncodeImage(width, height, color, alphaPlane, JxlMetadataWriter.Encoding(metadata, gray), icc,
            JxlMetadataWriter.Orientation(metadata));
        byte[] codestream = JxlCodestreamWriter.Write(input, cancellationToken);
        JxlBox[] boxes = JxlMetadataWriter.DescriptiveBoxes(
            options.Metadata.HasFlag(MetadataKinds.Exif) ? metadata.Exif : null,
            options.Metadata.HasFlag(MetadataKinds.Xmp) ? metadata.Xmp : null,
            options.CompressMetadata);
        int level = ConformanceLevel(width, height);
        return boxes.Length == 0 && level == JxlContainer.DefaultLevel ? codestream : JxlContainerWriter.Write(codestream, boxes, level);
    }

    // One integer plane per colour channel (one for gray) and an alpha plane when requested.
    private static (int[][] Color, int[]? Alpha) SplitPlanes(ReadOnlySpan<Rgba32> pixels, bool gray, bool alpha, CancellationToken cancellationToken)
    {
        int count = pixels.Length;
        int[][] color = gray ? [new int[count]] : [new int[count], new int[count], new int[count]];
        int[]? alphaPlane = alpha ? new int[count] : null;
        for (int i = 0; i < count; i++)
        {
            if ((i & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();
            Rgba32 pixel = pixels[i];
            color[0][i] = pixel.R;
            if (!gray)
            {
                color[1][i] = pixel.G;
                color[2][i] = pixel.B;
            }

            if (alphaPlane is not null) alphaPlane[i] = pixel.A;
        }

        return (color, alphaPlane);
    }

    private static int ConformanceLevel(int width, int height) =>
        width > Level5MaxDimension || height > Level5MaxDimension || (long)width * height > Level5MaxPixels
            ? ExtendedLevel
            : JxlContainer.DefaultLevel;

    private static bool IsGray(ReadOnlySpan<Rgba32> pixels)
    {
        foreach (Rgba32 pixel in pixels)
        {
            if (pixel.R != pixel.G || pixel.G != pixel.B) return false;
        }

        return true;
    }
}
