# Lossless JPEG transcoding

`JxlTranscoder` archives an existing JPEG as JPEG XL without decoding its pixels. `ToJpeg` reconstructs the original
JPEG **byte for byte**, including progressive scans, Huffman tables, restart markers, metadata, padding, unknown
APP/COM markers and bytes between markers or after EOI. No format registration is needed.

```csharp
using PictureSharp.Formats.Jxl;

var archived = new MemoryStream();
JxlTranscoder.FromJpeg(File.ReadAllBytes("original.jpg"), archived);
File.WriteAllBytes("archived.jxl", archived.ToArray());

var restored = new MemoryStream();
await JxlTranscoder.ToJpegAsync(await File.ReadAllBytesAsync("archived.jxl"), restored, cancellationToken: ct);
File.WriteAllBytes("restored.jpg", restored.ToArray());
```

Both directions accept `Stream` or `ReadOnlySpan<byte>` (`ReadOnlyMemory<byte>` for async) and write to a `Stream`.
Streams stay open and need not be seekable; reading starts at the current position. Async methods use
asynchronous I/O and cooperative cancellation, without `Task.Run`. Input and output are buffered. A write error or
cancellation can leave a partial result, so use a temporary destination if you need atomic replacement.

## Supported JPEGs

- Huffman coding, 8-bit samples, sequential (SOF0/SOF1) or progressive (SOF2), including refinement scans.
- One grayscale component, or three YCbCr components with 4:4:4, 4:2:2 or 4:2:0 sampling.
- Restart intervals, optimized Huffman tables, 8/16-bit quantization entries, canonical split ICC, EXIF/orientation
  and XMP. Original bytes are preserved even when metadata is also exposed to image readers.
- Up to three quantization tables; unused ones must duplicate the preceding table (a libjxl requirement).

Arithmetic coding, 12-bit samples, CMYK/YCCK, direct RGB, other sampling layouts and restart-interval changes within
a file throw `NotSupportedException`; nothing is silently converted through pixels. An invalid/truncated JPEG,
corrupt reconstruction data or an exceeded limit throws `ImageFormatException`; cancellation throws
`OperationCanceledException`.

`FromJpeg` writes a JPEG XL container with a VarDCT coefficient frame and a `jbrd` reconstruction box. ICC is stored
in the codestream, canonical EXIF/XMP in container boxes, and unknown markers and auxiliary bytes in the
reconstruction data. `ToJpeg` also reads `cjxl` 0.12.0 files, including fragmented `jxlp` containers and
Brotli-compressed EXIF/XMP. A JPEG XL file without `jbrd` throws `NotSupportedException`, since the original JPEG
bytes cannot be inferred from pixels. Editing metadata of a file with `jbrd` is unsupported, as it breaks exact
reconstruction.

## Resource limits

Pass a `JxlTranscoderOptions` to either direction:

| Option | Default | Applies to |
| --- | --- | --- |
| `MaxPixels` | 268,435,456 | Stored dimensions, checked before allocating coefficient buffers |
| `MaxInputBytes` | `Array.MaxLength` | Encoded input; streams are checked while buffering |
| `MaxMetadataBytes` | 64 MiB | Metadata and reconstruction structures, including expanded Brotli payloads |

Limits must be positive, except `MaxMetadataBytes`, which may be zero. They do not bound every temporary allocation.

## Interoperability and rendering

Synthetic fixtures cover both directions (this encoder → `djxl`, `cjxl` → this reconstructor) with exact whole-file
comparison. Rendering is compared with `djxl` decoding the **same JXL** within 1/255 per channel; different JPEG
decoders or JXL encoders can render slightly differently while still reconstructing the same JPEG exactly.

The implementation is scalar and uses only the BCL and PictureSharp. It uses natural AC orders and zero chroma
correlation, so size/speed parity with `cjxl -e 7` is not a 0.7.1 goal. See [measurements](transcoding-measurements.md).

Regenerate fixtures with `python3 tests/assets/generate-transcoding.py` (Pillow 10.2+ and pinned libjxl tools). CI
verifies the checked-in fixtures without native tools; `Category=Oracle` tests additionally check against `djxl`.

On Linux, `python3 scripts/measure-transcoding.py [jpeg-directory]` compares against single-threaded `cjxl -e 7` and
writes JSON to ignored `artifacts/transcoding-measurements/`. CLI timings include process startup, so do not compare
them directly with warm .NET API timings.
