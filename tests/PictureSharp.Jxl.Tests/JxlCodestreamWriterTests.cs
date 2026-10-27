using PictureSharp.Formats.Jxl.Codestream.Encoding;
using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Tests.JxlTestData;

namespace PictureSharp.Formats.Jxl.Tests;

/// <summary>Codestreams written by the encoder core decode to the same samples with the plugin's decoder.</summary>
public sealed class JxlCodestreamWriterTests
{
    public enum Pattern { Noise, Gradient, FewColors, Flat }

    /// <summary>Deterministic RGBA test pixels; alpha 0 pixels keep arbitrary colour.</summary>
    internal static byte[] Pixels(int width, int height, Pattern pattern, bool alpha, int seed = 1)
    {
        var random = new Random(seed);
        byte[] rgba = new byte[width * height * 4];
        Span<byte> palette = stackalloc byte[24];
        random.NextBytes(palette);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                switch (pattern)
                {
                    case Pattern.Noise:
                        random.NextBytes(rgba.AsSpan(i, 3));
                        break;
                    case Pattern.Gradient:
                        rgba[i] = (byte)(x * 255 / Math.Max(width - 1, 1));
                        rgba[i + 1] = (byte)(y * 255 / Math.Max(height - 1, 1));
                        rgba[i + 2] = (byte)((x + y + random.Next(3)) & 255);
                        break;
                    case Pattern.FewColors:
                        int k = ((x / 7) + (y / 5)) % 8;
                        palette.Slice(k * 3, 3).CopyTo(rgba.AsSpan(i, 3));
                        break;
                    default:
                        rgba[i] = 10;
                        rgba[i + 1] = 200;
                        rgba[i + 2] = 30;
                        break;
                }

                rgba[i + 3] = !alpha ? (byte)255 : (byte)((x * 7 + y * 3) % 5 == 0 ? 0 : random.Next(256));
            }
        }

        return rgba;
    }

    internal static byte[] Encode(byte[] rgba, int width, int height, bool gray, bool alpha, byte[]? icc = null)
    {
        int count = width * height;
        int[][] color = gray ? [new int[count]] : [new int[count], new int[count], new int[count]];
        int[]? alphaPlane = alpha ? new int[count] : null;
        for (int i = 0; i < count; i++)
        {
            for (int c = 0; c < color.Length; c++) color[c][i] = rgba[i * 4 + c];
            if (alphaPlane is not null) alphaPlane[i] = rgba[i * 4 + 3];
        }

        var encoding = gray ? new JxlColorEncoding { ColorSpace = JxlColorSpace.Gray } : JxlColorEncoding.Srgb;
        return JxlCodestreamWriter.Write(new JxlEncodeImage(width, height, color, alphaPlane, encoding, icc, 1));
    }

    internal static string Compare(byte[] encoded, byte[] rgba, int width, int height, bool gray)
    {
        using Image image = Image.Load(encoded, Options());
        if (image.Width != width || image.Height != height) return $"size {image.Width}x{image.Height}";
        int i = 0;
        foreach (Rgba32 pixel in image.GetPixelMemory().Span)
        {
            byte r = rgba[i], g = gray ? rgba[i] : rgba[i + 1], b = gray ? rgba[i] : rgba[i + 2], a = rgba[i + 3];
            if (pixel.R != r || pixel.G != g || pixel.B != b || pixel.A != a)
                return $"pixel {i / 4}: {pixel} instead of ({r}, {g}, {b}, {a})";
            i += 4;
        }

        return "ok";
    }

    [Test]
    [Arguments(1, 1, Pattern.Noise, false, false)]
    [Arguments(3, 5, Pattern.Gradient, false, true)]
    [Arguments(64, 48, Pattern.Noise, false, false)]
    [Arguments(64, 48, Pattern.Gradient, false, true)]
    [Arguments(100, 80, Pattern.FewColors, false, false)]
    [Arguments(100, 80, Pattern.FewColors, false, true)]
    [Arguments(90, 70, Pattern.Gradient, true, false)]
    [Arguments(90, 70, Pattern.FewColors, true, true)]
    [Arguments(40, 40, Pattern.Flat, false, false)]
    [Arguments(300, 270, Pattern.Gradient, false, true)]
    [Arguments(513, 257, Pattern.FewColors, false, false)]
    [Arguments(260, 600, Pattern.Gradient, true, false)]
    public async Task Images_RoundTripExactly(int width, int height, Pattern pattern, bool gray, bool alpha)
    {
        byte[] rgba = Pixels(width, height, pattern, alpha);
        byte[] encoded = Encode(rgba, width, height, gray, alpha);
        await Assert.That(Compare(encoded, rgba, width, height, gray)).IsEqualTo("ok");
    }
}
