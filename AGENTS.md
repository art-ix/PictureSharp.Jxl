# Working on PictureSharp.Jxl

Read local plan.md and the latest progress.md entries when present. Both are ignored;
keep them local and do not force-add them. Update progress.md after significant work;
keep it at most 600 lines by condensing the oldest entries.

- Target net10.0 / C# 14. Library dependencies: PictureSharp NuGet and BCL only.
- Use public SPI only; no core project reference, copied internals, reflection,
  InternalsVisibleTo, native libraries, P/Invoke or unsafe code in the library.
- Public API: PictureSharp.Formats.Jxl. Implementation types stay internal.
- Keep 0.7.x honest: Decode handles still single-image codestreams (VarDCT, Modular, patches, DC
  frames) and throws NotSupportedException for every other valid feature until it is implemented.
  The encoder writes one 8-bit or 16-bit frame: lossy VarDCT (default, distance 1.0) or lossless Modular;
  metadata is read, written and edited (ICC, EXIF, XMP, brob via the BCL Brotli codecs). Lossless
  output must decode exactly with djxl; lossy output within 1 of djxl, alpha exact.
- 0.7.1 JxlTranscoder preserves sequential/progressive 8-bit Huffman gray/YCbCr JPEG as integer coefficients,
  plus jbrd and metadata; ToJpeg must reconstruct byte for byte, interoperating with cjxl/djxl 0.12.0.
  Reconstruction bypasses IDCT/rendering. No automatic lossy fallback. See docs/transcoding.md for limits.
- Preserve nullable, warnings as errors, XML docs, trimming and Native AOT compatibility.
- Future decoders reserve pixels/metadata before allocation and honor cancellation.
  Corrupt data: ImageFormatException; unsupported features: NotSupportedException.
- Pixels use straight alpha: Rgba32 by default, Rgba64 for sources above 8 bits with PreserveBitDepth. Encode Rgba64 without an 8-bit intermediate; charge its pixels twice through ReservePixels.
- Port scalar code first from pinned references, record origin and license notices.
- Tests use TUnit / Microsoft.Testing.Platform; public API changes require snapshot review.
  Parsers under src/.../Container and Codestream stay self-contained (they may use only
  JxlDescriptor) because the tests compile them directly. Regenerate the synthetic corpus with
  tests/assets/generate-corpus.py; Category=Oracle tests need jxlinfo and skip without it.
- Build/test Release, pack, then run scripts/verify-package-consumer.sh before release.
- Never commit private photos, external corpora, references, build outputs or local logs.
- Commit/push/publication only when requested. The repository and its GitHub
  releases are public (user decision, 2026-10-27). Release tags also push the package to nuget.org via
  Trusted Publishing (environment `nuget`); a manual Release run with a tag republishes an existing release.
- VERSION is the single source of package version. Documentation and identifiers use English.

Reference baseline: libjxl v0.12.0, pinned in scripts/reference-lock.json. Treat downloaded sources as read-only. User authorized this source download on 2026-10-26.

CI scope (user decision, 2026-10-26): Linux only for 0.1.0, including package consumer and Native AOT. Revisit platform coverage when implementing codecs and SIMD.
