using PictureSharp.Formats.Jxl.Codestream.Entropy;
using PictureSharp.Formats.Jxl.Codestream.IO;

namespace PictureSharp.Formats.Jxl.Codestream.Metadata;

/// <summary>ICC entropy stream navigation, ported from libjxl v0.12.0 icc_codec.cc (BSD-3-Clause).</summary>
internal static class JxlIccStream
{
    private const int IccContexts = 41;
    /// <summary>Decodes the entropy-coded ICC stream only to find where the frames start.</summary>
    public static void Skip(ref JxlBitReader reader, ulong size, CancellationToken cancellationToken)
    {
        long begin = reader.BitPosition;
        JxlEntropyCode code = JxlEntropyCode.Read(ref reader, IccContexts);
        using var symbols = new JxlSymbolReader(code, ref reader);
        byte previous1 = 0;
        byte previous2 = 0;
        for (ulong i = 0; i < size; i++)
        {
            if ((i & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            // libjxl's guard against streams that produce many bytes from few bits.
            if (i > 0 && (i & 0xFFFF) == 0 && i > (ulong)(reader.BitPosition - begin) / 8 * 256)
            {
                throw JxlDescriptor.Corrupt("Corrupt ICC profile stream.");
            }

            int context = i <= 128 ? 0 : 1 + ByteKind1(previous1) + ByteKind2(previous2) * 8;
            byte value = (byte)symbols.Read(context, ref reader);
            (previous2, previous1) = (previous1, value);
        }

        if (!symbols.IsFinalStateValid)
        {
            throw JxlDescriptor.Corrupt("Corrupt ICC profile stream.");
        }
    }

    private static int ByteKind1(byte b) => b switch
    {
        >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' => 0,
        >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)',' => 1,
        0 => 2,
        1 => 3,
        < 16 => 4,
        255 => 6,
        > 240 => 5,
        _ => 7,
    };

    private static int ByteKind2(byte b) => b switch
    {
        >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' => 0,
        >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)',' => 1,
        < 16 => 2,
        > 240 => 3,
        _ => 4,
    };
}
