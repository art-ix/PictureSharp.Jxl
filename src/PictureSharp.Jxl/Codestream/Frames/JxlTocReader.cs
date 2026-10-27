using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Model;
using static PictureSharp.Formats.Jxl.Codestream.IO.JxlU32;
using static PictureSharp.Formats.Jxl.Codestream.Model.JxlToc;

namespace PictureSharp.Formats.Jxl.Codestream.Frames;

/// <summary>Reads JxlToc using the libjxl v0.12.0 field order (BSD-3-Clause).</summary>
internal static class JxlTocReader
{
    public static JxlToc Read(ref JxlBitReader reader, long entries)
    {
        if (entries > MaxEntries)
        {
            throw JxlDescriptor.Corrupt("Too many TOC entries.");
        }

        int count = (int)entries;
        // Every entry takes at least 12 bits, so a TOC larger than the input is truncated, not allocated.
        if (count * 12L > reader.RemainingBits)
        {
            throw JxlDescriptor.Corrupt("Truncated codestream.");
        }

        int[]? permutation = reader.ReadBool() ? ReadPermutation(ref reader, count) : null;
        reader.JumpToByteBoundary();
        uint[] sizes = new uint[count];
        for (int i = 0; i < count; i++)
        {
            sizes[i] = JxlFields.U32(ref reader, Bits32(10), BitsOffset(14, 1024), BitsOffset(22, 17408), BitsOffset(30, 4211712));
        }

        reader.JumpToByteBoundary();
        long[] offsets = new long[count];
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            offsets[i] = total;
            total += sizes[i];
        }

        if (permutation is null)
        {
            return new JxlToc(offsets, sizes, total);
        }

        return new JxlToc([.. permutation.Select(index => offsets[index])], [.. permutation.Select(index => sizes[index])], total);
    }

    private static int[] ReadPermutation(ref JxlBitReader reader, int size)
    {
        JxlEntropyCode code = JxlEntropyCode.Read(ref reader, JxlPermutation.Contexts);
        using var symbols = new JxlSymbolReader(code, ref reader);
        int[] permutation = JxlPermutation.Read(ref reader, symbols, code.ContextMap, 0, size);
        return symbols.IsFinalStateValid ? permutation : throw JxlDescriptor.Corrupt("Invalid ANS stream.");
    }
}
