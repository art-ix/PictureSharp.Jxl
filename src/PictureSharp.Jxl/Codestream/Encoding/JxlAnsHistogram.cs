using PictureSharp.Formats.Jxl.Codestream.Entropy;
using static PictureSharp.Formats.Jxl.Codestream.Numerics.JxlIntMath;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>
/// An ANS distribution summing to 4096 that the histogram code can represent, with the method that codes it: the
/// simple code (one or two symbols), the flat code, or log counts with a precision shift, where the omitted symbol
/// takes the remainder. The counterpart of <see cref="JxlEntropyCode"/>'s histogram reading; modelled on libjxl
/// v0.12.0 lib/jxl/enc_ans.cc (BSD-3-Clause).
/// </summary>
internal sealed class JxlAnsHistogram
{
    private const int TabSize = JxlEntropyCode.AnsTabSize;
    private const int LogTabSize = JxlEntropyCode.AnsLogTabSize;
    private const int MaxFlatAlphabet = 256;
    private const int MinReps = 5;
    private const int RepMarker = LogTabSize + 1;

    // Static code of ANS bit widths (count log + 1; 13 is the RLE marker), from enc_ans.cc.
    private static ReadOnlySpan<byte> BitWidthLengths => [5, 4, 4, 4, 4, 4, 3, 3, 3, 3, 3, 6, 7, 7];
    private static ReadOnlySpan<byte> BitWidthSymbols => [17, 11, 15, 3, 9, 7, 4, 2, 5, 6, 0, 33, 1, 65];

    private readonly int method;
    private readonly int omit;

    // Method: -1 simple, 0 flat, else the precision shift + 1.
    private JxlAnsHistogram(int[] counts, int method, int omit)
    {
        Counts = counts;
        this.method = method;
        this.omit = omit;
    }

    public int[] Counts { get; }

    private bool IsSimple => method < 0;

    private bool IsFlat => method == 0;

    private int Shift => method - 1;

    private static JxlAnsHistogram Simple(int[] counts) => new(counts, -1, 0);

    private static JxlAnsHistogram Flat(int[] counts) => new(counts, 0, 0);

    private static JxlAnsHistogram Shifted(int[] counts, int shift, int omit) => new(counts, shift + 1, omit);

    /// <summary>Normalizes <paramref name="frequencies"/> with the simple code, flat or every shift, and keeps the cheapest.</summary>
    public static JxlAnsHistogram Normalize(long[] frequencies)
    {
        int alphabet = frequencies.AsSpan().LastIndexOfAnyExcept(0) + 1;
        int symbols = frequencies.Count(f => f > 0);
        long total = frequencies.Sum();
        if (symbols <= 1)
        {
            int[] single = new int[Math.Max(alphabet, 1)];
            single[Math.Max(alphabet - 1, 0)] = TabSize;
            return Simple(single);
        }

        if (symbols == 2)
        {
            int first = Array.FindIndex(frequencies, f => f > 0);
            int second = Array.FindLastIndex(frequencies, f => f > 0);
            int[] pair = new int[alphabet];
            pair[first] = (int)Math.Clamp(Math.Round(frequencies[first] * (double)TabSize / total), 1, TabSize - 1);
            pair[second] = TabSize - pair[first];
            return Simple(pair);
        }

        JxlAnsHistogram? best = null;
        double bestCost = double.MaxValue;
        if (alphabet <= MaxFlatAlphabet)
        {
            best = Flat(JxlEntropyCode.FlatDistribution(alphabet));
            bestCost = best.Cost(frequencies);
        }

        for (int shift = 0; shift < LogTabSize; shift++)
        {
            if (NormalizeWithShift(frequencies, alphabet, total, shift, out int omit) is not { } counts) continue;
            var candidate = Shifted(counts, shift, omit);
            double cost = candidate.Cost(frequencies);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = candidate;
            }
        }

        return best ?? throw new InvalidOperationException("No ANS histogram fits.");
    }

    public void Write(JxlBitWriter writer)
    {
        int alphabet = Counts.AsSpan().LastIndexOfAnyExcept(0) + 1;
        if (IsSimple)
        {
            WriteSimple(writer, alphabet);
            return;
        }

        writer.Write(false);
        if (IsFlat)
        {
            writer.Write(true);
            WriteVarLenUint8(writer, alphabet - 1);
            return;
        }

        writer.Write(false);
        WriteShift(writer);
        WriteVarLenUint8(writer, alphabet - 3);
        int[] widths = Widths(alphabet);
        int[] same = Runs(alphabet);
        WriteWidths(writer, widths, same);
        WriteMantissas(writer, widths, same);
    }

    // The bits of the histogram plus the bits of coding the frequencies with it.
    private double Cost(long[] frequencies)
    {
        var writer = new JxlBitWriter();
        Write(writer);
        double dataBits = 0;
        for (int i = 0; i < Counts.Length; i++)
        {
            if (frequencies[i] > 0) dataBits += frequencies[i] * (LogTabSize - Math.Log2(Counts[i]));
        }

        return writer.BitLength + dataBits;
    }

    private void WriteSimple(JxlBitWriter writer, int alphabet)
    {
        int[] nonzero = [.. Enumerable.Range(0, alphabet).Where(i => Counts[i] > 0)];
        writer.Write(true);
        writer.Write(1, (uint)Math.Max(nonzero.Length - 1, 0));
        foreach (int symbol in nonzero.Length == 0 ? [0] : nonzero) WriteVarLenUint8(writer, symbol);
        if (nonzero.Length == 2) writer.Write(LogTabSize, (uint)Counts[nonzero[0]]);
    }

    // Elias gamma-like code of the method, without the terminating zero at the largest length.
    private void WriteShift(JxlBitWriter writer)
    {
        int log = Log2Floor((uint)method);
        writer.Write(log, (1u << log) - 1);
        if (log != 3) writer.Write(false);
        writer.Write(log, (uint)(method & ((1 << log) - 1)));
    }

    // Count log + 1 per symbol. The omitted symbol takes the remainder: its width is written as at least that of every
    // later symbol and more than every earlier one, so the decoder finds it as the first of the widest.
    private int[] Widths(int alphabet)
    {
        int[] widths = new int[alphabet];
        for (int i = 0; i < alphabet; i++) widths[i] = Counts[i] > 0 ? Log2Floor((uint)Counts[i]) + 1 : 0;
        int omitWidth = 10;
        for (int i = 0; i < alphabet; i++)
        {
            if (i != omit && Counts[i] > 0) omitWidth = Math.Max(omitWidth, widths[i] + (i < omit ? 1 : 0));
        }

        widths[omit] = omitWidth;
        return widths;
    }

    // At the start of every run of equal counts, its length; runs stop at the omitted symbol and the one after it.
    private int[] Runs(int alphabet)
    {
        int[] same = new int[alphabet];
        int last = 0;
        for (int i = 1; i <= alphabet; i++)
        {
            if (i == alphabet || i == omit || i == omit + 1 || Counts[i] != Counts[last])
            {
                same[last] = i - last;
                last = i;
            }
        }

        return same;
    }

    private static void WriteWidths(JxlBitWriter writer, int[] widths, int[] same)
    {
        for (int i = 0; i < widths.Length; i++)
        {
            writer.Write(BitWidthLengths[widths[i]], BitWidthSymbols[widths[i]]);
            if (same[i] >= MinReps)
            {
                writer.Write(BitWidthLengths[RepMarker], BitWidthSymbols[RepMarker]);
                WriteVarLenUint8(writer, same[i] - MinReps);
                i += same[i] - 1;
            }
        }
    }

    private void WriteMantissas(JxlBitWriter writer, int[] widths, int[] same)
    {
        if (Shift == 0)
        {
            return;
        }

        for (int i = 0; i < widths.Length; i++)
        {
            if (widths[i] > 1 && i != omit)
            {
                int bitCount = JxlEntropyCode.CountPrecision(widths[i] - 1, Shift);
                int drop = widths[i] - 1 - bitCount;
                writer.Write(bitCount, (uint)((Counts[i] >> drop) - (1 << bitCount)));
            }

            if (same[i] >= MinReps) i += same[i] - 1;
        }
    }

    private static int[]? NormalizeWithShift(long[] frequencies, int alphabet, long total, int shift, out int omit)
    {
        int[] counts = new int[alphabet];
        omit = -1;
        for (int i = 0; i < alphabet; i++)
        {
            if (frequencies[i] > 0 && (omit < 0 || frequencies[i] > frequencies[omit])) omit = i;
        }

        for (int attempt = 0; attempt < alphabet; attempt++)
        {
            int sum = 0;
            for (int i = 0; i < alphabet; i++)
            {
                if (i == omit || frequencies[i] == 0)
                {
                    counts[i] = 0;
                    continue;
                }

                counts[i] = Representable(Math.Clamp((int)Math.Round(frequencies[i] * (double)TabSize / total), 1, TabSize - 1), shift);
                sum += counts[i];
            }

            // Reduce the largest counts until the omitted symbol keeps a positive remainder.
            while (sum >= TabSize)
            {
                int largest = -1;
                for (int i = 0; i < alphabet; i++)
                {
                    if (i != omit && counts[i] > 1 && (largest < 0 || counts[i] > counts[largest])) largest = i;
                }

                if (largest < 0) return null;
                int reduced = Lower(counts[largest], shift);
                sum -= counts[largest] - reduced;
                counts[largest] = reduced;
            }

            counts[omit] = TabSize - sum;
            // The decoder omits the first symbol of the largest written width; the omitted width is written as at
            // least that of every symbol after it and more than every one before, which is impossible past 2048.
            int conflict = Array.FindIndex(counts, 0, omit, c => c >= 2048);
            if (conflict < 0) return counts;
            omit = conflict;
        }

        return null;
    }

    // The nearest count the shift can represent.
    private static int Representable(int count, int shift)
    {
        int log = Log2Floor((uint)count);
        int drop = log - JxlEntropyCode.CountPrecision(log, shift);
        int step = 1 << drop;
        int down = count & ~(step - 1);
        int up = down + step;
        int result = count - down <= up - count || up >= TabSize ? down : up;
        return Math.Max(result, 1);
    }

    // The largest representable count below count.
    private static int Lower(int count, int shift)
    {
        for (int c = count - 1; c >= 1; c--)
        {
            if (Representable(c, shift) == c) return c;
        }

        return 1;
    }

    /// <summary>0, or 1, three bits of n and n bits of value − 2^n (the counterpart of DecodeVarLenUint8).</summary>
    public static void WriteVarLenUint8(JxlBitWriter writer, int value)
    {
        if (value == 0)
        {
            writer.Write(false);
            return;
        }

        writer.Write(true);
        int bits = Log2Floor((uint)value);
        writer.Write(3, (uint)bits);
        writer.Write(bits, (uint)(value - (1 << bits)));
    }
}
