using System.Numerics;
using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.VarDct;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding.Lossy;

/// <summary>
/// The coefficient orders of a frame: per order bucket the natural order, or the positions sorted by how often their
/// coefficients are zero (in a sample of the varblocks), coded as Lehmer codes. Ported from libjxl v0.12.0
/// lib/jxl/enc_coeff_order.cc and lehmer_code.h (BSD-3-Clause).
/// </summary>
internal static class JxlCoefficientOrders
{
    /// <summary>
    /// The orders and the buckets whose order is coded (the rest use the natural order). The orders cover the buckets
    /// up to the largest one the frame uses, at the offsets <see cref="JxlAcStrategy.OrderOffset"/> gives.
    /// </summary>
    public static (int[] Orders, uint CodedBuckets) Compute(JxlLossyFrame frame)
    {
        (uint used, uint customized) = UsedOrders(frame);
        uint present = used;
        foreach (JxlVarblock varblock in frame.Strategies) present |= 1u << varblock.Type.OrderBucket;
        // The orders of all buckets take 394K coefficients (most of them for DCT128 and DCT256): only the buckets up
        // to the largest used one are kept.
        int[] orders = new int[JxlAcStrategy.OrderOffset(31 - BitOperations.LeadingZeroCount(present) + 1, 0)];
        if (frame.Jpeg) customized = 0;
        long[] zeros = customized != 0 ? new long[orders.Length] : [];
        if (customized != 0)
        {
            CountZeros(frame, zeros, frame.Settings.SpeedTier >= JxlLossySettings.Squirrel && customized == 1 ? 0.5 : 1.0);
        }

        uint computed = 0;
        for (int s = 0; s < JxlAcStrategy.Count; s++)
        {
            var type = (JxlAcStrategyType)s;
            int bucket = type.OrderBucket;
            if ((computed & (1u << bucket)) != 0) continue;
            computed |= 1u << bucket;
            if ((used & (1u << bucket)) == 0) continue;
            int[] natural = JxlAcStrategy.NaturalOrder(type);
            int size = natural.Length;
            if ((customized & (1u << bucket)) == 0)
            {
                for (int c = 0; c < 3; c++) natural.CopyTo(orders, JxlAcStrategy.OrderOffset(bucket, c));
                continue;
            }

            bool nondefault = false;
            float inverseSqrtSize = 1.0f / MathF.Sqrt(size);
            var keys = new ulong[size];
            for (int c = 0; c < 3; c++)
            {
                int offset = JxlAcStrategy.OrderOffset(bucket, c);
                for (int i = 0; i < size; i++)
                {
                    // The lowest frequencies count -1, so they stay first.
                    ulong count = (ulong)Math.Max(0.0f, zeros[offset + natural[i]] * inverseSqrtSize + 0.1f);
                    keys[i] = (count << 16) | (uint)i;
                }

                Array.Sort(keys);
                for (int i = 0; i < size; i++)
                {
                    int position = natural[(int)(keys[i] & 0xFFFF)];
                    orders[offset + i] = position;
                    nondefault |= position != natural[i];
                }
            }

            if (!nondefault) customized &= ~(1u << bucket);
        }

        return (orders, customized);
    }

    /// <summary>Writes the coded buckets and their orders (EncodeCoeffOrders) as one entropy-coded stream.</summary>
    public static void Write(JxlBitWriter writer, int[] orders, uint codedBuckets)
    {
        JxlFieldWriter.U32(writer, codedBuckets, IO.JxlU32.Val(0x5F), IO.JxlU32.Val(0x13), IO.JxlU32.Val(0), IO.JxlU32.Bits32(JxlAcStrategy.Orders));
        if (codedBuckets == 0) return;
        var tokens = new JxlTokens();
        uint computed = 0;
        for (int s = 0; s < JxlAcStrategy.Count; s++)
        {
            var type = (JxlAcStrategyType)s;
            int bucket = type.OrderBucket;
            if ((computed & (1u << bucket)) != 0) continue;
            computed |= 1u << bucket;
            if ((codedBuckets & (1u << bucket)) == 0) continue;
            int[] natural = JxlAcStrategy.NaturalOrder(type);
            int[] lut = new int[natural.Length];
            for (int i = 0; i < natural.Length; i++) lut[natural[i]] = i;
            int lowest = type.BlocksX * type.BlocksY;
            int[] permutation = new int[natural.Length];
            for (int c = 0; c < 3; c++)
            {
                int offset = JxlAcStrategy.OrderOffset(bucket, c);
                for (int i = 0; i < permutation.Length; i++) permutation[i] = lut[orders[offset + i]];
                TokenizePermutation(permutation, lowest, tokens);
            }
        }

        JxlEntropyEncoder.WriteStandalone(writer, JxlPermutation.Contexts, tokens);
    }

    // ComputeUsedOrders: every bucket in use, and those of the smaller transforms whose order may be customized.
    private static (uint Used, uint Customized) UsedOrders(JxlLossyFrame frame)
    {
        if (!frame.Settings.CustomOrders) return (1, 1);
        uint used = 0;
        uint customized = 0;
        foreach (JxlVarblock varblock in frame.Strategies)
        {
            int bucket = varblock.Type.OrderBucket;
            used |= 1u << bucket;
            if (bucket <= 6) customized |= 1u << bucket;
        }

        return frame.XBlocks < 5 && frame.YBlocks < 5 ? (used, 0) : (used, customized);
    }

    // Counts the zero coefficients of a fixed pseudo-random sample of the varblocks.
    private static void CountZeros(JxlLossyFrame frame, long[] zeros, double fraction)
    {
        ulong threshold = (ulong)((ulong.MaxValue >> 32) * fraction);
        ulong s0 = 0x94D049BB133111EB;
        ulong s1 = 0xBF58476D1CE4E5B9;
        for (int group = 0; group < frame.Coefficients.Length; group++)
        {
            (int x0, int y0, int width, int height) = frame.GroupRect(group);
            int[] coefficients = frame.Coefficients[group];
            int area = width * height * JxlAcStrategy.BlockSize;
            int offset = 0;
            for (int by = y0; by < y0 + height; by++)
            {
                for (int bx = x0; bx < x0 + width; bx++)
                {
                    JxlVarblock varblock = frame.Strategies[by * frame.XBlocks + bx];
                    if (!varblock.IsFirst) continue;
                    JxlAcStrategyType type = varblock.Type;
                    int size = JxlAcStrategy.BlockSize << type.Log2Blocks;
                    // xorshift128+, as libjxl samples.
                    ulong a = s0;
                    ulong b = s1;
                    ulong bits = a + b;
                    s0 = b;
                    a ^= a << 23;
                    a ^= b ^ (a >> 18) ^ (b >> 5);
                    s1 = a;
                    if ((bits >> 32) <= threshold)
                    {
                        int cx = Math.Max(type.BlocksX, type.BlocksY);
                        int cy = Math.Min(type.BlocksX, type.BlocksY);
                        for (int c = 0; c < 3; c++)
                        {
                            int orderOffset = JxlAcStrategy.OrderOffset(type.OrderBucket, c);
                            ReadOnlySpan<int> block = coefficients.AsSpan(c * area + offset, size);
                            for (int k = 0; k < size; k++)
                            {
                                if (block[k] == 0) zeros[orderOffset + k]++;
                            }

                            for (int iy = 0; iy < cy; iy++)
                            {
                                for (int ix = 0; ix < cx; ix++) zeros[orderOffset + iy * 8 * cx + ix] = -1;
                            }
                        }
                    }

                    offset += size;
                }
            }
        }
    }

    // TokenizePermutation: the length of the coded part after the first skip entries, then its Lehmer code.
    private static void TokenizePermutation(int[] permutation, int skip, JxlTokens tokens)
    {
        int size = permutation.Length;
        uint[] lehmer = LehmerCode(permutation);
        int end = size;
        while (end > skip && lehmer[end - 1] == 0) end--;
        tokens.Add(Context((uint)size), (uint)(end - skip));
        uint last = 0;
        for (int i = skip; i < end; i++)
        {
            tokens.Add(Context(last), lehmer[i]);
            last = lehmer[i];
        }
    }

    // The rank of every element among the elements not used before it.
    private static uint[] LehmerCode(int[] permutation)
    {
        int n = permutation.Length;
        int[] tree = new int[n + 1];
        uint[] code = new uint[n];
        for (int i = 0; i < n; i++)
        {
            int value = permutation[i];
            int smallerUsed = 0;
            for (int j = value; j > 0; j -= j & -j) smallerUsed += tree[j];
            code[i] = (uint)(value - smallerUsed);
            for (int j = value + 1; j <= n; j += j & -j) tree[j]++;
        }

        return code;
    }

    private static int Context(uint value) => Math.Min(value == 0 ? 0 : Log2Floor(value) + 1, JxlPermutation.Contexts - 1);
}
