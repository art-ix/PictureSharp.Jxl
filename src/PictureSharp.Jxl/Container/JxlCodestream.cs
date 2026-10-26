namespace PictureSharp.Formats.Jxl.Container;

/// <summary>
/// A JPEG XL codestream as one or more consecutive slices of the input: the whole file for a raw codestream, the
/// jxlc payload, or the jxlp payloads in counter order. Slices are not copied.
/// </summary>
internal sealed class JxlCodestream
{
    public JxlCodestream(params ReadOnlyMemory<byte>[] segments)
    {
        Segments = segments;
        foreach (ReadOnlyMemory<byte> segment in segments)
        {
            Length += segment.Length;
        }
    }

    public ReadOnlyMemory<byte>[] Segments { get; }

    /// <summary>Total length in bytes.</summary>
    public long Length { get; }
}
