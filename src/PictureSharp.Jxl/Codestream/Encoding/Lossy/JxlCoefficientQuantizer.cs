using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.VarDct;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>
/// The quantized coefficients and DC of every varblock (ComputeCoefficients): Y is quantized first, with the
/// quantization value raised where high frequencies would be lost (AdjustQuantBlockAC), and its dequantized value
/// predicts X and B through chroma from luma. Ported from libjxl v0.12.0 lib/jxl/enc_group.cc (BSD-3-Clause).
/// </summary>
internal sealed partial class JxlCoefficientQuantizer
{
    private const int MaxCoefficients = JxlAcStrategy.BlockSize << 10;

    // Blocks without a lowest-frequency part beyond their first coefficients; AdjustQuantBlockAC leaves them alone.
    private const uint PartialBlockKinds = (1u << (int)JxlAcStrategyType.Identity) | (1u << (int)JxlAcStrategyType.Dct2X2)
        | (1u << (int)JxlAcStrategyType.Dct4X4) | (1u << (int)JxlAcStrategyType.Dct4X8) | (1u << (int)JxlAcStrategyType.Dct8X4)
        | (1u << (int)JxlAcStrategyType.Afv0) | (1u << (int)JxlAcStrategyType.Afv1) | (1u << (int)JxlAcStrategyType.Afv2)
        | (1u << (int)JxlAcStrategyType.Afv3);

    private readonly JxlLossyFrame frame;
    private readonly JxlImage3F image;
    private readonly float[] biases = [.. JxlCustomTransformData.Default.EffectiveQuantBiases];
    private readonly float[] block = new float[3 * MaxCoefficients];
    private readonly int[] quantized = new int[3 * MaxCoefficients];
    private readonly float[] scratch = new float[JxlDct.ScratchSize(MaxCoefficients)];
    private readonly float[] thresholdsY = new float[4];
    private readonly float[] thresholds = new float[4];
    private readonly float[] chromaThresholds = new float[4];

    public JxlCoefficientQuantizer(JxlLossyFrame frame, JxlImage3F image)
    {
        this.frame = frame;
        this.image = image;
    }

    /// <summary>Quantizes the varblocks of group <paramref name="group"/> into <see cref="JxlLossyFrame.Coefficients"/>.</summary>
    public void ComputeGroup(int group)
    {
        (int x0, int y0, int width, int height) = frame.GroupRect(group);
        int area = width * height * JxlAcStrategy.BlockSize;
        int[] coefficients = frame.Coefficients[group] = frame.Buffers is null ? new int[3 * area] : frame.Buffers.Rent<int>(3 * area);
        int stride = image.Width;
        int xBlocks = frame.XBlocks;
        JxlEncoderQuantizer quantizer = frame.Quantizer;
        bool adjust = frame.Settings.AdjustBlockQuantization;
        int offset = 0;
        for (int by = y0; by < y0 + height; by++)
        {
            for (int bx = x0; bx < x0 + width; bx++)
            {
                int index = by * xBlocks + bx;
                JxlVarblock varblock = frame.Strategies[index];
                if (!varblock.IsFirst) continue;
                JxlAcStrategyType type = varblock.Type;
                int size = JxlAcStrategy.BlockSize << type.Log2Blocks;
                int xs = Math.Max(type.BlocksX, type.BlocksY);
                int ys = Math.Min(type.BlocksX, type.BlocksY);
                int pixel = by * 8 * stride + bx * 8;
                for (int c = 0; c < 3; c++)
                {
                    JxlDct.TransformFromPixels(type, image.Planes[c].AsSpan(pixel), stride, block.AsSpan(c * size, size), scratch);
                }

                JxlDct.DcFromLowestFrequencies(type, block.AsSpan(size, size), frame.Dc[1].AsSpan(index), xBlocks, scratch);
                int quant = frame.QuantField[index];
                float[] inverse = frame.Matrices.InverseMatrix(type);
                float[] dequant = frame.Matrices.Matrix(type);

                // QuantizeRoundtripYBlockAC.
                if (adjust)
                {
                    int original = quant;
                    int max = 0;
                    ReadOnlySpan<float> multipliers = [frame.XQmMultiplier, 1.0f, frame.BQmMultiplier];
                    foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
                    {
                        thresholds[0] = 0.58f;
                        thresholds.AsSpan(1).Fill(0.64f);
                        int candidate = original;
                        AdjustQuantBlockAC(c, multipliers[c], type, xs, ys, thresholds, inverse.AsSpan(c * size, size), block.AsSpan(c * size, size),
                            ref candidate);
                        if (c == 1) thresholds.CopyTo(thresholdsY);
                        max = Math.Max(max, candidate);
                    }

                    quant = max;
                }
                else
                {
                    thresholdsY[0] = 0.56f;
                    thresholdsY.AsSpan(1).Fill(0.62f);
                }

                QuantizeBlockAC(1, 1.0f, xs, ys, thresholdsY, inverse.AsSpan(size, size), block.AsSpan(size, size), quant,
                    quantized.AsSpan(size, size));
                float inverseQuant = quantizer.InverseQuantAc(quant);
                int tile = frame.Tile(bx, by);
                float xFactor = JxlLossyFrame.YToXRatio(frame.YToX[tile]);
                float bFactor = JxlLossyFrame.YToBRatio(frame.YToB[tile]);
                // Both loops below at once (the second reads only the first's results).
                int done = DequantizeYAndSubtractAvx2(quantized.AsSpan(size, size), dequant.AsSpan(size, size), block.AsSpan(0, 3 * size),
                    size, inverseQuant, xFactor, bFactor, biases[1], biases[3]);
                for (int k = done; k < size; k++)
                {
                    block[size + k] = AdjustQuantBias(quantized[size + k]) * dequant[size + k] * inverseQuant;
                }

                for (int k = done; k < size; k++)
                {
                    float y = block[size + k];
                    block[k] = MathF.FusedMultiplyAdd(-xFactor, y, block[k]);
                    block[2 * size + k] = MathF.FusedMultiplyAdd(-bFactor, y, block[2 * size + k]);
                }

                foreach (int c in (ReadOnlySpan<int>)[0, 2])
                {
                    chromaThresholds[0] = 0.58f;
                    chromaThresholds.AsSpan(1).Fill(0.62f);
                    QuantizeBlockAC(c, c == 0 ? frame.XQmMultiplier : frame.BQmMultiplier, xs, ys, chromaThresholds, inverse.AsSpan(c * size, size),
                        block.AsSpan(c * size, size), quant, quantized.AsSpan(c * size, size));
                    JxlDct.DcFromLowestFrequencies(type, block.AsSpan(c * size, size), frame.Dc[c].AsSpan(index), xBlocks, scratch);
                }

                frame.QuantField[index] = quant;
                for (int c = 0; c < 3; c++)
                {
                    quantized.AsSpan(c * size, size).CopyTo(coefficients.AsSpan(c * area + offset, size));
                }

                offset += size;
            }
        }
    }

    // The expected value of a coefficient quantized to q (AdjustQuantBias), for Y.
    private float AdjustQuantBias(int q)
    {
        float value = q;
        float magnitude = Math.Abs(value);
        if (magnitude < 1.125f)
        {
            return magnitude > 0 ? MathF.CopySign(biases[1], value) : 0;
        }

        return MathF.FusedMultiplyAdd(-biases[3], 1.0f / value, value);
    }

    // Rounds to integers, with the coefficients below a per-quadrant threshold set to zero (QuantizeBlockAC).
    private void QuantizeBlockAC(int c, float qmMultiplier, int xs, int ys, Span<float> thresholds, ReadOnlySpan<float> weights,
        ReadOnlySpan<float> input, int quant, Span<int> output)
    {
        float qac = frame.Quantizer.Scale * quant;
        if (c != 1 && xs * ys >= 4)
        {
            for (int i = 0; i < 4; i++)
            {
                thresholds[i] = Math.Max(0.5f, thresholds[i] - 0.00744f * xs * ys);
            }
        }

        float quantV = qac * qmMultiplier;
        int columns = xs * 8;
        for (int y = 0; y < ys * 8; y++)
        {
            int yFix = y >= ys * 4 ? 2 : 0;
            int offset = y * columns;
            int done = QuantizeRowSimd(weights.Slice(offset, columns), input.Slice(offset, columns), output.Slice(offset, columns),
                quantV, thresholds[yFix], thresholds[yFix + 1]);
            for (int x = done; x < columns; x++)
            {
                float threshold = thresholds[yFix + (x >= columns / 2 ? 1 : 0)];
                float value = weights[offset + x] * quantV * input[offset + x];
                output[offset + x] = Math.Abs(value) >= threshold ? (int)MathF.Round(value) : 0;
            }
        }
    }

    // Raises the quantization value of a block when its high frequencies would be lost or badly approximated.
    private void AdjustQuantBlockAC(int c, float qmMultiplier, JxlAcStrategyType type, int xs, int ys, Span<float> thresholds,
        ReadOnlySpan<float> weights, ReadOnlySpan<float> input, ref int quant)
    {
        if ((PartialBlockKinds & (1u << (int)type)) != 0) return;
        float qac = frame.Quantizer.Scale * quant;
        if (xs > 1 || ys > 1)
        {
            for (int i = 0; i < 4; i++)
            {
                thresholds[i] = Math.Max(0.54f, thresholds[i] - Math.Clamp(0.003f * xs * ys, 0.0f, 0.08f));
            }
        }

        float sumOfHighestFreqRowAndColumn = 0;
        float sumOfError = 0;
        float sumOfVals = 0;
        Span<float> hfNonZeros = stackalloc float[4];
        Span<float> hfMaxError = stackalloc float[4];
        int columns = xs * 8;
        for (int y = 0; y < ys * 8; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                if (x < xs && y < ys) continue;
                int pos = y * columns + x;
                int hf = (y >= ys * 4 ? 2 : 0) + (x >= columns / 2 ? 1 : 0);
                float value = input[pos] * (weights[pos] * qac * qmMultiplier);
                float v = Math.Abs(value) < thresholds[hf] ? 0 : MathF.Round(value);
                float error = Math.Abs(value - v);
                sumOfError += error;
                sumOfVals += Math.Abs(v);
                if (c == 1 && v == 0 && hfMaxError[hf] < error) hfMaxError[hf] = error;
                if (v != 0.0f)
                {
                    hfNonZeros[hf] += Math.Abs(v);
                    bool inCorner = y >= 7 * ys && x >= 7 * xs;
                    bool onBorder = y == ys * 8 - 1 || x == columns - 1;
                    bool inLargerCorner = x >= 4 * xs && y >= 4 * ys;
                    if (inCorner || (onBorder && inLargerCorner)) sumOfHighestFreqRowAndColumn += Math.Abs(value);
                }
            }
        }

        const double limit = 0.46;
        const double mul = 0.9999;
        if (c == 1 && sumOfVals * 8 < xs * ys)
        {
            int original = quant;
            int newQuant = quant;
            for (int i = 1; i < 4; i++)
            {
                if (hfNonZeros[i] == 0.0 && hfMaxError[i] > limit)
                {
                    newQuant = original + 1;
                    break;
                }
            }

            quant = newQuant;
            if (hfNonZeros[3] == 0.0 && hfMaxError[3] > limit)
            {
                thresholds[3] = (float)(mul * hfMaxError[3] * newQuant / original);
            }
            else if ((hfNonZeros[1] == 0.0 && hfMaxError[1] > limit) || (hfNonZeros[2] == 0.0 && hfMaxError[2] > limit))
            {
                thresholds[1] = (float)(mul * Math.Max(hfMaxError[1], hfMaxError[2]) * newQuant / original);
                thresholds[2] = thresholds[1];
            }
            else if (hfNonZeros[0] == 0.0 && hfMaxError[0] > limit)
            {
                thresholds[0] = (float)(mul * hfMaxError[0] * newQuant / original);
            }
        }

        // Increase the quantization when the highest frequencies need it.
        {
            float all = hfNonZeros[0] + hfNonZeros[1] + hfNonZeros[2] + hfNonZeros[3] + 1;
            ReadOnlySpan<float> muls = [70, 30, 60];
            if (muls[c] * sumOfHighestFreqRowAndColumn >= all)
            {
                quant = (int)(quant + muls[c] * sumOfHighestFreqRowAndColumn / all);
                quant = Math.Min(quant, JxlEncoderQuantizer.QuantMax - 1);
            }
        }

        if (type == JxlAcStrategyType.Dct && hfNonZeros[0] + hfNonZeros[1] + hfNonZeros[2] + hfNonZeros[3] < 11)
        {
            quant = Math.Min(quant + 1, JxlEncoderQuantizer.QuantMax - 1);
        }

        if (type >= JxlAcStrategyType.Dct16X16)
        {
            ReadOnlySpan<double> mul1 =
            [
                0.22080615753848404, 0.45797479824262011, 0.29859235095977965,
                0.70109486510286834, 0.16185281305512639, 0.14387691730035473,
                0.114985964456218638, 0.44656840441027695, 0.10587658215149048,
                0.46849665264409396, 0.41239077937781954, 0.088667407767185444,
            ];
            ReadOnlySpan<double> mul2 =
            [
                0.27450281941822197, 1.1255766549984996, 0.98950459134128388,
                0.4652168675598285, 0.40945807983455818, 0.36581899811751367,
                0.28034972424715715, 0.9182653201929738, 1.5581531543057416,
                0.26873118114033728, 0.68863712390392484, 1.2082185408666786,
            ];
            const double quantNormalizer = 2.2942708343284721;
            double error = sumOfError * quantNormalizer;
            double vals = sumOfVals * quantNormalizer;
            int ix = type switch
            {
                JxlAcStrategyType.Dct32X16 or JxlAcStrategyType.Dct16X32 => 1,
                JxlAcStrategyType.Dct16X16 => 0,
                JxlAcStrategyType.Dct32X32 => 2,
                _ => 3,
            };
            double bound = mul1[ix * 3 + c] * xs * ys * 64 + mul2[ix * 3 + c] * vals;
            int step = Math.Clamp((int)(error / bound), 0, 2);
            if (error > bound)
            {
                quant = Math.Min(quant + step, JxlEncoderQuantizer.QuantMax - 1);
            }
        }

        {
            int div = xs * ys;
            float minHfNonZeros = Math.Min(Math.Min(hfNonZeros[0], hfNonZeros[1]), Math.Min(hfNonZeros[2], hfNonZeros[3]));
            int activity = minHfNonZeros < 15.0f * div ? ((int)minHfNonZeros + div / 2) / div : 15;
            int qp = quant - activity;
            if (c == 1)
            {
                for (int i = 1; i < 4; i++) thresholds[i] = (float)(thresholds[i] + 0.01 * activity);
            }

            quant = Math.Max(qp, Math.Max(4, quant / 2));
        }
    }
}
