using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>
/// Differences from djxl found by the 0.8.8 differential targets (docs/design/0.8.8-differential.md), in corpus/diff
/// with djxl's 8-bit rendering next to each input, so that the check needs no libjxl.
/// </summary>
public sealed class JxlDiffRegressionTests
{
    public static IEnumerable<string> Files() =>
        Directory.GetFiles(CorpusPath("diff"), "orientation-*.jxl").Select(path => Path.Combine("diff", Path.GetFileName(path)))
            // Recompressed JPEGs with orientation 6, dithered from the bottom row like orientation-dither-12bit.
            .Concat(["transcoding/metadata-0.jxl", "transcoding/metadata-1.jxl"])
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// libjxl dithers 8-bit output from the bottom row when the orientation it undoes flips the image vertically
    /// (3, 4, 6, 7) and from the last column when it flips it horizontally (2, 3, 7, 8); a decode that applies the
    /// orientation matches djxl within the usual tolerance only that way.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task OrientedDecode_DithersLikeDjxl(string name)
    {
        using Image image = Image.Load(CorpusPath(name), Options() with { ApplyExifOrientation = true });
        using Image expected = Image.Load(CorpusPath(Path.ChangeExtension(name, ".png")));
        await Assert.That(JxlDecodeTests.Tolerance(image, expected)).IsNull();
    }

    /// <summary>
    /// A JPEG whose DC values exceed ±2047 is rejected by FromJpeg: reconstruction cannot write them, so the file would
    /// fail in ToJpeg (and in djxl, which cjxl's file fails in too).
    /// </summary>
    [Test]
    public async Task FromJpeg_RejectsDcValuesItCannotReconstruct()
    {
        byte[] jpeg = File.ReadAllBytes(CorpusPath(Path.Combine("diff", "fromjpeg-dc-out-of-range.jpg")));
        await Assert.That(() => JxlTranscoder.FromJpeg(jpeg, new MemoryStream())).Throws<ImageFormatException>()
            .WithMessageContaining("DC coefficient out of range");
    }

    /// <summary>
    /// cjxl records extra zero runs that cover more than the zeros left in a block; djxl then writes no end of band, and
    /// ToJpeg must reconstruct the JPEG byte for byte the same way.
    /// </summary>
    [Test]
    public async Task ToJpeg_ExtraZeroRunsBeyondTheBlock_ReconstructLikeDjxl()
    {
        using var output = new MemoryStream();
        JxlTranscoder.ToJpeg(File.ReadAllBytes(CorpusPath(Path.Combine("diff", "tojpeg-extra-zero-runs.jxl"))), output);
        await Assert.That(output.ToArray()).IsEquivalentTo(File.ReadAllBytes(CorpusPath(Path.Combine("diff", "tojpeg-extra-zero-runs.jpg"))));
    }
}
