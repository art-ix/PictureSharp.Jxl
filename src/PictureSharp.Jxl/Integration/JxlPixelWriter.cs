using System.Runtime.CompilerServices;
using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.Numerics;

namespace PictureSharp.Formats.Jxl.Integration;

/// <summary>
/// Copies rendered planes to the PictureSharp-owned straight-alpha pixel buffer, in row bands of up to
/// <c>parallelism</c> threads (every row is written on its own).
/// </summary>
internal static partial class JxlPixelWriter
{
    private const int MinBandRows = 16;

    /// <summary>
    /// How the dither pattern is mirrored: libjxl undoes the orientation while it writes 8-bit output and flips a row
    /// (X) or the row order (Y) before dithering (stage_write.cc, FlipX and ProcessRow); a transpose comes after it.
    /// </summary>
    internal readonly record struct DitherFlip(bool X, bool Y)
    {
        /// <summary>The flips of the EXIF <paramref name="orientation"/> that is applied after decoding.</summary>
        public static DitherFlip For(uint orientation) => new(orientation is 2 or 3 or 7 or 8, orientation is 3 or 4 or 6 or 7);
    }

    /// <summary>
    /// The 8-bit image. <paramref name="flip"/> mirrors the dither pattern as libjxl does for the orientation it undoes,
    /// so that an image whose orientation is applied afterwards gets djxl's samples.
    /// </summary>
    public static Image Write(JxlDecodedImage decoded, CancellationToken cancellationToken, int parallelism = JxlParallel.Sequential, DitherFlip flip = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (decoded.IntegerSamples is not null) return WriteInteger(decoded, wide: false, cancellationToken, parallelism, flip);
        if (decoded.Rows is not null) return WriteRendered(decoded, wide: false, cancellationToken, parallelism, flip);
        JxlPlane? alpha = decoded.Alpha;
        var image = CreateNarrowImage(decoded.Width, decoded.Height, alpha is not null);
        try
        {
            Memory<Rgba32> pixels = image.GetPixelMemory<Rgba32>();
            JxlPlane red = decoded.Color[0];
            JxlPlane green = decoded.Color[decoded.Color.Length == 3 ? 1 : 0];
            JxlPlane blue = decoded.Color[^1];
            // Grayscale is written as one channel (dithered as channel 0, alpha as channel 1) and replicated.
            bool gray = decoded.IsGrayscale;
            JxlParallel.ForBands(decoded.Height, MinBandRows, parallelism, cancellationToken, (start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ReadOnlySpan<float> a = alpha is null ? default : alpha.Row(y);
                    Span<Rgba32> row = pixels.Span.Slice(y * decoded.Width, decoded.Width);
                    if (gray)
                    {
                        WriteGrayRow(red.Row(y), a, row, DitherRow(y, decoded.Height, flip), flip.X);
                    }
                    else
                    {
                        WriteColorRow(red.Row(y), green.Row(y), blue.Row(y), a, row, DitherRow(y, decoded.Height, flip), flip.X);
                    }
                }
            });

            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static int DitherRow(int y, int height, DitherFlip flip) => flip.Y ? height - 1 - y : y;

    // Keep image construction out of the row loop's JIT frame when the core's constructors inline.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Image CreateNarrowImage(int width, int height, bool alpha) =>
        alpha ? new Image(width, height) : new Image(width, height, new Rgba32(0, 0, 0, 255));

    public static Image WriteWide(JxlDecodedImage decoded, CancellationToken cancellationToken, int parallelism = JxlParallel.Sequential)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (decoded.IntegerSamples is not null) return WriteInteger(decoded, wide: true, cancellationToken, parallelism, default);
        if (decoded.Rows is not null) return WriteRendered(decoded, wide: true, cancellationToken, parallelism, default);
        var image = CreateWideImage(decoded.Width, decoded.Height, decoded.Alpha is not null);
        try
        {
            Memory<Rgba64> pixels = image.GetPixelMemory<Rgba64>();
            JxlParallel.ForBands(decoded.Height, MinBandRows, parallelism, cancellationToken, (start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Span<Rgba64> row = pixels.Span.Slice(y * decoded.Width, decoded.Width);
                    ReadOnlySpan<float> r = decoded.Color[0].Row(y);
                    ReadOnlySpan<float> g = decoded.IsGrayscale ? r : decoded.Color[1].Row(y);
                    ReadOnlySpan<float> b = decoded.IsGrayscale ? r : decoded.Color[2].Row(y);
                    ReadOnlySpan<float> a = decoded.Alpha is null ? default : decoded.Alpha.Row(y);
                    WriteWideRow(r, g, b, a, row, decoded.IsGrayscale);
                }
            });

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

    internal static void WriteWideRow(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b,
        ReadOnlySpan<float> a, Span<Rgba64> row, bool gray)
    {
        int done = WriteWideRowSimd(r, g, b, a, row, gray);
        if (gray)
        {
            if (a.IsEmpty)
                for (int x = done; x < row.Length; x++) { ushort v = ToUInt16(r[x]); row[x] = new(v, v, v, 65535); }
            else
                for (int x = done; x < row.Length; x++) { ushort v = ToUInt16(r[x]); row[x] = new(v, v, v, ToUInt16(a[x])); }
        }
        else if (a.IsEmpty)
            for (int x = done; x < row.Length; x++) row[x] = new(ToUInt16(r[x]), ToUInt16(g[x]), ToUInt16(b[x]), 65535);
        else
            for (int x = done; x < row.Length; x++) row[x] = new(ToUInt16(r[x]), ToUInt16(g[x]), ToUInt16(b[x]), ToUInt16(a[x]));
    }

    // Preserve the old two-step floating-point conversion (sample × scale, then the float writer) without storing
    // intermediate planes; each worker keeps one scaled row per channel.
    private static Image WriteInteger(JxlDecodedImage decoded, bool wide, CancellationToken cancellationToken, int parallelism, DitherFlip flip)
    {
        JxlIntegerImage source = decoded.IntegerSamples!;
        bool gray = source.Color.Length == 1;
        Image image = wide ? CreateWideImage(decoded.Width, decoded.Height, source.Alpha is not null)
            : CreateNarrowImage(decoded.Width, decoded.Height, source.Alpha is not null);
        try
        {
            float scale = (float)(1.0 / ((1u << source.ColorBits) - 1));
            float alphaScale = (float)(1.0 / ((1u << source.AlphaBits) - 1));
            Memory<Rgba64> widePixels = wide ? image.GetPixelMemory<Rgba64>() : default;
            Memory<Rgba32> narrowPixels = wide ? default : image.GetPixelMemory<Rgba32>();
            int width = decoded.Width;
            JxlParallel.ForBands(decoded.Height, MinBandRows, parallelism, cancellationToken, (start, end) =>
            {
                // One scaled row per channel, owned by this worker: the float row writers vectorize the conversion.
                using var buffers = new JxlBufferScope();
                Span<float> fr = buffers.Rent<float>(width, clear: false).AsSpan(0, width);
                Span<float> fg = gray ? fr : buffers.Rent<float>(width, clear: false).AsSpan(0, width);
                Span<float> fb = gray ? fr : buffers.Rent<float>(width, clear: false).AsSpan(0, width);
                Span<float> fa = source.Alpha is null ? default : buffers.Rent<float>(width, clear: false).AsSpan(0, width);
                for (int y = start; y < end; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ScaleRow(source.Color[0].Row(y), scale, fr);
                    if (!gray)
                    {
                        ScaleRow(source.Color[1].Row(y), scale, fg);
                        ScaleRow(source.Color[2].Row(y), scale, fb);
                    }

                    if (source.Alpha is not null)
                    {
                        ScaleRow(source.Alpha.Row(y), alphaScale, fa);
                    }

                    if (wide) WriteWideRow(fr, fg, fb, fa, widePixels.Span.Slice(y * width, width), gray);
                    else if (gray) WriteGrayRow(fr, fa, narrowPixels.Span.Slice(y * width, width), DitherRow(y, decoded.Height, flip), flip.X);
                    else WriteColorRow(fr, fg, fb, fa, narrowPixels.Span.Slice(y * width, width), DitherRow(y, decoded.Height, flip), flip.X);
                }
            });
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    // Colour rows rendered per band (subsampled YCbCr, 0.8.6): each worker keeps one row per channel and its band's
    // chroma rows, then writes them as the plane rows would be written.
    private static Image WriteRendered(JxlDecodedImage decoded, bool wide, CancellationToken cancellationToken, int parallelism, DitherFlip flip)
    {
        JxlColorRows rows = decoded.Rows!;
        JxlPlane? alpha = decoded.Alpha;
        Image image = wide ? CreateWideImage(decoded.Width, decoded.Height, alpha is not null)
            : CreateNarrowImage(decoded.Width, decoded.Height, alpha is not null);
        try
        {
            Memory<Rgba64> widePixels = wide ? image.GetPixelMemory<Rgba64>() : default;
            Memory<Rgba32> narrowPixels = wide ? default : image.GetPixelMemory<Rgba32>();
            int width = decoded.Width;
            JxlParallel.ForBands(decoded.Height, MinBandRows, parallelism, cancellationToken, (start, end) =>
            {
                using var buffers = new JxlBufferScope();
                Span<float> r = buffers.Rent<float>(width, clear: false).AsSpan(0, width);
                Span<float> g = buffers.Rent<float>(width, clear: false).AsSpan(0, width);
                Span<float> b = buffers.Rent<float>(width, clear: false).AsSpan(0, width);
                JxlColorRows.Band band = rows.Open();
                for (int y = start; y < end; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    band.Render(y, r, g, b);
                    ReadOnlySpan<float> a = alpha is null ? default : alpha.Row(y);
                    if (wide) WriteWideRow(r, g, b, a, widePixels.Span.Slice(y * width, width), gray: false);
                    else WriteColorRow(r, g, b, a, narrowPixels.Span.Slice(y * width, width), DitherRow(y, decoded.Height, flip), flip.X);
                }
            });
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    // value × scale in single precision, as the integer rows were converted before 0.8.4.
    private static void ScaleRow(ReadOnlySpan<int> values, float scale, Span<float> output)
    {
        values = values[..output.Length];
        for (int x = ScaleRowSimd(values, scale, output); x < output.Length; x++)
        {
            output[x] = values[x] * scale;
        }
    }

    private static ushort ToUInt16(float value) => (ushort)Math.Clamp(MathF.Floor((value * 65535.0f) + 0.5f), 0, 65535);

    // Without alpha the row has no alpha samples and every pixel is opaque. A mirrored row (flipX, orientations 2, 3, 7
    // and 8 only) takes its dither from column width - 1 - x and is written by the scalar loop.
    internal static void WriteGrayRow(ReadOnlySpan<float> v, ReadOnlySpan<float> a, Span<Rgba32> row, int y, bool flipX = false)
    {
        ReadOnlySpan<float> dither = JxlDither.Row(y, 0);
        ReadOnlySpan<float> ditherAlpha = JxlDither.Row(y, 1);
        int last = row.Length - 1;
        for (int x = flipX ? 0 : WriteNarrowRowSimd(v, v, v, a, row, true, y); x < row.Length; x++)
        {
            int column = flipX ? last - x : x;
            byte value = JxlDither.ToByte(v[x], dither[JxlDither.Column(column, 0)]);
            row[x] = new Rgba32(value, value, value, a.IsEmpty ? (byte)255 : JxlDither.ToByte(a[x], ditherAlpha[JxlDither.Column(column, 1)]));
        }
    }

    internal static void WriteColorRow(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, ReadOnlySpan<float> a,
        Span<Rgba32> row, int y, bool flipX = false)
    {
        ReadOnlySpan<float> ditherR = JxlDither.Row(y, 0);
        ReadOnlySpan<float> ditherG = JxlDither.Row(y, 1);
        ReadOnlySpan<float> ditherB = JxlDither.Row(y, 2);
        ReadOnlySpan<float> ditherA = JxlDither.Row(y, 3);
        r = r[..row.Length];
        g = g[..row.Length];
        b = b[..row.Length];
        int last = row.Length - 1;
        for (int x = flipX ? 0 : WriteNarrowRowSimd(r, g, b, a, row, false, y); x < row.Length; x++)
        {
            int column = flipX ? last - x : x;
            row[x] = new Rgba32(JxlDither.ToByte(r[x], ditherR[JxlDither.Column(column, 0)]), JxlDither.ToByte(g[x], ditherG[JxlDither.Column(column, 1)]),
                JxlDither.ToByte(b[x], ditherB[JxlDither.Column(column, 2)]), a.IsEmpty ? (byte)255 : JxlDither.ToByte(a[x], ditherA[JxlDither.Column(column, 3)]));
        }
    }
}
