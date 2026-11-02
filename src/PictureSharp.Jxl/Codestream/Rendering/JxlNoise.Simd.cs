using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Numerics;

namespace PictureSharp.Formats.Jxl.Codestream.Rendering;

// Eight adjacent pixels per vector; each pixel keeps the scalar order of additions and fused multiply-adds, and the LUT
// is looked up with a lane permutation (it has exactly eight entries). Reads stay inside the mirrored 2-pixel border.
internal static partial class JxlNoise
{
    private static int ConvolveAvx2(float[] data, int r0, int r1, int r2, int r3, int r4, Span<float> output)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported) return 0;
        int end = output.Length & ~7;
        ReadOnlySpan<float> d = data;
        for (int x = 0; x < end; x += 8)
        {
            Vector256<float> others = Vector256<float>.Zero;
            for (int i = -2; i <= 2; i++)
            {
                others += Vector256.Create(d.Slice(r0 + x + i, 8));
                others += Vector256.Create(d.Slice(r1 + x + i, 8));
                others += Vector256.Create(d.Slice(r3 + x + i, 8));
                others += Vector256.Create(d.Slice(r4 + x + i, 8));
            }

            others += Vector256.Create(d.Slice(r2 + x - 2, 8));
            others += Vector256.Create(d.Slice(r2 + x - 1, 8));
            others += Vector256.Create(d.Slice(r2 + x + 1, 8));
            others += Vector256.Create(d.Slice(r2 + x + 2, 8));
            Fma.MultiplyAdd(others, Vector256.Create(0.16f), Vector256.Create(d.Slice(r2 + x, 8)) * -3.84f).CopyTo(output.Slice(x, 8));
        }

        return end;
    }

    private static int AddNoiseAvx2(float[] lut, Span<float> rowX, Span<float> rowY, Span<float> rowB, float[] convolved, int width,
        float yToX, float yToB)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported) return 0;
        int end = width & ~7;
        Vector256<float> table = Vector256.Create(lut.AsSpan(0, JxlNoiseParameters.Points));
        ReadOnlySpan<float> random = convolved;
        for (int x = 0; x < end; x += 8)
        {
            Vector256<float> vx = Vector256.Create(rowX.Slice(x, 8));
            Vector256<float> vy = Vector256.Create(rowY.Slice(x, 8));
            Vector256<float> strengthG = StrengthAvx2(table, (vy - vx) * 0.5f);
            Vector256<float> strengthR = StrengthAvx2(table, (vy + vx) * 0.5f);
            Vector256<float> randomR = Vector256.Create(random.Slice(x, 8)) * NormConst;
            Vector256<float> randomG = Vector256.Create(random.Slice(width + x, 8)) * NormConst;
            Vector256<float> randomC = Vector256.Create(random.Slice(2 * width + x, 8)) * NormConst;
            Vector256<float> correlated = randomC * RgCorrelation;
            Vector256<float> red = strengthR * Fma.MultiplyAdd(Vector256.Create(RgNoCorrelation), randomR, correlated);
            Vector256<float> green = strengthG * Fma.MultiplyAdd(Vector256.Create(RgNoCorrelation), randomG, correlated);
            Vector256<float> sum = red + green;
            (Fma.MultiplyAdd(Vector256.Create(yToX), sum, red - green) + vx).CopyTo(rowX.Slice(x, 8));
            (vy + sum).CopyTo(rowY.Slice(x, 8));
            Fma.MultiplyAdd(Vector256.Create(yToB), sum, Vector256.Create(rowB.Slice(x, 8))).CopyTo(rowB.Slice(x, 8));
        }

        return end;
    }

    // Strength(): maxps returns its second operand for NaN, so NaN and -0 become +0 as in the scalar code.
    private static Vector256<float> StrengthAvx2(Vector256<float> table, Vector256<float> value)
    {
        const float Scale = JxlNoiseParameters.Points - 2;
        Vector256<float> scaled = Avx.Max(value * Scale, Vector256<float>.Zero);
        Vector256<float> floor = Avx.RoundToNegativeInfinity(scaled);
        Vector256<float> fraction = scaled - floor;
        Vector256<float> beyond = Avx.CompareGreaterThanOrEqual(scaled, Vector256.Create(Scale + 1));
        floor = Avx.BlendVariable(floor, Vector256.Create(Scale), beyond);
        fraction = Avx.BlendVariable(fraction, Vector256.Create(1.0f), beyond);
        Vector256<int> index = Avx.ConvertToVector256Int32WithTruncation(floor);
        Vector256<float> low = Avx2.PermuteVar8x32(table, index);
        Vector256<float> high = Avx2.PermuteVar8x32(table, index + Vector256<int>.One);
        Vector256<float> strength = Fma.MultiplyAdd(high - low, fraction, low);
        return Avx.Max(Avx.Min(strength, Vector256.Create(1.0f)), Vector256<float>.Zero);
    }

    // 1.0 + 23 random mantissa bits from the 32-bit halves in memory order, 16 at a time.
    private static int ToFloatsAvx2(ReadOnlySpan<ulong> batch, Span<float> output)
    {
        if (!Avx2.IsSupported || output.Length < FloatsPerBatch) return 0;
        ReadOnlySpan<uint> bits = MemoryMarshal.Cast<ulong, uint>(batch[..JxlXorshift128Plus.Lanes]);
        for (int i = 0; i < FloatsPerBatch; i += 8)
        {
            Vector256<uint> mantissa = (Vector256.Create(bits.Slice(i, 8)) >>> 9) | Vector256.Create(0x3F800000u);
            mantissa.AsSingle().CopyTo(output.Slice(i, 8));
        }

        return FloatsPerBatch;
    }
}
