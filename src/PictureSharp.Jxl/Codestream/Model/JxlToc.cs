namespace PictureSharp.Formats.Jxl.Codestream.Model;

/// <summary>
/// The table of contents: the byte range of every section in logical order, relative to the end of the TOC. Ported
/// from libjxl v0.12.0 lib/jxl/toc.cc and the permutation decoding of lib/jxl/coeff_order.cc (BSD-3-Clause).
/// </summary>
internal sealed class JxlToc(long[] offsets, uint[] sizes, long totalSize)
{
    public const int MaxEntries = 65536;

    public long TotalSize { get; } = totalSize;

    public IReadOnlyList<long> Offsets { get; } = offsets.Snapshot();

    public IReadOnlyList<uint> Sizes { get; } = sizes.Snapshot();
}
