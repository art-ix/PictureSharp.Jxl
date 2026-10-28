## Faster Modular decoding

Third step of the scalar speed work towards 0.8.0. Output is byte-identical to 0.7.2: decoded pixels, encoded files
and transcoded JPEGs.

- Modular trees without decisions (as `cjxl -e 1` writes) read the residuals of a whole row first, with prefix codes
  and LZ77 runs decoded in one loop, and then predict the row.
- Trees where every leaf uses the gradient predictor and the tree splits on the gradient (`cjxl -e 2`) have their own
  row loop.
- Both prediction loops are compiled fully optimised from the start; the clamped gradient no longer branches, and the
  bit reader no longer counts its position on every read.

Single-thread warm medians of lossless decoding compared with 0.7.4:

| Case | 0.7.4 | 0.7.5 | Speed-up |
| --- | ---: | ---: | ---: |
| photo12, cjxl -d 0 -e 1 | 833 ms | 621 ms | 1.34× |
| photo12, cjxl -d 0 -e 2 | 813 ms | 715 ms | 1.14× |
| photo1, cjxl -d 0 -e 7 | 282 ms | 263 ms | 1.07× |
| photo12, cjxl -d 0 -e 7 | 2978 ms | 2921 ms | 1.02× |

The smaller effort 3 and screenshot cases stay within noise. Lossy decoding and encoding are unchanged. See the [speed measurements](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.7.5/docs/benchmarks/speed.md), including the approaches that were measured and not kept.

No API changes. Also available on [NuGet](https://www.nuget.org/packages/PictureSharp.Jxl/0.7.5):
`dotnet add package PictureSharp.Jxl --version 0.7.5`.
