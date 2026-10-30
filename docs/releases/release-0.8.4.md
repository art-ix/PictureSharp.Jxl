## Faster lossless decoding

Lossless (Modular) decoding is 1.2–1.9× faster on one thread, and lossless encoding up to 1.5× faster. Decoded
pixels and encoded bytes are identical to 0.8.3; no API changes.

- **Weighted predictor without branches:** its comparisons are computed with sign masks, as libjxl's compiled code
  does with conditional moves. This is the main cost of `cjxl` effort 3–9 files, and the encoder uses it too.
- **Prefix-code rows** (`cjxl -e 1` files) read the bit buffer from locals, with one refill per symbol.
- **Integer samples are written to pixels** through the vectorized row writers.

Speed from 0.8.3 to 0.8.4 (warm medians, Intel Core i5-12500H, 16 logical CPUs, JIT, Linux; `cjxl` files):

| Decode, lossless | Effort | 1 thread: 0.8.3 → 0.8.4 | Speed-up | 16 threads: 0.8.3 → 0.8.4 | Speed-up | libjxl, 1 thread |
| --- | :---: | ---: | ---: | ---: | ---: | ---: |
| 1 MP photo | 3 | 185 → 99 ms | **1.86×** | 28.8 → 19.4 ms | 1.48× | 72 ms |
| 1 MP photo | 7 | 277 → 181 ms | **1.53×** | 38.7 → 32.2 ms | 1.20× | 140 ms |
| 1 MP photo, 16-bit | 7 | 365 → 266 ms | **1.37×** | 50.7 → 39.8 ms | 1.27× | 182 ms |
| Screenshot | 7 | 99 → 78 ms | **1.27×** | 20.2 → 15.1 ms | 1.34× | 57 ms |
| 12 MP photo | 1 | 610 → 458 ms | **1.33×** | 107 → 83 ms | 1.29× | 335 ms |
| 12 MP photo | 2 | 621 → 536 ms | **1.16×** | 103 → 95 ms | 1.09× | 411 ms |
| 12 MP photo | 7 | 3.35 → 2.16 s | **1.55×** | 433 → 303 ms | 1.43× | 1.69 s |
| 12 MP photo, 16-bit | 7 | 4.36 → 3.09 s | **1.41×** | 535 → 397 ms | 1.35× | 2.16 s |

| Encode, lossless | Effort | 1 thread: 0.8.3 → 0.8.4 | Speed-up | 16 threads: 0.8.3 → 0.8.4 | Speed-up |
| --- | :---: | ---: | ---: | ---: | ---: |
| 1 MP photo | 3 | 265 → 182 ms | **1.46×** | 50 → 39 ms | 1.28× |
| 1 MP photo | 7 | 775 → 697 ms | **1.11×** | 203 → 197 ms | 1.03× |
| 12 MP photo | 7 | 4.50 → 3.63 s | **1.24×** | 843 → 751 ms | 1.12× |

Lossy decoding, JPEG transcoding and lossy encoding are unchanged or slightly faster. Photos with real 16-bit content
still decode in about 1.45× the 8-bit time (libjxl: 1.3×), above the 1.1× target: `cjxl` builds much larger trees for
them.

Details: [speed](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.4/docs/benchmarks/speed.md#modular-decoding-084),
[design](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.4/docs/design/0.8.4-modular.md),
[raw data](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.4/docs/measurements/modular-0.8.4.json).

Validation: 526 tests (the new kernels against frozen 0.8.3 code), 1433 golden results identical to 0.8.3 at 1 and
16 threads, identical output on AVX2, SSE4.1 and scalar.

NuGet package: [PictureSharp.Jxl 0.8.4](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.4).
`dotnet add package PictureSharp.Jxl`.
