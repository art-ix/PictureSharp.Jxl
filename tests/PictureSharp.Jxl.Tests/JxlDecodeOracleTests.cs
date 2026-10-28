using System.Diagnostics;
using PictureSharp.Formats.Jxl.Codestream.Frames;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Container;
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
        foreach (Rgba32 pixel in image.GetPixelMemory<Rgba32>().Span)
        {
            rgba[i++] = pixel.R;
            rgba[i++] = pixel.G;
            rgba[i++] = pixel.B;
            rgba[i++] = pixel.A;
        }

        return rgba;
    }

    /// <summary>
    /// The djxl --color_space (sRGB, or gray with the sRGB curve) that ToSrgb renders directly when the last frame is XYB
    /// and the image is not plain sRGB, as djxl does without a colour management system; otherwise null.
    /// </summary>
    internal static string? SrgbColorSpace(string path)
    {
        JxlCodestream codestream = JxlContainer.Parse(File.ReadAllBytes(path)).Codestream;
        JxlHeaders headers = JxlHeadersReader.Read(codestream);
        JxlColorEncoding color = headers.Metadata.Color;
        bool srgb = !color.WantIcc && color.Gamma == 0 && color.TransferFunction == JxlTransferFunction.Srgb
            && (color.ColorSpace == JxlColorSpace.Gray || color.Primaries == JxlPrimaries.Srgb && color.WhitePoint == JxlWhitePoint.D65);
        if (srgb || JxlDecodeSession.ReadFrameHeaders(codestream, headers)[^1].ColorTransform != JxlColorTransform.Xyb)
        {
            return null;
        }

        return color.ColorSpace == JxlColorSpace.Gray ? "Gra_D65_Rel_SRG" : "RGB_D65_SRG_Rel_SRG";
    }

    /// <summary>
    /// Every corpus file this version decodes matches djxl: lossless ones exactly, lossy ones within
    /// <see cref="JxlDecodeTests.Tolerance"/>; the others are reported unsupported. XYB images outside sRGB are also
    /// converted with ToSrgb and compared with djxl --color_space=sRGB within the same tolerance.
    /// </summary>
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
                string? colorSpace = SrgbColorSpace(path);
                if (colorSpace is not null)
                {
                    using Image converted = Image.Load(path, Options() with { ApplyExifOrientation = true, ColorConversion = ColorConversionMode.ToSrgb });
                    using Image srgb = DjxlImage(djxl!, path, "--color_space=" + colorSpace);
                    if (JxlDecodeTests.Tolerance(converted, srgb) is { } converted8)
                    {
                        mismatches.Add($"{name} (ToSrgb): {converted8}");
                    }
                }

                if (colorSpace is not null && image.Metadata.IccProfile is not null)
                {
                    // Rendered in the profile; djxl would need its colour management system (cms/*.png covers it).
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
