using PictureSharp.Formats.Jxl.Codestream.IO;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;

namespace PictureSharp.Formats.Jxl.Codestream.Entropy;

/// <summary>
/// The hybrid integer split: tokens below <see cref="SplitToken"/> are values; larger ones carry the exponent and the
/// top <see cref="MsbInToken"/> and bottom <see cref="LsbInToken"/> mantissa bits, the rest follow as raw bits.
/// </summary>
internal readonly record struct JxlHybridUint(int SplitExponent, int MsbInToken, int LsbInToken)
{
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
}

/// <summary>One alias table entry; see libjxl's AliasTable for the layout.</summary>
internal readonly record struct JxlAliasEntry(int Cutoff, int RightValue, int Freq0, int Offsets1, int Freq1);

/// <summary>
/// Clustered entropy codes for a set of contexts: the context map, LZ77 parameters, and per cluster a hybrid integer
/// configuration with either an ANS distribution or a prefix code. Ported from libjxl v0.12.0 lib/jxl/dec_ans.cc,
/// lib/jxl/ans_common.cc, lib/jxl/dec_context_map.cc and lib/jxl/inverse_mtf-inl.h (BSD-3-Clause).
/// </summary>
internal sealed class JxlEntropyCode
{
    public const int AnsLogTabSize = 12;
    public const int AnsTabSize = 1 << AnsLogTabSize;
    public const uint AnsSignature = 0x13 << 16;
    private const int PrefixMaxBits = 15;
    private const int MaxClusters = 256;

    private JxlEntropyCode(byte[] contextMap) => ContextMap = contextMap;

    /// <summary>Context → cluster; with LZ77 the last entry is the distance context.</summary>
    public byte[] ContextMap { get; }

    public bool UsePrefixCode { get; private init; }

    public int LogAlphaSize { get; private init; }

    public JxlHybridUint[] Configs { get; private init; } = [];

    /// <summary>The alias tables of all clusters, <c>1 &lt;&lt; LogAlphaSize</c> entries each.</summary>
    public JxlAliasEntry[] AliasTables { get; private init; } = [];

    public JxlPrefixCode[] PrefixCodes { get; private init; } = [];

    /// <summary>Per cluster, the only symbol of an ANS distribution that has one (and only zeros before it), else -1.</summary>
    public int[] DegenerateSymbols { get; private init; } = [];

    public bool Lz77Enabled { get; private init; }

    public uint Lz77MinSymbol { get; private init; }

    public uint Lz77MinLength { get; private init; }

    public JxlHybridUint Lz77LengthConfig { get; private init; }

    /// <summary>The clustered context of LZ77 distances.</summary>
    public int Lz77DistanceCluster => ContextMap[^1];

    /// <summary>Reads LZ77 parameters, the context map and the clustered distributions (DecodeHistograms).</summary>
    public static JxlEntropyCode Read(ref JxlBitReader reader, int contexts, bool disallowLz77 = false)
    {
        bool lz77 = reader.ReadBool();
        uint minSymbol = 0;
        uint minLength = 0;
        JxlHybridUint lengthConfig = default;
        if (lz77)
        {
            if (disallowLz77)
            {
                throw JxlDescriptor.Corrupt("LZ77 is not allowed in this context map.");
            }

            minSymbol = JxlFields.U32(ref reader, Val(224), Val(512), Val(4096), BitsOffset(15, 8));
            minLength = JxlFields.U32(ref reader, Val(3), Val(4), BitsOffset(2, 5), BitsOffset(8, 9));
            lengthConfig = ReadConfig(ref reader, 8);
            contexts++;
        }

        int histograms = 1;
        byte[] contextMap = new byte[contexts];
        if (contexts > 1)
        {
            histograms = ReadContextMap(ref reader, contextMap);
        }

        bool prefix = reader.ReadBool();
        int logAlphaSize = prefix ? PrefixMaxBits : (int)reader.ReadBits(2) + 5;
        var configs = new JxlHybridUint[histograms];
        for (int i = 0; i < configs.Length; i++)
        {
            configs[i] = ReadConfig(ref reader, logAlphaSize);
        }

        int[] degenerate = new int[histograms];
        Array.Fill(degenerate, -1);
        var code = new JxlEntropyCode(contextMap)
        {
            UsePrefixCode = prefix,
            LogAlphaSize = logAlphaSize,
            Configs = configs,
            Lz77Enabled = lz77,
            Lz77MinSymbol = minSymbol,
            Lz77MinLength = minLength,
            Lz77LengthConfig = lengthConfig,
            AliasTables = prefix ? [] : ReadAnsCodes(ref reader, logAlphaSize, degenerate),
            PrefixCodes = prefix ? ReadPrefixCodes(ref reader, histograms) : [],
            DegenerateSymbols = degenerate,
        };
        return code;
    }

    private static JxlHybridUint ReadConfig(ref JxlBitReader reader, int logAlphaSize)
    {
        int splitExponent = (int)reader.ReadBits(CeilLog2(logAlphaSize + 1));
        int msb = 0;
        int lsb = 0;
        if (splitExponent != logAlphaSize)
        {
            msb = (int)reader.ReadBits(CeilLog2(splitExponent + 1));
            if (msb > splitExponent)
            {
                throw JxlDescriptor.Corrupt("Invalid hybrid integer configuration.");
            }

            lsb = (int)reader.ReadBits(CeilLog2(splitExponent - msb + 1));
        }

        return lsb + msb <= splitExponent
            ? new JxlHybridUint(splitExponent, msb, lsb)
            : throw JxlDescriptor.Corrupt("Invalid hybrid integer configuration.");
    }

    private static JxlPrefixCode[] ReadPrefixCodes(ref JxlBitReader reader, int histograms)
    {
        int[] alphabetSizes = new int[histograms];
        for (int i = 0; i < histograms; i++)
        {
            alphabetSizes[i] = VarLenUint(ref reader, 4) + 1;
            if (alphabetSizes[i] > 1 << PrefixMaxBits)
            {
                throw JxlDescriptor.Corrupt("Prefix code alphabet is too large.");
            }
        }

        var codes = new JxlPrefixCode[histograms];
        for (int i = 0; i < histograms; i++)
        {
            codes[i] = alphabetSizes[i] > 1 ? JxlPrefixCode.Read(ref reader, alphabetSizes[i]) : JxlPrefixCode.Zero;
        }

        return codes;
    }

    private static JxlAliasEntry[] ReadAnsCodes(ref JxlBitReader reader, int logAlphaSize, int[] degenerate)
    {
        int tableSize = 1 << logAlphaSize;
        var tables = new JxlAliasEntry[degenerate.Length * tableSize];
        for (int i = 0; i < degenerate.Length; i++)
        {
            int[] counts = ReadHistogram(ref reader);
            if (counts.Length > tableSize)
            {
                throw JxlDescriptor.Corrupt("ANS alphabet is too large.");
            }

            int last = Array.FindLastIndex(counts, count => count != 0);
            degenerate[i] = last <= 0 || Array.FindIndex(counts, count => count != 0) == last ? Math.Max(last, 0) : -1;

            InitAliasTable(counts, logAlphaSize, tables.AsSpan(i * tableSize, tableSize));
        }

        return tables;
    }

    /// <summary>An ANS distribution summing to <see cref="AnsTabSize"/>, possibly with trailing zeros.</summary>
    private static int[] ReadHistogram(ref JxlBitReader reader)
    {
        if (reader.ReadBool())
        {
            // Simple: one symbol with all the probability, or two with an explicit split.
            int symbols = (int)reader.ReadBits(1) + 1;
            Span<int> values = stackalloc int[2];
            for (int i = 0; i < symbols; i++)
            {
                values[i] = VarLenUint(ref reader, 3);
            }

            int[] simple = new int[Math.Max(values[0], symbols == 2 ? values[1] : 0) + 1];
            if (symbols == 1)
            {
                simple[values[0]] = AnsTabSize;
                return simple;
            }

            if (values[0] == values[1])
            {
                throw InvalidHistogram();
            }

            simple[values[0]] = (int)reader.ReadBits(AnsLogTabSize);
            simple[values[1]] = AnsTabSize - simple[values[0]];
            return simple;
        }

        if (reader.ReadBool())
        {
            int alphabetSize = VarLenUint(ref reader, 3) + 1;
            int[] flat = new int[alphabetSize];
            for (int i = 0; i < flat.Length; i++)
            {
                flat[i] = AnsTabSize / alphabetSize + (i < AnsTabSize % alphabetSize ? 1 : 0);
            }

            return flat;
        }

        int log = 0;
        while (log < 3 && reader.ReadBool())
        {
            log++;
        }

        int shift = (int)((reader.ReadBits(log) | (1u << log)) - 1);
        if (shift > AnsLogTabSize + 1)
        {
            throw InvalidHistogram();
        }

        int length = VarLenUint(ref reader, 3) + 3;
        int[] counts = new int[length];
        int[] logCounts = new int[length];
        int[] same = new int[length];
        int omitLog = -1;
        int omitPosition = -1;
        for (int i = 0; i < length; i++)
        {
            (int bits, int value) = LogCountCode[(int)reader.PeekBits(7)];
            reader.Consume(bits);
            logCounts[i] = value - 1;
            if (logCounts[i] == AnsLogTabSize)
            {
                // A run of copies of the previous count.
                int run = VarLenUint(ref reader, 3);
                same[i] = run + 5;
                i += run + 3;
                continue;
            }

            if (logCounts[i] > omitLog)
            {
                omitLog = logCounts[i];
                omitPosition = i;
            }
        }

        if (omitPosition < 0 || (omitPosition + 1 < length && logCounts[omitPosition + 1] == AnsLogTabSize))
        {
            throw InvalidHistogram();
        }

        int total = 0;
        int previous = 0;
        int repeat = 0;
        for (int i = 0; i < length; i++)
        {
            if (same[i] != 0)
            {
                repeat = same[i] - 1;
                previous = i > 0 ? counts[i - 1] : 0;
            }

            if (repeat > 0)
            {
                counts[i] = previous;
                repeat--;
            }
            else
            {
                int code = logCounts[i];
                if (i == omitPosition || code < 0)
                {
                    continue;
                }

                if (shift == 0 || code == 0)
                {
                    counts[i] = 1 << code;
                }
                else
                {
                    int bitCount = Math.Max(0, Math.Min(code, shift - ((AnsLogTabSize - code) >> 1)));
                    counts[i] = (1 << code) + ((int)reader.ReadBits(bitCount) << (code - bitCount));
                }
            }

            total += counts[i];
        }

        counts[omitPosition] = AnsTabSize - total;
        return counts[omitPosition] > 0 ? counts : throw InvalidHistogram();
    }

    // (bits, log count + 1) indexed by 7 peeked bits.
    private static readonly (int Bits, int Value)[] LogCountCode =
    [
        (3, 10), (7, 12), (3, 7), (4, 3), (3, 6), (3, 8), (3, 9), (4, 5), (3, 10), (4, 4), (3, 7), (4, 1), (3, 6), (3, 8), (3, 9), (4, 2),
        (3, 10), (5, 0), (3, 7), (4, 3), (3, 6), (3, 8), (3, 9), (4, 5), (3, 10), (4, 4), (3, 7), (4, 1), (3, 6), (3, 8), (3, 9), (4, 2),
        (3, 10), (6, 11), (3, 7), (4, 3), (3, 6), (3, 8), (3, 9), (4, 5), (3, 10), (4, 4), (3, 7), (4, 1), (3, 6), (3, 8), (3, 9), (4, 2),
        (3, 10), (5, 0), (3, 7), (4, 3), (3, 6), (3, 8), (3, 9), (4, 5), (3, 10), (4, 4), (3, 7), (4, 1), (3, 6), (3, 8), (3, 9), (4, 2),
        (3, 10), (7, 13), (3, 7), (4, 3), (3, 6), (3, 8), (3, 9), (4, 5), (3, 10), (4, 4), (3, 7), (4, 1), (3, 6), (3, 8), (3, 9), (4, 2),
        (3, 10), (5, 0), (3, 7), (4, 3), (3, 6), (3, 8), (3, 9), (4, 5), (3, 10), (4, 4), (3, 7), (4, 1), (3, 6), (3, 8), (3, 9), (4, 2),
        (3, 10), (6, 11), (3, 7), (4, 3), (3, 6), (3, 8), (3, 9), (4, 5), (3, 10), (4, 4), (3, 7), (4, 1), (3, 6), (3, 8), (3, 9), (4, 2),
        (3, 10), (5, 0), (3, 7), (4, 3), (3, 6), (3, 8), (3, 9), (4, 5), (3, 10), (4, 4), (3, 7), (4, 1), (3, 6), (3, 8), (3, 9), (4, 2),
    ];

    /// <summary>
    /// Builds the alias table of a distribution: every entry covers <c>AnsTabSize >> logAlphaSize</c> slots split
    /// between its own symbol (below the cutoff) and one other symbol.
    /// </summary>
    private static void InitAliasTable(int[] distribution, int logAlphaSize, Span<JxlAliasEntry> table)
    {
        int length = distribution.Length;
        while (length > 0 && distribution[length - 1] == 0)
        {
            length--;
        }

        if (length == 0)
        {
            distribution = [AnsTabSize];
            length = 1;
        }

        int tableSize = table.Length;
        int entrySize = AnsTabSize >> logAlphaSize;
        int single = -1;
        int sum = 0;
        for (int symbol = 0; symbol < length; symbol++)
        {
            sum += distribution[symbol];
            if (distribution[symbol] == AnsTabSize)
            {
                single = symbol;
            }
        }

        if (sum != AnsTabSize || length > tableSize)
        {
            throw InvalidHistogram();
        }

        if (single >= 0)
        {
            // The state never changes when decoding a single-symbol distribution.
            for (int i = 0; i < tableSize; i++)
            {
                table[i] = new JxlAliasEntry(0, single, 0, entrySize * i, AnsTabSize);
            }

            return;
        }

        var underfull = new Stack<int>();
        var overfull = new Stack<int>();
        int[] cutoffs = new int[tableSize];
        int[] right = new int[tableSize];
        int[] offsets = new int[tableSize];
        for (int i = 0; i < length; i++)
        {
            cutoffs[i] = distribution[i];
            if (cutoffs[i] > entrySize)
            {
                overfull.Push(i);
            }
            else if (cutoffs[i] < entrySize)
            {
                underfull.Push(i);
            }
        }

        for (int i = length; i < tableSize; i++)
        {
            underfull.Push(i);
        }

        while (overfull.Count > 0)
        {
            int over = overfull.Pop();
            int under = underfull.Pop();
            cutoffs[over] -= entrySize - cutoffs[under];
            right[under] = over;
            offsets[under] = cutoffs[over];
            if (cutoffs[over] < entrySize)
            {
                underfull.Push(over);
            }
            else if (cutoffs[over] > entrySize)
            {
                overfull.Push(over);
            }
        }

        for (int i = 0; i < tableSize; i++)
        {
            int cutoff = 0;
            if (cutoffs[i] == entrySize)
            {
                right[i] = i;
                offsets[i] = 0;
            }
            else
            {
                offsets[i] -= cutoffs[i];
                cutoff = cutoffs[i];
            }

            int freq0 = i < length ? distribution[i] : 0;
            int freq1 = right[i] < length ? distribution[right[i]] : 0;
            table[i] = new JxlAliasEntry(cutoff, right[i], freq0, offsets[i], freq1);
        }
    }

    /// <summary>Reads the cluster of every context and returns the number of clusters; all must be used.</summary>
    public static int ReadContextMap(ref JxlBitReader reader, byte[] contextMap)
    {
        if (reader.ReadBool())
        {
            int bits = (int)reader.ReadBits(2);
            for (int i = 0; i < contextMap.Length; i++)
            {
                contextMap[i] = (byte)reader.ReadBits(bits);
            }
        }
        else
        {
            bool moveToFront = reader.ReadBool();
            // Two-entry maps may not use LZ77, so a map cannot recurse into maps of its own forever.
            JxlEntropyCode code = Read(ref reader, 1, disallowLz77: contextMap.Length <= 2);
            using (var symbols = new JxlSymbolReader(code, ref reader))
            {
                uint max = 0;
                for (int i = 0; i < contextMap.Length; i++)
                {
                    uint symbol = symbols.Read(0, ref reader);
                    max = Math.Max(max, symbol);
                    contextMap[i] = (byte)symbol;
                }

                if (max >= MaxClusters || !symbols.IsFinalStateValid)
                {
                    throw JxlDescriptor.Corrupt("Invalid context map.");
                }
            }

            if (moveToFront)
            {
                InverseMoveToFront(contextMap);
            }
        }

        int clusters = contextMap.Max() + 1;
        Span<bool> used = stackalloc bool[MaxClusters];
        foreach (byte cluster in contextMap)
        {
            used[cluster] = true;
        }

        return used[..clusters].Contains(false) ? throw JxlDescriptor.Corrupt("Incomplete context map.") : clusters;
    }

    private static void InverseMoveToFront(Span<byte> values)
    {
        Span<byte> order = stackalloc byte[256];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = (byte)i;
        }

        for (int i = 0; i < values.Length; i++)
        {
            int index = values[i];
            byte value = order[index];
            values[i] = value;
            if (index != 0)
            {
                order[..index].CopyTo(order[1..]);
                order[0] = value;
            }
        }
    }

    /// <summary>0, or a value of 1 + Bits(n) bits for n read in <paramref name="lengthBits"/> bits (DecodeVarLenUint8/16).</summary>
    private static int VarLenUint(ref JxlBitReader reader, int lengthBits)
    {
        if (!reader.ReadBool())
        {
            return 0;
        }

        int bits = (int)reader.ReadBits(lengthBits);
        return bits == 0 ? 1 : (int)reader.ReadBits(bits) + (1 << bits);
    }

    private static int CeilLog2(int value) => value <= 1 ? 0 : JxlPrefixCode.Log2Floor((uint)value - 1) + 1;

    private static ImageFormatException InvalidHistogram() => JxlDescriptor.Corrupt("Invalid ANS histogram.");
}
