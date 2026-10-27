using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Color;

namespace PictureSharp.Formats.Jxl.Integration;

/// <summary>Copies rendered planes to the PictureSharp-owned straight-alpha pixel buffer.</summary>
internal static class JxlPixelWriter
{
    public static Image Write(JxlDecodedImage decoded, uint orientationValue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JxlPlane? alpha = decoded.Alpha;
        var image = alpha is not null ? new Image(decoded.Width, decoded.Height) : new Image(decoded.Width, decoded.Height, new Rgba32(0, 0, 0, 255));
        try
        {
            Span<Rgba32> pixels = image.GetPixelMemory().Span;
            JxlPlane red = decoded.Color[0];
            JxlPlane green = decoded.Color[decoded.Color.Length == 3 ? 1 : 0];
            JxlPlane blue = decoded.Color[^1];
            // Grayscale is written as one channel (dithered as channel 0, alpha as channel 1) and replicated.
            bool gray = decoded.IsGrayscale;
            static byte Gray(float value, int x, int y) => JxlDither.ToByte(value, x, y, 0);
            for (int y = 0; y < decoded.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Span<float> r = red.Row(y);
                Span<float> g = green.Row(y);
                Span<float> b = blue.Row(y);
                Span<float> a = alpha is null ? default : alpha.Row(y);
                Span<Rgba32> row = pixels.Slice(y * decoded.Width, decoded.Width);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = gray
                        ? new Rgba32(Gray(r[x], x, y), Gray(r[x], x, y), Gray(r[x], x, y), alpha is null ? (byte)255 : JxlDither.ToByte(a[x], x, y, 1))
                        : new Rgba32(JxlDither.ToByte(r[x], x, y, 0), JxlDither.ToByte(g[x], x, y, 1), JxlDither.ToByte(b[x], x, y, 2),
                            alpha is null ? (byte)255 : JxlDither.ToByte(a[x], x, y, 3));
                }
            }

            // Stored orientation, as for JPEG: ApplyExifOrientation (or AutoOrient) applies it.
            var orientation = (ExifOrientation)orientationValue;
            if (orientation != ExifOrientation.TopLeft)
            {
                image.Metadata.Orientation = orientation;
            }

            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }
}
