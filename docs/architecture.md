# Codec architecture

PictureSharp.Jxl is one library and one package. JPEG XL is the domain boundary;
its vocabulary follows the format and the pinned libjxl reference. The adapter
uses only PictureSharp's public SPI. All implementation types remain internal.

## Modules and dependencies

| Module | Responsibility | Other codec modules it may use |
| --- | --- | --- |
| Integration | SPI adapters (decoder, encoder, metadata codec), ImageInfo mapping, straight-alpha Rgba32 output, metadata mapping in both directions and metadata rewriting | Container and codec modules |
| Container | Box validation, codestream assembly, optional metadata index, bounded brob decompression, box writing and brob compression | IO |
| Codestream.Model | Immutable header/transform descriptions and small validated values | None |
| Codestream.IO | Borrowed segmented input, bit/field reading, header readers | Model |
| Codestream.Entropy | Prefix/ANS/LZ77 symbol decoding | IO |
| Codestream.Buffers | Owned working planes/channels, rendered result, reference slots | Model |
| Codestream.Modular | Modular streams, prediction, RCT/Palette/Squeeze operations | Model, IO, Entropy, Buffers |
| Codestream.VarDct | Coefficients, transforms, quantization, EPF sigma derivation | Model, IO, Entropy, Modular, Buffers, Rendering, Numerics |
| Codestream.Rendering | Ordered restoration, resampling and patch application | Model, Buffers, Color, Numerics |
| Codestream.Color | Output colour transforms, matrix/TRC ICC output targets, quantization/dither | Model, Buffers, Numerics |
| Codestream.Metadata | ICC entropy stream, prediction decoding and its simplest prediction for writing | IO, Entropy |
| Codestream.Frames | One-image session, frame scheduling, support policy, section readers | Codec modules above |
| Codestream.Numerics | Scalar numerical primitives and integer helpers (`JxlIntMath`) | None; every module may use it |
| Codestream.Encoding | Bit/field/header writers, ANS entropy encoder, MA tree learner, lossless Modular frame encoder, header rewriting | Model, Buffers, IO, Entropy, Modular, Metadata |

`JxlDescriptor` is the shared exception/format boundary and holds the codestream and container signatures. The codec does not use
PictureSharp images, contexts, options or pixels directly. The compiled module
boundary test checks signatures, fields, locals and IL references, including
compiler-generated lambdas. No dependency-injection container is needed.

## Decode flow

1. `JxlDecoder` parses the container and headers and reserves the output pixels.
2. A new `JxlDecodeSession` owns the references and DC frames for that one call.
   It decodes the ICC profile (charged before allocation), derives the optional
   `JxlIccTarget`, skips the preview, then consumes the frame sequence.
3. `JxlDecodeSupportPolicy` rejects unsupported valid features and checks
   cross-frame consistency. Readers continue to validate encoded syntax.
4. `JxlFrameSectionReader` schedules one `JxlFrameDecoder`: LF global, LF groups,
   LF completion, HF global, then each group's passes and reconstruction.
   `JxlFrameProgress` prevents skipped/repeated phases and repeated consumption.
5. Modular transforms operate in two phases. Their immutable descriptions are
   separate from RCT, palette and squeeze execution. Derived squeeze steps belong
   to the operation, never to a shared header. The Modular image owns structural
   channel changes and pending transforms.
6. `JxlFrameRenderer` receives planes, frame/header parameters, patches and EPF
   sigma values, not a VarDCT decoder. It preserves the original order of filters,
   patching, upsampling and colour conversion.
7. The completed planes are transferred to `JxlDecodedImage`. `JxlPixelWriter`
   creates the PictureSharp image and writes pixels. `JxlMetadataReader` attaches the
   ICC profile (only when the output encoding is the profile), the colour description,
   EXIF/XMP and finally the codestream orientation; the decoder disposes the image if
   any step fails. PictureSharp applies orientation on request.
8. `JxlMetadataCodec` (Image.ReadMetadata) parses the container and headers, decodes
   only the ICC stream and maps the same metadata without reading frames.

## Encode flow

1. `JxlEncoder` copies the root frame into integer planes (gray when R = G = B, alpha when any pixel is not
   opaque) and maps the metadata with `JxlMetadataWriter`: the ICC profile or the colour description to a
   `JxlColorEncoding`, the EXIF orientation to the codestream orientation.
2. `JxlCodestreamWriter` writes the signature, SizeHeader, ImageMetadata, default transform data and the ICC
   stream, then `JxlModularEncoder` writes the frame: palette or RCT, the stream layout (global stream and
   256×256 groups), samples for `JxlTreeLearner`, tokens per stream, one `JxlEntropyEncoder` for all streams,
   then frame header, TOC and sections. Encoding reuses the decoder's descriptions (`JxlImageMetadata`,
   `JxlTransform`, `JxlChannel`) and its weighted predictor, and computes properties exactly as `JxlMaTree`.
3. `JxlContainerWriter` adds the Exif and xml boxes (optionally brob) when there are any.
4. `JxlMetadataRewriter` (EditMetadata) rewrites ImageMetadata and the ICC stream by bit positions the header
   reader records, copies the transform data bits and all frame bytes, and replaces only the changed boxes.

The encoder is stateless and single-threaded; each call owns its planes and tokens.

The public decoder is stateless and can be shared. Sessions and frame decoders are
single-use. Mathematical kernels remain static. Explicit interfaces are used for
the three Modular transform implementations, where two shared operations have
multiple real implementations.

## Values and ownership

- The format's vocabulary is typed where the code used to compute it by hand: `JxlFrameHeader` answers group
  origins, TOC section indices and Modular stream ids; `JxlMaProperty`, `JxlNeighbours` and `JxlMaTreeContext` are
  shared by `JxlMaTree` and the encoder, so predictions cannot drift; `JxlRctType`, `JxlVarblock`,
  `JxlAcStrategySet`, `JxlBoxType`/`JxlBoxHeader` and the standard chromaticities on `JxlColorEncoding` replace packed
  integers, strings and repeated constants. Field distributions read and written by both sides
  (`JxlTransformFields`, `JxlHybridUint`) are defined once.

- `JxlSizeHeader` and `JxlBitDepth` are validated readonly record structs. Their
  `Validate` methods reject the otherwise unavoidable `default(struct)` value at
  use boundaries. Context-dependent constraints stay with their consumers.
- Header collection setters snapshot their input and expose read-only wrappers;
  shared defaults cannot be mutated through a cast. Records support `with` updates,
  but collection-bearing records are not used for structural equality of lists.
- `JxlCodestream` snapshots the segment table and borrows the payload bytes. Input
  memory must remain stable for the synchronous call. Slicing does not copy bytes.
- Working channel/plane samples are mutable, zero-initialized, GC-owned arrays.
  They are not value objects and are not pooled. Completed reference frames are
  borrowed without modifying their samples; replacing a slot releases the old
  reference. DC levels and patch slots have separate checked accessors.
- The LZ77 window is pooled. Its owner rents only after fallible setup, returns it
  once, tolerates repeated Dispose, and rejects reads after disposal. `using`
  scopes release it on corruption and cancellation too.
- Cancellation is propagated through the container scan, ICC navigation, Modular
  channel and transform loops, VarDCT rows, filters, patches, colour conversion
  and final pixel writing. It is checked at rows or bounded batches, not per bit.

`ReservePixels` limits the output image; it is not a byte budget for all temporary
planes and references. Allocation shape arithmetic is checked separately. A shared
working-memory budget and broader pooling require a later, measured change.

## Extension points and unchanged capabilities

0.5.0 added `JxlFormat.MetadataCodec`; 0.6.0 added `JxlFormat.Encoder`,
`JxlEncoderOptions` (`CompressMetadata`) and `JxlImageExtensions` (`SaveAsJxl`, `ToJxl`). XYB frames are rendered to a `JxlColorTarget`: the image's
colour space (its ICC profile when `JxlIccTarget` can use it) or, for
`ColorConversion = ToSrgb`, sRGB, chosen once per decode session. The scalar numerical behavior of earlier versions is fixed.

- **ICC/EXIF/XMP:** `JxlContainer.Parse(..., includeMetadata: true)` indexes
  borrowed Exif/xml payloads, including `brob` payloads with the inner type removed;
  Identify and `IgnoreMetadata` decodes do not index them. `JxlMetadataBox.Read`
  materializes a payload, charging every byte through a callback before it exists
  (Brotli output as it grows). The ICC stream is decoded by `JxlIccStream` with the
  same callback and an explicit size limit. Codec modules receive `Action<long>`
  and limits, never the PictureSharp context. Writing and rewriting use separate
  writers (`JxlMetadataWriter`, `JxlContainerWriter`, `JxlMetadataRewriter`).
- **Colour correctness:** the rendered result carries its actual `ColorEncoding`
  and grayscale flag. `WantIcc` in that encoding means the samples are in the
  profile: unconverted output and XYB rendered through a `JxlIccTarget`. The XYB
  fallback for lookup-table profiles reports sRGB and the adapter attaches no
  profile, so converted sRGB samples are never labelled with the original one.
  A full CMS (lookup-table profiles) would extend `JxlIccTarget`, not the adapter.
- **Encoding:** lossy VarDCT (0.7.0) adds a frame encoder beside `JxlModularEncoder`
  and options for quality and effort; the header, entropy and container writers are
  shared. Parallel groups need per-group token streams, which the layout already has.
- **Animation:** extend session composition and map displayed frames, durations
  and looping onto PictureSharp's existing frame model. Reference/DC frames are
  not displayed frames. Until supported, animation still throws explicitly.
- **Performance:** benchmark before pooling planes, adding SIMD or parallelizing
  groups; preserve zero-initialization assumptions and scalar comparison results.

Tests compile Container/Codestream source directly to exercise internals without
InternalsVisibleTo. Public integration and corpus tests run against the library.
Release verification includes build/test, pack, and the actual package consumer
under JIT and trimmed Native AOT; the script invalidates the local plugin package
cache so a same-version rebuild cannot accidentally test a previous binary.
