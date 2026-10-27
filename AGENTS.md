# Working on PictureSharp.Jxl

Read local plan.md and the latest progress.md entries when present. Both are ignored;
keep them local and do not force-add them. Update progress.md after significant work;
keep it at most 600 lines by condensing the oldest entries.

- Target net10.0 / C# 14. Library dependencies: PictureSharp NuGet and BCL only.
- Use public SPI only; no core project reference, copied internals, reflection,
  InternalsVisibleTo, native libraries, P/Invoke or unsafe code in the library.
- Public API: PictureSharp.Formats.Jxl. Implementation types stay internal.
- Keep 0.4.x honest: Decode handles still single-image codestreams (VarDCT, Modular, patches, DC
  frames) and throws NotSupportedException for every other valid feature until it is implemented.
- Preserve nullable, warnings as errors, XML docs, trimming and Native AOT compatibility.
- Future decoders reserve pixels/metadata before allocation and honor cancellation.
  Corrupt data: ImageFormatException; unsupported features: NotSupportedException.
- Future pixels use Rgba32 with straight alpha. Document precision reduction.
- Port scalar code first from pinned references, record origin and license notices.
- Tests use TUnit / Microsoft.Testing.Platform; public API changes require snapshot review.
  Parsers under src/.../Container and Codestream stay self-contained (they may use only
  JxlDescriptor) because the tests compile them directly. Regenerate the synthetic corpus with
  tests/assets/generate-corpus.py; Category=Oracle tests need jxlinfo and skip without it.
- Build/test Release, pack, then run scripts/verify-package-consumer.sh before release.
- Never commit private photos, external corpora, references, build outputs or local logs.
- Commit/push/publication only when requested. Releases remain private; no NuGet publishing.
- VERSION is the single source of package version. Documentation and identifiers use English.

Reference baseline: libjxl v0.12.0, pinned in scripts/reference-lock.json. Treat downloaded sources as read-only. User authorized this source download on 2026-10-26.

CI scope (user decision, 2026-10-26): Linux only for 0.1.0, including package consumer and Native AOT. Revisit platform coverage when implementing codecs and SIMD.
