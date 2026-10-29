# Decoding JPEG XL

Examples assume `JxlFormat` is registered in `ImageConfiguration.Default` (see the README).

```csharp
using Image image = Image.Load("photo.jxl");                               // straight-alpha Rgba32, orientation not applied
ImageInfo info = Image.Identify("photo.jxl");                              // size and colour layout, no frame decoding
ImageMetadata metadata = Image.ReadMetadata("photo.jxl");                  // ICC, EXIF, XMP, orientation, no frame decoding
```

Raw codestreams (`FF 0A`) and box containers (`JXL `) are both recognized. See also the [metadata guide](metadata.md).

## What is decoded

Still images, lossy and lossless, as `cjxl` produces them:

| | Supported |
| --- | --- |
| VarDCT (lossy) | all 27 transforms (2×2 to 256×256, AFV), custom quantisation tables, chroma from luma, adaptive DC smoothing, coefficient orders, progressive AC passes |
| Modular | ANS and prefix codes, LZ77, meta-adaptive trees with every predictor, RCT, palette, squeeze; lossless, and lossy with XYB |
| Recompressed JPEGs | YCbCr incl. 4:2:0, 4:2:2, 4:4:0, rendered as libjxl renders them (not as the reconstructed JPEG) |
| Frames of one image | reference frames with patches, DC frames (`cjxl -p`); a preview frame is skipped |
| Rendering | Gaborish, edge-preserving filter, 2×/4×/8× upsampling, XYB and YCbCr to RGB |
| Colour | RGB and grayscale with alpha; sRGB, linear, gamma, BT.709, DCI and PQ transfer functions |
| Samples | integers up to 16 bits; lossy images of any declared depth |

`NotSupportedException`: animations; layers, cropped or blended frames; photon noise and splines; float samples
(except XYB); HLG with XYB; CMYK and spot colours; premultiplied or float alpha.

### Conversion to 8 bits

By default `Image` holds `Rgba32`, and samples are reduced as libjxl does for 8-bit output:

- 32×32 blue-noise dither, clamping, round-half-to-even; 10–16-bit integers are first scaled to [0, 1] in single precision.
- Grayscale is replicated to RGB. The first alpha channel becomes alpha; other extra channels are decoded and dropped.

### Preserving sample precision

With `DecoderOptions.PreserveBitDepth = true`, colour or alpha declared above 8 bits returns `Rgba64`; 8-bit files
still return `Rgba32`. Rendered float samples are clamped and rounded once to 0–65535, without an 8-bit intermediate
or dithering. Grayscale is replicated to RGB. A 16-bit output pixel counts twice against `MaxPixels`.

```csharp
using Image image = Image.Load("scan.jxl", new DecoderOptions { PreserveBitDepth = true });
```

`ToSrgb` of a deeper source converts before reducing to `Rgba32` when that output is requested. XYB is rendered
directly to sRGB; Modular profiles use PictureSharp's 16-bit colour conversion.

### Colour

Pixels stay in the image's colour space:

- Lossless images and recompressed JPEGs keep their samples.
- Lossy (XYB) images are rendered into their enumerated colour space or their matrix/TRC ICC profile (the usual
  camera and display kind); with a lookup-table profile they become sRGB without the profile.

`ColorConversion = ToSrgb` renders XYB straight to sRGB in floating point (like `djxl --color_space=sRGB`), clipping
out-of-gamut colours; other images are converted by PictureSharp. PQ and HLG throw with `ToSrgb`.

```csharp
using Image srgb = Image.Load("wide-gamut.jxl", new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb });
```

## Identify

Reads and validates the container and codestream headers, without frames:

| `ImageInfo` | Value |
| --- | --- |
| `Width`, `Height` | stored size; orientation reported, not applied |
| `Color.Model` | `Rgb` (also XYB), `Gray`, `Cmyk` with a Black channel, `Other` for the XYB colour space |
| `Color.ChannelCount` | colour channels (1 or 3) plus extra channels |
| `Color.BitsPerSample`, `SampleFormat` | widest channel: integers 1–31 bits, floats 5–32 bits; `Float` if any channel is float |
| `Color.AlphaMode` | `Straight`, `Premultiplied` or `None` |
| `Color.HasIccProfile` | an ICC profile is declared |
| `Color.Cicp` | H.273 code points when they map exactly; null with ICC, custom primaries, gamma or XYB |
| `Compression` | `Lossy` for XYB, else `Unknown` (headers don't say if Modular is lossless) |
| `Orientation` | codestream orientation 1–8 |
| `IsAnimated`, `FrameCount` | `FrameCount` is 1 for still images, null for animations |
| `Resolution` | null |

Identify also reports features `Load` rejects. Widths above `int.MaxValue` throw `NotSupportedException`.

## Options, limits and errors

- `MaxPixels` and `MaxMetadataBytes` are checked before allocating; metadata is charged as it grows, so a
  decompression bomb stops with `ImageFormatException`.
- Other `DecoderOptions` work as for built-in formats; `TargetSize` resizes after full decoding.
- Memory: ~12 bytes per pixel per frame buffer, two buffers while filtering.
- `MaxDegreeOfParallelism` (default -1, all cores; 1 = single-threaded) limits the threads that decode LF and pass
  groups and render row bands. Pixels and exceptions do not depend on it; limits are checked before any worker starts.
- Corrupt or truncated data (also a corrupt ICC stream with `IgnoreMetadata`): `ImageFormatException`.
- Container rules follow libjxl: `JXL ` box first, `ftyp` second, one `jxlc` or contiguous `jxlp` parts, never both.

## Exactness

The port follows libjxl v0.12.0 operation by operation (FMAs, polynomial approximations, dither):

- lossless: identical to `djxl`;
- lossy: at most 1 off in a few samples (< 0.01%), due to libjxl's approximate reciprocals;
- lossy in an ICC profile, and `ToSrgb`: within 1 of the reference.

## Validation

`tests/assets/corpus` holds small synthetic files (`generate-corpus.py`, cjxl v0.12.0) with recorded `jxlinfo`
output, pixel hashes and reference renderings, so tests don't need the tools.

Oracle tests compare against `jxlinfo` and `djxl` and are skipped without the tools on `PATH` (or `JXL_TOOLS`);
`JXL_ORACLE_DIR` adds local `.jxl` files:

```sh
dotnet test -c Release --treenode-filter "/*/*/*/*[Category=Oracle]"
```

The reference is libjxl [v0.12.0](https://github.com/libjxl/libjxl/releases/tag/v0.12.0) (commit
`a7a9c787341cf703dede03c2009fa460cae5e5df`), pinned in `scripts/reference-lock.json`; sources go to the
Git-ignored `reference/libjxl-0.12.0/` (no native code is built). Fetch with Python 3.12+:

```sh
python3 scripts/fetch-reference.py
```

## Memory in 0.7.10

Eligible lossless Modular frames write integer channels directly to the output pixels. They retain the
same float normalization and dithering or 16-bit rounding, while avoiding intermediate float planes.
Frames requiring colour transforms, filtering, patches or upsampling continue through the float pipeline.

Working channels, output planes, group coefficients and predictor scratch use explicitly scoped pools.
Reservations are checked before the output allocation; pooling does not add a limit on all temporary memory.
See the [allocation and peak RSS measurements](benchmarks/speed.md#allocations-and-memory-0710).
