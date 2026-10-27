Conversion to sRGB without double quantization, for PictureSharp 0.8.5 (.NET 10 / C# 14).

- With `ColorConversion = ToSrgb`, lossy (XYB) images are rendered straight to sRGB in the floating-point
  pipeline, as `djxl --color_space=sRGB` does, and quantized to 8 bits once. In 0.5.0 they were quantized
  in their ICC profile and then converted by PictureSharp, which near the edge of sRGB differed from djxl
  by several levels (up to 16 in dark channels of wide-gamut photos).
- This also covers ICC profiles with lookup tables and enumerated colour spaces such as Display P3; gray
  images are rendered as gray with the sRGB curve. Colours outside sRGB are clipped, as in djxl.
- The converted image has no ICC profile or colour description; `Image.ReadMetadata` still reports the
  file's profile. Without `ToSrgb` nothing changes.
- Images coded without XYB (lossless images, recompressed JPEGs) keep their colour space and are converted
  by PictureSharp as before. PQ and HLG with `ToSrgb` still throw `NotSupportedException`, now before the
  pixels are decoded.
- Tests: `ToSrgb` matches djxl's sRGB rendering within the normal lossy tolerance (committed `srgb/`
  references for the ICC and P3 corpus files, and the djxl oracle), replacing 0.5.0's looser comparison;
  372 tests.

No public API changes. Still not implemented: encoding, metadata editing and animation. See the
[README](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.5.1/README.md) and the
[decoding guide](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.5.1/docs/decoding.md).

Packages are attached to this private release only, not published to a package registry.
