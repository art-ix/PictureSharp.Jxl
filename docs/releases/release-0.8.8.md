## Differential testing against libjxl and three compatibility fixes

0.8.8 compares this library with libjxl 0.12.0 (`cjxl`, `djxl`) on the same inputs: decoding, encoding, and JPEG
transcoding in both directions, on the test corpus and on mutated files. It found three differences, all fixed here.
No API changes. Encoded bytes are identical to 0.8.7, and so are the decoded pixels of every file in the test corpus
when the orientation is not applied.

- **Dithering with `ApplyExifOrientation`.** libjxl undoes the orientation while it writes 8-bit output, so its
  dither pattern is mirrored for orientations that flip the image (rows for 3, 4, 6 and 7, columns for 2, 3, 7 and 8).
  This library now does the same. Before, about a quarter of the samples of such images differed from `djxl` by 1.
- **`FromJpeg` and DC values beyond ±2047.** Such a JPEG was transcoded into a file that neither `ToJpeg` nor `djxl`
  could reconstruct. It is now rejected with `ImageFormatException`. `cjxl` accepts it, but its file fails in `djxl`
  or reconstructs to other bytes.
- **`ToJpeg` and extra zero runs.** `cjxl` can record zero runs that cover more than the zeros left in a block.
  `ToJpeg` rejected such files; it now reconstructs them byte for byte, as `djxl` does.

On damaged or unusual input this library stays stricter than libjxl in places, on purpose: damaged container boxes,
out-of-range chromaticities, reconstruction data that describes an invalid JPEG, and some JPEGs `cjxl` transcodes but
this library cannot reconstruct exactly. These cases are now listed in
[decoding](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.8/docs/decoding.md#differences-from-libjxl) and
[transcoding](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.8/docs/transcoding.md#compatibility).

One difference is in PictureSharp itself: `ToSrgb` of images above 8 bits decodes them at 16 bits and rounds the
result to 8 bits without dither, so such images differ from `djxl` by 1 in many samples. The 16-bit result matches.

Four targets ran for 10 minutes each with three seeds on an x86 PC, about 3.5 million inputs in total. After the
fixes the last run found no pixel, byte or quality differences. The checks:

- **Decoding:** lossless images identical to `djxl`, lossy ones within its tolerance, ICC profiles byte for byte.
- **Encoding:** `djxl` reads every file this library writes, and lossless ones return the source. Lossy quality
  (ssimulacra2) and size are within limits of `cjxl` at the same settings.
- **JPEG transcoding:** `FromJpeg` output reconstructs byte for byte in `djxl`, and `cjxl` output in `ToJpeg`.

The differential targets (`tools/PictureSharp.Jxl.FuzzTargets/DiffTargets.cs`, `scripts/fuzz.sh diff`) are for
development only and need the libjxl tools; they are not part of the package. The inputs that found the differences
are regression tests that need no libjxl.

Validation: 542 tests, 1524 golden results at 1 and 16 threads: the 1487 from 0.8.7 identical, plus 37 for the new
regression inputs. The package passes the JIT and Native AOT consumer checks.

Details: [design and results](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.8/docs/design/0.8.8-differential.md).

NuGet package: [PictureSharp.Jxl 0.8.8](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.8).
`dotnet add package PictureSharp.Jxl`.
