## Fuzzing and two decoder fixes

0.8.7 starts the fuzzing and resilience series. Six parts of the codec were fuzzed with a custom mutator and with
AFL++, on an x86 PC and on arm64: the container, the headers, entropy decoding, full decoding, and JPEG transcoding in
both directions. The fuzzing found two decoder bugs, both fixed here. No API changes. Encoded bytes are identical to
0.8.6, and so are the decoded pixels of every file in the test corpus.

- **VarDCT DC with Modular transforms.** A lossy image whose DC stream uses Modular transforms (colour transform,
  palette or squeeze) was read from the channels as they were before those transforms. A damaged file could throw
  `IndexOutOfRangeException` instead of `ImageFormatException`, and a valid file could have wrong DC values.
- **Invalid chromaticities.** A colour encoding with primaries or a white point outside the CIE range is still
  rejected with `ImageFormatException`, but no longer wraps an `ArgumentException`. Decoder failures now contain
  only format errors.

Each target ran for 1 hour on the PC and 30 minutes to 2 hours on an Orange Pi 5 (arm64), about 4 billion inputs in
total. There were no hangs, no out-of-memory errors and no output differences between 1 and 4 threads. Every target
must fail only with `ImageFormatException` or `NotSupportedException`. Decoding, `ToJpeg` and `FromJpeg` must give the
same result at 1 and 4 threads, and `FromJpeg` followed by `ToJpeg` must return the original JPEG byte for byte. A set
of 1314 encodes at sizes around the SIMD widths, 256-pixel groups and 2048-pixel LF groups gives identical bytes and
pixels with AVX2, SSE4.1, scalar code and on arm64.

The fuzzing tools (`tools/PictureSharp.Jxl.Fuzz`, `tools/PictureSharp.Jxl.SharpFuzz`, `scripts/fuzz.sh`) are for
development only and are not part of the package. The inputs that found the bugs are regression tests.

Details: [design and results](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.7/docs/design/0.8.7-fuzzing.md).

Validation: 536 tests, 1487 golden results at 1 and 16 threads: the 1469 from 0.8.6 identical, plus 18 for the two new
regression inputs.

NuGet package: [PictureSharp.Jxl 0.8.7](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.7).
`dotnet add package PictureSharp.Jxl`.
