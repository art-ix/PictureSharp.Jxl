# 0.7.4 — faster lossless decoding

Second step of the scalar speed work towards 0.8.0. Output is byte-identical to 0.7.2: decoded pixels, encoded files
and transcoded JPEGs.

- Modular trees without decisions (as `cjxl -e 1` writes) predict directly, without computing pixel properties.
- Trees that decide on a single property (the gradient at effort 2, the weighted predictor's error at effort 3) find
  the leaf in a lookup table instead of walking the tree.
- Tree nodes are packed into one array; the weighted predictor keeps its four sub-predictions in fields and updates
  their errors without a loop.
- ANS alias table entries are packed into 8 bytes, and the symbol reader has a shorter path for the common case
  without LZ77.

Single-thread warm medians compared with 0.7.3: lossless decoding is 1.2–1.5× faster for files from `cjxl -d 0`
at efforts 1–3 and for a synthetic screenshot, and about 1.05× at effort 7, where the tree walk's data-dependent
branches dominate. Lossy decoding and encoding are unchanged. See the
[speed measurements](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.7.4/docs/benchmarks/speed.md), including
the effort 7 approaches that were measured and not kept.

No API changes. Also available on [NuGet](https://www.nuget.org/packages/PictureSharp.Jxl/0.7.4):
`dotnet add package PictureSharp.Jxl --version 0.7.4`.
