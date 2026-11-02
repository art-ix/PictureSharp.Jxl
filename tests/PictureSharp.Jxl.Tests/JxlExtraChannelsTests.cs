using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Container;
using PictureSharp.Formats.Jxl.Integration;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>
/// 0.8.16 extra channels (docs/design/0.8.16-extra-channels.md): premultiplied alpha returned straight, spot colours
/// rendered, other extra channels dropped, CMYK and float rejected. The oracle is jxl_dec_straight (tests/assets/tools,
/// libjxl 0.12.0 with unpremultiplying on), which renders like djxl; the corpus tests also compare with corpus.json.
/// </summary>
public sealed class JxlExtraChannelsTests
{
    private static readonly string Conformance = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "external",
        "conformance", "testcases");

    private static readonly string[] Rejected = ["extra-black.jxl", "extra-float.jxl"];

    public static IEnumerable<string> ExtraChannelFiles() =>
    [
        .. JxlDecodeTests.CorpusFiles().Where(name => name.StartsWith("premul-", StringComparison.Ordinal)
            || name.StartsWith("spot-", StringComparison.Ordinal) || name.StartsWith("extra-", StringComparison.Ordinal)).Except(Rejected),
    ];

    private static bool IsLossy(string path) =>
        JxlHeadersReader.Read(JxlContainer.Parse(File.ReadAllBytes(path)).Codestream).Metadata.XybEncoded;

    private static string Pixels(Image image) => Convert.ToHexString(SHA256.HashData(image.Frames.SelectMany(frame => frame.PixelType == PixelType.Rgba64
        ? MemoryMarshal.AsBytes(frame.GetPixelMemory<Rgba64>().Span).ToArray()
        : MemoryMarshal.AsBytes(frame.GetPixelMemory<Rgba32>().Span).ToArray()).ToArray()));

    [Test]
    public async Task Corpus_HasExtraChannelFiles() => await Assert.That(ExtraChannelFiles().Count()).IsGreaterThanOrEqualTo(24);

    [Test]
    [MethodDataSource(nameof(ExtraChannelFiles))]
    public async Task Decode_IsIndependentOfThreadsAndOptions(string name)
    {
        foreach (DecoderOptions variant in new[]
        {
            new DecoderOptions(), new DecoderOptions { PreserveBitDepth = true }, new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb },
        })
        {
            using Image single = Image.Load(CorpusPath(name), Options(variant with { MaxDegreeOfParallelism = 1 }));
            using Image many = Image.Load(CorpusPath(name), Options(variant with { MaxDegreeOfParallelism = 16 }));
            await Assert.That(Pixels(many)).IsEqualTo(Pixels(single));
        }
    }

    [Test]
    [Category("Oracle")]
    [MethodDataSource(nameof(ExtraChannelFiles))]
    public async Task Decode_MatchesTheStraightOracle(string name)
    {
        string path = CorpusPath(name);
        List<Image>? expected = JxlDecodeOracleTests.StraightFrames(path);
        Skip.Unless(expected is not null, "jxl_dec_straight is not available (PATH or JXL_TOOLS).");
        using Image image = Image.Load(path, Options(new DecoderOptions { ApplyExifOrientation = true }));
        try
        {
            await CompareFrames(image.Frames.Select((_, i) => image.Frames.CloneFrame(i)).ToList(), expected!, name, IsLossy(path));
        }
        finally
        {
            expected!.ForEach(frame => frame.Dispose());
        }
    }

    [Test]
    [Category("Oracle")]
    [MethodDataSource(nameof(ExtraChannelFiles))]
    public async Task Decode_PreserveBitDepth_MatchesTheStraightOracle(string name)
    {
        // 16-bit output: lossless exactly (to 1/65535), lossy within 1/255 like the 8-bit comparison.
        string path = CorpusPath(name);
        using Image image = Image.Load(path, Options(new DecoderOptions { ApplyExifOrientation = true, PreserveBitDepth = true }));
        Skip.Unless(image.PixelType == PixelType.Rgba64, "8-bit image.");
        List<Image>? expected = JxlDecodeOracleTests.StraightFrames(path, wide: true);
        Skip.Unless(expected is not null, "jxl_dec_straight is not available (PATH or JXL_TOOLS).");
        try
        {
            int tolerance = IsLossy(path) ? 257 : 1;
            for (int i = 0; i < expected!.Count; i++)
            {
                ReadOnlySpan<ushort> actual = MemoryMarshal.Cast<Rgba64, ushort>(image.Frames[i].GetPixelMemory<Rgba64>().Span);
                ReadOnlySpan<ushort> reference = MemoryMarshal.Cast<Rgba64, ushort>(expected[i].GetPixelMemory<Rgba64>().Span);
                int worst = 0;
                for (int s = 0; s < actual.Length; s++) worst = Math.Max(worst, Math.Abs(actual[s] - reference[s]));
                await Assert.That(worst).IsLessThanOrEqualTo(tolerance).Because($"{name} frame {i}");
            }
        }
        finally
        {
            expected!.ForEach(frame => frame.Dispose());
        }
    }

    [Test]
    [Category("Oracle")]
    [MethodDataSource(nameof(ExtraChannelFiles))]
    public async Task Layers_MatchTheStraightOracleWithoutCoalescing(string name)
    {
        string path = CorpusPath(name);
        Skip.Unless(JxlHeadersReader.Read(JxlContainer.Parse(File.ReadAllBytes(path)).Codestream).Metadata.Animation is null,
            "animations have no layers.");
        List<Image>? expected = JxlDecodeOracleTests.StraightFrames(path, layers: true);
        Skip.Unless(expected is not null, "jxl_dec_straight is not available (PATH or JXL_TOOLS).");
        using JxlLayeredImage layered = JxlLayers.Load(path, Options(new DecoderOptions { ApplyExifOrientation = true }));
        try
        {
            await CompareFrames([.. layered.Layers.Select(layer => layer.Image.Clone())], expected!, name + " layers", IsLossy(path));
        }
        finally
        {
            expected!.ForEach(frame => frame.Dispose());
        }
    }

    [Test]
    public async Task Rejected_NameTheMissingCoreSupport()
    {
        foreach ((string name, string message) in new[]
        {
            ("extra-black.jxl", "CMYK images (a Black extra channel) need colour management for CMYK profiles"),
            ("extra-float.jxl", "floating-point samples need a float pixel type"),
            ("float32.jxl", "floating-point samples need a float pixel type"),
        })
        {
            string path = CorpusPath(name);
            await Assert.That(() => Image.Load(path, Options())).Throws<NotSupportedException>().WithMessageContaining(message);
            await Assert.That(() => JxlLayers.Load(path, Options())).Throws<NotSupportedException>().WithMessageContaining(message);
            await Assert.That(async () =>
            {
                await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(path, Options());
                while (await reader.MoveNextAsync()) { }
            }).Throws<NotSupportedException>().WithMessageContaining("PictureSharp does not provide yet");
            // Identify still describes them.
            await Assert.That(Image.Identify(path, Options()).Width).IsGreaterThan(0);
        }
    }

    [Test]
    public async Task Identify_DescribesTheStoredChannels()
    {
        ImageInfo premultiplied = Image.Identify(CorpusPath("premul-lossless.jxl"), Options());
        await Assert.That(premultiplied.Color.AlphaMode).IsEqualTo(ImageAlphaMode.Premultiplied);
        ImageInfo spot = Image.Identify(CorpusPath("spot-two.jxl"), Options());
        await Assert.That((spot.Color.ChannelCount, spot.Color.AlphaMode)).IsEqualTo((6, ImageAlphaMode.Straight));
        ImageInfo types = Image.Identify(CorpusPath("extra-types.jxl"), Options());
        await Assert.That(types.Color.ChannelCount).IsEqualTo(8);
        await Assert.That(Image.Identify(CorpusPath("extra-black.jxl"), Options()).Color.Model).IsEqualTo(ImageColorModel.Cmyk);
    }

    [Test]
    public async Task Decode_RendersSpotColoursAndDropsOtherChannels()
    {
        // The spot channel paints its colour over the gradient; the other channel types leave the colour untouched.
        using Image spot = Image.Load(CorpusPath("spot-rgb.jxl"), Options());
        using Image plain = Image.Load(CorpusPath("extra-16bit.jxl"), Options());
        using Image types = Image.Load(CorpusPath("extra-types.jxl"), Options());
        await Assert.That(Pixels(spot)).IsNotEqualTo(Pixels(plain));
        await Assert.That(JxlDecodeOracleTests.Rgba(types).Where((_, i) => i % 4 != 3))
            .IsEquivalentTo(JxlDecodeOracleTests.Rgba(plain).Where((_, i) => i % 4 != 3));
    }

    [Test]
    public async Task Unpremultiply_FollowsLibjxl()
    {
        // 1 / max(2^-26, alpha), unclamped: colour 0 stays 0 at alpha 0; other colour is multiplied by 2^26.
        float[] r = [0, 0.5f, 0.25f, 0.3f, 1e-9f], g = [0, 0.5f, 0.5f, 0.3f, 0], b = [0, 0, 0.125f, 0.3f, 0];
        float[] a = [0, 0, 0.5f, 1, 1e-9f];
        JxlPixelWriter.Unpremultiply(r, g, b, a, gray: false);
        await Assert.That(r).IsEquivalentTo(new[] { 0, 0.5f * (1 << 26), 0.5f, 0.3f, 1e-9f * (1 << 26) });
        await Assert.That(g).IsEquivalentTo(new[] { 0, 0.5f * (1 << 26), 1, 0.3f, 0 });
        await Assert.That(b).IsEquivalentTo(new[] { 0, 0, 0.25f, 0.3f, 0 });
        float[] gray = [0.25f, 0.5f];
        JxlPixelWriter.Unpremultiply(gray, gray, gray, [0.5f, 1], gray: true);
        await Assert.That(gray).IsEquivalentTo(new[] { 0.5f, 0.5f });
    }

    [Test]
    public async Task MixSpotRow_FollowsSpotColorStage()
    {
        float[] row = [0.2f, 0.2f, 0.2f, 0.2f];
        JxlColorOutput.MixSpotRow(row, [0, 1, 0.5f, 1], 1, 0.5f);
        float mix = 0.5f * 0.5f;
        await Assert.That(row).IsEquivalentTo(new[] { 0.2f, (0.5f * 1) + (0.5f * 0.2f), (mix * 1) + ((1 - mix) * 0.2f), (0.5f * 1) + (0.5f * 0.2f) });
        JxlColorOutput.MixSpotRow(row, [1, 1, 1, 1], 0.7f, 0);
        await Assert.That(row[0]).IsEqualTo(0.2f);
    }

    [Test]
    public async Task Decode_IsCancellable()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        foreach (string name in new[] { "spot-layers.jxl", "premul-anim.jxl", "spot-xyb.jxl" })
        {
            await Assert.That(() => Image.Load(CorpusPath(name), Options(), cancellation.Token)).Throws<OperationCanceledException>();
        }
    }

    [Test]
    [Category("Oracle")]
    public async Task Conformance_MatchesTheOracles()
    {
        // alpha_premultiplied (VarDCT, 16-bit premultiplied alpha), alpha_nonpremultiplied and spot (Modular, two spot
        // colours, two layers, ProPhoto ICC); cmyk_layers needs a CMS for CMYK.
        string? djxl = JxlDecodeOracleTests.Tool("djxl");
        Skip.Unless(djxl is not null && JxlDecodeOracleTests.Tool("jxl_dec_straight") is not null, "djxl or jxl_dec_straight is missing.");
        int compared = 0;
        foreach (string name in new[] { "alpha_premultiplied", "alpha_nonpremultiplied", "spot" })
        {
            string path = Path.Combine(Conformance, name, "input.jxl");
            if (!File.Exists(path)) continue;
            using Image image = Image.Load(path, Options(new DecoderOptions { ApplyExifOrientation = true }));
            using Image expected = JxlDecodeOracleTests.ReferenceImage(djxl!, path);
            string? mismatch = IsLossy(path) ? JxlDecodeTests.Tolerance(image, expected)
                : JxlDecodeOracleTests.Rgba(image).SequenceEqual(JxlDecodeOracleTests.Rgba(expected)) ? null : "pixels differ";
            await Assert.That(mismatch).IsNull().Because(name);
            using Image single = Image.Load(path, Options(new DecoderOptions { MaxDegreeOfParallelism = 1, PreserveBitDepth = true }));
            using Image many = Image.Load(path, Options(new DecoderOptions { MaxDegreeOfParallelism = 16, PreserveBitDepth = true }));
            await Assert.That(Pixels(many)).IsEqualTo(Pixels(single));
            compared++;
        }

        string cmyk = Path.Combine(Conformance, "cmyk_layers", "input.jxl");
        if (File.Exists(cmyk))
        {
            await Assert.That(() => Image.Load(cmyk, Options())).Throws<NotSupportedException>().WithMessageContaining("CMYK");
        }

        Console.WriteLine($"Extra-channel conformance files compared: {compared}");
    }

    // Lossless exactly, lossy within JxlDecodeTests.Tolerance; disposes the actual frames.
    private static async Task CompareFrames(List<Image> actual, List<Image> expected, string label, bool lossy)
    {
        try
        {
            await Assert.That(actual.Count).IsEqualTo(expected.Count).Because(label);
            for (int i = 0; i < actual.Count; i++)
            {
                string? mismatch = lossy ? JxlDecodeTests.Tolerance(actual[i], expected[i])
                    : (actual[i].Width, actual[i].Height) != (expected[i].Width, expected[i].Height) ? "size differs"
                    : JxlDecodeOracleTests.Rgba(actual[i]).SequenceEqual(JxlDecodeOracleTests.Rgba(expected[i])) ? null : "pixels differ";
                if (mismatch is not null) Assert.Fail($"{label} frame {i}: {mismatch}");
            }
        }
        finally
        {
            actual.ForEach(frame => frame.Dispose());
        }
    }
}
