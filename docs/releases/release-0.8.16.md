## Extra channels

0.8.16 decodes every kind of extra channel a JPEG XL image can carry next to its colour, so files with premultiplied
alpha, spot colours or depth maps now load instead of throwing `NotSupportedException`.

```csharp
using Image image = Image.Load("print.jxl");   // spot colours rendered, alpha straight, as djxl shows them
```

- **Premultiplied alpha:** images whose alpha is stored premultiplied (associated) come back with straight alpha, like
  every PictureSharp image. Layers are still blended in premultiplied form, as libjxl blends them, and the colour is
  divided by alpha only when the pixels are written, exactly as libjxl does when asked to unpremultiply. `Identify`
  still reports `ImageAlphaMode.Premultiplied`, describing how the file stores its samples.
- **Spot colours:** inks with their own colour and opacity are mixed into the colour in channel order, as `djxl`
  renders them by default, also on blended layers, animation frames and each layer from `JxlLayers`.
- **Other channels:** depth, selection masks, thermal, optional and other channels, and further alpha channels, are
  decoded and left out of the pixels. `Identify` still counts them.
- **Still not supported:** CMYK images (a Black channel) and float samples, including float extra channels. They
  need colour management for CMYK profiles and a float pixel type in PictureSharp itself; the
  `NotSupportedException` message now says so.
  ([design](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.16/docs/design/0.8.16-extra-channels.md),
  [guide](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.16/docs/decoding.md#extra-channels))

Images without these channels decode exactly as in 0.8.15. PictureSharp 0.9.1 or later is still required.

Validation: 1316 tests, including 26 new test files (premultiplied alpha at 8 and 16 bits, gray, lossy, layers and
animations; one or two spot colours on RGB, gray, lossy, upsampled, layered and animated images; depth, selection,
thermal and optional channels; CMYK and float files that must still throw) and the libjxl conformance files `alpha_premultiplied`, `alpha_nonpremultiplied` and
`spot`. djxl cannot unpremultiply, so premultiplied files are compared with libjxl 0.12.0's decoder with
unpremultiplying on (a small test tool); everything else with djxl. Lossless files match exactly, lossy ones within
1 of 255. Golden results at 1 and 16 threads are identical to each other; compared with 0.8.15 only the new test
files changed (0.8.15 rejected them) and the wording of the CMYK and float errors. Fuzzing (5 minutes per target):
decode, frames, layers and cancellation targets and the djxl differential target, which now compares premultiplied
files with libjxl's straight-alpha output, found nothing new. The package passes the JIT and Native AOT consumer checks.

NuGet package: [PictureSharp.Jxl 0.8.16](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.16).
`dotnet add package PictureSharp.Jxl`.
