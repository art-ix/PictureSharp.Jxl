# PictureSharp.Jxl 0.7.8

Read and write 16-bit JPEG XL images with PictureSharp 0.9.0. Requires .NET 10 and core 0.9.0 or later.
Earlier plugin builds cannot be registered with core 0.9.0.

```csharp
using PictureSharp;
using PictureSharp.Formats.Jxl;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new JxlFormat()).Build();
using Image scan = Image.Load("scan.jxl", new DecoderOptions { PreserveBitDepth = true });
scan.SaveAsJxl("copy.jxl", new JxlEncoderOptions { Distance = 0 });
```

- `PreserveBitDepth` returns `Rgba64` when color or alpha uses more than 8 bits. Default loading stays 8-bit.
- Lossless encoding preserves all 65536 values, including the color of fully transparent pixels.
- Lossy encoding works directly from 16-bit input; alpha stays lossless at 16 bits.
- Converting deeper sources to sRGB uses their precision before reducing to 8 bits, when requested. This can
  improve the result compared with previous versions. Existing 8-bit output and JPEG transcoding are unchanged.
- Direct pixel access uses the typed core API, for example `GetPixelSpan<Rgba64>()`.
  A 16-bit pixel counts twice against `MaxPixels`.

Validation: 467 Release tests passed, including comparison with djxl and 16-bit round trips. The actual package
passes JIT and trimmed Native AOT consumer checks. Controlled 8-bit measurements show no slowdown beyond 1%.
Details: [verification and measurements](../measurements/core-0.9.0-step5.json).

The lossy encoder speed work previously planned for 0.7.8 moves to 0.7.9. Allocation work moves to 0.7.10;
group parallelism remains planned for 0.8.0.
