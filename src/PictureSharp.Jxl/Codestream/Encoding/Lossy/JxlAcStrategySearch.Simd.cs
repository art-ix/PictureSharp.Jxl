using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Formats.Jxl.Codestream.VarDct;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

internal sealed partial class JxlAcStrategySearch
{
    // Square roots of the quantized magnitudes of the three channels, then the eighth powers of their masked errors,
    // already widened to double (exact) so the ordered double sums need no conversion with a false dependency.
    private float[] entropyTerms = [];
    private double[] lossTerms = [];

    /// <summary>
    /// EstimateEntropy's per-channel sums: the per-coefficient and per-pixel terms are computed eight at a time (unfused,
    /// nearest-even rounding as MathF.Round), then the six independent sums are accumulated together, each in its
    /// scalar order, so the serial additions overlap instead of following one another. False when AVX2 is unavailable.
    /// </summary>
    private bool SumChannelsAvx2(JxlAcStrategyType type, int size, int x, int y, float quantNorm16, ReadOnlySpan<float> cmap,
        float[] dequant, float[] quant, Span<float> entropySums, Span<double> channelLosses, Span<int> nonZeros)
    {
        if (!Avx2.IsSupported) return false;
        if (entropyTerms.Length < 3 * size) entropyTerms = new float[3 * size];
        if (lossTerms.Length < 3 * size) lossTerms = new double[3 * size];
        int stride = image.Width;
        int pixelWidth = type.BlocksX * 8;
        int pixelRows = type.BlocksY * 8;
        ReadOnlySpan<float> yBlock = block.AsSpan(size, size);
        Span<float> errors = error.AsSpan(0, size);
        Vector256<float> norm = Vector256.Create(quantNorm16);
        for (int c = 0; c < 3; c++)
        {
            int offset = c * size;
            ReadOnlySpan<float> coefficients = block.AsSpan(offset, size);
            ReadOnlySpan<float> quantRow = quant.AsSpan(offset, size);
            ReadOnlySpan<float> dequantRow = dequant.AsSpan(offset, size);
            Span<float> roots = entropyTerms.AsSpan(offset, size);
            Vector256<float> factor = Vector256.Create(cmap[c]);
            int nonZero = 0;
            for (int i = 0; i < size; i += 8)
            {
                Vector256<float> value = (Vector256.Create(coefficients.Slice(i, 8)) - Vector256.Create(yBlock.Slice(i, 8)) * factor)
                    * (Vector256.Create(quantRow.Slice(i, 8)) * norm);
                Vector256<float> rounded = Avx.RoundToNearestInteger(value);
                (Vector256.Create(dequantRow.Slice(i, 8)) * (value - rounded)).CopyTo(errors.Slice(i, 8));
                Vector256<float> q = Avx.AndNot(Vector256.Create(-0.0f), rounded);
                Avx.Sqrt(q).CopyTo(roots.Slice(i, 8));
                nonZero += BitOperations.PopCount((uint)Avx.MoveMask(Avx.CompareNotEqual(q, Vector256<float>.Zero)));
            }

            nonZeros[c] = nonZero;
            JxlDct.TransformToPixels(type, errors, pixels, pixelWidth, scratch);
            Span<double> channelTerms = lossTerms.AsSpan(offset, size);
            Vector256<float> maskOffset = Vector256.Create(MaskOffsets[c]);
            for (int py = 0; py < pixelRows; py++)
            {
                ReadOnlySpan<float> maskRow = mask1X1.AsSpan((y + py) * stride + x, pixelWidth);
                ReadOnlySpan<float> pixelRow = pixels.AsSpan(py * pixelWidth, pixelWidth);
                Span<double> termRow = channelTerms.Slice(py * pixelWidth, pixelWidth);
                for (int px = 0; px < pixelWidth; px += 8)
                {
                    Vector256<float> v = (Vector256.Create(maskRow.Slice(px, 8)) + maskOffset) * Vector256.Create(pixelRow.Slice(px, 8));
                    v *= v;
                    v *= v;
                    v *= v;
                    Avx.ConvertToVector256Double(v.GetLower()).CopyTo(termRow.Slice(px, 4));
                    Avx.ConvertToVector256Double(v.GetUpper()).CopyTo(termRow.Slice(px + 4, 4));
                }
            }
        }

        ReadOnlySpan<float> r0 = entropyTerms.AsSpan(0, size), r1 = entropyTerms.AsSpan(size, size), r2 = entropyTerms.AsSpan(2 * size, size);
        ReadOnlySpan<double> l0 = lossTerms.AsSpan(0, size), l1 = lossTerms.AsSpan(size, size), l2 = lossTerms.AsSpan(2 * size, size);
        float e0 = 0, e1 = 0, e2 = 0;
        double d0 = 0, d1 = 0, d2 = 0;
        for (int i = 0; i < r0.Length; i++)
        {
            e0 += r0[i];
            e1 += r1[i];
            e2 += r2[i];
            d0 += l0[i];
            d1 += l1[i];
            d2 += l2[i];
        }

        (entropySums[0], entropySums[1], entropySums[2]) = (e0, e1, e2);
        (channelLosses[0], channelLosses[1], channelLosses[2]) = (d0, d1, d2);
        return true;
    }
}
