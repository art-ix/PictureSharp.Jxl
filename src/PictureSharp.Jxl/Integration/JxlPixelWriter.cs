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
        if (decoded.IntegerSamples is not null) return WriteInteger(decoded, wide: false, cancellationToken);
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
        if (decoded.IntegerSamples is not null) return WriteInteger(decoded, wide: true, cancellationToken);
        var image = CreateWideImage(decoded.Width, decoded.Height, decoded.Alpha is not null);
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
                WriteWideRow(r, g, b, a, row, decoded.IsGrayscale);
            }

            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Image CreateWideImage(int width, int height, bool alpha) => alpha
        ? new Image(width, height, PixelType.Rgba64)
        : new Image(width, height, PixelType.Rgba64, new Rgba64(0, 0, 0, ushort.MaxValue));

    private static void WriteWideRow(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b,
        ReadOnlySpan<float> a, Span<Rgba64> row, bool gray)
    {
        if (gray)
        {
            if (a.IsEmpty)
                for (int x = 0; x < row.Length; x++) { ushort v = ToUInt16(r[x]); row[x] = new(v, v, v, 65535); }
            else
                for (int x = 0; x < row.Length; x++) { ushort v = ToUInt16(r[x]); row[x] = new(v, v, v, ToUInt16(a[x])); }
        }
        else if (a.IsEmpty)
            for (int x = 0; x < row.Length; x++) row[x] = new(ToUInt16(r[x]), ToUInt16(g[x]), ToUInt16(b[x]), 65535);
        else
            for (int x = 0; x < row.Length; x++) row[x] = new(ToUInt16(r[x]), ToUInt16(g[x]), ToUInt16(b[x]), ToUInt16(a[x]));
    }

    // Preserve the old two-step floating-point conversion without storing intermediate planes or rows.
    private static Image WriteInteger(JxlDecodedImage decoded, bool wide, CancellationToken cancellationToken)
    {
        JxlIntegerImage source = decoded.IntegerSamples!;
        bool gray = source.Color.Length == 1;
        Image image = wide ? CreateWideImage(decoded.Width, decoded.Height, source.Alpha is not null)
            : CreateNarrowImage(decoded.Width, decoded.Height, source.Alpha is not null);
        try
        {
            float scale = (float)(1.0 / ((1u << source.ColorBits) - 1));
            float alphaScale = (float)(1.0 / ((1u << source.AlphaBits) - 1));
            for (int y = 0; y < decoded.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<int> r = source.Color[0].Row(y);
                ReadOnlySpan<int> g = gray ? r : source.Color[1].Row(y);
                ReadOnlySpan<int> b = gray ? r : source.Color[2].Row(y);
                ReadOnlySpan<int> a = source.Alpha is null ? default : source.Alpha.Row(y);
                if (wide) WriteIntegerWideRow(r, g, b, a, scale, alphaScale, image.GetRowSpan<Rgba64>(y), gray);
                else WriteIntegerNarrowRow(r, g, b, a, scale, alphaScale, image.GetRowSpan<Rgba32>(y), gray, y);
            }
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    private static void WriteIntegerWideRow(ReadOnlySpan<int> r, ReadOnlySpan<int> g, ReadOnlySpan<int> b,
        ReadOnlySpan<int> a, float scale, float alphaScale, Span<Rgba64> row, bool gray)
    {
        if (gray)
        {
            if (a.IsEmpty)
                for (int x = 0; x < row.Length; x++) { ushort v = ToUInt16(r[x] * scale); row[x] = new(v, v, v, 65535); }
            else
                for (int x = 0; x < row.Length; x++) { ushort v = ToUInt16(r[x] * scale); row[x] = new(v, v, v, ToUInt16(a[x] * alphaScale)); }
        }
        else if (a.IsEmpty)
            for (int x = 0; x < row.Length; x++) row[x] = new(ToUInt16(r[x] * scale), ToUInt16(g[x] * scale), ToUInt16(b[x] * scale), 65535);
        else
            for (int x = 0; x < row.Length; x++) row[x] = new(ToUInt16(r[x] * scale), ToUInt16(g[x] * scale), ToUInt16(b[x] * scale), ToUInt16(a[x] * alphaScale));
    }

    private static void WriteIntegerNarrowRow(ReadOnlySpan<int> r, ReadOnlySpan<int> g, ReadOnlySpan<int> b,
        ReadOnlySpan<int> a, float scale, float alphaScale, Span<Rgba32> row, bool gray, int y)
    {
        ReadOnlySpan<float> dr = JxlDither.Row(y, 0), dg = JxlDither.Row(y, 1), db = JxlDither.Row(y, 2);
        int alphaChannel = gray ? 1 : 3;
        ReadOnlySpan<float> da = JxlDither.Row(y, alphaChannel);
        if (gray)
        {
            for (int x = 0; x < row.Length; x++)
            {
                byte v = JxlDither.ToByte(r[x] * scale, dr[JxlDither.Column(x, 0)]);
                row[x] = new(v, v, v, a.IsEmpty ? (byte)255 : JxlDither.ToByte(a[x] * alphaScale, da[JxlDither.Column(x, alphaChannel)]));
            }
        }
        else for (int x = 0; x < row.Length; x++)
            row[x] = new(JxlDither.ToByte(r[x] * scale, dr[JxlDither.Column(x, 0)]),
                JxlDither.ToByte(g[x] * scale, dg[JxlDither.Column(x, 1)]),
                JxlDither.ToByte(b[x] * scale, db[JxlDither.Column(x, 2)]),
                a.IsEmpty ? (byte)255 : JxlDither.ToByte(a[x] * alphaScale, da[JxlDither.Column(x, alphaChannel)]));
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
