using PictureSharp.Formats.Jxl.Codestream.IO;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;

namespace PictureSharp.Formats.Jxl.Codestream.Entropy;

/// <summary>
/// One alias table entry, packed into 8 bytes like libjxl's AliasTable::Entry: alphabets have at most 256 symbols, so
/// the cutoff (at most 4096 / 32) and the right value fit a byte, frequencies (at most 4096) and offsets a 16-bit field.
/// </summary>
internal readonly struct JxlAliasEntry(int cutoff, int rightValue, int freq0, int offsets1, int freq1)
{
    private readonly byte cutoff = (byte)cutoff;
    private readonly byte rightValue = (byte)rightValue;
    private readonly ushort freq0 = (ushort)freq0;
    private readonly short offsets1 = (short)offsets1;
    private readonly ushort freq1 = (ushort)freq1;

    public int Cutoff => cutoff;

    public int RightValue => rightValue;

    public int Freq0 => freq0;

    public int Offsets1 => offsets1;

    public int Freq1 => freq1;
}

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
            lengthConfig = JxlHybridUint.Read(ref reader, 8);
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
            configs[i] = JxlHybridUint.Read(ref reader, logAlphaSize);
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
            return ReadSimpleHistogram(ref reader);
        }

        return reader.ReadBool() ? FlatDistribution(VarLenUint(ref reader, 3) + 1) : ReadShiftedHistogram(ref reader);
    }

    /// <summary>The probabilities of <paramref name="alphabetSize"/> symbols spread as evenly as possible.</summary>
    public static int[] FlatDistribution(int alphabetSize)
    {
        int[] flat = new int[alphabetSize];
        for (int i = 0; i < flat.Length; i++)
        {
            flat[i] = AnsTabSize / alphabetSize + (i < AnsTabSize % alphabetSize ? 1 : 0);
        }

        return flat;
    }

    /// <summary>The number of explicit mantissa bits of a count of about 2^<paramref name="logCount"/> at a precision shift.</summary>
    public static int CountPrecision(int logCount, int shift) => Math.Max(0, Math.Min(logCount, shift - ((AnsLogTabSize - logCount) >> 1)));

    // One symbol with all the probability, or two with an explicit split.
    private static int[] ReadSimpleHistogram(ref JxlBitReader reader)
    {
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

    // A precision shift, then the log counts with runs, then the mantissa bits; the widest symbol takes the remainder.
    private static int[] ReadShiftedHistogram(ref JxlBitReader reader)
    {
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
        int[] logCounts = new int[length];
        int[] same = new int[length];
        int omitPosition = ReadLogCounts(ref reader, logCounts, same);
        int[] counts = ExpandCounts(ref reader, logCounts, same, omitPosition, shift);
        return counts[omitPosition] > 0 ? counts : throw InvalidHistogram();
    }

    // The log count + 1 of every symbol (-1: zero); a run marker sets the run length instead. Returns the omitted symbol.
    private static int ReadLogCounts(ref JxlBitReader reader, int[] logCounts, int[] same)
    {
        int length = logCounts.Length;
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

        return omitPosition;
    }

    private static int[] ExpandCounts(ref JxlBitReader reader, int[] logCounts, int[] same, int omitPosition, int shift)
    {
        int length = logCounts.Length;
        int[] counts = new int[length];
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
                    int bitCount = CountPrecision(code, shift);
                    counts[i] = (1 << code) + ((int)reader.ReadBits(bitCount) << (code - bitCount));
                }
            }

            total += counts[i];
        }

        counts[omitPosition] = AnsTabSize - total;
        return counts;
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
    internal static void InitAliasTable(int[] distribution, int logAlphaSize, Span<JxlAliasEntry> table)
    {
        int length = distribution.AsSpan().LastIndexOfAnyExcept(0) + 1;
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
            FillSingleSymbol(table, single, entrySize);
            return;
        }

        // Stacks and scratch on the stack (0.8.6): the table has at most 256 entries and this runs per histogram.
        Span<int> underfull = stackalloc int[tableSize];
        Span<int> overfull = stackalloc int[tableSize];
        int underCount = 0, overCount = 0;
        Span<int> cutoffs = stackalloc int[tableSize];
        Span<int> right = stackalloc int[tableSize];
        Span<int> offsets = stackalloc int[tableSize];
        cutoffs.Clear();
        right.Clear();
        offsets.Clear();
        for (int i = 0; i < length; i++)
        {
            cutoffs[i] = distribution[i];
            if (cutoffs[i] > entrySize)
            {
                overfull[overCount++] = i;
            }
            else if (cutoffs[i] < entrySize)
            {
                underfull[underCount++] = i;
            }
        }

        for (int i = length; i < tableSize; i++)
        {
            underfull[underCount++] = i;
        }

        while (overCount > 0)
        {
            int over = overfull[--overCount];
            if (underCount == 0) throw InvalidHistogram();
            int under = underfull[--underCount];
            cutoffs[over] -= entrySize - cutoffs[under];
            right[under] = over;
            offsets[under] = cutoffs[over];
            if (cutoffs[over] < entrySize)
            {
                underfull[underCount++] = over;
            }
            else if (cutoffs[over] > entrySize)
            {
                overfull[overCount++] = over;
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

    // The state never changes when decoding a single-symbol distribution.
    private static void FillSingleSymbol(Span<JxlAliasEntry> table, int symbol, int entrySize)
    {
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = new JxlAliasEntry(0, symbol, 0, entrySize * i, AnsTabSize);
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
                JxlMoveToFront.Inverse(contextMap);
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

    private static ImageFormatException InvalidHistogram() => JxlDescriptor.Corrupt("Invalid ANS histogram.");
}
