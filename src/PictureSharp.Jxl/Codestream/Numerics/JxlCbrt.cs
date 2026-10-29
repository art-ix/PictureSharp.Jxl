using System.Numerics;
using System.Runtime.CompilerServices;

namespace PictureSharp.Formats.Jxl.Codestream.Numerics;

/// <summary>
/// The correctly rounded cube root of a float: a degree-7 polynomial in double for the mantissa, the exponent
/// divided by three through a table of 2^(k/3), and one Newton correction when the first result is too close to a
/// rounding boundary. Ported from CORE-MATH src/binary32/cbrt/cbrtf.c (Copyright (c) 2023, 2024 Alexei Sibidanov,
/// MIT; see THIRD-PARTY-NOTICES.txt). Unlike <see cref="MathF.Cbrt"/>, whose result depends on the platform's C
/// library, it gives the same result everywhere; checked exhaustively on all positive floats.
/// </summary>
internal static partial class JxlCbrt
{
    // 2^(k/3) for k = 0, 1, 2 (CORE-MATH: escale).
    private static ReadOnlySpan<double> EScale => [1.0, 1.2599210498948732, 1.5874010519681996];

    // The polynomial for the cube root of the mantissa z in [1, 2) (CORE-MATH: c).
    private static ReadOnlySpan<double> Coefficients =>
    [
        0.5685564078059381, 0.7024960185339382, -0.39381000363475277, 0.21397507019181075,
        -0.08593966563932363, 0.023134567971640832, -0.003702862366439682, 0.00026571366637555694,
    ];

    /// <summary>The cube root of <paramref name="x"/>, correctly rounded to nearest.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Cbrt(float x)
    {
        uint u = BitConverter.SingleToUInt32Bits(x);
        uint au = u << 1;
        uint sgn = u >> 31;
        uint e = au >> 24;
        if (au < 1u << 24 || au >= 0xFFu << 24)
        {
            if (au >= 0xFFu << 24) return x + x; // inf, NaN
            if (au == 0) return x; // ±0
            // Subnormal: normalise the mantissa and lower the exponent.
            int nz = BitOperations.LeadingZeroCount(au) - 7;
            au <<= nz;
            e -= (uint)(nz - 1);
        }

        uint mant = au & 0xFFFFFF;
        e += 899;
        uint et = e / 3, it = e % 3;
        ulong isc = BitConverter.DoubleToUInt64Bits(EScale[(int)it]);
        isc += (ulong)(et - 342) << 52;
        isc |= (ulong)sgn << 63;
        double cvt2 = BitConverter.UInt64BitsToDouble(isc);
        ReadOnlySpan<double> c = Coefficients;
        double z = BitConverter.UInt64BitsToDouble((ulong)mant << 28 | 0x3FFUL << 52);
        double r0 = -0.024975246527242426 / z;
        double z2 = z * z;
        double z4 = z2 * z2;
        double f = ((c[0] + z * c[1]) + z2 * (c[2] + z * c[3])) + z4 * ((c[4] + z * c[5]) + z2 * (c[6] + z * c[7])) + r0;
        double r = f * cvt2;
        float ub = (float)r;
        float lb = (float)(r - cvt2 * 1.4182e-9);
        // The polynomial is accurate enough unless the result lies close to the midpoint of two floats.
        if (ub == lb) return ub;
        const double u0 = -13.34654827009379;
        double h = f * f * f - z;
        f -= (f * r0 * u0) * h;
        r = f * cvt2;
        ulong cvt1 = BitConverter.DoubleToUInt64Bits(r);
        ub = (float)r;
        long m0 = (long)(cvt1 << 19);
        long m1 = m0 >> 63;
        // Exact cases: the double result is (nearly) a float; round it to that float.
        if ((m0 ^ m1) < (1L << 31))
        {
            cvt1 = (cvt1 + (1UL << 31)) & 0xFFFFFFFF00000000UL;
            ub = (float)BitConverter.UInt64BitsToDouble(cvt1);
        }

        return ub;
    }
}
