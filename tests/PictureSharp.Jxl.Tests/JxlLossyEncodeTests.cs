using static PictureSharp.Formats.Jxl.Tests.JxlCodestreamWriterTests;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>The lossy (VarDCT) encoder: options, quality, alpha, and decoding by djxl v0.12.0 like this decoder.</summary>
public sealed class JxlLossyEncodeTests
{
    private static readonly ImageConfiguration Formats = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();

    private static JxlEncoderOptions Lossy(float? distance = null, int effort = JxlEncoderOptions.DefaultEffort) =>
        new() { Configuration = Formats, Distance = distance, Effort = effort };

    public enum Scene { Photo, Text }

    /// <summary>Smooth gradients, a disc and fine stripes, or repeated glyphs on a flat background.</summary>
    internal static byte[] ScenePixels(int width, int height, Scene scene, bool alpha, bool gray = false)
    {
        byte[] rgba = new byte[width * height * 4];
        var random = new Random(7);
        bool[][] glyphs = [.. Enumerable.Range(0, 6).Select(_ => Enumerable.Range(0, 9 * 12).Select(_ => random.Next(3) == 0).ToArray())];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                if (scene == Scene.Photo)
                {
                    double dx = x - width * 0.6;
                    double dy = y - height * 0.4;
                    bool disc = dx * dx + dy * dy < width * height * 0.04;
                    double stripes = x > width * 3 / 4 ? 40 * Math.Sin(x * 1.3) : 0;
                    rgba[i] = (byte)Math.Clamp(disc ? 220 : 40 + 150.0 * x / width + stripes, 0, 255);
                    rgba[i + 1] = (byte)Math.Clamp(disc ? 60 : 30 + 180.0 * y / height + random.Next(6), 0, 255);
                    rgba[i + 2] = (byte)Math.Clamp(disc ? 90 : 200 - 120.0 * (x + y) / (width + height), 0, 255);
                }
                else
                {
                    int glyph = (x / 11 + y / 16 * 3) % glyphs.Length;
                    int gx = x % 11;
                    int gy = y % 16;
                    bool ink = gx < 9 && gy < 12 && x / 11 % 8 != 7 && glyphs[glyph][gy * 9 + gx];
                    (rgba[i], rgba[i + 1], rgba[i + 2]) = ink ? ((byte)20, (byte)24, (byte)40) : ((byte)248, (byte)248, (byte)245);
                }

                if (gray) rgba[i + 1] = rgba[i + 2] = rgba[i];
                rgba[i + 3] = !alpha ? (byte)255 : (byte)(x < width / 4 ? 0 : x < width / 2 ? x * 255 / width : 255);
            }
        }

        return rgba;
    }

    private static Image Load(byte[] rgba, int width, int height) => Image.LoadPixelData(rgba, width, height, PixelFormat.Rgba32, width * 4);

    /// <summary>The PSNR of the colour of visible pixels, and whether alpha is unchanged.</summary>
    private static (double Psnr, bool AlphaExact) Compare(Image decoded, byte[] rgba)
    {
        byte[] actual = JxlDecodeOracleTests.Rgba(decoded);
        double error = 0;
        long samples = 0;
        bool alphaExact = true;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            alphaExact &= actual[i + 3] == rgba[i + 3];
            if (rgba[i + 3] == 0) continue;
            for (int c = 0; c < 3; c++)
            {
                double difference = actual[i + c] - rgba[i + c];
                error += difference * difference;
                samples++;
            }
        }

        double mse = error / Math.Max(samples, 1);
        return (mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse), alphaExact);
    }

    [Test]
    public async Task DefaultOptions_EncodeLossilyNearTheImage()
    {
        byte[] rgba = ScenePixels(200, 150, Scene.Photo, alpha: false);
        using Image image = Load(rgba, 200, 150);
        byte[] lossy = image.ToJxl(new JxlEncoderOptions { Configuration = Formats });
        byte[] lossless = image.ToJxl(Lossy(distance: 0));
        using Image decoded = Image.Load(lossy, new DecoderOptions { Configuration = Formats });
        (double psnr, _) = Compare(decoded, rgba);
        await Assert.That(lossy.Length).IsLessThan(lossless.Length / 2);
        await Assert.That(psnr).IsGreaterThan(34.0);
    }

    [Test]
    public async Task Quality100_IsLossless()
    {
        byte[] rgba = ScenePixels(64, 48, Scene.Photo, alpha: true);
        using Image image = Load(rgba, 64, 48);
        using Image decoded = Image.Load(image.ToJxl(new JxlEncoderOptions { Configuration = Formats, Quality = 100 }), new DecoderOptions { Configuration = Formats });
        await Assert.That(JxlDecodeOracleTests.Rgba(decoded)).IsEquivalentTo(rgba);
    }

    [Test]
    public async Task LossyOutput_IsDeterministic_AndKeepsAlpha()
    {
        byte[] rgba = ScenePixels(150, 90, Scene.Photo, alpha: true);
        using Image image = Load(rgba, 150, 90);
        byte[] first = image.ToJxl(Lossy());
        byte[] second = image.ToJxl(Lossy());
        using Image decoded = Image.Load(first, new DecoderOptions { Configuration = Formats });
        (double psnr, bool alphaExact) = Compare(decoded, rgba);
        await Assert.That(second).IsEquivalentTo(first);
        await Assert.That(alphaExact).IsTrue();
        await Assert.That(psnr).IsGreaterThan(30.0);
    }

    [Test]
    public async Task HigherDistances_GiveSmallerFiles()
    {
        using Image image = Load(ScenePixels(160, 120, Scene.Photo, alpha: false), 160, 120);
        int[] sizes = [.. new[] { 0.5f, 1, 3, 12 }.Select(distance => image.ToJxl(Lossy(distance)).Length)];
        await Assert.That(sizes).IsInDescendingOrder();
    }

    [Test]
    public async Task Cancellation_StopsLossyEncoding()
    {
        using Image image = Load(ScenePixels(64, 64, Scene.Photo, alpha: false), 64, 64);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var output = new MemoryStream();
        await Assert.That(async () => await image.SaveAsJxlAsync(output, Lossy(), cancelled.Token)).Throws<OperationCanceledException>();
    }

    /// <summary>djxl decodes the lossy output as this decoder does, and the result is close to the image.</summary>
    [Test]
    [Property("Category", "Oracle")]
    [Arguments(1, 1, 1.0f, 7, false, false)]
    [Arguments(37, 21, 1.0f, 7, false, true)]
    [Arguments(200, 150, 0.5f, 7, false, false)]
    [Arguments(200, 150, 1.0f, 3, false, false)]
    [Arguments(200, 150, 1.0f, 5, true, false)]
    [Arguments(200, 150, 2.0f, 6, false, true)]
    [Arguments(200, 150, 4.0f, 1, false, false)]
    [Arguments(160, 120, 12.0f, 7, false, false)]
    [Arguments(513, 300, 1.0f, 7, false, true)]
    [Arguments(2100, 40, 1.5f, 7, true, false)]
    public async Task LossyEncoded_DecodesLikeDjxl(int width, int height, float distance, int effort, bool gray, bool alpha)
    {
        string? djxl = JxlDecodeOracleTests.Tool("djxl");
        Skip.Unless(djxl is not null, "djxl v0.12.0 is not available (PATH or JXL_TOOLS).");
        byte[] rgba = ScenePixels(width, height, Scene.Photo, alpha, gray);
        using Image image = Load(rgba, width, height);
        string path = Path.Combine(Path.GetTempPath(), $"jxl-lossy-{Guid.NewGuid():N}.jxl");
        try
        {
            byte[] encoded = image.ToJxl(Lossy(distance, effort));
            File.WriteAllBytes(path, encoded);
            using Image expected = JxlDecodeOracleTests.DjxlImage(djxl!, path);
            using Image actual = Image.Load(encoded, new DecoderOptions { Configuration = Formats });
            await Assert.That(JxlDecodeTests.Tolerance(actual, expected)).IsNull();
            (double psnr, bool alphaExact) = Compare(expected, rgba);
            await Assert.That(alphaExact).IsTrue();
            await Assert.That(psnr).IsGreaterThan(distance >= 10 ? 22.0 : distance >= 4 ? 28.0 : 32.0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Repeated glyphs become patches at effort 7, which djxl adds back like this decoder.</summary>
    [Test]
    [Property("Category", "Oracle")]
    public async Task RepeatedShapes_AreCodedAsPatches()
    {
        string? djxl = JxlDecodeOracleTests.Tool("djxl");
        Skip.Unless(djxl is not null, "djxl v0.12.0 is not available (PATH or JXL_TOOLS).");
        byte[] rgba = ScenePixels(352, 256, Scene.Text, alpha: true);
        using Image image = Load(rgba, 352, 256);
        byte[] withPatches = image.ToJxl(Lossy(1.0f, 7));
        byte[] withoutPatches = image.ToJxl(Lossy(1.0f, 6));
        string path = Path.Combine(Path.GetTempPath(), $"jxl-patches-{Guid.NewGuid():N}.jxl");
        try
        {
            File.WriteAllBytes(path, withPatches);
            using Image expected = JxlDecodeOracleTests.DjxlImage(djxl!, path);
            using Image actual = Image.Load(withPatches, new DecoderOptions { Configuration = Formats });
            await Assert.That(JxlDecodeTests.Tolerance(actual, expected)).IsNull();
            (double psnr, bool alphaExact) = Compare(expected, rgba);
            await Assert.That(alphaExact).IsTrue();
            await Assert.That(psnr).IsGreaterThan(30.0);
            await Assert.That(withPatches.Length).IsLessThan(withoutPatches.Length / 2);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
