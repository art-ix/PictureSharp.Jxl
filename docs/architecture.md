# Codec architecture

One library, one package. Vocabulary follows the JPEG XL format and the pinned libjxl reference. The adapter uses
only PictureSharp's public SPI; all implementation types are internal.

## Modules and dependencies

| Module | Responsibility | May use |
| --- | --- | --- |
| Integration | SPI adapters (decoder, encoder, metadata codec), ImageInfo mapping, straight-alpha Rgba32 output, metadata mapping and rewriting | Container and codec modules |
| Container | Box validation, codestream assembly, metadata index, bounded brob decompression, box writing and brob compression | IO |
| Codestream.Model | Immutable header/transform descriptions, small validated values | None |
| Codestream.IO | Segmented input, bit/field and header readers | Model |
| Codestream.Entropy | Prefix/ANS/LZ77 symbol decoding | IO |
| Codestream.Buffers | Working planes/channels, rendered result, reference slots | Model |
| Codestream.Modular | Modular streams, prediction, RCT/Palette/Squeeze | Model, IO, Entropy, Buffers |
| Codestream.VarDct | Coefficients, transforms, quantization, EPF sigma | Model, IO, Entropy, Modular, Buffers, Rendering, Numerics |
| Codestream.Rendering | Restoration, resampling, patches | Model, Buffers, Color, Numerics |
| Codestream.Color | Output colour transforms, matrix/TRC ICC targets, quantization/dither | Model, Buffers, Numerics |
| Codestream.Metadata | ICC entropy stream and prediction (decode; simplest prediction for writing) | IO, Entropy |
| Codestream.Frames | Decode session, frame scheduling, support policy, section readers | Codec modules above |
| Codestream.Numerics | Scalar numerics, integer helpers (`JxlIntMath`) | None; usable by all |
| Codestream.Encoding | Bit/field/header writers, ANS encoder, MA tree learner, lossless Modular encoder, lossy VarDCT encoder (`Lossy/`), header rewriting | Model, Buffers, IO, Entropy, Modular, Metadata, VarDct, Rendering |

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
7. Planes move to `JxlDecodedImage`; `JxlPixelWriter` writes the PictureSharp image; `JxlMetadataReader` attaches the
   ICC profile (only when the output is in it), colour description, EXIF/XMP and orientation. On failure the image is
   disposed. PictureSharp applies orientation on request.
8. `JxlMetadataCodec` (`Image.ReadMetadata`) parses headers and only the ICC stream, without frames.

## Encode flow

1. `JxlEncoder` copies the root frame into integer planes (gray if R = G = B, alpha if any pixel isn't opaque);
   `JxlMetadataWriter` maps ICC or colour description to `JxlColorEncoding` and EXIF orientation to the codestream.
2. `JxlCodestreamWriter` writes signature, SizeHeader, ImageMetadata, transform data and ICC stream;
   `JxlModularEncoder` writes the frame: palette or RCT, 256×256 group layout, samples for `JxlTreeLearner`, tokens,
   one `JxlEntropyEncoder`, then frame header, TOC and sections. It reuses the decoder's descriptions and weighted
   predictor and computes properties exactly as `JxlMaTree`.
3. `JxlContainerWriter` adds Exif and xml boxes (optionally brob).
4. `JxlMetadataRewriter` (`EditMetadata`) rewrites ImageMetadata and the ICC stream at bit positions recorded by the
   header reader, copies transform data and frames, and replaces only changed boxes.

Lossy frames branch at step 2: `JxlXybInput` converts to XYB, and `JxlVarDctEncoder` runs libjxl's pipeline (patches,
adaptive quantization, inverse Gaborish, chroma-from-luma, transform search, quantization, nonlinear DC, coefficient
orders, tokens), writing AC metadata, DC and alpha via `JxlModularStreamSet`. Patches come first as a reference-only
Modular frame (`JxlPatchEncoder`). The EPF search reconstructs with the decoder's `JxlVarDctFrame` and
`JxlFrameRenderer`, so both sides share dequantization, transforms and filters.

Encoder and public decoder are stateless (the decoder can be shared); sessions and frame decoders are single-use.
Everything is single-threaded. Kernels are static; interfaces exist only for the three Modular transforms.

## Values and ownership

- Format concepts are typed, not hand-computed (`JxlFrameHeader`, `JxlRctType`, `JxlVarblock`, `JxlBoxHeader`, …).
  Decoder and encoder share MA-tree properties (`JxlMaProperty`, `JxlNeighbours`) and field codings
  (`JxlTransformFields`, `JxlHybridUint`), so they can't drift.
- Header values are validated record structs (`Validate` rejects `default`); header collections are read-only snapshots.
- `JxlCodestream` borrows input bytes (must stay stable during the call); slicing doesn't copy.
- Working samples are zero-initialized, unpooled arrays; only the LZ77 window is pooled (released via `using`).
- Cancellation is checked per row or bounded batch, not per bit.
- `ReservePixels` limits the output image, not temporary memory; a shared budget needs a later, measured change.

## Extension points

0.5.0 added `JxlFormat.MetadataCodec`; 0.6.0 added `JxlFormat.Encoder`, `JxlEncoderOptions` and `JxlImageExtensions`
(`SaveAsJxl`, `ToJxl`). XYB frames render to a `JxlColorTarget` chosen once per session: the image's colour space (or
its ICC profile via `JxlIccTarget`) or sRGB for `ToSrgb`.

- **Metadata:** `JxlContainer.Parse(..., includeMetadata: true)` indexes Exif/xml payloads; readers charge every
  byte through an `Action<long>` callback before it exists, never touching the PictureSharp context.
- **Colour:** a full CMS (lookup-table profiles) would extend `JxlIccTarget`, not the adapter.
- **Encoding:** JPEG recompression (0.7.1) adds a transcoder reusing the VarDCT writer, plus a `jbrd` box.
- **Animation:** extend session composition and map frames, durations and looping; until then it throws.
- **Performance:** benchmark before pooling, SIMD or parallel groups; keep scalar results unchanged.

## Tests and release

Tests compile Container/Codestream sources directly (no InternalsVisibleTo); integration and corpus tests use the
library. Release verification builds, tests, packs and runs a package consumer under JIT and trimmed Native AOT,
clearing the local package cache so a same-version rebuild isn't masked by an old binary.
