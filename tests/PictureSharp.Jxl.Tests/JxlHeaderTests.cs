using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

public sealed class JxlHeaderTests
{
    /// <summary>
    /// An 8×8 codestream with non-default ImageMetadata (extra fields present); each part can be replaced. The
    /// defaults are: 8-bit integer samples, no extra channels, not XYB, default colour and tone mapping.
    /// </summary>
    internal static byte[] Codestream(
        int orientation = 1,
        Action<BitWriter>? size = null,
        Action<BitWriter>? animation = null,
        Action<BitWriter>? bitDepth = null,
        Action<BitWriter>? extraChannels = null,
        bool xyb = false,
        Action<BitWriter>? color = null,
        Action<BitWriter>? tone = null,
        Action<BitWriter>? extensions = null,
        Action<BitWriter>? transform = null,
        Action<BitWriter>? tail = null)
    {
        var w = new BitWriter().Bits(0x0AFF, 16);
        (size ?? (s => s.Bool(true).Bits(0, 5).Bits(1, 3)))(w);
        w.Bool(false).Bool(true).Bits((ulong)orientation - 1, 3).Bool(false).Bool(false);
        if (animation is null)
        {
            w.Bool(false);
        }
        else
        {
            animation(w.Bool(true));
        }

        (bitDepth ?? (b => b.Bool(false).U32(0)))(w);
        w.Bool(true);
        (extraChannels ?? (e => e.U32(0)))(w);
        w.Bool(xyb);
        (color ?? (c => c.Bool(true)))(w);
        (tone ?? (t => t.Bool(true)))(w);
        (extensions ?? (x => x.U64(0)))(w);
        (transform ?? (t => t.Bool(true)))(w);
        tail?.Invoke(w);
        return w.ToArray();
    }

    private static JxlHeaders Read(byte[] codestream) => JxlHeadersReader.Read(new JxlCodestream(codestream));

    [Test]
    public async Task MinimalCodestream_HasDefaults()
    {
        JxlHeaders headers = Read(Minimal());
        await Assert.That(headers.Size).IsEqualTo(new JxlSizeHeader(8, 8));
        await Assert.That(headers.Metadata).IsSameReferenceAs(JxlImageMetadata.Default);
        await Assert.That(headers.Metadata.XybEncoded).IsTrue();
        await Assert.That(headers.Metadata.Color).IsSameReferenceAs(JxlColorEncoding.Srgb);
        await Assert.That(headers.TransformData).IsSameReferenceAs(JxlCustomTransformData.Default);
        await Assert.That(headers.EndBitPosition).IsEqualTo(32L);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    public async Task Orientation_IsReportedByIdentify(int orientation)
    {
        ImageInfo info = Image.Identify(Codestream(orientation), Options());
        await Assert.That(info.Orientation).IsEqualTo((ExifOrientation)orientation);
        await Assert.That((info.Width, info.Height)).IsEqualTo((8, 8));
    }

    [Test]
    public async Task SizeHeader_SmallRatioAndLargeForms()
    {
        byte[] small = Codestream(size: s => s.Bool(true).Bits(31, 5).Bits(0, 3).Bits(2, 5));
        await Assert.That(Read(small).Size).IsEqualTo(new JxlSizeHeader(24, 256));
        foreach ((int ratio, uint width) in new[] { (1, 90u), (2, 108u), (3, 120u), (4, 135u), (5, 160u), (6, 112u), (7, 180u) })
        {
            byte[] withRatio = Codestream(size: s => s.Bool(false).U32(0, 89, 9).Bits((ulong)ratio, 3));
            await Assert.That(Read(withRatio).Size).IsEqualTo(new JxlSizeHeader(width, 90));
        }

        byte[] explicitSize = Codestream(size: s => s.Bool(false).U32(1, 4999, 13).Bits(0, 3).U32(2, 200_000, 18));
        await Assert.That(Read(explicitSize).Size).IsEqualTo(new JxlSizeHeader(200_001, 5000));
    }

    [Test]
    public async Task HugeDimensions_AreLimited()
    {
        // Height 2^30 with a 2:1 aspect ratio: width 2^31 does not fit an Int32.
        byte[] huge = Codestream(size: s => s.Bool(false).U32(3, (1 << 30) - 1, 30).Bits(7, 3));
        await Assert.That(Read(huge).Size).IsEqualTo(new JxlSizeHeader(1u << 31, 1u << 30));
        await Assert.That(() => Image.Identify(huge, Options())).Throws<ImageFormatException>();
        await Assert.That(() => Image.Identify(huge, Options(new DecoderOptions { MaxPixels = long.MaxValue }))).Throws<NotSupportedException>();
        await Assert.That(() => Image.Identify(Minimal(), Options(new DecoderOptions { MaxPixels = 63 }))).Throws<ImageFormatException>();
        await Assert.That(Image.Identify(Minimal(), Options(new DecoderOptions { MaxPixels = 64 })).Width).IsEqualTo(8);
    }

    [Test]
    public async Task BitDepth_ValidAndInvalid()
    {
        await Assert.That(Read(Codestream(bitDepth: b => b.Bool(false).U32(3, 30, 6))).Metadata.BitDepth).IsEqualTo(new JxlBitDepth(false, 31, 0));
        await Assert.That(Read(Codestream(bitDepth: b => b.Bool(true).U32(1).Bits(4, 4))).Metadata.BitDepth).IsEqualTo(new JxlBitDepth(true, 16, 5));
        await Assert.That(Image.Identify(Codestream(bitDepth: b => b.Bool(true).U32(0).Bits(7, 4)), Options()).Color.BitsPerSample).IsEqualTo(32);
        foreach (Action<BitWriter> invalid in new Action<BitWriter>[]
        {
            b => b.Bool(false).U32(3, 31, 6),  // 32-bit integers
            b => b.Bool(true).U32(0).Bits(0, 4),  // 1 exponent bit
            b => b.Bool(true).U32(0).Bits(8, 4),  // 9 exponent bits
            b => b.Bool(true).U32(3, 3, 6).Bits(1, 4),  // 4 bits: 2 exponent bits leave 1 mantissa bit
        })
        {
            await Assert.That(() => Read(Codestream(bitDepth: invalid))).Throws<ImageFormatException>();
        }
    }

    [Test]
    public async Task ExtraChannels_AreParsed()
    {
        byte[] data = Codestream(extraChannels: e => e.U32(2, 2, 4)
            // Alpha, 16-bit, associated, named "a".
            .Bool(false).Enum(0).Bool(false).U32(3, 15, 6).U32(0).U32(1, 1, 4).Bits('a', 8).Bool(true)
            // Spot colour at half resolution.
            .Bool(false).Enum(2).Bool(false).U32(0).U32(3, 0, 3).U32(0).F16((Half)0.25).F16((Half)0.5).F16((Half)1).F16((Half)0.75)
            // CFA channel 7, default bit depth.
            .Bool(false).Enum(5).Bool(false).U32(0).U32(0).U32(0).U32(2, 4, 4)
            // Depth, all default but the type.
            .Bool(false).Enum(1).Bool(false).U32(0).U32(1).U32(0));
        IReadOnlyList<JxlExtraChannelInfo> channels = Read(data).Metadata.ExtraChannels;
        await Assert.That(channels.Count).IsEqualTo(4);
        await Assert.That((channels[0].Type, channels[0].BitDepth.BitsPerSample, channels[0].Name, channels[0].AlphaAssociated))
            .IsEqualTo((JxlExtraChannelType.Alpha, 16u, "a", true));
        await Assert.That((channels[1].Type, channels[1].DimShift)).IsEqualTo((JxlExtraChannelType.SpotColor, 1u));
        await Assert.That(channels[1].SpotColor!).IsEquivalentTo(new[] { 0.25f, 0.5f, 1f, 0.75f });
        await Assert.That((channels[2].Type, channels[2].CfaChannel)).IsEqualTo((JxlExtraChannelType.Cfa, 7u));
        await Assert.That((channels[3].Type, channels[3].DimShift)).IsEqualTo((JxlExtraChannelType.Depth, 3u));
        ImageInfo info = Image.Identify(data, Options());
        await Assert.That(info.Color.HasAlpha).IsTrue();
    }

    [Test]
    [Arguments(7u, 0)]
    [Arguments(14u, 0)]
    [Arguments(15u, 0)]
    [Arguments(17u, 0)]
    [Arguments(1u, 4)]
    public async Task ExtraChannels_ReservedTypesAndLargeShifts_AreCorrupt(uint type, int shiftOffset)
    {
        byte[] data = Codestream(extraChannels: e => e.U32(1).Bool(false).Enum(type).Bool(false).U32(0).U32(3, (ulong)shiftOffset, 3).U32(0));
        await Assert.That(() => Read(data)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task Color_CustomDescription()
    {
        byte[] data = Codestream(color: c =>
        {
            c.Bool(false).Bool(false).Enum(0).Enum(2);
            Xy(c, 312_700);
            Xy(c, 329_000);
            c.Enum(2);
            foreach (int value in new[] { 640_000, 330_000, 300_000, 600_000, 150_000, -60_000 })
            {
                Xy(c, value);
            }

            c.Bool(true).Bits(4_545_500, 24).Enum(3);
        });
        JxlColorEncoding color = Read(data).Metadata.Color;
        await Assert.That((color.ColorSpace, color.WhitePoint, color.Primaries, color.Gamma, color.RenderingIntent))
            .IsEqualTo((JxlColorSpace.Rgb, JxlWhitePoint.Custom, JxlPrimaries.Custom, 4_545_500u, JxlRenderingIntent.Absolute));
        await Assert.That(color.CustomWhitePoint).IsEqualTo(new JxlChromaticity(312_700, 329_000));
        await Assert.That(color.CustomPrimaries).IsEqualTo((new JxlChromaticity(640_000, 330_000), new JxlChromaticity(300_000, 600_000), new JxlChromaticity(150_000, -60_000)));
    }

    // A Customxy coordinate: zigzag-packed, then the smallest of the four U32 distributions.
    private static void Xy(BitWriter writer, int value)
    {
        ulong packed = (ulong)(value < 0 ? -2L * value - 1 : 2L * value);
        _ = packed switch
        {
            < 1 << 19 => writer.U32(0, packed, 19),
            < 1 << 20 => writer.U32(1, packed - (1 << 19), 19),
            < 1 << 21 => writer.U32(2, packed - (1 << 20), 20),
            _ => writer.U32(3, packed - (1 << 21), 21)
        };
    }

    [Test]
    public async Task Color_GrayAndXybUseImplicitFields()
    {
        JxlColorEncoding gray = Read(Codestream(color: c => c.Bool(false).Bool(false).Enum(1).Enum(11).Bool(false).Enum(8).Enum(0))).Metadata.Color;
        await Assert.That((gray.ColorSpace, gray.WhitePoint, gray.HasPrimaries, gray.TransferFunction)).IsEqualTo((JxlColorSpace.Gray, JxlWhitePoint.Dci, false, JxlTransferFunction.Linear));
        JxlColorEncoding xyb = Read(Codestream(color: c => c.Bool(false).Bool(false).Enum(2).Enum(1))).Metadata.Color;
        await Assert.That((xyb.ColorSpace, xyb.WhitePoint, xyb.Gamma, xyb.RenderingIntent)).IsEqualTo((JxlColorSpace.Xyb, JxlWhitePoint.D65, 3_333_333u, JxlRenderingIntent.Relative));
    }

    [Test]
    public async Task Color_InvalidDescriptions_AreCorrupt()
    {
        foreach (Action<BitWriter> invalid in new Action<BitWriter>[]
        {
            c => c.Bool(false).Bool(false).Enum(4),  // colour space
            c => c.Bool(false).Bool(false).Enum(3).Enum(1).Enum(1).Bool(false).Enum(13).Enum(0),  // unknown colour space without ICC
            c => c.Bool(false).Bool(false).Enum(0).Enum(1).Enum(1).Bool(false).Enum(2).Enum(0),  // unknown transfer without ICC
            c => c.Bool(false).Bool(false).Enum(0).Enum(3),  // white point
            c => c.Bool(false).Bool(false).Enum(0).Enum(1).Enum(3),  // primaries
            c => c.Bool(false).Bool(false).Enum(0).Enum(1).Enum(1).Bool(false).Enum(3),  // transfer function
            c => c.Bool(false).Bool(false).Enum(0).Enum(1).Enum(1).Bool(false).Enum(13).Enum(4),  // rendering intent
            c => c.Bool(false).Bool(false).Enum(0).Enum(1).Enum(1).Bool(true).Bits(10_000_001, 24),  // gamma above 1
            c => c.Bool(false).Bool(false).Enum(0).Enum(1).Enum(1).Bool(true).Bits(1220, 24),  // gamma below 1/8192
            c => c.Bool(false).Bool(false).Enum(0).Enum(2).U32(0).U32(0, 2, 19),  // white point x = 0
        })
        {
            await Assert.That(() => Read(Codestream(color: invalid))).Throws<ImageFormatException>();
        }
    }

    [Test]
    public async Task IccProfile_SizeIsRead()
    {
        byte[] data = Codestream(color: c => c.Bool(false).Bool(true).Enum(0), tail: t => t.U64(272));
        JxlHeaders headers = Read(data);
        await Assert.That(headers.Metadata.Color.WantIcc).IsTrue();
        await Assert.That(headers.IccSize).IsEqualTo(272UL);
        await Assert.That(() => Read(Codestream(color: c => c.Bool(false).Bool(true).Enum(0), tail: t => t.U64(0)))).Throws<ImageFormatException>();
    }

    [Test]
    public async Task ToneMapping_AnimationAndExtensions()
    {
        byte[] data = Codestream(
            animation: a => a.U32(1).U32(2, 0, 8).U32(1, 5, 3).Bool(true),
            tone: t => t.Bool(false).F16((Half)1000).F16((Half)0.5).Bool(true).F16((Half)0.25),
            extensions: x => x.U64(0b1000_0001).U64(5).U64(20).Bits(0x1FFFFFF, 25));
        JxlImageMetadata metadata = Read(data).Metadata;
        await Assert.That(metadata.Animation).IsEqualTo(new JxlAnimationHeader(1000, 1, 5, true));
        await Assert.That(metadata.ToneMapping).IsEqualTo(new JxlToneMapping(1000, 0.5f, true, 0.25f));
        await Assert.That(metadata.Extensions).IsEqualTo(0b1000_0001UL);
        ImageInfo info = Image.Identify(data, Options());
        await Assert.That(info.IsAnimated).IsTrue();
        await Assert.That(info.FrameCount).IsNull();

        foreach (Action<BitWriter> invalid in new Action<BitWriter>[]
        {
            t => t.Bool(false).F16((Half)0),
            t => t.Bool(false).F16((Half)100).F16((Half)101),
            t => t.Bool(false).F16((Half)100).F16((Half)(-1)),
            t => t.Bool(false).F16((Half)100).F16((Half)0).Bool(true).F16((Half)1.5),
            t => t.Bool(false).F16((Half)100).F16((Half)0).Bool(false).F16((Half)(-0.5)),
        })
        {
            await Assert.That(() => Read(Codestream(tone: invalid))).Throws<ImageFormatException>();
        }
    }

    [Test]
    public async Task TransformData_CustomWeightsAndOpsin()
    {
        byte[] data = Codestream(xyb: true, transform: t =>
        {
            t.Bool(false).Bool(false);
            for (int i = 0; i < 16; i++)
            {
                t.F16((Half)(i / 16.0));
            }

            t.Bits(0b101, 3);
            for (int i = 0; i < 15 + 210; i++)
            {
                t.F16((Half)(-i / 512.0));
            }
        });
        JxlCustomTransformData transform = Read(data).TransformData;
        await Assert.That(transform.OpsinInverseMatrix!.Count).IsEqualTo(9);
        await Assert.That(transform.QuantBiases![3]).IsEqualTo(15 / 16f);
        await Assert.That(transform.Upsampling4Weights).IsNull();
        await Assert.That(transform.Upsampling8Weights![209]).IsEqualTo((float)(Half)(-224 / 512.0));
    }

    [Test]
    public async Task Padding_AfterTheHeaders_MustBeZero()
    {
        byte[] data = Minimal();
        data[^1] |= 0x80;
        await Assert.That(() => Read(data)).Throws<ImageFormatException>();
        await Assert.That(() => Read([0xFF, 0x0B, 0])).Throws<ImageFormatException>();
    }

    [Test]
    public async Task EveryHeaderPrefix_IsCorrupt_AndIdentifyThrowsOnlyFormatErrors()
    {
        byte[] data = Codestream(extraChannels: e => e.U32(1).Bool(true), color: c => c.Bool(false).Bool(false).Enum(0).Enum(1).Enum(9).Bool(false).Enum(16).Enum(1));
        await Assert.That(Read(data).Metadata.Color.TransferFunction).IsEqualTo(JxlTransferFunction.Pq);
        for (int length = 0; length < data.Length; length++)
        {
            await Assert.That(() => Image.Identify(data.AsMemory(0, length), Options())).Throws<ImageFormatException>();
        }
    }
}
