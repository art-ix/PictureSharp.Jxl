namespace PictureSharp.Formats.Jxl;

internal static class JxlSignature
{
    private static ReadOnlySpan<byte> Container => [0, 0, 0, 12, 0x4A, 0x58, 0x4C, 0x20, 13, 10, 0x87, 10];

    public static bool IsSupported(ReadOnlySpan<byte> header) =>
        (header.Length >= 2 && header[0] == 0xFF && header[1] == 0x0A) || header.StartsWith(Container);
}
