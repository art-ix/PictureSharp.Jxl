namespace PictureSharp.Formats.Jxl.Container;

/// <summary>A borrowed encoded payload. Brotli payloads exclude their four-byte inner type.
/// Consumers must reserve the decompressed metadata size before materializing it.</summary>
internal readonly record struct JxlMetadataBox(string Type, ReadOnlyMemory<byte> Payload, bool IsBrotliCompressed);
