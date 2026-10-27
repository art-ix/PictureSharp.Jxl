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

    /// <summary>The RGBA pixels djxl renders as an 8-bit PNG (its PAM output ignores --bits_per_sample), read by the core.</summary>
    internal static (int Width, int Height, byte[] Rgba) Djxl(string djxl, string file)
    {
        string output = Path.Combine(Path.GetTempPath(), $"jxl-oracle-{Guid.NewGuid():N}.png");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(djxl, [file, output, "--bits_per_sample=8", "--quiet"])
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

            using Image reference = Image.Load(output);
            return (reference.Width, reference.Height, Rgba(reference));
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

    /// <summary>Every corpus file this version decodes matches djxl exactly; the others are reported unsupported.</summary>
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
                (int width, int height, byte[] expected) = Djxl(djxl!, path);
                byte[] actual = Rgba(image);
                int first = actual.AsSpan().CommonPrefixLength(expected);
                if (image.Width != width || image.Height != height || first != expected.Length)
                {
                    mismatches.Add($"{name}: {image.Width}x{image.Height} vs {width}x{height}, first difference at byte {first}");
                }

                decoded.Add(name);
            }
        }

        Console.WriteLine($"Decoded like djxl: {string.Join(", ", decoded)}");
        await Assert.That(mismatches).IsEmpty();
    }
}
