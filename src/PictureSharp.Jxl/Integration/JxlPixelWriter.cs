using System.Runtime.CompilerServices;
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
        var image = CreateNarrowImage(decoded.Width, decoded.Height, alpha is not null);
        try
        {
            Span<Rgba32> pixels = image.GetPixelMemory<Rgba32>().Span;
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

    // Keep image construction out of the row loop's JIT frame when the core's constructors inline.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Image CreateNarrowImage(int width, int height, bool alpha) =>
        alpha ? new Image(width, height) : new Image(width, height, new Rgba32(0, 0, 0, 255));

    public static Image WriteWide(JxlDecodedImage decoded, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var image = decoded.Alpha is null
            ? new Image(decoded.Width, decoded.Height, PixelType.Rgba64, new Rgba64(0, 0, 0, ushort.MaxValue))
            : new Image(decoded.Width, decoded.Height, PixelType.Rgba64);
        try
        {
            for (int y = 0; y < decoded.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Span<Rgba64> row = image.GetRowSpan<Rgba64>(y);
                ReadOnlySpan<float> r = decoded.Color[0].Row(y);
                ReadOnlySpan<float> g = decoded.IsGrayscale ? r : decoded.Color[1].Row(y);
                ReadOnlySpan<float> b = decoded.IsGrayscale ? r : decoded.Color[2].Row(y);
                ReadOnlySpan<float> a = decoded.Alpha is null ? default : decoded.Alpha.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgba64(ToUInt16(r[x]), ToUInt16(g[x]), ToUInt16(b[x]), a.IsEmpty ? ushort.MaxValue : ToUInt16(a[x]));
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

    private static ushort ToUInt16(float value) => (ushort)Math.Clamp(MathF.Floor((value * 65535.0f) + 0.5f), 0, 65535);

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
