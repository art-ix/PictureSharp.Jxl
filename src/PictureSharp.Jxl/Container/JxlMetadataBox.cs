using System.Buffers;
using System.IO.Compression;

namespace PictureSharp.Formats.Jxl.Container;

/// <summary>A borrowed encoded payload. Brotli payloads exclude their four-byte inner type.
/// Consumers must reserve the decompressed metadata size before materializing it.</summary>
internal readonly record struct JxlMetadataBox(string Type, ReadOnlyMemory<byte> Payload, bool IsBrotliCompressed)
{
    private const int Chunk = 16384;

    /// <summary>
    /// The box content: the borrowed payload, or the decompressed brob payload. Each byte is passed to
    /// <paramref name="reserve"/> before it is materialized, and the content may not exceed <paramref name="maxBytes"/>.
    /// Returns null for a corrupt or truncated Brotli stream (ISO/IEC 18181-2 brob box, RFC 7932).
    /// </summary>
    public ReadOnlyMemory<byte>? Read(long maxBytes, Action<long>? reserve, CancellationToken cancellationToken = default)
    {
        if (!IsBrotliCompressed)
        {
            if (Payload.Length > maxBytes) throw JxlDescriptor.Corrupt($"The {Type.TrimEnd()} box exceeds MaxMetadataBytes.");
            reserve?.Invoke(Payload.Length);
            return Payload;
        }

        using var decoder = new BrotliDecoder();
        ReadOnlySpan<byte> source = Payload.Span;
        long limit = Math.Min(maxBytes, Array.MaxLength);
        byte[] output = new byte[(int)Math.Min(limit, Chunk)];
        int length = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (length == output.Length)
            {
                // One byte more than the limit allows proves the content exceeds it.
                Array.Resize(ref output, (int)Math.Min(Math.Max((long)output.Length, Chunk) * 2, limit + 1));
            }

            OperationStatus status = decoder.Decompress(source, output.AsSpan(length), out int consumed, out int written);
            source = source[consumed..];
            if (written > 0)
            {
                if (length + (long)written > limit) throw JxlDescriptor.Corrupt($"The decompressed {Type.TrimEnd()} box exceeds MaxMetadataBytes.");
                reserve?.Invoke(written);
                length += written;
            }

            switch (status)
            {
                case OperationStatus.Done:
                    return source.IsEmpty ? output.AsMemory(0, length) : null;
                case OperationStatus.DestinationTooSmall:
                    continue;
                case OperationStatus.NeedMoreData when consumed > 0 || written > 0:
                    continue;
                default:
                    return null;
            }
        }
    }
}
