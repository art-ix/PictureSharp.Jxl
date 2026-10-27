using PictureSharp.Formats.Jxl.Codestream.VarDct;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>
/// The chroma-from-luma factors of each 64×64 tile: the multiples of Y that, subtracted from X and B, leave the
/// cheapest quantized coefficients. Ported from libjxl v0.12.0 lib/jxl/enc_chroma_from_luma.cc (BSD-3-Clause); the DC
/// factors stay at their defaults, as libjxl leaves them.
/// </summary>
internal sealed class JxlChromaFromLumaSearch(JxlImage3F image, JxlQuantWeights matrices)
{
    /// <summary>The denominator of the coded factors (kDefaultColorFactor).</summary>
    public const int ColorFactor = 84;
    private const int TileBlocks = 8;
    private const int MaxCoefficients = JxlAcStrategy.BlockSize << 10;
    private const int TileCoefficients = 64 * 64;
    private const float DistanceMultiplier = 1e-9f;

    private readonly float[] blockY = new float[MaxCoefficients];
    private readonly float[] blockX = new float[MaxCoefficients];
    private readonly float[] blockB = new float[MaxCoefficients];
    private readonly float[] yx = new float[TileCoefficients];
    private readonly float[] x = new float[TileCoefficients];
    private readonly float[] yb = new float[TileCoefficients];
    private readonly float[] b = new float[TileCoefficients];
    private readonly float[] scratch = new float[JxlDct.ScratchSize(MaxCoefficients)];

    /// <summary>
    /// The X and B factors of the tile (<paramref name="tileX"/>, <paramref name="tileY"/>): with DCT8 and no
    /// quantization when <paramref name="strategies"/> is null, else with the chosen transforms and quantization.
    /// </summary>
    public (sbyte YToX, sbyte YToB) ComputeTile(int tileX, int tileY, JxlVarblock[]? strategies, int[]? quantField, float quantScale, bool fast)
    {
        int xBlocks = image.Width / 8;
        int yBlocks = image.Height / 8;
        int x0 = tileX * TileBlocks;
        int y0 = tileY * TileBlocks;
        int x1 = Math.Min(x0 + TileBlocks, xBlocks);
        int y1 = Math.Min(y0 + TileBlocks, yBlocks);
        int stride = image.Width;
        int count = 0;
        for (int by = y0; by < y1; by++)
        {
            for (int bx = x0; bx < x1; bx++)
            {
                JxlAcStrategyType type = strategies is null ? JxlAcStrategyType.Dct : strategies[by * xBlocks + bx].Type;
                if (strategies is not null && !strategies[by * xBlocks + bx].IsFirst) continue;
                if (type.BlocksX + x0 > x1 || type.BlocksY + y0 > y1) continue;
                int size = JxlAcStrategy.BlockSize << type.Log2Blocks;
                int pixel = by * 8 * stride + bx * 8;
                JxlDct.TransformFromPixels(type, image.Planes[1].AsSpan(pixel), stride, blockY, scratch);
                JxlDct.TransformFromPixels(type, image.Planes[0].AsSpan(pixel), stride, blockX, scratch);
                JxlDct.TransformFromPixels(type, image.Planes[2].AsSpan(pixel), stride, blockB, scratch);
                int cx = Math.Max(type.BlocksX, type.BlocksY);
                int cy = Math.Min(type.BlocksX, type.BlocksY);
                for (int iy = 0; iy < cy; iy++)
                {
                    for (int ix = 0; ix < cx; ix++)
                    {
                        blockY[cx * 8 * iy + ix] = 0;
                        blockX[cx * 8 * iy + ix] = 0;
                        blockB[cx * 8 * iy + ix] = 0;
                    }
                }

                float[] weights = matrices.InverseMatrix(type);
                const float strangeMultiplier = 128;
                float q = strategies is null ? 1 : quantScale * strangeMultiplier * (quantField?[by * xBlocks + bx] ?? 1);
                for (int i = 0; i < size; i++)
                {
                    float qx = q * weights[i];
                    float qb = q * weights[2 * size + i];
                    yx[count] = blockY[i] * qx;
                    x[count] = blockX[i] * qx;
                    yb[count] = blockY[i] * qb;
                    b[count] = blockB[i] * qb;
                    count++;
                }
            }
        }

        return (FindBestMultiplier(yx, x, count, 0.0f, fast), FindBestMultiplier(yb, b, count, 1.0f, fast));
    }

    // The factor minimizing a smooth estimate of the coded size of s - (base + factor / 84) m, rounded towards zero.
    private static sbyte FindBestMultiplier(float[] m, float[] s, int count, float baseCorrelation, bool fast)
    {
        if (count == 0) return 0;
        const float invColorFactor = 1.0f / ColorFactor;
        float x;
        if (fast)
        {
            float ca = 0;
            float cb = 0;
            for (int i = 0; i < count; i++)
            {
                float a = invColorFactor * m[i];
                float b = baseCorrelation * m[i] - s[i];
                ca = MathF.FusedMultiplyAdd(a, a, ca);
                cb = MathF.FusedMultiplyAdd(a, b, cb);
            }

            x = -cb / (ca + count * DistanceMultiplier * 0.5f);
        }
        else
        {
            const float eps = 100;
            const float clamp = 20.0f;
            x = 0;
            for (int i = 0; i < 20; i++)
            {
                float derivative = Derivatives(m, s, count, baseCorrelation, x, eps, out float plus, out float minus);
                float second = (plus - minus) / (2 * eps);
                const float stabilizer = 0.85f;
                float step = derivative / (second + stabilizer);
                x -= Math.Clamp(step, -clamp, clamp);
                if (Math.Abs(step) < 3e-3f) break;
            }
        }

        const float towardsZero = 2.6f;
        x = x >= towardsZero ? x - towardsZero : x <= -towardsZero ? x + towardsZero : 0;
        return (sbyte)Math.Clamp(MathF.Round(x, MidpointRounding.AwayFromZero), -128.0f, 127.0f);
    }

    // CFLFunction::Compute: the derivative of the cost at x, x + eps and x - eps.
    private static float Derivatives(float[] m, float[] s, int count, float baseCorrelation, float x, float eps, out float plus, out float minus)
    {
        const float invColorFactor = 1.0f / ColorFactor;
        const float threshold = 100.0f;
        const float coefficientX2 = 2.0f / 3;
        float sum = 0;
        float sumPlus = 0;
        float sumMinus = 0;
        for (int i = 0; i < count; i++)
        {
            float a = invColorFactor * m[i];
            float b = baseCorrelation * m[i] - s[i];
            float v = MathF.FusedMultiplyAdd(a, x, b);
            float vp = MathF.FusedMultiplyAdd(a, x + eps, b);
            float vm = MathF.FusedMultiplyAdd(a, x - eps, b);
            float scale = coefficientX2 * a;
            // Values far from zero at x are left out of all three derivatives.
            if (Math.Abs(v) >= threshold) continue;
            sum += Signed(scale * (Math.Abs(v) + 1), v);
            sumPlus += Signed(scale * (Math.Abs(vp) + 1), vp);
            sumMinus += Signed(scale * (Math.Abs(vm) + 1), vm);
        }

        plus = 2 * DistanceMultiplier * count * (x + eps) + sumPlus;
        minus = 2 * DistanceMultiplier * count * (x - eps) + sumMinus;
        return 2 * DistanceMultiplier * count * x + sum;
    }

    // d, negated where v is negative (IfThenElse(v < 0, -d, d)).
    private static float Signed(float d, float v) => v < 0 ? -d : d;
}
