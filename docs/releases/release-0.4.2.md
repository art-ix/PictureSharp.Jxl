Internal architecture release for PictureSharp 0.8.5 (.NET 10 / C# 14). Decoding results and the
public API are unchanged from 0.4.0.

- The codec is split into explicit modules (Integration, Container, Codestream Model, IO, Entropy,
  Buffers, Modular, VarDct, Rendering, Color, Metadata, Frames and Numerics) with dependency rules
  enforced by a compiled module boundary test. See the
  [architecture guide](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.4.2/docs/architecture.md).
- Each image is decoded in its own session with explicit reference frames and frame-stage
  scheduling; header models are read-only snapshots and small values validate their invariants.
- Cancellation now reaches pixel loops and ICC profile decoding.
- Containers are indexed for encoded Exif, XMP and `brob` metadata internally; metadata reading is
  still not exposed (planned for 0.5.0).
- 14 new tests for ownership, session state, concurrency, cancellation, the metadata index and
  module boundaries (333 in total).
- The package consumer check always restores the freshly built package.

Still not implemented: encoding, animation, metadata and ICC profile reading. See the
[README](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.4.2/README.md) and the
[decoding guide](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.4.2/docs/decoding.md).

Packages are attached to this private release only, not published to a package registry.
