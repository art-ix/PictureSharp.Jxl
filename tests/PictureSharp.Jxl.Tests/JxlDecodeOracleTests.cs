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

    /// <summary>
    /// The oracle rendering: djxl's, or for an image with premultiplied alpha jxl_dec_straight's (tests/assets/tools,
    /// djxl's rendering with the alpha made straight, which djxl cannot do; 0.8.16), every frame in one image.
    /// </summary>
    internal static Image ReferenceImage(string djxl, string file)
    {
        if (!IsPremultiplied(file)) return DjxlImage(djxl, file);
        List<Image> frames = StraightFrames(file) ?? throw new InvalidOperationException("jxl_dec_straight is not available (PATH or JXL_TOOLS).");
        Image image = frames[0];
        foreach (Image frame in frames.Skip(1))
        {
            using (frame)
            {
                var copy = new ImageFrame(frame.Width, frame.Height);
                frame.GetPixelMemory<Rgba32>().CopyTo(copy.GetPixelMemory<Rgba32>());
                image.Frames.Add(copy);
            }
        }

        return image;
    }

    /// <summary>Whether the image's first alpha channel is premultiplied (djxl then writes premultiplied colour).</summary>
    internal static bool IsPremultiplied(string file)
    {
        JxlImageMetadata metadata = JxlHeadersReader.Read(JxlContainer.Parse(File.ReadAllBytes(file)).Codestream).Metadata;
        return metadata.ExtraChannels.FirstOrDefault(channel => channel.Type == JxlExtraChannelType.Alpha)?.AlphaAssociated == true;
    }

    /// <summary>
    /// The frames jxl_dec_straight renders (oriented, spot colours rendered, alpha straight) as Rgba32, or Rgba64 when
    /// <paramref name="wide"/>; every layer at its own size when <paramref name="layers"/>. Null without the tool.
    /// </summary>
    internal static List<Image>? StraightFrames(string file, bool wide = false, bool layers = false)
    {
        string? tool = Tool("jxl_dec_straight");
        if (tool is null) return null;
        string directory = Path.Combine(Path.GetTempPath(), $"jxl-straight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string prefix = Path.Combine(directory, "frame");
            using var process = Process.Start(new ProcessStartInfo(tool,
                [wide ? "--bits=16" : "--bits=8", .. layers ? ["--no_coalescing"] : Array.Empty<string>(), file, prefix])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"jxl_dec_straight failed: {error}");
            int count = Directory.GetFiles(directory, "frame-*.pam").Length;
            return [.. Enumerable.Range(0, count).Select(i => ReadPam(File.ReadAllBytes($"{prefix}-{i}.pam")))];
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // A P7 file of 1-4 channels (gray, gray+alpha, RGB, RGBA) with maxval 255 or 65535 (big endian) as Rgba32/Rgba64.
    private static Image ReadPam(byte[] pam)
    {
        int end = pam.AsSpan().IndexOf("ENDHDR\n"u8) + 7;
        Dictionary<string, string> header = System.Text.Encoding.ASCII.GetString(pam, 0, end).Split('\n')
            .Where(line => line.Contains(' ')).ToDictionary(line => line[..line.IndexOf(' ')], line => line[(line.IndexOf(' ') + 1)..]);
        int width = int.Parse(header["WIDTH"]), height = int.Parse(header["HEIGHT"]), depth = int.Parse(header["DEPTH"]);
        bool wide = header["MAXVAL"] == "65535";
        var image = new Image(width, height, wide ? PixelType.Rgba64 : PixelType.Rgba32);
        Span<ushort> s = stackalloc ushort[4];
        for (int p = 0; p < width * height; p++)
        {
            for (int c = 0; c < depth; c++)
            {
                int offset = end + ((p * depth) + c) * (wide ? 2 : 1);
                s[c] = wide ? (ushort)((pam[offset] << 8) | pam[offset + 1]) : pam[offset];
            }

            ushort max = wide ? ushort.MaxValue : byte.MaxValue;
            (ushort r, ushort g, ushort b, ushort a) = depth switch
            {
                1 => (s[0], s[0], s[0], max),
                2 => (s[0], s[0], s[0], s[1]),
                3 => (s[0], s[1], s[2], max),
                _ => (s[0], s[1], s[2], s[3]),
            };
            if (wide) image.GetPixelMemory<Rgba64>().Span[p] = new Rgba64(r, g, b, a);
            else image.GetPixelMemory<Rgba32>().Span[p] = new Rgba32((byte)r, (byte)g, (byte)b, (byte)a);
        }

        return image;
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

                using Image expected = ReferenceImage(djxl!, path);
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
