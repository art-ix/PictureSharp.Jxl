using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PictureSharp.Formats.Jxl.Codestream.Encoding;
using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Modular;
using PictureSharp.Formats.Jxl.Container;

namespace PictureSharp.Formats.Jxl.Fuzz;

/// <summary>Two runs of one input that should agree did not.</summary>
public sealed class FuzzMismatchException(string message) : Exception(message);

/// <summary>
/// The fuzz targets and their pass rules (docs/design/0.8.7-fuzzing.md). A run passes when it returns or throws an
/// allowed exception; any other exception, including one wrapped inside an allowed one, is a finding.
/// </summary>
public static class FuzzTargets
{
    public static readonly string[] Names = ["container", "headers", "decode", "entropy", "tojpeg", "fromjpeg"];

    public const long MaxPixels = 1 << 22;

    // The second run of a decode or transcode uses several threads and buffers the first run returned to the pool.
    private const int Threads = 4;

    // JXL_FUZZ_SINGLE=1 (scripts/fuzz.sh --single) skips the second run: AFL++ on one core reaches deeper paths faster,
    // while the mutation fuzzer keeps the 1 vs 4 thread comparison.
    private static readonly bool Single = Environment.GetEnvironmentVariable("JXL_FUZZ_SINGLE") == "1";

    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();

    public static bool IsAllowed(Exception exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (e is not (ImageFormatException or NotSupportedException)) return false;
        }

        return true;
    }

    public static void Run(string target, byte[] data)
    {
        switch (target)
        {
            case "container": Container(data); break;
            case "headers": Headers(data); break;
            case "decode": Decode(data); break;
            case "entropy": Entropy(data); break;
            case "tojpeg": ToJpeg(data); break;
            case "fromjpeg": FromJpeg(data); break;
            default: throw new ArgumentException($"Unknown target {target}.", nameof(target));
        }
    }

    /// <summary>The decoder options of an input, derived from its bytes so that a saved input reproduces alone.</summary>
    public static DecoderOptions DecodeOptions(ReadOnlySpan<byte> data, int threads = 1)
    {
        // ToSrgb needs the ICC profile, so only None may ignore metadata.
        uint h = Hash(data);
        bool toSrgb = (h & 2) != 0;
        return new DecoderOptions
        {
            Configuration = Configuration,
            MaxPixels = MaxPixels,
            PreserveBitDepth = (h & 1) != 0,
            ColorConversion = toSrgb ? ColorConversionMode.ToSrgb : ColorConversionMode.None,
            IgnoreMetadata = !toSrgb && (h & 4) != 0,
            MaxDegreeOfParallelism = threads,
        };
    }

    private static void Container(byte[] data)
    {
        JxlContainer container = JxlContainer.Parse(data, includeMetadata: true, includeJpegReconstruction: true);
        foreach (JxlMetadataBox box in container.MetadataBoxes) box.Read(1 << 24, null);
        _ = container.Codestream.Length;
    }

    private static void Headers(byte[] data)
    {
        // Directly first, so that a stack trace is not wrapped by the core.
        JxlHeadersReader.Read(JxlContainer.Parse(data).Codestream);
        Image.Identify(data, DecodeOptions(data));
    }

    private static void Decode(byte[] data)
    {
        string first;
        using (Image image = Image.Load(data, DecodeOptions(data))) first = Describe(image);
        if (Single) return;
        string second;
        using (Image image = Image.Load(data, DecodeOptions(data, Threads))) second = Describe(image);
        if (first != second) throw new FuzzMismatchException($"1 thread: {first}; {Threads} threads: {second}");
    }

    private static void Entropy(byte[] data)
    {
        if (data.Length < 2) return;
        var reader = new JxlBitReader(new JxlCodestream(data.AsMemory(2)));
        int parameter = data[1];
        switch (data[0] % 5)
        {
            case 0:
                int contexts = Math.Max(1, parameter);
                JxlEntropyCode code = JxlEntropyCode.Read(ref reader, contexts);
                using (var symbols = new JxlSymbolReader(code, ref reader))
                {
                    for (int i = 0; i < EntropySymbols; i++) symbols.Read(i % contexts, ref reader);
                    _ = symbols.IsFinalStateValid;
                }

                break;
            case 1: JxlMaTree.Read(ref reader, 1 << 16); break;
            case 2: JxlPrefixCode.Read(ref reader, 1 + parameter * 128); break;
            case 3: JxlEntropyCode.ReadContextMap(ref reader, new byte[Math.Max(1, parameter)]); break;
            default: JxlHybridUint.Read(ref reader, 5 + parameter % 11); break;
        }
    }

    private const int EntropySymbols = 4096;

    private static void ToJpeg(byte[] data)
    {
        byte[] first = ToJpeg(data, 1);
        if (Single) return;
        byte[] second = ToJpeg(data, Threads);
        if (!first.AsSpan().SequenceEqual(second)) throw new FuzzMismatchException($"ToJpeg differs: {first.Length} bytes at 1 thread, {second.Length} at {Threads}");
    }

    private static void FromJpeg(byte[] data)
    {
        byte[] jxl = FromJpeg(data, 1);
        byte[] parallel = Single ? jxl : FromJpeg(data, Threads);
        if (!jxl.AsSpan().SequenceEqual(parallel)) throw new FuzzMismatchException($"FromJpeg differs: {jxl.Length} bytes at 1 thread, {parallel.Length} at {Threads}");
        // Accepted JPEGs are reconstructed byte for byte.
        byte[] jpeg = ToJpeg(jxl, 1);
        if (!jpeg.AsSpan().SequenceEqual(data)) throw new FuzzMismatchException($"reconstruction differs: {data.Length} bytes in, {jpeg.Length} out");
    }

    private static byte[] ToJpeg(byte[] jxl, int threads)
    {
        using var output = new MemoryStream();
        JxlTranscoder.ToJpeg(jxl, output, Transcode(threads));
        return output.ToArray();
    }

    private static byte[] FromJpeg(byte[] jpeg, int threads)
    {
        using var output = new MemoryStream();
        JxlTranscoder.FromJpeg(jpeg, output, Transcode(threads));
        return output.ToArray();
    }

    private static JxlTranscoderOptions Transcode(int threads) => new() { MaxPixels = MaxPixels, MaxDegreeOfParallelism = threads };

    private static string Describe(Image image)
    {
        ReadOnlySpan<byte> pixels = image.PixelType == PixelType.Rgba64
            ? MemoryMarshal.AsBytes(image.GetPixelMemory<Rgba64>().Span)
            : MemoryMarshal.AsBytes(image.GetPixelMemory<Rgba32>().Span);
        ImageMetadata m = image.Metadata;
        return $"{image.Width}x{image.Height} {image.PixelType} {Sha(pixels)} icc={Sha(m.IccProfile)} exif={Sha(m.Exif)} xmp={Sha(m.Xmp)}";
    }

    private static string Sha(ReadOnlyMemory<byte>? bytes) => bytes is { } b ? Sha(b.Span) : "-";

    private static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16];

    public static uint Hash(ReadOnlySpan<byte> data)
    {
        uint h = 2166136261;
        foreach (byte b in data) h = (h ^ b) * 16777619;
        return h;
    }

    /// <summary>Entropy seeds: valid ANS and prefix streams from the encoder, mode 0 with the contexts in byte 1.</summary>
    public static IEnumerable<(string Name, byte[] Data)> EntropySeeds()
    {
        foreach (int contexts in (int[])[1, 2, 5, 17])
        {
            foreach ((string name, Func<int, uint> value) in ((string, Func<int, uint>)[])[
                ("zero", _ => 0), ("small", i => (uint)(i * 7 % 13)), ("skewed", i => i % 97 == 0 ? 3u : 0u),
                ("large", i => (uint)(i * 2654435761u >> (i % 29))), ("runs", i => (uint)(i / 64 % 5))])
            {
                var tokens = new JxlTokens();
                for (int i = 0; i < EntropySymbols; i++) tokens.Add(i % contexts, value(i));
                var writer = new JxlBitWriter();
                JxlEntropyEncoder.WriteStandalone(writer, contexts, tokens);
                yield return ($"entropy-{name}-c{contexts}", [0, (byte)contexts, .. writer.ToArray()]);
            }
        }

        // The other modes start from the same streams, which their readers take as arbitrary bits.
        var map = new JxlBitWriter();
        JxlEntropyEncoder.WriteContextMap(map, [0, 1, 1, 2, 0, 2, 3, 3], 4);
        yield return ("entropy-contextmap", [3, 8, .. map.ToArray()]);
    }
}
