## Faster VarDCT decoding, less memory

Fourth step of the scalar speed work towards 0.8.0. Output is byte-identical to 0.7.2: decoded pixels, encoded files
and transcoded JPEGs.

- The edge-preserving filter (EPF) runs each stage as its own pixel loop instead of calling a function per pixel.
- Gaborish and the EPF stages filter the image in place, writing each row back once no later row needs it, so
  decoding no longer allocates a second full set of colour planes.
- Gaborish slides its 3x3 window along each row, and the sRGB and PQ output curves are evaluated unrolled.

Single-thread warm medians compared with 0.7.5 on a 12 MP photo: lossy decoding is 1.10× faster, 1.17× with
`ColorConversionMode.ToSrgb`; the 1 MP photo and the screenshot are 1.09–1.12× faster. Peak memory of the 12 MP
decode drops from about 1.1 GB to 0.64 GB. Lossless decoding and encoding are unchanged. See the
[speed measurements](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.7.6/docs/benchmarks/speed.md), including
the approaches that were measured and not kept.

No API changes. Also available on [NuGet](https://www.nuget.org/packages/PictureSharp.Jxl/0.7.6):
`dotnet add package PictureSharp.Jxl --version 0.7.6`.
