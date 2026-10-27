# Decoding JPEG XL

Examples assume `JxlFormat` is registered in `ImageConfiguration.Default` (see the README).

```csharp
using Image image = Image.Load("photo.jxl");                               // straight-alpha Rgba32, orientation not applied
ImageInfo info = Image.Identify("photo.jxl");                              // size and colour layout, no frame decoding
ImageMetadata metadata = Image.ReadMetadata("photo.jxl");                  // ICC, EXIF, XMP, orientation, no frame decoding
```

Both raw codestreams (`FF 0A`) and box containers (`JXL ` signature) are recognized. Metadata is described in the
[metadata guide](metadata.md).

## What is decoded

`Image.Load` decodes still images, lossy and lossless, as `cjxl` produces them:

| | Supported |
| --- | --- |
| VarDCT (lossy) | all 27 transforms from 2×2 to 256×256 including AFV, custom quantisation tables, chroma from luma, adaptive DC smoothing, coefficient orders, progressive AC passes |
| Modular | ANS and prefix codes, LZ77, meta-adaptive trees with every predictor (also the weighted one), RCT, palette and squeeze; lossless, and lossy with XYB |
| Recompressed JPEGs | `cjxl` on JPEG input: YCbCr, also with 4:2:0, 4:2:2 and 4:4:0 chroma subsampling, rendered as libjxl renders them (not as the reconstructed JPEG file) |
| Frames of one image | reference frames with patches (text and graphics) and DC frames (`cjxl -p`); a preview frame is skipped |
| Rendering | Gaborish and the edge-preserving filter, 2×, 4× and 8× upsampling, XYB and YCbCr to RGB |
| Colour | RGB and grayscale with alpha; transfer functions sRGB, linear, gamma, BT.709, DCI and PQ |
| Samples | integers up to 16 bits; lossy images of any declared depth (in XYB the depth is only metadata) |

Not supported, with `NotSupportedException`: animations; several displayed frames (layers), cropped or blended
frames; photon noise and splines; float samples (except XYB); HLG with XYB; CMYK and spot colours; premultiplied or
float alpha.

### Conversion to 8 bits

`Image` holds 8-bit straight-alpha RGBA, so the decoder reduces samples as libjxl writes 8-bit output:

- Rendered samples get libjxl's 32×32 blue-noise dither, are clamped and rounded to nearest even. 10-, 12- and 16-bit
  integers are scaled to [0, 1] in single precision first.
- Grayscale is replicated to RGB. The first alpha channel becomes alpha; other extra channels (depth, selection mask,
  thermal, …) are decoded and dropped.

### Colour

Pixels stay in the image's colour space, which `ImageInfo.Color` and the metadata describe:

- Lossless images and recompressed JPEGs keep their samples; the ICC profile or colour description says what they are.
- Lossy (XYB) images are rendered into their enumerated colour space (sRGB, P3, BT.2100, custom primaries, through
  XYZ as libjxl does) or into their ICC profile when it is a matrix/TRC RGB or gray TRC profile, the kind cameras,
  phones and displays use.
- Lossy images with a lookup-table profile (A2B/B2A) are rendered as sRGB without the profile, so the samples are
  never labelled with a profile they are not in.

With `ColorConversion = ToSrgb`, XYB images are rendered straight to sRGB in floating point, as
`djxl --color_space=sRGB` does, so their samples are quantized once; colours outside sRGB are clipped, also for
lookup-table profiles. Other images are converted by PictureSharp from the 8-bit samples. PQ and HLG images throw
`NotSupportedException` with `ToSrgb`.

```csharp
using Image srgb = Image.Load("wide-gamut.jxl", new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb });
```

## Identify

`Image.Identify` reads the container and codestream headers and checks them as libjxl does, without frames:

| `ImageInfo` | Value |
| --- | --- |
| `Width`, `Height` | the stored size; the orientation is reported, not applied |
| `Color.Model` | `Rgb` (also for XYB), `Gray`, `Cmyk` with a Black channel, `Other` for the XYB colour space |
| `Color.ChannelCount` | colour channels (1 or 3) plus every extra channel |
| `Color.BitsPerSample`, `SampleFormat` | the widest channel: integers 1–31 bits, floats 5–32 bits; `Float` if any channel is float |
| `Color.AlphaMode` | `Straight` or `Premultiplied`, else `None` |
| `Color.HasIccProfile` | the codestream declares an ICC profile |
| `Color.Cicp` | H.273 code points when the enumerated colour maps exactly; null with ICC, custom primaries, a gamma or XYB |
| `Compression` | `Lossy` for XYB; otherwise `Unknown`, since the headers do not say whether Modular is lossless |
| `Orientation` | the codestream orientation 1–8 |
| `IsAnimated`, `FrameCount` | animation header present; `FrameCount` is 1 for still images, null for animations |
| `Resolution` | null (resolution is not read) |

Identify also reports animations and features `Load` rejects. Widths above `int.MaxValue` throw
`NotSupportedException`.

## Options and limits

- `MaxPixels` and `MaxMetadataBytes` are checked before allocating; ICC, EXIF and XMP are charged before they are
  materialized, Brotli output as it grows, so a decompression bomb stops at the limit with `ImageFormatException`.
- `TargetSize`, `ApplyExifOrientation`, `ColorConversion`, `IgnoreMetadata` and cancellation behave as for the
  built-in formats. `TargetSize` resizes after the whole image is decoded.
- Memory: the decoder renders whole frames in single precision, about 12 bytes per pixel per buffer, two buffers while
  filtering. Cancellation is checked between sections, groups and rows.
- The decoder is a scalar, single-threaded port; SIMD and parallel groups come later.

## Errors

- Corrupt or truncated data throws `ImageFormatException`, also a corrupt ICC stream with `IgnoreMetadata` (libjxl
  decodes it for every image).
- Valid files with features outside the list above throw `NotSupportedException`.
- Container rules follow libjxl v0.12.0: `JXL ` box first, `ftyp` second, one `jxlc` box or a contiguous `jxlp`
  sequence, never both; out-of-order `jxlp` parts are accepted in file format version 1.

## Exactness

The port follows libjxl v0.12.0 operation by operation, including fused multiply-adds, rational polynomial
approximations and the 8-bit dither:

- lossless images match `djxl` exactly;
- lossy images differ by at most 1 in a few samples (typically under 0.01%), since libjxl uses hardware approximate
  reciprocals in dequantisation;
- lossy images in an ICC profile match a double-precision colour conversion within 1;
- `ToSrgb` matches `djxl --color_space=sRGB` within the lossy tolerance.

## Validation

`tests/assets/corpus` holds small synthetic files made by `tests/assets/generate-corpus.py` with cjxl v0.12.0.
`corpus.json` records their `jxlinfo` output and the SHA-256 of djxl's 8-bit RGBA pixels; lossy files keep djxl's
rendering in `corpus/djxl/*.png`, files with a test ICC profile a double-precision conversion in `corpus/cms/*.png`,
and wide-gamut files djxl's sRGB rendering in `corpus/srgb/*.png`. The tests therefore do not need the tools.

Oracle tests compare damaged files with `jxlinfo`, pixels with `djxl`, ICC profiles with `djxl --orig_icc_out` and
`ToSrgb` with `djxl --color_space`. They are skipped when the tools are not on `PATH` (or `JXL_TOOLS`);
`JXL_ORACLE_DIR` adds the `.jxl` files of a local directory:

```sh
dotnet test -c Release --treenode-filter "/*/*/*/*[Category=Oracle]"
```

The reference is libjxl [v0.12.0](https://github.com/libjxl/libjxl/releases/tag/v0.12.0) (commit
`a7a9c787341cf703dede03c2009fa460cae5e5df`), pinned with its SHA-256 in `scripts/reference-lock.json`. Its sources
are kept in `reference/libjxl-0.12.0/`, ignored by Git and excluded from the package; no native code is built or
linked. Re-fetch them with Python 3.12+:

```sh
python3 scripts/fetch-reference.py
```
