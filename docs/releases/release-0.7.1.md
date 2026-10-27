## Lossless JPEG transcoding

`JxlTranscoder.FromJpeg` archives an original JPEG as JPEG XL using its integer DCT coefficients.
`JxlTranscoder.ToJpeg` reconstructs the original file byte for byte, including its metadata and scan/marker layout.
Both directions interoperate with libjxl 0.12.0's `cjxl`/`djxl`.

- New sync span/stream and async memory/stream APIs, independent `JxlTranscoderOptions` resource limits,
  cooperative cancellation and non-seekable streams; streams remain open.
- Sequential and progressive 8-bit Huffman JPEG, grayscale and YCbCr 4:4:4/4:2:2/4:2:0; exact progressive
  refinement, Huffman tables, restarts, padding, APP/COM and trailing-byte preservation.
- BCL Brotli reconstruction data, canonical ICC/EXIF/XMP mapping, coefficient-only reconstruction without IDCT
  or pixel buffers. Ordinary JPEG XL image reads continue to skip `jbrd`.
- Malformed AC streams whose non-zero count exceeds the remaining coefficients now fail as format errors
  before indexing the entropy context map.

The scalar compressor uses natural coefficient orders and zero chroma correlation; size and speed are measured,
with further optimization deferred. RGB, CMYK/YCCK, arithmetic/12-bit JPEG and other sampling layouts remain
unsupported. Metadata editing of files with reconstruction data remains unsupported.

See [JPEG transcoding](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.7.1/docs/transcoding.md) for API examples, limits and rendering semantics, and
[measurements](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.7.1/docs/transcoding-measurements.md) for size/time/memory comparisons. The library remains compatible
with trimming and Native AOT and has no native runtime dependencies.
