using PictureSharp.Formats.Jxl.Codestream.Frames;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;
using System.Security.Cryptography;
using System.Text.Json;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>
/// Decoding against the djxl renderings recorded with the corpus (no tools needed): lossless files by the hash in
/// corpus.json, lossy ones against djxl/*.png within <see cref="Tolerance"/>, XYB images with an ICC profile against
/// cms/*.png (djxl's linear output converted to the profile) within <see cref="ManagedTolerance"/>, and XYB images
/// outside sRGB converted with ToSrgb against srgb/*.png (djxl --color_space=sRGB) within <see cref="Tolerance"/>.
/// </summary>
public sealed class JxlDecodeTests
{
    // Valid files outside this version's subset: animation, float samples and photon noise.
    private static readonly string[] Unsupported = ["animation.jxl", "float32.jxl", "lossy-noise.jxl"];

    public static IEnumerable<string> CorpusFiles()
    {
        using JsonDocument corpus = JsonDocument.Parse(File.ReadAllText(CorpusPath("corpus.json")));
        return [.. corpus.RootElement.GetProperty("files").EnumerateObject().Select(file => file.Name)];
    }

    private static string? ExpectedHash(string name)
    {
        using JsonDocument corpus = JsonDocument.Parse(File.ReadAllText(CorpusPath("corpus.json")));
        return corpus.RootElement.GetProperty("files").GetProperty(name).GetProperty("rgba8Sha256").GetString();
    }

    [Test]
    [MethodDataSource(nameof(CorpusFiles))]
    public async Task Corpus_MatchesTheDjxlRendering(string name)
    {
        // djxl applies the orientation, so the comparison does too.
        DecoderOptions options = Options() with { ApplyExifOrientation = true };
        if (Unsupported.Contains(name))
        {
            await Assert.That(() => Image.Load(CorpusPath(name), options)).Throws<NotSupportedException>();
            return;
        }

        using Image image = Image.Load(CorpusPath(name), options);
        string managed = CorpusPath(Path.Combine("cms", Path.ChangeExtension(name, ".png")));
        if (File.Exists(managed))
        {
            using Image converted = Image.Load(managed);
            await Assert.That(ManagedTolerance(image, converted)).IsNull();
            return;
        }

        string reference = CorpusPath(Path.Combine("djxl", Path.ChangeExtension(name, ".png")));
        if (!File.Exists(reference))
        {
            string hash = Convert.ToHexStringLower(SHA256.HashData(JxlDecodeOracleTests.Rgba(image)));
            await Assert.That(hash).IsEqualTo(ExpectedHash(name));
            return;
        }

        using Image expected = Image.Load(reference);
        await Assert.That(Tolerance(image, expected)).IsNull();
    }

    public static IEnumerable<string> SrgbFiles() =>
        Directory.GetFiles(CorpusPath("srgb"), "*.png").Select(path => Path.ChangeExtension(Path.GetFileName(path), ".jxl")).Order(StringComparer.Ordinal);

    [Test]
    [MethodDataSource(nameof(SrgbFiles))]
    public async Task Corpus_ToSrgbMatchesTheDjxlSrgbRendering(string name)
    {
        // XYB is rendered as sRGB in floating point and quantized once, so nothing is left for PictureSharp to convert.
        using Image image = Image.Load(CorpusPath(name), Options() with { ApplyExifOrientation = true, ColorConversion = ColorConversionMode.ToSrgb });
        await Assert.That((image.Metadata.IccProfile, image.Metadata.HasColorDescription)).IsEqualTo(((byte[]?)null, false));
        using Image expected = Image.Load(CorpusPath(Path.Combine("srgb", Path.ChangeExtension(name, ".png"))));
        await Assert.That(Tolerance(image, expected)).IsNull();
    }

    /// <summary>
    /// Null when <paramref name="actual"/> matches the djxl rendering within libjxl's floating-point freedom: every
    /// 8-bit sample within 1 and at most 0.1% of them different. djxl uses approximate reciprocals in the
    /// dequantisation and filters, so lossy images are not bit-exact.
    /// </summary>
    internal static string? Tolerance(Image actual, Image expected)
    {
        if ((actual.Width, actual.Height) != (expected.Width, expected.Height))
        {
            return $"{actual.Width}x{actual.Height} instead of {expected.Width}x{expected.Height}";
        }

        byte[] a = JxlDecodeOracleTests.Rgba(actual);
        byte[] e = JxlDecodeOracleTests.Rgba(expected);
        int different = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int difference = Math.Abs(a[i] - e[i]);
            if (difference > 1)
            {
                return $"sample {i} differs by {difference}";
            }

            different += difference;
        }

        return different * 1000L > a.Length ? $"{different} of {a.Length} samples differ" : null;
    }

    /// <summary>
    /// Null when every sample is within 1 of the colour-managed reference. The reference is rounded, while the decoder
    /// dithers 8-bit output as libjxl does, so many samples differ by 1.
    /// </summary>
    internal static string? ManagedTolerance(Image actual, Image expected)
    {
        if ((actual.Width, actual.Height) != (expected.Width, expected.Height))
        {
            return $"{actual.Width}x{actual.Height} instead of {expected.Width}x{expected.Height}";
        }

        byte[] a = JxlDecodeOracleTests.Rgba(actual);
        byte[] e = JxlDecodeOracleTests.Rgba(expected);
        for (int i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i] - e[i]) > 1)
            {
                return $"sample {i} differs by {Math.Abs(a[i] - e[i])}";
            }
        }

        return null;
    }

    [Test]
    public async Task Corpus_FramesUseTheCodingToolsTheyNameAfter()
    {
        static List<JxlFrameHeader> Frames(string name)
        {
            JxlCodestream codestream = JxlContainer.Parse(File.ReadAllBytes(CorpusPath(name))).Codestream;
            return JxlDecodeSession.ReadFrameHeaders(codestream, JxlHeadersReader.Read(codestream));
        }

        List<JxlFrameHeader> patches = Frames("lossy-patches.jxl");
        await Assert.That(patches.Select(frame => frame.Type)).IsEquivalentTo([JxlFrameType.ReferenceOnly, JxlFrameType.Regular]);
        await Assert.That(patches[^1].Flags.HasFlag(JxlFrameFlags.Patches)).IsTrue();
        List<JxlFrameHeader> progressive = Frames("lossy-progressive-dc.jxl");
        await Assert.That(progressive.Select(frame => frame.Type)).IsEquivalentTo([JxlFrameType.Dc, JxlFrameType.Regular]);
        await Assert.That(progressive[^1].Flags.HasFlag(JxlFrameFlags.UseDcFrame)).IsTrue();
        await Assert.That(Frames("lossy-progressive-ac.jxl")[^1].Passes.Count).IsGreaterThan(1u);
        await Assert.That(Frames("lossy-resampling.jxl")[^1].Upsampling).IsEqualTo(2u);
        await Assert.That(Frames("lossy-epf3.jxl")[^1].LoopFilter.EpfIterations).IsEqualTo(3u);
        await Assert.That(Frames("lossy-modular.jxl")[^1].Encoding).IsEqualTo(JxlFrameEncoding.Modular);
        await Assert.That(Frames("lossy-groups.jxl")[^1].Groups).IsGreaterThan(1);
        JxlFrameHeader jpeg = Frames("jpeg-420.jxl")[^1];
        await Assert.That((jpeg.ColorTransform, jpeg.Is444)).IsEqualTo((JxlColorTransform.YCbCr, false));
    }

    [Test]
    public async Task Decode_ReportsAlphaAndKeepsTheStoredOrientation()
    {
        using (Image rgba = Image.Load(CorpusPath("rgba8-lossless.jxl"), Options()))
        {
            await Assert.That(rgba.HasAlpha).IsTrue();
            await Assert.That(rgba.IsOpaque()).IsFalse();
        }

        using (Image rgb = Image.Load(CorpusPath("rgb8-lossless.jxl"), Options()))
        {
            await Assert.That(rgb.HasAlpha).IsFalse();
            await Assert.That(rgb.Metadata.Orientation).IsEqualTo(ExifOrientation.Unknown);
        }

        // Orientation 6 (rotate 90° clockwise) is stored 40×24, like the source JPEG, and displayed 24×40.
        using Image stored = Image.Load(CorpusPath("orient6.jxl"), Options());
        await Assert.That((stored.Width, stored.Height, stored.Metadata.Orientation)).IsEqualTo((40, 24, ExifOrientation.RightTop));
        using Image oriented = Image.Load(CorpusPath("orient6.jxl"), Options() with { ApplyExifOrientation = true });
        await Assert.That((oriented.Width, oriented.Height)).IsEqualTo((24, 40));
    }

    [Test]
    public async Task Decode_GrayscaleIsReplicated()
    {
        using Image image = Image.Load(CorpusPath("gray8-lossless.jxl"), Options());
        foreach (Rgba32 pixel in image.GetPixelMemory().Span)
        {
            if (pixel.R != pixel.G || pixel.G != pixel.B || pixel.A != 255)
            {
                Assert.Fail($"Not an opaque gray pixel: {pixel}.");
            }
        }

        await Assert.That(image.HasAlpha).IsFalse();
    }

    [Test]
    [Arguments("squeeze.jxl")]
    [Arguments("lossy-progressive-ac.jxl")]
    [Arguments("lossy-patches.jxl")]
    public async Task Decode_EveryTruncation_IsRejectedAsCorrupt(string name)
    {
        byte[] data = File.ReadAllBytes(CorpusPath(name));
        for (int length = 0; length < data.Length; length++)
        {
            await Assert.That(() => Image.Load(data.AsSpan(0, length).ToArray(), Options())).Throws<ImageFormatException>();
        }
    }

    [Test]
    [Arguments("rgba8-lossless.jxl")]
    [Arguments("palette.jxl")]
    [Arguments("progressive.jxl")]
    [Arguments("effort1.jxl")]
    [Arguments("lossy-progressive-dc.jxl")]
    [Arguments("jpeg-420.jxl")]
    [Arguments("lossy-alpha.jxl")]
    public async Task Decode_EveryBitFlip_DecodesOrFailsCleanly(string name)
    {
        byte[] data = File.ReadAllBytes(CorpusPath(name));
        var unexpected = new List<string>();
        for (int bit = 0; bit < data.Length * 8; bit++)
        {
            byte[] damaged = [.. data];
            damaged[bit / 8] ^= (byte)(1 << (bit % 8));
            try
            {
                using Image _ = Image.Load(damaged, Options(new DecoderOptions { MaxPixels = 1 << 22 }));
            }
            catch (Exception exception) when (exception is ImageFormatException or NotSupportedException)
            {
            }
            catch (Exception exception)
            {
                unexpected.Add($"bit {bit}: {exception.GetType().Name} {exception.Message}");
            }
        }

        await Assert.That(unexpected.Take(10)).IsEmpty();
    }

    [Test]
    public async Task Decode_ChecksThePixelLimitAndCancellation()
    {
        byte[] data = File.ReadAllBytes(CorpusPath("groups-300x200.jxl"));
        await Assert.That(() => Image.Load(data, Options(new DecoderOptions { MaxPixels = 300 * 200 - 1 }))).Throws<ImageFormatException>();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.That(() => Image.Load(data, Options(), canceled.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Decode_RejectsAnimationAndNoiseAsUnsupported()
    {
        await Assert.That(() => Image.Load(CorpusPath("animation.jxl"), Options())).Throws<NotSupportedException>()
            .WithMessageContaining("animation");
        await Assert.That(() => Image.Load(CorpusPath("lossy-noise.jxl"), Options())).Throws<NotSupportedException>()
            .WithMessageContaining("noise");
    }

    [Test]
    public async Task Decode_LossyAlphaAndGray()
    {
        using (Image rgba = Image.Load(CorpusPath("lossy-alpha.jxl"), Options()))
        {
            await Assert.That(rgba.HasAlpha).IsTrue();
            await Assert.That(rgba.IsOpaque()).IsFalse();
        }

        using Image gray = Image.Load(CorpusPath("lossy-gray.jxl"), Options());
        await Assert.That(gray.HasAlpha).IsFalse();
        foreach (Rgba32 pixel in gray.GetPixelMemory().Span)
        {
            if (pixel.R != pixel.G || pixel.G != pixel.B)
            {
                Assert.Fail($"Not a gray pixel: {pixel}.");
            }
        }
    }
}
