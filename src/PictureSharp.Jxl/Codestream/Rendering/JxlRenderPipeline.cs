using PictureSharp.Formats.Jxl.Codestream.Buffers;
using PictureSharp.Formats.Jxl.Codestream.Model;
using PictureSharp.Formats.Jxl.Codestream.Numerics;

namespace PictureSharp.Formats.Jxl.Codestream.Rendering;

/// <summary>
/// The restoration and resampling stages of the render pipeline, applied to whole planes as libjxl's simple
/// pipeline does: each stage mirrors its input around the plane's size. Ported from libjxl v0.12.0
/// lib/jxl/render_pipeline/simple_render_pipeline.cc, stage_chroma_upsampling.cc, stage_gaborish.cc, stage_epf.cc,
/// stage_upsampling.cc and lib/jxl/epf.cc (BSD-3-Clause).
/// </summary>
internal static class JxlRenderPipeline
{
    // 4 * (sqrt(0.5) - 1), so that Weight(sigma) = 0.5, and the sigma below which EPF leaves a block unchanged.
    public const float InvSigmaNum = -1.1715728752538099024f;
    private const float MinSigma = -3.90524291751269967465540850526868f;

    public static JxlPlane UpsampleChromaHorizontally(JxlPlane input, int width, CancellationToken cancellationToken = default)
    {
        input.Mirror(1, 0, cancellationToken);
        var output = new JxlPlane(width, input.Height, 2 * input.Width);
        float[] src = input.Data;
        float[] dst = output.Data;
        for (int y = 0; y < input.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int i = input.Offset(0, y);
            int o = output.Offset(0, y);
            for (int x = 0; x < input.Width; x++, i++)
            {
                float current = src[i] * 0.75f;
                dst[o + 2 * x] = JxlFastMath.Fma(0.25f, src[i - 1], current);
                dst[o + 2 * x + 1] = JxlFastMath.Fma(0.25f, src[i + 1], current);
            }
        }

        return output;
    }

    public static JxlPlane UpsampleChromaVertically(JxlPlane input, int height, CancellationToken cancellationToken = default)
    {
        input.Mirror(0, 1, cancellationToken);
        var output = new JxlPlane(input.Width, height, 0, 2 * input.Height);
        float[] src = input.Data;
        float[] dst = output.Data;
        for (int y = 0; y < input.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int i = input.Offset(0, y);
            int top = output.Offset(0, 2 * y);
            int bottom = output.Offset(0, 2 * y + 1);
            for (int x = 0; x < input.Width; x++, i++)
            {
                float middle = src[i] * 0.75f;
                dst[top + x] = JxlFastMath.Fma(src[i - input.Stride], 0.25f, middle);
                dst[bottom + x] = JxlFastMath.Fma(src[i + input.Stride], 0.25f, middle);
            }
        }

        return output;
    }

    /// <summary>The 3x3 Gaborish convolution of the three colour planes.</summary>
    public static JxlPlane[] Gaborish(JxlPlane[] planes, JxlLoopFilter filter, JxlPlane[]? spare = null, CancellationToken cancellationToken = default)
    {
        JxlPlane[] output = Outputs(planes, spare);
        for (int c = 0; c < 3; c++)
        {
            JxlPlane input = planes[c];
            input.Mirror(1, 1, cancellationToken);
            float w0 = 1;
            float w1 = filter.GaborishWeights[2 * c];
            float w2 = filter.GaborishWeights[2 * c + 1];
            float mul = 1.0f / (w0 + 4 * (w1 + w2));
            (w0, w1, w2) = (w0 * mul, w1 * mul, w2 * mul);
            JxlPlane result = output[c];
            float[] src = input.Data;
            float[] dst = result.Data;
            int stride = input.Stride;
            for (int y = 0; y < input.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int i = input.Offset(0, y);
                int o = result.Offset(0, y);
                for (int x = 0; x < input.Width; x++, i++)
                {
                    float sum1 = (src[i - 1] + src[i + 1]) + (src[i - stride] + src[i + stride]);
                    float sum2 = (src[i - stride - 1] + src[i - stride + 1]) + (src[i + stride - 1] + src[i + stride + 1]);
                    dst[o + x] = JxlFastMath.Fma(sum2, w2, JxlFastMath.Fma(sum1, w1, src[i] * w0));
                }
            }
        }

        return output;
    }

    /// <summary>Edge-preserving filter stage 0: 5x5 plus-shaped kernel with 3x3 plus-shaped SADs.</summary>
    public static JxlPlane[] Epf0(JxlPlane[] planes, JxlLoopFilter filter, float[] sigma, int xBlocks, JxlPlane[]? spare = null, CancellationToken cancellationToken = default)
    {
        float sm = (float)(filter.Pass0SigmaScale * 1.65);
        float[] sads = new float[12];
        return Epf(planes, spare, 3, sm, filter, sigma, xBlocks, (src, i, stride, invSigma, scale, result) =>
        {
            Array.Clear(sads);
            for (int c = 0; c < 3; c++)
            {
                for (int k = 0; k < 12; k++)
                {
                    float sad = 0;
                    int neighbour = i + Epf0Offsets[k].Row * stride + Epf0Offsets[k].Column;
                    foreach ((int row, int column) in Plus)
                    {
                        int delta = row * stride + column;
                        sad += Math.Abs(src[c][i + delta] - src[c][neighbour + delta]);
                    }

                    sads[k] = JxlFastMath.Fma(sad, scale[c], sads[k]);
                }
            }

            float w = 1;
            float x = src[0][i];
            float y = src[1][i];
            float b = src[2][i];
            for (int k = 0; k < 12; k++)
            {
                int neighbour = i + Epf0Offsets[k].Row * stride + Epf0Offsets[k].Column;
                AddPixel(src, neighbour, sads[k], invSigma, ref x, ref y, ref b, ref w);
            }

            Store(result, x, y, b, w);
        }, cancellationToken);
    }

    /// <summary>Edge-preserving filter stage 1: 3x3 plus-shaped kernel with 3x3 plus-shaped SADs.</summary>
    public static JxlPlane[] Epf1(JxlPlane[] planes, JxlLoopFilter filter, float[] sigma, int xBlocks, JxlPlane[]? spare = null, CancellationToken cancellationToken = default) =>
        Epf(planes, spare, 2, 1.65f, filter, sigma, xBlocks, (src, i, stride, invSigma, scale, result) =>
        {
            float sad0 = 0;
            float sad1 = 0;
            float sad2 = 0;
            float sad3 = 0;
            for (int c = 0; c < 3; c++)
            {
                float[] p = src[c];
                int up = i - stride;
                int down = i + stride;
                float p20 = p[i - 2 * stride];
                float p21 = p[up];
                float sad0c = Math.Abs(p20 - p21);
                float p11 = p[up - 1];
                float sad1c = Math.Abs(p11 - p21);
                float p31 = p[up + 1];
                float sad2c = Math.Abs(p31 - p21);
                float p02 = p[i - 2];
                float p12 = p[i - 1];
                sad1c += Math.Abs(p02 - p12);
                sad0c += Math.Abs(p11 - p12);
                float p22 = p[i];
                float t = Math.Abs(p12 - p22);
                sad1c += t;
                sad2c += t;
                t = Math.Abs(p22 - p21);
                float sad3c = t;
                sad0c += t;
                float p32 = p[i + 1];
                sad0c += Math.Abs(p31 - p32);
                t = Math.Abs(p22 - p32);
                sad1c += t;
                sad2c += t;
                float p42 = p[i + 2];
                sad2c += Math.Abs(p42 - p32);
                float p13 = p[down - 1];
                sad3c += Math.Abs(p13 - p12);
                float p23 = p[down];
                t = Math.Abs(p22 - p23);
                sad0c += t;
                sad3c += t;
                sad1c += Math.Abs(p13 - p23);
                float p33 = p[down + 1];
                sad2c += Math.Abs(p33 - p23);
                sad3c += Math.Abs(p33 - p32);
                float p24 = p[i + 2 * stride];
                sad3c += Math.Abs(p24 - p23);
                sad0 = JxlFastMath.Fma(sad0c, scale[c], sad0);
                sad1 = JxlFastMath.Fma(sad1c, scale[c], sad1);
                sad2 = JxlFastMath.Fma(sad2c, scale[c], sad2);
                sad3 = JxlFastMath.Fma(sad3c, scale[c], sad3);
            }

            float w = 1;
            float x = src[0][i];
            float y = src[1][i];
            float b = src[2][i];
            AddPixel(src, i - stride, sad0, invSigma, ref x, ref y, ref b, ref w);
            AddPixel(src, i - 1, sad1, invSigma, ref x, ref y, ref b, ref w);
            AddPixel(src, i + 1, sad2, invSigma, ref x, ref y, ref b, ref w);
            AddPixel(src, i + stride, sad3, invSigma, ref x, ref y, ref b, ref w);
            Store(result, x, y, b, w);
        }, cancellationToken);

    /// <summary>Edge-preserving filter stage 2: 3x3 plus-shaped kernel with one-pixel SADs.</summary>
    public static JxlPlane[] Epf2(JxlPlane[] planes, JxlLoopFilter filter, float[] sigma, int xBlocks, JxlPlane[]? spare = null, CancellationToken cancellationToken = default)
    {
        float sm = (float)(filter.Pass2SigmaScale * 1.65);
        return Epf(planes, spare, 1, sm, filter, sigma, xBlocks, (src, i, stride, invSigma, scale, result) =>
        {
            float rx = src[0][i];
            float ry = src[1][i];
            float rb = src[2][i];
            float w = 1;
            float x = rx;
            float y = ry;
            float b = rb;
            foreach (int neighbour in (ReadOnlySpan<int>)[i - stride, i - 1, i + 1, i + stride])
            {
                float cx = src[0][neighbour];
                float cy = src[1][neighbour];
                float cb = src[2][neighbour];
                float sad = Math.Abs(cx - rx) * scale[0];
                sad = JxlFastMath.Fma(Math.Abs(cy - ry), scale[1], sad);
                sad = JxlFastMath.Fma(Math.Abs(cb - rb), scale[2], sad);
                float weight = Math.Max(0, JxlFastMath.Fma(sad, invSigma, 1.0f));
                w += weight;
                x = JxlFastMath.Fma(weight, cx, x);
                y = JxlFastMath.Fma(weight, cy, y);
                b = JxlFastMath.Fma(weight, cb, b);
            }

            Store(result, x, y, b, w);
        }, cancellationToken);
    }

    private static readonly (int Row, int Column)[] Epf0Offsets =
    [
        (-2, 0), (-1, -1), (-1, 0), (-1, 1), (0, -2), (0, -1), (0, 1), (0, 2), (1, -1), (1, 0), (1, 1), (2, 0),
    ];

    private static readonly (int Row, int Column)[] Plus = [(0, 0), (-1, 0), (0, -1), (1, 0), (0, 1)];

    /// <summary>Planes of the same sizes as <paramref name="planes"/> and one layout: <paramref name="spare"/> when it fits.</summary>
    private static JxlPlane[] Outputs(JxlPlane[] planes, JxlPlane[]? spare)
    {
        bool fits = spare is not null && spare.Length == planes.Length && spare.All(plane => plane.Stride == spare[0].Stride)
            && spare.Zip(planes).All(pair => pair.First.Width == pair.Second.Width && pair.First.Height == pair.Second.Height
                && pair.First != pair.Second);
        return fits ? spare! : [.. planes.Select(plane => new JxlPlane(plane.Width, plane.Height))];
    }

    private delegate void EpfPixel(float[][] src, int index, int stride, float invSigma, float[] scale, Span<float> result);

    private static void AddPixel(float[][] src, int neighbour, float sad, float invSigma, ref float x, ref float y, ref float b, ref float w)
    {
        float weight = Math.Max(0, JxlFastMath.Fma(sad, invSigma, 1.0f));
        w += weight;
        x = JxlFastMath.Fma(weight, src[0][neighbour], x);
        y = JxlFastMath.Fma(weight, src[1][neighbour], y);
        b = JxlFastMath.Fma(weight, src[2][neighbour], b);
    }

    private static void Store(Span<float> result, float x, float y, float b, float w)
    {
        float inverse = 1.0f / w;
        result[0] = x * inverse;
        result[1] = y * inverse;
        result[2] = b * inverse;
    }

    // Runs one EPF stage: blocks whose sigma is below the threshold are copied, the rest filtered pixel by pixel.
    private static JxlPlane[] Epf(JxlPlane[] planes, JxlPlane[]? spare, int border, float sm, JxlLoopFilter filter, float[] sigma, int xBlocks,
        EpfPixel pixel, CancellationToken cancellationToken)
    {
        // The pixel functions address the three planes with one index: they need one layout.
        if (planes.Any(plane => plane.Stride != planes[0].Stride))
        {
            JxlPlane[] uniform = Outputs(planes, null);
            for (int c = 0; c < 3; c++)
            {
                planes[c].CopyTo(uniform[c], cancellationToken);
            }

            planes = uniform;
        }

        float bsm = sm * filter.BorderSadMul;
        float[][] src = [.. planes.Select(plane => plane.Data)];
        foreach (JxlPlane plane in planes)
        {
            plane.Mirror(border, border, cancellationToken);
        }

        JxlPlane[] output = Outputs(planes, spare);
        int width = planes[0].Width;
        int stride = planes[0].Stride;
        float[] scale = [.. filter.ChannelScale];
        Span<float> result = stackalloc float[3];
        for (int y = 0; y < planes[0].Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool borderRow = y % 8 == 0 || y % 8 == 7;
            int sigmaRow = y / 8 * xBlocks;
            int i = planes[0].Offset(0, y);
            int o = output[0].Offset(0, y);
            for (int x = 0; x < width; x++, i++, o++)
            {
                float blockSigma = sigma[sigmaRow + x / 8];
                if (blockSigma < MinSigma)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        output[c].Data[o] = src[c][i];
                    }

                    continue;
                }

                float invSigma = blockSigma * (borderRow || x % 8 == 0 || x % 8 == 7 ? bsm : sm);
                pixel(src, i, stride, invSigma, scale, result);
                for (int c = 0; c < 3; c++)
                {
                    output[c].Data[o] = result[c];
                }
            }
        }

        return output;
    }

    /// <summary>
    /// 2×, 4× or 8× upsampling with 5x5 kernels, clamped to the range of the 5x5 neighbourhood; the result is
    /// cropped to <paramref name="width"/>×<paramref name="height"/>.
    /// </summary>
    public static JxlPlane Upsample(JxlPlane input, int shift, IReadOnlyList<float> weights, int width, int height, CancellationToken cancellationToken = default)
    {
        int n = 1 << shift;
        int h = n / 2;
        float[] kernel = new float[n * n * 25];
        for (int ky = 0; ky < h; ky++)
        {
            for (int kx = 0; kx < h; kx++)
            {
                int offset0 = (ky * n + kx) * 25;
                int offset1 = (ky * n + (n - 1 - kx)) * 25;
                int offset2 = ((n - 1 - ky) * n + kx) * 25;
                int offset3 = ((n - 1 - ky) * n + (n - 1 - kx)) * 25;
                for (int py = 0; py < 5; py++)
                {
                    for (int px = 0; px < 5; px++)
                    {
                        int j = 5 * ky + py;
                        int i = 5 * kx + px;
                        int my = Math.Min(i, j);
                        int mx = Math.Max(i, j);
                        float w = weights[5 * h * my - my * (my - 1) / 2 + mx - my];
                        kernel[offset0 + py * 5 + px] = w;
                        kernel[offset1 + py * 5 + (4 - px)] = w;
                        kernel[offset2 + (4 - py) * 5 + px] = w;
                        kernel[offset3 + (4 - py) * 5 + (4 - px)] = w;
                    }
                }
            }
        }

        input.Mirror(2, 2, cancellationToken);
        var output = new JxlPlane(width, height, n * input.Width, n * input.Height);
        float[] src = input.Data;
        float[] dst = output.Data;
        int stride = input.Stride;
        Span<float> values = stackalloc float[25];
        for (int y = 0; y < input.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < input.Width; x++)
            {
                int center = input.Offset(x, y);
                float min = float.MaxValue;
                float max = float.MinValue;
                for (int iy = -2; iy <= 2; iy++)
                {
                    for (int ix = -2; ix <= 2; ix++)
                    {
                        float v = src[center + iy * stride + ix];
                        values[5 * (iy + 2) + ix + 2] = v;
                        min = Math.Min(min, v);
                        max = Math.Max(max, v);
                    }
                }

                for (int oy = 0; oy < n; oy++)
                {
                    int row = output.Offset(x * n, y * n + oy);
                    for (int ox = 0; ox < n; ox++)
                    {
                        int k = (n * oy + ox) * 25;
                        float acc0 = values[0] * kernel[k];
                        float acc1 = values[1] * kernel[k + 1];
                        float acc2 = values[2] * kernel[k + 2];
                        for (int i = 3; i < 24; i += 3)
                        {
                            acc0 = JxlFastMath.Fma(values[i], kernel[k + i], acc0);
                            acc1 = JxlFastMath.Fma(values[i + 1], kernel[k + i + 1], acc1);
                            acc2 = JxlFastMath.Fma(values[i + 2], kernel[k + i + 2], acc2);
                        }

                        acc0 = JxlFastMath.Fma(values[24], kernel[k + 24], acc0);
                        dst[row + ox] = Math.Clamp((acc1 + acc2) + acc0, min, max);
                    }
                }
            }
        }

        return output;
    }
}
