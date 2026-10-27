Lossless encoding and metadata editing, for PictureSharp 0.8.5 (.NET 10 / C# 14).

- **Encoding:** `JxlFormat.Encoder`, `JxlEncoderOptions` and `SaveAsJxl` / `SaveAsJxlAsync` / `ToJxl` write the root
  frame as one lossless 8-bit Modular frame. Every sample round-trips, including the colour of fully transparent
  pixels. Gray images get one colour channel; alpha is written only when a pixel is not opaque.
- **Compression:** a palette for images with up to 1024 colours, otherwise the best of the 37 reversible colour
  transforms; an MA tree learned from pixel samples with the weighted and gradient predictors, as `cjxl -d 0 -e 7`
  does; clustered ANS codes with a hybrid integer configuration per cluster; 256×256 groups. A 12-megapixel photo is
  about 1% smaller than `cjxl -d 0 -e 7` at a similar single-threaded speed.
- **Metadata:** the ICC profile goes into the codestream, EXIF and XMP into `Exif` and `xml ` boxes
  (`CompressMetadata` makes them `brob`), the colour description into the enumerated colour encoding and the
  EXIF orientation into the codestream orientation. `EncoderOptions.Metadata` selects the blocks.
- **Editing:** `Image.EditMetadata` changes ICC, EXIF, XMP, colour description and orientation, copying the frames
  and all other boxes unchanged. Resolutions and recompressed JPEGs (`jbrd`) throw `NotSupportedException`.
- **Tests:** writers against the decoder's readers, exact round trips through the plugin and through `djxl` (also
  multi-group, gray and alpha images), ICC profiles against `djxl --orig_icc_out` after encoding and editing,
  public API, cancellation and editing tests; the package consumer encodes and edits under Native AOT.

Public API additions: `JxlEncoderOptions`, `JxlFormat.Encoder`, `JxlImageExtensions`. Lossy encoding, quality and
effort controls (0.7.0) and animation are not implemented yet. See the
[README](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.6.0/README.md) and the
[encoding guide](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.6.0/docs/encoding.md).

Packages are attached to this private release only, not published to a package registry.
