using PictureSharp.Formats.Jxl.Container;
using static PictureSharp.Formats.Jxl.Tests.JxlCodestreamWriterTests;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>The public encoder: SaveAsJxl/ToJxl, options, metadata and EditMetadata.</summary>
public sealed class JxlEncoderTests
{
    private static readonly ImageConfiguration Formats = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();

    private static JxlEncoderOptions Encoder(JxlEncoderOptions? options = null) => (options ?? new()) with { Configuration = Formats };

    private static Image Create(int width, int height, Pattern pattern, bool alpha, bool gray = false)
    {
        byte[] rgba = Pixels(width, height, pattern, alpha);
        if (gray)
        {
            for (int i = 0; i < rgba.Length; i += 4) rgba[i + 1] = rgba[i + 2] = rgba[i];
        }

        return Image.LoadPixelData(rgba, width, height, PixelFormat.Rgba32, width * 4);
    }

    private static async Task SamePixels(Image actual, Image expected)
    {
        await Assert.That((actual.Width, actual.Height)).IsEqualTo((expected.Width, expected.Height));
        await Assert.That(JxlDecodeOracleTests.Rgba(actual)).IsEquivalentTo(JxlDecodeOracleTests.Rgba(expected));
    }

    [Test]
    public async Task Options_HaveTheDocumentedDefaults()
    {
        var options = new JxlEncoderOptions();
        await Assert.That(options.Format).IsSameReferenceAs(ImageFormat.Jxl);
        await Assert.That(options.CompressMetadata).IsFalse();
        await Assert.That(options.Metadata).IsEqualTo(MetadataKinds.All);
        await Assert.That((options with { CompressMetadata = true }).CompressMetadata).IsTrue();
    }

    [Test]
    public async Task ToJxl_KeepsEveryValue_IncludingColourUnderTransparentAlpha()
    {
        using Image image = Create(150, 90, Pattern.Noise, alpha: true);
        byte[] encoded = image.ToJxl(Encoder());
        await Assert.That(encoded[..2]).IsEquivalentTo(new byte[] { 0xFF, 0x0A });
        ImageInfo info = Image.Identify(encoded, Options());
        await Assert.That((info.Color.Model, info.Color.AlphaMode, info.Color.BitsPerSample)).IsEqualTo((ImageColorModel.Rgb, ImageAlphaMode.Straight, 8));
        using Image decoded = Image.Load(encoded, Options());
        await SamePixels(decoded, image);
    }

    [Test]
    public async Task GrayAndOpaqueImages_UseOneColourChannelAndNoAlpha()
    {
        using Image image = Create(80, 60, Pattern.Gradient, alpha: false, gray: true);
        byte[] encoded = image.ToJxl(Encoder());
        ImageInfo info = Image.Identify(encoded, Options());
        await Assert.That((info.Color.Model, info.Color.ChannelCount, info.Color.AlphaMode)).IsEqualTo((ImageColorModel.Gray, 1, ImageAlphaMode.None));
        using Image decoded = Image.Load(encoded, Options());
        await SamePixels(decoded, image);
    }

    [Test]
    public async Task SaveAsJxl_WritesFilesAndStreams()
    {
        using Image image = Create(40, 30, Pattern.FewColors, alpha: false);
        string path = Path.Combine(Path.GetTempPath(), $"jxl-save-{Guid.NewGuid():N}.jxl");
        try
        {
            image.SaveAsJxl(path, Encoder());
            using var stream = new MemoryStream();
            image.SaveAsJxl(stream, Encoder());
            await image.SaveAsJxlAsync(path + "2", Encoder());
            using var asyncStream = new MemoryStream();
            await image.SaveAsJxlAsync(asyncStream, Encoder());
            byte[] expected = image.ToJxl(Encoder());
            await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(expected);
            await Assert.That(File.ReadAllBytes(path + "2")).IsEquivalentTo(expected);
            await Assert.That(stream.ToArray()).IsEquivalentTo(expected);
            await Assert.That(asyncStream.ToArray()).IsEquivalentTo(expected);
            using Image decoded = Image.Load(path, Options());
            await SamePixels(decoded, image);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "2");
        }
    }

    [Test]
    public async Task Metadata_IsWrittenAndRead()
    {
        using Image source = Image.Load(CorpusPath("icc-profile.jxl"), Options());
        byte[] icc = source.Metadata.IccProfile!;
        using Image image = Create(64, 64, Pattern.Gradient, alpha: false);
        image.Metadata.IccProfile = icc;
        image.Metadata.Xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'/>"u8.ToArray();
        image.Metadata.Orientation = ExifOrientation.RightTop;
        byte[] exif = image.Metadata.Exif!;
        foreach (bool compress in new[] { false, true })
        {
            byte[] encoded = image.ToJxl(Encoder(new() { CompressMetadata = compress }));
            var boxes = JxlContainer.Boxes(encoded).Select(box => box.Inner ?? box.Type).ToList();
            await Assert.That(boxes).IsEquivalentTo(new[] { "JXL ", "ftyp", "Exif", "xml ", "jxlc" });
            await Assert.That(JxlContainer.Boxes(encoded).Count(box => box.Type == "brob")).IsEqualTo(compress ? 2 : 0);
            ImageMetadata metadata = Image.ReadMetadata((ReadOnlyMemory<byte>)encoded, Options());
            await Assert.That(metadata.IccProfile).IsEquivalentTo(icc);
            await Assert.That(metadata.Exif).IsEquivalentTo(exif);
            await Assert.That(metadata.Xmp).IsEquivalentTo(image.Metadata.Xmp!);
            await Assert.That(Image.Identify(encoded, Options()).Orientation).IsEqualTo(ExifOrientation.RightTop);
            using Image decoded = Image.Load(encoded, Options());
            await SamePixels(decoded, image);
        }

        byte[] bare = image.ToJxl(Encoder(new() { Metadata = MetadataKinds.None }));
        await Assert.That(bare[..2]).IsEquivalentTo(new byte[] { 0xFF, 0x0A });
        ImageMetadata none = Image.ReadMetadata((ReadOnlyMemory<byte>)bare, Options());
        await Assert.That(none.IccProfile).IsNull();
        await Assert.That(none.Xmp).IsNull();
        // The orientation is part of the codestream, not of a metadata block.
        await Assert.That(none.Orientation).IsEqualTo(ExifOrientation.RightTop);
    }

    [Test]
    [Arguments("lossy-p3.jxl")]
    [Arguments("gamma-custom.jxl")]
    [Arguments("p3-perceptual.jxl")]
    public async Task ColourDescriptions_RoundTrip(string name)
    {
        ImageMetadata expected = Image.ReadMetadata(CorpusPath(name), Options());
        using Image image = Create(16, 16, Pattern.Gradient, alpha: false);
        image.Metadata.Gamma = expected.Gamma;
        image.Metadata.Chromaticities = expected.Chromaticities;
        ImageMetadata actual = Image.ReadMetadata((ReadOnlyMemory<byte>)image.ToJxl(Encoder()), Options());
        await Assert.That((actual.Gamma, actual.Chromaticities)).IsEqualTo((expected.Gamma, expected.Chromaticities));
    }

    [Test]
    public async Task UnsupportedProfiles_Throw()
    {
        using Image image = Create(8, 8, Pattern.Noise, alpha: false);
        byte[] cmyk = new byte[132];
        "CMYK"u8.CopyTo(cmyk.AsSpan(16));
        image.Metadata.IccProfile = cmyk;
        await Assert.That(() => image.ToJxl(Encoder())).Throws<NotSupportedException>();
        "GRAY"u8.CopyTo(cmyk.AsSpan(16));
        image.Metadata.IccProfile = cmyk;
        await Assert.That(() => image.ToJxl(Encoder())).Throws<NotSupportedException>();
    }

    [Test]
    public async Task Animations_EncodeTheRootFrame()
    {
        using Image first = Create(20, 10, Pattern.Noise, alpha: false);
        using Image second = Create(20, 10, Pattern.Gradient, alpha: false);
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(100), [first, second]);
        using Image decoded = Image.Load(animation.ToJxl(Encoder()), Options());
        await SamePixels(decoded, first);
    }

    [Test]
    public async Task Cancellation_StopsEncoding()
    {
        using Image image = Create(300, 300, Pattern.Noise, alpha: false);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var output = new MemoryStream();
        await Assert.That(async () => await image.SaveAsJxlAsync(output, Encoder(), cancelled.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task EditMetadata_ChangesBlocksAndKeepsFrames()
    {
        using Image image = Create(300, 270, Pattern.Gradient, alpha: true);
        byte[] encoded = image.ToJxl(Encoder());
        byte[] icc = Image.ReadMetadata(CorpusPath("icc-profile.jxl"), Options()).IccProfile!;
        byte[] edited = Image.EditMetadata((ReadOnlyMemory<byte>)encoded, metadata =>
        {
            metadata.IccProfile = icc;
            metadata.Xmp = "<a/>"u8.ToArray();
            metadata.Orientation = ExifOrientation.BottomRight;
        }, Options());
        ImageMetadata read = Image.ReadMetadata((ReadOnlyMemory<byte>)edited, Options());
        await Assert.That(read.IccProfile).IsEquivalentTo(icc);
        await Assert.That(read.Xmp).IsEquivalentTo("<a/>"u8.ToArray());
        await Assert.That(read.Orientation).IsEqualTo(ExifOrientation.BottomRight);
        using (Image decoded = Image.Load(edited, Options())) await SamePixels(decoded, image);

        // Back again: no profile, no XMP, upright; EXIF holds only the orientation tag.
        byte[] reverted = Image.EditMetadata((ReadOnlyMemory<byte>)edited, metadata =>
        {
            metadata.IccProfile = null;
            metadata.Xmp = null;
            metadata.Orientation = ExifOrientation.TopLeft;
        }, Options());
        ImageMetadata plain = Image.ReadMetadata((ReadOnlyMemory<byte>)reverted, Options());
        await Assert.That((plain.IccProfile, plain.Xmp, plain.Orientation)).IsEqualTo(((byte[]?)null, (byte[]?)null, ExifOrientation.TopLeft));
        using (Image decoded = Image.Load(reverted, Options())) await SamePixels(decoded, image);
    }

    [Test]
    public async Task EditMetadata_KeepsOtherBoxes()
    {
        byte[] data = File.ReadAllBytes(CorpusPath("container-exif.jxl"));
        ImageMetadata original = Image.ReadMetadata((ReadOnlyMemory<byte>)data, Options());
        byte[] edited = Image.EditMetadata((ReadOnlyMemory<byte>)data, metadata => metadata.Xmp = "<b/>"u8.ToArray(), Options());
        ImageMetadata read = Image.ReadMetadata((ReadOnlyMemory<byte>)edited, Options());
        await Assert.That(read.Exif).IsEquivalentTo(original.Exif!);
        await Assert.That(read.Xmp).IsEquivalentTo("<b/>"u8.ToArray());
        // The codestream boxes are copied byte for byte.
        static byte[] Codestream(byte[] file) => [.. JxlContainer.Boxes(file).Where(box => box.Type is "jxlc" or "jxlp")
            .SelectMany(box => file[(int)box.Start..(int)box.End])];
        await Assert.That(Codestream(edited)).IsEquivalentTo(Codestream(data));
        using Image expected = Image.Load(data, Options());
        using Image decoded = Image.Load(edited, Options());
        await SamePixels(decoded, expected);
    }

    [Test]
    public async Task EditMetadata_RejectsWhatJpegXlCannotStore()
    {
        byte[] data = Create(16, 16, Pattern.Noise, alpha: false).ToJxl(Encoder());
        await Assert.That(() => Image.EditMetadata((ReadOnlyMemory<byte>)data, metadata => metadata.HorizontalResolution = 300, Options()))
            .Throws<NotSupportedException>();
        byte[] jpeg = File.ReadAllBytes(CorpusPath("jpeg-420.jxl"));
        await Assert.That(() => Image.EditMetadata((ReadOnlyMemory<byte>)jpeg, metadata => metadata.Xmp = "<c/>"u8.ToArray(), Options()))
            .Throws<NotSupportedException>();
    }
}
