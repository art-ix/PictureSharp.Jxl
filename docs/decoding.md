# Decoding JPEG XL

## Identify

Identify reads the container, the codestream headers (SizeHeader, ImageMetadata including colour
encoding, tone mapping and extra channels, transform data and the ICC size) and checks them like
libjxl v0.12.0. It does not validate frames. `DecoderOptions.MaxPixels` applies; widths above
`int.MaxValue` throw `NotSupportedException`.

Container rules: `JXL ` signature box first, `ftyp` second (brand `jxl `, file format version 0
or 1), one `jxlc` box or a `jxlp` sequence, never both. `jxlp` counters must be unique and
contiguous from 0. Version 0 requires them in file order; version 1 also accepts out-of-order
parts, as libjxl v0.12.0 does. Metadata boxes (`Exif`, `xml `, `jumb`, `brob`, `jbrd`, `jxli`,
`jhgm`) are skipped.

| `ImageInfo` field | Value |
| --- | --- |
| `Width`, `Height` | Stored size from the codestream SizeHeader; orientation is reported, not applied |
| `Color.Model` | `Rgb` (also when XYB-encoded), `Gray`, `Cmyk` with a Black extra channel, `Other` for the XYB colour space |
| `Color.ChannelCount` | Colour channels (1 or 3) plus every extra channel (alpha, depth, spot colours, black, …) |
| `Color.BitsPerSample`, `Color.SampleFormat` | The widest channel, colour or extra: 1–31 for integers, 5–32 for floats; `Float` if any channel is float |
| `Color.AlphaMode` | `Straight` or `Premultiplied` (`alpha_associated`) for an alpha extra channel, else `None` |
| `Color.HasIccProfile` | The colour encoding declares an ICC profile (Identify does not decode it; Load and ReadMetadata do) |
| `Color.Cicp` | The enumerated colour encoding as H.273 code points when it maps exactly; null with an ICC profile, custom primaries or white point, a gamma exponent or the XYB colour space |
| `Compression` | `Lossy` for XYB-encoded images; otherwise `Unknown`, because the codestream headers do not say whether Modular coding is lossless |
| `Orientation` | The codestream orientation 1–8 (an EXIF box is not consulted) |
| `IsAnimated`, `FrameCount` | Animation header present; `FrameCount` is null for animations (frames are not counted yet), else 1 |
| `Resolution` | null (resolution metadata is not read yet) |

## Load

`Image.Load` decodes still JPEG XL images, lossy and lossless, as `cjxl` produces them:

- **VarDCT** (lossy): all 27 transforms from 2×2 to 256×256 including AFV, custom quantisation tables,
  chroma from luma, adaptive DC smoothing, coefficient orders, progressive AC passes and several
  histogram sets.
- **Modular**: entropy coding (ANS and prefix codes, LZ77, clustered contexts), meta-adaptive trees
  with all predictors including the weighted predictor, the RCT, palette and squeeze transforms,
  groups, LF groups and progressive passes; lossless, and lossy with XYB.
- **Recompressed JPEGs** (`cjxl` on JPEG input): YCbCr with 4:2:0, 4:2:2 and 4:4:0 chroma subsampling,
  rendered as libjxl renders them (not as the reconstructed JPEG file).
- **Several frames** of one still image: reference frames with patches (cjxl codes text and graphics
  this way) and DC frames (`cjxl -p`).
- **Rendering**: Gaborish and the edge-preserving filter, 2×, 4× and 8× upsampling of colour and
  extra channels, XYB to RGB in the image's colour space with its transfer function (sRGB, linear,
  gamma, BT.709, DCI, PQ) and YCbCr to RGB.

The port follows libjxl v0.12.0 operation by operation, including fused multiply-adds, its rational
polynomial approximations and the ordered dither libjxl applies to 8-bit output. Lossless images
match djxl exactly. Lossy images differ by at most 1 in a few samples (typically under 0.01%), since
libjxl uses hardware approximate reciprocals in dequantisation.

- Samples become `Rgba32` with straight alpha. Rendered samples are scaled to 0–255 as libjxl writes
  8-bit output: plus a 32×32 blue-noise dither, clamped, rounded to nearest even. Integer samples
  (10, 12, 16 bits) are scaled to [0, 1] in single precision first, so they are reduced to 8 bits.
- Grayscale is replicated to RGB. The first alpha extra channel becomes alpha. Other extra channels
  (depth, selection mask, thermal, …) are decoded and dropped.
- Pixels stay in the image's colour space, which `ImageInfo.Color` and the metadata describe. XYB images
  are rendered in their enumerated colour space (P3, BT.2100 and custom primaries are converted from XYB
  through XYZ, as libjxl does). XYB images with an ICC profile are rendered in that profile when it is a
  matrix/TRC RGB or a gray TRC profile (the profiles of cameras, phones and displays): linear sRGB to
  XYZ D50, the inverse colourant matrix and the inverse tone curves (`curv` gamma or table, `para`), where
  libjxl uses a colour management system. Profiles with lookup tables (A2B/B2A) are rendered as sRGB and
  the profile is not attached, so the samples are never labelled with a profile they are not in.
- The orientation is not applied: like JPEG with EXIF, `Image.Metadata.Orientation` carries it, and
  `DecoderOptions.ApplyExifOrientation` or `AutoOrient()` applies it once (see Metadata).
- A preview frame is skipped. `DecoderOptions.MaxPixels` applies before allocation. The decoder
  renders whole frames in single precision (about 12 bytes per pixel per buffer, two buffers while
  filtering) and checks cancellation between sections and groups. It is a scalar port; SIMD and
  multithreading come later.

`NotSupportedException` for valid images outside this subset:

- animations and floating-point samples (other than XYB images, whose bit depth is only metadata);
- several displayed frames (layers), cropped or blended frames;
- photon noise and splines;
- HLG transfer with XYB (HDR);
- CMYK (Black) and spot colour channels; premultiplied or float alpha.

Not implemented: encoding and metadata editing. CanEncode returns false.

Corrupt or truncated data throws `ImageFormatException`; recognized features that are not
supported throw `NotSupportedException`.

## Metadata

`Image.Load` (unless `DecoderOptions.IgnoreMetadata`) and `Image.ReadMetadata` report:

- **ICC profile:** decoded from the codestream (the entropy-coded stream and libjxl's ICC prediction, as
  `djxl --orig_icc_out` writes it) and attached when the samples are in it. `ReadMetadata` always reports the
  file's profile, also for the lookup-table case above. libjxl decodes the profile for every image, so a
  corrupt profile stream fails Load with `ImageFormatException` even with `IgnoreMetadata`; profiles are
  limited to 256 MiB as in libjxl.
- **Colour description** for enumerated colour encodings, as for PNG and HEIF: chromaticities for
  primaries or white points other than sRGB/D65 and a gamma (the exponent, 1/2.2 for sRGB-like and BT.709
  curves, 1 for linear, 1/2.6 for DCI). sRGB needs none. PQ and HLG cannot be described;
  `ColorConversion = ToSrgb` throws `NotSupportedException` for them.
- **EXIF and XMP:** the first `Exif` box (its four-byte TIFF offset removed) and the first `xml ` box,
  plain or Brotli-compressed in `brob` boxes (decompressed with the BCL `BrotliDecoder`). Malformed Exif
  boxes and corrupt or truncated Brotli streams are skipped; JUMBF and other boxes are not read.
- **Orientation:** the codestream orientation is authoritative (ISO/IEC 18181-2), so it replaces the
  EXIF orientation tag (or creates one). `ApplyExifOrientation` therefore rotates exactly once, even when
  an Exif box disagrees.

ICC, EXIF and XMP bytes are charged against `DecoderOptions.MaxMetadataBytes` before they are
materialized; Brotli output is charged as it grows, so a decompression bomb stops at the limit with
`ImageFormatException`. With `IgnoreMetadata` nothing is charged or reported (the orientation still is).
`Image.EditMetadata` throws `NotSupportedException`.

## Tests and corpus

`tests/assets/corpus` holds small synthetic files made by `tests/assets/generate-corpus.py` with
cjxl v0.12.0. `corpus.json` records their jxlinfo output and the SHA-256 of the RGBA pixels djxl
renders at 8 bits; lossy files also keep djxl's rendering in `corpus/djxl/*.png`, compared within the
tolerance above. Lossy files with a test ICC profile (Adobe RGB-like primaries, a 1024-point tone curve
cjxl cannot express as an enumerated encoding) keep `corpus/cms/*.png`: djxl's linear sRGB floats
converted to the profile in double precision, compared within 1 (the decoder dithers, the reference
rounds). The oracle tests also check every ICC profile against `djxl --orig_icc_out` and, for such
images, PictureSharp's sRGB conversion against `djxl --color_space` (mean difference at most 0.5). The tests therefore do not need the tools. Oracle tests compare damaged files with
jxlinfo and decoded pixels with djxl, and are skipped when the tools are not on `PATH` (or
`JXL_TOOLS`). `JXL_ORACLE_DIR` adds the `.jxl` files of a local directory to the pixel comparison:

```sh
dotnet test -c Release --treenode-filter "/*/*/*/*[Category=Oracle]"
```

## Reference sources

The baseline is the latest stable official libjxl release checked on 2026-10-26:
[v0.12.0](https://github.com/libjxl/libjxl/releases/tag/v0.12.0), commit
`a7a9c787341cf703dede03c2009fa460cae5e5df`. The source archive SHA-256 and URL
are recorded in `scripts/reference-lock.json`. Sources are kept locally in
`reference/libjxl-0.12.0/`, ignored by Git and excluded from the package.
The archive contains libjxl sources, not the separate Git submodules or test corpus.
No native code is built or linked by this plugin. Re-fetch with Python 3.12+:

```sh
python3 scripts/fetch-reference.py
```
