## Less memory for large images

Large decodes, encodes and JPEG reconstruction need less memory and allocate less. Decoded pixels and encoded bytes are
identical to 0.8.5; no API changes.

- **JPEG-recompressed images** (4:2:0 chroma) are upsampled and converted to RGB per row band while the pixels are
  written, without full-resolution chroma planes; also 1.9× faster with 16 threads.
- **Lossless encoding** applies the colour transform to its group copies, records the predictor errors in the token
  storage and sizes that storage once.
- **Lossy encoding** releases its working image before the sharpness search reuses the memory.
- **Modular decoding** and **JPEG reconstruction** no longer allocate per block or per histogram in their inner paths.

Per 12 MP image, from 0.8.5 to 0.8.6 (peak RSS of one operation with a full collection before it; Intel Core
i5-12500H, 16 logical CPUs, JIT, Linux):

| Case | Allocated, 1 thread | Peak RSS, 1 thread | Peak RSS, 16 threads | Time, 16 threads |
| --- | ---: | ---: | ---: | ---: |
| Decode JPEG-recompressed | 244 → 53 MB | 332 → 180 MiB | 375 → 235 MiB | 82.8 → 45.2 ms |
| JPEG reconstruction | 118 → 38 MB | 143 → 132 MiB | 197 → 193 MiB | 57.5 → 53.3 ms |
| Encode lossless e7 | 976 → 576 MB | 1192 → 864 MiB | 1593 → 1035 MiB | 743 → 743 ms |
| Encode lossy d1 e7 | 376 → 282 MB | 959 → 817 MiB | 994 → 847 MiB | 1093 → 1091 ms |
| Decode 16-bit lossless e7 | 341 → 170 MB | 369 → 351 MiB | 441 → 442 MiB | 434 → 398 ms |

Other cases are within ±3% in time. The decoded image's pixel buffer (48 MB for 12 MP RGBA) is allocated by
PictureSharp; in applications that decode many images, the process peak also depends on when the GC collects them.

The next releases, 0.8.7 to 0.8.10, cover fuzzing and resilience.

Details: [speed](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.6/docs/benchmarks/speed.md#memory-use-086),
[design](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.6/docs/design/0.8.6-memory.md),
[raw data](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.6/docs/measurements/memory-0.8.6.json).

Validation: 529 tests, 1469 golden results identical to 0.8.5 at 1 and 16 threads, identical output on AVX2, SSE4.1
and scalar.

NuGet package: [PictureSharp.Jxl 0.8.6](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.6).
`dotnet add package PictureSharp.Jxl`.
