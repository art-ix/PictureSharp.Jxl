# Decoding JPEG XL

Examples assume `JxlFormat` is registered in `ImageConfiguration.Default` (see the README).

```csharp
using Image image = Image.Load("photo.jxl");                               // straight-alpha Rgba32, orientation not applied
ImageInfo info = Image.Identify("photo.jxl");                              // size and colour layout, no frame decoding
ImageMetadata metadata = Image.ReadMetadata("photo.jxl");                  // ICC, EXIF, XMP, orientation, no frame decoding
```

Raw codestreams (`FF 0A`) and box containers (`JXL `) are both recognized. See also the [metadata guide](metadata.md).

## What is decoded

Still images and animations, lossy and lossless, as `cjxl` produces them:

| | Supported |
| --- | --- |
| VarDCT (lossy) | all 27 transforms (2×2 to 256×256, AFV), custom quantisation tables, chroma from luma, adaptive DC smoothing, coefficient orders, progressive AC passes |
| Modular | ANS and prefix codes, LZ77, meta-adaptive trees with every predictor, RCT, palette, squeeze; lossless, and lossy with XYB |
| Recompressed JPEGs | YCbCr incl. 4:2:0, 4:2:2, 4:4:0, rendered as libjxl renders them (not as the reconstructed JPEG) |
| Frames of one image | reference frames with patches, DC frames (`cjxl -p`); a preview frame is skipped |
| Layers (0.8.10) | several frames blended into one image as `djxl` does by default (coalescing): cropped frames at any offset, Replace, Add, Blend, AlphaWeightedAdd and Mul for colour and each extra channel; read one by one with `JxlLayers` (0.8.11, [below](#layers)) |
| Animations (0.8.12) | every frame `djxl` shows, as a full canvas with its duration, and the loop count ([below](#animations)) |
| Rendering | Gaborish, edge-preserving filter, 2×/4×/8× upsampling, XYB and YCbCr to RGB |
| Colour | RGB and grayscale with alpha; sRGB, linear, gamma, BT.709, DCI and PQ transfer functions |
| Samples | integers up to 16 bits; lossy images of any declared depth |

Layers are blended in the image's colour encoding, as libjxl does. With `ColorConversion = ToSrgb` the blended XYB
image is then converted to sRGB in floating point (djxl cannot render blended XYB frames in another colour space);
through an ICC profile a few more samples differ by 1 than for a single frame.

`NotSupportedException`: photon noise and splines; float samples
(except XYB); HLG with XYB; CMYK and spot colours; premultiplied or float alpha.

### Conversion to 8 bits

By default `Image` holds `Rgba32`, and samples are reduced as libjxl does for 8-bit output:

- 32×32 blue-noise dither, clamping, round-half-to-even; 10–16-bit integers are first scaled to [0, 1] in single precision.
  With `ApplyExifOrientation` the dither pattern is mirrored like libjxl's, which undoes the orientation while it
  writes 8-bit output, so oriented images match `djxl` too.
- `ToSrgb` of images above 8 bits: PictureSharp decodes them at 16 bits, converts and rounds to 8 bits without
  dither, so such images differ from `djxl` by 1 in many samples (the 16-bit result matches).
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

## Layers

`Image.Load` flattens a layered image. `JxlLayers` reads the layers themselves (0.8.11), each as an `Image` of its
own size, as `djxl --no_coalescing` does, together with the flattened image:

```csharp
using JxlLayeredImage layered = JxlLayers.Load("drawing.jxl");
foreach (JxlLayer layer in layered.Layers)                                   // bottom first
    Console.WriteLine($"{layer.Name} at {layer.Offset}: {layer.Image.Size}, {layer.BlendMode}");
using Image flattened = layered.Flatten();                                   // exactly Image.Load("drawing.jxl")
JxlLayeredImageInfo info = JxlLayers.Identify("drawing.jxl");                // layers without decoding pixels
```

- **Which files:** a simple stack, as image editors and libjxl's encoder write layers: each frame blends onto the
  result of the frame before it (the first onto an empty canvas) and its extra channels come from the same
  reference slot. Reference-only, DC and patch frames are not layers; a single-frame image is one layer. Other
  frame structures and animations throw `NotSupportedException`; `Image.Load` still flattens them.
- **Pixels:** the frame before blending, in the pixel type `Image.Load` would return, with its colour description;
  `Offset` may be negative or outside the canvas. `BlendMode` (Replace, Add, Blend, AlphaWeightedAdd, Multiply) and
  `Clamp` say how it combines with the layers below; blending happens in floating point in the image's encoding, so
  `Flatten()` is the exact result.
- **Options:** the file is decoded once through `Image.Load` with the given options and limits. `MaxPixels` covers
  the flattened image and every layer, and `MaxFrames` counts them all. `ToSrgb` converts every layer.
  `ApplyExifOrientation` orients every layer and maps its offset onto the oriented canvas, like libjxl; without it
  layers are in the stored orientation (`layered.Metadata.Orientation`). `TargetSize` is not supported.
- `Dispose` releases every layer image; `Flatten()` returns a new image the caller owns.

## Animations

`Image.Load` decodes an animation into an `Image` with one `ImageFrame` per frame that `djxl` shows with its default
coalescing (0.8.12), as PictureSharp does for animated PNG, GIF and WebP:

```csharp
using Image animation = Image.Load("clip.jxl");
Console.WriteLine($"{animation.FrameCount} frames, loops: {animation.LoopCount}");   // 0 = forever
foreach (ImageFrame frame in animation.Frames)
    Console.WriteLine($"{frame.Duration.TotalMilliseconds} ms");
```

- **Frames:** a frame is shown when it has a duration or is the last one; frames of duration 0 before it are
  blended into it, as libjxl does. Every frame is the full canvas after blending, in the same pixel type and colour
  description; `ApplyExifOrientation`, `ToSrgb` and `TargetSize` apply to every frame.
- **Timing:** `Duration` is the frame's ticks × `tps_denominator` / `tps_numerator` seconds, rounded to 100 ns
  (exact for cjxl's milliseconds from APNG and hundredths from GIF); a last frame of duration 0 keeps 0.
  `LoopCount` is the header's `num_loops` (0 = forever); a result with one frame has `LoopCount` 0, as for PNG and GIF.
  Timecodes and frame names are not kept.
- **Limits:** every shown frame is reserved before it is allocated, so `MaxPixels` sums all of them (16-bit frames
  twice); `MaxFrames` counts every frame stored in the file. `FirstFrameOnly` decodes only the first frame, but still
  reads the remaining frame headers, so `MaxFrames` and `Identify` reject the same files. Working memory is that of
  one layered image (the frame being decoded and the canvases kept in reference slots), not of the whole animation.
- `JxlLayers` rejects animations.

### Frame by frame

`Image.OpenFrameReaderAsync` (PictureSharp 0.9.1, plugin 0.8.13) reads the same frames one at a time, so a long
animation needs memory and `MaxPixels` for one canvas instead of all of them:

```csharp
await using ImageFrameReader reader = await Image.OpenFrameReaderAsync("clip.jxl");
await foreach (ImageFrame frame in reader.ReadFramesAsync())
    Console.WriteLine($"{frame.Duration.TotalMilliseconds} ms");   // borrowed: Clone() it to keep it
Console.WriteLine($"loops: {reader.LoopCount}");
```

- **Same result as `Image.Load`:** the frames, durations, pixel type, loop count and metadata (ICC, EXIF, XMP, colour
  description and orientation, all in `reader.Metadata` before the first frame) equal those of `Image.Load` with the
  same options. Still and layered images give one frame. `ColorConversion`, `TargetSize` and
  `ApplyExifOrientation` are not available in frame readers (the core rejects them); convert or orient each frame
  yourself.
- **Limits:** `MaxPixels` limits one canvas (16-bit twice), `MaxFrames` every frame stored in the file, as for
  `Image.Load`. The compressed input is buffered in memory and counts against `MaxInputBytes`. Working memory is the
  input, the canvases in reference slots, the frame being decoded and the one output frame, which the reader overwrites
  for every frame.
- **FirstFrameOnly** returns the first frame, then reads the remaining frame headers before ending, as `Image.Load`.
  The frame count comes from every frame header, so a damaged later header fails when the reader opens.
- One call at a time per reader; cancellation is checked between frames and inside them, and disposing the reader at
  any point returns its buffers.

## Identify

Reads and validates the container and codestream headers; for animations also every frame header and table of
contents, without decoding the frames:

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
| `IsAnimated`, `FrameCount` | `FrameCount` is 1 for still images and the number of shown frames for animations (0.8.12; before, null); `IsAnimated` when it is above 1 |
| `Resolution` | null |

Identify also reports features `Load` rejects. Widths above `int.MaxValue` throw `NotSupportedException`.

## Options, limits and errors

- `MaxPixels` and `MaxMetadataBytes` are checked before allocating; metadata is charged as it grows, so a
  decompression bomb stops with `ImageFormatException`.
- Working memory is bounded by `MaxPixels` too (0.8.9): every buffer a decode allocates, released or not, counts
  against 64 bytes per allowed pixel plus 64 MiB, so a small file cannot declare many extra channels or frames that
  need far more memory than its image. The largest decode measured needs 36 bytes per pixel. At the default
  `MaxPixels` (2^28) this allows 16 GiB; lower `MaxPixels` to bound it further. Exceeding it throws
  `ImageFormatException` ("Decoding needs more working memory than MaxPixels allows").
- `MaxFrames` counts every frame stored in the codestream, including reference and DC frames that are not
  displayed, also with `FirstFrameOnly` and in `Identify`.
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

## Differences from libjxl

Differential runs against libjxl 0.12.0 (docs/design/0.8.8-differential.md) found no difference on valid files
beyond the tolerances above. On damaged or unusual input this library is stricter in some places and rejects with
`ImageFormatException` what libjxl still decodes. This is intended:

- **Container:** a box that runs past the end of the file, or a truncated box after the codestream, which libjxl
  never reads; an invalid box size; a second signature box, or `ftyp` not second; `jxlp` parts out of order in a
  file format version 0 file (libjxl ignores the counter), too small for their index, or after the last part.
- **Codestream:** non-zero padding bits.
- **Colour:** custom primaries or white points outside the valid chromaticity range.

It is more lenient where it can stay correct: a damaged metadata box (for example a Brotli box that does not
decompress) is skipped and the image decodes, while `djxl` fails. It also decodes a last box of size 0 (to the end of
the file, as ISO BMFF allows) after out-of-order `jxlp` parts, which `djxl` rejects.

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
