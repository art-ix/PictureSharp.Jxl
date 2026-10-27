# Codec architecture

PictureSharp.Jxl is one library and one package. JPEG XL is the domain boundary;
its vocabulary follows the format and the pinned libjxl reference. The adapter
uses only PictureSharp's public SPI. All implementation types remain internal.

## Modules and dependencies

| Module | Responsibility | Other codec modules it may use |
| --- | --- | --- |
| Integration | SPI adapter, ImageInfo mapping, straight-alpha Rgba32 output | Container and codec modules |
| Container | Box validation, codestream assembly, optional encoded metadata index | IO |
| Codestream.Model | Immutable header/transform descriptions and small validated values | None |
| Codestream.IO | Borrowed segmented input, bit/field reading, header readers | Model |
| Codestream.Entropy | Prefix/ANS/LZ77 symbol decoding | IO |
| Codestream.Buffers | Owned working planes/channels, rendered result, reference slots | Model |
| Codestream.Modular | Modular streams, prediction, RCT/Palette/Squeeze operations | Model, IO, Entropy, Buffers |
| Codestream.VarDct | Coefficients, transforms, quantization, EPF sigma derivation | Model, IO, Entropy, Modular, Buffers, Rendering, Numerics |
| Codestream.Rendering | Ordered restoration, resampling and patch application | Model, Buffers, Color, Numerics |
| Codestream.Color | Output colour transforms and quantization/dither | Model, Buffers, Numerics |
| Codestream.Metadata | ICC entropy stream navigation | IO, Entropy |
| Codestream.Frames | One-image session, frame scheduling, support policy, section readers | Codec modules above |
| Codestream.Numerics | Scalar numerical primitives | None |

`JxlDescriptor` is the shared exception/format boundary. The codec does not use
PictureSharp images, contexts, options or pixels directly. The compiled module
boundary test checks signatures, fields, locals and IL references, including
compiler-generated lambdas. No dependency-injection container is needed.

## Decode flow

1. `JxlDecoder` parses the container and headers and reserves the output pixels.
2. A new `JxlDecodeSession` owns the references and DC frames for that one call.
   It navigates the ICC stream and preview, then consumes the frame sequence.
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
   creates the PictureSharp image, writes pixels, records orientation and disposes
   the incomplete output on failure. PictureSharp applies orientation on request.

The public decoder is stateless and can be shared. Sessions and frame decoders are
single-use. Mathematical kernels remain static. Explicit interfaces are used for
the three Modular transform implementations, where two shared operations have
multiple real implementations.

## Values and ownership

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

This refactoring adds no public options and no new decoding/encoding features.
The public API snapshot and the existing scalar numerical behavior remain fixed.

- **ICC/EXIF/XMP:** `JxlContainer.Parse(..., includeMetadata: true)` provides
  borrowed Exif/xml payloads, including `brob` payloads with the inner type removed.
  Indexing is opt-in; ordinary Identify/Decode does not accumulate metadata blocks.
  These are encoded slices, not validated or decompressed metadata. A future
  metadata adapter must charge `ReserveMetadata` before materializing decompressed
  content, honor `IgnoreMetadata`, and implement the existing metadata SPI only
  when it actually reads metadata. ICC inside the codestream is a separate path.
- **Colour correctness:** the rendered result carries its actual `ColorEncoding`
  and grayscale flag. The existing XYB+ICC fallback reports sRGB; unconverted
  output retains its source description. A future adapter must not attach an
  original ICC profile to already converted sRGB samples.
- **Encoding:** add a separate encoder session and writers that consume shared
  descriptions. Introduce `JxlEncoderOptions : EncoderOptions`, the typed encoder
  SPI and convenience extensions together with a functioning encoder.
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
