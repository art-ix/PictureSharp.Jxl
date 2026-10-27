Lossy decoding for PictureSharp 0.8.5 (.NET 10 / C# 14).

- `Image.Load` now decodes still lossy JPEG XL images, as `cjxl` produces them by default:
  - VarDCT with XYB: all 27 transforms from 2×2 to 256×256 including AFV, custom quantisation
    tables, chroma from luma, adaptive DC smoothing, coefficient orders, progressive AC passes;
  - lossy Modular (`cjxl -m 1`);
  - recompressed JPEGs (YCbCr with chroma subsampling), rendered like libjxl;
  - patches from reference frames, which cjxl uses for text and graphics, and DC frames (`cjxl -p`);
  - Gaborish, the edge-preserving filter and 2×/4×/8× upsampling of colour and extra channels;
  - XYB to RGB in the image's colour space (sRGB, P3, BT.2100 and custom primaries; sRGB, linear,
    gamma, BT.709, DCI and PQ transfer).
- Lossy images match djxl v0.12.0 within 1/255 in a few samples (libjxl uses approximate
  reciprocals); lossless images still match exactly. 8-bit output includes libjxl's ordered dither.
- The corpus gained 18 lossy fixtures with djxl renderings; every decodable file is checked against
  them, and fuzzing found only `ImageFormatException` and `NotSupportedException`.
- XYB images with an ICC profile are rendered as sRGB until colour management arrives (0.5.0).
- `NotSupportedException` for animations, layered images, photon noise, splines, HLG lossy images,
  float samples, CMYK and spot colours, premultiplied or float alpha.

Still not implemented: encoding, metadata and ICC profile reading. See the
[README](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.4.0/README.md) and the
[decoding guide](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.4.0/docs/decoding.md).

Packages are attached to this private release only, not published to a package registry.
