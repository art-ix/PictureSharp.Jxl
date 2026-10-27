using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.Encoding;
using PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Integration;

/// <summary>
/// The PictureSharp SPI adapter of the encoder: the root frame's samples as gray or RGB with alpha when any pixel is
/// not opaque, lossy in XYB or lossless, the colour encoding of the ICC profile or colour description, the orientation,
/// and Exif and xml boxes in a container when there are any.
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
        JxlColorEncoding encoding = JxlMetadataWriter.Encoding(metadata, gray);
        uint orientation = JxlMetadataWriter.Orientation(metadata);
        float distance = options.EffectiveDistance;
        // As libjxl for XYB images: a profile an enumerated encoding describes is stored as that encoding, so decoders
        // return pixels in it rather than in linear sRGB. Lossless frames keep the profile.
        if (distance != 0 && icc is not null && JxlIccEncoding.TryEnumerate(icc, gray) is { } enumerated)
        {
            (encoding, icc) = (enumerated, null);
        }

        JxlXybInput? xybInput = distance == 0 ? null
            : JxlXybInput.TryCreate(icc is null ? encoding : new JxlColorEncoding { WantIcc = true, ColorSpace = gray ? JxlColorSpace.Gray : JxlColorSpace.Rgb },
                icc is null ? null : JxlIccTarget.TryCreate(icc, gray), JxlToneMapping.Default.IntensityTarget);
        byte[] codestream;
        if (xybInput is not null)
        {
            // Profiles and transfer functions XYB cannot be converted from (lookup tables, HLG) stay lossless.
            JxlImage3F xyb = ToXyb(pixels, width, height, xybInput, cancellationToken);
            int[]? alphaPlane = alpha ? AlphaPlane(pixels, cancellationToken) : null;
            pixels = [];
            // Alpha stays lossless at full size, so frames with alpha are not downsampled at high distances.
            var settings = JxlLossySettings.Create(Math.Max(distance, JxlLossySettings.MinDistance), options.Effort, resample: !alpha);
            codestream = JxlCodestreamWriter.WriteLossy(new JxlLossyEncodeImage(width, height, xyb, alphaPlane, gray, encoding, icc, orientation, settings),
                cancellationToken);
        }
        else
        {
            (int[][] color, int[]? alphaPlane) = SplitPlanes(pixels, gray, alpha, cancellationToken);
            pixels = [];
            var input = new JxlEncodeImage(width, height, color, alphaPlane, encoding, icc, orientation);
            codestream = JxlCodestreamWriter.Write(input, cancellationToken);
        }

        JxlBox[] boxes = JxlMetadataWriter.DescriptiveBoxes(
            options.Metadata.HasFlag(MetadataKinds.Exif) ? metadata.Exif : null,
            options.Metadata.HasFlag(MetadataKinds.Xmp) ? metadata.Xmp : null,
            options.CompressMetadata);
        int level = ConformanceLevel(width, height);
        return boxes.Length == 0 && level == JxlContainer.DefaultLevel ? codestream : JxlContainerWriter.Write(codestream, boxes, level);
    }

    // The XYB planes of the pixels; gray pixels have equal channels.
    private static JxlImage3F ToXyb(ReadOnlySpan<Rgba32> pixels, int width, int height, JxlXybInput input, CancellationToken cancellationToken)
    {
        var xyb = new JxlImage3F(width, height);
        for (int y = 0; y < height; y++)
        {
            if ((y & 0xF) == 0) cancellationToken.ThrowIfCancellationRequested();
            Span<float> r = xyb.Row(0, y);
            Span<float> g = xyb.Row(1, y);
            Span<float> b = xyb.Row(2, y);
            ReadOnlySpan<Rgba32> row = pixels.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                r[x] = row[x].R;
                g[x] = row[x].G;
                b[x] = row[x].B;
            }

            input.Convert(r, g, b);
        }

        return xyb;
    }

    private static int[] AlphaPlane(ReadOnlySpan<Rgba32> pixels, CancellationToken cancellationToken)
    {
        int[] alpha = new int[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & CancellationCheckMask) == 0) cancellationToken.ThrowIfCancellationRequested();
            alpha[i] = pixels[i].A;
        }

        return alpha;
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
