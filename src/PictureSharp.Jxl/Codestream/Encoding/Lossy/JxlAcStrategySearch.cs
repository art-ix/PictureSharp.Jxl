using PictureSharp.Formats.Jxl.Codestream.Numerics;
using PictureSharp.Formats.Jxl.Codestream.VarDct;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>
/// The varblocks of a frame: per 64×64 tile the best 8×8 transform of every block by estimated entropy and visual
/// loss, then merges into 8×16 up to 64×64 transforms where they cost less (ProcessRectACS). Ported from libjxl v0.12.0
/// lib/jxl/enc_ac_strategy.cc (BSD-3-Clause).
/// </summary>
internal sealed class JxlAcStrategySearch
{
    private const int TileBlocks = 8;
    private const int MaxCoefficients = JxlAcStrategy.BlockSize << 10;

    private static readonly (JxlAcStrategyType Type, int MaxSpeedTier, float EntropyMul)[] Transforms8X8 =
    [
        (JxlAcStrategyType.Dct, 9, 0.8f),
        (JxlAcStrategyType.Dct4X4, 5, 1.08f),
        (JxlAcStrategyType.Dct2X2, 5, 0.95f),
        (JxlAcStrategyType.Dct4X8, 4, 0.85931637428340035f),
        (JxlAcStrategyType.Dct8X4, 4, 0.85931637428340035f),
        (JxlAcStrategyType.Identity, 5, 1.0427542510634957f),
        (JxlAcStrategyType.Afv0, 4, 0.81779489591359944f),
        (JxlAcStrategyType.Afv1, 4, 0.81779489591359944f),
        (JxlAcStrategyType.Afv2, 4, 0.81779489591359944f),
        (JxlAcStrategyType.Afv3, 4, 0.81779489591359944f),
    ];

    private const float EntropyMul16X8 = 1.21f;
    private const float EntropyMul16X16 = 1.34f;
    private const float EntropyMul16X32 = 1.49f;
    private const float EntropyMul32X32 = 1.48f;
    private const float EntropyMul64X32 = 2.25f;
    private const float EntropyMul64X64 = 2.25f;

    private static readonly (JxlAcStrategyType Type, byte Priority, float EntropyMul)[] Merges =
    [
        (JxlAcStrategyType.Dct16X8, 2, EntropyMul16X8),
        (JxlAcStrategyType.Dct8X16, 2, EntropyMul16X8),
        (JxlAcStrategyType.Dct16X32, 4, EntropyMul16X32),
        (JxlAcStrategyType.Dct32X16, 4, EntropyMul16X32),
        (JxlAcStrategyType.Dct64X32, 6, EntropyMul64X32),
        (JxlAcStrategyType.Dct32X64, 6, EntropyMul64X32),
    ];

    private readonly JxlImage3F image;
    private readonly float[] quantField;
    private readonly float[] mask1X1;
    private readonly JxlQuantWeights matrices;
    private readonly JxlVarblock[] strategies;
    private readonly int xBlocks;
    private readonly int yBlocks;
    private readonly float distance;
    private readonly int speedTier;
    private readonly float infoLossMultiplier;
    private readonly float zerosMul;
    private readonly float costDelta;
    private readonly float[] block = new float[3 * MaxCoefficients];
    private readonly float[] error = new float[MaxCoefficients];
    private readonly float[] pixels = new float[MaxCoefficients];
    private readonly float[] scratch = new float[JxlDct.ScratchSize(MaxCoefficients)];
    private readonly float[] entropyEstimate = new float[64];
    private readonly byte[] priority = new byte[64];

    public JxlAcStrategySearch(JxlImage3F image, float[] quantField, float[] mask1X1, JxlQuantWeights matrices, JxlVarblock[] strategies,
        float distance, int speedTier)
    {
        this.image = image;
        this.quantField = quantField;
        this.mask1X1 = mask1X1;
        this.matrices = matrices;
        this.strategies = strategies;
        this.distance = distance;
        this.speedTier = speedTier;
        xBlocks = image.Width / 8;
        yBlocks = image.Height / 8;
        const float bias = 0.13731742964354549f;
        float ratio = (distance + bias) / (1.0f + bias);
        infoLossMultiplier = 1.2f * MathF.Pow(ratio, 0.33677806662454718f);
        zerosMul = 9.3089059022677905f * MathF.Pow(ratio, 0.50990926717963703f);
        costDelta = 10.833273317067883f * MathF.Pow(ratio, 0.36702940662370243f);
    }

    /// <summary>Fills every block of <paramref name="strategies"/> with DCT8.</summary>
    public static void FillDct8(JxlVarblock[] strategies) => Array.Fill(strategies, JxlVarblock.Create(JxlAcStrategyType.Dct, first: true));

    /// <summary>Chooses the transforms of the tile of blocks (<paramref name="bx"/>, <paramref name="by"/>) and size up to 8×8.</summary>
    public void ProcessTile(int bx, int by, int width, int height, float yToX, float yToB)
    {
        ReadOnlySpan<float> cmap = [yToX, 0.0f, yToB];
        Array.Clear(entropyEstimate);
        Array.Clear(priority);
        const float mul1 = -0.4f;
        const float mul2 = 1.0f;
        const float baseValue = 1.4f;
        float mul8X8 = mul2 + mul1 / (distance + baseValue);
        for (int iy = 0; iy < height; iy++)
        {
            for (int ix = 0; ix < width; ix++)
            {
                (JxlAcStrategyType best, float entropy) = FindBest8X8Transform(8 * (bx + ix), 8 * (by + iy), cmap);
                Set(bx + ix, by + iy, best);
                entropyEstimate[iy * 8 + ix] = entropy * mul8X8;
            }
        }

        foreach ((JxlAcStrategyType type, byte mergePriority, float entropyMul) in Merges)
        {
            int coveredX = type.BlocksX;
            int coveredY = type.BlocksY;
            for (int cy = 0; cy + coveredY - 1 < height; cy += coveredY)
            {
                for (int cx = 0; cx + coveredX - 1 < width; cx += coveredX)
                {
                    if (cy + 7 < height && cx + 7 < width)
                    {
                        if (type == JxlAcStrategyType.Dct32X64)
                        {
                            if ((cy | cx) % 8 == 0)
                            {
                                FindBestFirstLevelDivisionForSquare(8, true, bx, by, cx, cy, cmap, entropyMul, EntropyMul64X64);
                            }

                            continue;
                        }

                        if (type == JxlAcStrategyType.Dct32X16) continue;
                    }

                    if ((type == JxlAcStrategyType.Dct16X32 && cy % 4 != 0) || (type == JxlAcStrategyType.Dct32X16 && cx % 4 != 0)) continue;
                    if (cy + 3 < height && cx + 3 < width)
                    {
                        if (type == JxlAcStrategyType.Dct16X32)
                        {
                            if ((cy | cx) % 4 == 0)
                            {
                                FindBestFirstLevelDivisionForSquare(4, true, bx, by, cx, cy, cmap, entropyMul, EntropyMul32X32);
                            }

                            continue;
                        }

                        if (type == JxlAcStrategyType.Dct32X16) continue;
                    }

                    if ((type == JxlAcStrategyType.Dct16X32 && cy % 4 != 0) || (type == JxlAcStrategyType.Dct32X16 && cx % 4 != 0)) continue;
                    if (cy + 1 < height && cx + 1 < width)
                    {
                        if (type == JxlAcStrategyType.Dct8X16)
                        {
                            if ((cy | cx) % 2 == 0)
                            {
                                FindBestFirstLevelDivisionForSquare(2, true, bx, by, cx, cy, cmap, entropyMul, EntropyMul16X16);
                            }

                            continue;
                        }

                        if (type == JxlAcStrategyType.Dct16X8) continue;
                    }

                    if ((type == JxlAcStrategyType.Dct8X16 && cy % 2 == 1) || (type == JxlAcStrategyType.Dct16X8 && cx % 2 == 1)) continue;
                    TryMerge(type, bx, by, cx, cy, cmap, entropyMul, mergePriority);
                }
            }
        }

        if (speedTier >= JxlLossySettings.Hare)
        {
            return;
        }

        // Squares at odd positions, for blocks the aligned merges above did not find.
        for (int cy = 0; cy + 1 < height; cy++)
        {
            for (int cx = 0; cx + 1 < width; cx++)
            {
                if ((cy | cx) % 2 != 0)
                {
                    FindBestFirstLevelDivisionForSquare(2, true, bx, by, cx, cy, cmap, EntropyMul16X8, EntropyMul16X16);
                }
            }
        }

        // libjxl steps by two below its slowest tier (kGlacier), which this encoder does not implement.
        for (int cy = 0; cy + 3 < height; cy += 2)
        {
            for (int cx = 0; cx + 3 < width; cx += 2)
            {
                if ((cy | cx) % 4 == 0) continue;
                FindBestFirstLevelDivisionForSquare(4, true, bx, by, cx, cy, cmap, EntropyMul16X32, EntropyMul32X32);
            }
        }
    }

    // AcStrategyImage::Set: the first block of the varblock and the blocks it covers.
    private void Set(int x, int y, JxlAcStrategyType type)
    {
        for (int iy = 0; iy < type.BlocksY; iy++)
        {
            for (int ix = 0; ix < type.BlocksX; ix++)
            {
                strategies[(y + iy) * xBlocks + x + ix] = JxlVarblock.Create(type, ix == 0 && iy == 0);
            }
        }
    }

    private (JxlAcStrategyType Type, float Entropy) FindBest8X8Transform(int x, int y, ReadOnlySpan<float> cmap)
    {
        float best = 1e30f;
        JxlAcStrategyType bestType = Transforms8X8[0].Type;
        foreach ((JxlAcStrategyType type, int maxSpeedTier, float mul) in Transforms8X8)
        {
            if (maxSpeedTier < speedTier) continue;
            float entropyMul = mul / Transforms8X8[0].EntropyMul;
            if (type is JxlAcStrategyType.Dct2X2 or JxlAcStrategyType.Identity && distance < 5.0f)
            {
                const float favor2X2AtHighQuality = 0.4f;
                float weight = MathF.Pow((5.0f - distance) / 5.0f, 2.0f);
                entropyMul -= favor2X2AtHighQuality * weight;
            }

            if (type is not (JxlAcStrategyType.Dct or JxlAcStrategyType.Dct2X2 or JxlAcStrategyType.Identity) && distance > 4.0f)
            {
                const float avoidEntropyOfTransforms = 0.5f;
                float avoid = distance < 12.0f ? (12.0f - 4.0f) / (distance - 4.0f) : 1.0f;
                entropyMul += avoidEntropyOfTransforms * avoid;
            }

            float entropy = EstimateEntropy(type, entropyMul, x, y, cmap);
            if (entropy < best)
            {
                bestType = type;
                best = entropy;
            }
        }

        return (bestType, best);
    }

    private void TryMerge(JxlAcStrategyType type, int bx, int by, int cx, int cy, ReadOnlySpan<float> cmap, float entropyMul, byte candidatePriority)
    {
        float current = 0;
        for (int iy = 0; iy < type.BlocksY; iy++)
        {
            for (int ix = 0; ix < type.BlocksX; ix++)
            {
                if (priority[(cy + iy) * 8 + cx + ix] >= candidatePriority) return;
                current += entropyEstimate[(cy + iy) * 8 + cx + ix];
            }
        }

        float candidate = EstimateEntropy(type, entropyMul, (bx + cx) * 8, (by + cy) * 8, cmap);
        if (candidate >= current) return;
        for (int iy = 0; iy < type.BlocksY; iy++)
        {
            for (int ix = 0; ix < type.BlocksX; ix++)
            {
                entropyEstimate[(cy + iy) * 8 + cx + ix] = 0;
                priority[(cy + iy) * 8 + cx + ix] = candidatePriority;
            }
        }

        Set(bx + cx, by + cy, type);
        entropyEstimate[cy * 8 + cx] = candidate;
    }

    private void SetEntropy(int cx, int cy, JxlAcStrategyType type, float entropy)
    {
        for (int dy = 0; dy < type.BlocksY; dy++)
        {
            entropyEstimate.AsSpan((cy + dy) * 8 + cx, type.BlocksX).Clear();
        }

        entropyEstimate[cy * 8 + cx] = entropy;
    }

    private static JxlAcStrategyType Square(int blocks) => blocks switch
    {
        2 => JxlAcStrategyType.Dct16X16,
        4 => JxlAcStrategyType.Dct32X32,
        _ => JxlAcStrategyType.Dct64X64,
    };

    // Two side-by-side halves: 16 rows by 8 columns for two blocks.
    private static JxlAcStrategyType VerticalSplit(int blocks) => blocks switch
    {
        2 => JxlAcStrategyType.Dct16X8,
        4 => JxlAcStrategyType.Dct32X16,
        _ => JxlAcStrategyType.Dct64X32,
    };

    private static JxlAcStrategyType HorizontalSplit(int blocks) => blocks switch
    {
        2 => JxlAcStrategyType.Dct8X16,
        4 => JxlAcStrategyType.Dct16X32,
        _ => JxlAcStrategyType.Dct32X64,
    };

    // A blocks×blocks square as one transform, two side by side, two stacked, or unchanged, whichever costs least.
    private void FindBestFirstLevelDivisionForSquare(int blocks, bool allowSquare, int bx, int by, int cx, int cy, ReadOnlySpan<float> cmap,
        float entropyMulJxK, float entropyMulJxJ)
    {
        int half = blocks / 2;
        JxlAcStrategyType jxk = VerticalSplit(blocks);
        JxlAcStrategyType kxj = HorizontalSplit(blocks);
        JxlAcStrategyType jxj = Square(blocks);
        int x = bx + cx;
        int y = by + cy;
        if (CrossesHorizontal(x, y, x + blocks) || CrossesHorizontal(x, y + blocks, x + blocks)
            || CrossesVertical(x, y, y + blocks) || CrossesVertical(x + blocks, y, y + blocks))
        {
            // Some transforms leak out of the square.
            return;
        }

        bool allowJxK = !CrossesVertical(x + half, y, y + blocks);
        bool allowKxJ = !CrossesHorizontal(x, y + half, x + blocks);
        Span<float> entropy = stackalloc float[4];
        for (int dy = 0; dy < blocks; dy++)
        {
            for (int dx = 0; dx < blocks; dx++)
            {
                entropy[dy / half * 2 + dx / half] += entropyEstimate[(cy + dy) * 8 + cx + dx];
            }
        }

        float jxkLeft = float.MaxValue;
        float jxkRight = float.MaxValue;
        float kxjTop = float.MaxValue;
        float kxjBottom = float.MaxValue;
        float square = float.MaxValue;
        if (allowJxK)
        {
            if (strategies[y * xBlocks + x].Type != jxk) jxkLeft = EstimateEntropy(jxk, entropyMulJxK, x * 8, y * 8, cmap);
            if (strategies[y * xBlocks + x + half].Type != jxk) jxkRight = EstimateEntropy(jxk, entropyMulJxK, (x + half) * 8, y * 8, cmap);
        }

        if (allowKxJ)
        {
            if (strategies[y * xBlocks + x].Type != kxj) kxjTop = EstimateEntropy(kxj, entropyMulJxK, x * 8, y * 8, cmap);
            if (strategies[(y + half) * xBlocks + x].Type != kxj) kxjBottom = EstimateEntropy(kxj, entropyMulJxK, x * 8, (y + half) * 8, cmap);
        }

        if (allowSquare)
        {
            square = EstimateEntropy(jxj, entropyMulJxJ, x * 8, y * 8, cmap);
        }

        float costJxN = Math.Min(jxkLeft, entropy[0] + entropy[2]) + Math.Min(jxkRight, entropy[1] + entropy[3]);
        float costNxJ = Math.Min(kxjTop, entropy[0] + entropy[1]) + Math.Min(kxjBottom, entropy[2] + entropy[3]);
        if (square < costJxN && square < costNxJ)
        {
            Set(x, y, jxj);
            SetEntropy(cx, cy, jxj, square);
        }
        else if (costJxN < costNxJ)
        {
            if (jxkLeft < entropy[0] + entropy[2])
            {
                Set(x, y, jxk);
                SetEntropy(cx, cy, jxk, jxkLeft);
            }

            if (jxkRight < entropy[1] + entropy[3])
            {
                Set(x + half, y, jxk);
                SetEntropy(cx + half, cy, jxk, jxkRight);
            }
        }
        else
        {
            if (kxjTop < entropy[0] + entropy[1])
            {
                Set(x, y, kxj);
                SetEntropy(cx, cy, kxj, kxjTop);
            }

            if (kxjBottom < entropy[2] + entropy[3])
            {
                Set(x, y + half, kxj);
                SetEntropy(cx, cy + half, kxj, kxjBottom);
            }
        }
    }

    // Whether a varblock crosses the horizontal line at the top of row y between columns startX and endX.
    private bool CrossesHorizontal(int startX, int y, int endX)
    {
        if (startX >= xBlocks || y >= yBlocks || y % 8 == 0) return false;
        endX = Math.Min(endX, xBlocks);
        int row = y * xBlocks;
        int limit = startX & ~7;
        while (startX != limit && !strategies[row + startX].IsFirst) startX--;
        for (int x = startX; x < endX;)
        {
            JxlVarblock varblock = strategies[row + x];
            if (!varblock.IsFirst) return true;
            x += varblock.Type.BlocksX;
        }

        return false;
    }

    private bool CrossesVertical(int x, int startY, int endY)
    {
        if (x >= xBlocks || startY >= yBlocks || x % 8 == 0) return false;
        endY = Math.Min(endY, yBlocks);
        int limit = startY & ~7;
        while (startY != limit && !strategies[startY * xBlocks + x].IsFirst) startY--;
        for (int y = startY; y < endY;)
        {
            JxlVarblock varblock = strategies[y * xBlocks + x];
            if (!varblock.IsFirst) return true;
            y += varblock.Type.BlocksY;
        }

        return false;
    }

    // The estimated bits of the quantized coefficients plus the masked error they leave (EstimateEntropy).
    private float EstimateEntropy(JxlAcStrategyType type, float entropyMul, int x, int y, ReadOnlySpan<float> cmap)
    {
        int size = JxlAcStrategy.BlockSize << type.Log2Blocks;
        int stride = image.Width;
        for (int c = 0; c < 3; c++)
        {
            JxlDct.TransformFromPixels(type, image.Planes[c].AsSpan(y * stride + x), stride, block.AsSpan(c * size, size), scratch);
        }

        int blocksX = type.BlocksX;
        int blocksY = type.BlocksY;
        int numBlocks = blocksX * blocksY;
        int bx = x / 8;
        int by = y / 8;
        float quantNorm16;
        if (numBlocks == 1)
        {
            quantNorm16 = Quant(bx, by);
        }
        else if (numBlocks == 2)
        {
            quantNorm16 = blocksY == 2 ? Math.Max(Quant(bx, by), Quant(bx, by + 1)) : Math.Max(Quant(bx, by), Quant(bx + 1, by));
        }
        else
        {
            quantNorm16 = 0;
            for (int iy = 0; iy < blocksY; iy++)
            {
                for (int ix = 0; ix < blocksX; ix++)
                {
                    float q = Quant(bx + ix, by + iy);
                    q *= q;
                    q *= q;
                    q *= q;
                    quantNorm16 += q * q;
                }
            }

            quantNorm16 /= numBlocks;
            quantNorm16 = JxlFastMath.FastPowf(quantNorm16, 1.0f / 16.0f);
        }

        ReadOnlySpan<float> masku = [12.0f, 0.0f, 4.0f];
        ReadOnlySpan<double> channelMul = [Math.Pow(8.2, 8.0), 1.0, Math.Pow(1.03, 8.0)];
        float entropy = 0;
        double loss = 0;
        float[] dequant = matrices.Matrix(type);
        float[] quant = matrices.InverseMatrix(type);
        int pixelWidth = blocksX * 8;
        for (int c = 0; c < 3; c++)
        {
            float entropySum = 0;
            int nonZeros = 0;
            int offset = c * size;
            float cmapFactor = cmap[c];
            for (int i = 0; i < size; i++)
            {
                float value = (block[offset + i] - block[size + i] * cmapFactor) * (quant[offset + i] * quantNorm16);
                float rounded = MathF.Round(value);
                error[i] = dequant[offset + i] * (value - rounded);
                float q = Math.Abs(rounded);
                entropySum += MathF.Sqrt(q);
                if (q != 0) nonZeros++;
            }

            JxlDct.TransformToPixels(type, error.AsSpan(0, size), pixels, pixelWidth, scratch);
            double channelLoss = 0;
            for (int py = 0; py < blocksY * 8; py++)
            {
                int maskRow = (y + py) * stride + x;
                for (int px = 0; px < pixelWidth; px++)
                {
                    float v = (mask1X1[maskRow + px] + masku[c]) * pixels[py * pixelWidth + px];
                    v *= v;
                    v *= v;
                    v *= v;
                    channelLoss += v;
                }
            }

            loss += channelMul[c] * channelLoss;
            entropy += costDelta * entropySum;
            int bits = CeilLog2(nonZeros + 1) + 1;
            entropy += zerosMul * (CeilLog2(bits + 17) + bits);
            if (c == 0 && numBlocks >= 2)
            {
                float w = 1.0f + Math.Min(3.0f, numBlocks / 8.0f);
                entropy *= w;
                loss *= w;
            }
        }

        float lossScalar = (float)(Math.Pow(loss / (numBlocks * 64), 1.0 / 8.0) * (numBlocks * 64) / quantNorm16);
        return entropy * entropyMul + infoLossMultiplier * lossScalar;
    }

    private float Quant(int bx, int by) => quantField[by * xBlocks + bx];
}
