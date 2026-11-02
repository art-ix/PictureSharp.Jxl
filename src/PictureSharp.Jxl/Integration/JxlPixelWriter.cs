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

    // libjxl's kSmallAlpha (alpha.h): the smallest alpha that divides premultiplied colour.
    private const float SmallAlpha = 1.0f / (1u << 26);

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
        Image image = CreateNarrowImage(decoded.Width, decoded.Height, HasAlpha(decoded));
        try
        {
            WriteNarrowPixels(decoded, image.GetPixelMemory<Rgba32>(), cancellationToken, parallelism, flip);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A further animation frame of <paramref name="pixelType"/>, written as <see cref="Write"/> or <see cref="WriteWide"/>
    /// writes an image; the caller appends it to the image.
    /// </summary>
    public static ImageFrame WriteFrame(JxlDecodedImage decoded, PixelType pixelType, CancellationToken cancellationToken,
        int parallelism = JxlParallel.Sequential, DitherFlip flip = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frame = new ImageFrame(decoded.Width, decoded.Height, pixelType);
        WriteInto(decoded, frame, cancellationToken, parallelism, flip);
        return frame;
    }

    /// <summary>
    /// Overwrites every pixel of <paramref name="frame"/> (of the decoded size, in its own pixel type) as
    /// <see cref="WriteFrame"/> writes a new one; the frame readers reuse one frame (0.8.13).
    /// </summary>
    public static void WriteInto(JxlDecodedImage decoded, ImageFrame frame, CancellationToken cancellationToken,
        int parallelism = JxlParallel.Sequential, DitherFlip flip = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.Width != decoded.Width || frame.Height != decoded.Height)
        {
            throw new InvalidOperationException("The frame does not have the decoded size.");
        }

        if (frame.PixelType == PixelType.Rgba64) WriteWidePixels(decoded, frame.GetPixelMemory<Rgba64>(), cancellationToken, parallelism);
        else WriteNarrowPixels(decoded, frame.GetPixelMemory<Rgba32>(), cancellationToken, parallelism, flip);
    }

    private static bool HasAlpha(JxlDecodedImage decoded) =>
        decoded.IntegerSamples is { } samples ? samples.Alpha is not null : decoded.Alpha is not null;

    private static void WriteNarrowPixels(JxlDecodedImage decoded, Memory<Rgba32> pixels, CancellationToken cancellationToken, int parallelism,
        DitherFlip flip)
    {
        if (decoded.IntegerSamples is not null)
        {
            WriteInteger(decoded, default, pixels, cancellationToken, parallelism, flip);
            return;
        }

        if (decoded.Rows is not null)
        {
            WriteRendered(decoded, default, pixels, cancellationToken, parallelism, flip);
            return;
        }

        JxlPlane? alpha = decoded.Alpha;
        JxlPlane red = decoded.Color[0];
        JxlPlane green = decoded.Color[decoded.Color.Length == 3 ? 1 : 0];
        JxlPlane blue = decoded.Color[^1];
        // Grayscale is written as one channel (dithered as channel 0, alpha as channel 1) and replicated.
        bool gray = decoded.IsGrayscale;
        bool premultiplied = decoded.Premultiplied && alpha is not null;
        JxlParallel.ForBands(decoded.Height, MinBandRows, parallelism, cancellationToken, (start, end) =>
        {
            using var rows = new StraightRows(premultiplied, gray, decoded.Width);
            for (int y = start; y < end; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<float> a = alpha is null ? default : alpha.Row(y);
                ReadOnlySpan<float> r = red.Row(y), g = green.Row(y), b = blue.Row(y);
                rows.Straighten(ref r, ref g, ref b, a);
                Span<Rgba32> row = pixels.Span.Slice(y * decoded.Width, decoded.Width);
                if (gray)
                {
                    WriteGrayRow(r, a, row, DitherRow(y, decoded.Height, flip), flip.X);
                }
                else
                {
                    WriteColorRow(r, g, b, a, row, DitherRow(y, decoded.Height, flip), flip.X);
                }
            }
        });
    }

    private static int DitherRow(int y, int height, DitherFlip flip) => flip.Y ? height - 1 - y : y;

    // Keep image construction out of the row loop's JIT frame when the core's constructors inline.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Image CreateNarrowImage(int width, int height, bool alpha) =>
        alpha ? new Image(width, height) : new Image(width, height, new Rgba32(0, 0, 0, 255));

    public static Image WriteWide(JxlDecodedImage decoded, CancellationToken cancellationToken, int parallelism = JxlParallel.Sequential)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Image image = CreateWideImage(decoded.Width, decoded.Height, HasAlpha(decoded));
        try
        {
            WriteWidePixels(decoded, image.GetPixelMemory<Rgba64>(), cancellationToken, parallelism);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static void WriteWidePixels(JxlDecodedImage decoded, Memory<Rgba64> pixels, CancellationToken cancellationToken, int parallelism)
    {
        if (decoded.IntegerSamples is not null)
        {
            WriteInteger(decoded, pixels, default, cancellationToken, parallelism, default);
            return;
        }

        if (decoded.Rows is not null)
        {
            WriteRendered(decoded, pixels, default, cancellationToken, parallelism, default);
            return;
        }

        bool gray = decoded.IsGrayscale;
        bool premultiplied = decoded.Premultiplied && decoded.Alpha is not null;
        JxlParallel.ForBands(decoded.Height, MinBandRows, parallelism, cancellationToken, (start, end) =>
        {
            using var rows = new StraightRows(premultiplied, gray, decoded.Width);
            for (int y = start; y < end; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Span<Rgba64> row = pixels.Span.Slice(y * decoded.Width, decoded.Width);
                ReadOnlySpan<float> a = decoded.Alpha is null ? default : decoded.Alpha.Row(y);
                ReadOnlySpan<float> r = decoded.Color[0].Row(y);
                ReadOnlySpan<float> g = gray ? default : decoded.Color[1].Row(y), b = gray ? default : decoded.Color[2].Row(y);
                rows.Straighten(ref r, ref g, ref b, a);
                WriteWideRow(r, gray ? r : g, gray ? r : b, a, row, gray);
            }
        });
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
    // Writes widePixels when they are not empty, otherwise narrowPixels.
    private static void WriteInteger(JxlDecodedImage decoded, Memory<Rgba64> widePixels, Memory<Rgba32> narrowPixels,
        CancellationToken cancellationToken, int parallelism, DitherFlip flip)
    {
        JxlIntegerImage source = decoded.IntegerSamples!;
        bool gray = source.Color.Length == 1;
        bool wide = !widePixels.IsEmpty;
        float scale = (float)(1.0 / ((1u << source.ColorBits) - 1));
        float alphaScale = (float)(1.0 / ((1u << source.AlphaBits) - 1));
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
                    if (decoded.Premultiplied) Unpremultiply(fr, fg, fb, fa, gray);
                }

                if (wide) WriteWideRow(fr, fg, fb, fa, widePixels.Span.Slice(y * width, width), gray);
                else if (gray) WriteGrayRow(fr, fa, narrowPixels.Span.Slice(y * width, width), DitherRow(y, decoded.Height, flip), flip.X);
                else WriteColorRow(fr, fg, fb, fa, narrowPixels.Span.Slice(y * width, width), DitherRow(y, decoded.Height, flip), flip.X);
            }
        });
    }

    // Colour rows rendered per band (subsampled YCbCr, 0.8.6): each worker keeps one row per channel and its band's
    // chroma rows, then writes them as the plane rows would be written.
    private static void WriteRendered(JxlDecodedImage decoded, Memory<Rgba64> widePixels, Memory<Rgba32> narrowPixels,
        CancellationToken cancellationToken, int parallelism, DitherFlip flip)
    {
        JxlColorRows rows = decoded.Rows!;
        JxlPlane? alpha = decoded.Alpha;
        bool wide = !widePixels.IsEmpty;
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
                if (decoded.Premultiplied && alpha is not null) Unpremultiply(r, g, b, a, gray: false);
                if (wide) WriteWideRow(r, g, b, a, widePixels.Span.Slice(y * width, width), gray: false);
                else WriteColorRow(r, g, b, a, narrowPixels.Span.Slice(y * width, width), DitherRow(y, decoded.Height, flip), flip.X);
            }
        });
    }

    /// <summary>
    /// Associated alpha made straight right before the conversion (libjxl WriteToOutputStage::UnpremulAlpha and
    /// alpha.cc UnpremultiplyAlpha, v0.12.0, BSD-3-Clause): every colour sample × (1 / max(2^-26, alpha)), unclamped. A
    /// gray row (<paramref name="gray"/>, the same span three times) is multiplied once.
    /// </summary>
    internal static void Unpremultiply(Span<float> r, Span<float> g, Span<float> b, ReadOnlySpan<float> a, bool gray)
    {
        a = a[..r.Length];
        if (gray)
        {
            for (int x = 0; x < r.Length; x++) r[x] *= 1.0f / MathF.Max(SmallAlpha, a[x]);
            return;
        }

        g = g[..r.Length];
        b = b[..r.Length];
        for (int x = 0; x < r.Length; x++)
        {
            float multiplier = 1.0f / MathF.Max(SmallAlpha, a[x]);
            r[x] *= multiplier;
            g[x] *= multiplier;
            b[x] *= multiplier;
        }
    }

    // A band's rows of straight colour: the plane rows themselves, or copies unpremultiplied by the alpha row.
    private readonly ref struct StraightRows
    {
        private readonly JxlBufferScope? buffers;
        private readonly Span<float> r, g, b;
        private readonly bool gray;

        public StraightRows(bool premultiplied, bool gray, int width)
        {
            this.gray = gray;
            if (!premultiplied) return;
            buffers = new JxlBufferScope();
            r = buffers.Rent<float>(width, clear: false).AsSpan(0, width);
            g = gray ? r : buffers.Rent<float>(width, clear: false).AsSpan(0, width);
            b = gray ? r : buffers.Rent<float>(width, clear: false).AsSpan(0, width);
        }

        // Replaces the rows by their straight copies when the colour is premultiplied.
        public void Straighten(ref ReadOnlySpan<float> red, ref ReadOnlySpan<float> green, ref ReadOnlySpan<float> blue, ReadOnlySpan<float> alpha)
        {
            if (buffers is null) return;
            red[..r.Length].CopyTo(r);
            if (!gray)
            {
                green[..g.Length].CopyTo(g);
                blue[..b.Length].CopyTo(b);
            }

            Unpremultiply(r, g, b, alpha, gray);
            red = r;
            green = g;
            blue = b;
        }

        public void Dispose() => buffers?.Dispose();
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
