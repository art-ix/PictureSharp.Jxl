## Faster scalar decoding and encoding

First step of the speed work towards 0.8.0, still without SIMD. Output is byte-identical to 0.7.2: decoded pixels,
encoded files and transcoded JPEGs, checked on 1142 decode, encode and transcoding results.

- 1D DCT/IDCT passes transform all columns of a varblock at once instead of one column at a time.
- Dithering, the weighted predictor, edge-preserving filtering, `ToSrgb` colour conversion and lossless RCT selection
  do less work per sample.

Single-thread warm medians compared with 0.7.2 (`photo1` is a 1280×800 photo, `photo12` a 12 MP photo, `screen` a synthetic screenshot):

| Case | 0.7.2 | 0.7.3 | Speed-up |
| --- | ---: | ---: | ---: |
| Lossy decode, photo1, distance 1 | 107 ms | 66 ms | 1.62× |
| Lossy decode, screen, distance 1 | 186 ms | 128 ms | 1.45× |
| Lossy decode, photo12, distance 1 | 1226 ms | 965 ms | 1.27× |
| Lossy decode to sRGB, photo12 | 1295 ms | 954 ms | 1.36× |
| Recompressed JPEG decode | 747 ms | 421 ms | 1.77× |
| Lossy encode, photo1, effort 7 | 2906 ms | 1430 ms | 2.03× |
| Lossy encode, photo1, effort 3 | 126 ms | 99 ms | 1.27× |
| Lossless decode, photo1, effort 3 | 346 ms | 271 ms | 1.28× |
| Lossless decode, screen, effort 7 | 204 ms | 140 ms | 1.46× |
| Lossless encode, photo12, effort 7 | 7746 ms | 6885 ms | 1.13× |

Lossless encoding of the screenshot was within noise. See the [speed measurements](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.7.3/docs/benchmarks/speed.md) for every case and the comparison with libjxl.

No API changes. Also available on [NuGet](https://www.nuget.org/packages/PictureSharp.Jxl/0.7.3):
`dotnet add package PictureSharp.Jxl --version 0.7.3`.
