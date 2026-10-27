# PictureSharp.Jxl

JPEG XL for [PictureSharp](https://github.com/art-ix/PictureSharp), in pure C# with no native dependencies.
Requires .NET 10+ and PictureSharp 0.8.5+.

The package is attached to the private GitHub releases only (not nuget.org): put
`PictureSharp.Jxl.0.3.0.nupkg` in a local NuGet feed next to nuget.org, then

```bash
dotnet add package PictureSharp.Jxl --version 0.3.0
```

## Usage

Register the format once, then identify and load `.jxl` files like any other image:

```csharp
using PictureSharp;
using PictureSharp.Formats.Jxl;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();

ImageInfo info = Image.Identify("photo.jxl");
using var image = Image.Load("lossless.jxl", new DecoderOptions { ApplyExifOrientation = true });
```

## Support

- **Identify:** raw codestreams and containers; size, colour model, channels, bit depth, alpha, ICC presence,
  CICP, orientation and animation.
- **Read:** still lossless Modular images (what `cjxl -d 0` produces): RGB, grayscale and alpha, at any integer
  bit depth, reduced to 8 bits. All Modular transforms and predictors, groups and progressive passes.
- **Write:** not yet.
- **Metadata:** orientation only; the ICC profile, EXIF and XMP are not read yet.
- **Not supported yet:** lossy (VarDCT/XYB) images, animations, float samples, layered images and patches, CMYK.
  These throw `NotSupportedException`.

Pixels match djxl (libjxl 0.12.0) exactly.

**Status:** early development (0.3.0): header parsing and lossless decoding are done. Next come lossy decoding
(0.4.0), colour and metadata (0.5.0), then lossless and lossy encoding. Not for production use yet; the API may
change. CI runs on Linux, including a Native AOT build.

Guides: [decoding](docs/decoding.md) · [release notes](docs/releases)

## License

MIT. Ported libjxl sources are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Build

```bash
dotnet build -c Release
dotnet test -c Release
dotnet pack src/PictureSharp.Jxl -c Release --no-build -o artifacts
bash scripts/verify-package-consumer.sh
```
