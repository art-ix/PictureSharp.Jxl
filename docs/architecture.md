# Codec architecture

One library, one package. Vocabulary follows the JPEG XL format and the pinned libjxl reference. The adapter uses
only PictureSharp's public SPI; all implementation types are internal.

## Modules and dependencies

| Module | Responsibility | May use |
| --- | --- | --- |
| Integration | SPI adapters (decoder, encoder, metadata codec), ImageInfo mapping, straight-alpha Rgba32/Rgba64 output, metadata mapping and rewriting | Container and codec modules |
| Container | Box validation, codestream assembly, metadata index, bounded brob decompression, box writing and brob compression | IO |
| Codestream.Model | Immutable header/transform descriptions, small validated values | None |
| Codestream.IO | Segmented input, bit/field and header readers | Model |
| Codestream.Entropy | Prefix/ANS/LZ77 symbol decoding | IO |
| Codestream.Buffers | Working planes/channels, rendered result, reference slots | Model |
| Codestream.Modular | Modular streams, prediction, RCT/Palette/Squeeze | Model, IO, Entropy, Buffers |
| Codestream.VarDct | Coefficients, transforms, quantization, EPF sigma | Model, IO, Entropy, Modular, Buffers, Rendering, Numerics, Jpeg |
| Codestream.Rendering | Restoration, resampling, patches | Model, Buffers, Color, Numerics |
| Codestream.Color | Output colour transforms, matrix/TRC ICC targets, quantization/dither | Model, Buffers, Numerics |
| Codestream.Metadata | ICC entropy stream and prediction (decode; simplest prediction for writing) | IO, Entropy |
| Codestream.Frames | Decode session, frame scheduling, support policy, section readers | Codec modules above, Jpeg |
| Codestream.Numerics | Scalar numerics, integer helpers (`JxlIntMath`) | None; usable by all |
| Codestream.Jpeg | JPEG coefficient/scan model, JPEG entropy parser/writer, bounded jbrd reader and shared field order | IO, Numerics |
| Codestream.Encoding | Bit/field/header writers, ANS encoder, MA tree learner, lossless Modular encoder, lossy VarDCT encoder (`Lossy/`), header rewriting | Model, Buffers, IO, Entropy, Modular, Metadata, VarDct, Rendering, Jpeg |

`JxlDescriptor` is the shared exception/format boundary and holds the signatures. The codec never touches PictureSharp
images, contexts, options or pixels. A compiled boundary test checks signatures, fields, locals and IL references
(including compiler-generated lambdas). No DI container.

## Decode flow

1. `JxlDecoder` parses container and headers and reserves the output pixels.
2. A single-use `JxlDecodeSession` owns references and DC frames: decodes the ICC profile (charged before
   allocation), derives the optional `JxlIccTarget`, skips the preview, consumes the frames.
3. `JxlDecodeSupportPolicy` rejects unsupported valid features and checks cross-frame consistency; readers still
   validate syntax.
4. `JxlFrameSectionReader` drives one `JxlFrameDecoder`: LF global, LF groups, LF completion, HF global, then each
   group's passes. `JxlFrameProgress` blocks skipped or repeated phases.
5. Modular transform descriptions are immutable and separate from their execution.
6. `JxlFrameRenderer` gets planes, parameters, patches and EPF sigma (not a VarDCT decoder) and keeps libjxl's order
   of filters, patching, upsampling and colour conversion.
7. After common transform and shape validation, eligible Modular frames expose integer samples directly; other
   frames expose rendered planes through `JxlDecodedImage`. `JxlPixelWriter` writes the PictureSharp image; `JxlMetadataReader` attaches the
   ICC profile (only when the output is in it), colour description, EXIF/XMP and orientation. On failure the image is
   disposed. PictureSharp applies orientation on request.
8. `JxlMetadataCodec` (`Image.ReadMetadata`) parses headers and only the ICC stream, without frames.

## Encode flow

1. `JxlEncoder` reads the root frame directly and separates its samples into integer planes (gray if R = G = B, alpha if any pixel isn't opaque);
   `JxlMetadataWriter` maps ICC or colour description to `JxlColorEncoding` and EXIF orientation to the codestream.
2. `JxlCodestreamWriter` writes signature, SizeHeader, ImageMetadata, transform data and ICC stream;
   `JxlModularEncoder` writes the frame: palette or RCT, 256×256 group layout, samples for `JxlTreeLearner`, tokens,
   one `JxlEntropyEncoder`, then frame header, TOC and sections. It reuses the decoder's descriptions and weighted
   predictor and computes properties exactly as `JxlMaTree`. `JxlModularEffort` (0.8.3) maps the effort to libjxl's
   tiers: a predefined tree (per channel, `kGradientFixedDC`, `kWPFixedDC`) for efforts 1–3, otherwise the learner's
   properties, sample fraction and threshold, and YCoCg or the RCT search. Gradient-only trees are tokenized without the
   weighted predictor.
3. `JxlContainerWriter` writes Exif and xml boxes (optionally brob), the unchanged jxlc header and prepared
   codestream sections directly to the output stream.
4. `JxlMetadataRewriter` (`EditMetadata`) rewrites ImageMetadata and the ICC stream at bit positions recorded by the
   header reader, copies transform data and frames, and replaces only changed boxes.

Lossy frames branch at step 2: `JxlXybInput` converts to XYB, and `JxlVarDctEncoder` runs libjxl's pipeline (patches,
adaptive quantization, inverse Gaborish, chroma-from-luma, transform search, quantization, nonlinear DC, coefficient
orders, tokens), writing AC metadata, DC and alpha via `JxlModularStreamSet`. Patches come first as a reference-only
Modular frame (`JxlPatchEncoder`). The EPF search reconstructs with the decoder's `JxlVarDctFrame` and
`JxlFrameRenderer`, so both sides share dequantization, transforms and filters.

Encoder and public decoder are stateless (the decoder can be shared); sessions and frame decoders are single-use.
Kernels are static; Modular transforms use their shared execution interface. Since 0.8.1 independent groups, tiles,
streams and row bands run on up to `MaxDegreeOfParallelism` threads (core `DecoderOptions`, `JxlEncoderOptions`,
`JxlTranscoderOptions`; default -1) through `JxlParallel`; see **Parallelism** below.

## Values and ownership

- Format concepts are typed, not hand-computed (`JxlFrameHeader`, `JxlRctType`, `JxlVarblock`, `JxlBoxHeader`, …).
  Decoder and encoder share MA-tree properties (`JxlMaProperty`, `JxlNeighbours`) and field codings
  (`JxlTransformFields`, `JxlHybridUint`), so they can't drift.
- Header values are validated record structs (`Validate` rejects `default`); header collections are read-only snapshots.
- `JxlCodestream` borrows input bytes (must stay stable during the call); slicing doesn't copy.
- Decode-call scopes hold pooled integer channels and VarDCT output planes through pixel writing. Per-group and
  per-kernel scopes release coefficients, prediction/error rows, transform crops and DCT scratch on every exit.
  Initialization follows the previous zero-initialized behavior; algorithms use logical dimensions, not rented capacity.
- Encoding retains pooled sections until they are written directly to the caller stream; the TOC and box lengths
  are known before writing. A single-group frame still concatenates section bits before padding. Token buffers
  grow by replacing/returning rentals and are disposed with their stream/frame owners.
- Eligible Modular frames return integer samples after common transform/shape validation. Pixel writing normalizes
  samples with the previous float operations and dithering, avoiding intermediate float planes (since 0.8.4 through
  one scaled float row per channel and worker, which the vectorized row writers convert).
- Cancellation is checked per row or bounded batch, not per bit.
- `ReservePixels` limits the output image, not temporary memory; a shared budget needs a later, measured change.

## Extension points

0.5.0 added `JxlFormat.MetadataCodec`; 0.6.0 added `JxlFormat.Encoder`, `JxlEncoderOptions` and `JxlImageExtensions`
(`SaveAsJxl`, `ToJxl`). XYB frames render to a `JxlColorTarget` chosen once per session: the image's colour space (or
its ICC profile via `JxlIccTarget`) or sRGB for `ToSrgb`.

- **Metadata:** `JxlContainer.Parse(..., includeMetadata: true)` indexes Exif/xml payloads; readers charge every
  byte through an `Action<long>` callback before it exists, never touching the PictureSharp context.
- **Colour:** a full CMS (lookup-table profiles) would extend `JxlIccTarget`, not the adapter.
- **JPEG transcoding (0.7.1):** `JxlTranscoder` delegates to Integration. The self-contained Jpeg module owns
  coefficients, markers/scans, exact JPEG parsing/writing, and jbrd reading. Encoding implements the writing side
  of the shared reconstruction field visitor and prepares integer JPEG coefficients for the shared VarDCT section
  writer. Frames/VarDct recover coefficients with no IDCT, pixel planes or rendering. Their explicit dependency on
  Jpeg is checked by the module boundary test; Jpeg depends on IO and Numerics, so there is no dependency cycle.
- **Reconstruction metadata:** Container optionally borrows a unique jbrd payload. Ordinary image reads skip it.
  The jbrd bit header precedes its Brotli stream; canonical ICC/EXIF/XMP markers refer to external metadata.
  The per-call integration budget accounts for reconstruction header fields and expanded auxiliary data before
  corresponding payload allocations. Streams are buffered; the limits are not a bound on all temporary memory.
- **Animation:** extend session composition and map frames, durations and looping; until then it throws.
- **Performance:** benchmark before pooling, SIMD or parallel groups; keep scalar results unchanged.
- **Parallelism (0.8.1):** `JxlParallel.For`/`ForWorkers`/`ForBands` is the only primitive: degree 1 is a plain loop
  on the calling thread; otherwise the exception of the lowest failed item is rethrown unchanged after the join.
  Decoding parallelizes LF groups, pass groups with their rendering or JPEG coefficient recovery, and row bands of
  Gaborish/EPF (band edges written back after all bands), upsampling, colour output and pixel writing. Encoding
  parallelizes XYB input, adaptive quantization, inverse Gaborish, 64×64 AC-strategy/CfL tiles (per-worker search
  scratch), group quantization, tokenization and section coding, the EPF sharpness reconstruction, and Modular
  tree-sample scans (each channel starts at its jumped-ahead xorshift state; samples appended in channel order) and
  stream tokenization. Since 0.8.2 MA-tree splitting is parallel as well (below).
  Histograms, entropy codes and section order stay sequential. Budgets are
  charged on the calling thread before regions start; workers own their scratch, and shared scopes synchronize rentals.
  Output is identical at every degree; see the [parallelism design](design/0.8.1-parallelism.md).
- **Scheduling and scratch (0.8.2):** MA-tree properties and then independent subtrees are learned in
  parallel; replay in sequential depth-first order preserves node numbering and encoded bytes. RCT candidates
  are costed independently, and palette discovery/index mapping uses row bands with deterministic final sorting.
  JPEG scans with `Ss = 0` and `Ah = 0` encode restart-interval or MCU-row chunks independently, merging raw bits,
  byte stuffing, padding and markers in order. A 64-bit accumulator replaces per-bit output. Corrupt chunk encoding
  is replayed sequentially so an earlier padding error is not hidden by a later coefficient error. AC search, CfL
  and quantizer scratch use the encoder's DCT64×64 maximum (4096 coefficients); decoder support is unchanged.
  See the [scheduling design](design/0.8.2-scheduling.md) for ownership and determinism details.
- **SIMD (0.8.0):** vector kernels live in sibling `*.Simd.cs` files of the same partial type. The scalar caller
  invokes a dispatch helper per row/block that returns the processed count or declines (unsupported ISA, short
  input, layout); scalar tails stay. Kernels whose scalar code uses `MathF.FusedMultiplyAdd` require AVX2 + FMA;
  non-fused kernels (pixel output, quantization, the encoder's 5×5 convolution, DCT row helpers) also have SSE4.1 paths. Reduction order, rounding and clamping are
  those of 0.7.10; test-only frozen scalar copies under `tests/.../ScalarBaseline` and `scripts/verify-simd.sh`
  (native, SSE4.1-only, scalar) check bit identity. See the [SIMD design](design/0.8.0-simd.md).
  Native AOT compiles `IsSupported` against its instruction-set baseline (x86-64-v2 by default: SSE4.1 kernels
  only); applications opt into AVX2/FMA with `IlcInstructionSet`.
- **Modular decoding (0.8.4):** the weighted predictor is branch-free (sign masks; the JIT emits no conditional
  moves) with its four error rows interleaved and read through `Unsafe.Add` after one range check. Row-batched
  prefix-code symbols go through `JxlBitReader.ReadRow`, which keeps the bit buffer in locals while 8 bytes remain
  in the slice and hands it to an inlined `IJxlBitDecoder`. See the [Modular design](design/0.8.4-modular.md).

## Tests and release

Tests compile Container/Codestream sources directly (no InternalsVisibleTo); integration and corpus tests use the
library. Release verification builds, tests, packs and runs a package consumer under JIT and trimmed Native AOT,
clearing the local package cache so a same-version rebuild isn't masked by an old binary.

The [0.7.10 memory design](design/0.7.10-memory.md) describes buffer lifetimes, section assembly and integer-output eligibility.
