# Working on PictureSharp.Jxl

Read local plan.md and the latest progres.md entries when present. Both are ignored;
keep them local and do not force-add them. Update progress after significant work.

- Target net10.0 / C# 14. Library dependencies: PictureSharp NuGet and BCL only.
- Use public SPI only; no core project reference, copied internals, reflection,
  InternalsVisibleTo, native libraries, P/Invoke or unsafe code in the library.
- Public API: PictureSharp.Formats.Jxl. Implementation types stay internal.
- Keep 0.1.0 honest: signature detection only; placeholder Identify/Decode throw.
- Preserve nullable, warnings as errors, XML docs, trimming and Native AOT compatibility.
- Future decoders reserve pixels/metadata before allocation and honor cancellation.
  Corrupt data: ImageFormatException; unsupported features: NotSupportedException.
- Future pixels use Rgba32 with straight alpha. Document precision reduction.
- Port scalar code first from pinned references, record origin and license notices.
- Tests use TUnit / Microsoft.Testing.Platform; public API changes require snapshot review.
- Build/test Release, pack, then run scripts/verify-package-consumer.sh before release.
- Never commit private photos, external corpora, references, build outputs or local logs.
- Commit/push/publication only when requested. Releases remain private; no NuGet publishing.
- VERSION is the single source of package version. Documentation and identifiers use English.

Reference baseline: libjxl v0.12.0, pinned in scripts/reference-lock.json. Treat downloaded sources as read-only. User authorized this source download on 2026-10-26.
