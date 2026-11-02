## Writing animations

0.8.14 writes JPEG XL animations. `SaveAsJxl`, `ToJxl` and `image.Save` now write every frame of an image with
several frames (before, only the first frame was written), and `Image.CreateFrameWriter` writes an animation one frame
at a time, so a long clip never has to be held in memory whole.

```csharp
using Image clip = Image.Load("clip.gif");
clip.SaveAsJxl("clip.jxl", new JxlEncoderOptions { Distance = 0 });   // all frames, durations and LoopCount

await using ImageFrameWriter writer = Image.CreateFrameWriter("long.jxl", new Size(640, 360), new JxlEncoderOptions());
foreach (Image frame in frames) await writer.WriteAsync(frame, TimeSpan.FromMilliseconds(40));
await writer.CompleteAsync();
```

- **Read back the same way:** `Image.Load`, `Image.OpenFrameReaderAsync` and djxl return the written frames: lossless
  exactly, lossy within the usual tolerance with exact alpha. 8- and 16-bit frames, metadata and every encoder option
  work as for still images.
- **Timing:** durations are stored in milliseconds (1000 ticks per second, up to 2^32 − 1 ms per frame); `LoopCount`
  is the number of plays, 0 forever.
- **Smaller files:** each frame after the first stores only the rectangle that changed since the previous frame.
  Files are about as small as `cjxl` output for the same frames, and much smaller when `cjxl` gets full frames.
- **Fewer frames, same animation:** a frame identical to the previous one extends its duration instead of being
  stored again, and a zero-duration frame (other than the last) is merged into the next one by every JPEG XL decoder.
  `Image.Load` then returns fewer frames than were written.
- **Frame writer:** written forward only, so any writable stream works; it holds one encoded frame and the previous
  canvas. Later frames are not known when the headers are written, so frames are stored as RGB with alpha (gray only
  with a gray ICC profile); the decoded pixels are the same.
  ([design](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.14/docs/design/0.8.14-animation-encode.md),
  [guide](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.14/docs/encoding.md#animations))

Still images are written byte for byte as in 0.8.13. PictureSharp 0.9.1 or later is still required.

Validation: 927 tests, including round trips of every animated test file through `Image.Save` and the frame writer,
and djxl decoding written animations (lossless exactly, lossy within tolerance). Golden results at 1 and 16 threads
are identical to each other; compared with 0.8.13 only the encoded output of the ten animated test files changed (one
file now reports its out-of-range frame duration). Fuzzing: the new `encode-frames` target, 117,596 inputs, no findings.
The package passes the JIT and Native AOT consumer checks, which now also write an animation frame by frame.

NuGet package: [PictureSharp.Jxl 0.8.14](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.14).
`dotnet add package PictureSharp.Jxl`.
