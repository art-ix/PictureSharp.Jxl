## Faster VarDCT decoding, less memory

Fourth step of the scalar speed work towards 0.8.0. Output is byte-identical to 0.7.2: decoded pixels, encoded files
and transcoded JPEGs.

- The edge-preserving filter (EPF) runs each stage as its own pixel loop instead of calling a function per pixel.
- Gaborish and the EPF stages filter the image in place, writing each row back once no later row needs it, so
  decoding no longer allocates a second full set of colour planes.
- Gaborish slides its 3x3 window along each row, and the sRGB and PQ output curves are evaluated unrolled.

Single-thread warm medians of lossy decoding compared with 0.7.5:

| Case | 0.7.5 | 0.7.6 | Speed-up |
| --- | ---: | ---: | ---: |
| photo12, distance 1 | 720 ms | 652 ms | 1.10× |
| photo12, distance 1, `ToSrgb` | 844 ms | 722 ms | 1.17× |
| photo1, distance 1 | 75 ms | 69 ms | 1.09× |
| screen, distance 1 | 138 ms | 123 ms | 1.12× |

Peak memory of the 12 MP decode drops from about 1.1 GB to 0.64 GB (allocations 528 → 381 MB). Lossless decoding
and encoding are unchanged. See the [speed measurements](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.7.6/docs/benchmarks/speed.md), including the approaches that were measured and not kept.

No API changes. Also available on [NuGet](https://www.nuget.org/packages/PictureSharp.Jxl/0.7.6):
`dotnet add package PictureSharp.Jxl --version 0.7.6`.
