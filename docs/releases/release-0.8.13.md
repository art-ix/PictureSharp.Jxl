## Reading animations frame by frame

0.8.13 lets `Image.OpenFrameReaderAsync` read JPEG XL, through the frame reader interface for plugins that
PictureSharp 0.9.1 added. A long animation is now read one canvas at a time instead of being decoded whole by
`Image.Load`. **PictureSharp 0.9.1 or later is required.**

```csharp
await using ImageFrameReader reader = await Image.OpenFrameReaderAsync("clip.jxl");
await foreach (ImageFrame frame in reader.ReadFramesAsync())
    Console.WriteLine($"{frame.Duration.TotalMilliseconds} ms");   // borrowed: Clone() it to keep it
Console.WriteLine($"loops: {reader.LoopCount}");
```

- **Same result as `Image.Load`:** frames, durations, pixel type (`Rgba64` with `PreserveBitDepth`), loop count and
  metadata (ICC, EXIF, XMP, colour description and orientation, in `reader.Metadata` before the first frame) are those
  of `Image.Load` with the same options. Still and layered images give one frame.
- **Limits:** `MaxPixels` now has to allow only one canvas (16-bit pixels twice), and `MaxFrames` still counts every
  frame stored in the file. The compressed input is held in memory and counts against `MaxInputBytes`. Working memory
  is a few canvases: the reference slots, the frame being decoded and one output frame reused for every frame.
- **FirstFrameOnly** returns the first frame, then reads the remaining frame headers before it ends, as `Image.Load`.
- Cancellation is checked between frames and inside them; disposing the reader at any point returns its buffers.
- `ColorConversion`, `TargetSize` and `ApplyExifOrientation` are not available in frame readers (PictureSharp rejects
  them); `Image.Load` is unchanged. Writing animations, also frame by frame, follows in 0.8.14.
  ([design](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.13/docs/design/0.8.13-frame-reader.md),
  [guide](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.13/docs/decoding.md#frame-by-frame))

Every frame, timing and metadata block read this way equals `Image.Load` for each test file, with and without
`PreserveBitDepth` and `IgnoreMetadata`, at one and 16 threads. A new 132-frame test animation (119 frames shown) reads
with `MaxPixels` of a single 16×12 canvas, which `Image.Load` rejects.

Fuzzing (short runs): the new `frames` target, which compares the reader with `Image.Load` and disposes it after a
random frame, 1.37 million inputs, and `cancel-frames`, which cancels inside and between frames, 623,000; `decode`
and `cancel-decode` 1.41 million and 362,000. No findings.

Validation: 900 tests. Golden results at 1 and 16 threads are identical to each other and to 0.8.12 (1,766 results,
`Image.Load` output unchanged). The package passes the JIT and Native AOT consumer checks, which now also read an
animation frame by frame.

NuGet package: [PictureSharp.Jxl 0.8.13](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.13).
`dotnet add package PictureSharp.Jxl`.
