# Working on PictureSharp.Jxl

Read local plan.md and the latest progress.md entries when present. Both are ignored;
keep them local and do not force-add them. Update progress.md after significant work;
keep it at most 600 lines by condensing the oldest entries.

- Target net10.0 / C# 14. Library dependencies: PictureSharp NuGet and BCL only.
- Use public SPI only; no core project reference, copied internals, reflection,
  InternalsVisibleTo, native libraries, P/Invoke or `unsafe` blocks/pointers in the library.
- Low-level helpers (user decision, 2026-10-29): `MemoryMarshal`, `Unsafe.Add`/`ReadUnaligned` and `SkipLocalsInit`
  are allowed in measured hot loops. They skip bounds checks: validate logical lengths explicitly before the loop,
  keep a test at every boundary, and use them only with a measured gain. `SkipLocalsInit` needs
  `AllowUnsafeBlocks=true` (CS0227); if enabled, a test must still assert that no `unsafe` keyword exists in src.
- Output identity (user decision, 2026-10-29): decoder output stays matched to djxl as before. From 0.8.3 the encoder
  may change bytes when size stays within ±0.5% and ssimulacra2/butteraugli do not degrade on the test set; record a
  new golden baseline per release. Lossless effort tiers like libjxl (e1 fast path, e2–e3 fixed trees, e4+ learned)
  came in 0.8.3 (JxlModularEffort); 8-bit e7 output unchanged, > 8-bit lossless may change (palette above 32767,
  channel compaction; user decision 2026-10-29). 16-bit target: decode/encode ≤ 1.1× the 8-bit time of the same image.
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
- Roadmap (user decision, 2026-10-29): 0.8.0 SIMD, 0.8.1 group parallelism,
  0.8.2–0.8.5 further improvements, 0.8.6–0.9.0 fuzzing and resilience.
  Follow docs/design/0.8.0-simd.md (AVX2/SSE4.1, FMA checked separately). Vector implementations belong in sibling `*.Simd.cs`
  partial-class files (same capitalization as the core); keep scalar fallback, 0.7.10 bit identity (until 0.8.3, see above),
  explicit FMA/reduction semantics, logical buffer bounds and the no-`unsafe`-blocks constraint.
- Parallelism (0.8.1, docs/design/0.8.1-parallelism.md): `MaxDegreeOfParallelism` (-1 default, 1 = sequential) on core
  `DecoderOptions`, `JxlEncoderOptions` and `JxlTranscoderOptions`. Use only `JxlParallel` (no nested regions); output,
  pixels and exceptions must be identical at 1 and N threads (golden at both), merges in item order, budgets charged
  on the calling thread, per-worker scratch released by the worker. JXL_GOLDEN_THREADS / JXL_THREADS select the degree
  in the golden tool and speed harness; scripts/measure-parallel.py measures 0.8.0 vs 1 vs N threads.
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
