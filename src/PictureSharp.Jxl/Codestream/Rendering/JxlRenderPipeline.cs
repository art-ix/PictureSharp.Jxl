using System.Runtime.Intrinsics;
using System.Runtime.CompilerServices;
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
internal static partial class JxlRenderPipeline
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

    /// <summary>The 3x3 Gaborish convolution of the three colour planes, in place.</summary>
    public static void Gaborish(JxlPlane[] planes, JxlLoopFilter filter, CancellationToken cancellationToken = default)
    {
        for (int c = 0; c < 3; c++)
        {
            JxlPlane plane = planes[c];
            plane.Mirror(1, 1, cancellationToken);
            float w0 = 1;
            float w1 = filter.GaborishWeights[2 * c];
            float w2 = filter.GaborishWeights[2 * c + 1];
            float mul = 1.0f / (w0 + 4 * (w1 + w2));
            (w0, w1, w2) = (w0 * mul, w1 * mul, w2 * mul);
            float[] data = plane.Data;
            int stride = plane.Stride;
            var rows = new DelayedRows(plane, 1);
            for (int y = 0; y < plane.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int i = plane.Offset(-1, y);
                Span<float> result = rows.Row(y);
                ReadOnlySpan<float> up = data.AsSpan(i - stride, result.Length + 2);
                ReadOnlySpan<float> middle = data.AsSpan(i, result.Length + 2);
                ReadOnlySpan<float> down = data.AsSpan(i + stride, result.Length + 2);
                int done = GaborishAvx2(up, middle, down, result, w0, w1, w2);
                // A 3x3 window slid along the row: each pixel loads only its right column.
                (float upLeft, float upCentre) = (up[done], up[done + 1]);
                (float left, float centre) = (middle[done], middle[done + 1]);
                (float downLeft, float downCentre) = (down[done], down[done + 1]);
                for (int x = done; x < result.Length; x++)
                {
                    float upRight = up[x + 2];
                    float right = middle[x + 2];
                    float downRight = down[x + 2];
                    float sum1 = (left + right) + (upCentre + downCentre);
                    float sum2 = (upLeft + upRight) + (downLeft + downRight);
                    result[x] = JxlFastMath.Fma(sum2, w2, JxlFastMath.Fma(sum1, w1, centre * w0));
                    (upLeft, upCentre) = (upCentre, upRight);
                    (left, centre) = (centre, right);
                    (downLeft, downCentre) = (downCentre, downRight);
                }

                rows.Done(y);
            }

            rows.Finish();
        }
    }

    /// <summary>
    /// Output rows of an in-place 2D filter that reads <c>reach</c> rows above and below: row y is written to the plane
    /// once row y + reach is filtered, when no later row reads its unfiltered values.
    /// </summary>
    private readonly struct DelayedRows(JxlPlane plane, int reach)
    {
        private readonly float[] buffer = new float[(reach + 1) * plane.Width];

        public Span<float> Row(int y) => buffer.AsSpan(y % (reach + 1) * plane.Width, plane.Width);

        public void Done(int y)
        {
            if (y >= reach)
            {
                Row(y - reach).CopyTo(plane.Row(y - reach));
            }
        }

        public void Finish()
        {
            for (int y = Math.Max(plane.Height - reach, 0); y < plane.Height; y++)
            {
                Row(y).CopyTo(plane.Row(y));
            }
        }
    }

    /// <summary>Edge-preserving filter stage 0: 5x5 plus-shaped kernel with 3x3 plus-shaped SADs.</summary>
    public static JxlPlane[] Epf0(JxlPlane[] planes, JxlLoopFilter filter, float[] sigma, int xBlocks, CancellationToken cancellationToken = default)
    {
        float sm = (float)(filter.Pass0SigmaScale * 1.65);
        return Epf(planes, 3, sm, filter, sigma, xBlocks, new Epf0Kernel(filter.ChannelScale), cancellationToken);
    }

    /// <summary>Edge-preserving filter stage 1: 3x3 plus-shaped kernel with 3x3 plus-shaped SADs.</summary>
    public static JxlPlane[] Epf1(JxlPlane[] planes, JxlLoopFilter filter, float[] sigma, int xBlocks, CancellationToken cancellationToken = default) =>
        Epf(planes, 2, 1.65f, filter, sigma, xBlocks, new Epf1Kernel(filter.ChannelScale), cancellationToken);

    /// <summary>Edge-preserving filter stage 2: 3x3 plus-shaped kernel with one-pixel SADs.</summary>
    public static JxlPlane[] Epf2(JxlPlane[] planes, JxlLoopFilter filter, float[] sigma, int xBlocks, CancellationToken cancellationToken = default)
    {
        float sm = (float)(filter.Pass2SigmaScale * 1.65);
        return Epf(planes, 1, sm, filter, sigma, xBlocks, new Epf2Kernel(filter.ChannelScale), cancellationToken);
    }

    private static readonly (int Row, int Column)[] Epf0Offsets =
    [
        (-2, 0), (-1, -1), (-1, 0), (-1, 1), (0, -2), (0, -1), (0, 1), (0, 2), (1, -1), (1, 0), (1, 1), (2, 0),
    ];

    private static readonly (int Row, int Column)[] Plus = [(0, 0), (-1, 0), (0, -1), (1, 0), (0, 1)];

    /// <summary>
    /// One EPF stage's filter of the pixel at <c>i</c> of the three planes: the weighted sums of the three channels and
    /// the total weight. A struct type parameter, so each stage's pixel loop is compiled with its kernel inlined.
    /// </summary>
    private interface IEpfKernel
    {
        void Filter(float[] p0, float[] p1, float[] p2, int i, int stride, float invSigma, out float x, out float y, out float b, out float w);
        void FilterAvx2(float[] p0, float[] p1, float[] p2, int i, int stride, Vector256<float> invSigma,
            out Vector256<float> x, out Vector256<float> y, out Vector256<float> b, out Vector256<float> w);
    }

    private readonly partial struct Epf0Kernel(IReadOnlyList<float> scale) : IEpfKernel
    {
        private readonly float scale0 = scale[0];
        private readonly float scale1 = scale[1];
        private readonly float scale2 = scale[2];

        public void Filter(float[] p0, float[] p1, float[] p2, int i, int stride, float invSigma, out float x, out float y, out float b, out float w)
        {
            Span<float> sads = stackalloc float[12];
            for (int c = 0; c < 3; c++)
            {
                float[] p = c == 0 ? p0 : c == 1 ? p1 : p2;
                float s = c == 0 ? scale0 : c == 1 ? scale1 : scale2;
                for (int k = 0; k < 12; k++)
                {
                    float sad = 0;
                    int neighbour = i + Epf0Offsets[k].Row * stride + Epf0Offsets[k].Column;
                    foreach ((int row, int column) in Plus)
                    {
                        int delta = row * stride + column;
                        sad += Math.Abs(p[i + delta] - p[neighbour + delta]);
                    }

                    sads[k] = JxlFastMath.Fma(sad, s, sads[k]);
                }
            }

            w = 1;
            x = p0[i];
            y = p1[i];
            b = p2[i];
            for (int k = 0; k < 12; k++)
            {
                int neighbour = i + Epf0Offsets[k].Row * stride + Epf0Offsets[k].Column;
                AddPixel(p0, p1, p2, neighbour, sads[k], invSigma, ref x, ref y, ref b, ref w);
            }
        }
    }

    private readonly partial struct Epf1Kernel(IReadOnlyList<float> scale) : IEpfKernel
    {
        private readonly float scale0 = scale[0];
        private readonly float scale1 = scale[1];
        private readonly float scale2 = scale[2];

        public void Filter(float[] p0, float[] p1, float[] p2, int i, int stride, float invSigma, out float x, out float y, out float b, out float w)
        {
            float sad0 = 0;
            float sad1 = 0;
            float sad2 = 0;
            float sad3 = 0;
            Sads(p0, i, stride, scale0, ref sad0, ref sad1, ref sad2, ref sad3);
            Sads(p1, i, stride, scale1, ref sad0, ref sad1, ref sad2, ref sad3);
            Sads(p2, i, stride, scale2, ref sad0, ref sad1, ref sad2, ref sad3);
            w = 1;
            x = p0[i];
            y = p1[i];
            b = p2[i];
            AddPixel(p0, p1, p2, i - stride, sad0, invSigma, ref x, ref y, ref b, ref w);
            AddPixel(p0, p1, p2, i - 1, sad1, invSigma, ref x, ref y, ref b, ref w);
            AddPixel(p0, p1, p2, i + 1, sad2, invSigma, ref x, ref y, ref b, ref w);
            AddPixel(p0, p1, p2, i + stride, sad3, invSigma, ref x, ref y, ref b, ref w);
        }

        // Adds one channel's SADs of the four neighbours, scaled; the order of additions follows libjxl.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Sads(float[] p, int i, int stride, float scale, ref float sad0, ref float sad1, ref float sad2, ref float sad3)
        {
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
            sad0 = JxlFastMath.Fma(sad0c, scale, sad0);
            sad1 = JxlFastMath.Fma(sad1c, scale, sad1);
            sad2 = JxlFastMath.Fma(sad2c, scale, sad2);
            sad3 = JxlFastMath.Fma(sad3c, scale, sad3);
        }
    }

    private readonly partial struct Epf2Kernel(IReadOnlyList<float> scale) : IEpfKernel
    {
        private readonly float scale0 = scale[0];
        private readonly float scale1 = scale[1];
        private readonly float scale2 = scale[2];

        public void Filter(float[] p0, float[] p1, float[] p2, int i, int stride, float invSigma, out float x, out float y, out float b, out float w)
        {
            float rx = p0[i];
            float ry = p1[i];
            float rb = p2[i];
            w = 1;
            x = rx;
            y = ry;
            b = rb;
            Add(p0, p1, p2, i - stride, rx, ry, rb, invSigma, ref x, ref y, ref b, ref w);
            Add(p0, p1, p2, i - 1, rx, ry, rb, invSigma, ref x, ref y, ref b, ref w);
            Add(p0, p1, p2, i + 1, rx, ry, rb, invSigma, ref x, ref y, ref b, ref w);
            Add(p0, p1, p2, i + stride, rx, ry, rb, invSigma, ref x, ref y, ref b, ref w);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Add(float[] p0, float[] p1, float[] p2, int neighbour, float rx, float ry, float rb, float invSigma,
            ref float x, ref float y, ref float b, ref float w)
        {
            float cx = p0[neighbour];
            float cy = p1[neighbour];
            float cb = p2[neighbour];
            float sad = Math.Abs(cx - rx) * scale0;
            sad = JxlFastMath.Fma(Math.Abs(cy - ry), scale1, sad);
            sad = JxlFastMath.Fma(Math.Abs(cb - rb), scale2, sad);
            float weight = Math.Max(0, JxlFastMath.Fma(sad, invSigma, 1.0f));
            w += weight;
            x = JxlFastMath.Fma(weight, cx, x);
            y = JxlFastMath.Fma(weight, cy, y);
            b = JxlFastMath.Fma(weight, cb, b);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddPixel(float[] p0, float[] p1, float[] p2, int neighbour, float sad, float invSigma, ref float x, ref float y, ref float b, ref float w)
    {
        float weight = Math.Max(0, JxlFastMath.Fma(sad, invSigma, 1.0f));
        w += weight;
        x = JxlFastMath.Fma(weight, p0[neighbour], x);
        y = JxlFastMath.Fma(weight, p1[neighbour], y);
        b = JxlFastMath.Fma(weight, p2[neighbour], b);
    }

    // Runs one EPF stage in place: blocks whose sigma is below the threshold are kept, the rest filtered pixel by pixel.
    // Returns the planes, which are copies when the inputs had different layouts.
    private static JxlPlane[] Epf<TKernel>(JxlPlane[] planes, int border, float sm, JxlLoopFilter filter, float[] sigma, int xBlocks,
        TKernel kernel, CancellationToken cancellationToken) where TKernel : struct, IEpfKernel
    {
        // The kernels address the three planes with one index: they need one layout.
        if (planes.Any(plane => plane.Stride != planes[0].Stride))
        {
            JxlPlane[] uniform = [.. planes.Select(plane => new JxlPlane(plane.Width, plane.Height))];
            for (int c = 0; c < 3; c++)
            {
                planes[c].CopyTo(uniform[c], cancellationToken);
            }

            planes = uniform;
        }

        float bsm = sm * filter.BorderSadMul;
        foreach (JxlPlane plane in planes)
        {
            plane.Mirror(border, border, cancellationToken);
        }

        (float[] src0, float[] src1, float[] src2) = (planes[0].Data, planes[1].Data, planes[2].Data);
        (DelayedRows rows0, DelayedRows rows1, DelayedRows rows2) =
            (new DelayedRows(planes[0], border), new DelayedRows(planes[1], border), new DelayedRows(planes[2], border));
        int width = planes[0].Width;
        int stride = planes[0].Stride;
        for (int y = 0; y < planes[0].Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool borderRow = y % 8 == 0 || y % 8 == 7;
            int sigmaRow = y / 8 * xBlocks;
            int rowIn = planes[0].Offset(0, y);
            Span<float> dst0 = rows0.Row(y);
            Span<float> dst1 = rows1.Row(y);
            Span<float> dst2 = rows2.Row(y);
            for (int x0 = 0; x0 < width; x0 += 8)
            {
                int count = Math.Min(8, width - x0);
                int i = rowIn + x0;
                float blockSigma = sigma[sigmaRow + x0 / 8];
                if (blockSigma < MinSigma)
                {
                    src0.AsSpan(i, count).CopyTo(dst0[x0..]);
                    src1.AsSpan(i, count).CopyTo(dst1[x0..]);
                    src2.AsSpan(i, count).CopyTo(dst2[x0..]);
                    continue;
                }

                float inner = blockSigma * sm;
                float edge = blockSigma * bsm;
                if (count == 8 && EpfAvx2(kernel, src0, src1, src2, i, stride, inner, edge, borderRow,
                    dst0.Slice(x0, 8), dst1.Slice(x0, 8), dst2.Slice(x0, 8))) continue;
                for (int k = 0; k < count; k++, i++)
                {
                    float invSigma = borderRow || k == 0 || k == 7 ? edge : inner;
                    kernel.Filter(src0, src1, src2, i, stride, invSigma, out float rx, out float ry, out float rb, out float w);
                    float inverse = 1.0f / w;
                    dst0[x0 + k] = rx * inverse;
                    dst1[x0 + k] = ry * inverse;
                    dst2[x0 + k] = rb * inverse;
                }
            }

            rows0.Done(y);
            rows1.Done(y);
            rows2.Done(y);
        }

        rows0.Finish();
        rows1.Finish();
        rows2.Finish();
        return planes;
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
