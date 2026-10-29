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

Original JPEG bytes can be preserved with the separate [JPEG transcoder](transcoding.md). Loading a JPEG into an
`Image` and calling `SaveAsJxl` works on pixels and cannot reconstruct the original JPEG file.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `Quality` | `90` | 0–100 on `cjxl -q`'s scale (100 lossless, 90 = distance 1.0); used when `Distance` is null |
| `Distance` | `null` | butteraugli distance 0–25 as `cjxl -d`; overrides `Quality`; 0 is lossless |
| `Effort` | `7` | 1–9 as `cjxl -e`; lossless tiers since 0.8.3 ([below](#lossless-frames)); 8 and 9 currently behave as 7 |
| `Metadata` | `All` | which of ICC, EXIF and XMP are written |
| `CompressMetadata` | `false` | Brotli-compress EXIF and XMP into `brob` boxes (as `cjxl`); readers without Brotli won't see them |

`EffectiveDistance` reports the distance actually used. Quality maps like `cjxl`: 0.1 + (100 − q) × 0.09 from 30 up,
quadratically to 25 below. Distances in (0, 0.05) become 0.05.

## Sample precision

`Rgba32` images are written at 8 bits; `Rgba64` images at 16 bits. `Distance = 0` preserves every sample, including
colour under transparent alpha. Lossy VarDCT converts 16-bit input directly to XYB using full-resolution transfer
curves; alpha remains lossless at 16 bits. Load with `PreserveBitDepth` to retain that precision on decoding.

```csharp
using Image scan = Image.Load("scan.png", new DecoderOptions { PreserveBitDepth = true });
scan.SaveAsJxl("scan.jxl", new JxlEncoderOptions { Distance = 0 });
```

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

These size and quality comparisons were recorded for the initial VarDCT encoder. For current timing, allocations
and peak RSS, see the [speed measurements](benchmarks/speed.md).

## Lossless frames

One 8-bit or 16-bit Modular frame. Effort 7 is chosen like `cjxl -d 0 -e 7`:

- **Channels:** gray when R = G = B everywhere (unless an RGB ICC profile is written), else RGB; alpha when any pixel
  is not opaque.
- **Transforms:** palette for ≤ 1024 colours (and ≤ one per eight pixels), sorted by luma; otherwise the best of the
  37 distinct RCTs, or none.
- **Prediction:** meta-adaptive tree learned from a pixel sample, weighted or gradient predictor per leaf.
- **Entropy coding:** clustered ANS (≤ 64 distributions), no LZ77; 256×256 groups, no progressive passes or squeeze.

Since 0.8.3 lower efforts follow libjxl 0.12.0's Modular tiers (`enc_modular.cc`; node threshold 75 + 14 × speed
tier, `nb_repeats` and `max_property_values` per tier). Before 0.8.3 every effort produced the effort-7 output;
efforts 7–9 still do, byte for byte. The palette is tried at every effort.

| Effort | libjxl tier | Colour transform | Tree | Properties, sample fraction, quantization |
| --- | --- | --- | --- | --- |
| 1 | lightning | YCoCg | one gradient-predictor leaf per channel | none |
| 2 | thunder | YCoCg | fixed `kGradientFixedDC` tree on W + N − NW, gradient predictor | none |
| 3 | falcon | YCoCg | fixed `kWPFixedDC` tree on the weighted predictor's error, weighted predictor | none |
| 4 | cheetah | YCoCg | learned, weighted or gradient predictor per leaf | 3, 15%, 32 values |
| 5 | hare | RCT search | learned | 4, 25%, 48 values |
| 6 | wombat | RCT search | learned | 5, 35%, 64 values |
| 7–9 | squirrel | RCT search | learned | 7, 50%, 96 values |

Input of more than 8 bits (all efforts, since 0.8.3): the palette also accepts samples above 32767, and a channel
that uses few of its values (at most a quarter of its range and one value per 16 pixels, and cheaper to predict) is
replaced by a sorted per-channel palette and indices, like libjxl's channel compaction. 8-bit images widened to
16 bits then code about as small as the 8-bit image; full-range 16-bit photos are unchanged.

The fixed trees are libjxl's `MakeFixedTree`: 33 cutoffs, shallower below 2^14 pixels, cutoffs × 16 for 16-bit
samples. libjxl's effort 1 is a separate encoder (`enc_fast_lossless`, prefix codes and run lengths); here effort 1
keeps this encoder's ANS bitstream with the cheapest tree, so its files are smaller and it is slower than `cjxl -e 1`.

Sizes against `cjxl` 0.12.0 (bytes, `--num_threads=1`):

| Image | e1 | e2 | e3 | e4 | e5 | e6 | e7 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1280×800 photo, this encoder | 908 156 | 895 470 | 853 183 | 842 444 | 817 412 | 793 336 | 785 105 |
| 1280×800 photo, `cjxl` | 1 015 203 | 880 332 | 854 203 | 840 469 | 830 459 | 814 919 | 785 474 |
| 1600×1000 screen, this encoder | 207 130 | 178 723 | 198 170 | 140 252 | 126 351 | 118 958 | 108 866 |
| 1600×1000 screen, `cjxl` | 245 462 | 218 166 | 237 860 | 200 927 | 127 520 | 118 863 | 109 392 |

Encoding times per effort are in the [speed measurements](benchmarks/speed.md#encoder-work-083).

Output is a bare codestream, or a container when EXIF or XMP are written; images beyond level 5 get a `jxll` box.

## Exactness

Deterministic: same input, same bytes. Lossless output decodes with `djxl` to exactly the input; lossy output decodes
with `djxl` and this decoder within 1 of each other, alpha exact (checked by a randomized sweep).

## Speed and memory

Version 0.7.10 reads the image buffer directly and writes prepared sections to the destination stream,
without assembling a complete intermediate codestream array. Section sizes are determined before the TOC
is written, so the destination need not support seeking. Working buffers return to their pools before
output writing where their last consumer has finished; retained section buffers live through the write.

In the measured 1 MP cases, warm lossless encode allocations fall from 224.4 to 43.0 MiB for 8-bit input
and from 250.2 to 54.8 MiB for 16-bit input. Lossy effort-7 allocations fall from about 143.5 to 29.7 MiB
and from 143.6 to 29.9 MiB respectively. These counts include the encoded output array.

Pool retention and bucket sizes affect peak RSS separately from allocations. See the
[speed measurements](benchmarks/speed.md#allocations-and-memory-0710) for the 1 MP, 12 MP and screenshot
cases, GC counts and controlled timings. Cancellation is checked between stages and bounded batches.
