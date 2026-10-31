using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using PictureSharp.Formats.Jxl.Codestream.Frames;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;
using PictureSharp.Formats.Png;

namespace PictureSharp.Formats.Jxl.Fuzz;

/// <summary>This library and libjxl disagree on whether an input is valid.</summary>
public sealed class FuzzAcceptanceException(string message) : Exception(message);

/// <summary>
/// This library accepts what libjxl rejects, and the result agrees with libjxl where it can be checked: a damaged
/// metadata box this library skips, a JPEG only this library reconstructs (its pixels match djxl), or a JPEG only this
/// library transcodes (djxl reconstructs the file byte for byte). Kept for review, not a defect by itself.
/// </summary>
public sealed class FuzzLenientException(string message) : Exception(message);

/// <summary>A lossy encode is clearly worse or larger than cjxl at the same settings.</summary>
public sealed class FuzzQualityException(string message) : Exception(message);

/// <summary>A libjxl tool failed in a way that says nothing about the input (missing, timed out).</summary>
public sealed class FuzzToolException(string message) : Exception(message);

/// <summary>
/// Differential targets for 0.8.8 (docs/design/0.8.8-differential.md): the same input through this library and the
/// pinned libjxl tools (JXL_TOOLS or PATH). Agreement passes; a pixel or byte difference is a
/// <see cref="FuzzMismatchException"/>, a disagreement on validity a <see cref="FuzzAcceptanceException"/>. Inputs this
/// library does not support, or rejects for a resource limit, are not compared.
/// </summary>
public static class DiffTargets
{
    public static readonly string[] Names = ["diff-decode", "diff-encode", "diff-tojpeg", "diff-fromjpeg"];

    // Quality limits of a lossy encode against cjxl at the same distance and effort (ssimulacra2 points, size ratio).
    public const double MaxScoreLoss = 5;
    public const double MaxSizeRatio = 1.25;

    // Below this cjxl score (noise at high distances) neither score nor size compares meaningfully.
    public const double MinScore = 30;

    // Below this cjxl size headers and padding dominate, so the size ratio is not compared.
    public const int MinSizeBytes = 1024;

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(20);

    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();

    /// <summary>Temporary files of this process; the fuzzer sets it below its output directory.</summary>
    public static string WorkDirectory { get; set; } = Path.Combine(Path.GetTempPath(), $"jxl-diff-{Environment.ProcessId}");

    /// <summary>The ICC profile that encode inputs may attach (corpus icc-profile.jxl), set by the fuzzer.</summary>
    public static byte[]? IccProfile { get; set; }

    public static void Run(string target, byte[] data)
    {
        Directory.CreateDirectory(WorkDirectory);
        string stem = Path.Combine(WorkDirectory, $"{FuzzTargets.Hash(data):x8}-{Environment.CurrentManagedThreadId}");
        try
        {
            switch (target)
            {
                case "diff-decode": Decode(data, stem); break;
                case "diff-encode": Encode(data, stem); break;
                case "diff-tojpeg": ToJpeg(data, stem); break;
                case "diff-fromjpeg": FromJpeg(data, stem); break;
                default: throw new ArgumentException($"Unknown target {target}.", nameof(target));
            }
        }
        finally
        {
            // JXL_DIFF_KEEP=1 keeps the files of each run for triage.
            if (Environment.GetEnvironmentVariable("JXL_DIFF_KEEP") != "1")
            {
                foreach (string file in Directory.EnumerateFiles(WorkDirectory, Path.GetFileName(stem) + ".*")) File.Delete(file);
            }
        }
    }

    public static string Tool(string name)
    {
        string? tools = Environment.GetEnvironmentVariable("JXL_TOOLS");
        IEnumerable<string> directories = tools is not null ? [tools] : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        return directories.Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists)
            ?? throw new FuzzToolException($"{name} is not available (JXL_TOOLS or PATH).");
    }

    /// <summary>The version line of every tool, recorded with each campaign.</summary>
    public static IEnumerable<string> Versions() => ((string[])["cjxl", "djxl", "ssimulacra2"]).Select(name =>
    {
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Tool(name))))[..16];
        if (name == "ssimulacra2") return $"{name}: sha256 {hash}";
        (int _, string output) = Exec(name, "--version");
        return $"{name}: {output.Split('\n')[0].Trim()}, sha256 {hash}";
    });

    private static void Decode(byte[] data, string stem)
    {
        string jxl = stem + ".jxl", png = stem + ".png", icc = stem + ".icc";
        File.WriteAllBytes(jxl, data);
        Image? ours = null;
        string? rejected = null, colorSpace = null;
        Comparison comparison = Comparison.Tolerance;
        int bitDepth = 8;
        try
        {
            (colorSpace, comparison, bitDepth) = Rendering(data);
            ours = Image.Load(data, Options(colorSpace is not null));
        }
        catch (ImageFormatException exception) when (IsLimit(exception))
        {
            return;
        }
        catch (ImageFormatException exception)
        {
            rejected = exception.Message;
        }

        using (ours)
        {
            (int code, string log) = Exec("djxl", [jxl, png, "--bits_per_sample=8", "--num_threads=1", "--orig_icc_out=" + icc,
                .. colorSpace is null ? (string[])[] : ["--color_space=" + colorSpace]]);
            bool theirs = code == 0 && File.Exists(png);
            if (ours is null)
            {
                if (theirs) throw new FuzzAcceptanceException($"rejected ({rejected}) but djxl decodes");
                return;
            }

            string? lenient = null;
            if (!theirs && colorSpace is not null && Exec("djxl", jxl, png, "--bits_per_sample=8", "--num_threads=1").Code == 0)
            {
                // djxl converts to sRGB with its colour management, which fails on some profiles; ToSrgb renders XYB
                // as sRGB directly and needs none. Nothing to compare the pixels with.
                throw new FuzzLenientException("djxl decodes, but cannot convert to sRGB; this library renders XYB as sRGB directly");
            }

            if (!theirs)
            {
                // djxl fails on damaged metadata boxes, which this library skips: retry with the codestream boxes only.
                byte[]? stripped = CodestreamOnly(data);
                if (stripped is null || stripped.Length == data.Length) throw new FuzzAcceptanceException($"decoded {ours.Width}x{ours.Height} but djxl fails: {Last(log)}");
                File.WriteAllBytes(jxl, stripped);
                (code, log) = Exec("djxl", [jxl, png, "--bits_per_sample=8", "--num_threads=1", .. colorSpace is null ? (string[])[] : ["--color_space=" + colorSpace]]);
                if (code != 0 || !File.Exists(png)) throw new FuzzAcceptanceException($"decoded {ours.Width}x{ours.Height} but djxl fails, also without metadata: {Last(log)}");
                lenient = "djxl fails on the metadata boxes, which this library skips";
            }

            // ToSrgb of more than 8 bits: the core decodes 16 bits, converts and rounds to 8 without dithering
            // (ImageDecodeService.ReduceToRgba32), so djxl's 16-bit output is rounded the same way.
            bool reduced = colorSpace is not null && bitDepth > 8;
            if (reduced)
            {
                (code, log) = Exec("djxl", [jxl, png, "--bits_per_sample=16", "--num_threads=1", "--color_space=" + colorSpace]);
                if (code != 0) throw new FuzzAcceptanceException($"djxl decodes 8 bits but not 16: {Last(log)}");
            }

            using Image expected = reduced ? Rounded(png) : Image.Load(png);
            string? difference = comparison switch
            {
                Comparison.Exact => Exact(ours, expected),
                Comparison.Jpeg => Tolerance(ours, expected, everySample: true),
                _ => Tolerance(ours, expected),
            };
            if (difference is not null)
            {
                throw new FuzzMismatchException($"pixels ({comparison}{(colorSpace is null ? "" : reduced ? ", ToSrgb rounded" : ", ToSrgb")}): {difference}");
            }

            if (lenient is not null) throw new FuzzLenientException(lenient);

            // Metadata-only reads take no pixel options.
            byte[]? profile = Image.ReadMetadata(data, new DecoderOptions { Configuration = Configuration }).IccProfile;
            byte[]? reference = File.Exists(icc) ? File.ReadAllBytes(icc) : null;
            if (profile is not null && reference is not null && !profile.AsSpan().SequenceEqual(reference))
            {
                throw new FuzzMismatchException($"ICC profile: {profile.Length} bytes, djxl {reference.Length}");
            }
        }
    }

    private static DecoderOptions Options(bool toSrgb) => new()
    {
        Configuration = Configuration,
        MaxPixels = FuzzTargets.MaxPixels,
        ApplyExifOrientation = true,
        ColorConversion = toSrgb ? ColorConversionMode.ToSrgb : ColorConversionMode.None,
        MaxDegreeOfParallelism = 1,
    };

    /// <summary>How decoded pixels are compared with djxl.</summary>
    private enum Comparison
    {
        /// <summary>Every frame Modular without XYB, at most 8 bits: identical.</summary>
        Exact,

        /// <summary>JxlDecodeTests.Tolerance: every sample within 1, at most 0.1% of them (16 in small images) different.</summary>
        Tolerance,

        /// <summary>YCbCr (recompressed JPEG) frames, as JxlJpegTests: every sample within 1.</summary>
        Jpeg,
    }

    /// <summary>
    /// The djxl colour space for an XYB image outside sRGB, which only ToSrgb renders like djxl without a colour
    /// management system (as in JxlDecodeOracleTests), and how the pixels are compared.
    /// </summary>
    private static (string? ColorSpace, Comparison Comparison, int BitDepth) Rendering(byte[] data)
    {
        JxlCodestream codestream = JxlContainer.Parse(data).Codestream;
        JxlHeaders headers = JxlHeadersReader.Read(codestream);
        IReadOnlyList<JxlFrameHeader> frames = JxlDecodeSession.ReadFrameHeaders(codestream, headers);
        JxlColorEncoding color = headers.Metadata.Color;
        bool srgb = !color.WantIcc && color.Gamma == 0 && color.TransferFunction == JxlTransferFunction.Srgb
            && (color.ColorSpace == JxlColorSpace.Gray || color.Primaries == JxlPrimaries.Srgb && color.WhitePoint == JxlWhitePoint.D65);
        bool xyb = frames[^1].ColorTransform == JxlColorTransform.Xyb;
        string? colorSpace = srgb || !xyb ? null : color.ColorSpace == JxlColorSpace.Gray ? "Gra_D65_Rel_SRG" : "RGB_D65_SRG_Rel_SRG";
        Comparison comparison = headers.Metadata.BitDepth.BitsPerSample <= 8
            && frames.All(frame => frame.Encoding == JxlFrameEncoding.Modular && frame.ColorTransform != JxlColorTransform.Xyb) ? Comparison.Exact
            : frames.Any(frame => frame.ColorTransform == JxlColorTransform.YCbCr) ? Comparison.Jpeg
            : Comparison.Tolerance;
        return (colorSpace, comparison, (int)headers.Metadata.BitDepth.BitsPerSample);
    }

    // Input layout: byte 0 flags (bit 0 lossless, 1 16-bit, 2 alpha, 3 gray, 4 ICC, 5-7 pattern), byte 1 effort, byte 2
    // distance, bytes 3-6 width and height, the rest the pixel noise.
    private static readonly float[] Distances = [0.5f, 1, 1.5f, 2, 3, 4, 6, 8, 10, 15];

    private const int MaxSide = 320;

    private static void Encode(byte[] data, string stem)
    {
        if (data.Length < 8) return;
        bool lossless = (data[0] & 1) != 0, wide = (data[0] & 2) != 0, alpha = (data[0] & 4) != 0, gray = (data[0] & 8) != 0;
        bool icc = lossless && (data[0] & 16) != 0 && IccProfile is not null;
        int pattern = data[0] >> 5;
        int effort = 1 + data[1] % 9;
        float distance = lossless ? 0 : Distances[data[2] % Distances.Length];
        int width = 1 + (data[3] | data[4] << 8) % MaxSide, height = 1 + (data[5] | data[6] << 8) % MaxSide;
        using Image source = Synthetic(width, height, wide, alpha, gray, pattern, data.AsSpan(7));
        if (icc) source.Metadata.IccProfile = IccProfile;
        byte[] encoded = source.ToJxl(new JxlEncoderOptions { Configuration = Configuration, Distance = distance, Effort = effort, MaxDegreeOfParallelism = 1 });
        string label = $"{width}x{height} {(wide ? 16 : 8)}-bit{(alpha ? " alpha" : "")}{(gray ? " gray" : "")}{(icc ? " icc" : "")} pattern {pattern} e{effort} d{distance}";
        string jxl = stem + ".jxl", png = stem + ".png", profile = stem + ".icc";
        File.WriteAllBytes(jxl, encoded);
        int bits = lossless && wide ? 16 : 8;
        (int code, string log) = Exec("djxl", jxl, png, $"--bits_per_sample={bits}", "--num_threads=1", "--orig_icc_out=" + profile);
        if (code != 0 || !File.Exists(png)) throw new FuzzAcceptanceException($"{label}: djxl fails on the encoded file: {Last(log)}");
        if (icc && !(File.Exists(profile) && File.ReadAllBytes(profile).AsSpan().SequenceEqual(IccProfile)))
        {
            throw new FuzzMismatchException($"{label}: ICC profile not read back by djxl");
        }

        using Image theirs = Image.Load(png, new DecoderOptions { PreserveBitDepth = bits == 16 });
        if (lossless)
        {
            if (Exact(theirs, source) is { } difference) throw new FuzzMismatchException($"{label}: lossless differs in djxl: {difference}");
            return;
        }

        using (Image ours = Image.Load(encoded, new DecoderOptions { Configuration = Configuration, MaxDegreeOfParallelism = 1 }))
        {
            if (Tolerance(ours, theirs) is { } difference) throw new FuzzMismatchException($"{label}: own decode differs from djxl: {difference}");
        }

        // The quality check covers 8-bit opaque images of at least 64x64 pixels, where ssimulacra2 (which ignores
        // alpha and reads 8 bits here) and the size say something; the others are compared only for decoding.
        if (wide || alpha || width < 64 || height < 64) return;
        string original = stem + ".src.png", reference = stem + ".ref.jxl", referencePng = stem + ".ref.png";
        source.SaveAsPng(original, new PngEncoderOptions { Configuration = Configuration });
        // Lossy efforts 8 and 9 behave as 7 (plan.md, no butteraugli iterations yet): compared with cjxl -e 7.
        string referenceEffort = Math.Min(effort, 7).ToString(CultureInfo.InvariantCulture);
        (code, log) = Exec("cjxl", original, reference, "-d", distance.ToString(CultureInfo.InvariantCulture), "-e", referenceEffort, "--num_threads=1");
        if (code != 0) throw new FuzzToolException($"{label}: cjxl fails: {Last(log)}");
        (code, log) = Exec("djxl", reference, referencePng, "--bits_per_sample=8", "--num_threads=1");
        if (code != 0) throw new FuzzToolException($"{label}: djxl fails on cjxl output: {Last(log)}");
        double score = Score(original, png), referenceScore = Score(original, referencePng);
        long size = encoded.Length, referenceSize = new FileInfo(reference).Length;
        // A trade of size for quality is not a finding: worse by MaxScoreLoss without being 10% smaller, or larger by
        // MaxSizeRatio without being 2 points better.
        bool worse = score < referenceScore - MaxScoreLoss && size > referenceSize * 0.9;
        bool larger = referenceSize >= MinSizeBytes && size > referenceSize * MaxSizeRatio && score < referenceScore + 2;
        if (referenceScore >= MinScore && (worse || larger))
        {
            throw new FuzzQualityException($"{label}: ssimulacra2 {score:F2} at {size} bytes, cjxl {referenceScore:F2} at {referenceSize} bytes");
        }
    }

    private static double Score(string original, string distorted)
    {
        (int code, string output) = Exec("ssimulacra2", original, distorted);
        if (code != 0 || !double.TryParse(output.Trim().Split('\n')[^1], CultureInfo.InvariantCulture, out double score))
        {
            throw new FuzzToolException($"ssimulacra2 fails: {Last(output)}");
        }

        return score;
    }

    /// <summary>Gradients, flat areas, few colours, stripes or noise, with the input bytes as noise.</summary>
    private static Image Synthetic(int width, int height, bool wide, bool alpha, bool gray, int pattern, ReadOnlySpan<byte> noise)
    {
        var image = new Image(width, height, wide ? PixelType.Rgba64 : PixelType.Rgba32);
        int count = width * height;
        Span<ushort> samples = stackalloc ushort[4];
        Span<Rgba64> wideRow = wide ? image.GetPixelMemory<Rgba64>().Span : default;
        Span<Rgba32> narrowRow = wide ? default : image.GetPixelMemory<Rgba32>().Span;
        for (int i = 0; i < count; i++)
        {
            int x = i % width, y = i / width;
            int n = noise.Length == 0 ? 0 : noise[i % noise.Length];
            for (int c = 0; c < 4; c++)
            {
                int value = pattern switch
                {
                    0 => x * (7 + c) + y * 3 + (n >> 4),
                    1 => (x / 16 + y / 16 + c) % 2 * 255,
                    2 => noise.Length == 0 ? 0 : noise[(n % 8 * 3 + c) % noise.Length],
                    3 => (x + c) % 4 < 2 ? 40 : 220,
                    4 => n * (c + 1) + x,
                    5 => (int)((uint)((x * 31 + y * 17 + c) * 2654435761u) >> 24),
                    6 => y * 255 / Math.Max(1, height - 1),
                    _ => 128 + (n - 128) / (c + 1),
                };
                samples[c] = wide ? (ushort)(value * 257 + n * (c + 3) & 0xFFFF) : (ushort)(value & 0xFF);
            }

            if (gray) samples[1] = samples[2] = samples[0];
            ushort max = wide ? ushort.MaxValue : byte.MaxValue;
            samples[3] = alpha ? samples[3] : max;
            if (wide) wideRow[i] = new(samples[0], samples[1], samples[2], samples[3]);
            else narrowRow[i] = new((byte)samples[0], (byte)samples[1], (byte)samples[2], (byte)samples[3]);
        }

        return image;
    }

    private static void ToJpeg(byte[] data, string stem)
    {
        string jxl = stem + ".jxl", jpg = stem + ".jpg";
        File.WriteAllBytes(jxl, data);
        byte[]? ours = null;
        string? rejected = null;
        try
        {
            using var output = new MemoryStream();
            JxlTranscoder.ToJpeg(data, output, Transcode());
            ours = output.ToArray();
        }
        catch (ImageFormatException exception) when (IsLimit(exception))
        {
            return;
        }
        catch (ImageFormatException exception)
        {
            rejected = exception.Message;
        }

        // djxl falls back to pixels when it cannot reconstruct: only "Reconstructed to JPEG." counts.
        (int code, string log) = Exec("djxl", jxl, jpg, "--num_threads=1");
        byte[]? theirs = code == 0 && log.Contains("Reconstructed to JPEG.", StringComparison.Ordinal) && File.Exists(jpg) ? File.ReadAllBytes(jpg) : null;
        if (ours is null && theirs is null) return;
        if (ours is null) throw new FuzzAcceptanceException($"rejected ({rejected}) but djxl reconstructs {theirs!.Length} bytes");
        if (theirs is null)
        {
            // djxl falls back to pixels (and writes them as a new lossy JPEG): the pixels of both decoders must agree.
            if (code != 0 || !File.Exists(jpg)) throw new FuzzAcceptanceException($"reconstructed {ours.Length} bytes but djxl fails: {Last(log)}");
            string png = stem + ".png";
            (code, log) = Exec("djxl", jxl, png, "--bits_per_sample=8", "--num_threads=1");
            if (code != 0) throw new FuzzAcceptanceException($"reconstructed {ours.Length} bytes but djxl fails on pixels: {Last(log)}");
            using Image pixels = Image.Load(png), decoded = Image.Load(data, Options(false));
            if (Tolerance(decoded, pixels, everySample: true) is { } difference)
            {
                throw new FuzzMismatchException($"reconstructed {ours.Length} bytes, djxl only decodes to pixels, and the pixels differ: {difference}");
            }

            throw new FuzzLenientException("djxl only decodes to pixels, this library also reconstructs; the pixels agree");
        }
        if (!ours.AsSpan().SequenceEqual(theirs)) throw new FuzzMismatchException($"JPEG differs: {ours.Length} bytes, djxl {theirs.Length}");
    }

    private static void FromJpeg(byte[] data, string stem)
    {
        string jpg = stem + ".jpg", ourJxl = stem + ".jxl", theirJxl = stem + ".ref.jxl", back = stem + ".back.jpg";
        File.WriteAllBytes(jpg, data);
        byte[]? ours = null;
        string? rejected = null;
        try
        {
            using var output = new MemoryStream();
            JxlTranscoder.FromJpeg(data, output, Transcode());
            ours = output.ToArray();
        }
        catch (ImageFormatException exception) when (IsLimit(exception))
        {
            return;
        }
        catch (ImageFormatException exception)
        {
            rejected = exception.Message;
        }

        (int code, string log) = Exec("cjxl", jpg, theirJxl, "--lossless_jpeg=1", "--num_threads=1");
        bool theirs = code == 0 && File.Exists(theirJxl);
        if (ours is null && !theirs) return;
        if (ours is null)
        {
            // Rejecting is right when libjxl cannot round-trip the file cjxl wrote (a DC value beyond ±2047, say).
            string theirBack = stem + ".ref.jpg";
            bool roundTrips = Exec("djxl", theirJxl, theirBack, "--num_threads=1").Code == 0 && File.Exists(theirBack)
                && File.ReadAllBytes(theirBack).AsSpan().SequenceEqual(data);
            if (roundTrips) throw new FuzzAcceptanceException($"rejected ({rejected}) but cjxl transcodes");
            return;
        }

        // Our file must reconstruct in djxl, also when cjxl refuses the JPEG.
        File.WriteAllBytes(ourJxl, ours);
        (int djxlCode, string djxlLog) = Exec("djxl", ourJxl, back, "--num_threads=1");
        string? failure = djxlCode != 0 || !djxlLog.Contains("Reconstructed to JPEG.", StringComparison.Ordinal)
            ? $"djxl does not reconstruct our {ours.Length} bytes{(theirs ? "" : " (cjxl refuses the JPEG)")}: {Last(djxlLog)}"
            : !File.ReadAllBytes(back).AsSpan().SequenceEqual(data) ? "djxl reconstructs our file to other bytes" : null;
        if (failure is not null)
        {
            // libjxl does not round-trip some JPEGs it accepts: then cjxl's own file fails in djxl as well, while this
            // library reconstructs both files exactly (checked below and by the fromjpeg target).
            string theirBack = stem + ".ref.jpg";
            bool libjxlRoundTrips = theirs && Exec("djxl", theirJxl, theirBack, "--num_threads=1").Code == 0
                && File.Exists(theirBack) && File.ReadAllBytes(theirBack).AsSpan().SequenceEqual(data);
            using var own = new MemoryStream();
            try
            {
                JxlTranscoder.ToJpeg(ours, own, Transcode());
            }
            catch (ImageFormatException exception)
            {
                throw new FuzzMismatchException($"{failure}; this library cannot reconstruct its own file either: {exception.Message}");
            }

            if (libjxlRoundTrips || !theirs || !own.ToArray().AsSpan().SequenceEqual(data)) throw new FuzzMismatchException(failure);
            throw new FuzzLenientException($"libjxl does not round-trip this JPEG (cjxl's file fails in djxl too), this library does: {failure}");
        }
        if (!theirs) throw new FuzzLenientException($"cjxl refuses the JPEG ({Last(log)}), djxl reconstructs our file");
        using var reconstructed = new MemoryStream();
        try
        {
            JxlTranscoder.ToJpeg(File.ReadAllBytes(theirJxl), reconstructed, Transcode());
        }
        catch (ImageFormatException exception)
        {
            throw new FuzzMismatchException($"cjxl's file, which djxl reconstructs, is rejected: {exception.Message}");
        }

        if (!reconstructed.ToArray().AsSpan().SequenceEqual(data))
        {
            throw new FuzzMismatchException($"cjxl's file reconstructs to {reconstructed.Length} bytes instead of {data.Length}");
        }
    }

    /// <summary>The file with only its signature, ftyp, level and codestream boxes; null for a bare or unparsable file.</summary>
    private static byte[]? CodestreamOnly(byte[] data)
    {
        if (data.Length < 12 || data[4] != (byte)'J' || data[5] != (byte)'X' || data[6] != (byte)'L') return null;
        var kept = new List<byte>();
        for (long at = 0; at < data.Length;)
        {
            if (data.Length - at < 8) return null;
            long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)at));
            if (size == 1 && data.Length - at >= 16) size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan((int)at + 8));
            else if (size == 0) size = data.Length - at;
            if (size < 8 || size > data.Length - at) return null;
            string type = System.Text.Encoding.Latin1.GetString(data, (int)at + 4, 4);
            if (type is "JXL " or "ftyp" or "jxll" or "jxlc" or "jxlp") kept.AddRange(data.AsSpan((int)at, (int)size));
            at += size;
        }

        return [.. kept];
    }

    /// <summary>A 16-bit PNG rounded to 8 bits as the core's ReduceToRgba32 does: (v × 255 + 32767) / 65535.</summary>
    private static Image Rounded(string png)
    {
        using Image wide = Image.Load(png, new DecoderOptions { PreserveBitDepth = true });
        var image = new Image(wide.Width, wide.Height);
        ReadOnlySpan<Rgba64> source = wide.PixelType == PixelType.Rgba64 ? wide.GetPixelMemory<Rgba64>().Span : throw new FuzzToolException("djxl wrote no 16-bit PNG");
        Span<Rgba32> target = image.GetPixelMemory<Rgba32>().Span;
        static byte Round(ushort value) => (byte)((value * 255 + 32767) / 65535);
        for (int i = 0; i < source.Length; i++) target[i] = new(Round(source[i].R), Round(source[i].G), Round(source[i].B), Round(source[i].A));
        return image;
    }

    private static JxlTranscoderOptions Transcode() => new() { MaxPixels = FuzzTargets.MaxPixels, MaxDegreeOfParallelism = 1 };

    // MaxPixels and MaxMetadataBytes are this library's budgets; libjxl has none, so such inputs are not compared.
    private static bool IsLimit(ImageFormatException exception) => exception.Message.Contains("exceeds Max", StringComparison.Ordinal)
        || exception.Message.Contains("exceeds DecoderOptions.Max", StringComparison.Ordinal);

    private static string? Exact(Image actual, Image expected)
    {
        if ((actual.Width, actual.Height) != (expected.Width, expected.Height)) return $"{actual.Width}x{actual.Height} instead of {expected.Width}x{expected.Height}";
        if (actual.PixelType != expected.PixelType) return $"{actual.PixelType} instead of {expected.PixelType}";
        ReadOnlySpan<ushort> a = Samples(actual), e = Samples(expected);
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != e[i]) return $"sample {i} (x {i / 4 % actual.Width}, y {i / 4 / actual.Width}, c {i % 4}): {a[i]} instead of {e[i]}";
        }

        return null;
    }

    private static ReadOnlySpan<ushort> Samples(Image image) => image.PixelType == PixelType.Rgba64
        ? MemoryMarshal.Cast<Rgba64, ushort>(image.GetPixelMemory<Rgba64>().Span)
        : MemoryMarshal.AsBytes(image.GetPixelMemory<Rgba32>().Span).ToArray().Select(b => (ushort)b).ToArray();

    /// <summary>
    /// JxlDecodeTests.Tolerance: every 8-bit sample within 1, at most 0.1% of them different; small images may have
    /// 16, which the 0.1% rounds away, and a gray pixel counts once. <paramref name="everySample"/> drops the count (JxlJpegTests).
    /// </summary>
    private static string? Tolerance(Image actual, Image expected, bool everySample = false)
    {
        if ((actual.Width, actual.Height) != (expected.Width, expected.Height)) return $"{actual.Width}x{actual.Height} instead of {expected.Width}x{expected.Height}";
        ReadOnlySpan<byte> a = MemoryMarshal.AsBytes(actual.GetPixelMemory<Rgba32>().Span), e = MemoryMarshal.AsBytes(expected.GetPixelMemory<Rgba32>().Span);
        int different = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int difference = Math.Abs(a[i] - e[i]);
            if (difference > 1) return $"sample {i} (x {i / 4 % actual.Width}, y {i / 4 / actual.Width}, c {i % 4}) differs by {difference}";
            // A gray pixel is one sample, replicated: G and B are not counted again.
            int c = i % 4;
            bool replicated = c is 1 or 2 && a[i - c] == a[i - c + 1] && a[i - c] == a[i - c + 2] && e[i - c] == e[i - c + 1] && e[i - c] == e[i - c + 2];
            if (!replicated) different += difference;
        }

        return !everySample && different > Math.Max(16, a.Length / 1000) ? $"{different} of {a.Length} samples differ" : null;
    }

    private static string Last(string log) => log.Trim().Split('\n')[^1].Trim();

    private static (int Code, string Output) Exec(string tool, params string[] arguments)
    {
        var info = new ProcessStartInfo(Tool(tool)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ToolTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new FuzzToolException($"{tool} over {ToolTimeout.TotalSeconds} s");
        }

        return (process.ExitCode, output.Result + error.Result);
    }
}
