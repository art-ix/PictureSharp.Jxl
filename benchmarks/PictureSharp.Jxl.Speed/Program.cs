using System.Diagnostics;
using System.Runtime;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using PictureSharp;
using PictureSharp.Formats.Jxl;

// Transcoding (scripts/measure-transcoding.py): encode|decode input output [warm].
// Codec cases (scripts/measure-speed.py): list <root> | case <root> <id> [runs].
if (args is ["encode" or "decode", _, _, ..] && args.Length is 3 or 4)
{
    return Transcode(args);
}

if (args is ["list", string listRoot])
{
    foreach (SpeedCase speedCase in SpeedCase.All(listRoot))
    {
        Console.WriteLine($"{speedCase.Id}\t{speedCase.Input}\t{speedCase.Reference}");
    }

    return 0;
}

if (args is ["case", string caseRoot, string id, ..] && args.Length is 3 or 4)
{
    SpeedCase speedCase = SpeedCase.All(caseRoot).Single(c => c.Id == id);
    int runs = args.Length == 4 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 5;
    Console.WriteLine(JsonSerializer.Serialize(speedCase.Measure(runs)));
    return 0;
}

Console.Error.WriteLine("Usage: encode|decode input output [warm] | list <root> | case <root> <id> [runs]");
return 2;

static int Transcode(string[] args)
{
    byte[] input = File.ReadAllBytes(args[1]);
    byte[] Convert()
    {
        using var output = new MemoryStream();
        if (args[0] == "encode") JxlTranscoder.FromJpeg(input, output, SpeedCase.Transcoding);
        else JxlTranscoder.ToJpeg(input, output, SpeedCase.Transcoding);
        return output.ToArray();
    }

    bool warm = args.Length == 4 && args[3] == "warm";
    byte[] result = Convert(); // Single cold operation; with "warm", also the JIT warmup.
    var times = new double[3];
    var allocations = new long[3];
    for (int i = 0; warm && i < times.Length; i++)
    {
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        long start = Stopwatch.GetTimestamp();
        result = Convert();
        times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        allocations[i] = GC.GetTotalAllocatedBytes(precise: true) - allocated;
    }

    Array.Sort(times); Array.Sort(allocations);
    File.WriteAllBytes(args[2], result);
    Console.WriteLine(JsonSerializer.Serialize(new { bytes = result.Length, medianApiMs = warm ? times[1] : (double?)null, allocatedBytes = warm ? allocations[1] : (long?)null }));
    return 0;
}

/// <summary>
/// One measured operation on an input from artifacts/speed-inputs (scripts/make-speed-inputs.py) or samples/private.
/// <see cref="Prepare"/> turns the input bytes into the timed operation (encoding decodes the PNG outside the timing).
/// <see cref="Reference"/> is the equivalent cjxl/djxl command the measurement script times with one thread.
/// </summary>
internal sealed record SpeedCase(string Id, string Input, string Reference, Func<byte[], Func<byte[]>> Prepare)
{
    private static readonly (string Image, float Distance, int Effort)[] EncodeCases =
    [
        ("photo1", 0, 1), ("photo1", 0, 3), ("photo1", 0, 7),
        ("photo1", 1, 1), ("photo1", 1, 3), ("photo1", 1, 5), ("photo1", 1, 7),
        ("screen", 0, 7), ("screen", 1, 7),
        ("photo12", 0, 7), ("photo12", 1, 7),
        ("photo1-16", 0, 7), ("photo1-16", 1, 7), ("screen-16", 0, 7), ("screen-16", 1, 7),
        ("photo12-16", 0, 7), ("photo12-16", 1, 7),
        // Lossless effort tiers (0.8.3) and the widened 8-bit photo (same content as photo1) for the 16/8-bit ratio.
        ("photo1", 0, 2), ("photo1", 0, 4), ("photo1", 0, 5), ("photo1", 0, 6),
        ("screen", 0, 1), ("screen", 0, 2), ("screen", 0, 3), ("screen", 0, 4), ("screen", 0, 5),
        ("photo1w-16", 0, 1), ("photo1w-16", 0, 3), ("photo1w-16", 0, 7), ("photo1w-16", 1, 3), ("photo1w-16", 1, 7),
        ("screen-16", 0, 1), ("screen-16", 0, 3),
        // Small images (0.8.5): fixed per-image cost.
        ("small64", 0, 7), ("small64", 1, 7), ("small256", 0, 7), ("small256", 1, 7),
    ];

    // JXL_THREADS sets MaxDegreeOfParallelism (default -1, the library default); package builds before 0.8.1 have no
    // encoder or transcoder option (source builds define JXL_PARALLEL) and ignore the decoder one.
    internal static readonly int Threads = int.TryParse(Environment.GetEnvironmentVariable("JXL_THREADS"), out int threads) ? threads : -1;

    // JXL_CONCURRENT runs that many copies of the operation at once (Parallel.For), like a server handling independent
    // images; times are per batch.
    internal static readonly int Concurrent = int.TryParse(Environment.GetEnvironmentVariable("JXL_CONCURRENT"), out int concurrent) ? concurrent : 1;

    // JXL_COLLECT=1 (0.8.6) runs a full compacting collection, outside the timing, before every operation, so the
    // process's peak RSS is that of one operation (live data, its garbage and pooled buffers) instead of growing with
    // the number of runs until the GC's large-object budget triggers a collection.
    internal static readonly bool Collect = Environment.GetEnvironmentVariable("JXL_COLLECT") == "1";

    private static readonly DecoderOptions Load = new() { MaxDegreeOfParallelism = Threads };

    private static readonly DecoderOptions Preserve = Load with { PreserveBitDepth = true };

    internal static readonly JxlTranscoderOptions Transcoding = new()
    {
#if JXL_PARALLEL
        MaxDegreeOfParallelism = Threads,
#endif
    };

    public static IEnumerable<SpeedCase> All(string root)
    {
        ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();
        foreach (string name in new[] { "tiny-1x1", "small-64x64", "lossy-e1" })
        {
            string tiny = Path.Combine(root, "tests/assets/corpus", name + ".jxl");
            if (File.Exists(tiny)) yield return new($"decode/{name}", tiny, "djxl {in} {out}.png", Decode(Load));
        }
        string inputs = Path.Combine(root, "artifacts/speed-inputs");
        if (Directory.Exists(inputs))
        {
            foreach (string file in Directory.GetFiles(inputs, "*.jxl").Order(StringComparer.Ordinal))
            {
                yield return new($"decode/{Path.GetFileNameWithoutExtension(file)}", file, "djxl {in} {out}.png", Decode(Load));
            }

            string lossy12 = Path.Combine(inputs, "photo12-d1-e7.jxl");
            if (File.Exists(lossy12))
            {
                yield return new("decode-srgb/photo12-d1-e7", lossy12, "djxl {in} {out}.png --color_space=sRGB",
                    Decode(Load with { ColorConversion = ColorConversionMode.ToSrgb }));
            }

            // 16-bit inputs (name suffix -16) decoded to Rgba64.
            foreach (string file in Directory.GetFiles(inputs, "*-16-*.jxl").Order(StringComparer.Ordinal))
            {
                yield return new($"decode16/{Path.GetFileNameWithoutExtension(file)}", file, "djxl {in} {out}.png --bits_per_sample=16", Decode(Preserve));
            }

            string lossy12Deep = Path.Combine(inputs, "photo12-16-d1-e7.jxl");
            if (File.Exists(lossy12Deep))
            {
                yield return new("decode16-srgb/photo12-16-d1-e7", lossy12Deep, "djxl {in} {out}.png --color_space=sRGB --bits_per_sample=16",
                    Decode(Preserve with { ColorConversion = ColorConversionMode.ToSrgb }));
            }

            foreach ((string image, float distance, int effort) in EncodeCases)
            {
                string file = Path.Combine(inputs, image + ".png");
                if (File.Exists(file))
                {
                    var options = new JxlEncoderOptions
                    {
                        Distance = distance, Effort = effort,
#if JXL_PARALLEL
                        MaxDegreeOfParallelism = Threads,
#endif
                    };
                    string kind = image.EndsWith("-16", StringComparison.Ordinal) ? "encode16" : "encode";
                    yield return new($"{kind}/{image}-d{distance}-e{effort}", file, $"cjxl {{in}} {{out}}.jxl -d {distance} -e {effort}", Encode(options));
                }
            }
        }

        string recompressed = Path.Combine(root, "samples/private/IMG_0160.jxl");
        if (File.Exists(recompressed))
        {
            yield return new("decode/IMG_0160-jpeg", recompressed, "djxl {in} {out}.png", Decode(Load));
            yield return new("tojpeg/IMG_0160-jpeg", recompressed, "djxl {in} {out}.jpg", ToJpeg);
        }
    }

    private static Func<byte[], Func<byte[]>> Decode(DecoderOptions options) => input => () =>
    {
        using Image image = Image.Load(input, options);
        return [];
    };

    private static Func<byte[]> ToJpeg(byte[] input) => () =>
    {
        using var output = new MemoryStream();
        JxlTranscoder.ToJpeg(input, output, Transcoding);
        return output.ToArray();
    };

    private static Func<byte[], Func<byte[]>> Encode(JxlEncoderOptions options) => input =>
    {
        Image source = Image.Load(input, Preserve); // Kept for the lifetime of the process; Rgba64 only for 16-bit PNGs.
        return () => source.ToJxl(options);
    };

    private static void Settle()
    {
        if (!Collect) return;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    public object Measure(int runs)
    {
        Func<byte[]> single = Prepare(File.ReadAllBytes(Input));
        Func<byte[]> operation = Concurrent <= 1 ? single : () =>
        {
            var results = new byte[Concurrent][];
            Parallel.For(0, Concurrent, new ParallelOptions { MaxDegreeOfParallelism = Concurrent }, i => results[i] = single());
            return results[0];
        };
        Settle();
        long coldAllocated = GC.GetTotalAllocatedBytes(precise: true);
        long coldStart = Stopwatch.GetTimestamp();
        byte[] result = operation();
        double coldMs = Stopwatch.GetElapsedTime(coldStart).TotalMilliseconds;
        long coldAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - coldAllocated;
        // Tiered JIT: hot loops start in OSR code and reach tier 1 (with PGO) only after enough calls, so warm up for
        // about two seconds (at least one more call) before timing.
        int warmups = 1;
        for (long warmStart = Stopwatch.GetTimestamp(); warmups < 50 && (warmups < 2 || Stopwatch.GetElapsedTime(warmStart).TotalMilliseconds < 2000); warmups++)
        {
            Settle();
            result = operation();
        }

        var times = new double[runs];
        var allocations = new long[runs];
        int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
        for (int i = 0; i < runs; i++)
        {
            Settle();
            long allocated = GC.GetTotalAllocatedBytes(precise: true);
            long start = Stopwatch.GetTimestamp();
            result = operation();
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocations[i] = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        }

        Array.Sort(times);
        Array.Sort(allocations);
        return new
        {
            id = Id,
            threads = Threads,
            concurrent = Concurrent,
            processors = Environment.ProcessorCount,
            hardware = new { avx2 = Avx2.IsSupported, sse41 = Sse41.IsSupported, fma = Fma.IsSupported },
            bytes = result.Length,
            coldMs,
            coldAllocatedBytes,
            warmups,
            medianMs = times[runs / 2],
            minMs = times[0],
            allocatedBytes = allocations[runs / 2],
            // Without the collections JXL_COLLECT induces between runs.
            gen0PerRun = (double)(GC.CollectionCount(0) - gen0) / runs - (Collect ? 1 : 0),
            gen2PerRun = (double)(GC.CollectionCount(2) - gen2) / runs - (Collect ? 1 : 0),
            collect = Collect,
        };
    }
}
