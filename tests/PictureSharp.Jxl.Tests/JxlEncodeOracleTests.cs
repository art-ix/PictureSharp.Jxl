using static PictureSharp.Formats.Jxl.Tests.JxlCodestreamWriterTests;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>djxl v0.12.0 (on PATH or in JXL_TOOLS) decodes the encoder's output to the same pixels; skipped without djxl.</summary>
public sealed class JxlEncodeOracleTests
{
    [Test]
    [Property("Category", "Oracle")]
    [Arguments(1, 1, Pattern.Noise, false, false)]
    [Arguments(64, 48, Pattern.Noise, false, true)]
    [Arguments(100, 80, Pattern.FewColors, false, true)]
    [Arguments(90, 70, Pattern.Gradient, true, false)]
    [Arguments(90, 70, Pattern.FewColors, true, true)]
    [Arguments(300, 270, Pattern.Gradient, false, true)]
    [Arguments(513, 257, Pattern.FewColors, false, false)]
    [Arguments(260, 600, Pattern.Gradient, true, false)]
    public async Task Encoded_DecodesLikeDjxl(int width, int height, Pattern pattern, bool gray, bool alpha)
    {
        string? djxl = JxlDecodeOracleTests.Tool("djxl");
        Skip.Unless(djxl is not null, "djxl v0.12.0 is not available (PATH or JXL_TOOLS).");
        byte[] rgba = Pixels(width, height, pattern, alpha);
        string path = Path.Combine(Path.GetTempPath(), $"jxl-encode-{Guid.NewGuid():N}.jxl");
        try
        {
            File.WriteAllBytes(path, Encode(rgba, width, height, gray, alpha));
            using Image decoded = JxlDecodeOracleTests.DjxlImage(djxl!, path);
            byte[] actual = JxlDecodeOracleTests.Rgba(decoded);
            string result = "ok";
            for (int i = 0; i < actual.Length && result == "ok"; i++)
            {
                int c = i % 4;
                byte expected = gray && c is 1 or 2 ? rgba[i - c] : rgba[i];
                if (actual[i] != expected) result = $"sample {i}: {actual[i]} instead of {expected}";
            }

            await Assert.That(result).IsEqualTo("ok");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A written and an edited ICC profile are what djxl --orig_icc_out extracts, and the pixels stay the same.</summary>
    [Test]
    [Property("Category", "Oracle")]
    public async Task IccProfiles_MatchDjxl_AfterEncodingAndEditing()
    {
        string? djxl = JxlDecodeOracleTests.Tool("djxl");
        Skip.Unless(djxl is not null, "djxl v0.12.0 is not available (PATH or JXL_TOOLS).");
        var formats = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();
        byte[] icc = Image.ReadMetadata(JxlTestData.CorpusPath("icc-profile.jxl"), JxlTestData.Options()).IccProfile!;
        byte[] rgba = Pixels(70, 50, Pattern.Gradient, alpha: false);
        using Image image = Image.LoadPixelData(rgba, 70, 50, PixelFormat.Rgba32, 280);
        image.Metadata.IccProfile = icc;
        byte[] encoded = image.ToJxl(new JxlEncoderOptions { Configuration = formats });
        byte[] edited = Image.EditMetadata((ReadOnlyMemory<byte>)encoded, metadata =>
        {
            byte[] changed = metadata.IccProfile!;
            changed[^1] ^= 1;
            metadata.IccProfile = changed;
            metadata.Xmp = "<x/>"u8.ToArray();
        }, JxlTestData.Options());
        byte[] expectedEdited = (byte[])icc.Clone();
        expectedEdited[^1] ^= 1;
        foreach ((byte[] file, byte[] profile) in new[] { (encoded, icc), (edited, expectedEdited) })
        {
            string path = Path.Combine(Path.GetTempPath(), $"jxl-encode-{Guid.NewGuid():N}");
            try
            {
                File.WriteAllBytes(path + ".jxl", file);
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(djxl!,
                    [path + ".jxl", path + ".png", "--orig_icc_out=" + path + ".icc", "--quiet"]) { RedirectStandardError = true })!;
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                await Assert.That(File.ReadAllBytes(path + ".icc")).IsEquivalentTo(profile);
                using Image decoded = Image.Load(path + ".png");
                await Assert.That(JxlDecodeOracleTests.Rgba(decoded)).IsEquivalentTo(rgba);
            }
            finally
            {
                foreach (string extension in new[] { ".jxl", ".png", ".icc" }) File.Delete(path + extension);
            }
        }
    }
}
