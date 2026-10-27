# 0.7.2 — file path overloads for JPEG transcoding

First public release of PictureSharp.Jxl.

- `JxlTranscoder.FromJpeg`, `ToJpeg`, `FromJpegAsync` and `ToJpegAsync` accept an input and an output file path:

  ```csharp
  JxlTranscoder.FromJpeg("photo.jpg", "photo.jxl");
  JxlTranscoder.ToJpeg("photo.jxl", "photo-restored.jpg");
  ```

  The input file is checked against `MaxInputBytes` before reading. The output file is created or overwritten only
  after transcoding succeeds, so unsupported or corrupt input, exceeded limits and cancellation leave an existing file
  untouched. Stream and buffer overloads are unchanged.
- Rewritten, shorter [JPEG transcoding guide](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.7.2/docs/transcoding.md);
  measurements moved to [docs/benchmarks](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.7.2/docs/benchmarks/transcoding.md).

Codec behaviour and output are unchanged from 0.7.1. Packages are attached to this GitHub release only, not published
to a package registry.
