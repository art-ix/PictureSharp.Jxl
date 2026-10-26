namespace PictureSharp.Formats.Jxl;

// The shared descriptor and error factories. Self-contained so the tests can compile the parsers directly.
internal static class JxlDescriptor
{
    public static readonly ImageFormat Format = ImageFormat.Create("JPEG XL", "image/jxl", ".jxl");

    /// <summary>Malformed or truncated data.</summary>
    public static ImageFormatException Corrupt(string message) => new(Format, message);

    /// <summary>A valid feature this version does not implement.</summary>
    public static NotSupportedException Unsupported(string message) => new($"JPEG XL: {message}");
}
