## Layered images

0.8.10 decodes JPEG XL images made of several frames: layers that are cropped, placed at any offset (also partly or
wholly outside the image) and blended onto each other. They are composited into one image, as `djxl` does by
default. No API changes. Images of one frame decode exactly as in 0.8.9.

- **Blend modes:** Replace, Add, Blend (over, with the alpha channel), AlphaWeightedAdd and Mul, for colour and for
  each extra channel, with libjxl's clamping and reference slots. Lossless layers match `djxl` exactly; the
  libjxl conformance files `blendmodes`, `mul_no_extra_channels` and `sunset_logo` decode identically at 8 bits.
- **Colour:** layers are blended in the image's colour encoding, as libjxl does. With
  `ColorConversion = ToSrgb` a blended XYB image is then converted to sRGB in floating point, so it is still
  quantized once (libjxl cannot render such images in another colour space).
- **Memory:** each layer's temporary memory is returned once it is blended; only the composited images that later
  layers refer to stay charged against `MaxPixels`.
- Patches and blending now share one implementation; patch results are unchanged.

Still `NotSupportedException`: animations (0.8.12), photon noise and splines (0.8.14), premultiplied alpha and spot
colours (0.8.15); float samples and CMYK wait for PictureSharp itself.
([design](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.10/docs/design/0.8.10-blending.md))

Fuzzing of decoding, cancellation and the comparison with djxl (1.7 million inputs) found no defect.

Validation: 593 tests, golden results at 1 and 16 threads: the 1542 of 0.8.9 identical, plus 94 for ten new
layered test files. The package passes the JIT and Native AOT consumer checks.

NuGet package: [PictureSharp.Jxl 0.8.10](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.10).
`dotnet add package PictureSharp.Jxl`.
