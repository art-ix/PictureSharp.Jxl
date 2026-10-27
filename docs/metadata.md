# Metadata

JPEG XL keeps the colour encoding and the orientation in the codestream header, and EXIF and XMP in boxes of the
container. The plugin maps them to `ImageMetadata`:

| `ImageMetadata` | JPEG XL | Read | Written | Edited |
| --- | --- | :---: | :---: | :---: |
| `IccProfile` | ICC stream in the codestream | ✔ | ✔ | ✔ |
| `Gamma`, `Chromaticities`, `SrgbRenderingIntent` | enumerated colour encoding | ✔ | ✔ | ✔ |
| `Exif` | `Exif` box, also Brotli-compressed (`brob`) | ✔ | ✔ | ✔ |
| `Xmp` | `xml ` box, also Brotli-compressed (`brob`) | ✔ | ✔ | ✔ |
| `Orientation` | codestream orientation | ✔ | ✔ | ✔ |
| `HorizontalResolution`, `VerticalResolution` | none | | | |

JUMBF (`jumb`), JPEG reconstruction data (`jbrd`) and unknown boxes are not read; editing keeps them.

## Reading

```csharp
ImageMetadata metadata = Image.ReadMetadata("photo.jxl");                  // headers and boxes, no frame decoding
using Image image = Image.Load("photo.jxl", new DecoderOptions { ApplyExifOrientation = true });
```

`Image.Load` reports the same metadata with the pixels, unless `DecoderOptions.IgnoreMetadata` is set.

- **ICC profile:** decoded from the codestream as `djxl --orig_icc_out` writes it, up to 256 MiB as in libjxl.
  `ReadMetadata` always reports it; `Load` attaches it when the samples are in it, which is not the case for lossy
  images with lookup-table profiles or with `ColorConversion = ToSrgb` (see the
  [decoding guide](decoding.md#colour)).
- **Colour description:** without a profile, the enumerated colour encoding becomes `Chromaticities` for primaries or
  white points other than sRGB/D65 and a `Gamma`: 1/2.2 for sRGB-like and BT.709 curves, 1 for linear, 1/2.6 for DCI,
  or the stored exponent. sRGB needs none. PQ and HLG cannot be described.
- **EXIF and XMP:** the first `Exif` box (its four-byte TIFF offset removed) and the first `xml ` box. Brotli streams
  are decompressed with the BCL `BrotliDecoder`; malformed Exif boxes and corrupt Brotli streams are skipped.
- **Orientation:** the codestream orientation is authoritative (ISO/IEC 18181-2) and replaces the EXIF orientation
  tag, or creates one. `ApplyExifOrientation` or `AutoOrient()` therefore rotates exactly once, even when the Exif box
  disagrees. It is reported also with `IgnoreMetadata`.

Every block is charged against `MaxMetadataBytes` before it is materialized, Brotli output as it grows.

## Writing

When `EncoderOptions.Metadata` allows them (all by default), the encoder writes:

- **ICC profile:** into the codestream, so the samples are declared to be in that profile. Only RGB and gray profiles
  are accepted; a gray profile on colour pixels throws `NotSupportedException`.
- **Colour description** (without a profile): `SrgbRenderingIntent` gives sRGB with that intent; `Chromaticities` give
  the named primaries and white points (sRGB, P3, BT.2100; D65, DCI, E) or custom ones; `Gamma` 1 is linear, 1/2.6 the
  DCI curve, 1/2.2 the sRGB curve, any other value up to 1 a pure gamma. A gamma above 1 keeps the sRGB curve. Images
  without a description are sRGB.
- **EXIF and XMP:** an `Exif` box (a zero offset, then the TIFF stream) and an `xml ` box before the codestream, in
  `brob` boxes with `JxlEncoderOptions.CompressMetadata`.
- **Orientation:** `ImageMetadata.Orientation` becomes the codestream orientation, also when `Metadata` excludes EXIF;
  the EXIF block is written unchanged.

Resolution cannot be stored and is dropped.

## Editing without re-encoding

`Image.EditMetadata` changes ICC, EXIF, XMP, the colour description and the orientation and copies every frame
unchanged:

```csharp
Image.EditMetadata("photo.jxl", "tagged.jxl", metadata =>
{
    metadata.Xmp = File.ReadAllBytes("tags.xmp");
    metadata.Orientation = ExifOrientation.RightTop;                    // rotate 90° clockwise when displayed
});
```

- A changed profile, colour description or orientation rewrites the image header and the ICC stream; the size header,
  the transform data and every frame (including a preview) are copied bit for bit. A profile cannot change an image
  between gray and colour; removing it writes the colour description (or sRGB).
- Changed EXIF or XMP replaces the first `Exif` or `xml ` box (compressed again when it was `brob`) and drops later
  ones; new boxes go before the codestream. A bare codestream becomes a container when it needs boxes.
- Every other box (`jxll`, `jumb`, unknown boxes, `jxlp` parts when the codestream is unchanged) keeps its bytes and
  position.
- `NotSupportedException`: a changed resolution, recompressed JPEGs with a `jbrd` box (the reconstruction data refers
  to the old metadata) and headers with extensions.

Oracle tests check written and edited ICC profiles against `djxl --orig_icc_out`.
