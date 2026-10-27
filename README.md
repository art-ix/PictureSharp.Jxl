# PictureSharp.Jxl

JPEG XL for [PictureSharp](https://github.com/art-ix/PictureSharp), in pure C# with no native dependencies.
Requires .NET 10+ and PictureSharp 0.8.5+.

Install from [NuGet](https://www.nuget.org/packages/PictureSharp.Jxl):

```bash
dotnet add package PictureSharp.Jxl --version 0.7.3
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

Loading keeps the ICC profile, EXIF and XMP; saving writes them back. Saving is lossy at distance 1.0 like `cjxl`
by default; `new JxlEncoderOptions { Quality = 100 }` (or `Distance = 0`) saves losslessly. `Image.EditMetadata` changes
the metadata without touching the frames.

To preserve an existing JPEG exactly, use the separate transcoder without loading its pixels:

```csharp
JxlTranscoder.FromJpeg("original.jpg", "archived.jxl");
JxlTranscoder.ToJpeg("archived.jxl", "restored.jpg"); // byte-identical to original.jpg
// Stream and buffer overloads are also available, e.g. FromJpeg(jpegStream, jxlStream).
```

## Support

- **Read:** still images as `cjxl` produces them: lossy (VarDCT) and lossless (Modular), recompressed JPEGs,
  progressive files, patches; RGB and grayscale with alpha, up to 16 bits per sample (reduced to 8).
- **Write:** lossy VarDCT with `Quality`/`Distance` and `Effort` 1–9, about as small as `cjxl` at the same settings
  (patches included); lossless 8-bit gray or RGB with alpha, about as small as `cjxl -d 0 -e 7`.
- **Metadata:** ICC, EXIF, XMP (also Brotli-compressed), colour description and orientation; read, written and
  edited in place (editing files with JPEG reconstruction data is unsupported).
- **JPEG transcoding:** `JxlTranscoder.FromJpeg` stores sequential or progressive 8-bit Huffman JPEG (gray or
  YCbCr 4:4:4/4:2:2/4:2:0); `ToJpeg` reconstructs the original JPEG byte for byte, including metadata.
- **Not supported yet:** animations, layers, photon noise and splines, float samples, CMYK
  (`NotSupportedException`); writing animations and high bit depths; transcoding RGB, CMYK/YCCK, arithmetic or 12-bit JPEG.

Pixels match djxl (libjxl 0.12.0): lossless images exactly, lossy ones within 1 of 255 in a few samples. Files this
package writes decode with djxl the same way.

**Status:** early development (0.7.3). Not for production use yet; the API may change. CI runs on Linux, including a Native AOT build.

Guides: [decoding](docs/decoding.md) · [encoding](docs/encoding.md) · [JPEG transcoding](docs/transcoding.md) · [metadata](docs/metadata.md) ·
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
