using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PictureSharp.Formats.Jxl.Fuzz;

/// <summary>
/// Images at widths and heights around SIMD lane counts, groups (256) and LF groups (2048), encoded and decoded. The
/// printed hashes must be identical in the native, SSE4.1 and scalar modes (scripts/fuzz.sh boundary); lossless round
/// trips must return the source. The encoded files also serve as fuzzing seeds.
/// </summary>
public static class Boundary
{
    private static readonly int[] Sizes = [.. Enumerable.Range(1, 33), 63, 64, 65, 255, 256, 257, 2047, 2048, 2049];
    private static readonly int[] Across = [1, 2, 7, 8, 9, 33, 256, 257];
    private static readonly int[] Narrow = [1, 3, 17];

    public static IEnumerable<(int Width, int Height)> Dimensions() =>
        Sizes.SelectMany(w => Across.Select(h => (w, h))).Concat(Narrow.SelectMany(w => Sizes.Select(h => (w, h)))).Distinct();

    /// <summary>Prints one line per encode; returns the number of failed lossless round trips.</summary>
    public static int Run(TextWriter output, string? directory)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        // ToJxl encodes through the default configuration, as in the golden harness.
        ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();
        var load = new DecoderOptions { MaxDegreeOfParallelism = 1 };
        int failures = 0;
        foreach ((int width, int height) in Dimensions())
        {
            foreach ((string variant, bool wide, float? distance, int effort) in (ReadOnlySpan<(string, bool, float?, int)>)[
                ("lossless8-e3", false, 0f, 3), ("lossy8-e7", false, null, 7), ("lossless16-e1", true, 0f, 1)])
            {
                using Image source = wide ? Synthetic16(width, height) : Synthetic(width, height);
                byte[] encoded = source.ToJxl(new JxlEncoderOptions { Distance = distance, Effort = effort, MaxDegreeOfParallelism = 1 });
                using Image decoded = Image.Load(encoded, load with { PreserveBitDepth = wide });
                string pixels = Pixels(decoded);
                if (distance == 0 && pixels != Pixels(source))
                {
                    failures++;
                    output.WriteLine($"# lossless round trip differs: {width}x{height} {variant}");
                }

                output.WriteLine($"{width}x{height} {variant} {encoded.Length} {Sha(encoded)} {pixels}");
                if (directory is not null) File.WriteAllBytes(Path.Combine(directory, $"{width}x{height}-{variant}.jxl"), encoded);
            }
        }

        return failures;
    }

    private static string Pixels(Image image) => image.PixelType == PixelType.Rgba64
        ? Sha(MemoryMarshal.AsBytes(image.GetPixelMemory<Rgba64>().Span))
        : Sha(MemoryMarshal.AsBytes(image.GetPixelMemory<Rgba32>().Span));

    private static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16];

    // Smooth gradients with noise and a varying alpha, so that every predictor, transform and SIMD tail is used.
    private static Image Synthetic(int width, int height)
    {
        var image = new Image(width, height);
        Span<Rgba32> pixels = image.GetPixelMemory<Rgba32>().Span;
        for (int i = 0; i < pixels.Length; i++)
        {
            int x = i % width, y = i / width;
            int noise = (int)((uint)(i * 2654435761u) >> 29);
            pixels[i] = new((byte)(x * 7 + noise), (byte)(y * 5 + x), (byte)((x ^ y) * 3), (byte)(255 - (x + y) % 64));
        }

        return image;
    }

    private static Image Synthetic16(int width, int height)
    {
        var image = new Image(width, height, PixelType.Rgba64);
        Span<Rgba64> pixels = image.GetPixelMemory<Rgba64>().Span;
        for (int i = 0; i < pixels.Length; i++)
        {
            int x = i % width, y = i / width;
            int noise = (int)((uint)(i * 2654435761u) >> 25);
            pixels[i] = new((ushort)(x * 1777 + noise), (ushort)(y * 1301 + x * 13), (ushort)((x ^ y) * 211), (ushort)(65535 - (x + y) % 512 * 64));
        }

        return image;
    }
}
