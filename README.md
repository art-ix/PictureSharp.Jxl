# PictureSharp.Jxl

JPEG XL for [PictureSharp](https://github.com/art-ix/PictureSharp), in pure C# with no native dependencies.
Requires .NET 10+ and PictureSharp 0.9.0+.

Install from [NuGet](https://www.nuget.org/packages/PictureSharp.Jxl):

```bash
dotnet add package PictureSharp.Jxl
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

## Keep 16-bit samples

```csharp
using Image scan = Image.Load("scan.png", new DecoderOptions { PreserveBitDepth = true });
scan.SaveAsJxl("scan.jxl", new JxlEncoderOptions { Distance = 0 });
```

This preserves every 16-bit sample, including alpha. Default loading returns 8-bit pixels; enable `PreserveBitDepth`
when reading the result too.

## Support

- **Read:** still images as `cjxl` produces them: lossy (VarDCT) and lossless (Modular), recompressed JPEGs,
  progressive files, patches; RGB and grayscale with alpha, up to 16 bits per sample (`Rgba64` with `PreserveBitDepth`, otherwise reduced to 8).
- **Write:** lossy VarDCT with `Quality`/`Distance` and `Effort` 1–9, about as small as `cjxl` at the same settings
  (patches included); lossless 8-bit or 16-bit gray or RGB with alpha, with libjxl's effort tiers and about as small
  as `cjxl -d 0` at the same effort.
- **Metadata:** ICC, EXIF, XMP (also Brotli-compressed), colour description and orientation; read, written and
  edited in place (editing files with JPEG reconstruction data is unsupported).
- **JPEG transcoding:** `JxlTranscoder.FromJpeg` stores sequential or progressive 8-bit Huffman JPEG (gray or
  YCbCr 4:4:4/4:2:2/4:2:0); `ToJpeg` reconstructs the original JPEG byte for byte, including metadata.
- **Not supported yet:** animations, layers, photon noise and splines, float samples, CMYK
  (`NotSupportedException`); writing animations; transcoding RGB, CMYK/YCCK, arithmetic or 12-bit JPEG.

Pixels match djxl (libjxl 0.12.0): lossless images exactly, lossy ones within 1 of 255 in a few samples. Files this
package writes decode with djxl the same way.

**Performance:** AVX2/FMA and SSE4.1 kernels with a scalar fallback (identical output); Native AOT applications get the
AVX2/FMA kernels only when published with `-p:IlcInstructionSet=x86-64-v3` (or `native`). Independent groups run in
parallel: `DecoderOptions.MaxDegreeOfParallelism` (PictureSharp core), `JxlEncoderOptions` and
`JxlTranscoderOptions.MaxDegreeOfParallelism` (default -1, all cores; 1 = single-threaded); output does not depend on
it. See the [speed measurements](docs/benchmarks/speed.md).

**Status:** early development (0.8.3). Not for production use yet; the API may change. CI runs on Linux, including a Native AOT build.

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
