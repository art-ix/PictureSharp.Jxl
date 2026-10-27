# PictureSharp.Jxl

JPEG XL for [PictureSharp](https://github.com/art-ix/PictureSharp), in pure C# with no native dependencies.
Requires .NET 10+ and PictureSharp 0.8.5+.

The package is attached to the private GitHub releases only (not nuget.org): put `PictureSharp.Jxl.0.6.1.nupkg` in a
local NuGet feed, then

```bash
dotnet add package PictureSharp.Jxl --version 0.6.1
```

## Usage

Register the format once, then load and save `.jxl` files like any other image:

```csharp
using PictureSharp;
using PictureSharp.Formats.Jxl;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();

using var image = Image.Load("photo.jxl", new DecoderOptions { ApplyExifOrientation = true });
image.Mutate(x => x.Resize(1600, 0));
image.SaveAsJxl("small.jxl");
```

Loading keeps the ICC profile, EXIF and XMP; saving is lossless and writes them back. `Image.EditMetadata` changes
the metadata without touching the frames.

## Support

- **Read:** still images as `cjxl` produces them: lossy (VarDCT) and lossless (Modular), recompressed JPEGs,
  progressive files, patches; RGB and grayscale with alpha, up to 16 bits per sample (reduced to 8).
- **Write:** lossless 8-bit gray or RGB with alpha, about as small as `cjxl -d 0 -e 7`.
- **Metadata:** ICC, EXIF, XMP (also Brotli-compressed), colour description and orientation; read, written and
  edited in place.
- **Not supported yet:** animations, layers, photon noise and splines, float samples, CMYK, lossy encoding
  (`NotSupportedException`).

Pixels match djxl (libjxl 0.12.0): lossless images exactly, lossy ones within 1 of 255 in a few samples.

**Status:** early development (0.6.1). Lossy encoding with quality and effort controls comes in 0.7.0. Not for
production use yet; the API may change. CI runs on Linux, including a Native AOT build.

Guides: [decoding](docs/decoding.md) · [encoding](docs/encoding.md) · [metadata](docs/metadata.md) ·
[architecture](docs/architecture.md) · [release notes](docs/releases)

## License

MIT. Ported libjxl sources are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Build

```bash
dotnet build -c Release
dotnet test -c Release
dotnet pack src/PictureSharp.Jxl -c Release --no-build -o artifacts
bash scripts/verify-package-consumer.sh
```
