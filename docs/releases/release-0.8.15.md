## Photon noise and splines

0.8.15 decodes the last two image features that lossy JPEG XL files use on top of their pixels, and lets the encoder
add film grain.

```csharp
using Image photo = Image.Load("photo.png");
photo.SaveAsJxl("grain.jxl", new JxlEncoderOptions { Distance = 1, PhotonNoiseIso = 3200 });

using Image back = Image.Load("grain.jxl");   // grain rendered as djxl renders it
```

- **Photon noise:** files written with `cjxl --photon_noise_iso` (or any noise LUT) now load instead of throwing
  `NotSupportedException`. The grain is generated as libjxl generates it, from the frame's position in the file, so
  `Image.Load`, the frame reader, `JxlLayers` and every thread count return the same pixels, within 1 of 255 of djxl.
- **Splines:** smooth strokes stored as curves (for example in the libjxl conformance animations) are drawn as libjxl
  draws them. libjxl's limits on spline count, coordinates and drawn area apply, and their memory counts against the
  `MaxPixels` working budget.
- **`PhotonNoiseIso`:** a new encoder option that writes the same noise description as
  `cjxl --photon_noise_iso`, about 10 bytes per frame. It applies to lossy output only: lossless output (`Distance = 0`,
  `Quality = 100`) throws `ArgumentException` so it stays exact. Every frame of an animation gets the grain of the full
  canvas, even when only a changed rectangle is stored.
  ([design](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.15/docs/design/0.8.15-noise-splines.md),
  [guide](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.15/docs/encoding.md#photon-noise))

Images without noise or splines decode and encode exactly as in 0.8.14. PictureSharp 0.9.1 or later is still required.

Validation: 1050 tests, including every noise and spline test file and the libjxl conformance files `noise`,
`noise_5`, `animation_spline` and `animation_spline_5` against djxl (noise within 1 of 255, splines identical), and our
`PhotonNoiseIso` output decoding to the same pixels as `cjxl --photon_noise_iso` output. Golden results at 1 and 16
threads are identical to each other; compared with 0.8.14 only the 16 noise and spline test files changed (they used
to throw). Fuzzing (5 minutes per target, now also drawing `PhotonNoiseIso`): decode, frames, layers, cancellation,
encode and animation encode targets and the djxl differential target found nothing new. The package passes the JIT
and Native AOT consumer checks, which now also write and read photon noise.

Speed: noise is rendered with AVX2/FMA (bit-identical to the scalar fallback) and costs about 65 ms per
12 megapixels on one thread, close to djxl (about 50 ms); images without noise or splines take the same code path as
in 0.8.14.

NuGet package: [PictureSharp.Jxl 0.8.15](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.15).
`dotnet add package PictureSharp.Jxl`.
