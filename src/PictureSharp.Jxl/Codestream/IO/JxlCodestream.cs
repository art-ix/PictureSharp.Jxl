namespace PictureSharp.Formats.Jxl.Codestream.IO;

/// <summary>
/// A JPEG XL codestream as one or more consecutive slices of the input: the whole file for a raw codestream, the
/// jxlc payload, or the jxlp payloads in counter order. Slices are not copied.
/// </summary>
internal sealed class JxlCodestream
{
    public JxlCodestream(params ReadOnlyMemory<byte>[] segments)
    {
        this.segments = (ReadOnlyMemory<byte>[])segments.Clone();
        foreach (ReadOnlyMemory<byte> segment in this.segments)
        {
            Length += segment.Length;
        }
    }

    private readonly ReadOnlyMemory<byte>[] segments;

    public ReadOnlySpan<ReadOnlyMemory<byte>> Segments => segments;

    /// <summary>Total length in bytes.</summary>
    public long Length { get; }

    /// <summary>The bytes <c>[offset, offset + length)</c> as a codestream of their own, without copying.</summary>
    public JxlCodestream Slice(long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > Length - length)
        {
            throw JxlDescriptor.Corrupt("Truncated codestream.");
        }

        var slices = new List<ReadOnlyMemory<byte>>();
        foreach (ReadOnlyMemory<byte> segment in Segments)
        {
            if (length == 0)
            {
                break;
            }

            if (offset >= segment.Length)
            {
                offset -= segment.Length;
                continue;
            }

            int take = (int)Math.Min(segment.Length - offset, length);
            slices.Add(segment.Slice((int)offset, take));
            length -= take;
            offset = 0;
        }

        return new JxlCodestream([.. slices]);
    }
}
