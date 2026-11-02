// Frozen 0.8.15 scalar reference of the noise stage (before JxlNoise.Simd.cs). Do not update from SIMD output.
using PictureSharp.Formats.Jxl.Codestream.Buffers;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlFastMath;

namespace PictureSharp.Formats.Jxl.Codestream.Rendering;

internal static class JxlNoiseScalar
{
    public static void ToFloats(ReadOnlySpan<ulong> batch, Span<float> output)
    {
        for (int i = 0; i < output.Length; i++)
        {
            uint bits = (uint)(batch[i >> 1] >> (32 * (i & 1)));
            output[i] = BitConverter.UInt32BitsToSingle((bits >> 9) | 0x3F800000u);
        }
    }

    public static (ulong[] S0, ulong[] S1) Seed(uint seed1, uint seed2, uint seed3, uint seed4)
    {
        ulong[] s0 = new ulong[8], s1 = new ulong[8];
        unchecked
        {
            s0[0] = SplitMix64(((ulong)seed1 << 32) + seed2 + 0x9E3779B97F4A7C15ul);
            s1[0] = SplitMix64(((ulong)seed3 << 32) + seed4 + 0x9E3779B97F4A7C15ul);
            for (int i = 1; i < 8; i++)
            {
                s0[i] = SplitMix64(s0[i - 1]);
                s1[i] = SplitMix64(s1[i - 1]);
            }
        }

        return (s0, s1);
    }

    private static ulong SplitMix64(ulong z)
    {
        unchecked
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ul;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBul;
            return z ^ (z >> 31);
        }
    }

    public static void Fill(ulong[] s0, ulong[] s1, Span<ulong> bits)
    {
        unchecked
        {
            for (int i = 0; i < 8; i++)
            {
                ulong first = s0[i];
                ulong second = s1[i];
                bits[i] = first + second;
                s0[i] = second;
                first ^= first << 23;
                s1[i] = first ^ second ^ (first >> 18) ^ (second >> 5);
            }
        }
    }

    public static void Convolve(JxlPlane plane, int y, Span<float> output)
    {
        float[] data = plane.Data;
        int r0 = plane.Offset(0, y - 2), r1 = plane.Offset(0, y - 1), r2 = plane.Offset(0, y), r3 = plane.Offset(0, y + 1), r4 = plane.Offset(0, y + 2);
        for (int x = 0; x < output.Length; x++)
        {
            float others = 0;
            for (int i = -2; i <= 2; i++)
            {
                others += data[r0 + x + i];
                others += data[r1 + x + i];
                others += data[r3 + x + i];
                others += data[r4 + x + i];
            }

            others += data[r2 + x - 2];
            others += data[r2 + x - 1];
            others += data[r2 + x + 1];
            others += data[r2 + x + 2];
            output[x] = Fma(others, 0.16f, data[r2 + x] * -3.84f);
        }
    }

    public static void AddNoise(float[] lut, Span<float> rowX, Span<float> rowY, Span<float> rowB, float[] convolved, int width,
        float yToX, float yToB)
    {
        for (int x = 0; x < width; x++)
        {
            float vx = rowX[x];
            float vy = rowY[x];
            float strengthG = Strength(lut, (vy - vx) * 0.5f);
            float strengthR = Strength(lut, (vy + vx) * 0.5f);
            float randomR = convolved[x] * 0.22f;
            float randomG = convolved[width + x] * 0.22f;
            float randomC = convolved[2 * width + x] * 0.22f;
            float red = strengthR * Fma(0.0078125f, randomR, 0.9921875f * randomC);
            float green = strengthG * Fma(0.0078125f, randomG, 0.9921875f * randomC);
            float sum = red + green;
            rowX[x] = Fma(yToX, sum, red - green) + vx;
            rowY[x] = vy + sum;
            rowB[x] = Fma(yToB, sum, rowB[x]);
        }
    }

    private static float Strength(float[] lut, float value)
    {
        float scaled = value * 6;
        if (!(scaled > 0)) scaled = 0;
        float floor = MathF.Floor(scaled);
        float fraction = scaled - floor;
        if (scaled >= 7)
        {
            floor = 6;
            fraction = 1;
        }

        int index = (int)floor;
        float low = lut[index];
        float strength = Fma(lut[index + 1] - low, fraction, low);
        return strength > 1 ? 1 : strength > 0 ? strength : 0;
    }
}
