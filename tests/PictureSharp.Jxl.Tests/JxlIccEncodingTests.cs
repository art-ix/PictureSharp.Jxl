using System.Buffers.Binary;
using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Tests.JxlLossyEncodeTests;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>ICC profiles of lossy images stored as the enumerated colour encoding they describe, as libjxl does.</summary>
public sealed class JxlIccEncodingTests
{
    private static readonly ImageConfiguration Formats = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();

    // The sRGB curve as a parametric type 3 curve: g, a, b, c, d.
    private static readonly double[] SrgbCurve = [2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045];

    // Display P3 colourants adapted to D50 and the Bradford D65 → D50 matrix, as phones embed them.
    private static readonly double[][] P3Colourants = [[0.515121, 0.241196, -0.001053], [0.291977, 0.692245, 0.041885], [0.157104, 0.066574, 0.784073]];
    private static readonly double[] BradfordD65 = [1.047882, 0.022919, -0.050217, 0.029587, 0.990479, -0.017074, -0.009232, 0.015076, 0.751678];

    /// <summary>A version 4 matrix/TRC (or gray) display profile.</summary>
    internal static byte[] Profile(double[][]? colourants, double[] curve, double[]? chad = null, uint intent = 0)
    {
        var tags = new List<(string Name, byte[] Data)>();
        byte[] Xyz(double x, double y, double z) => [.. "XYZ "u8, 0, 0, 0, 0, .. Fixed(x), .. Fixed(y), .. Fixed(z)];
        byte[] para = curve.Length == 1
            ? [.. "para"u8, 0, 0, 0, 0, 0, 0, 0, 0, .. Fixed(curve[0])]
            : [.. "para"u8, 0, 0, 0, 0, 0, 3, 0, 0, .. curve.SelectMany(Fixed)];
        tags.Add(("wtpt", Xyz(0.9642, 1.0, 0.8249)));
        if (chad is not null) tags.Add(("chad", [.. "sf32"u8, 0, 0, 0, 0, .. chad.SelectMany(Fixed)]));
        if (colourants is null)
        {
            tags.Add(("kTRC", para));
        }
        else
        {
            tags.Add(("rXYZ", Xyz(colourants[0][0], colourants[0][1], colourants[0][2])));
            tags.Add(("gXYZ", Xyz(colourants[1][0], colourants[1][1], colourants[1][2])));
            tags.Add(("bXYZ", Xyz(colourants[2][0], colourants[2][1], colourants[2][2])));
            tags.AddRange([("rTRC", para), ("gTRC", para), ("bTRC", para)]);
        }

        int offset = 132 + tags.Count * 12;
        var table = new List<byte>();
        var data = new List<byte>();
        foreach ((string name, byte[] bytes) in tags)
        {
            table.AddRange([.. System.Text.Encoding.ASCII.GetBytes(name), .. BigEndian(offset + data.Count), .. BigEndian(bytes.Length)]);
            data.AddRange(bytes);
            while (data.Count % 4 != 0) data.Add(0);
        }

        byte[] header = new byte[128];
        "mntr"u8.CopyTo(header.AsSpan(12));
        (colourants is null ? "GRAY"u8 : "RGB "u8).CopyTo(header.AsSpan(16));
        "XYZ "u8.CopyTo(header.AsSpan(20));
        "acsp"u8.CopyTo(header.AsSpan(36));
        header[8] = 4;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(64), intent);
        byte[] profile = [.. header, .. BigEndian(tags.Count), .. table, .. data];
        BinaryPrimitives.WriteUInt32BigEndian(profile, (uint)profile.Length);
        return profile;

        static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
        static byte[] Fixed(double value) => BigEndian((int)Math.Round(value * 65536));
    }

    internal static byte[] DisplayP3 => Profile(P3Colourants, SrgbCurve, BradfordD65);

    [Test]
    public async Task DisplayP3_IsEnumerated()
    {
        JxlColorEncoding? encoding = JxlIccEncoding.TryEnumerate(DisplayP3, gray: false);
        await Assert.That(encoding).IsNotNull();
        await Assert.That((encoding!.WantIcc, encoding.Primaries, encoding.WhitePoint, encoding.TransferFunction, encoding.Gamma))
            .IsEqualTo((false, JxlPrimaries.P3, JxlWhitePoint.D65, JxlTransferFunction.Srgb, 0u));
    }

    [Test]
    public async Task CurvesAndIntent_AreDetected()
    {
        // Gamma 2.2 stays a gamma (libjxl never turns it into sRGB or BT.709); 1.0 is linear; 2.6 is DCI.
        JxlColorEncoding? gamma = JxlIccEncoding.TryEnumerate(Profile(P3Colourants, [2.2], BradfordD65, intent: 0), gray: false);
        await Assert.That(gamma!.RenderingIntent).IsEqualTo(JxlRenderingIntent.Perceptual);
        await Assert.That(Math.Abs(gamma.Gamma / 1e7 - 1 / 2.2)).IsLessThan(1e-4);
        await Assert.That(JxlIccEncoding.TryEnumerate(Profile(P3Colourants, [1.0], BradfordD65), gray: false)!.TransferFunction)
            .IsEqualTo(JxlTransferFunction.Linear);
        await Assert.That(JxlIccEncoding.TryEnumerate(Profile(P3Colourants, [2.6], BradfordD65), gray: false)!.TransferFunction)
            .IsEqualTo(JxlTransferFunction.Dci);
        JxlColorEncoding? relative = JxlIccEncoding.TryEnumerate(Profile(P3Colourants, SrgbCurve, BradfordD65, intent: 1), gray: false);
        await Assert.That(relative!.RenderingIntent).IsEqualTo(JxlRenderingIntent.Relative);
        JxlColorEncoding? gray = JxlIccEncoding.TryEnumerate(Profile(null, SrgbCurve, BradfordD65), gray: true);
        await Assert.That((gray!.ColorSpace, gray.WhitePoint, gray.TransferFunction)).IsEqualTo((JxlColorSpace.Gray, JxlWhitePoint.D65, JxlTransferFunction.Srgb));
    }

    [Test]
    public async Task UndescribableProfiles_KeepTheIcc()
    {
        // A curve no enumerated transfer function matches.
        await Assert.That(JxlIccEncoding.TryEnumerate(Profile(P3Colourants, [2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.2], BradfordD65), gray: false)).IsNull();
        // An RGB profile for a gray image.
        await Assert.That(JxlIccEncoding.TryEnumerate(DisplayP3, gray: true)).IsNull();
        // Profiles libjxl itself kept as ICC in lossy files.
        byte[] rgb = Image.ReadMetadata(CorpusPath("icc-lossy-rgb.jxl"), Options()).IccProfile!;
        byte[] grayIcc = Image.ReadMetadata(CorpusPath("icc-lossy-gray.jxl"), Options()).IccProfile!;
        await Assert.That(JxlIccEncoding.TryEnumerate(rgb, gray: false)).IsNull();
        await Assert.That(JxlIccEncoding.TryEnumerate(grayIcc, gray: true)).IsNull();
    }

    [Test]
    public async Task LossyImages_StoreTheEncoding_LosslessImagesTheProfile()
    {
        byte[] rgba = ScenePixels(64, 48, Scene.Photo, alpha: false);
        using Image image = Image.LoadPixelData(rgba, 64, 48, PixelFormat.Rgba32, 64 * 4);
        image.Metadata.IccProfile = DisplayP3;
        byte[] lossy = image.ToJxl(new JxlEncoderOptions { Configuration = Formats });
        ImageMetadata metadata = Image.ReadMetadata((ReadOnlyMemory<byte>)lossy, Options());
        await Assert.That(metadata.IccProfile).IsNull();
        await Assert.That(metadata.Chromaticities).IsNotNull();
        byte[] lossless = image.ToJxl(new JxlEncoderOptions { Configuration = Formats, Distance = 0 });
        await Assert.That(Image.ReadMetadata((ReadOnlyMemory<byte>)lossless, Options()).IccProfile).IsEquivalentTo(DisplayP3);
    }

    /// <summary>
    /// djxl returns a lossy Display P3 image in Display P3, near the input; with the ICC profile in the file it would
    /// return linear sRGB.
    /// </summary>
    [Test]
    [Property("Category", "Oracle")]
    public async Task DisplayP3_DecodesInP3WithDjxl()
    {
        string? djxl = JxlDecodeOracleTests.Tool("djxl");
        Skip.Unless(djxl is not null, "djxl v0.12.0 is not available (PATH or JXL_TOOLS).");
        byte[] rgba = ScenePixels(200, 150, Scene.Photo, alpha: false);
        using Image image = Image.LoadPixelData(rgba, 200, 150, PixelFormat.Rgba32, 200 * 4);
        image.Metadata.IccProfile = DisplayP3;
        string path = Path.Combine(Path.GetTempPath(), $"jxl-p3-{Guid.NewGuid():N}.jxl");
        try
        {
            byte[] encoded = image.ToJxl(new JxlEncoderOptions { Configuration = Formats, Distance = 1 });
            File.WriteAllBytes(path, encoded);
            using Image expected = JxlDecodeOracleTests.DjxlImage(djxl!, path);
            using Image actual = Image.Load(encoded, new DecoderOptions { Configuration = Formats });
            await Assert.That(JxlDecodeTests.Tolerance(actual, expected)).IsNull();
            byte[] decoded = JxlDecodeOracleTests.Rgba(expected);
            double error = 0;
            for (int i = 0; i < rgba.Length; i++)
            {
                if (i % 4 != 3) error += (decoded[i] - rgba[i]) * (double)(decoded[i] - rgba[i]);
            }

            double psnr = 10 * Math.Log10(255.0 * 255 / (error / (rgba.Length / 4 * 3)));
            await Assert.That(psnr).IsGreaterThan(32.0);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
