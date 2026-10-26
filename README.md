# PictureSharp.Jxl

A pure C# JPEG XL format plugin scaffold for PictureSharp, targeting .NET 10 and C# 14.

## Status: 0.1.0 scaffold

Available: explicit plugin registration, `.jxl` / `image/jxl` mapping, detection of
raw JPEG XL codestream and container signatures. Detection does not validate a complete file.

Not implemented: `Image.Load`, `Image.Identify`, encoding and metadata reading/editing.
Load and Identify throw `NotSupportedException`. No encoder options or SaveAsJxl API exist yet.
The SPI requires a codec to register a module, so a placeholder decoder is registered:
**CanDecode returns true, but does not indicate working decoding in this scaffold.**
CanEncode returns false.

```csharp
using PictureSharp;
using PictureSharp.Formats.Jxl;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder()
    .AddFormat(new JxlFormat()).Build();
byte[] header = [0xFF, 0x0A];
ImageFormat? format = Image.DetectFormat(header); // ImageFormat.Jxl
```

## Installation and verification

Download PictureSharp.Jxl.0.1.0.nupkg from the private GitHub pre-release and place it
in a local NuGet feed. Add that feed alongside nuget.org (for the PictureSharp dependency),
then reference PictureSharp.Jxl 0.1.0. This package is not published to nuget.org or GitHub Packages.
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

## Planned development

Container parsing and Identify, Modular lossless decoding, VarDCT decoding, SDR color
and metadata, lossless and lossy encoding, then fuzzing and optimization. The first cycle
covers static SDR RGB, grayscale and alpha. HDR, animation, other extra channels and
original JPEG reconstruction are excluded. Detailed planning is kept in local plan.md.

Reference: [JPEG XL format overview](https://github.com/libjxl/libjxl/blob/main/doc/format_overview.md).
License: MIT. See THIRD-PARTY-NOTICES.txt.
