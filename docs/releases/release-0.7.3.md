# 0.7.3 — faster scalar decoding and encoding

First step of the speed work towards 0.8.0, still without SIMD. Output is byte-identical to 0.7.2: decoded pixels,
encoded files and transcoded JPEGs, checked on 1142 decode, encode and transcoding results.

- 1D DCT/IDCT passes transform all columns of a varblock at once instead of one column at a time.
- Dithering, the weighted predictor, edge-preserving filtering, `ToSrgb` colour conversion and lossless RCT selection
  do less work per sample.

Single-thread warm medians compared with 0.7.2 on a 1280×800 photo: lossy decoding is about 1.6× faster and lossy
encoding at effort 7 about 2× faster. 12 MP lossy decoding (`ToSrgb`) is about 1.4× faster. Lossless decoding and
encoding gain up to 1.4× and 1.1× respectively. See the
[speed measurements](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.7.3/docs/benchmarks/speed.md) for every case
and the comparison with libjxl.

No API changes. Also available on [NuGet](https://www.nuget.org/packages/PictureSharp.Jxl/0.7.3):
`dotnet add package PictureSharp.Jxl --version 0.7.3`.
