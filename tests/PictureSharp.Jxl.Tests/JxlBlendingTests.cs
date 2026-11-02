using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Color;
using PictureSharp.Formats.Jxl.Codestream.Frames;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Rendering;
using PictureSharp.Formats.Jxl.Container;
using PictureSharp.Formats.Jxl.Integration;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>
/// Layered still images (0.8.10): frames blended onto reference slots like libjxl's coalescing. The corpus files
/// layers-*.jxl (jxl_from_tree) are compared with djxl by <see cref="JxlDecodeTests"/>; these tests cover the rest.
/// </summary>
public sealed class JxlBlendingTests
{
    [Test]
    public async Task Layers_AreStillImagesOfOneFrame()
    {
        ImageInfo info = Image.Identify(CorpusPath("layers-three.jxl"), Options());
        await Assert.That((info.FrameCount, info.Width, info.Height)).IsEqualTo(((int?)1, 64, 48));
        List<JxlFrameHeader> frames = Frames("layers-three.jxl");
        await Assert.That(frames.Count).IsEqualTo(3);
        await Assert.That(frames.Select(JxlBlending.NeedsBlending)).IsEquivalentTo([false, true, true]);
        await Assert.That(JxlBlending.NeedsBlending(Frames("rgb8-lossless.jxl")[^1])).IsFalse();
    }

    [Test]
    [Arguments("lossy-p3.jxl")]
    [Arguments("icc-lossy-rgb.jxl")]
    [Arguments("icc-lossy-gray.jxl")]
    public async Task CanvasToSrgb_MatchesTheDirectSrgbRendering(string name)
    {
        // A blended XYB canvas is in the image's encoding; converting it must give what djxl renders for sRGB.
        JxlCodestream codestream = JxlContainer.Parse(File.ReadAllBytes(CorpusPath(name))).Codestream;
        JxlHeaders headers = JxlHeadersReader.Read(codestream);
        var session = new JxlDecodeSession(codestream, headers);
        JxlDecodedImage decoded = session.Decode();
        bool gray = headers.Metadata.Color.ColorSpace == JxlColorSpace.Gray;
        JxlPlane[] color = decoded.Color.Length == 3 ? [.. decoded.Color] : [decoded.Color[0], decoded.Color[0], decoded.Color[0]];
        JxlColorEncoding encoding = JxlCanvasToSrgb.Convert(color, decoded.ColorEncoding,
            JxlColorTarget.Profile(session.IccProfile, gray).Icc, CancellationToken.None, -1);
        await Assert.That((encoding.WantIcc, encoding.TransferFunction, encoding.Primaries)).IsEqualTo((false, JxlTransferFunction.Srgb, JxlPrimaries.Srgb));
        var converted = new JxlDecodedImage(decoded.Width, decoded.Height, color, decoded.Alpha, gray, encoding);
        using Image image = JxlPixelWriter.Write(converted, CancellationToken.None);
        using Image expected = Image.Load(CorpusPath(Path.Combine("srgb", Path.ChangeExtension(name, ".png"))));
        // Through an ICC profile the samples pass its tone curves twice (encoded, then decoded through the sampled
        // curve), so a few more of them are off by 1 than the 0.1 % of the direct rendering (17 of 12288 for RGB).
        string? mismatch = encoding != decoded.ColorEncoding && decoded.ColorEncoding.WantIcc
            ? JxlDecodeTests.ManagedTolerance(image, expected)
            : JxlDecodeTests.Tolerance(image, expected);
        await Assert.That(mismatch).IsNull();
    }

    [Test]
    public async Task Layers_ToSrgbOfAnSrgbImageIsTheSameImage()
    {
        using Image plain = Image.Load(CorpusPath("layers-xyb.jxl"), Options());
        using Image converted = Image.Load(CorpusPath("layers-xyb.jxl"), Options() with { ColorConversion = ColorConversionMode.ToSrgb });
        await Assert.That(JxlDecodeOracleTests.Rgba(converted)).IsEquivalentTo(JxlDecodeOracleTests.Rgba(plain));
        await Assert.That(converted.Metadata.IccProfile).IsNull();
    }

    [Test]
    public async Task Layers_HonourCancellation()
    {
        // Cancelled while the second frame is blended: every row band checks the token.
        JxlCodestream codestream = JxlContainer.Parse(File.ReadAllBytes(CorpusPath("layers-three.jxl"))).Codestream;
        JxlHeaders headers = JxlHeadersReader.Read(codestream);
        using var cancellation = new CancellationTokenSource();
        var session = new JxlDecodeSession(codestream, headers, cancellation.Token);
        cancellation.Cancel();
        await Assert.That(session.Decode).Throws<OperationCanceledException>();
    }

    private static List<JxlFrameHeader> Frames(string name)
    {
        JxlCodestream codestream = JxlContainer.Parse(File.ReadAllBytes(CorpusPath(name))).Codestream;
        return JxlDecodeSession.ReadFrameHeaders(codestream, JxlHeadersReader.Read(codestream));
    }
}
