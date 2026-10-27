namespace PictureSharp.Formats.Jxl.Codestream.Color;

/// <summary>3×3 row-major matrix helpers in double precision (libjxl v0.12.0 base/matrix_ops.h, BSD-3-Clause).</summary>
internal static class JxlMatrix3
{
    /// <summary>
    /// Writes the adjugate (transposed cofactors) of <paramref name="m"/> and returns the determinant; the inverse is
    /// the adjugate divided by it. Callers decide how close to singular a matrix may be.
    /// </summary>
    public static double Adjugate(ReadOnlySpan<double> m, Span<double> adjugate)
    {
        adjugate[0] = m[4] * m[8] - m[5] * m[7];
        adjugate[1] = m[2] * m[7] - m[1] * m[8];
        adjugate[2] = m[1] * m[5] - m[2] * m[4];
        adjugate[3] = m[5] * m[6] - m[3] * m[8];
        adjugate[4] = m[0] * m[8] - m[2] * m[6];
        adjugate[5] = m[2] * m[3] - m[0] * m[5];
        adjugate[6] = m[3] * m[7] - m[4] * m[6];
        adjugate[7] = m[1] * m[6] - m[0] * m[7];
        adjugate[8] = m[0] * m[4] - m[1] * m[3];
        return m[0] * adjugate[0] + m[1] * adjugate[3] + m[2] * adjugate[6];
    }
}
