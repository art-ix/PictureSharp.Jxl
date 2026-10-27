Lossless Modular decoding for PictureSharp 0.8.5 (.NET 10 / C# 14).

- `Image.Load` decodes still images whose frame is Modular without XYB, as produced by
  `cjxl -d 0`. It covers:
  - ANS and prefix entropy coding, LZ77 and clustered contexts;
  - meta-adaptive trees with all 14 predictors, including the weighted predictor;
  - the RCT, palette (with delta and implicit entries) and squeeze transforms;
  - groups, LF groups and progressive passes.
- Pixels match djxl v0.12.0 exactly at 8 bits per sample. The tests check every decodable corpus
  file against the djxl rendering recorded in `corpus.json`.
- Output is `Rgba32` with straight alpha:
  - integer samples of any depth are scaled like libjxl, so 10 to 16-bit images are reduced to
    8 bits;
  - grayscale is replicated to RGB;
  - extra channels other than the first alpha channel are dropped.
- The orientation is kept in `Image.Metadata.Orientation`. `DecoderOptions.ApplyExifOrientation`
  applies it.
- Pixels stay in the image's colour space. The ICC profile is skipped, not attached.
- `DecoderOptions.MaxPixels` and cancellation are honoured. Corrupt data throws
  `ImageFormatException`.
- `NotSupportedException` for:
  - lossy (VarDCT or XYB) images, animations and floating-point samples;
  - layered images, including patches;
  - noise, splines, upsampling, cropped or blended frames and restoration filters;
  - CMYK and spot colour channels;
  - premultiplied, float or subsampled alpha.
- Releases are now published as regular (latest) GitHub releases instead of pre-releases.

Still not implemented: lossy decoding, encoding, metadata and ICC profile reading. See the
[README](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.3.0/README.md) and the
[decoding guide](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.3.0/docs/decoding.md).

Packages are attached to this private release only, not published to a package registry.
