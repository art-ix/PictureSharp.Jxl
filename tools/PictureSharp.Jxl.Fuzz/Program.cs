using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PictureSharp;
using PictureSharp.Formats.Jxl;
using PictureSharp.Formats.Jxl.Fuzz;

// Mutation fuzzer for 0.8.7 (docs/design/0.8.7-fuzzing.md), with the differential diff-* targets of 0.8.8
// (docs/design/0.8.8-differential.md). Every input is derived from (seed, iteration), so a
// finding is reproduced by `generate` from those two numbers as well as by `repro` from the saved file.
//   fuzz <target> [--seed S] [--time SEC | --iterations N] [--workers W] [--timeout SEC] [--heap-mb M] [--corpus DIR] [--out DIR]
//   repro <target> <file>...
//   generate <target> --seed S --iteration I --output FILE [--corpus DIR]
//   seeds <target> [--corpus DIR]
//   export-seeds <target> --output DIR [--corpus DIR]   (AFL++ input directory)
//   render <file.jxl> <out.png> [--srgb 1] [--wide 1]   this library's decode as the diff targets compare it (triage;
//                                        --wide keeps more than 8 bits)
//   headers <file.jxl>   the decoded image and frame headers (triage)
//   transcode <in.jpg> <out.jxl> | transcode <in.jxl> <out.jpg>   FromJpeg or ToJpeg as the diff targets run it
//   boundary [--output DIR]   SIMD/dimension boundary set: hashes to stdout, files to DIR (scripts/fuzz.sh boundary)
// --extra DIR adds the *.jxl files of DIR (for example the boundary set) to the seeds of the JPEG XL targets.
// A diff-* target uses every fourth iteration for an unchanged seed, in turn, so that valid files are compared too.
// Exit codes of a worker: 0 done, 3 hang (input saved); any other code is a crash, which the supervisor locates.
string command = args.Length > 0 ? args[0] : "";
var options = Options(args.Skip(1));
try
{
    if (command == "boundary") return Boundary.Run(Console.Out, options.GetValueOrDefault("output")) == 0 ? 0 : 1;
    if (command == "repro") return Repro(Target(), Positional().Skip(1));
    if (command == "render") return Render([.. Positional()], options.ContainsKey("srgb"), options.ContainsKey("wide"));
    if (command == "transcode") return Transcode([.. Positional()]);
    if (command == "headers") return FuzzTargets.PrintHeaders(File.ReadAllBytes(Positional().First()), Console.Out);
    string corpus = options.GetValueOrDefault("corpus") ?? DefaultCorpus();
    return command switch
    {
        "fuzz" => Supervise(Target(), options, corpus),
        "worker" => Worker(Target(), options, corpus),
        "generate" => Generate(Target(), options, corpus),
        "seeds" => ListSeeds(Target(), corpus),
        "export-seeds" => ExportSeeds(Target(), options, corpus),
        _ => Usage(),
    };
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

string Target()
{
    string target = Positional().FirstOrDefault() ?? throw new ArgumentException("Missing target.");
    string[] names = [.. FuzzTargets.Names, .. DiffTargets.Names];
    return names.Contains(target) ? target : throw new ArgumentException($"Unknown target {target}; one of {string.Join(", ", names)}.");
}

IEnumerable<string> Positional()
{
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal)) i++;
        else yield return args[i];
    }
}

static Dictionary<string, string> Options(IEnumerable<string> arguments)
{
    var result = new Dictionary<string, string>();
    string[] list = [.. arguments];
    for (int i = 0; i < list.Length; i++)
    {
        if (!list[i].StartsWith("--", StringComparison.Ordinal)) continue;
        if (i + 1 >= list.Length) throw new ArgumentException($"Missing value of {list[i]}.");
        result[list[i][2..]] = list[++i];
    }

    return result;
}

static long Number(Dictionary<string, string> options, string name, long fallback) =>
    options.TryGetValue(name, out string? value) ? long.Parse(value, CultureInfo.InvariantCulture) : fallback;

static string DefaultCorpus()
{
    // The repository's corpus, found upwards from the tool's directory.
    for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
    {
        string candidate = Path.Combine(directory, "tests", "assets", "corpus");
        if (Directory.Exists(candidate)) return candidate;
    }

    throw new ArgumentException("No corpus found; pass --corpus DIR.");
}

static int Usage()
{
    Console.Error.WriteLine("Usage: fuzz|repro|generate|seeds <target> ...; targets: " + string.Join(", ", [.. FuzzTargets.Names, .. DiffTargets.Names]));
    return 2;
}

List<FuzzSeed> LoadSeeds(string target, string corpus)
{
    List<FuzzSeed> seeds;
    bool jpeg = target is "fromjpeg" or "diff-fromjpeg";
    if (target == "entropy")
    {
        seeds = [.. FuzzTargets.EntropySeeds().Select(s => new FuzzSeed(s.Name, s.Data))];
    }
    else if (target == "diff-encode")
    {
        seeds = [.. EncodeSeeds()];
    }
    else
    {
        string pattern = jpeg ? "*.jpg" : "*.jxl";
        seeds = [.. Directory.EnumerateFiles(corpus, pattern, SearchOption.AllDirectories)
            .Where(file => new FileInfo(file).Length <= 256 * 1024)
            .Order(StringComparer.Ordinal)
            .Select(file => new FuzzSeed(Path.GetRelativePath(corpus, file), File.ReadAllBytes(file)))];
        // JPEG reconstruction is compared only where there is something to reconstruct.
        if (target == "diff-tojpeg") seeds.RemoveAll(s => s.Data.AsSpan().IndexOf("jbrd"u8) < 0);
        if (!jpeg && options.GetValueOrDefault("extra") is { } extra)
        {
            seeds.AddRange(Directory.EnumerateFiles(extra, "*.jxl").Where(file => new FileInfo(file).Length <= 256 * 1024)
                .Order(StringComparer.Ordinal).Select(file => new FuzzSeed("extra/" + Path.GetFileName(file), File.ReadAllBytes(file))));
        }
    }

    if (seeds.Count == 0) throw new ArgumentException($"No seeds for {target} in {corpus}.");
    if (target is not ("entropy" or "diff-encode") && !jpeg)
    {
        foreach (FuzzSeed seed in seeds) seed.TraceFields(() => { using Image _ = Image.Load(seed.Data, FuzzTargets.DecodeOptions(seed.Data)); });
    }

    return seeds;
}

// Encode inputs (DiffTargets.Encode): each flag combination and pattern, a few efforts, distances and sizes.
static IEnumerable<FuzzSeed> EncodeSeeds()
{
    var random = new FuzzRandom(88, 0);
    for (int flags = 0; flags < 256; flags++)
    {
        int width = 1 + random.Below(flags % 3 == 0 ? 40 : 300), height = 1 + random.Below(flags % 5 == 0 ? 40 : 300);
        byte[] data = [(byte)flags, (byte)random.Below(9), (byte)random.Below(10), (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8),
            .. Enumerable.Range(0, 64 + random.Below(512)).Select(_ => (byte)random.Next())];
        yield return new FuzzSeed($"encode-{flags:x2}", data);
    }
}

byte[] Input(string target, List<FuzzSeed> seeds, ulong seed, long iteration) =>
    target.StartsWith("diff-", StringComparison.Ordinal) && iteration % 4 == 0
        ? seeds[(int)((ulong)(iteration / 4) % (ulong)seeds.Count)].Data
        : Mutator.Generate(seeds, seed, iteration);

int ListSeeds(string target, string corpus)
{
    foreach (FuzzSeed seed in LoadSeeds(target, corpus)) Console.WriteLine($"{seed.Name}\t{seed.Data.Length}\t{seed.Fields.Length} fields");
    return 0;
}

int ExportSeeds(string target, Dictionary<string, string> options, string corpus)
{
    string output = options.GetValueOrDefault("output") ?? throw new ArgumentException("Missing --output.");
    Directory.CreateDirectory(output);
    foreach (FuzzSeed seed in LoadSeeds(target, corpus)) File.WriteAllBytes(Path.Combine(output, seed.Name.Replace('/', '_')), seed.Data);
    return 0;
}

int Generate(string target, Dictionary<string, string> options, string corpus)
{
    byte[] input = Input(target, LoadSeeds(target, corpus), (ulong)Number(options, "seed", 1), Number(options, "iteration", 0));
    File.WriteAllBytes(options.GetValueOrDefault("output") ?? throw new ArgumentException("Missing --output."), input);
    return 0;
}

static int Render(string[] files, bool srgb, bool wide)
{
    if (files.Length != 2) throw new ArgumentException("render <file.jxl> <out.png>");
    var formats = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();
    using Image image = Image.Load(files[0], new DecoderOptions
    {
        Configuration = formats, ApplyExifOrientation = true, MaxDegreeOfParallelism = 1,
        ColorConversion = srgb ? ColorConversionMode.ToSrgb : ColorConversionMode.None, PreserveBitDepth = wide,
    });
    PictureSharp.Formats.Png.PngImageExtensions.SaveAsPng(image, files[1], new PictureSharp.Formats.Png.PngEncoderOptions { Configuration = formats });
    return 0;
}

static int Transcode(string[] files)
{
    if (files.Length != 2) throw new ArgumentException("transcode <in> <out>");
    using FileStream output = File.Create(files[1]);
    var options = new JxlTranscoderOptions { MaxPixels = FuzzTargets.MaxPixels, MaxDegreeOfParallelism = 1 };
    if (files[0].EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) JxlTranscoder.FromJpeg(File.ReadAllBytes(files[0]), output, options);
    else JxlTranscoder.ToJpeg(File.ReadAllBytes(files[0]), output, options);
    return 0;
}

static int Repro(string target, IEnumerable<string> files)
{
    if (target.StartsWith("diff-", StringComparison.Ordinal)) PrepareDiff(Path.GetTempPath(), DefaultCorpus());
    int findings = 0;
    foreach (string file in files)
    {
        byte[] input = File.ReadAllBytes(file);
        try
        {
            FuzzTargets.Run(target, input);
            Console.WriteLine($"{file}: pass");
        }
        catch (Exception exception) when (FuzzTargets.IsAllowed(exception))
        {
            Console.WriteLine($"{file}: pass ({exception.GetType().Name}: {exception.Message})");
        }
        catch (Exception exception)
        {
            findings++;
            Console.WriteLine($"{file}: FINDING\n{exception}");
        }
    }

    return findings == 0 ? 0 : 1;
}

int Worker(string target, Dictionary<string, string> options, string corpus)
{
    ulong seed = (ulong)Number(options, "seed", 1);
    long start = Number(options, "start", 0);
    long end = Number(options, "end", long.MaxValue);
    long deadline = Number(options, "deadline", long.MaxValue);
    var timeout = TimeSpan.FromSeconds(Number(options, "timeout", 10));
    string output = options.GetValueOrDefault("out") ?? throw new ArgumentException("Missing --out.");
    List<FuzzSeed> seeds = LoadSeeds(target, corpus);
    var findings = new Findings(Path.Combine(output, target));
    PrepareDiff(output, corpus);

    // The watchdog saves an input that runs too long and ends the process; the supervisor goes on after it.
    long current = -1;
    long started = 0;
    byte[] running = [];
    var lockObject = new Lock();
    var watchdog = new Thread(() =>
    {
        while (true)
        {
            Thread.Sleep(250);
            lock (lockObject)
            {
                if (current >= 0 && Stopwatch.GetElapsedTime(started) > timeout)
                {
                    findings.Save("hang", $"hang-{FuzzTargets.Hash(running):x8}", running, $"seed {seed} iteration {current}: over {timeout.TotalSeconds} s");
                    Console.WriteLine($"hang {current}");
                    Console.Out.Flush();
                    Environment.Exit(3);
                }
            }
        }
    }) { IsBackground = true };
    watchdog.Start();

    long last = start;
    for (long iteration = start; iteration < end && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < deadline; iteration++)
    {
        byte[] input = Input(target, seeds, seed, iteration);
        lock (lockObject) (current, started, running) = (iteration, Stopwatch.GetTimestamp(), input);
        try
        {
            FuzzTargets.Run(target, input);
        }
        catch (Exception exception) when (FuzzTargets.IsAllowed(exception))
        {
        }
        catch (Exception exception)
        {
            string kind = exception switch
            {
                FuzzMismatchException => "mismatch",
                FuzzAcceptanceException => "accept",
                FuzzLenientException => "lenient",
                FuzzQualityException => "quality",
                FuzzToolException => "tool",
                _ => "exception",
            };
            findings.Save(kind, Signature(exception), input, $"seed {seed} iteration {iteration}\n{exception}");
        }

        lock (lockObject) current = -1;
        last = iteration + 1;
        if (last % 256 == 0)
        {
            Console.WriteLine($"progress {iteration + 1}");
            Console.Out.Flush();
        }
    }

    Console.WriteLine($"done {Math.Min(end, Math.Max(start, last))}");
    return 0;
}

// Type and the first library frames: one file per distinct bug, not per input.
static string Signature(Exception exception)
{
    Exception inner = exception;
    while (inner is ImageFormatException or NotSupportedException && inner.InnerException is not null) inner = inner.InnerException;
    IEnumerable<string> frames = (inner.StackTrace ?? "").Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.Contains("PictureSharp.Formats.Jxl.", StringComparison.Ordinal) && !line.Contains(".Fuzz.", StringComparison.Ordinal))
        .Select(line => line.Split(" in ")[0])
        .Take(3);
    // Differential findings have no library frames: their message with the numbers left out tells them apart.
    string message = inner switch
    {
        FuzzAcceptanceException or FuzzLenientException or FuzzQualityException or FuzzToolException => "|" + Normalize(inner.Message),
        FuzzMismatchException => "|" + Normalize(inner.Message.Split(':')[0]),
        _ => "",
    };
    string text = inner.GetType().FullName + "|" + string.Join("|", frames) + message;
    return $"{inner.GetType().Name}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12]}";
}

// Numbers and quoted box types vary between inputs of one finding.
static string Normalize(string message) => Regex.Replace(Regex.Replace(message, @"'[^']*'", "'?'"), @"\d+([.,]\d+)?", "#");

// A crash has no exception object: the library frames of its stderr, or the input when there are none.
static string CrashSignature(string stderr, byte[] input)
{
    string[] frames = [.. stderr.Split('\n').Select(line => line.Trim())
        .Where(line => line.Contains("PictureSharp.Formats.Jxl.", StringComparison.Ordinal) && !line.Contains(".Fuzz.", StringComparison.Ordinal))
        .Take(3)];
    return frames.Length == 0
        ? $"crash-{FuzzTargets.Hash(input):x8}"
        : $"crash-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", frames))))[..12]}";
}

int Supervise(string target, Dictionary<string, string> options, string corpus)
{
    ulong seed = (ulong)Number(options, "seed", 1);
    int workers = (int)Number(options, "workers", 1);
    long iterations = Number(options, "iterations", long.MaxValue);
    long deadline = options.ContainsKey("time") ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Number(options, "time", 0) : long.MaxValue;
    bool diff = target.StartsWith("diff-", StringComparison.Ordinal);
    string output = Path.GetFullPath(options.GetValueOrDefault("out") ?? Path.Combine("artifacts", diff ? "fuzz-0.8.8" : "fuzz-0.8.7"));
    var findings = new Findings(Path.Combine(output, target));
    List<FuzzSeed> seeds = LoadSeeds(target, corpus);
    Console.WriteLine($"{target}: {seeds.Count} seeds, {seeds.Sum(s => s.Fields.Length)} traced fields, {workers} workers, seed {seed}, out {output}");
    if (diff) foreach (string version in DiffTargets.Versions()) Console.WriteLine(version);

    // A worker that cannot start would turn every iteration into a crash: check one empty run first.
    (int startup, _, _, string startupErrors) = RunWorker(target, seed * 1000, 0, 0, long.MaxValue, options, corpus, output);
    if (startup != 0)
    {
        Console.Error.WriteLine($"{target}: the worker does not start (exit {startup}):\n{startupErrors}");
        return 1;
    }

    long total = 0;
    var watch = Stopwatch.StartNew();
    var threads = Enumerable.Range(0, workers).Select(w => new Thread(() =>
    {
        // Worker w has its own seed; its iterations run from 0.
        ulong workerSeed = seed * 1000 + (ulong)w;
        long next = 0;
        while (next < iterations && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < deadline)
        {
            long counted = next;
            (int code, long reached, long? hang, string stderr) = RunWorker(target, workerSeed, next, iterations, deadline, options, corpus, output,
                progress => Interlocked.Add(ref total, progress - Interlocked.Exchange(ref counted, progress)));
            Interlocked.Add(ref total, reached - counted);
            if (code == 0) break;
            if (code == 3 && hang is long h)
            {
                next = h + 1;
                continue;
            }

            // A crash within 256 iterations after the last progress report: bisect the range for the input.
            long low = reached, high = Math.Min(reached + 256, iterations);
            bool Crashes(long from, long to) => RunWorker(target, workerSeed, from, to, long.MaxValue, options, corpus, output).Code is not (0 or 3);
            if (!Crashes(low, high))
            {
                Console.WriteLine($"worker {w}: exit {code} after {reached}, not reproduced");
                next = high;
                continue;
            }

            while (high - low > 1)
            {
                long middle = low + (high - low) / 2;
                if (Crashes(low, middle)) high = middle;
                else low = middle;
            }

            byte[] input = Input(target, seeds, workerSeed, low);
            findings.Save("crash", CrashSignature(stderr, input), input, $"seed {workerSeed} iteration {low}: exit code {code}\n{stderr}");
            next = low + 1;
        }
    })).ToList();
    threads.ForEach(t => t.Start());
    for (int second = 1; threads.Any(t => t.IsAlive); second++)
    {
        Thread.Sleep(1000);
        if (second % 60 != 0) continue;
        long done = Interlocked.Read(ref total);
        Console.WriteLine($"{watch.Elapsed:hh\\:mm\\:ss} {done} execs ({done / Math.Max(1, watch.Elapsed.TotalSeconds):F0}/s), findings: {findings.Summary()}");
    }

    Console.WriteLine($"{target}: {total} execs in {watch.Elapsed:hh\\:mm\\:ss}, findings: {findings.Summary()}");
    return 0;
}

// Temporary files per worker below the output directory, and the ICC profile encode inputs attach.
static void PrepareDiff(string output, string corpus)
{
    DiffTargets.WorkDirectory = Path.Combine(output, "tmp", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    string icc = Path.Combine(corpus, "icc-profile.jxl");
    if (File.Exists(icc)) DiffTargets.IccProfile = Image.ReadMetadata(icc, new DecoderOptions { Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build() }).IccProfile;
}

static (int Code, long Reached, long? Hang, string Stderr) RunWorker(string target, ulong seed, long start, long end, long deadline,
    Dictionary<string, string> options, string corpus, string output, Action<long>? progress = null)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    // A self-contained or apphost launch: Environment.ProcessPath is the executable; under `dotnet x.dll` it is dotnet.
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!) == "dotnet") info.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
    foreach (string argument in (string[])["worker", target, "--seed", seed.ToString(CultureInfo.InvariantCulture),
        "--start", start.ToString(CultureInfo.InvariantCulture), "--end", end.ToString(CultureInfo.InvariantCulture),
        "--deadline", deadline.ToString(CultureInfo.InvariantCulture), "--timeout", options.GetValueOrDefault("timeout") ?? "10",
        "--corpus", corpus, "--out", output, .. options.TryGetValue("extra", out string? extra) ? (string[])["--extra", extra] : []])
    {
        info.ArgumentList.Add(argument);
    }

    // The heap limit makes runaway allocations an OutOfMemoryException, which is a finding.
    info.Environment["DOTNET_GCHeapHardLimit"] = "0x" + (Number(options, "heap-mb", 2048) << 20).ToString("X", CultureInfo.InvariantCulture);
    using Process process = Process.Start(info)!;
    // The end of stderr goes into the report of a crash.
    var stderr = new Queue<string>();
    process.ErrorDataReceived += (_, e) =>
    {
        if (e.Data is null) return;
        lock (stderr)
        {
            stderr.Enqueue(e.Data);
            if (stderr.Count > 60) stderr.Dequeue();
        }
    };
    process.BeginErrorReadLine();
    long reached = start;
    long? hang = null;
    while (process.StandardOutput.ReadLine() is { } line)
    {
        if (line.StartsWith("progress ", StringComparison.Ordinal))
        {
            reached = long.Parse(line[9..], CultureInfo.InvariantCulture);
            progress?.Invoke(reached);
        }
        else if (line.StartsWith("hang ", StringComparison.Ordinal)) hang = long.Parse(line[5..], CultureInfo.InvariantCulture);
        else if (line.StartsWith("done ", StringComparison.Ordinal)) reached = long.Parse(line[5..], CultureInfo.InvariantCulture);
    }

    process.WaitForExit();
    lock (stderr) return (process.ExitCode, hang is long h ? h + 1 : reached, hang, string.Join("\n", stderr));
}

/// <summary>Findings on disk: kind/signature.bin with the smallest input seen, .txt with its details, .hits with every hit.</summary>
sealed class Findings(string directory)
{
    private readonly Lock lockObject = new();

    public void Save(string kind, string signature, byte[] input, string details)
    {
        lock (lockObject)
        {
            string folder = Path.Combine(directory, kind);
            Directory.CreateDirectory(folder);
            string bin = Path.Combine(folder, signature + ".bin");
            // Workers are separate processes: every hit is one appended line, the input is replaced only when smaller.
            File.AppendAllText(Path.Combine(folder, signature + ".hits"), details.Split('\n')[0] + "\n");
            if (File.Exists(bin) && new FileInfo(bin).Length <= input.Length) return;
            File.WriteAllBytes(bin, input);
            File.WriteAllText(Path.Combine(folder, signature + ".txt"), details + "\n");
        }
    }

    public string Summary()
    {
        if (!Directory.Exists(directory)) return "none";
        IEnumerable<string> kinds = Directory.EnumerateDirectories(directory)
            .Select(folder => $"{Path.GetFileName(folder)} {Directory.EnumerateFiles(folder, "*.bin").Count()}");
        string text = string.Join(", ", kinds);
        return text.Length == 0 ? "none" : text;
    }
}
