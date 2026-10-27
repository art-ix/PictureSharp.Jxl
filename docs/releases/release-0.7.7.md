## Faster lossless encoding

Fifth step of the scalar speed work towards 0.8.0. Output is byte-identical to 0.7.2: decoded pixels, encoded files
and transcoded JPEGs.

- The weighted predictor runs once per pixel: the errors recorded while sampling pixels for tree learning are reused
  when the residuals are tokenized.
- The MA tree is resolved per channel and each row is walked through it together, one level at a time.
- Tree learning quantizes properties by counting and reuses its buffers; ANS encoding divides by exact reciprocals,
  and bit writing and the forward colour transform are faster.

Single-thread warm medians compared with 0.7.2: lossless encoding of a 12 MP photo at effort 7 is 1.82× faster
(about 8.1 s → 4.4 s, against 8.5 s for libjxl), the 1 MP photo 1.7–1.8× faster; the screenshot is unchanged.
Lossy encoding and decoding are unchanged. See the
[speed measurements](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.7.7/docs/benchmarks/speed.md), including
the approaches that were measured and not kept.

No API changes. Also available on [NuGet](https://www.nuget.org/packages/PictureSharp.Jxl/0.7.7):
`dotnet add package PictureSharp.Jxl --version 0.7.7`.
