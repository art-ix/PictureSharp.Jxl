using System.Diagnostics;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

public sealed class HighBitDepthTests
{
    private static readonly ImageConfiguration Formats = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();
    private static DecoderOptions Wide => new() { Configuration = Formats, PreserveBitDepth = true };

    private static Image Sample(bool gray, bool alpha, int width = 256, int height = 256)
    {
        var image = new Image(width, height, PixelType.Rgba64, new Rgba64(0, 0, 0, ushort.MaxValue));
        Span<Rgba64> pixels = image.GetPixelSpan<Rgba64>();
        for (int i = 0; i < pixels.Length; i++)
        {
            ushort r = (ushort)i;
            pixels[i] = new Rgba64(r, gray ? r : (ushort)((i * 2791) & 65535), gray ? r : (ushort)((i * 3157) & 65535),
                alpha ? (ushort)((i * 1709) & 65535) : ushort.MaxValue);
        }

        return image;
    }

    private static byte[] Encode(Image image, float distance = 0) => image.ToJxl(new JxlEncoderOptions { Configuration = Formats, Distance = distance, Effort = 1 });

    private static int Difference(Image first, Image second)
    {
        ReadOnlySpan<Rgba64> a = first.GetPixelSpan<Rgba64>(), b = second.GetPixelSpan<Rgba64>();
        int worst = 0;
        for (int i = 0; i < a.Length; i++)
        {
            worst = Math.Max(worst, Math.Max(Math.Abs(a[i].R - b[i].R), Math.Max(Math.Abs(a[i].G - b[i].G), Math.Max(Math.Abs(a[i].B - b[i].B), Math.Abs(a[i].A - b[i].A)))));
        }

        return worst;
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task Lossless_RoundTrip_PreservesEverySixteenBitValue(bool gray, bool alpha)
    {
        using Image source = Sample(gray, alpha);
        byte[] encoded = Encode(source);
        ImageInfo info = Image.Identify(encoded.AsSpan(), Wide);
        await Assert.That(info.Color.BitsPerSample).IsEqualTo(16);
        using Image decoded = Image.Load(encoded.AsSpan(), Wide);
        await Assert.That(decoded.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(decoded.HasAlpha).IsEqualTo(alpha);
        await Assert.That(Difference(source, decoded)).IsEqualTo(0);
        await Assert.That(decoded.GetPixelSpan<Rgba64>().ToArray().Select(p => p.R).Distinct().Count()).IsEqualTo(65536);
        using Image narrow = Image.Load(encoded.AsSpan(), Wide with { PreserveBitDepth = false });
        await Assert.That(narrow.PixelType).IsEqualTo(PixelType.Rgba32);
    }

    [Test]
    public async Task Palette_KeepsSixteenBitValues_AndTransparentColour()
    {
        using Image source = Sample(false, true, 64, 32);
        Span<Rgba64> pixels = source.GetPixelSpan<Rgba64>();
        for (int i = 0; i < pixels.Length; i++) pixels[i] = new Rgba64((ushort)(1000 + (i % 4)), 4000, 6000, 0);
        using Image decoded = Image.Load(Encode(source).AsSpan(), Wide);
        await Assert.That(Difference(source, decoded)).IsEqualTo(0);
    }

    [Test]
    public async Task Encoder_DeclaresBothPixelTypes()
    {
        IImageFormatEncoder encoder = ((IImageFormatModule)new JxlFormat()).Encoder!;
        await Assert.That(encoder.SupportsPixelType(PixelType.Rgba32)).IsTrue();
        await Assert.That(encoder.SupportsPixelType(PixelType.Rgba64)).IsTrue();
    }

    [Test]
    public async Task PreserveBitDepth_LeavesEightBitFilesAtEightBits()
    {
        using Image image = Image.Load(CorpusPath("rgba8-lossless.jxl"), Wide);
        await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba32);
    }

    [Test]
    public async Task Budget_ChargesSixteenBitPixelsTwice()
    {
        string path = CorpusPath("rgb16-lossless.jxl");
        ImageInfo info = Image.Identify(path, Wide);
        long pixels = (long)info.Width * info.Height;
        await Assert.That(() => Image.Load(path, Wide with { MaxPixels = (pixels * 2) - 1 })).Throws<ImageFormatException>();
        using Image image = Image.Load(path, Wide with { MaxPixels = pixels * 2 });
        await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba64);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LossyEncoding_PreservesAlphaAtSixteenBits(bool gray)
    {
        using Image source = Sample(gray, true, 64, 32);
        Span<Rgba64> pixels = source.GetPixelSpan<Rgba64>();
        for (int i = 0; i < pixels.Length; i++)
        {
            ushort value = (ushort)((long)i * 65535 / (pixels.Length - 1));
            if (gray) pixels[i] = new Rgba64(value, value, value, pixels[i].A);
        }

        using Image decoded = Image.Load(Encode(source, 1).AsSpan(), Wide);
        await Assert.That(decoded.PixelType).IsEqualTo(PixelType.Rgba64);
        Rgba64[] expected = source.GetPixelSpan<Rgba64>().ToArray(), actual = decoded.GetPixelSpan<Rgba64>().ToArray();
        await Assert.That(actual.Select(p => p.A).SequenceEqual(expected.Select(p => p.A))).IsTrue();
        await Assert.That(actual.Select(p => p.R).Distinct().Count()).IsGreaterThan(256);
        if (gray) await Assert.That(actual.All(p => p.R == p.G && p.G == p.B)).IsTrue();
    }

    [Test]
    [Property("Category", "Oracle")]
    [Arguments("rgb10-lossless.jxl", 1)]
    [Arguments("rgb12-lossless.jxl", 1)]
    [Arguments("rgb16-lossless.jxl", 1)]
    [Arguments("gray16-alpha.jxl", 1)]
    [Arguments("lossy-16bit.jxl", 4)]
    public async Task Corpus_SixteenBitOutput_MatchesDjxl(string file, int tolerance)
    {
        string? tool = JxlDecodeOracleTests.Tool("djxl");
        Skip.Unless(tool is not null, "djxl is unavailable.");
        using Image expected = Djxl(tool!, CorpusPath(file));
        using Image actual = Image.Load(CorpusPath(file), Wide with { ApplyExifOrientation = true });
        await Assert.That(actual.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(Difference(expected, actual)).IsLessThanOrEqualTo(tolerance);
    }

    [Test]
    [Property("Category", "Oracle")]
    [Arguments(0f, false)]
    [Arguments(1f, false)]
    [Arguments(0f, true)]
    [Arguments(1f, true)]
    public async Task Djxl_ReadsSixteenBitEncoding(float distance, bool gray)
    {
        string? tool = JxlDecodeOracleTests.Tool("djxl");
        Skip.Unless(tool is not null, "djxl is unavailable.");
        string path = Path.Combine(Path.GetTempPath(), $"picturesharp-jxl16-{Guid.NewGuid():N}.jxl");
        try
        {
            using Image source = Sample(gray, true, 64, 32);
            byte[] bytes = Encode(source, distance);
            File.WriteAllBytes(path, bytes);
            using Image expected = Djxl(tool!, path);
            using Image actual = Image.Load(bytes.AsSpan(), Wide);
            await Assert.That(Difference(expected, actual)).IsLessThanOrEqualTo(distance == 0 ? 0 : 4);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Image Djxl(string tool, string path)
    {
        string png = Path.Combine(Path.GetTempPath(), $"picturesharp-jxl16-{Guid.NewGuid():N}.png");
        try
        {
            using Process process = Process.Start(new ProcessStartInfo(tool, [path, png, "--bits_per_sample=16", "--quiet"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"djxl failed: {error}{output.GetAwaiter().GetResult()}");
            return Image.Load(png, new DecoderOptions { PreserveBitDepth = true });
        }
        finally
        {
            File.Delete(png);
        }
    }
}
