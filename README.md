# PictureSharp.Jxl

JPEG XL for [PictureSharp](https://github.com/art-ix/PictureSharp), in pure C# with no native dependencies.
Requires .NET 10+ and PictureSharp 0.8.5+.

The package is attached to the private GitHub releases only (not nuget.org): put
`PictureSharp.Jxl.0.4.2.nupkg` in a local NuGet feed next to nuget.org, then

```bash
dotnet add package PictureSharp.Jxl --version 0.4.2
```

## Usage

Register the format once, then identify and load `.jxl` files like any other image:

```csharp
using PictureSharp;
using PictureSharp.Formats.Jxl;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();

ImageInfo info = Image.Identify("photo.jxl");
using var image = Image.Load("photo.jxl", new DecoderOptions { ApplyExifOrientation = true });
```

## Support

- **Identify:** raw codestreams and containers; size, colour model, channels, bit depth, alpha, ICC presence,
  CICP, orientation and animation.
- **Read:** still images, lossy and lossless, as `cjxl` produces them: VarDCT with XYB, lossless and lossy
  Modular, recompressed JPEGs (YCbCr with chroma subsampling), progressive files, patches and DC frames,
  restoration filters and upsampling. RGB, grayscale and alpha, reduced to 8 bits.
- **Write:** not yet.
- **Metadata:** orientation only; the ICC profile, EXIF and XMP are not read yet.
- **Not supported yet:** animations, layered images, photon noise and splines, float samples, CMYK, HLG lossy
  images. These throw `NotSupportedException`.

Pixels match djxl (libjxl 0.12.0): lossless images exactly, lossy ones within 1 of 255 in a few samples.

**Status:** early development (0.4.2): header parsing, lossless and lossy decoding are done. Next come colour
management and metadata (0.5.0), then lossless and lossy encoding. Not for production use yet; the API may change.
CI runs on Linux, including a Native AOT build.

Guides: [architecture](docs/architecture.md) · [decoding](docs/decoding.md) · [release notes](docs/releases)

## License

MIT. Ported libjxl sources are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Build

```bash
dotnet build -c Release
dotnet test -c Release
dotnet pack src/PictureSharp.Jxl -c Release --no-build -o artifacts
bash scripts/verify-package-consumer.sh
```
