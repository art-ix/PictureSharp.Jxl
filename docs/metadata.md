# Metadata

JPEG XL stores colour encoding and orientation in the codestream header, EXIF and XMP in container boxes:

| `ImageMetadata` | JPEG XL | Read | Written | Edited |
| --- | --- | :---: | :---: | :---: |
| `IccProfile` | ICC stream in the codestream | ✔ | ✔ | ✔ |
| `Gamma`, `Chromaticities`, `SrgbRenderingIntent` | enumerated colour encoding | ✔ | ✔ | ✔ |
| `Exif` | `Exif` box, also Brotli (`brob`) | ✔ | ✔ | ✔ |
| `Xmp` | `xml ` box, also Brotli (`brob`) | ✔ | ✔ | ✔ |
| `Orientation` | codestream orientation | ✔ | ✔ | ✔ |
| `HorizontalResolution`, `VerticalResolution` | none | | | |

JUMBF (`jumb`), JPEG reconstruction data (`jbrd`) and unknown boxes are not read; editing keeps them.

## Reading

```csharp
ImageMetadata metadata = Image.ReadMetadata("photo.jxl");                  // headers and boxes, no frame decoding
using Image image = Image.Load("photo.jxl", new DecoderOptions { ApplyExifOrientation = true });
```

`Image.Load` reports the same metadata, unless `IgnoreMetadata` is set.

- **ICC profile:** decoded as `djxl --orig_icc_out` writes it, up to 256 MiB. `ReadMetadata` always reports it;
  `Load` attaches it only when the samples are in it — not for lossy images with lookup-table profiles or with
  `ColorConversion = ToSrgb` ([decoding guide](decoding.md#colour)).
- **Colour description:** without a profile, non-sRGB primaries become `Chromaticities` and the curve a `Gamma`
  (1/2.2 sRGB-like, 1 linear, 1/2.6 DCI, or the stored exponent). PQ and HLG cannot be described.
- **EXIF and XMP:** the first `Exif` and `xml ` boxes; Brotli via the BCL `BrotliDecoder`. Malformed boxes are skipped.
- **Orientation:** the codestream value is authoritative (ISO/IEC 18181-2) and replaces or creates the EXIF tag, so
  `ApplyExifOrientation` or `AutoOrient()` rotates exactly once. Reported even with `IgnoreMetadata`.

Each block is charged against `MaxMetadataBytes` before it is materialized (Brotli output as it grows).

## Writing

When `EncoderOptions.Metadata` allows (all by default):

- **ICC profile:** RGB or gray only (gray on colour pixels throws). Lossy images store a profile that an enumerated
  encoding describes (Display P3, sRGB, gamma curves) as that encoding, as `cjxl` does, so decoders return pixels in
  that space instead of linear sRGB; reading back gives a colour description, not the profile.
- **Colour description** (no profile): `SrgbRenderingIntent`, `Chromaticities` (named or custom) and `Gamma` (1
  linear, 1/2.6 DCI, 1/2.2 sRGB, other ≤ 1 pure gamma) map to the enumerated encoding. Default is sRGB.
- **EXIF and XMP:** an `Exif` box (zero offset + TIFF stream) and an `xml ` box before the codestream; `brob` with
  `JxlEncoderOptions.CompressMetadata`.
- **Orientation:** becomes the codestream orientation, even when `Metadata` excludes EXIF; EXIF is written unchanged.

Resolution cannot be stored and is dropped.

## Editing without re-encoding

`Image.EditMetadata` changes ICC, EXIF, XMP, colour description and orientation, copying every frame unchanged:

```csharp
Image.EditMetadata("photo.jxl", "tagged.jxl", metadata =>
{
    metadata.Xmp = File.ReadAllBytes("tags.xmp");
    metadata.Orientation = ExifOrientation.RightTop;                    // rotate 90° clockwise when displayed
});
```

- Colour or orientation changes rewrite only the image header and ICC stream; frames are copied bit for bit. A profile
  can't switch gray ↔ colour.
- New EXIF or XMP replaces the first matching box (recompressed if `brob`); a bare codestream becomes a container.
- All other boxes keep their bytes and position.
- `NotSupportedException`: changed resolution, recompressed JPEGs with `jbrd` (it refers to the old metadata),
  headers with extensions.

Oracle tests check written and edited ICC profiles against `djxl --orig_icc_out`.
