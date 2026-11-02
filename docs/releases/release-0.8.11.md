## Reading layers

0.8.11 adds `JxlLayers`, which reads the layers of a layered JPEG XL still image as separate images, as
`djxl --no_coalescing` does. `Image.Load` still returns the flattened image, unchanged from 0.8.10.

```csharp
using JxlLayeredImage layered = JxlLayers.Load("drawing.jxl");
foreach (JxlLayer layer in layered.Layers)                     // bottom first
    Console.WriteLine($"{layer.Name} at {layer.Offset}: {layer.Image.Size}, {layer.BlendMode}");
using Image flattened = layered.Flatten();                     // exactly Image.Load("drawing.jxl")
```

- **Layers:** each one is an `Image` of the layer's own size with its `Offset` on the canvas (also negative or
  outside it), `Name`, `BlendMode` (Replace, Add, Blend, AlphaWeightedAdd, Multiply) and `Clamp`. `Flatten()`
  returns a copy of the flattened image, bit-identical to `Image.Load` with the same options.
- **Which files:** layers stacked the way image editors and libjxl's encoder write them: each frame blends onto
  the result of the frame before it. Reference, DC and patch frames are not layers; a single-frame image is one
  layer. Other frame structures and animations throw `NotSupportedException` pointing to `Image.Load`.
- **Options and limits:** `Load`, `LoadAsync` and `Identify` for files, streams and memory. The image is decoded
  once through `Image.Load` with the same options: `MaxPixels` covers the flattened image and every layer,
  `MaxFrames` counts them all, `ToSrgb` and `PreserveBitDepth` apply to every layer, and `ApplyExifOrientation`
  orients every layer and maps its offset onto the oriented canvas as libjxl does. `TargetSize` is not supported.
  ([design](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.11/docs/design/0.8.11-layers.md),
  [guide](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.11/docs/decoding.md#layers))

Layers match `djxl --no_coalescing` exactly for every lossless layered test file and the libjxl conformance files
`blendmodes`, `mul_no_extra_channels` and `sunset_logo`; lossy layers within 1 of 255.

A new fuzz target for the layer API (8.7 million inputs) found one defect before release, now fixed: a
reference-only frame saved after the colour transform was counted as a layer.

Validation: 642 tests, golden results at 1 and 16 threads: all 1654 identical to 0.8.10 (including two new
oriented layered test files). The package passes the JIT and Native AOT
consumer checks, which now read layers too.

NuGet package: [PictureSharp.Jxl 0.8.11](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.11).
`dotnet add package PictureSharp.Jxl`.
