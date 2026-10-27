namespace PictureSharp.Formats.Jxl.Codestream.Entropy;

/// <summary>The move-to-front transform of context maps (libjxl v0.12.0 enc_context_map.cc and inverse_mtf-inl.h, BSD-3-Clause).</summary>
internal static class JxlMoveToFront
{
    /// <summary>Replaces every value by its position in a list that moves each used value to the front.</summary>
    public static byte[] Forward(ReadOnlySpan<byte> values)
    {
        Span<byte> order = stackalloc byte[256];
        Identity(order);
        byte[] result = new byte[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            int index = order.IndexOf(values[i]);
            result[i] = (byte)index;
            MoveToFront(order, index);
        }

        return result;
    }

    /// <summary>Undoes <see cref="Forward"/> in place.</summary>
    public static void Inverse(Span<byte> values)
    {
        Span<byte> order = stackalloc byte[256];
        Identity(order);
        for (int i = 0; i < values.Length; i++)
        {
            int index = values[i];
            values[i] = order[index];
            MoveToFront(order, index);
        }
    }

    private static void Identity(Span<byte> order)
    {
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = (byte)i;
        }
    }

    private static void MoveToFront(Span<byte> order, int index)
    {
        if (index == 0)
        {
            return;
        }

        byte value = order[index];
        order[..index].CopyTo(order[1..]);
        order[0] = value;
    }
}
