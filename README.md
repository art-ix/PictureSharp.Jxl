# PictureSharp.Jxl

JPEG XL for [PictureSharp](https://github.com/art-ix/PictureSharp), in pure C# with no native dependencies.
Requires .NET 10+ and PictureSharp 0.9.1+.

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

| | Supported |
|---|---|
| **Read** | Lossy (VarDCT) and lossless (Modular) images, including recompressed JPEGs, progressive files and patches. Layers are flattened like `djxl` or read one by one with `JxlLayers.Load`. Animations load whole or frame by frame with `Image.OpenFrameReaderAsync`. |
| **Write** | Lossy (`Quality`/`Distance`, `Effort` 1–9) and lossless, 8 or 16 bits; files about as small as `cjxl` at the same settings. Animations are saved whole or frame by frame with `Image.CreateFrameWriter`. |
| **Pixels** | Gray or RGB, with or without alpha, up to 16 bits per sample (`Rgba64` with `PreserveBitDepth`, otherwise 8-bit). |
| **Metadata** | ICC, EXIF, XMP, colour description and orientation: read, written and edited in place. |
| **JPEG** | `JxlTranscoder` stores 8-bit Huffman JPEGs (gray or YCbCr) and restores them byte for byte. |

**Not supported yet:** images with photon noise, splines, float samples or CMYK (decoding throws
`NotSupportedException`); transcoding RGB, CMYK, arithmetic-coded or 12-bit JPEGs.

Output matches djxl (libjxl 0.12.0): lossless exactly, lossy within 1 of 255, and files written here decode the same
way with djxl.

**Status:** early development (0.8.14). Not for production use yet; the API may change. CI runs on Linux, including a Native AOT build.

Guides: [decoding](docs/decoding.md) · [encoding](docs/encoding.md) · [JPEG transcoding](docs/transcoding.md) · [metadata](docs/metadata.md) ·
[architecture](docs/architecture.md) · [release notes](docs/releases)

## Performance

- **SIMD:** AVX2/FMA and SSE4.1 kernels with a scalar fallback; all paths produce identical output.
- **Native AOT:** publish with `-p:IlcInstructionSet=x86-64-v3` (or `native`) to get the AVX2/FMA kernels; the
  default target uses SSE4.1 only.
- **Threads:** decoding, encoding and JPEG transcoding run independent groups in parallel. Set
  `MaxDegreeOfParallelism` (-1 = all cores, the default; 1 = single-threaded) on `DecoderOptions`,
  `JxlEncoderOptions` or `JxlTranscoderOptions`. The output does not depend on it.
- **Encoding effort:** lower `Effort` is faster; lossless efforts 1–3 are several times faster than 7.
- **Memory:** a 12 MP image needs about 180 MiB (JPEG-recompressed decode) to 860 MiB (lossless encode) at its peak,
  including the 48 MB output or input image.

Measurements: [speed](docs/benchmarks/speed.md).

## License

MIT. Ported libjxl sources are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Build

```bash
dotnet build -c Release
dotnet test -c Release
dotnet pack src/PictureSharp.Jxl -c Release --no-build -o artifacts
bash scripts/verify-package-consumer.sh
```
