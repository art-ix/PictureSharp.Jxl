using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.VarDct;

// VarDCT transform types, coefficient orders and AC contexts. Ported from libjxl v0.12.0 lib/jxl/ac_strategy.h,
// ac_strategy.cc, coeff_order.h, coeff_order.cc, coeff_order_fwd.h, ac_context.h and entropy_coder.cc
// (BSD-3-Clause).

/// <summary>The transform of a varblock (AcStrategyType); a DCT named RxC covers R rows and C columns of pixels.</summary>
internal enum JxlAcStrategyType : byte
{
    Dct = 0,
    Identity = 1,
    Dct2X2 = 2,
    Dct4X4 = 3,
    Dct16X16 = 4,
    Dct32X32 = 5,
    Dct16X8 = 6,
    Dct8X16 = 7,
    Dct32X8 = 8,
    Dct8X32 = 9,
    Dct32X16 = 10,
    Dct16X32 = 11,
    Dct4X8 = 12,
    Dct8X4 = 13,
    Afv0 = 14,
    Afv1 = 15,
    Afv2 = 16,
    Afv3 = 17,
    Dct64X64 = 18,
    Dct64X32 = 19,
    Dct32X64 = 20,
    Dct128X128 = 21,
    Dct128X64 = 22,
    Dct64X128 = 23,
    Dct256X256 = 24,
    Dct256X128 = 25,
    Dct128X256 = 26,
}

/// <summary>A set of AC strategies, such as those a frame uses.</summary>
internal readonly record struct JxlAcStrategySet(uint Mask)
{
    public bool Contains(JxlAcStrategyType type) => (Mask & (1u << (int)type)) != 0;

    public JxlAcStrategySet With(JxlAcStrategyType type) => new(Mask | (1u << (int)type));

    public JxlAcStrategySet Union(JxlAcStrategySet other) => new(Mask | other.Mask);

    /// <summary>The coefficients of the largest varblock in the set.</summary>
    public int MaxCoefficients
    {
        get
        {
            int max = 0;
            for (int s = 0; s < JxlAcStrategy.Count; s++)
            {
                if (Contains((JxlAcStrategyType)s)) max = Math.Max(max, JxlAcStrategy.BlockSize << ((JxlAcStrategyType)s).Log2Blocks);
            }

            return max;
        }
    }
}

/// <summary>A block of a VarDCT frame: the strategy of its varblock and whether it is the varblock's first block.</summary>
internal readonly record struct JxlVarblock(byte Raw)
{
    /// <summary>A block that no varblock covers yet.</summary>
    public static JxlVarblock Unassigned => new(0xFF);

    public static JxlVarblock Create(JxlAcStrategyType type, bool first) => new((byte)(((int)type << 1) | (first ? 1 : 0)));

    public bool IsAssigned => Raw != Unassigned.Raw;

    /// <summary>Whether this is the top-left block, which carries the varblock's coefficients.</summary>
    public bool IsFirst => (Raw & 1) != 0;

    public JxlAcStrategyType Type => (JxlAcStrategyType)(Raw >> 1);
}

internal static class JxlAcStrategy
{
    public const int Count = 27;

    /// <summary>Order buckets: one natural order per size class, shared by RxC and CxR.</summary>
    public const int Orders = 13;

    public const int BlockSize = 64;

    private static ReadOnlySpan<byte> CoveredX => [1, 1, 1, 1, 2, 4, 1, 2, 1, 4, 2, 4, 1, 1, 1, 1, 1, 1, 8, 4, 8, 16, 8, 16, 32, 16, 32];

    private static ReadOnlySpan<byte> CoveredY => [1, 1, 1, 1, 2, 4, 2, 1, 4, 1, 4, 2, 1, 1, 1, 1, 1, 1, 8, 8, 4, 16, 16, 8, 32, 32, 16];

    private static ReadOnlySpan<byte> Log2Covered => [0, 0, 0, 0, 2, 4, 1, 1, 2, 2, 3, 3, 0, 0, 0, 0, 0, 0, 6, 5, 5, 8, 7, 7, 10, 9, 9];

    private static ReadOnlySpan<byte> StrategyOrder => [0, 1, 1, 1, 2, 3, 4, 4, 5, 5, 6, 6, 1, 1, 1, 1, 1, 1, 7, 8, 8, 9, 10, 10, 11, 12, 12];

    // Offsets of the orders of each bucket and channel, in units of 64 coefficients.
    private static ReadOnlySpan<short> OrderOffsets =>
    [
        0, 1, 2, 3, 4, 5, 6, 10, 14, 18, 34, 50, 66, 68, 70, 72, 76, 80, 84, 92, 100, 108, 172, 236, 300, 332, 364, 396, 652,
        908, 1164, 1292, 1420, 1548, 2572, 3596, 4620, 5132, 5644, 6156,
    ];

    extension(JxlAcStrategyType type)
    {
        /// <summary>The 8×8 blocks the varblock covers horizontally.</summary>
        public int BlocksX => CoveredX[(int)type];

        public int BlocksY => CoveredY[(int)type];

        /// <summary>log2 of the number of covered blocks.</summary>
        public int Log2Blocks => Log2Covered[(int)type];

        /// <summary>The coefficient order bucket, shared by RxC and CxR.</summary>
        public int OrderBucket => StrategyOrder[(int)type];
    }

    /// <summary>The first coefficient of the order of bucket <paramref name="order"/> and channel <paramref name="c"/>.</summary>
    public static int OrderOffset(int order, int c) => OrderOffsets[3 * order + c] * BlockSize;

    /// <summary>The size of one set of orders (all three channels) covering the strategies in <paramref name="usedStrategies"/>.</summary>
    public static int OrderSetSize(JxlAcStrategySet usedStrategies)
    {
        int size = 0;
        for (int s = 0; s < Count; s++)
        {
            if (usedStrategies.Contains((JxlAcStrategyType)s))
            {
                size = Math.Max(size, OrderOffsets[3 * (StrategyOrder[s] + 1)] * BlockSize);
            }
        }

        return size;
    }

    /// <summary>
    /// The natural coefficient order: a zig-zag generalised to rectangles, after the lowest frequencies (one per
    /// covered block). Coefficients are laid out with the smaller dimension as rows (CoeffOrderAndLut).
    /// </summary>
    public static int[] NaturalOrder(JxlAcStrategyType type)
    {
        int cx = Math.Max(type.BlocksX, type.BlocksY);
        int cy = Math.Min(type.BlocksX, type.BlocksY);
        int[] order = new int[cx * cy * BlockSize];
        int xs = cx / cy;
        int xsm = xs - 1;
        int xss = CeilLog2(xs);
        int dim = cx * JxlFrameHeader.BlockDim;
        int cur = cx * cy;
        for (int i = 0; i < dim; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                (int x, int y) = i % 2 != 0 ? (i - j, j) : (j, i - j);
                if ((y & xsm) != 0)
                {
                    continue;
                }

                y >>= xss;
                int value = x < cx && y < cy ? y * cx + x : cur++;
                order[value] = y * dim + x;
            }
        }

        for (int ip = dim - 1; ip > 0; ip--)
        {
            int i = ip - 1;
            for (int j = 0; j <= i; j++)
            {
                (int x, int y) = (dim - 1 - (i - j), dim - 1 - j);
                if (i % 2 != 0)
                {
                    (x, y) = (y, x);
                }

                if ((y & xsm) != 0)
                {
                    continue;
                }

                y >>= xss;
                order[cur++] = y * dim + x;
            }
        }

        return order;
    }

    /// <summary>
    /// Reads the coefficient orders of one pass (DecodeCoeffOrders): the natural order, or a permutation of it after
    /// the lowest frequencies for each bucket in <paramref name="usedOrders"/>.
    /// </summary>
    public static int[] ReadOrders(ref JxlBitReader reader, uint usedOrders, JxlAcStrategySet usedStrategies)
    {
        int[] orders = new int[OrderSetSize(usedStrategies)];
        JxlSymbolReader? symbols = null;
        JxlEntropyCode? code = null;
        if (usedOrders != 0)
        {
            code = JxlEntropyCode.Read(ref reader, JxlPermutation.Contexts);
            symbols = new JxlSymbolReader(code, ref reader);
        }

        try
        {
            uint bucketMask = 0;
            for (int s = 0; s < Count; s++)
            {
                if (usedStrategies.Contains((JxlAcStrategyType)s))
                {
                    bucketMask |= 1u << StrategyOrder[s];
                }
            }

            uint computed = 0;
            for (int s = 0; s < Count; s++)
            {
                int bucket = StrategyOrder[s];
                if ((computed & (1u << bucket)) != 0)
                {
                    continue;
                }

                computed |= 1u << bucket;
                var type = (JxlAcStrategyType)s;
                bool used = (bucketMask & (1u << bucket)) != 0;
                bool coded = (usedOrders & (1u << bucket)) != 0;
                if (!used && !coded)
                {
                    continue;
                }

                int[] natural = NaturalOrder(type);
                int lowest = type.BlocksX * type.BlocksY;
                for (int c = 0; c < 3; c++)
                {
                    if (!coded)
                    {
                        natural.CopyTo(orders, OrderOffset(bucket, c));
                        continue;
                    }

                    int[] permutation = JxlPermutation.Read(ref reader, symbols!, code!.ContextMap, lowest, natural.Length);
                    if (used)
                    {
                        Span<int> target = orders.AsSpan(OrderOffset(bucket, c), natural.Length);
                        for (int k = 0; k < target.Length; k++)
                        {
                            target[k] = natural[permutation[k]];
                        }
                    }
                }
            }

            if (symbols is not null && !symbols.IsFinalStateValid)
            {
                throw JxlDescriptor.Corrupt("Invalid ANS stream of coefficient orders.");
            }
        }
        finally
        {
            symbols?.Dispose();
        }

        return orders;
    }
}

/// <summary>
/// The block context map: clusters (channel, order bucket, quantisation field bucket, DC bucket) into the block
/// contexts that select the AC histograms (BlockCtxMap).
/// </summary>
internal sealed class JxlBlockContextMap
{
    /// <summary>Contexts for the number of non-zeros, per block context.</summary>
    public const int NonZeroBuckets = 37;

    /// <summary>Coefficient contexts per block context (ZeroDensityContext of nonzeros + k below 64).</summary>
    public const int ZeroDensityContextCount = 458;

    private static readonly byte[] DefaultMap =
    [
        0, 1, 2, 2, 3, 3, 4, 5, 6, 6, 6, 6, 6,
        7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14,
        7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14,
    ];

    private static ReadOnlySpan<ushort> CoeffFreqContext =>
    [
        0xBAD, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 15, 16, 16, 17, 17, 18, 18, 19, 19, 20, 20, 21, 21, 22, 22,
        23, 23, 23, 23, 24, 24, 24, 24, 25, 25, 25, 25, 26, 26, 26, 26, 27, 27, 27, 27, 28, 28, 28, 28, 29, 29, 29, 29, 30, 30,
        30, 30,
    ];

    private static ReadOnlySpan<ushort> CoeffNumNonzeroContext =>
    [
        0xBAD, 0, 31, 62, 62, 93, 93, 93, 93, 123, 123, 123, 123, 152, 152, 152, 152, 152, 152, 152, 152, 180, 180, 180, 180,
        180, 180, 180, 180, 180, 180, 180, 180, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
        206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
    ];

    private JxlBlockContextMap(int[][] dcThresholds, uint[] qfThresholds, byte[] map)
    {
        DcThresholds = dcThresholds;
        QfThresholds = qfThresholds;
        Map = map;
        Contexts = map.Max() + 1;
        DcContexts = dcThresholds.Aggregate(1, (product, thresholds) => product * (thresholds.Length + 1));
    }

    public static JxlBlockContextMap Default { get; } = new([[], [], []], [], DefaultMap);

    /// <summary>Thresholds of the quantised X, Y and B DC values.</summary>
    private int[][] DcThresholds { get; }

    private uint[] QfThresholds { get; }

    private byte[] Map { get; }

    public int Contexts { get; }

    public int DcContexts { get; }

    public int AcContexts => Contexts * (NonZeroBuckets + ZeroDensityContextCount);

    public static JxlBlockContextMap Read(ref JxlBitReader reader)
    {
        if (reader.ReadBool())
        {
            return Default;
        }

        int[][] dc = new int[3][];
        int dcContexts = 1;
        for (int c = 0; c < 3; c++)
        {
            dc[c] = new int[reader.ReadBits(4)];
            dcContexts *= dc[c].Length + 1;
            for (int i = 0; i < dc[c].Length; i++)
            {
                uint value = JxlFields.U32(ref reader, JxlU32.Bits32(4), JxlU32.BitsOffset(8, 16), JxlU32.BitsOffset(16, 272),
                    JxlU32.BitsOffset(32, 65808));
                dc[c][i] = UnpackSigned(value);
            }
        }

        uint[] qf = new uint[reader.ReadBits(4)];
        for (int i = 0; i < qf.Length; i++)
        {
            qf[i] = JxlFields.U32(ref reader, JxlU32.Bits32(2), JxlU32.BitsOffset(3, 4), JxlU32.BitsOffset(5, 12), JxlU32.BitsOffset(8, 44)) + 1;
        }

        if (dcContexts * (qf.Length + 1) > 64)
        {
            throw JxlDescriptor.Corrupt("Invalid block context map: too big.");
        }

        byte[] map = new byte[3 * JxlAcStrategy.Orders * dcContexts * (qf.Length + 1)];
        if (JxlEntropyCode.ReadContextMap(ref reader, map) > 16)
        {
            throw JxlDescriptor.Corrupt("Invalid block context map: too many contexts.");
        }

        return new JxlBlockContextMap(dc, qf, map);
    }

    public int Context(int dcIndex, int qf, int order, int c)
    {
        int qfIndex = 0;
        foreach (uint threshold in QfThresholds)
        {
            if (qf > threshold)
            {
                qfIndex++;
            }
        }

        int index = c < 2 ? c ^ 1 : 2;
        index = index * JxlAcStrategy.Orders + order;
        index = index * (QfThresholds.Length + 1) + qfIndex;
        return Map[index * DcContexts + dcIndex];
    }

    /// <summary>The DC bucket of quantised X, Y and B values (DequantDC).</summary>
    public int DcBucket(int x, int y, int b)
    {
        int bucketX = DcThresholds[0].Count(t => x > t);
        int bucketY = DcThresholds[1].Count(t => y > t);
        int bucketB = DcThresholds[2].Count(t => b > t);
        return (bucketX * (DcThresholds[2].Length + 1) + bucketB) * (DcThresholds[1].Length + 1) + bucketY;
    }

    public int ZeroDensityContextsOffset(int blockContext) => Contexts * NonZeroBuckets + ZeroDensityContextCount * blockContext;

    public int NonZeroContext(int nonZeros, int blockContext)
    {
        nonZeros = Math.Min(nonZeros, 64);
        return (nonZeros < 8 ? nonZeros : 4 + nonZeros / 2) * Contexts + blockContext;
    }

    public static int ZeroDensityContext(int nonZerosLeft, int k, int log2CoveredBlocks, int previous)
    {
        nonZerosLeft = (nonZerosLeft + (1 << log2CoveredBlocks) - 1) >> log2CoveredBlocks;
        k >>= log2CoveredBlocks;
        return (CoeffNumNonzeroContext[nonZerosLeft] + CoeffFreqContext[k]) * 2 + previous;
    }
}
