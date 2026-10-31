## Working memory bounded by MaxPixels, faster cancellation of encodes

0.8.9 checks what a decode, encode or transcode costs and how it ends when stopped or when its stream fails. Two
problems were found and fixed. No API changes. Decoded pixels and encoded bytes are identical to 0.8.8.

- **Working memory.** `MaxPixels` bounded only the returned image, while a header may declare up to 4096 extra
  channels, each decoded at full size: a valid 1369-byte file asked for about 4 GiB. Decoding and `ToJpeg` now charge
  every buffer against 64 bytes per allowed pixel plus 64 MiB and throw `ImageFormatException` beyond it. The largest
  decode measured needs 36 bytes per pixel. At the default `MaxPixels` (2^28) this allows 16 GiB; lower `MaxPixels`
  to bound memory further.
- **`MaxFrames`** now counts every frame read, including reference and DC frames.
- **Cancellation of encodes.** Two encoder stages did not check the token. A 12 MP encode now stops within 35 ms
  (up to 129 ms before); decoding and transcoding stop within 10 ms.

New fuzz targets, for development only
([design and results](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.9/docs/design/0.8.9-resilience.md)):

- **Returned buffers:** every rental is counted and must be returned, on success and on every error.
- **Cancellation** at chosen allocations, reproducibly; the test corpus at each of its 28,000 allocation points.
- **Failing streams:** short and failing reads, writes failing after any byte, sync and async; the `IOException`
  reaches the caller unchanged.
- **Encoder:** random sizes, bit depths, alpha, metadata and settings; the same bytes at 1 and 4 threads, exact
  lossless round trips. Metadata editing is fuzzed on the corpus.

About 190 million inputs on an x86 PC found no other defect in this library. Damaged EXIF makes `EditMetadata`
throw in PictureSharp itself, for every format.

Validation: 547 tests, 1542 golden results at 1 and 16 threads (the 1524 from 0.8.8 identical, plus 18 new). The
package passes the JIT and Native AOT consumer checks.

NuGet package: [PictureSharp.Jxl 0.8.9](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.9).
`dotnet add package PictureSharp.Jxl`.
