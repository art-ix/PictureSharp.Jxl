# Encoding JPEG XL

Examples assume `JxlFormat` is registered in `ImageConfiguration.Default` (see the README).

```csharp
using Image image = Image.Load("photo.png");
image.SaveAsJxl("photo.jxl");                                              // lossless, with ICC, EXIF and XMP
byte[] data = image.ToJxl(new JxlEncoderOptions { CompressMetadata = true });
```

`SaveAsJxl`, `SaveAsJxlAsync`, `ToJxl` and `image.Save("photo.jxl")` write the root frame (the first frame of an
animation) losslessly. `Image.Load` and `djxl` return every sample unchanged, including the colour of fully
transparent pixels. The stream is left open.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `Metadata` | `All` | which of the ICC profile, EXIF and XMP are written when the image has them |
| `CompressMetadata` | `false` | Brotli-compress EXIF and XMP into `brob` boxes, as `cjxl` does by default; readers without Brotli then cannot see them |

Quality and effort controls and lossy (VarDCT) encoding come in 0.7.0.

## What is written

One 8-bit lossless Modular frame, chosen like `cjxl -d 0 -e 7`:

- **Channels:** gray when every pixel has R = G = B (unless an RGB ICC profile is written), else RGB; straight alpha
  when any pixel is not opaque.
- **Transforms:** a palette when the image has at most 1024 colours (and at most one per eight pixels), sorted by
  luma; otherwise the reversible colour transform (RCT) with the smallest estimated size of the 37 distinct ones, or
  none.
- **Prediction:** a meta-adaptive tree learned from a sample of up to half of the pixels (at most 2^20), with the
  weighted or gradient predictor per leaf.
- **Entropy coding:** ANS with at most 64 clustered distributions, a hybrid integer configuration per cluster and the
  cheapest histogram precision; no LZ77.
- **Layout:** 256×256 groups with one global tree; no progressive passes, squeeze or loop filters.
- **Container:** a bare codestream, or a box container when EXIF or XMP are written. Images beyond level 5 (2^18
  pixels a side or 2^28 pixels) get a `jxll` box for level 10.

The metadata that goes with the frame is described in the [metadata guide](metadata.md#writing).

## Exactness

The encoder is deterministic: the same image and options give the same bytes. Every encoded file decodes with `djxl`
to exactly the input samples; oracle tests compare every sample and the ICC profile (`djxl --orig_icc_out`).

## Speed and memory

Single-threaded, measured on one core against `cjxl` 0.12.0 with `--num_threads=0`:

| Image | This encoder | `cjxl -d 0 -e 7` | `cjxl -d 0 -e 3` |
| --- | --- | --- | --- |
| 4032×3024 photo (8-bit RGB) | 10.41 MB, 8.3 s | 10.53 MB, 8.7 s | 11.49 MB, 1.5 s |
| 1500×1000, 8 colours | 3.3 KB, 0.8 s | 7.3 KB, 0.2 s | |
| 1500×1000 gradient with noise and alpha | 1.81 MB, 1.6 s | 1.95 MB, 1.2 s | |

The encoder keeps 32-bit integer channels and every coded residual with its context until the frame is written: up to
about 100 bytes per pixel at its peak, so a 12-megapixel photo needs about 1.2 GB. Cancellation is checked
while the planes are prepared and between encoding stages. Parallel groups come later.
