## Faster JPEG reconstruction and lossless encoding

0.8.2 removes the main sequential stages left after 0.8.1 and shrinks per-thread encoder memory. Output is
byte-identical to 0.8.1 at every thread count: decoded pixels, encoded files, metadata edits, reconstructed JPEGs
and exceptions. No API changes.

- JPEG reconstruction: bits are collected in a 64-bit accumulator instead of written one at a time. DC-first scans
  (`Ss = 0`, `Ah = 0`) are encoded in independent chunks of restart intervals or MCU rows, then merged in order
  with byte stuffing, padding and restart markers. Corrupt input reports the same first error as with one thread.
- Lossless encoding: MA-tree learning evaluates split properties, and later whole subtrees, in parallel; decisions
  are replayed in sequential order, so the tree and its node numbering are unchanged. RCT candidates are costed and
  palette colours collected in parallel.
- Lossy encoding: AC strategy search, chroma-from-luma and quantizer scratch are sized for the encoder's largest
  transform (DCT64×64, 4096 coefficients) instead of the decoder's DCT256×256, avoiding large per-worker allocations.

Warm medians, v0.8.1 vs 0.8.2, Intel Core i5-12500H (4 P-cores with SMT + 8 E-cores, 16 logical CPUs), JIT, Linux:

| Case | 0.8.1, 1 thread | 0.8.2, 1 thread | 0.8.1, 16 threads | 0.8.2, 16 threads |
| --- | ---: | ---: | ---: | ---: |
| JPEG reconstruction, 12 MP | 398 ms | 316 ms | 256 ms | 61 ms |
| Lossless encode, 1 MP photo, effort 7 | 811 ms | 797 ms | 495 ms | 215 ms |
| Lossless encode, screenshot, effort 7 | 482 ms | 486 ms | 371 ms | 168 ms |
| Lossless encode, 1 MP 16-bit photo, effort 7 | 1096 ms | 1071 ms | 760 ms | 273 ms |
| Lossless encode, 12 MP photo, effort 7 | 4.68 s | 4.69 s | 1.20 s | 0.93 s |
| Lossy encode, 1 MP photo, effort 3 | 46.9 ms | 47.2 ms | 28.0 ms | 25.8 ms |
| Lossy encode, 1 MP photo, effort 7 | 451 ms | 447 ms | 126 ms | 123 ms |

At 16 threads a 1 MP effort-7 lossy encode allocates 45 MiB instead of 168 MiB (peak RSS 529 → 251 MiB). JPEG
reconstruction keeps each chunk's bytes until the merge, so its 16-thread peak RSS grows (251 → 442 MiB for 12 MP);
pass `MaxDegreeOfParallelism = 1` to avoid it. Decoding is unchanged. See the
[speed measurements](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.2/docs/benchmarks/speed.md#scheduling-and-scratch-082),
[raw data](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.2/docs/measurements/scheduling-0.8.2.json) and the
[scheduling design](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.2/docs/design/0.8.2-scheduling.md).

Validation: 504 Release tests (new: JPEG reconstruction identity and error order at 1/2/3/7/64/-1 threads,
palette limit boundaries); 1433 exact golden results at 1 and 16 threads; native, SSE4.1-only and scalar execution;
actual-package JIT and trimmed linux-x64 Native AOT consumers.

NuGet package: [PictureSharp.Jxl 0.8.2](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.2).
`dotnet add package PictureSharp.Jxl`.
