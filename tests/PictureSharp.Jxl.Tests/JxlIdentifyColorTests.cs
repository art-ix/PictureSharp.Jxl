using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary><c>ImageInfo.Color</c> and <c>Compression</c> (PictureSharp 0.8.5) from the corpus and crafted headers.</summary>
public sealed class JxlIdentifyColorTests
{
    private static ImageInfo Identify(string name) => Image.Identify(File.ReadAllBytes(CorpusPath(name)), Options());

    private static ImageInfo Identify(byte[] codestream) => Image.Identify(codestream, Options());

    [Test]
    [Arguments("rgb8-lossy.jxl", ImageColorModel.Rgb, 3, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.None, ImageCompression.Lossy)]
    [Arguments("rgb8-lossless.jxl", ImageColorModel.Rgb, 3, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.None, ImageCompression.Unknown)]
    [Arguments("gray8-lossless.jxl", ImageColorModel.Gray, 1, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.None, ImageCompression.Unknown)]
    [Arguments("gray16-alpha.jxl", ImageColorModel.Gray, 2, 16, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.Straight, ImageCompression.Unknown)]
    [Arguments("rgba8-lossless.jxl", ImageColorModel.Rgb, 4, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.Straight, ImageCompression.Unknown)]
    [Arguments("float32.jxl", ImageColorModel.Rgb, 3, 32, ImageSampleFormat.Float, ImageAlphaMode.None, null)]
    public async Task Identify_ReportsModelChannelsSamplesAndCompression(string name, ImageColorModel model, int channels, int bits,
        ImageSampleFormat sample, ImageAlphaMode alpha, ImageCompression? compression)
    {
        ImageInfo info = Identify(name);
        await Assert.That(info.Color.Model).IsEqualTo(model);
        await Assert.That(info.Color.ChannelCount).IsEqualTo(channels);
        await Assert.That(info.Color.BitsPerSample).IsEqualTo(bits);
        await Assert.That(info.Color.SampleFormat).IsEqualTo(sample);
        await Assert.That(info.Color.AlphaMode).IsEqualTo(alpha);
        if (compression is { } expected)
        {
            await Assert.That(info.Compression).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Identify_MapsTheEnumeratedColourToCicp()
    {
        await Assert.That(Identify("rgb8-lossy.jxl").Color.Cicp).IsEqualTo(CicpColorDescription.Srgb);
        await Assert.That(Identify("pq-bt2100.jxl").Color.Cicp).IsEqualTo(new CicpColorDescription(9, 16, 0, true));
        await Assert.That(Identify("pq-bt2100.jxl").Color.Cicp!.IsHdrTransfer).IsTrue();
        // JPEG XL's P3 with D65 is Display P3 (CICP 12), not CICP 11.
        await Assert.That(Identify("p3-perceptual.jxl").Color.Cicp).IsEqualTo(new CicpColorDescription(12, 13, 0, true));
        await Assert.That(Identify("gray16-alpha.jxl").Color.Cicp).IsEqualTo(new CicpColorDescription(2, 13, 0, true));
        await Assert.That(Identify("gamma-custom.jxl").Color.Cicp).IsNull();
    }

    [Test]
    public async Task Identify_WithIccProfile_ReportsNoCicp()
    {
        ImageColorInfo color = Identify("icc-profile.jxl").Color;
        await Assert.That(color.HasIccProfile).IsTrue();
        await Assert.That(color.Cicp).IsNull();
        await Assert.That(Identify("rgb8-lossy.jxl").Color.HasIccProfile).IsFalse();
    }

    [Test]
    public async Task Identify_P3WithDciWhitePoint_IsCicp11()
    {
        byte[] data = JxlHeaderTests.Codestream(color: c => c.Bool(false).Bool(false).Enum(0).Enum(11).Enum(11).Bool(false).Enum(17).Enum(0));
        await Assert.That(Identify(data).Color.Cicp).IsEqualTo(new CicpColorDescription(11, 17, 0, true));
    }

    [Test]
    public async Task Identify_XybColourSpace_IsOther()
    {
        byte[] data = JxlHeaderTests.Codestream(color: c => c.Bool(false).Bool(false).Enum(2).Enum(1));
        ImageColorInfo color = Identify(data).Color;
        await Assert.That(color.Model).IsEqualTo(ImageColorModel.Other);
        await Assert.That(color.Cicp).IsNull();
    }

    [Test]
    public async Task Identify_BlackChannel_IsCmyk_AndAssociatedAlpha_IsPremultiplied()
    {
        byte[] cmyk = JxlHeaderTests.Codestream(extraChannels: e => e.U32(1).Bool(false).Enum(4).Bool(false).U32(0).U32(0).U32(0));
        ImageColorInfo color = Identify(cmyk).Color;
        await Assert.That((color.Model, color.ChannelCount, color.AlphaMode)).IsEqualTo((ImageColorModel.Cmyk, 4, ImageAlphaMode.None));

        byte[] premultiplied = JxlHeaderTests.Codestream(extraChannels: e => e.U32(1).Bool(false).Enum(0).Bool(false).U32(0).U32(0).U32(0).Bool(true));
        await Assert.That(Identify(premultiplied).Color.AlphaMode).IsEqualTo(ImageAlphaMode.Premultiplied);
    }

    [Test]
    public async Task Identify_ExtraChannelDepth_ReportsTheWidestChannel()
    {
        // 8-bit colour with a 12-bit integer alpha, then with a 16-bit float depth channel.
        byte[] alpha12 = JxlHeaderTests.Codestream(extraChannels: e => e.U32(1).Bool(false).Enum(0).Bool(false).U32(2).U32(0).U32(0).Bool(false));
        ImageColorInfo color = Identify(alpha12).Color;
        await Assert.That((color.BitsPerSample, color.SampleFormat)).IsEqualTo((12, ImageSampleFormat.UnsignedInteger));

        byte[] float16 = JxlHeaderTests.Codestream(extraChannels: e => e.U32(1).Bool(false).Enum(1).Bool(true).U32(1).Bits(4, 4).U32(0).U32(0));
        color = Identify(float16).Color;
        await Assert.That((color.BitsPerSample, color.SampleFormat, color.AlphaMode)).IsEqualTo((16, ImageSampleFormat.Float, ImageAlphaMode.None));
    }

    [Test]
    public async Task Identify_Animation_HasUnknownFrameCount()
    {
        ImageInfo info = Identify("animation.jxl");
        await Assert.That(info.IsAnimated).IsTrue();
        await Assert.That(info.FrameCount).IsNull();
    }
}
