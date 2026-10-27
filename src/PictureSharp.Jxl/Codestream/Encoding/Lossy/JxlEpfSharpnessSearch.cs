using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Rendering;
using PictureSharp.Formats.Jxl.Codestream.VarDct;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>
/// The EPF sharpness of every block (ComputeARHeuristics): the frame is reconstructed with each candidate sharpness,
/// and blocks take the value with the least masked error, biased towards their neighbours' values. The reconstruction
/// uses the decoder's own dequantization, transforms and filters. Ported from libjxl v0.12.0 lib/jxl/enc_heuristics.cc
/// (BSD-3-Clause).
/// </summary>
internal static class JxlEpfSharpnessSearch
{
    private const int Values = 8;

    /// <param name="frame">The frame with its coefficients; its sharpness map is replaced.</param>
    /// <param name="original">The XYB image before inverse Gaborish, padded to whole blocks.</param>
    /// <param name="mask1X1">The per-pixel masking of the adaptive quantization, in the padded layout.</param>
    /// <param name="dequantizedDc">The DC of every block as the decoder dequantizes it (X, Y, B).</param>
    /// <param name="patches">The patches the decoder adds, with the reference frames they come from.</param>
    /// <param name="references">The reference frames of <paramref name="patches"/>.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    public static void Choose(JxlLossyFrame frame, JxlImage3F original, float[] mask1X1, float[][] dequantizedDc, JxlPatch[] patches,
        JxlReferenceFrames references, CancellationToken cancellationToken)
    {
        float distance = frame.Settings.Distance;
        float clamped = Math.Min(5.0f, distance);
        byte[] steps = distance > 4.5f ? [0, 4] : [0, 2, 7];
        int[] lut = new int[Values];
        for (int i = 0; i < steps.Length; i++) lut[steps[i]] = i;
        int xBlocks = frame.XBlocks;
        int yBlocks = frame.YBlocks;
        var errors = new float[Values][];
        foreach (byte value in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Fill(frame.Sharpness, value);
            JxlPlane[] decoded = Reconstruct(frame, dequantizedDc, patches, references, cancellationToken);
            errors[value] = BlockErrors(frame, original, decoded, mask1X1);
        }

        byte[] output = frame.Sharpness;
        long[,] histogram = new long[9, Values];
        long[] totals = new long[9];
        Array.Fill(totals, 1);
        const float favorNoSmoothing = 0.99f;
        for (int by = 0; by < yBlocks; by++)
        {
            for (int bx = 0; bx < xBlocks; bx++)
            {
                int index = by * xBlocks + bx;
                byte top = by > 0 ? output[index - xBlocks] : (byte)0;
                byte left = bx > 0 ? output[index - 1] : (byte)0;
                float topError = errors[top][index];
                float leftError = errors[left][index];
                byte best = 0;
                float bestError = float.MaxValue;
                foreach (byte value in steps)
                {
                    float error = errors[value][index];
                    if (value == 0) error *= favorNoSmoothing;
                    if (error < bestError)
                    {
                        best = value;
                        bestError = error;
                    }
                }

                output[index] = bestError < Math.Min(topError, leftError) ? best : topError < leftError ? top : left;
                int context = lut[top] * 3 + lut[left];
                histogram[context, output[index]]++;
                totals[context]++;
            }
        }

        const float c3Base = 0.98017198824148288f;
        const float c3Clamp = 0.85970338919928291f;
        float c3 = Math.Max(c3Clamp, MathF.Pow(c3Base, clamped));
        const float c5 = 0.1087690359555803f;
        float[] mul = new float[27];
        foreach (byte top in steps)
        {
            foreach (byte left in steps)
            {
                int context = lut[top] * 3 + lut[left];
                foreach (byte value in steps)
                {
                    // libjxl divides the counts as integers.
                    long ratio = histogram[context, value] / totals[context];
                    int at = lut[value] + 3 * context;
                    mul[at] = (float)(1.0 / (1.0 + c5 * Math.Log(1 + ratio) / clamped));
                    if (value == 0) mul[at] *= c3;
                }
            }
        }

        for (int by = 0; by < yBlocks; by++)
        {
            for (int bx = 0; bx < xBlocks; bx++)
            {
                int index = by * xBlocks + bx;
                byte top = by > 0 ? output[index - xBlocks] : (byte)0;
                byte left = bx > 0 ? output[index - 1] : (byte)0;
                int context = lut[top] * 3 + lut[left];
                byte best = 0;
                float bestError = float.MaxValue;
                foreach (byte value in steps)
                {
                    float error = errors[value][index] * mul[lut[value] + 3 * context];
                    if (error < bestError)
                    {
                        best = value;
                        bestError = error;
                    }
                }

                output[index] = best;
            }
        }
    }

    // ReconstructImage: the frame as the decoder renders it before the colour transform, at the coded resolution.
    private static JxlPlane[] Reconstruct(JxlLossyFrame frame, float[][] dequantizedDc, JxlPatch[] patches, JxlReferenceFrames references,
        CancellationToken cancellationToken)
    {
        JxlFrameHeader header = frame.Header with
        {
            Width = (uint)frame.Header.XSize,
            Height = (uint)frame.Header.YSize,
            Upsampling = 1,
            ExtraChannelUpsampling = [],
            Flags = JxlFrameFlags.None,
        };
        var metadata = new JxlImageMetadata { BitDepth = new JxlBitDepth(false, 8, 0), XybEncoded = true };
        var varDct = new JxlVarDctFrame(header, metadata, frame.Matrices, JxlCustomTransformData.Default.EffectiveQuantBiases, cancellationToken);
        varDct.SetQuantizer((uint)frame.Quantizer.GlobalScale, (uint)frame.Quantizer.QuantDc);
        frame.Strategies.CopyTo(varDct.Varblocks, 0);
        frame.QuantField.CopyTo(varDct.QuantField, 0);
        frame.Sharpness.CopyTo(varDct.Sharpness, 0);
        for (int i = 0; i < frame.YToX.Length; i++)
        {
            varDct.YToX[i] = frame.YToX[i];
            varDct.YToB[i] = frame.YToB[i];
        }

        varDct.SetEncoded(dequantizedDc, frame.UsedStrategies);
        varDct.SmoothDc();
        for (int group = 0; group < frame.Coefficients.Length; group++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            varDct.RenderGroup(group, frame.Coefficients[group]);
        }

        float[] sigma = JxlEpfSigma.Compute(header.LoopFilter, frame.XBlocks, frame.YBlocks, varDct.QuantScale, varDct.QuantField, varDct.Sharpness,
            varDct.Varblocks, cancellationToken);
        var headers = new JxlHeaders(new JxlSizeHeader(header.Width, header.Height), metadata, JxlCustomTransformData.Default);
        return new JxlFrameRenderer(header, headers, patches, references, sigma, cancellationToken).Render(varDct.Planes, [], colorTransform: false);
    }

    // ComputeBlockL2Distance: the squared, masked error of each block, weighted per channel.
    private static float[] BlockErrors(JxlLossyFrame frame, JxlImage3F original, JxlPlane[] decoded, float[] mask1X1)
    {
        int width = decoded[0].Width;
        int height = decoded[0].Height;
        float[] errors = new float[frame.XBlocks * frame.YBlocks];
        Span<float> error = stackalloc float[3];
        for (int by = 0; by < frame.YBlocks; by++)
        {
            for (int bx = 0; bx < frame.XBlocks; bx++)
            {
                error.Clear();
                for (int y = by * 8; y < Math.Min(by * 8 + 8, height); y++)
                {
                    for (int x = bx * 8; x < Math.Min(bx * 8 + 8, width); x++)
                    {
                        float mask = mask1X1[y * original.Width + x];
                        float mask2 = mask * mask;
                        for (int c = 0; c < 3; c++)
                        {
                            float diff = original.Planes[c][y * original.Width + x] - decoded[c].Row(y)[x];
                            error[c] += mask2 * diff * diff;
                        }
                    }
                }

                errors[by * frame.XBlocks + bx] = (float)(12.339445295782363 * error[0] + 1.0 * error[1] + 0.2 * error[2]);
            }
        }

        return errors;
    }
}
