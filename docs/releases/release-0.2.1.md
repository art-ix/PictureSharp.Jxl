JPEG XL header identification for PictureSharp 0.8.5 (.NET 10 / C# 14).

- Requires PictureSharp >= 0.8.5. PictureSharp 0.8.5 rejects format plugins built against older
  cores, so 0.2.0 no longer loads with it; this release is built against the new `ImageInfo`.
- `ImageInfo.Color` from the codestream headers:
  - `Model`: `Rgb` (also when XYB-encoded), `Gray`, `Cmyk` with a Black extra channel, `Other`
    for the XYB colour space;
  - `ChannelCount`: colour channels plus every extra channel;
  - `BitsPerSample` and `SampleFormat`: the widest channel, colour or extra; `Float` when any
    channel is float;
  - `AlphaMode`: `Straight`, or `Premultiplied` for `alpha_associated`;
  - `HasIccProfile`: the colour encoding declares an ICC profile;
  - `Cicp`: the enumerated colour encoding as H.273 code points when it maps exactly (JPEG XL P3
    with D65 is Display P3, CICP 12; with the DCI white point, CICP 11); null with an ICC
    profile, custom primaries or white point, a gamma exponent or the XYB colour space.
- `ImageInfo.Compression`: `Lossy` for XYB-encoded images, otherwise `Unknown`, because the
  codestream headers do not say whether Modular coding is lossless.
- Animations report `FrameCount` null (unknown) instead of 0.

Still not implemented: pixel decoding (`Image.Load` throws `NotSupportedException`), encoding,
metadata and ICC profile reading. See the
[README](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.2.1/README.md).

Packages are attached to this private release only, not published to a package registry.
