## Parallel decoding and encoding

Independent groups, tiles and row bands now run on several threads. Output is byte-identical at every thread count
and to 0.8.0: decoded pixels, encoded files, metadata edits, reconstructed JPEGs and exceptions.

- API: `JxlEncoderOptions.MaxDegreeOfParallelism` and `JxlTranscoderOptions.MaxDegreeOfParallelism` (-1 = automatic,
  the default; 1 = single-threaded; other values throw `ArgumentOutOfRangeException`). Decoding uses the core
  `DecoderOptions.MaxDegreeOfParallelism`, so no new decoder option was added.
- Decoding: LF groups, pass groups with their rendering or JPEG coefficient recovery, and row bands of Gaborish,
  EPF, upsampling, colour conversion and pixel writing.
- Encoding: XYB input, adaptive quantization, inverse Gaborish, the 64×64 AC strategy and chroma-from-luma tiles,
  quantization, tokenization and section coding, the EPF sharpness reconstruction, and the Modular tree-sample scans
  and stream tokenization. MA-tree splitting, histograms, entropy codes and section order stay sequential.
- Errors: when several groups fail, the error of the first group in codestream order is thrown, as with one thread;
  cancellation is checked inside every worker. Pixel and metadata limits are charged before any worker starts.
- Also included: SIMD kernels added after 0.8.0 (copy-free DCT leaves, the encoder's 5×5 convolution, EPF sharpness
  block errors, encoder Y dequantization): single-threaded lossy encoding 10–18% and VarDCT decoding up to 6% faster.

Warm medians on an Intel Core i5-12500H (4 P-cores with SMT + 8 E-cores, 16 logical CPUs), JIT, Linux:

| Case | 0.8.0 | 0.8.1, 1 thread | 0.8.1, 16 threads | libjxl, 16 threads |
| --- | ---: | ---: | ---: | ---: |
| VarDCT decode, 1 MP photo | 20.4 ms | 19.7 ms | 10.6 ms | 5.2 ms |
| VarDCT decode, 12 MP photo | 252 ms | 247 ms | 78 ms | 50 ms |
| Lossless decode, 1 MP photo | 265 ms | 269 ms | 37.5 ms | 18.4 ms |
| Lossless decode, 12 MP photo | 3286 ms | 3266 ms | 418 ms | 200 ms |
| Recompressed JPEG decode, 12 MP | 232 ms | 230 ms | 87 ms | 38 ms |
| JPEG reconstruction, 12 MP | 389 ms | 391 ms | 250 ms | 76 ms |
| Lossy encode, 1 MP photo, effort 7 | 549 ms | 450 ms | 122 ms | 53 ms |
| Lossy encode, screenshot, effort 7 | 971 ms | 812 ms | 264 ms | 99 ms |
| Lossy encode, 12 MP photo, effort 7 | 6.57 s | 5.41 s | 1.18 s | 0.56 s |
| Lossless encode, 1 MP photo, effort 7 | 814 ms | 799 ms | 480 ms | 96 ms |
| Lossless encode, 12 MP photo, effort 7 | 4.57 s | 4.57 s | 1.15 s | 1.10 s |

Lossless encoding scales least at 1 MP because tree learning is still sequential. Peak memory grows with the number
of workers (for example 218 → 562 MiB for a 1 MP effort-7 encode); pass `MaxDegreeOfParallelism = 1` for the 0.8.0
behaviour. See the
[speed measurements](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.1/docs/benchmarks/speed.md#group-parallelism-081),
[raw data](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.1/docs/measurements/parallel-0.8.1.json) and the
[parallelism design](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.1/docs/design/0.8.1-parallelism.md).

Validation: 502 Release tests (new: 1 vs N determinism for the corpus, encoding at efforts 1–7 and transcoding;
lowest-failure exceptions, cancellation inside workers, corrupt groups, budgets); 1433 exact golden results at 1 and
16 threads; native, SSE4.1-only and scalar execution; actual-package JIT and trimmed linux-x64 Native AOT consumers.

NuGet package: [PictureSharp.Jxl 0.8.1](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.1).
`dotnet add package PictureSharp.Jxl`.
