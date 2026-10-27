namespace PictureSharp.Formats.Jxl;

// The shared descriptor, signatures and error factories. Self-contained so the tests can compile the parsers directly.
internal static class JxlDescriptor
{
    public static readonly ImageFormat Format = ImageFormat.Create("JPEG XL", "image/jxl", ".jxl");

    /// <summary>The first 16 bits of a codestream, read least significant bit first (ISO/IEC 18181-1).</summary>
    public const ushort CodestreamMarker = 0x0AFF;

    /// <summary>The two bytes that start a bare codestream.</summary>
    public static ReadOnlySpan<byte> CodestreamSignature => [0xFF, 0x0A];

    /// <summary>The 12-byte signature box that starts a container (ISO/IEC 18181-2).</summary>
    public static ReadOnlySpan<byte> ContainerSignature => [0, 0, 0, 12, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 13, 10, 0x87, 10];

    /// <summary>Malformed or truncated data.</summary>
    public static ImageFormatException Corrupt(string message) => new(Format, message);

    /// <summary>A valid feature this version does not implement.</summary>
    public static NotSupportedException Unsupported(string message) => new($"JPEG XL: {message}");
}
