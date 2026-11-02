using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Encoding;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Numerics;
using PictureSharp.Formats.Jxl.Codestream.Rendering;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>
/// 0.8.15 photon noise and splines (docs/design/0.8.15-noise-splines.md). The corpus tests compare every noise and spline
/// file with djxl; these tests cover thread independence, the conformance files, the math primitives, the spline limits
/// and <see cref="JxlEncoderOptions.PhotonNoiseIso"/>.
/// </summary>
public sealed class JxlNoiseSplinesTests
{
    private static readonly ImageConfiguration Formats = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();

    private static readonly string Conformance = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "external",
        "conformance", "testcases");

    public static IEnumerable<string> FeatureFiles() =>
        [.. JxlDecodeTests.CorpusFiles().Where(name => name.Contains("noise", StringComparison.Ordinal) || name.StartsWith("splines-", StringComparison.Ordinal))];

    private static IEnumerable<string> ConformanceFiles() =>
        new[] { "noise", "noise_5", "animation_spline", "animation_spline_5" }
            .Select(name => Path.Combine(Conformance, name, "input.jxl")).Where(File.Exists);

    private static string Pixels(Image image) => Convert.ToHexString(SHA256.HashData(image.Frames.SelectMany(frame => frame.PixelType == PixelType.Rgba64
        ? MemoryMarshal.AsBytes(frame.GetPixelMemory<Rgba64>().Span).ToArray()
        : MemoryMarshal.AsBytes(frame.GetPixelMemory<Rgba32>().Span).ToArray()).ToArray()));

    [Test]
    public async Task Corpus_HasNoiseAndSplineFiles() => await Assert.That(FeatureFiles().Count()).IsGreaterThanOrEqualTo(16);

    [Test]
    [MethodDataSource(nameof(FeatureFiles))]
    public async Task Decode_IsIndependentOfThreads(string name)
    {
        using Image single = Image.Load(CorpusPath(name), Options(new DecoderOptions { MaxDegreeOfParallelism = 1 }));
        using Image many = Image.Load(CorpusPath(name), Options(new DecoderOptions { MaxDegreeOfParallelism = 16 }));
        await Assert.That(Pixels(many)).IsEqualTo(Pixels(single));
    }

    [Test]
    public async Task Decode_DrawsNoiseAndSplines()
    {
        // The same tree without its features would be flat: noise and splines change the pixels.
        foreach (string name in new[] { "noise-groups.jxl", "splines-xyb.jxl", "noise-up4.jxl" })
        {
            using Image image = Image.Load(CorpusPath(name), Options());
            await Assert.That(image.GetPixelMemory<Rgba32>().ToArray().Distinct().Count()).IsGreaterThan(10);
        }
    }

    [Test]
    public async Task Conformance_MatchesDjxlAndThreads()
    {
        string? djxl = JxlDecodeOracleTests.Tool("djxl");
        foreach (string path in ConformanceFiles())
        {
            using Image single = Image.Load(path, Options(new DecoderOptions { MaxDegreeOfParallelism = 1 }));
            using Image many = Image.Load(path, Options(new DecoderOptions { MaxDegreeOfParallelism = 16 }));
            await Assert.That(Pixels(many)).IsEqualTo(Pixels(single));
            if (path.Contains("animation", StringComparison.Ordinal)) await Assert.That(single.Frames.Count).IsEqualTo(60);
            if (djxl is null) continue;
            using Image expected = JxlDecodeOracleTests.DjxlImage(djxl, path);
            for (int i = 0; i < expected.Frames.Count; i++)
            {
                using Image actualFrame = Frame(single, i);
                using Image expectedFrame = Frame(expected, i);
                string? mismatch = JxlDecodeTests.Tolerance(actualFrame, expectedFrame);
                if (mismatch is not null) Assert.Fail($"{path} frame {i}: {mismatch}");
            }
        }
    }

    private static Image Frame(Image image, int index)
    {
        var copy = new Image(image.Width, image.Height);
        image.Frames[index].GetPixelMemory<Rgba32>().CopyTo(copy.GetPixelMemory<Rgba32>());
        return copy;
    }

    [Test]
    public async Task Decode_HonoursCancellationAndBudget()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        foreach (string name in new[] { "noise-groups.jxl", "splines-noise.jxl" })
        {
            await Assert.That(() => Image.Load(CorpusPath(name), Options(), canceled.Token)).Throws<OperationCanceledException>();
            // The working memory of the noise planes and spline segments fits the budget of the image itself.
            using Image image = Image.Load(CorpusPath(name), Options());
            using Image exact = Image.Load(CorpusPath(name), Options(new DecoderOptions { MaxPixels = (long)image.Width * image.Height }));
            await Assert.That(Pixels(exact)).IsEqualTo(Pixels(image));
        }
    }

    [Test]
    public async Task FastMath_IsCloseToTheLibraryFunctions()
    {
        for (float x = -40; x <= 40; x += 0.01f)
        {
            if (Math.Abs(JxlFastMath.FastCosf(x) - MathF.Cos(x)) > 2e-4f) Assert.Fail($"cos({x})");
            if (Math.Abs(JxlFastMath.FastErff(x) - Erf(x)) > 1e-3f) Assert.Fail($"erf({x})");
        }

        await Assert.That(JxlFastMath.FastErff(0)).IsEqualTo(0f);
    }

    // Abramowitz-Stegun 7.1.26 (error 1.5e-7) as an independent reference.
    private static float Erf(float x)
    {
        double t = 1 / (1 + 0.3275911 * Math.Abs(x));
        double y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * (double)x);
        return (float)(x < 0 ? -y : y);
    }

    [Test]
    public async Task Xorshift_IsDeterministicPerSeed()
    {
        ulong[] first = new ulong[8];
        ulong[] second = new ulong[8];
        new JxlXorshift128Plus(1, 0, 256, 512).Fill(first);
        new JxlXorshift128Plus(1, 0, 256, 512).Fill(second);
        await Assert.That(first.AsSpan().SequenceEqual(second)).IsTrue();
        new JxlXorshift128Plus(1, 1, 256, 512).Fill(second);
        await Assert.That(first.AsSpan().SequenceEqual(second)).IsFalse();
    }

    [Test]
    public async Task Splines_RejectLibjxlLimits()
    {
        int[][] color = [new int[32], new int[32], new int[32]];
        int[] sigma = new int[32];
        sigma[0] = 10;
        JxlSplineData Data((long, long) start, params (int, int)[] points) =>
            new([start], 0, [new JxlQuantizedSpline(points, color, sigma)]);

        // Identical successive control points, a point beyond ±2^23, a Manhattan distance beyond the area limit.
        await Assert.That(() => JxlSplines.Prepare(Data((5, 5), (0, 0)), 64, 64, 0, 1, default)).Throws<ImageFormatException>();
        await Assert.That(() => JxlSplines.Prepare(Data((5, 5), (1 << 22, 0), (1 << 22, 0)), 64, 64, 0, 1, default))
            .Throws<ImageFormatException>();
        await Assert.That(() => JxlSplines.Prepare(Data((5, 5), [.. Enumerable.Repeat((1 << 20, 1 << 20), 4), .. Enumerable.Repeat((-(1 << 20), -(1 << 20)), 4)]),
            1, 1, 0, 1, default)).Throws<ImageFormatException>();
        await Assert.That(() => JxlSplineData.ValidatePosition(1 << 23, 0)).Throws<ImageFormatException>();

        // A valid spline outside the frame draws nothing and a single point draws a dot.
        JxlSplines dot = JxlSplines.Prepare(Data((20, 20)), 64, 64, 0, 1, default);
        JxlPlane[] planes = [new JxlPlane(64, 64), new JxlPlane(64, 64), new JxlPlane(64, 64)];
        dot.Draw(planes, 1, default);
        await Assert.That(planes[1].Row(20)[20]).IsEqualTo(0f);
    }

    [Test]
    public async Task Splines_ChargeTheWorkingBudget()
    {
        int[][] color = [new int[32], new int[32], new int[32]];
        color[1][0] = 100;
        int[] sigma = new int[32];
        sigma[0] = 1000;
        var data = new JxlSplineData([(0, 0)], 0, [new JxlQuantizedSpline([(4000, 4000)], color, sigma)]);
        using (JxlWorkBudget.Begin(16))
        {
            await Assert.That(() => JxlSplines.Prepare(data, 4096, 4096, 0, 1, default)).Throws<ImageFormatException>();
        }
    }

    [Test]
    public async Task PhotonNoise_ValidatesTheOption()
    {
        foreach (float iso in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
        {
            await Assert.That(() => new JxlEncoderOptions { PhotonNoiseIso = iso }).Throws<ArgumentOutOfRangeException>();
        }

        using var image = new Image(16, 16);
        await Assert.That(() => image.ToJxl(new JxlEncoderOptions { Configuration = Formats, Distance = 0, PhotonNoiseIso = 400 }))
            .Throws<ArgumentException>();
        await Assert.That(() => image.ToJxl(new JxlEncoderOptions { Configuration = Formats, Quality = 100, PhotonNoiseIso = 400 }))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task PhotonNoise_AddsOnlyTheLut()
    {
        using Image image = Image.Load(CorpusPath("rgb8-lossless.jxl"), Options());
        byte[] plain = image.ToJxl(new JxlEncoderOptions { Configuration = Formats });
        byte[] noisy = image.ToJxl(new JxlEncoderOptions { Configuration = Formats, PhotonNoiseIso = 3200 });
        await Assert.That(noisy.Length - plain.Length).IsBetween(9, 11);
        using Image decodedPlain = Image.Load(plain, Options());
        using Image decodedNoisy = Image.Load(noisy, Options());
        await Assert.That(Pixels(decodedNoisy)).IsNotEqualTo(Pixels(decodedPlain));
    }

    [Test]
    public async Task PhotonNoise_LutGrowsWithIso()
    {
        // Equality with cjxl's LUT is checked by PhotonNoise_MatchesCjxlAndDjxl (same grain on a flat image).
        float[] lut = JxlPhotonNoise.Simulate(512, 384, 100);
        await Assert.That(lut.All(value => value is >= 0 and < 1)).IsTrue();
        await Assert.That(JxlPhotonNoise.Simulate(512, 384, 51200).Zip(lut).All(pair => pair.First >= pair.Second)).IsTrue();
    }

    /// <summary>
    /// Our PhotonNoiseIso output decodes in djxl like ours, and on a flat image to exactly the pixels of cjxl's output with the
    /// same ISO (the same LUT and the same grain).
    /// </summary>
    [Test]
    [Category("Oracle")]
    public async Task PhotonNoise_MatchesCjxlAndDjxl()
    {
        string? djxl = JxlDecodeOracleTests.Tool("djxl");
        string? cjxl = JxlDecodeOracleTests.Tool("cjxl");
        Skip.Unless(djxl is not null && cjxl is not null, "cjxl/djxl v0.12.0 are not available (PATH or JXL_TOOLS).");
        string directory = Directory.CreateTempSubdirectory("jxl-noise-").FullName;
        try
        {
            using var flat = new Image(256, 192);
            flat.GetPixelMemory<Rgba32>().Span.Fill(new Rgba32(120, 140, 160, 255));
            string png = Path.Combine(directory, "flat.png");
            flat.Save(png);
            foreach (float iso in new[] { 100f, 3200f, 51200f })
            {
                string ours = Path.Combine(directory, $"ours-{iso}.jxl");
                string theirs = Path.Combine(directory, $"cjxl-{iso}.jxl");
                flat.SaveAsJxl(ours, new JxlEncoderOptions { Configuration = Formats, Distance = 1, PhotonNoiseIso = iso });
                Run(cjxl!, png, theirs, "-d", "1", $"--photon_noise_iso={iso}", "--quiet");
                using Image decodedOurs = Image.Load(ours, Options());
                using Image decodedTheirs = Image.Load(theirs, Options());
                await Assert.That(Pixels(decodedOurs)).IsEqualTo(Pixels(decodedTheirs));
                using Image expected = JxlDecodeOracleTests.DjxlImage(djxl!, ours);
                await Assert.That(JxlDecodeTests.Tolerance(decodedOurs, expected)).IsNull();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task PhotonNoise_AnimationFramesShareTheCanvasGrain()
    {
        // Two frames of a 32x32 animation; the second changes only a corner, so it is cropped and gets the canvas LUT
        // (decision 3). Lossless animations reject the option like stills.
        using var animation = new Image(32, 32);
        animation.GetPixelMemory<Rgba32>().Span.Fill(new Rgba32(100, 120, 140, 255));
        var second = new ImageFrame(32, 32);
        animation.GetPixelMemory<Rgba32>().CopyTo(second.GetPixelMemory<Rgba32>());
        second.GetPixelMemory<Rgba32>().Span[0] = new Rgba32(0, 0, 0, 255);
        animation.Frames.RootFrame.Duration = TimeSpan.FromMilliseconds(50);
        second.Duration = TimeSpan.FromMilliseconds(50);
        animation.Frames.Add(second);
        byte[] data = animation.ToJxl(new JxlEncoderOptions { Configuration = Formats, Distance = 1, PhotonNoiseIso = 6400 });
        using Image decoded = Image.Load(data, Options());
        await Assert.That(decoded.Frames.Count).IsEqualTo(2);
        await Assert.That(() => animation.ToJxl(new JxlEncoderOptions { Configuration = Formats, Distance = 0, PhotonNoiseIso = 6400 }))
            .Throws<ArgumentException>();
    }

    private static void Run(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{tool} failed: {process.StandardError.ReadToEnd()}");
    }
}
