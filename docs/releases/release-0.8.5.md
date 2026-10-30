## Lower per-image cost

Small images encode up to 2× and decode up to 1.5× faster, and the default parallelism no longer allocates scratch
for workers that get no work when several images are processed at once. Decoded pixels and encoded bytes are identical
to 0.8.4; no API changes.

- **RCT search** (lossless encoding, efforts 5–9): each distinct transformed channel is costed once instead of once per
  candidate, with the same choice.
- **Quantization tables** of the library defaults are computed once per process and shared.
- **Coefficient orders** in the lossy encoder cover only the transforms the frame uses: a 64×64 lossy encode allocates
  2.3 MB instead of 7.5 MB, without gen-2 collections.
- **ICC output target** is parsed only for images whose colour encoding is an ICC profile.
- **Per-worker scratch** is created when a worker takes its first item: 16 concurrent 256×256 lossy encodes with the
  default parallelism allocate 5.8 MB per image instead of 13.2 MB.

Speed from 0.8.4 to 0.8.5 (warm medians, Intel Core i5-12500H, 16 logical CPUs, JIT, Linux; `cjxl` files and crops of
a 12 MP photo):

| Case | 1 thread: 0.8.4 → 0.8.5 | Speed-up | 16 threads: 0.8.4 → 0.8.5 | libjxl, 1 thread |
| --- | ---: | ---: | ---: | ---: |
| Encode 64×64, lossless e7 | 5.12 → 2.58 ms | **1.98×** | 2.70 → 1.83 ms | 2.5 ms |
| Encode 256×256, lossless e7 | 96.6 → 56.1 ms | **1.72×** | 41.0 → 34.1 ms | 45 ms |
| Encode 64×64, lossy d1 | 3.73 → 2.57 ms | **1.45×** | 3.84 → 2.76 ms | 2.7 ms |
| Encode 256×256, lossy d1 | 27.5 → 26.1 ms | 1.05× | 15.1 → 13.9 ms | 13.7 ms |
| Decode 64×64, lossless | 0.507 → 0.340 ms | **1.49×** | 0.574 → 0.358 ms | 0.27 ms |
| Decode 64×64, lossy | 0.174 → 0.143 ms | **1.22×** | 0.186 → 0.162 ms | 0.20 ms |
| Decode 256×256, lossy | 1.41 → 1.34 ms | 1.05× | 1.78 → 1.37 ms | 0.91 ms |
| Encode 1 MP photo, lossless e7 | 717 → 663 ms | 1.08× | 197 → 201 ms | 624 ms |
| Encode 1 MP photo, lossy d1 e7 | 405 → 400 ms | 1.01× | 112 → 120 ms | 205 ms |
| Decode 1 MP photo, lossless e7 | 184 → 183 ms | 1.00× | 27.1 → 27.4 ms | 133 ms |
| Decode 1 MP photo, lossy | 18.7 → 17.9 ms | 1.04× | 10.6 → 10.5 ms | 12.7 ms |

Other large-image cases are within ±1.4% on one thread. Some 16-thread single runs were slower (up to +22% for a 15 ms
16-bit screenshot decode) on code paths that did not change; they were not rechecked.

0.8.5 closes the speed work. The next release, 0.8.6, reduces memory use of large decodes, encodes and transcodes;
fuzzing and resilience follow from 0.8.7. This release's benchmark matrix and golden results are their baseline.

Details: [speed](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.5/docs/benchmarks/speed.md#consolidation-085),
[design](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.5/docs/design/0.8.5-consolidation.md),
[raw data](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.5/docs/measurements/consolidation-0.8.5.json).

Validation: 527 tests, 1433 golden results identical to 0.8.4 at 1 and 16 threads (1469 with the new small inputs),
identical output on AVX2, SSE4.1 and scalar.

NuGet package: [PictureSharp.Jxl 0.8.5](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.5).
`dotnet add package PictureSharp.Jxl`.
