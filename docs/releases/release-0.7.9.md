## Faster lossy encoding

Sixth step of the scalar speed work towards 0.8.0, for 8-bit and 16-bit input alike.

- Chroma from luma computes each coefficient's residual once per search and flips signs without a branch.
- The AC strategy search estimates entropy without recomputing constants and without data-dependent branches.
- The 8-point DCT and IDCT read and write their rows directly, without copy and scaling passes (also used by decoding).
- Encoding reads the image's pixel buffer instead of copying it first (48 MB less for a 12 MP 8-bit image,
  96 MB for 16-bit).
- The XYB conversion uses a correctly rounded cube root, ported from
  [CORE-MATH](https://core-math.gitlabpages.inria.fr/) (MIT): 2.5× faster than `MathF.Cbrt` and the same result on
  every platform.

Single-thread warm medians, pinned to one core, alternating with the 0.7.8 package:

| Case (1 MP photo unless noted) | 0.7.8 | 0.7.9 | Speed-up |
| --- | ---: | ---: | ---: |
| Lossy encode, distance 1, effort 1 | 96 ms | 79 ms | 1.22× |
| Lossy encode, distance 1, effort 3 | 96 ms | 79 ms | 1.22× |
| Lossy encode, distance 1, effort 7 | 1692 ms | 1423 ms | 1.19× |
| Lossy encode, 16-bit input, effort 7 | 1700 ms | 1421 ms | 1.20× |
| Lossy encode, screenshot, effort 7 | 2413 ms | 2369 ms | 1.02× |
| Lossless encode, effort 7 | 811 ms | 813 ms | 1.00× |
| VarDCT decode, distance 1 | 66 ms | 63 ms | 1.04× |
| VarDCT decode, screenshot | 125 ms | 118 ms | 1.06× |

See the [speed measurements](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.7.9/docs/benchmarks/speed.md),
including the approaches that were measured and not kept.

Decoded pixels, lossless files and JPEG transcoding are byte-identical to 0.7.8. Lossy files can differ in a few
bytes: the previous cube root came from the platform's C library, which on Linux is not correctly rounded. Sizes
change by less than 0.2 % (−0.006 % in total) and quality is unchanged (ssimulacra2 within ±0.04); every file
decodes with djxl.

No API changes. Also available on [NuGet](https://www.nuget.org/packages/PictureSharp.Jxl/0.7.9):
`dotnet add package PictureSharp.Jxl`.
