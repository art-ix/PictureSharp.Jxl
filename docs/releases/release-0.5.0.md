Metadata and colour profiles for PictureSharp 0.8.5 (.NET 10 / C# 14).

- The ICC profile is decoded from the codestream (libjxl's entropy-coded stream and ICC prediction,
  byte-identical to `djxl --orig_icc_out`) and attached to decoded images.
- Lossy (XYB) images with an ICC profile are rendered in that profile when it is a matrix/TRC RGB or
  gray TRC profile, as photos from phones and cameras use; previously they were rendered as sRGB.
  `ColorConversion = ToSrgb` now converts them correctly. Profiles with lookup tables are still
  rendered as sRGB, without attaching the profile.
- Enumerated colour encodings become PictureSharp's colour description (chromaticities and gamma), as
  for PNG and HEIF. PQ and HLG with `ToSrgb` throw `NotSupportedException`.
- EXIF and XMP are read from `Exif` and `xml ` boxes, also Brotli-compressed `brob` boxes, which cjxl
  writes by default. Damaged boxes are skipped.
- The codestream orientation overrides the EXIF orientation tag, so `ApplyExifOrientation` rotates
  exactly once.
- `Image.ReadMetadata` is supported (`JxlFormat.MetadataCodec`); `Image.EditMetadata` is not yet.
- ICC, EXIF and XMP are charged against `MaxMetadataBytes` before allocation, including Brotli output.
- Integer samples up to 16 bits are reduced to 8 bits as djxl's 8-bit output; the corpus now covers
  10-, 12- and 16-bit lossless and 16-bit lossy files.
- Corpus: 61 files (6 new), including colour-managed references; 369 tests. Lossy images in a test
  profile match a double-precision conversion of djxl's output within 1; local fuzzing of the ICC,
  Exif and `brob` paths found only `ImageFormatException` and `NotSupportedException`.

Still not implemented: encoding, metadata editing and animation. See the
[README](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.5.0/README.md) and the
[decoding guide](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.5.0/docs/decoding.md).

Packages are attached to this private release only, not published to a package registry.
