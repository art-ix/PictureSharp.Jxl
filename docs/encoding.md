# Encoding JPEG XL

Examples assume `JxlFormat` is registered in `ImageConfiguration.Default` (see the README).

```csharp
using Image image = Image.Load("photo.png");
image.SaveAsJxl("photo.jxl");                                              // lossy, distance 1.0, effort 7
image.SaveAsJxl("small.jxl", new JxlEncoderOptions { Quality = 75, Effort = 5 });
image.SaveAsJxl("exact.jxl", new JxlEncoderOptions { Distance = 0 });     // lossless
byte[] data = image.ToJxl(new JxlEncoderOptions { CompressMetadata = true });
```

`SaveAsJxl`, `SaveAsJxlAsync`, `ToJxl` and `image.Save("photo.jxl")` write the root frame (the first frame of an
animation), with ICC, EXIF and XMP. By default the frame is lossy (VarDCT in XYB) at butteraugli distance 1.0 and
effort 7, like `cjxl` for non-JPEG input. `Quality = 100` or `Distance = 0` encode losslessly. The stream is left open.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `Quality` | `90` | 0–100 on `cjxl -q`'s scale: 100 is lossless, 90 is distance 1.0; used when `Distance` is null |
| `Distance` | `null` | butteraugli distance 0–25 as `cjxl -d`; takes precedence over `Quality`; 0 is lossless |
| `Effort` | `7` | 1–9 as `cjxl -e` for lossy frames; 8 and 9 currently behave as 7 |
| `Metadata` | `All` | which of the ICC profile, EXIF and XMP are written when the image has them |
| `CompressMetadata` | `false` | Brotli-compress EXIF and XMP into `brob` boxes, as `cjxl` does by default; readers without Brotli then cannot see them |

`EffectiveDistance` reports the distance that will be encoded. Quality maps like `cjxl`: 0.1 + (100 − q) × 0.09 from
30 up, rising quadratically to 25 below. Distances between 0 and 0.05 are encoded at 0.05.

## Lossy frames

One VarDCT frame, a scalar port of libjxl 0.12.0's encoder with the same decisions per effort:

| Effort | libjxl tier | What it adds |
| --- | --- | --- |
| 1–3 | thunder, falcon | DCT8 everywhere, constant quantization, default coefficient orders, linear DC |
| 4 | cheetah | custom coefficient orders, nonlinear DC quantization |
| 5 | hare | adaptive quantization, transform search, Gaborish, fast chroma-from-luma |
| 6 | wombat | edge-preserving filter sharpness search |
| 7–9 | squirrel | Newton chroma-from-luma, chromacity adjustments, patches for repeated shapes (text) |

- **Colour:** pixels go to XYB from sRGB, linear, gamma, BT.709, DCI and PQ transfer functions, the standard and
  custom primaries, and matrix/TRC ICC profiles. Images whose colour cannot be converted this way (lookup-table ICC
  profiles, HLG) are written losslessly instead.
- **Alpha:** stored losslessly as a Modular extra channel; the colour of invisible pixels is simplified first.
- **Resampling:** from distance 10 the frame is coded at half size and upsampled by the decoder, as in `cjxl`;
  images with alpha keep their full size.
- **Patches (effort 7+):** repeated shapes go to a reference-only lossless frame that the main frame adds back.
  `cjxl` skips patches in streaming mode (more than 8 groups at effort 7), so on large text images this encoder is
  much smaller.

Measured at distance 1 against `cjxl` 0.12.0 (`--num_threads=0`), quality by ssimulacra2 after `djxl`:

| Image | Effort | This encoder | `cjxl` |
| --- | --- | --- | --- |
| 1024×768 photo | 3 | 161 491 B, 85.16 | 161 714 B, 85.11 |
| 1024×768 photo | 5 | 163 519 B, 85.21 | 164 381 B, 85.29 |
| 1024×768 photo | 7 | 164 491 B, 85.53 | 165 396 B, 85.49 |
| small text screenshot | 7 | 16 085 B | 15 315 B |
| 900×600 text | 7 | 18 931 B, 92.6 | 166 208 B, 90.18 |

## Lossless frames

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

Both kinds are a bare codestream, or a box container when EXIF or XMP are written. Images beyond level 5 (2^18
pixels a side or 2^28 pixels) get a `jxll` box for level 10.

The metadata that goes with the frame is described in the [metadata guide](metadata.md#writing).

## Exactness

The encoder is deterministic: the same image and options give the same bytes. Lossless files decode with `djxl` to
exactly the input samples; oracle tests compare every sample and the ICC profile (`djxl --orig_icc_out`). Lossy files
decode with `djxl` and with this decoder to pixels within 1 of each other, with alpha unchanged; a randomized sweep
over sizes 1–1030, gray/RGB with alpha, distances 0.1–25 and efforts 1–9 checks the same.

## Speed and memory

Lossless, single-threaded, measured on one core against `cjxl` 0.12.0 with `--num_threads=0`:

| Image | This encoder | `cjxl -d 0 -e 7` | `cjxl -d 0 -e 3` |
| --- | --- | --- | --- |
| 4032×3024 photo (8-bit RGB) | 10.41 MB, 8.3 s | 10.53 MB, 8.7 s | 11.49 MB, 1.5 s |
| 1500×1000, 8 colours | 3.3 KB, 0.8 s | 7.3 KB, 0.2 s | |
| 1500×1000 gradient with noise and alpha | 1.81 MB, 1.6 s | 1.95 MB, 1.2 s | |

The encoder keeps 32-bit integer channels and every coded residual with its context until the frame is written: up to
about 100 bytes per pixel at its peak, so a 12-megapixel photo needs about 1.2 GB. Cancellation is checked
while the planes are prepared and between encoding stages. Parallel groups come later.
