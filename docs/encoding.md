# Encoding JPEG XL

## Save

`image.SaveAsJxl(...)`, `SaveAsJxlAsync(...)`, `ToJxl(...)`, or `image.Save(..., new JxlEncoderOptions())`
write the root frame (the first frame of an animation) losslessly. `Image.Load` and `djxl` return every sample
unchanged, including the colour of fully transparent pixels.

```csharp
image.SaveAsJxl("photo.jxl");
byte[] data = image.ToJxl(new JxlEncoderOptions { CompressMetadata = true, Metadata = MetadataKinds.Icc });
```

| Option | Default | Meaning |
| --- | --- | --- |
| `Metadata` | `All` | Which of the ICC profile, EXIF and XMP are written when the image has them |
| `CompressMetadata` | `false` | Brotli-compress EXIF and XMP into `brob` boxes, as `cjxl` does by default; readers without Brotli then cannot see them |

The output is one 8-bit lossless Modular frame:

- **Channels:** gray when every pixel has R = G = B (unless an RGB ICC profile is written), else RGB; an alpha
  channel (straight) when any pixel is not opaque.
- **Transforms:** a palette of all channels when the image has at most 1024 colours (and at most one per eight
  pixels), with entries sorted by luma. Otherwise the reversible colour transform (RCT) with the smallest estimated
  size among all 37 distinct ones (YCoCg and the subtracting variants in every channel order), or none.
- **Prediction and context modelling:** an MA tree learned from samples of up to half of the pixels (at most 2^20),
  splitting on the channel, the weighted predictor's error, the gradient and the neighbour differences into up to 96
  quantiles, with the weighted or gradient predictor per leaf. This follows `cjxl -d 0 -e 7`.
- **Entropy coding:** ANS with clustered contexts (at most 64 distributions), a hybrid integer configuration per
  cluster chosen from 20 candidates by estimated size, and the cheapest histogram precision. No LZ77.
- **Layout:** 256×256 groups with one global tree; no progressive passes, no squeeze, no loop filters.

The encoder is single-threaded and deterministic: the same image and options give the same bytes. It works on 32-bit
integer channels, copies them into the groups and keeps every coded residual with its context until the frame is
written, so it needs up to about 100 bytes per pixel at its peak (a 12-megapixel photo about 1.2 GB).

### Size and speed

Measured on one core against `cjxl` 0.12.0 with `--num_threads=0`:

| Image | This encoder | `cjxl -d 0 -e 7` | `cjxl -d 0 -e 3` |
| --- | --- | --- | --- |
| 4032×3024 photo (8-bit RGB) | 10.41 MB, 8.3 s | 10.53 MB, 8.7 s | 11.49 MB, 1.5 s |
| 1500×1000, 8 colours | 3.3 KB, 0.8 s | 7.3 KB, 0.2 s | |
| 1500×1000 gradient with noise and alpha | 1.81 MB, 1.6 s | 1.95 MB, 1.2 s | |

Quality and effort controls, lossy (VarDCT) encoding and parallel groups come in later versions.

## Metadata

- **ICC profile:** stored in the codestream (header prediction and one inserted block, entropy-coded), so the samples
  are declared to be in that profile. Only RGB and gray profiles are accepted; a gray profile on colour pixels throws
  `NotSupportedException`.
- **Colour description** without an ICC profile becomes the enumerated colour encoding: `SrgbRenderingIntent` gives
  sRGB with that intent; `Chromaticities` give the named primaries and white points (sRGB, P3, BT.2100; D65, DCI, E)
  or custom ones; `Gamma` 1 is linear, 1/2.6 the DCI curve, 1/2.2 with primaries other than sRGB the sRGB curve (as the
  decoder describes it), and any other value up to 1 a pure gamma. A gamma above 1 cannot be stored and keeps the sRGB
  curve. Images without a description are sRGB.
- **EXIF and XMP:** an `Exif` box (a zero offset, then the TIFF stream) and an `xml ` box before the `jxlc` box of a
  container. Without them the file is a bare codestream.
- **Orientation:** `ImageMetadata.Orientation` (from EXIF) becomes the codestream orientation, which JPEG XL readers
  apply; the EXIF block is written unchanged. It is written even when `Metadata` excludes EXIF.
- Resolution cannot be stored. Images larger than level 5 allows (2^18 pixels a side or 2^28 pixels) get a `jxll`
  box for level 10.

## Edit metadata

`Image.EditMetadata` changes ICC, EXIF, XMP, the colour description and the orientation without decoding frames:

- A changed ICC profile, colour description or orientation rewrites the ImageMetadata header and the ICC stream;
  the SizeHeader, the transform data and every frame (including a preview) are copied unchanged. A profile cannot
  change an image between gray and colour; removing the profile writes the colour description (or sRGB).
- Changed EXIF or XMP replaces the first `Exif` or `xml ` box (compressed again when it was a `brob` box) and drops
  later ones; new boxes go before the codestream. A bare codestream becomes a container when it needs boxes.
- Every other box (`jxll`, `jumb`, unknown boxes, `jxlp` parts when the codestream is unchanged) keeps its bytes
  and position.
- `NotSupportedException`: a changed resolution, recompressed JPEGs with a `jbrd` box (the reconstruction data
  refers to the old metadata), and headers with extensions.

## Tests

`JxlEntropyEncoderTests` decode the bit, field and ANS writers' output with the decoder's readers.
`JxlCodestreamWriterTests` round-trip synthetic images (noise, gradients, few colours, flat, gray, alpha with
transparent colour, one to several groups) through the plugin's decoder, and `JxlEncoderTests` cover the public API,
metadata, cancellation and editing. Oracle tests decode encoded files with `djxl` and compare every sample, and check
written and edited ICC profiles against `djxl --orig_icc_out`.
