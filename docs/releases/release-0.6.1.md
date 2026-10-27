Internal readability release for PictureSharp 0.8.5 (.NET 10 / C# 14). Decoded pixels, encoded bytes, edited files,
metadata, error messages and the public API are unchanged from 0.6.0.

- **One place for shared knowledge:** integer helpers (`JxlIntMath`), the codestream and container signatures, box
  types and box headers, the standard white points and primaries, the Modular transform fields and the hybrid integer
  configuration are each defined once and used by the reader, the writer and the encoder.
- **Domain types instead of numbers:** frame headers answer group origins, TOC sections and Modular stream ids; MA tree
  properties, neighbours and tree contexts are shared by the decoder and the encoder; RCT types, varblocks, AC strategy
  sets, box entries and metadata changes are named value types.
- **Smaller units:** the frame header reader, the ICC stream decoder, the entropy encoder (histograms, clustering, ANS
  tables), the Modular encoder (transform choice, tree, frame), the VarDCT frame, the quantisation tables, the renderer
  and metadata editing are split into named steps.
- **C# 14 idioms:** extension blocks (snapshots, AC strategy and patch blend properties), primary constructors, span
  constant tables, collection expressions and switch expressions where they make the code shorter.
- The module boundary test allows every module to use the dependency-free Numerics module.

Verification: all tests, byte-identical decode/encode/edit results against 0.6.0 for the corpus and private samples,
decode and encode within about 2% of 0.6.0's speed, package consumer under JIT and trimmed Native AOT. See the
[architecture guide](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.6.1/docs/architecture.md).

Packages are attached to this private release only, not published to a package registry.
