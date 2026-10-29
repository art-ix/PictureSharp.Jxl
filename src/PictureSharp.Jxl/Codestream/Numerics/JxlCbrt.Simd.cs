using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Formats.Jxl.Codestream.Numerics;

// The first (polynomial) stage of Cbrt on four lanes in double: the same integer steps and unfused double expression
// tree. Lanes the scalar code would not finish there (zero, subnormal, inf/NaN, negative or close to a rounding
// boundary) are reported for the scalar Cbrt instead.
internal static partial class JxlCbrt
{
    /// <summary>
    /// Cube roots of four floats; bit i of <paramref name="scalarLanes"/> marks a lane whose result is not final and
    /// must come from <see cref="Cbrt"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<float> CbrtAvx2(Vector128<float> x, out int scalarLanes)
    {
        Vector128<uint> u = x.AsUInt32();
        Vector128<uint> exponent = (u << 1) >>> 24;
        // Only positive normal inputs: zero/subnormal (exponent 0), inf/NaN (255) and the sign take the scalar path.
        Vector128<uint> special = Vector128.Equals(exponent, Vector128<uint>.Zero) | Vector128.Equals(exponent, Vector128.Create(255u))
            | Vector128.GreaterThan(u, Vector128.Create(0x7FFFFFFFu));
        Vector256<ulong> au = Avx2.ConvertToVector256Int64(u << 1).AsUInt64();
        Vector256<ulong> e = (au >>> 24) + Vector256.Create(899UL);
        // e / 3 for e < 2^16: e * 43691 >> 17.
        Vector256<ulong> et = Avx2.Multiply(e.AsUInt32(), Vector256.Create(43691UL).AsUInt32()) >>> 17;
        Vector256<ulong> it = e - ((et << 1) + et);
        ReadOnlySpan<double> scales = EScale;
        Vector256<double> escale = Avx.BlendVariable(Vector256.Create(scales[0]), Vector256.Create(scales[1]), Vector256.Equals(it, Vector256<ulong>.One).AsDouble());
        escale = Avx.BlendVariable(escale, Vector256.Create(scales[2]), Vector256.Equals(it, Vector256.Create(2UL)).AsDouble());
        Vector256<double> cvt2 = (escale.AsUInt64() + ((et - Vector256.Create(342UL)) << 52)).AsDouble();
        ReadOnlySpan<double> c = Coefficients;
        Vector256<double> z = (((au & Vector256.Create(0xFFFFFFUL)) << 28) | Vector256.Create(0x3FFUL << 52)).AsDouble();
        Vector256<double> r0 = Vector256.Create(-0.024975246527242426) / z;
        Vector256<double> z2 = z * z;
        Vector256<double> z4 = z2 * z2;
        Vector256<double> f = ((Vector256.Create(c[0]) + z * Vector256.Create(c[1])) + z2 * (Vector256.Create(c[2]) + z * Vector256.Create(c[3])))
            + z4 * ((Vector256.Create(c[4]) + z * Vector256.Create(c[5])) + z2 * (Vector256.Create(c[6]) + z * Vector256.Create(c[7]))) + r0;
        Vector256<double> r = f * cvt2;
        Vector128<float> ub = Avx.ConvertToVector128Single(r);
        Vector128<float> lb = Avx.ConvertToVector128Single(r - cvt2 * Vector256.Create(1.4182e-9));
        scalarLanes = Sse.MoveMask((special.AsSingle() | ~Vector128.Equals(ub, lb)));
        return ub;
    }
}
