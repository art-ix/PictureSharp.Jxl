using PictureSharp.Formats.Jxl.Codestream.IO;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Entropy;

/// <summary>
/// The hybrid integer split: tokens below <see cref="SplitToken"/> are values; larger ones carry the exponent and the
/// top <see cref="MsbInToken"/> and bottom <see cref="LsbInToken"/> mantissa bits, the rest follow as raw bits.
/// Ported from libjxl v0.12.0 lib/jxl/dec_ans.h and ans_params.h (HybridUintConfig, BSD-3-Clause).
/// </summary>
internal readonly record struct JxlHybridUint(int SplitExponent, int MsbInToken, int LsbInToken)
{
    /// <summary>libjxl's default configuration, used for MA tree residual estimates and histogram clustering.</summary>
    public static JxlHybridUint Default => new(4, 2, 0);

    public uint SplitToken => 1u << SplitExponent;

    public uint Decode(uint token, ref JxlBitReader reader)
    {
        if (token < SplitToken)
        {
            return token;
        }

        int inToken = MsbInToken + LsbInToken;
        // libjxl masks the bit count instead of failing; such values only occur in invalid streams.
        int bits = (int)((uint)(SplitExponent - inToken) + ((token - SplitToken) >> inToken)) & 31;
        uint low = token & ((1u << LsbInToken) - 1);
        token >>= LsbInToken;
        ulong raw = reader.ReadBits(bits);
        ulong value = ((((1UL << MsbInToken) | (token & ((1u << MsbInToken) - 1))) << bits) | raw) << LsbInToken | low;
        return (uint)value;
    }

    /// <summary>
    /// <see cref="Decode(uint, ref JxlBitReader)"/> of a token at or above <see cref="SplitToken"/> from a buffer of
    /// <see cref="JxlBitReader.ReadRow"/> holding at least 31 bits.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public uint Decode(uint token, ref ulong bits, ref int buffered)
    {
        int inToken = MsbInToken + LsbInToken;
        int count = (int)((uint)(SplitExponent - inToken) + ((token - SplitToken) >> inToken)) & 31;
        uint low = token & ((1u << LsbInToken) - 1);
        token >>= LsbInToken;
        ulong raw = JxlBitReader.Take(ref bits, ref buffered, count);
        ulong value = ((((1UL << MsbInToken) | (token & ((1u << MsbInToken) - 1))) << count) | raw) << LsbInToken | low;
        return (uint)value;
    }

    /// <summary>The token of <paramref name="value"/> and its raw bits (HybridUintConfig::Encode).</summary>
    public uint Encode(uint value, out int bitCount, out uint bits)
    {
        if (value < SplitToken)
        {
            bitCount = 0;
            bits = 0;
            return value;
        }

        int n = Log2Floor(value);
        uint m = value - (1u << n);
        int msb = MsbInToken;
        int lsb = LsbInToken;
        uint token = SplitToken + (uint)((n - SplitExponent) << (msb + lsb)) + ((m >> (n - msb)) << lsb) + (m & ((1u << lsb) - 1));
        bitCount = n - msb - lsb;
        bits = (value >> lsb) & (uint)((1UL << bitCount) - 1);
        return token;
    }

    /// <summary>Reads a configuration for an alphabet of <c>1 &lt;&lt; logAlphaSize</c> tokens.</summary>
    public static JxlHybridUint Read(ref JxlBitReader reader, int logAlphaSize)
    {
        int splitExponent = (int)reader.ReadBits(SplitExponentBits(logAlphaSize));
        int msb = 0;
        int lsb = 0;
        if (splitExponent != logAlphaSize)
        {
            msb = (int)reader.ReadBits(MsbBits(splitExponent));
            if (msb > splitExponent)
            {
                throw JxlDescriptor.Corrupt("Invalid hybrid integer configuration.");
            }

            lsb = (int)reader.ReadBits(LsbBits(splitExponent, msb));
        }

        return lsb + msb <= splitExponent
            ? new JxlHybridUint(splitExponent, msb, lsb)
            : throw JxlDescriptor.Corrupt("Invalid hybrid integer configuration.");
    }

    // Field widths of the coded configuration; the MSB and LSB fields are absent when the split covers the alphabet.
    public static int SplitExponentBits(int logAlphaSize) => CeilLog2(logAlphaSize + 1);

    public static int MsbBits(int splitExponent) => CeilLog2(splitExponent + 1);

    public static int LsbBits(int splitExponent, int msbInToken) => CeilLog2(splitExponent - msbInToken + 1);
}
