using PictureSharp.Formats.Jxl.Codestream.IO;

namespace PictureSharp.Formats.Jxl.Codestream.Entropy;

/// <summary>
/// Permutations coded as Lehmer codes: the TOC order and coefficient orders. Ported from libjxl v0.12.0
/// lib/jxl/coeff_order.cc (ReadPermutation) and lib/jxl/lehmer_code.h (BSD-3-Clause).
/// </summary>
internal static class JxlPermutation
{
    public const int Contexts = 8;

    /// <summary>
    /// Reads a permutation of <paramref name="size"/> elements whose first <paramref name="skip"/> stay in place, with
    /// symbols of the given (unclustered) context map.
    /// </summary>
    public static int[] Read(ref JxlBitReader reader, JxlSymbolReader symbols, byte[] contextMap, int skip, int size)
    {
        uint[] lehmer = new uint[size];
        uint end = symbols.ReadClustered(contextMap[Context((uint)size)], ref reader) + (uint)skip;
        if (end > size)
        {
            throw JxlDescriptor.Corrupt("Invalid permutation size.");
        }

        uint last = 0;
        for (int i = skip; i < end; i++)
        {
            lehmer[i] = symbols.ReadClustered(contextMap[Context(last)], ref reader);
            last = lehmer[i];
            if (lehmer[i] >= size - i)
            {
                throw JxlDescriptor.Corrupt("Invalid Lehmer code.");
            }
        }

        return DecodeLehmerCode(lehmer);
    }

    // The token of HybridUintConfig(0, 0, 0), capped: 0 for 0, else 1 + floor(log2(value)).
    private static int Context(uint value) => Math.Min(value == 0 ? 0 : JxlPrefixCode.Log2Floor(value) + 1, Contexts - 1);

    /// <summary>Lehmer code to permutation with an implicit order-statistics tree.</summary>
    private static int[] DecodeLehmerCode(uint[] code)
    {
        int n = code.Length;
        int log2 = n <= 1 ? 0 : JxlPrefixCode.Log2Floor((uint)n - 1) + 1;
        int padded = 1 << log2;
        uint[] tree = new uint[padded];
        for (int i = 0; i < padded; i++)
        {
            tree[i] = (uint)((i + 1) & -(i + 1));
        }

        int[] permutation = new int[n];
        for (int i = 0; i < n; i++)
        {
            uint rank = code[i] + 1;
            int bit = padded;
            int next = 0;
            for (int b = 0; b <= log2; b++)
            {
                int candidate = next + bit;
                bit >>= 1;
                if (candidate <= padded && tree[candidate - 1] < rank)
                {
                    next = candidate;
                    rank -= tree[candidate - 1];
                }
            }

            permutation[i] = next;
            for (next++; next <= padded; next += next & -next)
            {
                tree[next - 1]--;
            }
        }

        return permutation;
    }
}
