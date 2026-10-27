using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Color;

namespace PictureSharp.Formats.Jxl.Integration;

/// <summary>Copies rendered planes to the PictureSharp-owned straight-alpha pixel buffer.</summary>
internal static class JxlPixelWriter
{
    public static Image Write(JxlDecodedImage decoded, CancellationToken cancellationToken)
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
            for (int y = 0; y < decoded.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<float> a = alpha is null ? default : alpha.Row(y);
                Span<Rgba32> row = pixels.Slice(y * decoded.Width, decoded.Width);
                if (gray)
                {
                    WriteGrayRow(red.Row(y), a, row, y);
                }
                else
                {
                    WriteColorRow(red.Row(y), green.Row(y), blue.Row(y), a, row, y);
                }
            }

            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    // Without alpha the row has no alpha samples and every pixel is opaque.
    private static void WriteGrayRow(ReadOnlySpan<float> v, ReadOnlySpan<float> a, Span<Rgba32> row, int y)
    {
        ReadOnlySpan<float> dither = JxlDither.Row(y, 0);
        ReadOnlySpan<float> ditherAlpha = JxlDither.Row(y, 1);
        for (int x = 0; x < row.Length; x++)
        {
            byte value = JxlDither.ToByte(v[x], dither[JxlDither.Column(x, 0)]);
            row[x] = new Rgba32(value, value, value, a.IsEmpty ? (byte)255 : JxlDither.ToByte(a[x], ditherAlpha[JxlDither.Column(x, 1)]));
        }
    }

    private static void WriteColorRow(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, ReadOnlySpan<float> a,
        Span<Rgba32> row, int y)
    {
        ReadOnlySpan<float> ditherR = JxlDither.Row(y, 0);
        ReadOnlySpan<float> ditherG = JxlDither.Row(y, 1);
        ReadOnlySpan<float> ditherB = JxlDither.Row(y, 2);
        ReadOnlySpan<float> ditherA = JxlDither.Row(y, 3);
        r = r[..row.Length];
        g = g[..row.Length];
        b = b[..row.Length];
        for (int x = 0; x < row.Length; x++)
        {
            row[x] = new Rgba32(JxlDither.ToByte(r[x], ditherR[JxlDither.Column(x, 0)]), JxlDither.ToByte(g[x], ditherG[JxlDither.Column(x, 1)]),
                JxlDither.ToByte(b[x], ditherB[JxlDither.Column(x, 2)]), a.IsEmpty ? (byte)255 : JxlDither.ToByte(a[x], ditherA[JxlDither.Column(x, 3)]));
        }
    }
}
