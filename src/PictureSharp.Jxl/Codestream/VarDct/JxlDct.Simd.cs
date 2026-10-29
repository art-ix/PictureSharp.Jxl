using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Formats.Jxl.Codestream.VarDct;

// Lane-independent ports of the 0.7.10 butterflies (libjxl v0.12.0, BSD-3-Clause).
// FMA is required independently of AVX2: every lane follows the scalar fused expression tree.
internal static partial class JxlDct
{
    internal static bool CanTransformAvx2 => SelectFusedAvx2(Avx2.IsSupported, Fma.IsSupported);

    internal static bool SelectFusedAvx2(bool avx2Supported, bool fmaSupported) => avx2Supported && fmaSupported;

    // AfvBasis with rows and columns swapped, so the forward AFVDCT4x4 reads it like the inverse reads AfvBasis.
    private static readonly float[] AfvBasisTransposed = CreateAfvBasisTransposed();

    private static float[] CreateAfvBasisTransposed()
    {
        float[] transposed = new float[256];
        for (int i = 0; i < 16; i++)
        {
            for (int j = 0; j < 16; j++) transposed[j * 16 + i] = JxlDctTables.AfvBasis[i * 16 + j];
        }

        return transposed;
    }

    // The 16×16 AFV basis product output[i] = Σ input[j]·basis[j·16 + i], all sixteen outputs at once: each lane
    // keeps the scalar chain of FMAs from zero in ascending j. Returns 16, or 0 when AVX2+FMA is unavailable.
    private static int AfvBasisAvx2(ReadOnlySpan<float> input, ReadOnlySpan<float> basis, Span<float> output)
    {
        if (!CanTransformAvx2) return 0;
        Vector256<float> low = Vector256<float>.Zero, high = Vector256<float>.Zero;
        for (int j = 0; j < 16; j++)
        {
            Vector256<float> value = Vector256.Create(input[j]);
            low = Fma.MultiplyAdd(value, Vector256.Create(basis.Slice(j * 16, 8)), low);
            high = Fma.MultiplyAdd(value, Vector256.Create(basis.Slice(j * 16 + 8, 8)), high);
        }

        low.CopyTo(output);
        high.CopyTo(output[8..]);
        return 16;
    }

    // Gather of the four-row sub-blocks of an 8×8 coefficient block: rows of eight consecutive coefficients (DCT4X8
    // halves, start 0 or 8), or of every second one (4×4 quarters, start 0, 1, 8 or 9). Pure data movement; the
    // loads stay inside the 64 coefficients. False when AVX2 is unavailable or for any other shape.
    private static bool GatherAvx2(ReadOnlySpan<float> coefficients, int start, int columnStep, int columns, float dc, Span<float> block)
    {
        if (!Avx2.IsSupported) return false;
        if (columnStep == 1 && columns == 8 && start is 0 or 8)
        {
            for (int iy = 0; iy < 4; iy++) Vector256.Create(coefficients.Slice(start + iy * 16, 8)).CopyTo(block.Slice(iy * 8, 8));
        }
        else if (columnStep == 2 && columns == 4 && start is 0 or 1 or 8 or 9)
        {
            // Rows start at an even index; odd starts take the odd lanes. Two rows fill one vector.
            int parity = start & 1;
            Vector256<int> low = Vector256.Create(0, 2, 4, 6, 0, 2, 4, 6) + Vector256.Create(parity);
            Vector256<int> high = Vector256.Create(1, 3, 5, 7, 1, 3, 5, 7) + Vector256.Create(parity - 1);
            ReadOnlySpan<float> rows = coefficients.Slice(start - parity, 56);
            for (int iy = 0; iy < 4; iy += 2)
            {
                Vector256<float> top = Avx2.PermuteVar8x32(Vector256.Create(rows.Slice(iy * 16, 8)), low);
                Vector256<float> bottom = Avx2.PermuteVar8x32(Vector256.Create(rows.Slice(iy * 16 + 16, 8)), high);
                Avx.Blend(top, bottom, 0xF0).CopyTo(block.Slice(iy * 4, 8));
            }
        }
        else
        {
            return false;
        }

        block[0] = dc;
        return true;
    }

    // The three levels of the 2x2 Haar pyramid (Dct2X2) with four lanes per row: every coefficient keeps the scalar
    // expression c00 ± c01 ± c10 ± c11 (left to right). Levels 2 and 4 work on the top-left 4×4 corner in registers;
    // level 8 writes the pixels directly. False when SSE4.1 is unavailable.
    private static bool Dct2X2Sse41(ReadOnlySpan<float> coefficients, Span<float> pixels, int stride)
    {
        if (!Sse41.IsSupported) return false;
        coefficients = coefficients[..64];
        float c00 = coefficients[0], c01 = coefficients[1], c10 = coefficients[8], c11 = coefficients[9];
        Vector128<float> q0 = Vector128.Create(c00 + c01 + c10 + c11, c00 + c01 - c10 - c11, coefficients[2], coefficients[3]);
        Vector128<float> q1 = Vector128.Create(c00 - c01 + c10 - c11, c00 - c01 - c10 + c11, coefficients[10], coefficients[11]);
        Vector128<float> q2 = Load128(coefficients, 16), q3 = Load128(coefficients, 24);

        // Level 4: lanes 0 and 1 hold x = 0 and 1 of the corner rows y and y + 2, shifted copies the columns 2 + x.
        HaarInverse(q0, Sse.MoveHighToLow(q0, q0), q2, Sse.MoveHighToLow(q2, q2), out Vector128<float> s0, out Vector128<float> s1,
            out Vector128<float> s2, out Vector128<float> s3);
        Vector128<float> r0 = Sse.UnpackLow(s0, s1), r1 = Sse.UnpackLow(s2, s3);
        HaarInverse(q1, Sse.MoveHighToLow(q1, q1), q3, Sse.MoveHighToLow(q3, q3), out s0, out s1, out s2, out s3);
        Vector128<float> r2 = Sse.UnpackLow(s0, s1), r3 = Sse.UnpackLow(s2, s3);

        LevelEight(0, r0, coefficients, pixels, stride);
        LevelEight(1, r1, coefficients, pixels, stride);
        LevelEight(2, r2, coefficients, pixels, stride);
        LevelEight(3, r3, coefficients, pixels, stride);
        return true;

        static void LevelEight(int y, Vector128<float> corner, ReadOnlySpan<float> coefficients, Span<float> pixels, int stride)
        {
            HaarInverse(corner, Load128(coefficients, y * 8 + 4), Load128(coefficients, y * 8 + 32), Load128(coefficients, y * 8 + 36),
                out Vector128<float> s0, out Vector128<float> s1, out Vector128<float> s2, out Vector128<float> s3);
            Span<float> even = pixels.Slice(2 * y * stride, 8), odd = pixels.Slice((2 * y + 1) * stride, 8);
            Sse.UnpackLow(s0, s1).CopyTo(even);
            Sse.UnpackHigh(s0, s1).CopyTo(even[4..]);
            Sse.UnpackLow(s2, s3).CopyTo(odd);
            Sse.UnpackHigh(s2, s3).CopyTo(odd[4..]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HaarInverse(Vector128<float> c00, Vector128<float> c01, Vector128<float> c10, Vector128<float> c11,
        out Vector128<float> s0, out Vector128<float> s1, out Vector128<float> s2, out Vector128<float> s3)
    {
        s0 = c00 + c01 + c10 + c11;
        s1 = c00 + c01 - c10 - c11;
        s2 = c00 - c01 + c10 - c11;
        s3 = c00 - c01 - c10 + c11;
    }

    // The forward pyramid (Dct2TopBlock 8, 4, 2) of ForwardDct2X2 with the same expressions and the 0.25 scaling.
    private static bool ForwardDct2X2Sse41(ReadOnlySpan<float> pixels, int stride, Span<float> coefficients)
    {
        if (!Sse41.IsSupported) return false;
        coefficients = coefficients[..64];
        Vector128<float> quarter = Vector128.Create(0.25f);
        Span<Vector128<float>> corner = stackalloc Vector128<float>[4];
        for (int y = 0; y < 4; y++)
        {
            ReadOnlySpan<float> even = pixels.Slice(2 * y * stride, 8), odd = pixels.Slice((2 * y + 1) * stride, 8);
            Vector128<float> e0 = Load128(even, 0), e1 = Load128(even, 4), o0 = Load128(odd, 0), o1 = Load128(odd, 4);
            HaarForward(Sse.Shuffle(e0, e1, 0b10_00_10_00), Sse.Shuffle(e0, e1, 0b11_01_11_01), Sse.Shuffle(o0, o1, 0b10_00_10_00),
                Sse.Shuffle(o0, o1, 0b11_01_11_01), quarter, out corner[y], out Vector128<float> s1, out Vector128<float> s2, out Vector128<float> s3);
            s1.CopyTo(coefficients.Slice(y * 8 + 4, 4));
            s2.CopyTo(coefficients.Slice(y * 8 + 32, 4));
            s3.CopyTo(coefficients.Slice(y * 8 + 36, 4));
        }

        // Level 4 on the corner: lanes 0 and 1 of the shuffles are x = 0 and 1 of rows 2y (c00, c01) and 2y + 1 (c10, c11).
        for (int y = 0; y < 2; y++)
        {
            Vector128<float> top = corner[2 * y], bottom = corner[2 * y + 1];
            HaarForward(Sse.Shuffle(top, top, 0b10_00_10_00), Sse.Shuffle(top, top, 0b11_01_11_01), Sse.Shuffle(bottom, bottom, 0b10_00_10_00),
                Sse.Shuffle(bottom, bottom, 0b11_01_11_01), quarter, out Vector128<float> s0, out Vector128<float> s1, out Vector128<float> s2,
                out Vector128<float> s3);
            Sse.MoveLowToHigh(s0, s1).CopyTo(coefficients.Slice(y * 8, 4));
            Sse.MoveLowToHigh(s2, s3).CopyTo(coefficients.Slice(y * 8 + 16, 4));
        }

        float c00 = coefficients[0], c01 = coefficients[1], c10 = coefficients[8], c11 = coefficients[9];
        coefficients[0] = (c00 + c01 + c10 + c11) * 0.25f;
        coefficients[1] = (c00 + c01 - c10 - c11) * 0.25f;
        coefficients[8] = (c00 - c01 + c10 - c11) * 0.25f;
        coefficients[9] = (c00 - c01 - c10 + c11) * 0.25f;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HaarForward(Vector128<float> c00, Vector128<float> c01, Vector128<float> c10, Vector128<float> c11, Vector128<float> quarter,
        out Vector128<float> s0, out Vector128<float> s1, out Vector128<float> s2, out Vector128<float> s3)
    {
        s0 = (c00 + c01 + c10 + c11) * quarter;
        s1 = (c00 + c01 - c10 - c11) * quarter;
        s2 = (c00 - c01 + c10 - c11) * quarter;
        s3 = (c00 - c01 - c10 + c11) * quarter;
    }

    internal static int TransformColumnsSimd(int n, int count, ReadOnlySpan<float> from, int fromStride,
        Span<float> to, int toStride, bool inverse, float scale)
    {
        if (!CanTransformAvx2 || n is not (2 or 4 or 8)) return 0;
        int end = count & ~7;
        for (int c = 0; c < end; c += 8)
        {
            if (n == 8)
            {
                if (inverse) Idct8Avx2(from, c, fromStride, to, c, toStride);
                else Dct8Avx2(from, c, fromStride, to, c, toStride, scale);
            }
            else
            {
                Vector256<float> a = LoadAvx2(from, c), b = LoadAvx2(from, fromStride + c);
                if (n == 2)
                {
                    (Vector256<float> sum, Vector256<float> difference) = (a + b, a - b);
                    if (scale != 1.0f) (sum, difference) = (scale * sum, scale * difference);
                    sum.CopyTo(to.Slice(c, 8));
                    difference.CopyTo(to.Slice(toStride + c, 8));
                    continue;
                }
                Vector256<float> d = LoadAvx2(from, 2 * fromStride + c), e = LoadAvx2(from, 3 * fromStride + c);
                if (inverse) Idct4Avx2(a, b, d, e, out a, out b, out d, out e);
                else Dct4Avx2(a, b, d, e, out a, out b, out d, out e);
                if (scale != 1.0f) (a, b, d, e) = (scale * a, scale * b, scale * d, scale * e);
                a.CopyTo(to.Slice(c, 8)); b.CopyTo(to.Slice(toStride + c, 8));
                d.CopyTo(to.Slice(2 * toStride + c, 8)); e.CopyTo(to.Slice(3 * toStride + c, 8));
            }
        }

        if (count - end >= 4 && TransformColumns128(n, end, from, fromStride, to, toStride, inverse, scale)) end += 4;

        return end;
    }

    // Four columns of TransformColumnsSimd with 128-bit vectors and the same FMA-based operations.
    private static bool TransformColumns128(int n, int c, ReadOnlySpan<float> from, int fromStride, Span<float> to, int toStride, bool inverse, float scale)
    {
        Vector128<float> a = Load128(from, c), b = Load128(from, fromStride + c);
        if (n == 2)
        {
            (Vector128<float> sum, Vector128<float> difference) = (a + b, a - b);
            if (scale != 1.0f) (sum, difference) = (scale * sum, scale * difference);
            sum.CopyTo(to.Slice(c, 4));
            difference.CopyTo(to.Slice(toStride + c, 4));
            return true;
        }

        Vector128<float> d = Load128(from, 2 * fromStride + c), e = Load128(from, 3 * fromStride + c);
        if (n == 4)
        {
            if (inverse) Idct4x128(a, b, d, e, out a, out b, out d, out e);
            else Dct4x128(a, b, d, e, out a, out b, out d, out e);
            if (scale != 1.0f) (a, b, d, e) = (scale * a, scale * b, scale * d, scale * e);
            a.CopyTo(to.Slice(c, 4)); b.CopyTo(to.Slice(toStride + c, 4));
            d.CopyTo(to.Slice(2 * toStride + c, 4)); e.CopyTo(to.Slice(3 * toStride + c, 4));
            return true;
        }

        Vector128<float> f = Load128(from, 4 * fromStride + c), g = Load128(from, 5 * fromStride + c);
        Vector128<float> h = Load128(from, 6 * fromStride + c), k = Load128(from, 7 * fromStride + c);
        ReadOnlySpan<float> m = JxlDctTables.Multipliers8;
        Vector128<float> r0, r1, r2, r3, r4, r5, r6, r7;
        if (inverse)
        {
            Idct4x128(a, d, f, h, out Vector128<float> e0, out Vector128<float> e1, out Vector128<float> e2, out Vector128<float> e3);
            Idct4x128(b * Vector128.Create(Sqrt2), e + b, g + e, k + g, out Vector128<float> o0, out Vector128<float> o1, out Vector128<float> o2, out Vector128<float> o3);
            r7 = Fma.MultiplyAdd(Vector128.Create(-m[0]), o0, e0);
            r0 = Fma.MultiplyAdd(Vector128.Create(m[0]), o0, e0);
            r6 = Fma.MultiplyAdd(Vector128.Create(-m[1]), o1, e1);
            r1 = Fma.MultiplyAdd(Vector128.Create(m[1]), o1, e1);
            r5 = Fma.MultiplyAdd(Vector128.Create(-m[2]), o2, e2);
            r2 = Fma.MultiplyAdd(Vector128.Create(m[2]), o2, e2);
            r4 = Fma.MultiplyAdd(Vector128.Create(-m[3]), o3, e3);
            r3 = Fma.MultiplyAdd(Vector128.Create(m[3]), o3, e3);
        }
        else
        {
            Dct4x128(a + k, b + h, d + g, e + f, out Vector128<float> t0, out Vector128<float> t2, out Vector128<float> t4, out Vector128<float> t6);
            Dct4x128((a - k) * Vector128.Create(m[0]), (b - h) * Vector128.Create(m[1]), (d - g) * Vector128.Create(m[2]), (e - f) * Vector128.Create(m[3]),
                out Vector128<float> u0, out Vector128<float> u1, out Vector128<float> u2, out Vector128<float> u3);
            r0 = scale * t0;
            r1 = scale * Fma.MultiplyAdd(u0, Vector128.Create(Sqrt2), u1);
            r2 = scale * t2;
            r3 = scale * (u1 + u2);
            r4 = scale * t4;
            r5 = scale * (u2 + u3);
            r6 = scale * t6;
            r7 = scale * u3;
        }

        r0.CopyTo(to.Slice(c, 4)); r1.CopyTo(to.Slice(toStride + c, 4));
        r2.CopyTo(to.Slice(2 * toStride + c, 4)); r3.CopyTo(to.Slice(3 * toStride + c, 4));
        r4.CopyTo(to.Slice(4 * toStride + c, 4)); r5.CopyTo(to.Slice(5 * toStride + c, 4));
        r6.CopyTo(to.Slice(6 * toStride + c, 4)); r7.CopyTo(to.Slice(7 * toStride + c, 4));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Load128(ReadOnlySpan<float> data, int offset) => Vector128.Create(data.Slice(offset, 4));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Idct4x128(Vector128<float> in0, Vector128<float> in1, Vector128<float> in2, Vector128<float> in3, out Vector128<float> out0, out Vector128<float> out1, out Vector128<float> out2, out Vector128<float> out3)
    {
        Vector128<float> e0 = in0 + in2;
        Vector128<float> e1 = in0 - in2;
        Vector128<float> odd0 = in1 * Vector128.Create(Sqrt2);
        Vector128<float> odd1 = in3 + in1;
        Vector128<float> p0 = odd0 + odd1;
        Vector128<float> p1 = odd0 - odd1;
        ReadOnlySpan<float> m = JxlDctTables.Multipliers4;
        out0 = Fma.MultiplyAdd(Vector128.Create(m[0]), p0, e0);
        out3 = Fma.MultiplyAdd(Vector128.Create(-m[0]), p0, e0);
        out1 = Fma.MultiplyAdd(Vector128.Create(m[1]), p1, e1);
        out2 = Fma.MultiplyAdd(Vector128.Create(-m[1]), p1, e1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Dct4x128(Vector128<float> m0, Vector128<float> m1, Vector128<float> m2, Vector128<float> m3, out Vector128<float> out0, out Vector128<float> out1, out Vector128<float> out2, out Vector128<float> out3)
    {
        Vector128<float> t0 = m0 + m3;
        Vector128<float> t1 = m1 + m2;
        ReadOnlySpan<float> m = JxlDctTables.Multipliers4;
        Vector128<float> u0 = (m0 - m3) * Vector128.Create(m[0]);
        Vector128<float> u1 = (m1 - m2) * Vector128.Create(m[1]);
        Vector128<float> v0 = u0 + u1;
        Vector128<float> v1 = u0 - u1;
        out0 = t0 + t1;
        out1 = Fma.MultiplyAdd(v0, Vector128.Create(Sqrt2), v1);
        out2 = t0 - t1;
        out3 = v1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> LoadAvx2(ReadOnlySpan<float> data, int offset) => Vector256.Create(data.Slice(offset, 8));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Idct4Avx2(Vector256<float> in0, Vector256<float> in1, Vector256<float> in2, Vector256<float> in3, out Vector256<float> out0, out Vector256<float> out1, out Vector256<float> out2, out Vector256<float> out3)
    {
        Vector256<float> e0 = in0 + in2;
        Vector256<float> e1 = in0 - in2;
        Vector256<float> odd0 = in1 * Vector256.Create(Sqrt2);
        Vector256<float> odd1 = in3 + in1;
        Vector256<float> p0 = odd0 + odd1;
        Vector256<float> p1 = odd0 - odd1;
        ReadOnlySpan<float> m = JxlDctTables.Multipliers4;
        out0 = Fma.MultiplyAdd(Vector256.Create(m[0]), p0, e0);
        out3 = Fma.MultiplyAdd(Vector256.Create(-m[0]), p0, e0);
        out1 = Fma.MultiplyAdd(Vector256.Create(m[1]), p1, e1);
        out2 = Fma.MultiplyAdd(Vector256.Create(-m[1]), p1, e1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Dct4Avx2(Vector256<float> m0, Vector256<float> m1, Vector256<float> m2, Vector256<float> m3, out Vector256<float> out0, out Vector256<float> out1, out Vector256<float> out2, out Vector256<float> out3)
    {
        Vector256<float> t0 = m0 + m3;
        Vector256<float> t1 = m1 + m2;
        ReadOnlySpan<float> m = JxlDctTables.Multipliers4;
        Vector256<float> u0 = (m0 - m3) * Vector256.Create(m[0]);
        Vector256<float> u1 = (m1 - m2) * Vector256.Create(m[1]);
        Vector256<float> v0 = u0 + u1;
        Vector256<float> v1 = u0 - u1;
        out0 = t0 + t1;
        out1 = Fma.MultiplyAdd(v0, Vector256.Create(Sqrt2), v1);
        out2 = t0 - t1;
        out3 = v1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Idct8Avx2(ReadOnlySpan<float> from, int fromOffset, int fromStride, Span<float> to, int toOffset, int toStride)
    {
        Vector256<float> r0 = LoadAvx2(from, fromOffset), r1 = LoadAvx2(from, fromOffset + fromStride), r2 = LoadAvx2(from, fromOffset + 2 * fromStride), r3 = LoadAvx2(from, fromOffset + 3 * fromStride);
        Vector256<float> r4 = LoadAvx2(from, fromOffset + 4 * fromStride), r5 = LoadAvx2(from, fromOffset + 5 * fromStride), r6 = LoadAvx2(from, fromOffset + 6 * fromStride);
        Vector256<float> r7 = LoadAvx2(from, fromOffset + 7 * fromStride);
        Idct8Avx2(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Store8Avx2(to, toOffset, toStride, r0, r1, r2, r3, r4, r5, r6, r7);
    }

    // Element i of each column in r_i; results replace them in natural row order.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Idct8Avx2(ref Vector256<float> r0, ref Vector256<float> r1, ref Vector256<float> r2, ref Vector256<float> r3,
        ref Vector256<float> r4, ref Vector256<float> r5, ref Vector256<float> r6, ref Vector256<float> r7)
    {
        Idct4Avx2(r0, r2, r4, r6, out Vector256<float> e0, out Vector256<float> e1, out Vector256<float> e2, out Vector256<float> e3);
        Idct4Avx2(r1 * Vector256.Create(Sqrt2), r3 + r1, r5 + r3, r7 + r5, out Vector256<float> o0, out Vector256<float> o1, out Vector256<float> o2, out Vector256<float> o3);
        ReadOnlySpan<float> m = JxlDctTables.Multipliers8;
        r7 = Fma.MultiplyAdd(Vector256.Create(-m[0]), o0, e0);
        r0 = Fma.MultiplyAdd(Vector256.Create(m[0]), o0, e0);
        r6 = Fma.MultiplyAdd(Vector256.Create(-m[1]), o1, e1);
        r1 = Fma.MultiplyAdd(Vector256.Create(m[1]), o1, e1);
        r5 = Fma.MultiplyAdd(Vector256.Create(-m[2]), o2, e2);
        r2 = Fma.MultiplyAdd(Vector256.Create(m[2]), o2, e2);
        r4 = Fma.MultiplyAdd(Vector256.Create(-m[3]), o3, e3);
        r3 = Fma.MultiplyAdd(Vector256.Create(m[3]), o3, e3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Dct8Avx2(ReadOnlySpan<float> from, int fromOffset, int fromStride, Span<float> to, int toOffset, int toStride, float scale)
    {
        Vector256<float> r0 = LoadAvx2(from, fromOffset), r1 = LoadAvx2(from, fromOffset + fromStride), r2 = LoadAvx2(from, fromOffset + 2 * fromStride), r3 = LoadAvx2(from, fromOffset + 3 * fromStride);
        Vector256<float> r4 = LoadAvx2(from, fromOffset + 4 * fromStride), r5 = LoadAvx2(from, fromOffset + 5 * fromStride), r6 = LoadAvx2(from, fromOffset + 6 * fromStride);
        Vector256<float> r7 = LoadAvx2(from, fromOffset + 7 * fromStride);
        Dct8Avx2(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, scale);
        Store8Avx2(to, toOffset, toStride, r0, r1, r2, r3, r4, r5, r6, r7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Dct8Avx2(ref Vector256<float> r0, ref Vector256<float> r1, ref Vector256<float> r2, ref Vector256<float> r3,
        ref Vector256<float> r4, ref Vector256<float> r5, ref Vector256<float> r6, ref Vector256<float> r7, float scale)
    {
        ReadOnlySpan<float> m = JxlDctTables.Multipliers8;
        Dct4Avx2(r0 + r7, r1 + r6, r2 + r5, r3 + r4, out Vector256<float> t0, out Vector256<float> t2, out Vector256<float> t4, out Vector256<float> t6);
        Dct4Avx2((r0 - r7) * Vector256.Create(m[0]), (r1 - r6) * Vector256.Create(m[1]), (r2 - r5) * Vector256.Create(m[2]), (r3 - r4) * Vector256.Create(m[3]), out Vector256<float> u0, out Vector256<float> u1, out Vector256<float> u2, out Vector256<float> u3);
        r0 = scale * t0;
        r1 = scale * Fma.MultiplyAdd(u0, Vector256.Create(Sqrt2), u1);
        r2 = scale * t2;
        r3 = scale * (u1 + u2);
        r4 = scale * t4;
        r5 = scale * (u2 + u3);
        r6 = scale * t6;
        r7 = scale * u3;
    }

    /// <summary>
    /// The square 8×8 ScaledIdct/ScaledDct entirely in registers: columns, an in-register transpose, columns again.
    /// The same per-element operations as the scalar passes through scratch; returns false when unavailable.
    /// </summary>
    internal static bool ScaledIdct8x8Avx2(ReadOnlySpan<float> from, Span<float> to, int toStride)
    {
        if (!CanTransformAvx2) return false;
        Vector256<float> r0 = LoadAvx2(from, 0), r1 = LoadAvx2(from, 8), r2 = LoadAvx2(from, 16), r3 = LoadAvx2(from, 24);
        Vector256<float> r4 = LoadAvx2(from, 32), r5 = LoadAvx2(from, 40), r6 = LoadAvx2(from, 48), r7 = LoadAvx2(from, 56);
        Idct8Avx2(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Transpose8x8Avx(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Idct8Avx2(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Store8Avx2(to, 0, toStride, r0, r1, r2, r3, r4, r5, r6, r7);
        return true;
    }

    /// <inheritdoc cref="ScaledIdct8x8Avx2"/>
    internal static bool ScaledDct8x8Avx2(ReadOnlySpan<float> from, int fromStride, Span<float> to)
    {
        if (!CanTransformAvx2) return false;
        Vector256<float> r0 = LoadAvx2(from, 0), r1 = LoadAvx2(from, fromStride), r2 = LoadAvx2(from, 2 * fromStride), r3 = LoadAvx2(from, 3 * fromStride);
        Vector256<float> r4 = LoadAvx2(from, 4 * fromStride), r5 = LoadAvx2(from, 5 * fromStride), r6 = LoadAvx2(from, 6 * fromStride);
        Vector256<float> r7 = LoadAvx2(from, 7 * fromStride);
        Dct8Avx2(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, 0.125f);
        Transpose8x8Avx(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Dct8Avx2(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, 0.125f);
        Store8Avx2(to, 0, 8, r0, r1, r2, r3, r4, r5, r6, r7);
        return true;
    }

    /// <summary>
    /// The 16-point IdctColumns per group of eight columns: both 8-point halves in registers, read straight from the
    /// interleaved rows and merged straight into <paramref name="to"/> (which may be <paramref name="data"/>). The same
    /// operations as the even/odd copies, row additions, scaling and merge of the scalar recursion; false when unavailable.
    /// </summary>
    internal static bool IdctColumns16Avx2(int count, ReadOnlySpan<float> data, Span<float> to, int toStride, Span<float> scratch)
    {
        if (!CanTransformAvx2 || (count & 7) != 0) return false;
        Span<float> even = scratch[..64];
        ReadOnlySpan<float> m = JxlDctTables.Multipliers16;
        for (int c = 0; c < count; c += 8)
        {
            Vector256<float> r0 = LoadAvx2(data, c), r1 = LoadAvx2(data, 2 * count + c), r2 = LoadAvx2(data, 4 * count + c), r3 = LoadAvx2(data, 6 * count + c);
            Vector256<float> r4 = LoadAvx2(data, 8 * count + c), r5 = LoadAvx2(data, 10 * count + c), r6 = LoadAvx2(data, 12 * count + c);
            Vector256<float> r7 = LoadAvx2(data, 14 * count + c);
            Idct8Avx2(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            Store8Avx2(even, 0, 8, r0, r1, r2, r3, r4, r5, r6, r7);

            Vector256<float> o1 = LoadAvx2(data, count + c), o3 = LoadAvx2(data, 3 * count + c), o5 = LoadAvx2(data, 5 * count + c), o7 = LoadAvx2(data, 7 * count + c);
            Vector256<float> o9 = LoadAvx2(data, 9 * count + c), o11 = LoadAvx2(data, 11 * count + c), o13 = LoadAvx2(data, 13 * count + c);
            Vector256<float> o15 = LoadAvx2(data, 15 * count + c);
            r7 = o15 + o13; r6 = o13 + o11; r5 = o11 + o9; r4 = o9 + o7;
            r3 = o7 + o5; r2 = o5 + o3; r1 = o3 + o1; r0 = o1 * Vector256.Create(Sqrt2);
            Idct8Avx2(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            MergeAvx2(even, 0, r0, m[0], to, c, 15 * toStride + c);
            MergeAvx2(even, 8, r1, m[1], to, toStride + c, 14 * toStride + c);
            MergeAvx2(even, 16, r2, m[2], to, 2 * toStride + c, 13 * toStride + c);
            MergeAvx2(even, 24, r3, m[3], to, 3 * toStride + c, 12 * toStride + c);
            MergeAvx2(even, 32, r4, m[4], to, 4 * toStride + c, 11 * toStride + c);
            MergeAvx2(even, 40, r5, m[5], to, 5 * toStride + c, 10 * toStride + c);
            MergeAvx2(even, 48, r6, m[6], to, 6 * toStride + c, 9 * toStride + c);
            MergeAvx2(even, 56, r7, m[7], to, 7 * toStride + c, 8 * toStride + c);
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MergeAvx2(ReadOnlySpan<float> even, int evenOffset, Vector256<float> odd, float multiplier, Span<float> to, int top, int bottom)
    {
        Vector256<float> e = LoadAvx2(even, evenOffset);
        Fma.MultiplyAdd(Vector256.Create(multiplier), odd, e).CopyTo(to.Slice(top, 8));
        Fma.MultiplyAdd(Vector256.Create(-multiplier), odd, e).CopyTo(to.Slice(bottom, 8));
    }

    /// <summary>
    /// The 16-point DctColumns per group of eight columns, reading <paramref name="from"/> (which may be
    /// <paramref name="to"/>) and storing the interleaved halves to the contiguous <paramref name="to"/>, optionally times
    /// <paramref name="scale"/> as Dct1D's separate pass does. The same operations as the scalar split, halves, combine
    /// and copies; false when unavailable.
    /// </summary>
    internal static bool DctColumns16Avx2(int count, ReadOnlySpan<float> from, int fromStride, Span<float> to, bool scaled, float scale, Span<float> scratch)
    {
        if (!CanTransformAvx2 || (count & 7) != 0) return false;
        Span<float> even = scratch[..64];
        ReadOnlySpan<float> m = JxlDctTables.Multipliers16;
        for (int c = 0; c < count; c += 8)
        {
            Vector256<float> r0 = SplitAvx2(from, c, 0, fromStride, m[0], even);
            Vector256<float> r1 = SplitAvx2(from, c, 1, fromStride, m[1], even);
            Vector256<float> r2 = SplitAvx2(from, c, 2, fromStride, m[2], even);
            Vector256<float> r3 = SplitAvx2(from, c, 3, fromStride, m[3], even);
            Vector256<float> r4 = SplitAvx2(from, c, 4, fromStride, m[4], even);
            Vector256<float> r5 = SplitAvx2(from, c, 5, fromStride, m[5], even);
            Vector256<float> r6 = SplitAvx2(from, c, 6, fromStride, m[6], even);
            Vector256<float> r7 = SplitAvx2(from, c, 7, fromStride, m[7], even);
            Dct8Avx2(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, 1.0f);
            r0 = Fma.MultiplyAdd(r0, Vector256.Create(Sqrt2), r1);
            r1 += r2; r2 += r3; r3 += r4; r4 += r5; r5 += r6; r6 += r7;
            StoreOddAvx2(to, count, c, scaled, scale, r0, r1, r2, r3, r4, r5, r6, r7);

            r0 = LoadAvx2(even, 0); r1 = LoadAvx2(even, 8); r2 = LoadAvx2(even, 16); r3 = LoadAvx2(even, 24);
            r4 = LoadAvx2(even, 32); r5 = LoadAvx2(even, 40); r6 = LoadAvx2(even, 48); r7 = LoadAvx2(even, 56);
            Dct8Avx2(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, 1.0f);
            if (scaled)
            {
                Vector256<float> s = Vector256.Create(scale);
                (r0, r1, r2, r3, r4, r5, r6, r7) = (s * r0, s * r1, s * r2, s * r3, s * r4, s * r5, s * r6, s * r7);
            }

            Store8Avx2(to, c, 2 * count, r0, r1, r2, r3, r4, r5, r6, r7);
        }

        return true;
    }

    // Row i of the even half to scratch, row i of the odd half returned (top - bottom times the multiplier).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> SplitAvx2(ReadOnlySpan<float> from, int c, int i, int fromStride, float multiplier, Span<float> even)
    {
        Vector256<float> top = LoadAvx2(from, i * fromStride + c), bottom = LoadAvx2(from, (15 - i) * fromStride + c);
        (top + bottom).CopyTo(even.Slice(8 * i, 8));
        return (top - bottom) * multiplier;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreOddAvx2(Span<float> to, int count, int c, bool scaled, float scale, Vector256<float> r0, Vector256<float> r1,
        Vector256<float> r2, Vector256<float> r3, Vector256<float> r4, Vector256<float> r5, Vector256<float> r6, Vector256<float> r7)
    {
        if (scaled)
        {
            Vector256<float> s = Vector256.Create(scale);
            (r0, r1, r2, r3, r4, r5, r6, r7) = (s * r0, s * r1, s * r2, s * r3, s * r4, s * r5, s * r6, s * r7);
        }

        Store8Avx2(to, count + c, 2 * count, r0, r1, r2, r3, r4, r5, r6, r7);
    }

    /// <summary>
    /// Transpose in 8×8 (AVX2) or 4×4 (SSE4.1) register tiles when both dimensions allow it; false leaves it to the
    /// scalar loop.
    /// </summary>
    internal static bool TransposeSimd(int rows, int columns, ReadOnlySpan<float> from, int fromStride, Span<float> to, int toStride)
    {
        if (((rows | columns) & 7) != 0 || !Avx2.IsSupported) return ((rows | columns) & 3) == 0 && Transpose4Sse41(rows, columns, from, fromStride, to, toStride);
        for (int r = 0; r < rows; r += 8)
        {
            for (int c = 0; c < columns; c += 8)
            {
                int offset = r * fromStride + c;
                Vector256<float> r0 = LoadAvx2(from, offset), r1 = LoadAvx2(from, offset + fromStride), r2 = LoadAvx2(from, offset + 2 * fromStride), r3 = LoadAvx2(from, offset + 3 * fromStride);
                Vector256<float> r4 = LoadAvx2(from, offset + 4 * fromStride), r5 = LoadAvx2(from, offset + 5 * fromStride), r6 = LoadAvx2(from, offset + 6 * fromStride);
                Vector256<float> r7 = LoadAvx2(from, offset + 7 * fromStride);
                Transpose8x8Avx(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
                Store8Avx2(to, c * toStride + r, toStride, r0, r1, r2, r3, r4, r5, r6, r7);
            }
        }

        return true;
    }

    private static bool Transpose4Sse41(int rows, int columns, ReadOnlySpan<float> from, int fromStride, Span<float> to, int toStride)
    {
        if (!Sse41.IsSupported) return false;
        for (int r = 0; r < rows; r += 4)
        {
            for (int c = 0; c < columns; c += 4)
            {
                int offset = r * fromStride + c;
                Vector128<float> r0 = Load128(from, offset), r1 = Load128(from, offset + fromStride);
                Vector128<float> r2 = Load128(from, offset + 2 * fromStride), r3 = Load128(from, offset + 3 * fromStride);
                Vector128<float> t0 = Sse.UnpackLow(r0, r1), t1 = Sse.UnpackLow(r2, r3), t2 = Sse.UnpackHigh(r0, r1), t3 = Sse.UnpackHigh(r2, r3);
                int target = c * toStride + r;
                Sse.MoveLowToHigh(t0, t1).CopyTo(to.Slice(target, 4));
                Sse.MoveHighToLow(t1, t0).CopyTo(to.Slice(target + toStride, 4));
                Sse.MoveLowToHigh(t2, t3).CopyTo(to.Slice(target + 2 * toStride, 4));
                Sse.MoveHighToLow(t3, t2).CopyTo(to.Slice(target + 3 * toStride, 4));
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Transpose8x8Avx(ref Vector256<float> r0, ref Vector256<float> r1, ref Vector256<float> r2, ref Vector256<float> r3,
        ref Vector256<float> r4, ref Vector256<float> r5, ref Vector256<float> r6, ref Vector256<float> r7)
    {
        Vector256<float> t0 = Avx.UnpackLow(r0, r1), t1 = Avx.UnpackHigh(r0, r1), t2 = Avx.UnpackLow(r2, r3), t3 = Avx.UnpackHigh(r2, r3);
        Vector256<float> t4 = Avx.UnpackLow(r4, r5), t5 = Avx.UnpackHigh(r4, r5), t6 = Avx.UnpackLow(r6, r7), t7 = Avx.UnpackHigh(r6, r7);
        Vector256<float> s0 = Avx.Shuffle(t0, t2, 0x44), s1 = Avx.Shuffle(t0, t2, 0xEE), s2 = Avx.Shuffle(t1, t3, 0x44), s3 = Avx.Shuffle(t1, t3, 0xEE);
        Vector256<float> s4 = Avx.Shuffle(t4, t6, 0x44), s5 = Avx.Shuffle(t4, t6, 0xEE), s6 = Avx.Shuffle(t5, t7, 0x44), s7 = Avx.Shuffle(t5, t7, 0xEE);
        r0 = Avx.Permute2x128(s0, s4, 0x20); r1 = Avx.Permute2x128(s1, s5, 0x20);
        r2 = Avx.Permute2x128(s2, s6, 0x20); r3 = Avx.Permute2x128(s3, s7, 0x20);
        r4 = Avx.Permute2x128(s0, s4, 0x31); r5 = Avx.Permute2x128(s1, s5, 0x31);
        r6 = Avx.Permute2x128(s2, s6, 0x31); r7 = Avx.Permute2x128(s3, s7, 0x31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store8Avx2(Span<float> to, int offset, int stride, Vector256<float> r0, Vector256<float> r1, Vector256<float> r2, Vector256<float> r3,
        Vector256<float> r4, Vector256<float> r5, Vector256<float> r6, Vector256<float> r7)
    {
        r0.CopyTo(to.Slice(offset, 8)); r1.CopyTo(to.Slice(offset + stride, 8));
        r2.CopyTo(to.Slice(offset + 2 * stride, 8)); r3.CopyTo(to.Slice(offset + 3 * stride, 8));
        r4.CopyTo(to.Slice(offset + 4 * stride, 8)); r5.CopyTo(to.Slice(offset + 5 * stride, 8));
        r6.CopyTo(to.Slice(offset + 6 * stride, 8)); r7.CopyTo(to.Slice(offset + 7 * stride, 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScaleRowAvx2(Span<float> row, float scale)
    {
        if (!Avx2.IsSupported) return 0;
        int end = row.Length & ~7;
        for (int c = 0; c < end; c += 8)
            (scale * LoadAvx2(row, c)).CopyTo(row.Slice(c, 8));
        return end;
    }

    // to = from * scale (IdctColumnsTo's first odd row, DctColumnsFrom's scaled stores); non-fused, so SSE4.1 too.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScaleRowToSimd(ReadOnlySpan<float> from, Span<float> to, float scale)
    {
        int c = 0;
        if (Avx2.IsSupported)
        {
            for (; c + 8 <= to.Length; c += 8)
                (LoadAvx2(from, c) * scale).CopyTo(to.Slice(c, 8));
        }
        else if (Sse41.IsSupported)
        {
            for (; c + 4 <= to.Length; c += 4)
                (Load128(from, c) * scale).CopyTo(to.Slice(c, 4));
        }
        return c;
    }

    // to = row + other (IdctColumnsTo's odd rows); non-fused.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AddRowsToSimd(ReadOnlySpan<float> row, ReadOnlySpan<float> other, Span<float> to)
    {
        int c = 0;
        if (Avx2.IsSupported)
        {
            for (; c + 8 <= to.Length; c += 8)
                (LoadAvx2(row, c) + LoadAvx2(other, c)).CopyTo(to.Slice(c, 8));
        }
        else if (Sse41.IsSupported)
        {
            for (; c + 4 <= to.Length; c += 4)
                (Load128(row, c) + Load128(other, c)).CopyTo(to.Slice(c, 4));
        }
        return c;
    }

    // DctColumnsFrom's odd rows: fma(row, sqrt2, next) for the first (AVX2 + FMA only), else row + next; times scale
    // when scaled.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int OddRowToSimd(bool first, ReadOnlySpan<float> row, ReadOnlySpan<float> next, Span<float> to, bool scaled, float scale)
    {
        int c = 0;
        if (first ? CanTransformAvx2 : Avx2.IsSupported)
        {
            for (; c + 8 <= to.Length; c += 8)
            {
                Vector256<float> value = first ? Fma.MultiplyAdd(LoadAvx2(row, c), Vector256.Create(Sqrt2), LoadAvx2(next, c))
                    : LoadAvx2(row, c) + LoadAvx2(next, c);
                (scaled ? scale * value : value).CopyTo(to.Slice(c, 8));
            }
        }
        else if (!first && Sse41.IsSupported)
        {
            for (; c + 4 <= to.Length; c += 4)
            {
                Vector128<float> value = Load128(row, c) + Load128(next, c);
                (scaled ? scale * value : value).CopyTo(to.Slice(c, 4));
            }
        }
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MergeRowsAvx2(ReadOnlySpan<float> even, ReadOnlySpan<float> odd,
        Span<float> top, Span<float> bottom, float multiplier)
    {
        if (!CanTransformAvx2) return 0;
        int end = top.Length & ~7;
        for (int c = 0; c < end; c += 8)
        {
            Vector256<float> e = LoadAvx2(even, c), o = LoadAvx2(odd, c);
            Fma.MultiplyAdd(Vector256.Create(multiplier), o, e).CopyTo(top.Slice(c, 8));
            Fma.MultiplyAdd(Vector256.Create(-multiplier), o, e).CopyTo(bottom.Slice(c, 8));
        }
        return end;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SplitRowsAvx2(ReadOnlySpan<float> top, ReadOnlySpan<float> bottom,
        Span<float> even, Span<float> odd, float multiplier)
    {
        if (!Avx2.IsSupported) return 0;
        int end = top.Length & ~7;
        for (int c = 0; c < end; c += 8)
        {
            Vector256<float> t = LoadAvx2(top, c), b = LoadAvx2(bottom, c);
            (t + b).CopyTo(even.Slice(c, 8));
            ((t - b) * multiplier).CopyTo(odd.Slice(c, 8));
        }
        return end;
    }

}
