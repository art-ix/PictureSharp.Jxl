using System.Security.Cryptography;
using System.Text.Json;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Container;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>
/// Animations (0.8.12, docs/design/0.8.12-animation.md): every frame djxl shows with its default coalescing, with
/// durations from jxlinfo and the loop count, against the corpus oracle (corpus.json, djxl/&lt;name&gt;-&lt;frame&gt;.png for
/// lossy files) and djxl itself (APNG output, Category=Oracle), including the libjxl conformance animations.
/// </summary>
public sealed class JxlAnimationTests
{
    private static readonly string Conformance = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "external",
        "conformance", "testcases");

    private static readonly JsonElement Oracle = JsonDocument.Parse(File.ReadAllText(CorpusPath("corpus.json"))).RootElement.GetProperty("files");

    public static IEnumerable<string> AnimatedFiles() =>
        Oracle.EnumerateObject().Where(file => file.Value.GetProperty("animated").GetBoolean()).Select(file => file.Name);

    private static IEnumerable<string> ConformanceFiles() =>
        new[] { "animation_icos4d", "animation_icos4d_5", "animation_newtons_cradle" }
            .Select(name => Path.Combine(Conformance, name, "input.jxl")).Where(File.Exists);

    private static DecoderOptions Oriented(DecoderOptions? options = null) => Options(options) with { ApplyExifOrientation = true };

    [Test]
    [MethodDataSource(nameof(AnimatedFiles))]
    public async Task Corpus_FramesMatchDjxl(string name)
    {
        JsonElement expected = Oracle.GetProperty(name);
        string[] hashes = [.. expected.GetProperty("frameSha256").EnumerateArray().Select(hash => hash.GetString()!)];
        double[] durations = [.. expected.GetProperty("durationsMs").EnumerateArray().Select(ms => ms.GetDouble())];
        using Image image = Image.Load(CorpusPath(name), Oriented());
        await Assert.That(image.FrameCount).IsEqualTo(hashes.Length);
        await Assert.That(durations.Length).IsEqualTo(hashes.Length);
        await Assert.That(image.LoopCount).IsEqualTo(hashes.Length > 1 ? expected.GetProperty("loops").GetInt32() : 0);
        for (int i = 0; i < hashes.Length; i++)
        {
            // jxlinfo prints single-precision milliseconds with one decimal.
            double ms = image.Frames[i].Duration.TotalMilliseconds;
            if (Math.Abs(ms - durations[i]) > Math.Max(0.05, durations[i] * 1e-6))
                Assert.Fail($"{name} frame {i}: {ms} ms instead of {durations[i]} ms");

            using Image frame = image.Frames.CloneFrame(i);
            string reference = CorpusPath(Path.Combine("djxl", $"{Path.GetFileNameWithoutExtension(name)}-{i}.png"));
            string? mismatch;
            if (File.Exists(reference))
            {
                using Image djxl = Image.Load(reference);
                mismatch = JxlDecodeTests.Tolerance(frame, djxl);
            }
            else
            {
                mismatch = Convert.ToHexStringLower(SHA256.HashData(JxlDecodeOracleTests.Rgba(frame))) == hashes[i] ? null : "pixels differ";
            }

            if (mismatch is not null) Assert.Fail($"{name} frame {i}: {mismatch}");
        }
    }

    [Test]
    [MethodDataSource(nameof(AnimatedFiles))]
    public async Task Identify_CountsTheDisplayedFrames(string name)
    {
        ImageInfo info = Image.Identify(CorpusPath(name), Options());
        using Image image = Image.Load(CorpusPath(name), Options());
        await Assert.That(info.FrameCount).IsEqualTo(image.FrameCount);
        await Assert.That(info.IsAnimated).IsEqualTo(image.IsAnimated);
        await Assert.That(info.FrameCount).IsEqualTo(Oracle.GetProperty(name).GetProperty("frameSha256").GetArrayLength());
    }

    [Test]
    public async Task Durations_AreExactTickFractions()
    {
        // 30000/1001 ticks per second: 5 and 1001 ticks; the last frame has no duration.
        using Image ntsc = Image.Load(CorpusPath("anim-ticks-loops.jxl"), Options());
        await Assert.That(ntsc.Frames.Select(frame => frame.Duration.Ticks))
            .IsEquivalentTo([1_668_333L, 334_000_333L, 0L]);
        await Assert.That(ntsc.LoopCount).IsEqualTo(3);
        // 1/100 s ticks and the largest duration (2^32 - 1 ticks).
        using Image longest = Image.Load(CorpusPath("anim-16bit.jxl"), Options());
        await Assert.That(longest.Frames[0].Duration).IsEqualTo(TimeSpan.FromMilliseconds(4_294_967_295L * 10));
        await Assert.That(longest.Frames[1].Duration).IsEqualTo(TimeSpan.FromMilliseconds(70));
    }

    [Test]
    public async Task SingleDisplayedFrame_IsAStillImage()
    {
        // An animation header with loops = 5 but one frame to show: like PNG and GIF, no loop count.
        using Image image = Image.Load(CorpusPath("anim-single.jxl"), Options());
        await Assert.That((image.FrameCount, image.IsAnimated, image.LoopCount)).IsEqualTo((1, false, 0));
        ImageInfo info = Image.Identify(CorpusPath("anim-single.jxl"), Options());
        await Assert.That((info.FrameCount, info.IsAnimated)).IsEqualTo(((int?)1, false));
    }

    [Test]
    public async Task FirstFrameOnly_ReturnsTheFirstFrame()
    {
        foreach (string name in AnimatedFiles())
        {
            using Image all = Image.Load(CorpusPath(name), Options());
            using Image first = Image.Load(CorpusPath(name), Options(new DecoderOptions { FirstFrameOnly = true }));
            await Assert.That(first.FrameCount).IsEqualTo(1);
            await Assert.That(first.LoopCount).IsEqualTo(0);
            await Assert.That(first.Frames[0].Duration).IsEqualTo(all.Frames[0].Duration);
            using Image expected = all.Frames.CloneFrame(0);
            await Assert.That(first.GetPixelByteSpan().SequenceEqual(expected.GetPixelByteSpan())).IsTrue();
        }
    }

    [Test]
    public async Task MaxFrames_CountsEveryStoredFrame()
    {
        // Five frames in the codestream, three shown; FirstFrameOnly still reads the remaining headers.
        string path = CorpusPath("anim-ticks-loops.jxl");
        foreach (bool firstOnly in new[] { false, true })
        {
            using Image image = Image.Load(path, Options(new DecoderOptions { MaxFrames = 5, FirstFrameOnly = firstOnly }));
            await Assert.That(image.FrameCount).IsEqualTo(firstOnly ? 1 : 3);
            await Assert.That(() => Image.Load(path, Options(new DecoderOptions { MaxFrames = 4, FirstFrameOnly = firstOnly })))
                .Throws<ImageFormatException>();
        }

        await Assert.That(Image.Identify(path, Options(new DecoderOptions { MaxFrames = 5 })).FrameCount).IsEqualTo(3);
        await Assert.That(() => Image.Identify(path, Options(new DecoderOptions { MaxFrames = 4 }))).Throws<ImageFormatException>();
    }

    [Test]
    public async Task MaxPixels_ChargesEveryFrame()
    {
        // Four 32x24 frames; 16-bit frames count twice.
        string crop = CorpusPath("anim-crop.jxl");
        using (Image image = Image.Load(crop, Options(new DecoderOptions { MaxPixels = 4 * 32 * 24 })))
        {
            await Assert.That(image.FrameCount).IsEqualTo(4);
        }

        await Assert.That(() => Image.Load(crop, Options(new DecoderOptions { MaxPixels = 4 * 32 * 24 - 1 }))).Throws<ImageFormatException>();
        string wide = CorpusPath("anim-16bit.jxl");
        using (Image image = Image.Load(wide, Options(new DecoderOptions { PreserveBitDepth = true, MaxPixels = 2 * 3 * 32 * 20 })))
        {
            await Assert.That((image.FrameCount, image.PixelType)).IsEqualTo((3, PixelType.Rgba64));
        }

        await Assert.That(() => Image.Load(wide, Options(new DecoderOptions { PreserveBitDepth = true, MaxPixels = 2 * 3 * 32 * 20 - 1 })))
            .Throws<ImageFormatException>();
    }

    [Test]
    public async Task Truncated_FailsWithAFormatError()
    {
        byte[] data = File.ReadAllBytes(CorpusPath("anim-crop.jxl"));
        foreach (int length in new[] { data.Length - 1, data.Length * 3 / 4, data.Length / 2 })
        {
            foreach (bool firstOnly in new[] { false, true })
            {
                await Assert.That(() => Image.Load(data.AsMemory(0, length), Options(new DecoderOptions { FirstFrameOnly = firstOnly })))
                    .Throws<ImageFormatException>();
            }
        }
    }

    [Test]
    public async Task Cancellation_StopsTheDecode()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.That(() => Image.Load(CorpusPath("anim-crop.jxl"), Options(), canceled.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Frames_AreIdenticalAtOneAndManyThreads()
    {
        foreach (string path in AnimatedFiles().Select(CorpusPath).Concat(ConformanceFiles()))
        {
            using Image single = Image.Load(path, Options(new DecoderOptions { MaxDegreeOfParallelism = 1 }));
            using Image many = Image.Load(path, Options(new DecoderOptions { MaxDegreeOfParallelism = 16 }));
            await Assert.That(many.FrameCount).IsEqualTo(single.FrameCount);
            for (int i = 0; i < single.FrameCount; i++)
            {
                if (!single.Frames[i].GetPixelByteSpan().SequenceEqual(many.Frames[i].GetPixelByteSpan()))
                    Assert.Fail($"{Path.GetFileName(path)} frame {i} differs between 1 and 16 threads");
            }
        }
    }

    [Test]
    public async Task ToSrgb_KeepsOneColourDescriptionForAllFrames()
    {
        // The first frame takes the still path (rendered to sRGB directly), the others are blended canvases converted
        // afterwards; the image is sRGB, so the pixels equal the default decode within dithering.
        using Image converted = Image.Load(CorpusPath("anim-vardct-alpha.jxl"), Options(new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb }));
        using Image plain = Image.Load(CorpusPath("anim-vardct-alpha.jxl"), Options());
        await Assert.That(converted.FrameCount).IsEqualTo(4);
        for (int i = 0; i < plain.FrameCount; i++)
        {
            using Image a = converted.Frames.CloneFrame(i);
            using Image b = plain.Frames.CloneFrame(i);
            string? mismatch = JxlDecodeTests.Tolerance(a, b);
            if (mismatch is not null) Assert.Fail($"frame {i}: {mismatch}");
        }
    }

    [Test]
    public async Task Layers_StillRejectAnimations()
    {
        await Assert.That(() => JxlLayers.Load(CorpusPath("anim-crop.jxl"), Options())).Throws<NotSupportedException>();
    }

    /// <summary>Every frame matches djxl's APNG output (oriented, 8 bits): lossless exactly, lossy within the tolerance.</summary>
    [Test]
    [Category("Oracle")]
    public async Task Frames_MatchDjxl()
    {
        string? djxl = JxlDecodeOracleTests.Tool("djxl");
        Skip.Unless(djxl is not null, "djxl v0.12.0 is not available (PATH or JXL_TOOLS).");
        List<string> files = [.. AnimatedFiles().Select(CorpusPath), .. ConformanceFiles()];
        foreach (string path in files)
        {
            using Image image = Image.Load(path, Oriented());
            using Image expected = JxlDecodeOracleTests.DjxlImage(djxl!, path);
            string label = Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path);
            await Assert.That(image.FrameCount).IsEqualTo(expected.FrameCount);
            if (image.FrameCount > 1) await Assert.That(image.LoopCount).IsEqualTo(expected.LoopCount);
            bool lossy = JxlHeadersReader.Read(JxlContainer.Parse(File.ReadAllBytes(path)).Codestream).Metadata.XybEncoded;
            for (int i = 0; i < image.FrameCount; i++)
            {
                using Image actual = image.Frames.CloneFrame(i);
                using Image reference = expected.Frames.CloneFrame(i);
                string? mismatch = lossy ? JxlDecodeTests.Tolerance(actual, reference)
                    : JxlDecodeOracleTests.Rgba(actual).SequenceEqual(JxlDecodeOracleTests.Rgba(reference)) ? null : "pixels differ";
                if (mismatch is not null) Assert.Fail($"{label} frame {i}: {mismatch}");
            }
        }

        Console.WriteLine($"Animations like djxl: {files.Count} files");
    }

    [Test]
    public async Task Conformance_DurationsMatchTestJson()
    {
        foreach (string path in ConformanceFiles())
        {
            using JsonDocument test = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "test.json")));
            double[] seconds = [.. test.RootElement.GetProperty("frames").EnumerateArray().Select(frame => frame.GetProperty("duration").GetDouble())];
            using Image image = Image.Load(path, Options());
            await Assert.That(image.Frames.Select(frame => Math.Round(frame.Duration.TotalSeconds, 6))).IsEquivalentTo(seconds.Select(s => Math.Round(s, 6)));
        }
    }

    [Test]
    public async Task Conformance_SplineAnimationsAreNotSupportedYet()
    {
        foreach (string name in new[] { "animation_spline", "animation_spline_5" })
        {
            string path = Path.Combine(Conformance, name, "input.jxl");
            if (!File.Exists(path)) continue;
            await Assert.That(() => Image.Load(path, Options())).Throws<NotSupportedException>().WithMessageContaining("splines");
        }
    }
}
