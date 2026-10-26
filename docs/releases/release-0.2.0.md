JPEG XL container and codestream header parsing for PictureSharp (.NET 10 / C# 14).

- `Image.Identify` for raw codestreams and ISO BMFF containers: dimensions, bit depth, alpha,
  orientation and animation flag, with `DecoderOptions.MaxPixels` applied.
- Container: `JXL ` / `ftyp` order, file format versions 0 and 1, `jxlc`, and `jxlp` sequences
  including out-of-order parts (ftyp version 1, as in libjxl v0.12.0); duplicate, missing and
  misplaced parts, box size overflow and truncation are rejected with `ImageFormatException`.
- Codestream headers: LSB-first bit reader across `jxlp` parts, SizeHeader, ImageMetadata
  (bit depth, extra channels, colour encoding, tone mapping, extensions), transform data and the
  ICC size, ported from libjxl v0.12.0 with notices in
  [THIRD-PARTY-NOTICES.txt](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.2.0/THIRD-PARTY-NOTICES.txt).
- Synthetic test corpus generated with cjxl v0.12.0 and checked against jxlinfo, plus truncation
  and bit-flip tests and an optional jxlinfo differential test.

Still not implemented: pixel decoding (`Image.Load` throws `NotSupportedException`; CanDecode
reports true because Identify needs the registered decoder), encoding, metadata and ICC
profile reading. Frame counts of animations are reported as 0. See the
[README](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.2.0/README.md).

Packages are attached to this private release only, not published to a package registry.
