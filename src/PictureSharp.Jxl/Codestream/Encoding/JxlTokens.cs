namespace PictureSharp.Formats.Jxl.Codestream.Encoding;

/// <summary>The values of one entropy-coded stream, each with its (unclustered) context, in decoding order.</summary>
internal sealed class JxlTokens
{
    private int[] contexts = new int[64];
    private uint[] values = new uint[64];

    public int Count { get; private set; }

    public ReadOnlySpan<int> Contexts => contexts.AsSpan(0, Count);

    public ReadOnlySpan<uint> Values => values.AsSpan(0, Count);

    public void Add(int context, uint value)
    {
        if (Count == contexts.Length)
        {
            int size = Count <= Array.MaxLength / 2 ? Count * 2 : Count < Array.MaxLength ? Array.MaxLength
                : throw JxlDescriptor.Unsupported("the image has too many samples to encode.");
            Array.Resize(ref contexts, size);
            Array.Resize(ref values, size);
        }

        contexts[Count] = context;
        values[Count++] = value;
    }

    /// <summary>A signed value folded to unsigned: 0, -1, 1, -2, … become 0, 1, 2, 3, ….</summary>
    public static uint PackSigned(long value) => unchecked((uint)(value >= 0 ? 2 * (ulong)value : 2 * (ulong)(-value) - 1));
}
