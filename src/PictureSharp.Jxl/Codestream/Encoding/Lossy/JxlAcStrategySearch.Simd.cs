using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Formats.Jxl.Codestream.VarDct;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

internal sealed partial class JxlAcStrategySearch
{
    /// <summary>
    /// EstimateEntropy's per-channel sums, eight coefficients or pixels at a time (unfused, nearest-even rounding as
    /// MathF.Round). Each sum keeps eight lane accumulators (lane k takes the elements whose index is k modulo 8) and
    /// reduces them with <see cref="ReduceLanes(ReadOnlySpan{float})"/>'s order, as the scalar path does. False when AVX2
    /// is unavailable.
    /// </summary>
    private bool SumChannelsAvx2(JxlAcStrategyType type, int size, int x, int y, float quantNorm16, ReadOnlySpan<float> cmap,
        float[] dequant, float[] quant, Span<float> entropySums, Span<double> channelLosses, Span<int> nonZeros)
    {
        if (!Avx2.IsSupported) return false;
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
            Vector256<float> factor = Vector256.Create(cmap[c]);
            Vector256<float> roots = Vector256<float>.Zero;
            int nonZero = 0;
            for (int i = 0; i < size; i += 8)
            {
                Vector256<float> value = (Vector256.Create(coefficients.Slice(i, 8)) - Vector256.Create(yBlock.Slice(i, 8)) * factor)
                    * (Vector256.Create(quantRow.Slice(i, 8)) * norm);
                Vector256<float> rounded = Avx.RoundToNearestInteger(value);
                (Vector256.Create(dequantRow.Slice(i, 8)) * (value - rounded)).CopyTo(errors.Slice(i, 8));
                Vector256<float> q = Avx.AndNot(Vector256.Create(-0.0f), rounded);
                roots += Avx.Sqrt(q);
                nonZero += BitOperations.PopCount((uint)Avx.MoveMask(Avx.CompareNotEqual(q, Vector256<float>.Zero)));
            }

            nonZeros[c] = nonZero;
            Vector128<float> half = roots.GetLower() + roots.GetUpper();
            entropySums[c] = (half[0] + half[2]) + (half[1] + half[3]);
            JxlDct.TransformToPixels(type, errors, pixels, pixelWidth, scratch);
            Vector256<float> maskOffset = Vector256.Create(MaskOffsets[c]);
            Vector256<double> low = Vector256<double>.Zero, high = Vector256<double>.Zero;
            for (int py = 0; py < pixelRows; py++)
            {
                ReadOnlySpan<float> maskRow = mask1X1.AsSpan((y + py) * stride + x, pixelWidth);
                ReadOnlySpan<float> pixelRow = pixels.AsSpan(py * pixelWidth, pixelWidth);
                for (int px = 0; px < pixelWidth; px += 8)
                {
                    Vector256<float> v = (Vector256.Create(maskRow.Slice(px, 8)) + maskOffset) * Vector256.Create(pixelRow.Slice(px, 8));
                    v *= v;
                    v *= v;
                    v *= v;
                    low += Avx.ConvertToVector256Double(v.GetLower());
                    high += Avx.ConvertToVector256Double(v.GetUpper());
                }
            }

            Vector256<double> sum = low + high;
            channelLosses[c] = (sum[0] + sum[2]) + (sum[1] + sum[3]);
        }

        return true;
    }
}
