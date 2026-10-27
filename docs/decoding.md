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
| `Color.HasIccProfile` | The colour encoding declares an ICC profile (the profile is not read) |
| `Color.Cicp` | The enumerated colour encoding as H.273 code points when it maps exactly; null with an ICC profile, custom primaries or white point, a gamma exponent or the XYB colour space |
| `Compression` | `Lossy` for XYB-encoded images; otherwise `Unknown`, because the codestream headers do not say whether Modular coding is lossless |
| `Orientation` | The codestream orientation 1–8 (an EXIF box is not consulted) |
| `IsAnimated`, `FrameCount` | Animation header present; `FrameCount` is null for animations (frames are not counted yet), else 1 |
| `Resolution` | null (resolution metadata is not read yet) |

## Load

`Image.Load` decodes still images whose single frame is Modular without XYB, which is what
`cjxl -d 0` produces for PNG, PPM and JPEG input (`-j 0`). It implements the entropy coding (ANS
and prefix codes, LZ77, clustered contexts), meta-adaptive trees with all predictors including
the weighted predictor, the RCT, palette (with delta and implicit entries) and squeeze transforms,
groups, LF groups and progressive passes. Pixels match djxl v0.12.0 exactly at 8 bits per sample.

- Samples become `Rgba32` with straight alpha. Integer samples of any depth are scaled like libjxl:
  to [0, 1] in single precision, times 255, rounded to nearest even. Deeper samples (10, 12, 16 bits)
  are therefore reduced to 8 bits.
- Grayscale is replicated to RGB. The first alpha extra channel becomes alpha. Other extra channels
  (depth, selection mask, thermal, …) are decoded and dropped.
- Pixels stay in the image's colour space, which `ImageInfo.Color` describes. Colour management is
  not applied and the ICC profile is not attached yet.
- The orientation is not applied: like JPEG with EXIF, `Image.Metadata.Orientation` carries it, and
  `DecoderOptions.ApplyExifOrientation` or `AutoOrient()` applies it.
- A preview frame is skipped. `DecoderOptions.MaxPixels` applies before allocation. The decoder keeps
  32-bit working planes, about 4 bytes per sample per channel, and checks cancellation between
  sections and channels.

`NotSupportedException` for valid images outside this subset:

- lossy (VarDCT or XYB) images, animations, floating-point samples;
- several frames (layers, reference frames, patches), noise, splines, upsampling, cropped or
  blended frames, restoration filters;
- CMYK (Black) and spot colour channels; premultiplied, float or subsampled alpha.

Not implemented: encoding, metadata reading/editing and the ICC profile itself. CanEncode returns false.

Corrupt or truncated data throws `ImageFormatException`; recognized features that are not
supported throw `NotSupportedException`.

## Tests and corpus

`tests/assets/corpus` holds small synthetic files made by `tests/assets/generate-corpus.py` with
cjxl v0.12.0. `corpus.json` records their jxlinfo output and the SHA-256 of the RGBA pixels djxl
renders at 8 bits, so the tests do not need the tools. Oracle tests compare damaged files with
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
