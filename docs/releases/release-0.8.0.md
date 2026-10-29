## SIMD decoding and encoding

Vector kernels for x64 (AVX2 with FMA, SSE4.1) with a scalar fallback on every other CPU.
Output is byte-identical to 0.7.10: decoded pixels, encoded files, metadata edits and reconstructed JPEGs.

- DCT and IDCT: 2/4/8/16-point column leaves, 8×8 transforms held in registers, AVX/SSE transposes, the recursive
  steps of larger transforms, AFV, coefficient gathering and 2×2 Haar blocks.
- Decoding: XYB and YCbCr to RGB (sRGB and linear transfer), Gaborish and EPF stages 0–2, dequantization with
  chroma from luma, and Rgba32/Rgba64 pixel writing with the same dithering and rounding (also on SSE4.1).
- Lossy encoding: XYB input with an exact vector port of the correctly rounded cube root, AC strategy entropy
  terms, chroma-from-luma search and AC quantization.
- Kernels that the scalar code fuses (`MathF.FusedMultiplyAdd`) run only with AVX2 + FMA, so results never depend
  on the CPU. Reductions keep their order; no approximate math was introduced. Extra scratch memory is small
  (at most 96 KiB in the AC strategy search, a 16 KiB dither table).

Warm single-threaded medians on an Intel Core i5-12500H (AVX2, FMA), JIT, compared with the 0.7.10 package:

| Case | 0.7.10 | 0.8.0 | Speed-up |
| --- | ---: | ---: | ---: |
| VarDCT decode, 1 MP photo, 8-bit | 62.2 ms | 20.7 ms | 3.01× |
| VarDCT decode, 1 MP photo, 16-bit output | 61.5 ms | 21.4 ms | 2.87× |
| VarDCT decode, screenshot | 115.7 ms | 35.0 ms | 3.31× |
| VarDCT decode, 12 MP photo | 741.7 ms | 250.8 ms | 2.96× |
| Recompressed JPEG decode, 12 MP | 428.2 ms | 225.4 ms | 1.90× |
| Lossy encode, 1 MP photo, effort 3 | 73.2 ms | 52.3 ms | 1.40× |
| Lossy encode, 1 MP photo, effort 7 | 1388 ms | 543 ms | 2.56× |
| Lossy encode, 1 MP photo 16-bit, effort 7 | 1433 ms | 556 ms | 2.58× |
| Lossy encode, screenshot, effort 7 | 2378 ms | 955 ms | 2.49× |
| Lossy encode, 12 MP photo, effort 7 | 17.2 s | 6.6 s | 2.61× |

Lossless (Modular) decoding and encoding and JPEG reconstruction have no SIMD paths and stay within ±1%.
Warm allocations are unchanged (at most 0.3 MiB more per operation); peak RSS is higher in some cases, see the
[speed measurements](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.0/docs/benchmarks/speed.md#simd-080)
and [raw data](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.0/docs/measurements/simd-0.8.0-final.json).

Native AOT compiles hardware checks against its instruction-set baseline: with the default linux-x64 target only
the SSE4.1 kernels run. Publish with `-p:IlcInstructionSet=x86-64-v3` (or `native`) to use AVX2/FMA; such a binary
requires a CPU with those instructions. Speed under AOT, on CPUs without AVX2 and on ARM64 was not measured.

Validation: 486 Release tests, including frozen scalar references for every kernel; 1433 exact golden results
against the 0.7.10 package (efforts 1–9, 8/16-bit, ToSrgb, metadata, JPEG); tests under native, SSE4.1-only and
scalar execution; actual-package JIT, full trimming and linux-x64 Native AOT (default and x86-64-v3) consumer checks.
See the [SIMD design](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.0/docs/design/0.8.0-simd.md).

No API changes. NuGet package: [PictureSharp.Jxl 0.8.0](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.0).
`dotnet add package PictureSharp.Jxl`.
