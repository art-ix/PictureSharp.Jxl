using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Formats.Jxl.Codestream.Rendering;

// Independent adjacent output pixels; neighbour and channel accumulation retain scalar order.
// All reads remain inside the mirrored logical window; delayed-row commits are unchanged.
internal static partial class JxlRenderPipeline
{
    private static int GaborishAvx2(ReadOnlySpan<float> up, ReadOnlySpan<float> middle, ReadOnlySpan<float> down,
        Span<float> result, float w0, float w1, float w2)
    {
        if (!Avx2.IsSupported || !Fma.IsSupported) return 0;
        int end = result.Length & ~7;
        for (int x = 0; x < end; x += 8)
        {
            Vector256<float> sum1 = (Vector256.Create(middle.Slice(x, 8)) + Vector256.Create(middle.Slice(x + 2, 8)))
                + (Vector256.Create(up.Slice(x + 1, 8)) + Vector256.Create(down.Slice(x + 1, 8)));
            Vector256<float> sum2 = (Vector256.Create(up.Slice(x, 8)) + Vector256.Create(up.Slice(x + 2, 8)))
                + (Vector256.Create(down.Slice(x, 8)) + Vector256.Create(down.Slice(x + 2, 8)));
            Fma.MultiplyAdd(sum2, Vector256.Create(w2), Fma.MultiplyAdd(sum1, Vector256.Create(w1),
                Vector256.Create(middle.Slice(x + 1, 8)) * w0)).CopyTo(result.Slice(x, 8));
        }
        return end;
    }

    private static bool EpfAvx2<TKernel>(TKernel kernel, float[] p0, float[] p1, float[] p2, int i, int stride,
        float inner, float edge, bool borderRow, Span<float> dst0, Span<float> dst1, Span<float> dst2)
        where TKernel : struct, IEpfKernel
    {
        if (!Avx2.IsSupported || !Fma.IsSupported) return false;
        Vector256<float> sigma = borderRow ? Vector256.Create(edge)
            : Vector256.Create(edge, inner, inner, inner, inner, inner, inner, edge);
        kernel.FilterAvx2(p0, p1, p2, i, stride, sigma, out var x, out var y, out var b, out var w);
        Vector256<float> inverse = Vector256.Create(1.0f) / w;
        (x * inverse).CopyTo(dst0); (y * inverse).CopyTo(dst1); (b * inverse).CopyTo(dst2);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> LoadEpfAvx2(float[] data, int i) => Vector256.Create(data.AsSpan(i, 8));

    private readonly partial struct Epf0Kernel
    {
        public void FilterAvx2(float[] p0, float[] p1, float[] p2, int i, int stride, Vector256<float> invSigma, out Vector256<float> x, out Vector256<float> y, out Vector256<float> b, out Vector256<float> w)
        {
            Span<Vector256<float>> sads = stackalloc Vector256<float>[12];
            for (int c = 0; c < 3; c++)
            {
                float[] p = c == 0 ? p0 : c == 1 ? p1 : p2;
                float s = c == 0 ? scale0 : c == 1 ? scale1 : scale2;
                for (int k = 0; k < 12; k++)
                {
                    Vector256<float> sad = Vector256.Create(0.0f);
                    int neighbour = i + Epf0Offsets[k].Row * stride + Epf0Offsets[k].Column;
                    foreach ((int row, int column) in Plus)
                    {
                        int delta = row * stride + column;
                        sad += Vector256.Abs(LoadEpfAvx2(p, i + delta) - LoadEpfAvx2(p, neighbour + delta));
                    }

                    sads[k] = Fma.MultiplyAdd(sad, Vector256.Create(s), sads[k]);
                }
            }

            w = Vector256.Create(1.0f);
            x = LoadEpfAvx2(p0, i);
            y = LoadEpfAvx2(p1, i);
            b = LoadEpfAvx2(p2, i);
            for (int k = 0; k < 12; k++)
            {
                int neighbour = i + Epf0Offsets[k].Row * stride + Epf0Offsets[k].Column;
                AddPixelAvx2(p0, p1, p2, neighbour, sads[k], invSigma, ref x, ref y, ref b, ref w);
            }
        }
    }



    private readonly partial struct Epf1Kernel
    {
        // The neighbours' values come from the SAD windows instead of being loaded again.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FilterAvx2(float[] p0, float[] p1, float[] p2, int i, int stride, Vector256<float> invSigma, out Vector256<float> x, out Vector256<float> y, out Vector256<float> b, out Vector256<float> w)
        {
            Vector256<float> sad0 = Vector256.Create(0.0f);
            Vector256<float> sad1 = Vector256.Create(0.0f);
            Vector256<float> sad2 = Vector256.Create(0.0f);
            Vector256<float> sad3 = Vector256.Create(0.0f);
            SadsAvx2(p0, i, stride, scale0, ref sad0, ref sad1, ref sad2, ref sad3, out Neighbours nx);
            SadsAvx2(p1, i, stride, scale1, ref sad0, ref sad1, ref sad2, ref sad3, out Neighbours ny);
            SadsAvx2(p2, i, stride, scale2, ref sad0, ref sad1, ref sad2, ref sad3, out Neighbours nb);
            w = Vector256.Create(1.0f);
            x = nx.Center;
            y = ny.Center;
            b = nb.Center;
            AddWeightedAvx2(nx.Up, ny.Up, nb.Up, sad0, invSigma, ref x, ref y, ref b, ref w);
            AddWeightedAvx2(nx.Left, ny.Left, nb.Left, sad1, invSigma, ref x, ref y, ref b, ref w);
            AddWeightedAvx2(nx.Right, ny.Right, nb.Right, sad2, invSigma, ref x, ref y, ref b, ref w);
            AddWeightedAvx2(nx.Down, ny.Down, nb.Down, sad3, invSigma, ref x, ref y, ref b, ref w);
        }

        private struct Neighbours
        {
            public Vector256<float> Center, Up, Left, Right, Down;
        }

        // Adds one channel's SADs of the four neighbours, scaled; the order of additions follows libjxl. The five rows
        // are read through windows of the columns they need, one bounds check each.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SadsAvx2(float[] p, int i, int stride, float scale, ref Vector256<float> sad0, ref Vector256<float> sad1,
            ref Vector256<float> sad2, ref Vector256<float> sad3, out Neighbours neighbours)
        {
            ReadOnlySpan<float> row0 = p.AsSpan(i - 2 * stride, 8);
            ReadOnlySpan<float> row1 = p.AsSpan(i - stride - 1, 10);
            ReadOnlySpan<float> row2 = p.AsSpan(i - 2, 12);
            ReadOnlySpan<float> row3 = p.AsSpan(i + stride - 1, 10);
            ReadOnlySpan<float> row4 = p.AsSpan(i + 2 * stride, 8);
            Vector256<float> p20 = Vector256.Create(row0);
            Vector256<float> p21 = Vector256.Create(row1.Slice(1, 8));
            Vector256<float> sad0c = Vector256.Abs(p20 - p21);
            Vector256<float> p11 = Vector256.Create(row1[..8]);
            Vector256<float> sad1c = Vector256.Abs(p11 - p21);
            Vector256<float> p31 = Vector256.Create(row1.Slice(2, 8));
            Vector256<float> sad2c = Vector256.Abs(p31 - p21);
            Vector256<float> p02 = Vector256.Create(row2[..8]);
            Vector256<float> p12 = Vector256.Create(row2.Slice(1, 8));
            sad1c += Vector256.Abs(p02 - p12);
            sad0c += Vector256.Abs(p11 - p12);
            Vector256<float> p22 = Vector256.Create(row2.Slice(2, 8));
            Vector256<float> t = Vector256.Abs(p12 - p22);
            sad1c += t;
            sad2c += t;
            t = Vector256.Abs(p22 - p21);
            Vector256<float> sad3c = t;
            sad0c += t;
            Vector256<float> p32 = Vector256.Create(row2.Slice(3, 8));
            sad0c += Vector256.Abs(p31 - p32);
            t = Vector256.Abs(p22 - p32);
            sad1c += t;
            sad2c += t;
            Vector256<float> p42 = Vector256.Create(row2.Slice(4, 8));
            sad2c += Vector256.Abs(p42 - p32);
            Vector256<float> p13 = Vector256.Create(row3[..8]);
            sad3c += Vector256.Abs(p13 - p12);
            Vector256<float> p23 = Vector256.Create(row3.Slice(1, 8));
            t = Vector256.Abs(p22 - p23);
            sad0c += t;
            sad3c += t;
            sad1c += Vector256.Abs(p13 - p23);
            Vector256<float> p33 = Vector256.Create(row3.Slice(2, 8));
            sad2c += Vector256.Abs(p33 - p23);
            sad3c += Vector256.Abs(p33 - p32);
            Vector256<float> p24 = Vector256.Create(row4);
            sad3c += Vector256.Abs(p24 - p23);
            sad0 = Fma.MultiplyAdd(sad0c, Vector256.Create(scale), sad0);
            sad1 = Fma.MultiplyAdd(sad1c, Vector256.Create(scale), sad1);
            sad2 = Fma.MultiplyAdd(sad2c, Vector256.Create(scale), sad2);
            sad3 = Fma.MultiplyAdd(sad3c, Vector256.Create(scale), sad3);
            neighbours = new Neighbours { Center = p22, Up = p21, Left = p12, Right = p32, Down = p23 };
        }
    }



    private readonly partial struct Epf2Kernel
    {
        public void FilterAvx2(float[] p0, float[] p1, float[] p2, int i, int stride, Vector256<float> invSigma, out Vector256<float> x, out Vector256<float> y, out Vector256<float> b, out Vector256<float> w)
        {
            Vector256<float> rx = LoadEpfAvx2(p0, i);
            Vector256<float> ry = LoadEpfAvx2(p1, i);
            Vector256<float> rb = LoadEpfAvx2(p2, i);
            w = Vector256.Create(1.0f);
            x = rx;
            y = ry;
            b = rb;
            AddAvx2(p0, p1, p2, i - stride, rx, ry, rb, invSigma, ref x, ref y, ref b, ref w);
            AddAvx2(p0, p1, p2, i - 1, rx, ry, rb, invSigma, ref x, ref y, ref b, ref w);
            AddAvx2(p0, p1, p2, i + 1, rx, ry, rb, invSigma, ref x, ref y, ref b, ref w);
            AddAvx2(p0, p1, p2, i + stride, rx, ry, rb, invSigma, ref x, ref y, ref b, ref w);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AddAvx2(float[] p0, float[] p1, float[] p2, int neighbour, Vector256<float> rx, Vector256<float> ry, Vector256<float> rb, Vector256<float> invSigma,
            ref Vector256<float> x, ref Vector256<float> y, ref Vector256<float> b, ref Vector256<float> w)
        {
            Vector256<float> cx = LoadEpfAvx2(p0, neighbour);
            Vector256<float> cy = LoadEpfAvx2(p1, neighbour);
            Vector256<float> cb = LoadEpfAvx2(p2, neighbour);
            Vector256<float> sad = Vector256.Abs(cx - rx) * Vector256.Create(scale0);
            sad = Fma.MultiplyAdd(Vector256.Abs(cy - ry), Vector256.Create(scale1), sad);
            sad = Fma.MultiplyAdd(Vector256.Abs(cb - rb), Vector256.Create(scale2), sad);
            Vector256<float> weight = WeightAvx2(sad, invSigma);
            w += weight;
            x = Fma.MultiplyAdd(weight, cx, x);
            y = Fma.MultiplyAdd(weight, cy, y);
            b = Fma.MultiplyAdd(weight, cb, b);
        }
    }



    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddPixelAvx2(float[] p0, float[] p1, float[] p2, int neighbour, Vector256<float> sad, Vector256<float> invSigma, ref Vector256<float> x, ref Vector256<float> y, ref Vector256<float> b, ref Vector256<float> w) =>
        AddWeightedAvx2(LoadEpfAvx2(p0, neighbour), LoadEpfAvx2(p1, neighbour), LoadEpfAvx2(p2, neighbour), sad, invSigma, ref x, ref y, ref b, ref w);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddWeightedAvx2(Vector256<float> cx, Vector256<float> cy, Vector256<float> cb, Vector256<float> sad, Vector256<float> invSigma,
        ref Vector256<float> x, ref Vector256<float> y, ref Vector256<float> b, ref Vector256<float> w)
    {
        Vector256<float> weight = WeightAvx2(sad, invSigma);
        w += weight;
        x = Fma.MultiplyAdd(weight, cx, x);
        y = Fma.MultiplyAdd(weight, cy, y);
        b = Fma.MultiplyAdd(weight, cb, b);
    }

    // Math.Max(0, fma(sad, invSigma, 1)) as one vmaxps: it returns its second operand for NaN, as Math.Max does, and
    // for two zeros, which never differ in sign here because an exact zero sum rounds to +0.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> WeightAvx2(Vector256<float> sad, Vector256<float> invSigma) =>
        Avx.Max(Vector256<float>.Zero, Fma.MultiplyAdd(sad, invSigma, Vector256.Create(1.0f)));
}
