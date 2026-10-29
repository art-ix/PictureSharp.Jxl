using PictureSharp.Formats.Jxl.Codestream.Numerics;
using PictureSharp.Formats.Jxl.Codestream.VarDct;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>The initial quantization field with the masking images the transform search weighs errors with.</summary>
/// <param name="QuantField">Per 8×8 block, the relative AC quantization step (higher is finer).</param>
/// <param name="Mask">Per block, the visual masking used for the transform search.</param>
/// <param name="Mask1X1">Per pixel, the blurred masking of the intensity Laplacian.</param>
internal sealed record JxlAqMaps(float[] QuantField, float[] Mask, float[] Mask1X1);

/// <summary>
/// The adaptive quantization map of XYB pixels (AdaptiveQuantizationMap): masking from local contrast of the Y
/// channel, eroded to the smooth areas around it, and modulated by gamma, high frequencies and blue content; then the
/// adjustment to the chosen transforms (AdjustQuantField). Ported, as one tile covering the image, from libjxl v0.12.0
/// lib/jxl/enc_adaptive_quantization.cc and convolve_symmetric5.cc (BSD-3-Clause).
/// </summary>
internal static class JxlAdaptiveQuantization
{
    private const float AcQuant = 0.765f;
    private const float MatchGammaOffset = 0.019f;
    private const float SgMul = 226.77216153508914f;
    private const float SgMul2 = 1.0f / 73.377132366608819f;
    private const float InvLog2E = 0.6931471805599453f;
    private const float SgRetMul = SgMul2 * 18.6580932135f * InvLog2E;
    private const float SgVOffset = 7.7825991679894591f;

    /// <summary>A constant field for the faster efforts: 0.79 / distance, and the matching mask.</summary>
    public static JxlAqMaps Constant(float distance, int blocks, int pixels)
    {
        float q = 0.79f / distance;
        float[] field = new float[blocks];
        Array.Fill(field, q);
        float[] mask = new float[blocks];
        Array.Fill(mask, 1.0f / (q + 0.001f));
        return new JxlAqMaps(field, mask, new float[pixels]);
    }

    /// <summary>InitialQuantField of <paramref name="xyb"/> (whole 8×8 blocks) at <paramref name="distance"/>.</summary>
    public static JxlAqMaps Compute(float distance, JxlImage3F xyb, CancellationToken cancellationToken, int parallelism = JxlParallel.Sequential)
    {
        float scale = AcQuant / distance;
        int width = xyb.Width;
        int height = xyb.Height;
        int xBlocks = width / 8;
        int yBlocks = height / 8;
        float[] luma = xyb.Planes[1];

        // 1x1 Laplacian of intensity, as masking per pixel.
        float[] mask1X1 = new float[width * height];
        // Rows, 4-row quads and block rows below are independent: each band writes only its own outputs.
        JxlParallel.ForBands(height, 16, parallelism, cancellationToken, (start, end) =>
        {
        for (int y = start; y < end; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int row = y * width;
            int above = (y > 0 ? y - 1 : y) * width;
            int below = (y + 1 < height ? y + 1 : y) * width;
            for (int x = 0; x < width; x++)
            {
                float center = luma[row + x];
                float average = 0.25f * (luma[below + x] + luma[above + x] + luma[row + (x > 0 ? x - 1 : x)] + luma[row + (x + 1 < width ? x + 1 : x)]);
                float gammac = RatioOfDerivatives(center + MatchGammaOffset, invert: false);
                float diff = float.LogP1(Math.Abs(gammac * (center - average)));
                mask1X1[row + x] = 1.0f / (diff + 0.01f);
            }
        }
        });

        // Squared, limited differences averaged over 4×4 pixels.
        int preWidth = width / 4;
        int preHeight = height / 4;
        float[] preErosion = new float[preWidth * preHeight];
        const float limit = 0.2f;
        // Rows past the last whole quad add to no output.
        JxlParallel.ForBands(preHeight, 4, parallelism, cancellationToken, (start, end) =>
        {
        float[] rowSum = new float[width];
        for (int y = 4 * start; y < 4 * end; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int row = y * width;
            int above = (y > 0 ? y - 1 : y) * width;
            int below = (y + 1 < height ? y + 1 : y) * width;
            for (int x = 0; x < width; x++)
            {
                float center = luma[row + x];
                float average = 0.25f * (luma[below + x] + luma[above + x] + luma[row + (x > 0 ? x - 1 : x)] + luma[row + (x + 1 < width ? x + 1 : x)]);
                float gammac = RatioOfDerivatives(center + MatchGammaOffset, invert: false);
                float diff = gammac * (center - average);
                diff = Math.Min(diff * diff, limit);
                diff = MaskingSqrt(diff);
                rowSum[x] = y % 4 != 0 ? rowSum[x] + diff : diff;
            }

            if (y % 4 == 3)
            {
                int target = y / 4 * preWidth;
                for (int qx = 0; qx < preWidth; qx++)
                {
                    preErosion[target + qx] = (rowSum[qx * 4] + rowSum[qx * 4 + 1] + rowSum[qx * 4 + 2] + rowSum[qx * 4 + 3]) * 0.25f;
                }
            }
        }
        });

        float[] aq = FuzzyErosion(distance, preErosion, preWidth, preHeight, xBlocks, yBlocks);
        float[] mask = new float[aq.Length];
        for (int i = 0; i < aq.Length; i++)
        {
            mask[i] = 1.0f / (aq[i] + 0.001f);
        }

        PerBlockModulations(distance, xyb, scale, aq, xBlocks, yBlocks, cancellationToken, parallelism);
        return new JxlAqMaps(aq, mask, Blur1X1Masking(mask1X1, width, height, cancellationToken, parallelism));
    }

    /// <summary>
    /// Sets the field of every block of a multi-block transform to the maximum over the transform, mixed with the
    /// mean at higher distances (AdjustQuantField).
    /// </summary>
    public static void AdjustQuantField(JxlVarblock[] strategies, int xBlocks, int yBlocks, float distance, float[] field)
    {
        const float mixLimit = 1.54138f;
        const float mixMul = 0.56391f;
        float mixer = distance > mixLimit ? Math.Max(0.0f, 1.0f - (distance - mixLimit) * mixMul) : 1.0f;
        for (int y = 0; y < yBlocks; y++)
        {
            for (int x = 0; x < xBlocks; x++)
            {
                JxlVarblock varblock = strategies[y * xBlocks + x];
                if (!varblock.IsFirst) continue;
                JxlAcStrategyType type = varblock.Type;
                int index = y * xBlocks + x;
                float max = field[index];
                float mean = 0;
                for (int iy = 0; iy < type.BlocksY; iy++)
                {
                    for (int ix = 0; ix < type.BlocksX; ix++)
                    {
                        float value = field[index + iy * xBlocks + ix];
                        mean += value;
                        max = Math.Max(max, value);
                    }
                }

                int covered = type.BlocksX * type.BlocksY;
                mean /= covered;
                if (covered >= 4)
                {
                    max = max * mixer + (1.0f - mixer) * mean;
                }

                for (int iy = 0; iy < type.BlocksY; iy++)
                {
                    field.AsSpan(index + iy * xBlocks, type.BlocksX).Fill(max);
                }
            }
        }
    }

    // The opsin space is the cube root of photons; this ratio moves quantization to butteraugli's log-gamma space.
    private static float RatioOfDerivatives(float v, bool invert)
    {
        const float epsilon = 1e-2f;
        v = Math.Max(v, 0);
        const float numMul = SgRetMul * 3 * SgMul;
        const float vOffset = SgVOffset * InvLog2E + epsilon;
        const float denMul = InvLog2E * SgMul;
        float v2 = v * v;
        float num = MathF.FusedMultiplyAdd(numMul, v2, epsilon);
        float den = MathF.FusedMultiplyAdd(denMul * v, v2, vOffset);
        return invert ? num / den : den / num;
    }

    private static float MaskingSqrt(float v)
    {
        const float logOffset = 27.505837037000106f;
        const float mul = 211.66567973503678f;
        return 0.25f * MathF.Sqrt(MathF.FusedMultiplyAdd(v, MathF.Sqrt(mul * 1e8f), logOffset));
    }

    // Weighted sum of the four smallest values in each 3×3 neighbourhood, summed into a field downsampled 2×.
    private static float[] FuzzyErosion(float distance, float[] from, int width, int height, int xBlocks, int yBlocks)
    {
        ReadOnlySpan<float> mulBase = [0.125f, 0.1f, 0.09f, 0.06f];
        ReadOnlySpan<float> mulAdd = [0.0f, -0.1f, -0.09f, -0.06f];
        float mix = distance < 2.0f ? (2.0f - distance) * 0.5f : 0.0f;
        Span<float> weights = stackalloc float[4];
        float sum = 0;
        for (int i = 0; i < 4; i++)
        {
            weights[i] = mulBase[i] + mix * mulAdd[i];
            sum += weights[i];
        }

        const float total = 0.29959705784054957f;
        for (int i = 0; i < 4; i++) weights[i] *= total / sum;

        float[] to = new float[xBlocks * yBlocks];
        Span<float> min = stackalloc float[4];
        for (int y = 0; y < 2 * yBlocks; y++)
        {
            int ym1 = y >= 1 ? y - 1 : y;
            int yp1 = y + 1 < height ? y + 1 : y;
            int row = y * width;
            int rowT = ym1 * width;
            int rowB = yp1 * width;
            for (int x = 0; x < 2 * xBlocks; x++)
            {
                int xm1 = x >= 1 ? x - 1 : x;
                int xp1 = x + 1 < width ? x + 1 : x;
                min[0] = from[row + x];
                min[1] = from[row + xm1];
                min[2] = from[row + xp1];
                min[3] = from[rowT + xm1];
                Sort4(min);
                StoreMin4(from[rowT + x], min);
                StoreMin4(from[rowT + xp1], min);
                StoreMin4(from[rowB + xm1], min);
                StoreMin4(from[rowB + x], min);
                StoreMin4(from[rowB + xp1], min);
                float v = weights[0] * min[0] + weights[1] * min[1] + weights[2] * min[2] + weights[3] * min[3];
                int target = y / 2 * xBlocks + x / 2;
                to[target] = x % 2 == 0 && y % 2 == 0 ? v : to[target] + v;
            }
        }

        return to;
    }

    private static void Sort4(Span<float> v)
    {
        if (v[0] > v[1]) (v[0], v[1]) = (v[1], v[0]);
        if (v[0] > v[2]) (v[0], v[2]) = (v[2], v[0]);
        if (v[0] > v[3]) (v[0], v[3]) = (v[3], v[0]);
        if (v[1] > v[2]) (v[1], v[2]) = (v[2], v[1]);
        if (v[1] > v[3]) (v[1], v[3]) = (v[3], v[1]);
        if (v[2] > v[3]) (v[2], v[3]) = (v[3], v[2]);
    }

    private static void StoreMin4(float v, Span<float> min)
    {
        if (v >= min[3]) return;
        if (v < min[0])
        {
            (min[3], min[2], min[1], min[0]) = (min[2], min[1], min[0], v);
        }
        else if (v < min[1])
        {
            (min[3], min[2], min[1]) = (min[2], min[1], v);
        }
        else if (v < min[2])
        {
            (min[3], min[2]) = (min[2], v);
        }
        else
        {
            min[3] = v;
        }
    }

    private static void PerBlockModulations(float distance, JxlImage3F xyb, float scale, float[] aq, int xBlocks, int yBlocks,
        CancellationToken cancellationToken, int parallelism)
    {
        float baseLevel = 0.48f * scale;
        const float dampenRampStart = 2.0f;
        const float dampenRampEnd = 14.0f;
        float dampen = distance >= dampenRampStart
            ? Math.Max(0, 1.0f - (distance - dampenRampStart) / (dampenRampEnd - dampenRampStart))
            : 1.0f;
        float mul = scale * dampen;
        float add = (1.0f - dampen) * baseLevel;
        JxlParallel.ForBands(yBlocks, 2, parallelism, cancellationToken, (start, end) =>
        {
        for (int by = start; by < end; by++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int bx = 0; bx < xBlocks; bx++)
            {
                int index = by * xBlocks + bx;
                float maskValue = ComputeMask(aq[index]);
                maskValue = GammaModulation(xyb, bx * 8, by * 8, maskValue);
                float value = HfModulation(xyb, bx * 8, by * 8, maskValue);
                value = Math.Min(value, BlueModulation(xyb, bx * 8, by * 8, maskValue));
                aq[index] = JxlFastMath.FastPow2f(value * 1.442695041f) * mul + add;
            }
        }
        });
    }

    private static float ComputeMask(float value)
    {
        const float kBase = -0.7647f;
        const float mul4 = 9.4708735624378946f;
        const float mul2 = 17.35036561631863f;
        const float offset2 = 302.59587815579727f;
        const float mul3 = 6.7943250517376494f;
        const float offset3 = 3.7179635626140772f;
        const float offset4 = 0.25f * offset3;
        const float mul0 = 0.80061762862741759f;
        float v1 = Math.Max(value * mul0, 1e-3f);
        float v2 = 1.0f / (v1 + offset2);
        float v3 = 1.0f / MathF.FusedMultiplyAdd(v1, v1, offset3);
        float v4 = 1.0f / MathF.FusedMultiplyAdd(v1, v1, offset4);
        return kBase + MathF.FusedMultiplyAdd(mul4, v4, MathF.FusedMultiplyAdd(mul2, v2, mul3 * v3));
    }

    private static float GammaModulation(JxlImage3F xyb, int x0, int y0, float value)
    {
        const float bias = 0.16f;
        float ratio = 0;
        for (int dy = 0; dy < 8; dy++)
        {
            int row = (y0 + dy) * xyb.Width + x0;
            for (int dx = 0; dx < 8; dx++)
            {
                float y = xyb.Planes[1][row + dx] + bias;
                float x = xyb.Planes[0][row + dx];
                ratio += RatioOfDerivatives(y - x, invert: true);
                ratio += RatioOfDerivatives(y + x, invert: true);
            }
        }

        ratio *= 0.5f / 64;
        const float gamma = 0.1005613337192697f;
        return MathF.FusedMultiplyAdd(gamma, JxlFastMath.FastLog2f(ratio), value);
    }

    // More precision in blocks with significant (but not overwhelming) blue content.
    private static float BlueModulation(JxlImage3F xyb, int x0, int y0, float value)
    {
        const float blueLimit = 0.010474084867598155f;
        const float offset = 0.0031994768654636393f;
        float sum = 0;
        for (int dy = 0; dy < 8; dy++)
        {
            int row = (y0 + dy) * xyb.Width + x0;
            for (int dx = 0; dx < 8; dx++)
            {
                float px = xyb.Planes[0][row + dx];
                float pb = xyb.Planes[2][row + dx];
                float py = xyb.Planes[1][row + dx] + offset + Math.Abs(px);
                if (pb > py) sum += Math.Min(pb - py, blueLimit);
            }
        }

        if (sum >= 32 * blueLimit) sum = 64 * blueLimit - sum;
        const float maxLimit = 15.463398341612438f;
        sum = Math.Min(sum, maxLimit * blueLimit);
        return sum * 0.90590804735610064f + value;
    }

    // Less precision in blocks with high-frequency content.
    private static float HfModulation(JxlImage3F xyb, int x0, int y0, float value)
    {
        const float minimum = 0.0206f;
        float sum = 0;
        float[] luma = xyb.Planes[1];
        for (int dy = 0; dy < 8; dy++)
        {
            int row = (y0 + dy) * xyb.Width + x0;
            int next = dy == 7 ? row : row + xyb.Width;
            for (int dx = 0; dx < 8; dx++)
            {
                float p = luma[row + dx];
                if (dx < 7) sum += Math.Min(minimum, Math.Abs(p - luma[row + dx + 1]));
                sum += Math.Min(minimum, Math.Abs(p - luma[next + dx]));
            }
        }

        return sum * -0.38f + 0.42f + value;
    }

    // The 5×5 symmetric blur of the per-pixel masking (Blur1x1Masking).
    private static float[] Blur1X1Masking(float[] mask, int width, int height, CancellationToken cancellationToken, int parallelism)
    {
        ReadOnlySpan<float> k = [0.364911248f, 0.05f, 0.1688888021f, 0.221069183f, 0.306563504f];
        double sum = Math.Max(1e-5, 1.0 + 4 * (k[0] + k[1] + k[2] + k[4] + 2 * k[3]));
        float normalize = (float)(1.0 / sum);
        var weights = new JxlSymmetric5Weights(normalize, normalize * k[0], normalize * k[2], normalize * k[1], normalize * k[4], normalize * k[3]);
        return JxlConvolution.Symmetric5(mask, width, height, weights, cancellationToken, parallelism: parallelism);
    }
}
