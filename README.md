# PictureSharp.Jxl

A pure C# JPEG XL format plugin for PictureSharp, targeting .NET 10 and C# 14.

## Status: 0.2.0 — container, headers and Identify

Available:

- explicit plugin registration and the `.jxl` / `image/jxl` mapping;
- detection of raw codestream and container signatures (detection does not validate a file);
- `Image.Identify` for raw codestreams and ISO BMFF containers: dimensions, bit depth, alpha,
  EXIF-style orientation and animation.

| `ImageInfo` field | Value |
| --- | --- |
| `Width`, `Height` | Stored size from the codestream SizeHeader; orientation is reported, not applied |
| `BitsPerChannel` | Declared colour bits per sample: 1–31 for integers, 5–32 for floats |
| `HasAlpha` | An alpha extra channel is declared |
| `Orientation` | The codestream orientation 1–8 (an EXIF box is not consulted) |
| `IsAnimated`, `FrameCount` | Animation header present; `FrameCount` is 0 for animations (frames are not counted yet), else 1 |
| `HorizontalResolution`, `VerticalResolution` | 96 (resolution metadata is not read yet) |

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
Console.WriteLine($"{info.Width}x{info.Height}, {info.BitsPerChannel}-bit, alpha: {info.HasAlpha}");
```

Corrupt or truncated headers throw `ImageFormatException`; recognized features that are not
supported throw `NotSupportedException`.

## Installation and verification

Download PictureSharp.Jxl.0.2.0.nupkg from the private GitHub pre-release and place it
in a local NuGet feed. Add that feed alongside nuget.org (for the PictureSharp dependency),
then reference PictureSharp.Jxl 0.2.0. This package is not published to nuget.org or GitHub Packages.
The only library dependency is PictureSharp >= 0.8.2; no native dependencies or unsafe code.

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
