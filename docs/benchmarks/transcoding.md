# JPEG transcoding measurements — 0.7.1

Measured on 2026-10-27, Linux x64, Intel Core i5-12500H, .NET 10.0.12, libjxl 0.12.0 (`4128790`).
All inputs are generated synthetic images. `cjxl -e 7 --lossless_jpeg=1 --num_threads=1` and `djxl --num_threads=1`
were used; the managed code is scalar with `DOTNET_PROCESSOR_COUNT=1`. These results are measurements, not performance guarantees.

Both directions reconstructed every original JPEG byte for byte. There is no size or speed gate for 0.7.1.

## Method

- Each complete command invocation performs one conversion. Wall time is the median of three invocations and includes process/runtime startup and JIT.
- Peak RSS is measured by `/usr/bin/time` for the median invocation and includes the .NET runtime. It is not a codec-only memory budget.
- The separate warm API time is the median of three calls after one warmup in another process; it excludes process startup and must not be compared directly with CLI wall time.
- The 20 checked-in fixtures include ordinary/progressive JPEG, metadata, restarts, nonstandard padding and multiple AC/LF groups. Eight additional 1024×768 cases use the same deterministic RGB pattern (quality 87) or its grayscale conversion (quality 93).

## Encoded sizes

- 20 conformance fixtures: original JPEG **113,713 B**; this encoder **98,034 B**; `cjxl` **97,754 B** (+0.29% for this encoder).
- 8 images at 1024×768: original JPEG **5,556,426 B**; this encoder **4,338,173 B**; `cjxl` **4,214,291 B** (+2.94% for this encoder).

## Encoding: 1024×768 cases

| JPEG | This JXL / cjxl (B) | Warm API (ms) | CLI wall: .NET / cjxl (ms) | Peak RSS: .NET / cjxl (MiB) |
| --- | ---: | ---: | ---: | ---: |
| 4:4:4 sequential | 807,612 / 769,080 | 294.0 | 516.2 / 82.6 | 127.1 / 43.3 |
| 4:4:4 progressive | 807,515 / 768,983 | 310.3 | 518.9 / 94.2 | 126.3 / 43.4 |
| 4:2:2 sequential | 548,152 / 536,947 | 217.1 | 417.3 / 52.3 | 101.9 / 34.9 |
| 4:2:2 progressive | 548,042 / 536,837 | 234.2 | 449.6 / 59.2 | 102.1 / 35.0 |
| 4:2:0 sequential | 423,110 / 412,825 | 181.7 | 395.1 / 45.1 | 93.0 / 30.6 |
| 4:2:0 progressive | 422,990 / 412,705 | 191.6 | 391.8 / 51.8 | 94.3 / 30.8 |
| Gray sequential | 390,404 / 388,485 | 156.7 | 354.4 / 40.7 | 85.2 / 27.7 |
| Gray progressive | 390,348 / 388,429 | 165.4 | 362.1 / 43.6 | 85.5 / 28.0 |

## Reconstruction: 1024×768 cases

The managed API reads the `cjxl` file; `djxl` reads this encoder's file. Both write exactly the same original JPEG.

| JPEG | Warm API (ms) | CLI wall: .NET / djxl (ms) | Peak RSS: .NET / djxl (MiB) |
| --- | ---: | ---: | ---: |
| 4:4:4 sequential | 147.5 | 332.8 / 45.2 | 67.6 / 11.7 |
| 4:4:4 progressive | 208.7 | 390.5 / 62.3 | 68.4 / 10.7 |
| 4:2:2 sequential | 97.8 | 277.0 / 31.9 | 62.2 / 8.9 |
| 4:2:2 progressive | 160.4 | 325.1 / 44.2 | 61.7 / 8.4 |
| 4:2:0 sequential | 77.6 | 272.1 / 25.2 | 60.0 / 7.8 |
| 4:2:0 progressive | 123.2 | 297.9 / 34.8 | 59.5 / 7.4 |
| Gray sequential | 70.1 | 249.5 / 19.9 | 58.9 / 7.0 |
| Gray progressive | 100.5 | 274.4 / 28.5 | 57.8 / 6.8 |

The compressor currently uses natural AC orders and zero chroma correlation. Its additional size and scalar cost
are accepted for this release; further compression heuristics, SIMD and parallelism are deferred.

## Reproduce

```bash
python3 scripts/measure-transcoding.py
python3 scripts/measure-transcoding.py --synthetic-large
```

The second command needs Pillow 10.2+. Detailed per-file JSON (including managed allocations) is written to ignored
`artifacts/transcoding-measurements/results-transcoding.json` and `results-transcoding-large.json`. The benchmark
project is `benchmarks/PictureSharp.Jxl.Speed`; no native tool is needed by the shipped library.
