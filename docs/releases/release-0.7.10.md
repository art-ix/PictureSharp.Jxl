## Fewer allocations and less temporary memory

Seventh step of the scalar speed work towards 0.8.0, for 8-bit and 16-bit images.
Output is byte-identical to 0.7.9: decoded pixels, encoded files, metadata edits and transcoded JPEGs.

- Working channels, group coefficients, predictor rows, tokens and tree samples reuse pooled buffers,
  released after success, cancellation or failure.
- The encoder writes prepared sections directly to the output stream, without a complete codestream copy.
  The existing TOC, container layout and padding are preserved, including on non-seekable streams.
- Eligible lossless frames write integer channels directly to Rgba32/Rgba64, with the same normalization,
  dithering and rounding. The 16-bit writer has separate gray/RGB and alpha/opaque loops.
- Tree learning clears only active histogram ranges or cells, and XYB-to-sRGB conversion avoids per-sample
  transfer dispatch. These changes preserve the previous arithmetic and compression decisions.

Warm allocations per operation compared with 0.7.9, including the output image or encoded byte array:

| Case (1 MP photo, effort 7) | 0.7.9 | 0.7.10 | Fewer allocated bytes |
| --- | ---: | ---: | ---: |
| Lossless decode, 8-bit | 54.0 MiB | 10.6 MiB | 80.3% |
| VarDCT decode, 8-bit | 32.0 MiB | 5.6 MiB | 82.6% |
| Lossless decode, 16-bit | 64.7 MiB | 28.8 MiB | 55.5% |
| VarDCT decode, 16-bit | 35.9 MiB | 9.5 MiB | 73.5% |
| Lossless encode, 8-bit | 224.4 MiB | 43.0 MiB | 80.8% |
| Lossy encode, 8-bit | 143.5 MiB | 29.7 MiB | 79.3% |
| Lossless encode, 16-bit | 250.2 MiB | 54.8 MiB | 78.1% |
| Lossy encode, 16-bit | 143.6 MiB | 29.9 MiB | 79.2% |

All 20 main photo and screenshot cases, including 12 MP, allocate at least 50% fewer bytes (50.3–89.1%).
Peak RSS is measured separately: the 12 MP 16-bit lossy encode falls from 4856 to 3444 MiB, while some cases
retain more pooled memory and have a higher process peak despite allocating fewer bytes.

Controlled single-core 1 MP medians show decoding about 2–11% faster. Encoding stays within about 1% of
0.7.9, apart from the roughly 3% faster 8-bit lossless case. See the
[speed and memory measurements](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.7.10/docs/benchmarks/speed.md)
for GC counts, RSS, timing methodology and the approaches measured and not kept.

Validation: 474 Release tests, 1283 exact golden results against the 0.7.9 package, and actual-package
JIT, full trimming and linux-x64 Native AOT consumer checks.

No API changes. NuGet package: [PictureSharp.Jxl 0.7.10](https://www.nuget.org/packages/PictureSharp.Jxl/0.7.10).
`dotnet add package PictureSharp.Jxl`.
