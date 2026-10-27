using System.Diagnostics;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>
/// Compares decoded pixels with djxl v0.12.0 (on PATH or in JXL_TOOLS) rendering 8-bit PNG; skipped without djxl.
/// djxl applies the orientation, so the images are loaded with <see cref="DecoderOptions.ApplyExifOrientation"/>.
/// JXL_ORACLE_DIR adds the .jxl files of a local directory (external corpora are never committed).
/// </summary>
public sealed class JxlDecodeOracleTests
{
    internal static string? Tool(string name)
    {
        string? tools = Environment.GetEnvironmentVariable("JXL_TOOLS");
        IEnumerable<string> directories = tools is not null
            ? [tools]
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        return directories.Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
    }

    /// <summary>The image djxl renders as an 8-bit PNG (its PAM output ignores --bits_per_sample), read by the core.</summary>
    internal static Image DjxlImage(string djxl, string file, params string[] arguments)
    {
        string output = Path.Combine(Path.GetTempPath(), $"jxl-oracle-{Guid.NewGuid():N}.png");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(djxl, [file, output, "--bits_per_sample=8", "--quiet", .. arguments])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"djxl failed: {error}");
            }

            return Image.Load(output);
        }
        finally
        {
            File.Delete(output);
        }
    }

    internal static byte[] Rgba(Image image)
    {
        byte[] rgba = new byte[image.Width * image.Height * 4];
        int i = 0;
        foreach (Rgba32 pixel in image.GetPixelMemory().Span)
        {
            rgba[i++] = pixel.R;
            rgba[i++] = pixel.G;
            rgba[i++] = pixel.B;
            rgba[i++] = pixel.A;
        }

        return rgba;
    }

    /// <summary>
    /// Every corpus file this version decodes matches djxl: lossless ones exactly, lossy ones within
    /// <see cref="JxlDecodeTests.Tolerance"/>; the others are reported unsupported.
    /// </summary>
    /// <summary>
    /// Null when an image rendered in its ICC profile and converted to sRGB by PictureSharp is close to djxl's sRGB
    /// output: mean difference at most 0.5 and 99% of the samples within 2. Both conversions are exact to about 1, but
    /// the intermediate 8-bit samples of a wider gamut amplify near the edge of sRGB (several levels in dark channels).
    /// The profile rendering itself is checked within 1 against cms/*.png.
    /// </summary>
    internal static string? ConvertedTolerance(Image actual, Image expected)
    {
        if ((actual.Width, actual.Height) != (expected.Width, expected.Height))
        {
            return $"{actual.Width}x{actual.Height} instead of {expected.Width}x{expected.Height}";
        }

        byte[] a = Rgba(actual);
        byte[] e = Rgba(expected);
        long total = 0;
        int far = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int difference = Math.Abs(a[i] - e[i]);
            total += difference;
            far += difference > 2 ? 1 : 0;
        }

        return total * 2 > a.Length || far * 100L > a.Length ? $"mean {(double)total / a.Length:F3}, {far} samples differ by more than 2" : null;
    }

    [Test]
    [Property("Category", "Oracle")]
    public async Task Corpus_DecodesLikeDjxl()
    {
        string? djxl = Tool("djxl");
        Skip.Unless(djxl is not null, "djxl v0.12.0 is not available (PATH or JXL_TOOLS).");
        var mismatches = new List<string>();
        var decoded = new List<string>();
        string? extra = Environment.GetEnvironmentVariable("JXL_ORACLE_DIR");
        IEnumerable<string> files = Directory.GetFiles(CorpusPath(""), "*.jxl")
            .Concat(extra is not null ? Directory.GetFiles(extra, "*.jxl") : []);
        foreach (string path in files.Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            Image image;
            try
            {
                image = Image.Load(path, Options() with { ApplyExifOrientation = true });
            }
            catch (NotSupportedException exception)
            {
                Console.WriteLine($"{name}: not supported ({exception.Message})");
                continue;
            }
            catch (Exception exception)
            {
                mismatches.Add($"{name}: {exception.GetType().Name} {exception.Message}");
                continue;
            }

            using (image)
            {
                if (image.Metadata.IccProfile is not null && Image.Identify(path, Options()).Compression == ImageCompression.Lossy)
                {
                    // XYB with an ICC profile is rendered in the profile; djxl needs its colour management system.
                    using Image converted = Image.Load(path, Options() with { ApplyExifOrientation = true, ColorConversion = ColorConversionMode.ToSrgb });
                    using Image srgb = DjxlImage(djxl!, path, "--color_space=RGB_D65_SRG_Rel_SRG");
                    if (ConvertedTolerance(converted, srgb) is { } converted8)
                    {
                        mismatches.Add($"{name}: {converted8}");
                    }

                    decoded.Add(name);
                    continue;
                }

                using Image expected = DjxlImage(djxl!, path);
                if (JxlDecodeTests.Tolerance(image, expected) is { } difference)
                {
                    mismatches.Add($"{name}: {difference}");
                }

                decoded.Add(name);
            }
        }

        Console.WriteLine($"Decoded like djxl: {string.Join(", ", decoded)}");
        await Assert.That(mismatches).IsEmpty();
    }

    /// <summary>Every embedded ICC profile is decoded byte for byte like djxl --orig_icc_out.</summary>
    [Test]
    [Property("Category", "Oracle")]
    public async Task Icc_ProfilesMatchDjxl()
    {
        string? djxl = Tool("djxl");
        Skip.Unless(djxl is not null, "djxl v0.12.0 is not available (PATH or JXL_TOOLS).");
        string? extra = Environment.GetEnvironmentVariable("JXL_ORACLE_DIR");
        var compared = new List<string>();
        foreach (string path in Directory.GetFiles(CorpusPath(""), "*.jxl").Concat(extra is not null ? Directory.GetFiles(extra, "*.jxl") : []))
        {
            if (!Image.Identify(path, Options()).Color.HasIccProfile)
            {
                continue;
            }

            string output = Path.Combine(Path.GetTempPath(), $"jxl-oracle-{Guid.NewGuid():N}");
            try
            {
                using var process = Process.Start(new ProcessStartInfo(djxl!, [path, output + ".ppm", "--orig_icc_out=" + output + ".icc", "--quiet"])
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })!;
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                byte[] expected = File.ReadAllBytes(output + ".icc");
                await Assert.That(Image.ReadMetadata(path, Options()).IccProfile).IsEquivalentTo(expected);
                compared.Add(Path.GetFileName(path));
            }
            finally
            {
                File.Delete(output + ".ppm");
                File.Delete(output + ".icc");
            }
        }

        Console.WriteLine($"ICC profiles like djxl: {string.Join(", ", compared)}");
        await Assert.That(compared.Count).IsGreaterThanOrEqualTo(3);
    }
}
