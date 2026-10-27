using System.Numerics;

namespace PictureSharp.Formats.Jxl.Codestream.Numerics;

/// <summary>Integer helpers shared by every codec module (libjxl v0.12.0 base/bits.h and pack_signed.h, BSD-3-Clause).</summary>
internal static class JxlIntMath
{
    /// <summary>⌈value / divisor⌉ for a non-negative value and a positive divisor.</summary>
    public static int DivCeil(int value, int divisor) => (value + divisor - 1) / divisor;

    /// <inheritdoc cref="DivCeil(int, int)"/>
    public static long DivCeil(long value, long divisor) => (value + divisor - 1) / divisor;

    /// <summary>⌊log2(value)⌋; -1 for 0.</summary>
    public static int Log2Floor(uint value) => 31 - BitOperations.LeadingZeroCount(value);

    /// <summary>⌈log2(value)⌉; 0 for values up to 1.</summary>
    public static int CeilLog2(long value) => value <= 1 ? 0 : 64 - BitOperations.LeadingZeroCount((ulong)(value - 1));

    /// <summary>A signed value folded to unsigned: 0, -1, 1, -2, … become 0, 1, 2, 3, ….</summary>
    public static uint PackSigned(long value) => unchecked((uint)(value >= 0 ? 2 * (ulong)value : 2 * (ulong)(-value) - 1));

    /// <summary>The inverse of <see cref="PackSigned"/>.</summary>
    public static int UnpackSigned(uint value) => unchecked((int)((value >> 1) ^ ((~value & 1) - 1)));
}
