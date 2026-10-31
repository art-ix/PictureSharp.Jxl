## Working memory bounded by MaxPixels, faster cancellation of encodes

0.8.9 checks what a decode, encode or transcode costs and how it ends when it is stopped or its stream fails. It
found that the working memory of a decode was not bounded, and that cancelling an encode could take over 100 ms;
both are fixed. No API changes. Decoded pixels and encoded bytes are identical to 0.8.8.

- **Working memory.** `MaxPixels` bounded only the returned image. A JPEG XL header may declare up to 4096 extra
  channels, and each is decoded at full size: a valid file of 1369 bytes (2048×2048, 256 extra channels) asked for
  about 4 GiB, and one of 374 bytes for 282 MiB while its image was 4 MiB. Decoding and `ToJpeg` now charge every
  buffer they allocate against 64 bytes per allowed pixel plus 64 MiB, and throw `ImageFormatException` beyond it.
  The largest decode measured (12 MP, 16-bit lossless) needs 36 bytes per pixel. At the default `MaxPixels` (2^28)
  the budget is 16 GiB; lower `MaxPixels` to bound memory further.
- **`MaxFrames`** now counts every frame read, including reference and DC frames that are not displayed.
- **Cancellation of encodes.** Two stages ran without checking the token: chroma statistics before a lossy encode,
  and the token histograms of a lossless encode. A 12 MP encode now stops within 35 ms of the cancellation (up to
  129 ms before); decoding and transcoding stop within 10 ms.

New fuzz targets, for development only and not part of the package
([design and results](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.9/docs/design/0.8.9-resilience.md)):

- **Returned buffers:** in the fuzzing build every rental is counted, and each input must return all of them, on
  success and on every error.
- **Cancellation:** decoding and transcoding are cancelled at chosen allocations, reproducibly; the test corpus was
  cancelled at each of its 28,000 allocation points. Every run either finished with the full result or threw
  `OperationCanceledException`, with all buffers returned.
- **Failing streams:** short reads, failing reads and writes failing after any byte, sync and async: the
  `IOException` reaches the caller unchanged and the streams stay open.
- **Encoder:** random sizes up to 2048, 8 and 16 bits, alpha, gray, ICC, EXIF, XMP, quality, distance and effort.
  Each image encodes to the same bytes at 1 and 4 threads and on reused buffers, lossless images decode exactly,
  and metadata comes back. Metadata editing is fuzzed on the corpus.

About 190 million inputs on an x86 PC, 5 minutes per target: 28 million for the new targets, the rest the 0.8.7
targets run again with buffer counting. Apart from the two fixes above they found no defect in this library. Damaged EXIF makes `EditMetadata` throw in PictureSharp itself, before this plugin is called, for every
format.

Validation: 547 tests, 1542 golden results at 1 and 16 threads: the 1524 from 0.8.8 identical, plus 18 for the new
regression inputs. The package passes the JIT and Native AOT consumer checks.

NuGet package: [PictureSharp.Jxl 0.8.9](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.9).
`dotnet add package PictureSharp.Jxl`.
