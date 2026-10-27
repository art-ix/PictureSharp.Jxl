using PictureSharp.Formats.Jxl.Codestream.IO;
using PictureSharp.Formats.Jxl.Codestream.Metadata;
using PictureSharp.Formats.Jxl.Codestream.Model;

namespace PictureSharp.Formats.Jxl.Codestream.Frames;

internal static class JxlFrameSectionReader
{
    public static void DecodeSections(JxlCodestream codestream, JxlFrameHeader frame, long start, JxlToc toc, JxlFrameDecoder decoder)
    {
        if (toc.Sizes.Count == 1)
        {
            var section = new JxlBitReader(codestream.Slice(start, toc.Sizes[0]));
            decoder.DecodeLfGlobal(ref section);
            decoder.DecodeLfGroup(ref section, 0);
            decoder.FinishLf();
            decoder.DecodeHfGlobal(ref section);
            int[]? coefficients = decoder.BeginGroup(0);
            decoder.DecodePassGroup(ref section, 0, 0, coefficients);
            decoder.FinishGroup(0, coefficients);
            return;
        }

        JxlBitReader Section(long index) => new(codestream.Slice(start + toc.Offsets[(int)index], toc.Sizes[(int)index]));
        var global = Section(0);
        decoder.DecodeLfGlobal(ref global);
        for (int group = 0; group < frame.DcGroups; group++)
        {
            var section = Section(1 + group);
            decoder.DecodeLfGroup(ref section, group);
        }

        decoder.FinishLf();
        var hfGlobal = Section(1 + frame.DcGroups);
        decoder.DecodeHfGlobal(ref hfGlobal);
        for (int group = 0; group < frame.Groups; group++)
        {
            int[]? coefficients = decoder.BeginGroup(group);
            for (int pass = 0; pass < frame.Passes.Count; pass++)
            {
                var section = Section(2 + frame.DcGroups + pass * frame.Groups + group);
                decoder.DecodePassGroup(ref section, pass, group, coefficients);
            }

            decoder.FinishGroup(group, coefficients);
        }
    }

    /// <summary>
    /// A reader at the first frame after the headers, the ICC stream and a preview frame. The ICC profile is decoded,
    /// as libjxl always does; its size is checked against <paramref name="maxIccBytes"/> and charged to
    /// <paramref name="reserveIcc"/> before it is allocated.
    /// </summary>
    public static JxlBitReader FirstFrame(JxlCodestream codestream, JxlHeaders headers, CancellationToken cancellationToken,
        out byte[]? icc, long maxIccBytes = JxlIccStream.MaxProfileSize, Action<long>? reserveIcc = null)
    {
        var reader = ReadIcc(codestream, headers, cancellationToken, out icc, maxIccBytes, reserveIcc);

        if (headers.Metadata.Preview is not null)
        {
            JxlFrameHeader preview = JxlFrameHeaderReader.Read(ref reader, headers, preview: true);
            long previewStart = ReadToc(ref reader, codestream, preview, out JxlToc previewToc);
            reader = new JxlBitReader(codestream);
            reader.SkipBits((ulong)(previewStart + previewToc.TotalSize) * 8);
        }

        return reader;
    }

    /// <summary>The ICC profile after the headers, or null without one; the reader stops at the next byte boundary.</summary>
    public static JxlBitReader ReadIcc(JxlCodestream codestream, JxlHeaders headers, CancellationToken cancellationToken,
        out byte[]? icc, long maxIccBytes = JxlIccStream.MaxProfileSize, Action<long>? reserveIcc = null)
    {
        var reader = new JxlBitReader(codestream);
        reader.SkipBits((ulong)headers.EndBitPosition);
        icc = null;
        if (headers.IccSize is { } iccSize)
        {
            icc = JxlIccStream.Read(ref reader, iccSize, maxIccBytes, reserveIcc, cancellationToken);
            reader.JumpToByteBoundary();
        }

        return reader;
    }

    /// <summary>Reads the TOC and returns the byte offset of the first section; the sections must be present.</summary>
    public static long ReadToc(ref JxlBitReader reader, JxlCodestream codestream, JxlFrameHeader frame, out JxlToc toc)
    {
        toc = JxlTocReader.Read(ref reader, frame.TocEntries);
        long start = reader.BitPosition / 8;
        if (toc.TotalSize > codestream.Length - start)
        {
            throw JxlDescriptor.Corrupt("Truncated codestream.");
        }

        return start;
    }
}
