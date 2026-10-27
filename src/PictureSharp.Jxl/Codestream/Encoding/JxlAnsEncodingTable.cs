using PictureSharp.Formats.Jxl.Codestream.Entropy;

namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>
/// The encoder's view of a cluster's alias table: for every token its frequency and, per remainder, the slot the
/// decoder maps back to that token, so that encoding inverts <see cref="JxlSymbolReader"/> exactly.
/// </summary>
internal sealed class JxlAnsEncodingTable
{
    private const int TabSize = JxlEntropyCode.AnsTabSize;
    private const int LogTabSize = JxlEntropyCode.AnsLogTabSize;

    private readonly int[] counts;
    // Per token, the first index of its slots; then the slots that decode to (token, offset) in offset order.
    private readonly int[] starts;
    private readonly int[] slots;
    // Per token, ⌈2^64 / frequency⌉ (0 for a frequency of 0 or 1): the high half of its product with a 32-bit state
    // is the exact quotient, as the rounding error stays below 2^32 / 2^64.
    private readonly ulong[] reciprocals;

    public JxlAnsEncodingTable(int[] counts, int logAlphaSize)
    {
        this.counts = counts;
        var table = new JxlAliasEntry[1 << logAlphaSize];
        JxlEntropyCode.InitAliasTable(counts, logAlphaSize, table);
        starts = new int[counts.Length + 1];
        for (int s = 0; s < counts.Length; s++) starts[s + 1] = starts[s] + counts[s];
        reciprocals = new ulong[counts.Length];
        for (int s = 0; s < counts.Length; s++)
        {
            if (counts[s] > 1) reciprocals[s] = ulong.MaxValue / (ulong)counts[s] + 1;
        }

        slots = new int[TabSize];
        int logEntrySize = LogTabSize - logAlphaSize;
        for (int slot = 0; slot < TabSize; slot++)
        {
            JxlAliasEntry entry = table[slot >> logEntrySize];
            int position = slot & ((1 << logEntrySize) - 1);
            bool greater = position >= entry.Cutoff;
            int symbol = greater ? entry.RightValue : slot >> logEntrySize;
            int offset = (greater ? entry.Offsets1 : 0) + position;
            slots[starts[symbol] + offset] = slot;
        }
    }

    public uint Frequency(uint token) => (uint)counts[token];

    /// <summary><paramref name="state"/> divided by the token's frequency, which is not 0.</summary>
    public uint Quotient(uint token, uint state)
    {
        ulong reciprocal = reciprocals[token];
        return reciprocal == 0 ? state : (uint)Math.BigMul(state, reciprocal, out _);
    }

    /// <summary>The slot that decodes to <paramref name="token"/> with offset <paramref name="remainder"/>.</summary>
    public uint Slot(uint token, uint remainder) => (uint)slots[starts[token] + (int)remainder];
}
