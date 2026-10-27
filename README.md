# PictureSharp.Jxl

A pure C# JPEG XL format plugin for PictureSharp, targeting .NET 10 and C# 14.

## Status: 0.2.1 — container, headers and Identify

Available:

- explicit plugin registration and the `.jxl` / `image/jxl` mapping;
- detection of raw codestream and container signatures (detection does not validate a file);
- `Image.Identify` for raw codestreams and ISO BMFF containers: dimensions, colour and channel
  layout, colour description, EXIF-style orientation and animation.

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

Identify reads the container, the codestream headers (SizeHeader, ImageMetadata including colour
encoding, tone mapping and extra channels, transform data and the ICC size) and checks them like
libjxl v0.12.0. It does not validate frames. `DecoderOptions.MaxPixels` applies; widths above
`int.MaxValue` throw `NotSupportedException`.

Container rules: `JXL ` signature box first, `ftyp` second (brand `jxl `, file format version 0
or 1), one `jxlc` box or a `jxlp` sequence, never both. `jxlp` counters must be unique and
contiguous from 0. Version 0 requires them in file order; version 1 also accepts out-of-order
parts, as libjxl v0.12.0 does. Metadata boxes (`Exif`, `xml `, `jumb`, `brob`, `jbrd`, `jxli`,
`jhgm`) are skipped.

Not implemented: pixel decoding (`Image.Load` throws `NotSupportedException`), encoding,
metadata reading/editing and the ICC profile itself. The SPI requires a decoder for Identify, so
**CanDecode returns true, but Load does not work in this version.** CanEncode returns false.

```csharp
using PictureSharp;
using PictureSharp.Formats.Jxl;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder()
    .AddFormat(new JxlFormat()).Build();
ImageInfo info = Image.Identify("photo.jxl");
Console.WriteLine($"{info.Width}x{info.Height}, {info.Color.Model} {info.Color.BitsPerSample}-bit, alpha: {info.Color.AlphaMode}");
```

Corrupt or truncated headers throw `ImageFormatException`; recognized features that are not
supported throw `NotSupportedException`.

## Installation and verification

Download PictureSharp.Jxl.0.2.1.nupkg from the private GitHub pre-release and place it
in a local NuGet feed. Add that feed alongside nuget.org (for the PictureSharp dependency),
then reference PictureSharp.Jxl 0.2.1. This package is not published to nuget.org or GitHub Packages.
The only library dependency is PictureSharp >= 0.8.5; no native dependencies or unsafe code.

```sh
dotnet build -c Release
dotnet test -c Release
dotnet pack src/PictureSharp.Jxl -c Release --no-build -o artifacts
bash scripts/verify-package-consumer.sh
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

## Tests and corpus

`tests/assets/corpus` holds small synthetic files made by `tests/assets/generate-corpus.py` with
cjxl v0.12.0 and a `corpus.json` oracle from jxlinfo; the tests do not need the tools. Oracle
tests compare damaged files with jxlinfo and are skipped when it is not on `PATH` (or `JXL_TOOLS`):

```sh
dotnet test -c Release --treenode-filter "/*/*/*/*[Category=Oracle]"
```

## Planned development

Modular lossless decoding, VarDCT decoding, SDR color and metadata, lossless and lossy encoding,
then fuzzing and optimization. The first cycle covers static SDR RGB, grayscale and alpha. HDR,
animation, other extra channels and original JPEG reconstruction are excluded. Detailed planning
is kept in local plan.md.

Reference: [JPEG XL format overview](https://github.com/libjxl/libjxl/blob/main/doc/format_overview.md).
License: MIT. See THIRD-PARTY-NOTICES.txt.
