## Animations

0.8.12 decodes JPEG XL animations. `Image.Load` returns an `Image` with one frame per frame that `djxl` shows, as
PictureSharp does for animated PNG, GIF and WebP; still images decode exactly as in 0.8.11.

```csharp
using Image animation = Image.Load("clip.jxl");
Console.WriteLine($"{animation.FrameCount} frames, loops: {animation.LoopCount}");   // 0 = forever
foreach (ImageFrame frame in animation.Frames)
    Console.WriteLine($"{frame.Duration.TotalMilliseconds} ms");
```

- **Frames:** every frame `djxl` shows with its default coalescing: frames with a duration and the last one, each a
  full canvas after blending (frames of duration 0 blend into the next, as in libjxl). All frames share one pixel
  type and colour description; `ApplyExifOrientation`, `ToSrgb` and `TargetSize` apply to every frame.
- **Timing:** `Duration` converts the frame's ticks exactly (to 100 ns) for any tick rate, such as cjxl's milliseconds
  from APNG, hundredths from GIF or 30000/1001. `LoopCount` is the file's loop count (0 = forever); a result with one
  frame has 0, as for PNG and GIF. Timecodes and frame names are not kept.
- **Identify:** `FrameCount` is the number of shown frames (it was null for animations) and `IsAnimated` is true when
  there is more than one. Identify now reads the frame headers of an animation, so a file whose frames are missing
  fails.
- **Limits:** every frame is reserved before it is allocated, so `MaxPixels` sums all frames (16-bit frames twice).
  `MaxFrames` counts every frame stored in the file, also with `FirstFrameOnly`, which decodes only the first frame
  but still reads the remaining frame headers. Working memory is that of one layered image, not of the whole
  animation.
- `JxlLayers` still rejects animations, and `Image.OpenFrameReaderAsync` does not read JPEG XL (the core has no
  plugin interface for frame readers).
  ([design](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.12/docs/design/0.8.12-animation.md),
  [guide](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.12/docs/decoding.md#animations))

Every frame matches djxl: lossless animations exactly, lossy ones within 1 of 255. This holds for eleven new test
animations (from cjxl with APNG and GIF input, and from a small libjxl-based generator for loop counts, tick rates,
reference slots and blend modes cjxl does not write) and for the libjxl conformance animations `animation_icos4d`,
`animation_icos4d_5` and `animation_newtons_cradle`. Durations match `jxlinfo` and the conformance `test.json`.
`animation_spline` needs splines (0.8.14) and still throws `NotSupportedException`.

Speed: `animation_newtons_cradle` (36 frames, 480×360) decodes in 157 ms on one thread and 135 ms on all cores; djxl
takes about 151 ms.

Fuzzing (5 minutes per target): `decode` 2.46 million inputs and `cancel-decode` 609,000, no findings;
`diff-decode`, which now compares every frame with djxl, 179,000 inputs, no mismatch.

Validation: 717 tests. Golden results at 1 and 16 threads: identical to each other, and every one of the 1,757 results
is identical to 0.8.11 except those of the eleven animation files, which 0.8.11 rejected. The package passes the JIT
and Native AOT consumer checks, which now decode an animation too.

NuGet package: [PictureSharp.Jxl 0.8.12](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.12).
`dotnet add package PictureSharp.Jxl`.
