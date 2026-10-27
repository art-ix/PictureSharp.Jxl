Lossy encoding for PictureSharp 0.8.5 (.NET 10 / C# 14).

**Behaviour change:** `SaveAsJxl`, `ToJxl` and `image.Save("x.jxl")` now write lossy VarDCT at butteraugli distance
1.0 and effort 7, as `cjxl` does for non-JPEG input. Pass `new JxlEncoderOptions { Quality = 100 }` or
`Distance = 0` for the lossless output of 0.6.x, which is unchanged byte for byte.

- **Options:** `Quality` (0–100, `cjxl -q` scale), `Distance` (0–25, takes precedence), `Effort` (1–9; 8 and 9
  behave as 7 for now) and `EffectiveDistance`.
- **Encoder:** a scalar port of libjxl 0.12.0's VarDCT encoder: XYB conversion, adaptive quantization, inverse
  Gaborish, chroma-from-luma, transform search over all 27 strategies, nonlinear DC, custom coefficient orders,
  edge-preserving filter sharpness, chromacity adjustments, 2× downsampling from distance 10, and patches for
  repeated shapes at effort 7+.
- **Alpha** is stored losslessly; the colour of invisible pixels is simplified. Lookup-table ICC profiles and HLG
  images fall back to lossless.
- **Size and quality** match `cjxl` at the same distance and effort on photos (within about 1%, equal ssimulacra2);
  large text images are much smaller because `cjxl` skips patches in streaming mode.

Verification: all tests, djxl oracle tests and a randomized sweep (sizes 1–1030, gray/RGB with alpha, distances
0.1–25, efforts 1–9) decoding within 1 of djxl with alpha exact, lossless output identical to 0.6.1, package consumer
under JIT and trimmed Native AOT. See the
[encoding guide](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.7.0/docs/encoding.md).

Packages are attached to this private release only, not published to a package registry.
