using System.Security.Cryptography;
using System.Text.Json;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>Lossless Modular decoding against the djxl renderings recorded in corpus.json (no tools needed).</summary>
public sealed class JxlDecodeTests
{
    // Valid files outside this version's subset: animation, float samples and lossy (XYB) images.
    private static readonly string[] Unsupported = ["animation.jxl", "float32.jxl", "jxlp-v1-out-of-order.jxl", "jxlp-v1.jxl", "rgb8-lossy.jxl"];

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
        string hash = Convert.ToHexStringLower(SHA256.HashData(JxlDecodeOracleTests.Rgba(image)));
        await Assert.That(hash).IsEqualTo(ExpectedHash(name));
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
    public async Task Decode_EveryTruncation_IsRejectedAsCorrupt()
    {
        byte[] data = File.ReadAllBytes(CorpusPath("squeeze.jxl"));
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
    public async Task Decode_RejectsLayeredAndLossyImagesAsUnsupported()
    {
        await Assert.That(() => Image.Load(CorpusPath("rgb8-lossy.jxl"), Options())).Throws<NotSupportedException>()
            .WithMessageContaining("XYB");
        await Assert.That(() => Image.Load(CorpusPath("animation.jxl"), Options())).Throws<NotSupportedException>()
            .WithMessageContaining("animation");
    }
}
