using PictureSharp.Formats.Jxl.Integration;

namespace PictureSharp.Formats.Jxl;

/// <summary>
/// Losslessly transcodes 8-bit Huffman JPEG (sequential or progressive, grayscale or YCbCr 4:4:4/4:2:2/4:2:0) to JPEG XL
/// and reconstructs the original JPEG byte for byte. Streams remain open. Input and output are buffered; cancellation
/// or an I/O error during output may leave a partial result. Path overloads create or overwrite the output file only
/// after transcoding succeeds. This API does not re-encode pixels or change quality.
/// </summary>
public static class JxlTranscoder
{
    /// <summary>Writes a JPEG XL container preserving the original JPEG, including its metadata and marker layout.</summary>
    public static void FromJpeg(ReadOnlySpan<byte> jpeg, Stream output, JxlTranscoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateOutput(output);
        options ??= new();
        CheckLength(jpeg.Length, options);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] result = JxlTranscoding.FromJpeg(jpeg, options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); output.Write(result);
    }

    /// <summary>Reads JPEG from the current stream position to its end and writes a reconstructable JPEG XL container.</summary>
    public static void FromJpeg(Stream jpeg, Stream output, JxlTranscoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateOutput(output); options ??= new();
        FromJpeg(Read(jpeg, options, cancellationToken), output, options, cancellationToken);
    }

    /// <summary>Reconstructs the original JPEG; valid JPEG XL without reconstruction data is unsupported.</summary>
    public static void ToJpeg(ReadOnlySpan<byte> jxl, Stream output, JxlTranscoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateOutput(output); options ??= new(); CheckLength(jxl.Length, options);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] result = JxlTranscoding.ToJpeg(jxl.ToArray(), options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); output.Write(result);
    }

    /// <summary>Reads JPEG XL from the current stream position and writes the byte-exact original JPEG.</summary>
    public static void ToJpeg(Stream jxl, Stream output, JxlTranscoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateOutput(output); options ??= new();
        byte[] result = JxlTranscoding.ToJpeg(Read(jxl, options, cancellationToken), options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); output.Write(result);
    }

    /// <summary>Reads a JPEG file and writes a reconstructable JPEG XL file, creating or overwriting <paramref name="outputPath"/>.</summary>
    public static void FromJpeg(string jpegPath, string outputPath, JxlTranscoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidatePaths(jpegPath, outputPath); options ??= new();
        byte[] result = JxlTranscoding.FromJpeg(ReadFile(jpegPath, options, cancellationToken), options, cancellationToken);
        WriteFile(outputPath, result, cancellationToken);
    }

    /// <summary>Reads a JPEG XL file and writes the byte-exact original JPEG, creating or overwriting <paramref name="outputPath"/>.</summary>
    public static void ToJpeg(string jxlPath, string outputPath, JxlTranscoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidatePaths(jxlPath, outputPath); options ??= new();
        byte[] result = JxlTranscoding.ToJpeg(ReadFile(jxlPath, options, cancellationToken), options, cancellationToken);
        WriteFile(outputPath, result, cancellationToken);
    }

    /// <summary>Transcodes buffered JPEG with asynchronous output I/O and cooperative CPU cancellation.</summary>
    public static async Task FromJpegAsync(ReadOnlyMemory<byte> jpeg, Stream output, JxlTranscoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateOutput(output); options ??= new(); CheckLength(jpeg.Length, options);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] result = JxlTranscoding.FromJpeg(jpeg.Span, options, cancellationToken);
        await output.WriteAsync(result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Transcodes JPEG using asynchronous input and output I/O; supports non-seekable streams.</summary>
    public static async Task FromJpegAsync(Stream jpeg, Stream output, JxlTranscoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateOutput(output); options ??= new();
        byte[] input = await ReadAsync(jpeg, options, cancellationToken).ConfigureAwait(false);
        await FromJpegAsync(input.AsMemory(), output, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reconstructs buffered JPEG XL with asynchronous output I/O and cooperative CPU cancellation.</summary>
    public static async Task ToJpegAsync(ReadOnlyMemory<byte> jxl, Stream output, JxlTranscoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateOutput(output); options ??= new(); CheckLength(jxl.Length, options);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] result = JxlTranscoding.ToJpeg(jxl, options, cancellationToken);
        await output.WriteAsync(result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reconstructs original JPEG using asynchronous input and output I/O; supports non-seekable streams.</summary>
    public static async Task ToJpegAsync(Stream jxl, Stream output, JxlTranscoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateOutput(output); options ??= new();
        byte[] input = await ReadAsync(jxl, options, cancellationToken).ConfigureAwait(false);
        await ToJpegAsync(input.AsMemory(), output, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Transcodes a JPEG file to a JPEG XL file using asynchronous file I/O.</summary>
    public static async Task FromJpegAsync(string jpegPath, string outputPath, JxlTranscoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidatePaths(jpegPath, outputPath); options ??= new();
        byte[] input = await ReadFileAsync(jpegPath, options, cancellationToken).ConfigureAwait(false);
        byte[] result = JxlTranscoding.FromJpeg(input, options, cancellationToken);
        await WriteFileAsync(outputPath, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reconstructs the original JPEG file from a JPEG XL file using asynchronous file I/O.</summary>
    public static async Task ToJpegAsync(string jxlPath, string outputPath, JxlTranscoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidatePaths(jxlPath, outputPath); options ??= new();
        byte[] input = await ReadFileAsync(jxlPath, options, cancellationToken).ConfigureAwait(false);
        byte[] result = JxlTranscoding.ToJpeg(input, options, cancellationToken);
        await WriteFileAsync(outputPath, result, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateOutput(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("The stream must be writable.", nameof(output));
    }
    private static void ValidateInput(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead) throw new ArgumentException("The stream must be readable.", nameof(input));
    }
    private static void ValidatePaths(string inputPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
    }
    private static byte[] ReadFile(string path, JxlTranscoderOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        CheckLength(input.Length, options);
        return Read(input, options, cancellationToken);
    }
    private static async Task<byte[]> ReadFileAsync(string path, JxlTranscoderOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (input.ConfigureAwait(false))
        {
            CheckLength(input.Length, options);
            return await ReadAsync(input, options, cancellationToken).ConfigureAwait(false);
        }
    }
    private static void WriteFile(string path, byte[] data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1);
        output.Write(data);
    }
    private static async Task WriteFileAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
        await using (output.ConfigureAwait(false)) await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }
    private static void CheckLength(long length, JxlTranscoderOptions options)
    {
        if (length > options.MaxInputBytes || length > Array.MaxLength) throw JxlDescriptor.Corrupt("Transcoder input exceeds MaxInputBytes or the buffer size limit.");
    }
    private static byte[] Read(Stream input, JxlTranscoderOptions options, CancellationToken cancellationToken)
    {
        ValidateInput(input); cancellationToken.ThrowIfCancellationRequested();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16384];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int size = input.Read(chunk); if (size == 0) return buffer.ToArray();
            CheckLength(buffer.Length + size, options); buffer.Write(chunk, 0, size);
        }
    }
    private static async Task<byte[]> ReadAsync(Stream input, JxlTranscoderOptions options, CancellationToken cancellationToken)
    {
        ValidateInput(input); cancellationToken.ThrowIfCancellationRequested();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16384];
        while (true)
        {
            int size = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false); if (size == 0) return buffer.ToArray();
            CheckLength(buffer.Length + size, options); buffer.Write(chunk, 0, size);
        }
    }
}
