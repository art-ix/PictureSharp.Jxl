# Encoding JPEG XL

Examples assume `JxlFormat` is registered in `ImageConfiguration.Default` (see the README).

```csharp
using Image image = Image.Load("photo.png");
image.SaveAsJxl("photo.jxl");                                              // lossy, distance 1.0, effort 7
image.SaveAsJxl("small.jxl", new JxlEncoderOptions { Quality = 75, Effort = 5 });
image.SaveAsJxl("exact.jxl", new JxlEncoderOptions { Distance = 0 });     // lossless
byte[] data = image.ToJxl(new JxlEncoderOptions { CompressMetadata = true });
```

`SaveAsJxl`, `SaveAsJxlAsync`, `ToJxl` and `image.Save("photo.jxl")` write the root frame (first frame of an
animation) with ICC, EXIF and XMP. The default is lossy VarDCT in XYB at distance 1.0, effort 7, like `cjxl`.
`Quality = 100` or `Distance = 0` is lossless. The stream is left open.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `Quality` | `90` | 0–100 on `cjxl -q`'s scale (100 lossless, 90 = distance 1.0); used when `Distance` is null |
| `Distance` | `null` | butteraugli distance 0–25 as `cjxl -d`; overrides `Quality`; 0 is lossless |
| `Effort` | `7` | 1–9 as `cjxl -e` (lossy); 8 and 9 currently behave as 7 |
| `Metadata` | `All` | which of ICC, EXIF and XMP are written |
| `CompressMetadata` | `false` | Brotli-compress EXIF and XMP into `brob` boxes (as `cjxl`); readers without Brotli won't see them |

`EffectiveDistance` reports the distance actually used. Quality maps like `cjxl`: 0.1 + (100 − q) × 0.09 from 30 up,
quadratically to 25 below. Distances in (0, 0.05) become 0.05.

## Lossy frames

One VarDCT frame, a scalar port of libjxl 0.12.0's encoder with the same decisions per effort:

| Effort | libjxl tier | Adds |
| --- | --- | --- |
| 1–3 | thunder, falcon | DCT8 everywhere, constant quantization, default coefficient orders, linear DC |
| 4 | cheetah | custom coefficient orders, nonlinear DC |
| 5 | hare | adaptive quantization, transform search, Gaborish, fast chroma-from-luma |
| 6 | wombat | edge-preserving filter sharpness search |
| 7–9 | squirrel | Newton chroma-from-luma, chromacity adjustments, patches (text) |

- **Colour:** enumerated colour spaces (except HLG) and matrix/TRC ICC profiles go to XYB; lookup-table profiles and
  HLG fall back to lossless. Profiles like Display P3 are stored as an enumerated encoding ([metadata](metadata.md#writing)).
- **Alpha:** lossless; colour of invisible pixels is simplified first.
- **Resampling:** from distance 10 the frame is coded at half size (not with alpha), as in `cjxl`.
- **Patches (effort 7+):** repeated shapes go to a reference frame. `cjxl` skips them for large images, so large text
  images come out much smaller here.

Distance 1 vs `cjxl` 0.12.0 (`--num_threads=0`), ssimulacra2 after `djxl`:

| Image | Effort | This encoder | `cjxl` |
| --- | --- | --- | --- |
| 1024×768 photo | 3 | 161 491 B, 85.16 | 161 714 B, 85.11 |
| 1024×768 photo | 5 | 163 519 B, 85.21 | 164 381 B, 85.29 |
| 1024×768 photo | 7 | 164 491 B, 85.53 | 165 396 B, 85.49 |
| small text screenshot | 7 | 16 085 B | 15 315 B |
| 900×600 text | 7 | 18 931 B, 92.6 | 166 208 B, 90.18 |
| 3024×4032 iPhone photo, Display P3 | 7 | 812 720 B, 85.73 | 826 418 B, 85.70 |
| 3024×4032 iPhone photo, Display P3 | 7 | 864 752 B, 88.46 | 873 122 B, 88.49 |
| 4032×3024 iPhone photo, Display P3 | 7 | 2 093 782 B, 84.39 | 2 104 239 B, 84.37 |

Not optimized yet: the first 12 MP photo takes 37 s and 2.6 GB at effort 7 on one core (`cjxl`: 2.8 s, 0.35 GB).

## Lossless frames

One 8-bit Modular frame, chosen like `cjxl -d 0 -e 7`:

- **Channels:** gray when R = G = B everywhere (unless an RGB ICC profile is written), else RGB; alpha when any pixel
  is not opaque.
- **Transforms:** palette for ≤ 1024 colours (and ≤ one per eight pixels), sorted by luma; otherwise the best of the
  37 distinct RCTs, or none.
- **Prediction:** meta-adaptive tree learned from a pixel sample, weighted or gradient predictor per leaf.
- **Entropy coding:** clustered ANS (≤ 64 distributions), no LZ77; 256×256 groups, no progressive passes or squeeze.

Output is a bare codestream, or a container when EXIF or XMP are written; images beyond level 5 get a `jxll` box.

## Exactness

Deterministic: same input, same bytes. Lossless output decodes with `djxl` to exactly the input; lossy output decodes
with `djxl` and this decoder within 1 of each other, alpha exact (checked by a randomized sweep).

## Speed and memory

Lossless, one core, vs `cjxl` 0.12.0:

| Image | This encoder | `cjxl -d 0 -e 7` | `cjxl -d 0 -e 3` |
| --- | --- | --- | --- |
| 4032×3024 photo (8-bit RGB) | 10.41 MB, 8.3 s | 10.53 MB, 8.7 s | 11.49 MB, 1.5 s |
| 1500×1000, 8 colours | 3.3 KB, 0.8 s | 7.3 KB, 0.2 s | |
| 1500×1000 gradient with noise and alpha | 1.81 MB, 1.6 s | 1.95 MB, 1.2 s | |

Peak memory is ~100 bytes per pixel (~1.2 GB for 12 MP). Cancellation is checked between stages.
