# Lossless JPEG transcoding

`JxlTranscoder` archives an existing JPEG as JPEG XL without decoding its pixels. `ToJpeg` reconstructs the original
JPEG **byte for byte**, including progressive scans, Huffman tables, restart markers, metadata, padding, unknown
APP/COM markers, inter-marker bytes and bytes after EOI. No format registration is needed for these methods.

```csharp
using PictureSharp.Formats.Jxl;

using (var input = File.OpenRead("original.jpg"))
using (var output = File.Create("archived.jxl"))
    JxlTranscoder.FromJpeg(input, output);

using (var input = File.OpenRead("archived.jxl"))
using (var output = File.Create("restored.jpg"))
    await JxlTranscoder.ToJpegAsync(input, output, cancellationToken: cancellationToken);
```

Both directions accept `Stream` or `ReadOnlySpan<byte>` synchronously, and `Stream` or `ReadOnlyMemory<byte>`
asynchronously. They write to a `Stream`; async methods return `Task`. Streams stay open, reading starts at the
current position, and seeking is not required. Async methods use asynchronous input/output I/O and cooperative
CPU cancellation, without `Task.Run`. The initial implementation buffers input and output. A write error or
cancellation during output can leave a partial result; use a temporary destination if atomic replacement is needed.

## Supported JPEGs

- Huffman coding, 8-bit samples, sequential (SOF0/SOF1) or progressive (SOF2), including refinement scans.
- One grayscale component with 1×1 sampling, or three YCbCr components with 4:4:4, 4:2:2 or 4:2:0 sampling.
- Restart intervals, optimized/redefined Huffman tables, 8/16-bit quantization entries, canonical split ICC,
  EXIF/orientation and XMP. Original bytes are preserved when metadata is also exposed to image readers.
- Up to three quantization tables and the bounded marker/scan structures of libjxl's reconstruction format.
  Unused quantization tables must duplicate the preceding table, as required by that format.

Arithmetic coding, 12-bit samples, CMYK/YCCK, direct RGB, other sampling layouts and changes of restart interval
within a file throw `NotSupportedException`. An unsupported JPEG is never silently converted through pixels.
An invalid/truncated JPEG, corrupt reconstruction data or an exceeded resource limit throws `ImageFormatException`;
cancellation throws `OperationCanceledException`, and stream I/O errors propagate.

`FromJpeg` always writes a JPEG XL container with a VarDCT coefficient frame and a `jbrd` reconstruction box.
ICC is stored in the codestream; canonical EXIF/XMP are stored in container boxes and referenced by reconstruction
metadata. Repeated/unknown markers and auxiliary bytes remain in the reconstruction data. `ToJpeg` also reads
compatible `cjxl` 0.12.0 files, including fragmented `jxlp` containers and Brotli-compressed EXIF/XMP boxes.
A valid JPEG XL file without `jbrd` throws `NotSupportedException`; original JPEG bytes cannot be inferred from pixels.
Editing metadata of a file with `jbrd` remains unsupported because it invalidates exact reconstruction.

## Resource limits

Pass an independent `JxlTranscoderOptions` to either direction:

| Option | Default | Applies to |
| --- | --- | --- |
| `MaxPixels` | 268,435,456 | Stored dimensions, checked before allocating coefficient buffers |
| `MaxInputBytes` | `Array.MaxLength` | Encoded input; streams are checked while buffering |
| `MaxMetadataBytes` | 64 MiB | Metadata and reconstruction structures, including expanded Brotli payloads |

Limits must be positive except `MaxMetadataBytes`, which may be zero. They bound input, pixels and metadata;
they do not bound every temporary allocation or promise constant-memory streaming.

## Interoperability and rendering

Synthetic fixtures cover both directions: this encoder → `djxl` and `cjxl` → this reconstructor, with exact whole-file
comparison. Image decoding is compared with `djxl` decoding the **same JXL** within 1/255 per channel. Different JPEG
decoders can round IDCT/chroma conversion differently. Different JXL encoders can choose different coefficient
correlation models and thus render slightly differently while still reconstructing exactly the same original JPEG.

The implementation is scalar and uses only the BCL and PictureSharp at runtime. JPEG coefficient preparation shares
the VarDCT section/entropy writer, with effort-7 Modular prediction. It currently uses natural AC orders and zero
chroma correlation; it does not implement every compression heuristic of `cjxl -e 7`. Size/speed parity is not a
0.7.1 acceptance condition. See [measurements](transcoding-measurements.md) for measured size, time and memory.

Regenerate public synthetic fixtures with `python3 tests/assets/generate-transcoding.py` (Pillow 10.2+ and pinned
libjxl tools); the main corpus generator invokes it too. Checked-in JPEG/JXL/PNG fixtures let CI verify reconstruction
and rendering without native tools. Live `Category=Oracle` checks additionally verify this encoder with `djxl`.

Run `python3 scripts/measure-transcoding.py [jpeg-directory]` on Linux to compare against `cjxl -e 7`, one thread.
The script writes detailed JSON to ignored `artifacts/transcoding-measurements/`; native tools are development
oracles only. Peak RSS and complete command timings include runtime/startup. Warm .NET API timings and allocations
are recorded separately and must not be compared directly with CLI wall times.
