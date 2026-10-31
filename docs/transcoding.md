# Lossless JPEG transcoding

`JxlTranscoder` stores an existing JPEG as JPEG XL without decoding its pixels, and turns it back into the
**byte-identical** original JPEG. Quality never changes: the JPEG's own DCT coefficients are kept, together with its
metadata and marker layout. No format registration is needed.

## Usage

```csharp
using PictureSharp.Formats.Jxl;

// JPEG -> JPEG XL (14–22% smaller on the measured samples)
JxlTranscoder.FromJpeg("photo.jpg", "photo.jxl");

// JPEG XL -> the original JPEG, byte for byte
JxlTranscoder.ToJpeg("photo.jxl", "photo-restored.jpg");
```

Every method has an async variant and also accepts streams or in-memory buffers:

```csharp
// Async with cancellation
await JxlTranscoder.FromJpegAsync("photo.jpg", "photo.jxl", cancellationToken: ct);

// Streams (left open; need not be seekable; reading starts at the current position)
using var input = File.OpenRead("photo.jxl");
using var output = new MemoryStream();
JxlTranscoder.ToJpeg(input, output);

// Buffers
byte[] jpeg = File.ReadAllBytes("photo.jpg");
JxlTranscoder.FromJpeg(jpeg, output);

// Limits for untrusted input
var options = new JxlTranscoderOptions { MaxInputBytes = 50 * 1024 * 1024 };
JxlTranscoder.FromJpeg("upload.jpg", "upload.jxl", options);
```

Path overloads write the output file only after transcoding succeeds, so a failed or cancelled call leaves an existing
file untouched. Stream overloads write directly, so a failure there can leave a partial result.

## What is supported

| Supported | Not supported (`NotSupportedException`) |
| --- | --- |
| Baseline and progressive JPEG, Huffman coding, 8-bit | Arithmetic coding, 12-bit samples |
| Grayscale, or YCbCr 4:4:4 / 4:2:2 / 4:2:0 | CMYK/YCCK, direct RGB, other subsampling |
| ICC, EXIF, XMP, unknown markers, trailing bytes | Restart interval changing within a file |

`ToJpeg` also reads files produced by `cjxl` 0.12.0. It needs the JPEG reconstruction data (`jbrd` box) that
`FromJpeg` and `cjxl` write; an ordinary JPEG XL image without it throws `NotSupportedException`, because the original
JPEG cannot be recreated from pixels. Do not edit metadata of a transcoded file, as that breaks exact reconstruction.

Nothing is ever silently converted through pixels: if a JPEG cannot be preserved exactly, the call fails.

## Errors

| Exception | Cause |
| --- | --- |
| `NotSupportedException` | Valid input using an unsupported feature (see above) |
| `ImageFormatException` | Corrupt or truncated input, or an exceeded limit |
| `OperationCanceledException` | Cancellation |
| `FileNotFoundException`, `IOException` | File or stream I/O failure |

## Limits

| `JxlTranscoderOptions` | Default | Limits |
| --- | --- | --- |
| `MaxInputBytes` | `Array.MaxLength` | Size of the encoded input |
| `MaxPixels` | 268,435,456 | Image dimensions, checked before allocation |
| `MaxMetadataBytes` | 64 MiB | Metadata and reconstruction data, including decompressed Brotli |

Input is fully buffered in memory, and these limits do not bound every temporary allocation.

## Compatibility

Round trips are verified against libjxl 0.12.0 in both directions (this library → `djxl`, `cjxl` → this library)
with whole-file comparison, also on mutated inputs (docs/design/0.8.8-differential.md). Where the two differ, this
library is stricter on purpose:

- `FromJpeg` rejects JPEGs it cannot reconstruct exactly: DC values beyond ±2047 (`cjxl` accepts them, but `djxl`
  then fails or writes other bytes), and some unusual scan scripts or ICC chunk sequences that `cjxl` transcodes.
- `ToJpeg` rejects reconstruction data that describes an invalid JPEG (undefined tables, scan parameters,
  coefficients out of range, APP markers that do not match the metadata), where `djxl` writes whatever the data
  says; and files whose container is damaged (see [decoding](decoding.md#differences-from-libjxl)).

A JPEG this library transcodes always reconstructs byte for byte with both `ToJpeg` and `djxl`, also when `cjxl`
refuses it. Output is not tuned for size or speed parity with `cjxl -e 7`; see
[measurements](benchmarks/transcoding.md). Test fixtures are regenerated with
`python3 tests/assets/generate-transcoding.py`.
