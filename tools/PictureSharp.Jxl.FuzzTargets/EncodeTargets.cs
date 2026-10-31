using System.Text;

namespace PictureSharp.Formats.Jxl.Fuzz;

/// <summary>
/// The 0.8.9 encoder targets (docs/design/0.8.9-resilience.md), moved from 0.8.7. <c>encode</c> builds an image and
/// options from a parameter blob, encodes it at 1 and 4 threads and again on reused buffers, and decodes the result;
/// <c>edit</c> rewrites the metadata of a corpus file. Neither needs libjxl (that is <c>diff-encode</c>), so both run
/// under AFL++ too.
/// </summary>
public static class EncodeTargets
{
    public static readonly string[] Names = ["encode", "edit"];

    public static void Run(string target, byte[] data)
    {
        switch (target)
        {
            case "encode": Encode(data); break;
            case "edit": Edit(data); break;
            default: throw new ArgumentException($"Unknown target {target}.", nameof(target));
        }
    }

    // Lossy distances; quality instead when byte 8 says so.
    private static readonly float[] Distances = [0.1f, 0.5f, 1, 1.5f, 2, 3, 4, 6, 8, 10, 15, 20, 25];

    private const int MaxSide = 2048;

    // Efforts 8 and 9 search far more: their images stay small enough for a few inputs per second.
    private const int MaxSlowSide = 256;

    /// <summary>
    /// Input layout: byte 0 flags as <c>diff-encode</c> (bit 0 lossless, 1 16-bit, 2 alpha, 3 gray, 4 ICC, 5-7
    /// pattern), byte 1 effort, byte 2 distance, bytes 3-6 width and height, byte 7 (bits 0-1 size shift, 2-3 alpha
    /// kind, 4 CompressMetadata, 5 EXIF, 6 XMP, 7 metadata bytes random), byte 8 (bit 0 quality instead of distance,
    /// the rest the quality), the rest the pixel noise.
    /// </summary>
    private static void Encode(byte[] data)
    {
        if (data.Length < 9) return;
        bool lossless = (data[0] & 1) != 0, wide = (data[0] & 2) != 0, alpha = (data[0] & 4) != 0, gray = (data[0] & 8) != 0;
        bool icc = (data[0] & 16) != 0 && DiffTargets.IccProfile is not null;
        int pattern = data[0] >> 5;
        int effort = 1 + data[1] % 9;
        int shift = data[7] & 3;
        int maxSide = effort >= 8 ? MaxSlowSide : MaxSide;
        int width = 1 + ((data[3] | data[4] << 8) % maxSide >> shift), height = 1 + ((data[5] | data[6] << 8) % maxSide >> shift);
        int alphaKind = data[7] >> 2 & 3;
        bool compress = (data[7] & 16) != 0, exif = (data[7] & 32) != 0, xmp = (data[7] & 64) != 0, random = (data[7] & 128) != 0;
        ReadOnlySpan<byte> noise = data.AsSpan(9);
        using Image source = DiffTargets.Synthetic(width, height, wide, alpha, gray, pattern, noise);
        if (alpha) ShapeAlpha(source, alphaKind);
        if (icc) source.Metadata.IccProfile = DiffTargets.IccProfile;
        if (exif) source.Metadata.Exif = random ? [.. noise[..Math.Min(noise.Length, 64)]] : Exif(noise);
        if (xmp) source.Metadata.Xmp = random ? [.. noise[..Math.Min(noise.Length, 64)]] : Xmp(noise);
        bool quality = !lossless && (data[8] & 1) != 0;
        JxlEncoderOptions Options(int threads) => new()
        {
            Configuration = FuzzTargets.Configuration, Effort = effort, CompressMetadata = compress, MaxDegreeOfParallelism = threads,
            Distance = lossless ? 0f : quality ? null : Distances[data[2] % Distances.Length],
            Quality = quality ? Math.Min(data[8] >> 1, 100) : JxlEncoderOptions.DefaultQuality,
        };

        string label = $"{width}x{height} {(wide ? 16 : 8)}-bit{(alpha ? $" alpha{alphaKind}" : "")}{(gray ? " gray" : "")}{(icc ? " icc" : "")}"
            + $"{(exif ? " exif" : "")}{(xmp ? " xmp" : "")}{(random && (exif || xmp) ? " random" : "")} pattern {pattern} e{effort} "
            + (lossless ? "d0" : quality ? $"q{Options(1).Quality}" : $"d{Options(1).Distance}");
        byte[] encoded;
        try
        {
            encoded = source.ToJxl(Options(1));
        }
        catch (Exception exception) when (FuzzTargets.IsAllowed(exception) && random && (exif || xmp))
        {
            // Random metadata may be refused; the same image without it must encode.
            source.Metadata.Exif = null;
            source.Metadata.Xmp = null;
            exif = xmp = false;
            encoded = Encoded(source, Options(1), label);
        }
        catch (Exception exception) when (FuzzTargets.IsAllowed(exception))
        {
            throw new FuzzMismatchException($"{label}: encoding fails: {exception.Message}");
        }

        if (!Encoded(source, Options(4), label).AsSpan().SequenceEqual(encoded)) throw new FuzzMismatchException($"{label}: 4 threads write other bytes");
        if (!Encoded(source, Options(1), label).AsSpan().SequenceEqual(encoded)) throw new FuzzMismatchException($"{label}: a second encode writes other bytes");

        Image decoded;
        try
        {
            decoded = Image.Load(encoded, new DecoderOptions { Configuration = FuzzTargets.Configuration, PreserveBitDepth = wide, MaxDegreeOfParallelism = 1 });
        }
        catch (Exception exception) when (FuzzTargets.IsAllowed(exception))
        {
            throw new FuzzMismatchException($"{label}: the encoded file does not decode: {exception.Message}");
        }

        using (decoded)
        {
            if ((decoded.Width, decoded.Height) != (width, height)) throw new FuzzMismatchException($"{label}: decoded as {decoded.Width}x{decoded.Height}");
            if (decoded.PixelType != source.PixelType) throw new FuzzMismatchException($"{label}: decoded as {decoded.PixelType}");
            if (lossless && DiffTargets.Exact(decoded, source) is { } difference) throw new FuzzMismatchException($"{label}: lossless differs: {difference}");
            if (!lossless && alpha && AlphaDifference(decoded, source) is { } alphaDifference)
                throw new FuzzMismatchException($"{label}: lossy alpha differs: {alphaDifference}");
            // Lossy frames may store a describable profile as an enumerated encoding, so the ICC comes back byte for
            // byte only from lossless frames.
            if (lossless && icc && !Same(decoded.Metadata.IccProfile, source.Metadata.IccProfile)) throw new FuzzMismatchException($"{label}: ICC differs");
            // The decoder skips EXIF that is not a TIFF stream (JxlMetadataReader.TryGetTiff), which random bytes are not.
            byte[]? decodedExif = decoded.Metadata.Exif;
            if (exif && !Same(decodedExif, source.Metadata.Exif) && !(random && decodedExif is null)) throw new FuzzMismatchException($"{label}: EXIF differs");
            if (xmp && !Same(decoded.Metadata.Xmp, source.Metadata.Xmp)) throw new FuzzMismatchException($"{label}: XMP differs");
        }
    }

    private static byte[] Encoded(Image image, JxlEncoderOptions options, string label)
    {
        try
        {
            return image.ToJxl(options);
        }
        catch (Exception exception) when (FuzzTargets.IsAllowed(exception))
        {
            throw new FuzzMismatchException($"{label}: encoding fails: {exception.Message}");
        }
    }

    // Opaque, binary (0 or max), the noise as it is, or fully transparent with colour left under it.
    private static void ShapeAlpha(Image image, int kind)
    {
        if (image.PixelType == PixelType.Rgba64)
        {
            foreach (ref Rgba64 pixel in image.GetPixelMemory<Rgba64>().Span)
            {
                pixel = pixel with { A = kind switch { 0 => ushort.MaxValue, 1 => pixel.A >= 0x8000 ? ushort.MaxValue : (ushort)0, 3 => 0, _ => pixel.A } };
            }
        }
        else
        {
            foreach (ref Rgba32 pixel in image.GetPixelMemory<Rgba32>().Span)
            {
                pixel = pixel with { A = kind switch { 0 => byte.MaxValue, 1 => pixel.A >= 0x80 ? byte.MaxValue : (byte)0, 3 => 0, _ => pixel.A } };
            }
        }
    }

    private static string? AlphaDifference(Image actual, Image expected)
    {
        int count = actual.Width * actual.Height;
        for (int i = 0; i < count; i++)
        {
            int a = actual.PixelType == PixelType.Rgba64 ? actual.GetPixelMemory<Rgba64>().Span[i].A : actual.GetPixelMemory<Rgba32>().Span[i].A;
            int e = expected.PixelType == PixelType.Rgba64 ? expected.GetPixelMemory<Rgba64>().Span[i].A : expected.GetPixelMemory<Rgba32>().Span[i].A;
            if (a != e) return $"pixel {i}: {a} instead of {e}";
        }

        return null;
    }

    // A little-endian TIFF with one IFD: orientation 1 and a 24-character ASCII description from the noise.
    private static byte[] Exif(ReadOnlySpan<byte> noise)
    {
        byte[] text = new byte[25];
        for (int i = 0; i < 24; i++) text[i] = (byte)(0x20 + (i < noise.Length ? noise[i] : i) % 0x5F);
        var exif = new List<byte>();
        exif.AddRange("II*\0"u8.ToArray());
        exif.AddRange(BitConverter.GetBytes(8u));
        exif.AddRange(BitConverter.GetBytes((ushort)2));
        exif.AddRange([0x12, 0x01, 3, 0, 1, 0, 0, 0, 1, 0, 0, 0]);
        exif.AddRange([0x0E, 0x01, 2, 0, .. BitConverter.GetBytes((uint)text.Length), .. BitConverter.GetBytes(8u + 2 + 24 + 4)]);
        exif.AddRange(BitConverter.GetBytes(0u));
        exif.AddRange(text);
        return [.. exif];
    }

    private static byte[] Xmp(ReadOnlySpan<byte> noise) => Encoding.UTF8.GetBytes(
        $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\" dc:source=\"{Convert.ToHexString(noise[..Math.Min(noise.Length, 16)])}\"/></x:xmpmeta>");

    private static bool Same(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    /// <summary>
    /// <c>Image.EditMetadata</c> on a corpus file. The hash of the input picks each block: kept, removed, set to a
    /// valid value or (EXIF, XMP) to random bytes. When the file decodes, the edit must succeed (random blocks may be
    /// refused) and the result must decode to the same pixels with the blocks that were set.
    /// </summary>
    private static void Edit(byte[] data)
    {
        var options = new DecoderOptions { Configuration = FuzzTargets.Configuration, MaxPixels = FuzzTargets.MaxPixels, MaxDegreeOfParallelism = 1 };
        string before;
        try
        {
            using Image original = Image.Load(data, options);
            before = PixelHash(original);
        }
        catch (Exception exception) when (FuzzTargets.IsAllowed(exception))
        {
            return;
        }

        // A recompressed JPEG refuses every edit: its reconstruction data would no longer match (docs/metadata.md).
        if (Container.JxlContainer.Parse(data, includeJpegReconstruction: true).JpegReconstruction is not null)
        {
            try
            {
                Image.EditMetadata(data, m => m.Xmp = Xmp(data), options);
            }
            catch (Exception exception) when (FuzzTargets.IsAllowed(exception) && exception.Message.Contains("JPEG reconstruction", StringComparison.Ordinal))
            {
                return;
            }

            throw new FuzzMismatchException("edit of a recompressed JPEG succeeds");
        }

        uint h = FuzzTargets.Hash(data);
        ReadOnlySpan<byte> noise = data.AsSpan(data.Length / 2, Math.Min(64, data.Length - data.Length / 2));
        int exifChoice = (int)(h % 4), xmpChoice = (int)(h >> 2 & 3);
        // The ICC profile is only removed: a new profile changes how XYB frames render.
        bool removeIcc = (h & 16) != 0;
        byte[]? exif = exifChoice switch { 2 => Exif(noise), 3 => [.. noise], _ => null };
        byte[]? xmp = xmpChoice switch { 2 => Xmp(noise), 3 => [.. noise], _ => null };
        void Apply(ImageMetadata m, bool randomAllowed)
        {
            if (exifChoice == 1 || (exifChoice == 3 && !randomAllowed)) m.Exif = null;
            else if (exif is not null) m.Exif = exif;
            if (xmpChoice == 1 || (xmpChoice == 3 && !randomAllowed)) m.Xmp = null;
            else if (xmp is not null) m.Xmp = xmp;
        }

        byte[] edited;
        bool random = exifChoice == 3 || xmpChoice == 3;
        try
        {
            edited = Image.EditMetadata(data, m => Apply(m, randomAllowed: true), options);
        }
        catch (ImageFormatException exception) when (exception.Message.StartsWith("Malformed EXIF", StringComparison.Ordinal))
        {
            // PictureSharp parses the existing EXIF before any edit (ImageMetadataService.Rewrite) and refuses a damaged
            // one, for every format; the decoder passes such EXIF on as it is.
            return;
        }
        catch (Exception exception) when (FuzzTargets.IsAllowed(exception) && random)
        {
            edited = Edited(data, m => Apply(m, randomAllowed: false), options);
            exifChoice = exifChoice == 3 ? 1 : exifChoice;
            xmpChoice = xmpChoice == 3 ? 1 : xmpChoice;
        }
        catch (Exception exception) when (FuzzTargets.IsAllowed(exception))
        {
            throw new FuzzMismatchException($"edit (exif {exifChoice}, xmp {xmpChoice}) fails on a file that decodes: {exception.Message}");
        }

        if (removeIcc)
        {
            // Removing the profile of a file that needs it to describe its samples may be refused; that is checked
            // by the tests, here only that the refusal is a format error.
            try
            {
                edited = Image.EditMetadata(edited, m => m.IccProfile = null, options);
            }
            catch (Exception exception) when (FuzzTargets.IsAllowed(exception))
            {
                removeIcc = false;
            }
        }

        try
        {
            using Image result = Image.Load(edited, options);
            if (!removeIcc && PixelHash(result) != before) throw new FuzzMismatchException($"edit (exif {exifChoice}, xmp {xmpChoice}) changes the pixels");
            ImageMetadata m = Image.ReadMetadata(edited, options);
            if (exifChoice == 1 && m.Exif is not null) throw new FuzzMismatchException("edit: EXIF not removed");
            if (exifChoice == 2 && !Same(m.Exif, exif)) throw new FuzzMismatchException("edit: EXIF differs");
            if (exifChoice == 3 && m.Exif is not null && !Same(m.Exif, new ImageMetadata { Exif = exif }.Exif)) throw new FuzzMismatchException("edit: random EXIF differs");
            if (xmpChoice == 1 && m.Xmp is not null) throw new FuzzMismatchException("edit: XMP not removed");
            if (xmpChoice is 2 or 3 && !Same(m.Xmp, xmp)) throw new FuzzMismatchException($"edit: XMP {xmpChoice} differs");
        }
        catch (Exception exception) when (FuzzTargets.IsAllowed(exception))
        {
            throw new FuzzMismatchException($"edit (exif {exifChoice}, xmp {xmpChoice}, icc removed {removeIcc}) writes a file that does not decode: {exception.Message}");
        }
    }

    private static byte[] Edited(byte[] data, Action<ImageMetadata> edit, DecoderOptions options)
    {
        try
        {
            return Image.EditMetadata(data, edit, options);
        }
        catch (Exception exception) when (FuzzTargets.IsAllowed(exception))
        {
            throw new FuzzMismatchException($"edit fails on a file that decodes: {exception.Message}");
        }
    }

    private static string PixelHash(Image image) => FuzzTargets.Describe(image).Split(" icc=")[0];
}
