## Lossless effort tiers and faster encoding

The first release in which encoded bytes may change, provided file size and ssimulacra2/butteraugli do not get
worse. Decoding is unchanged; no API changes.

- **Lossless efforts 1–6 follow libjxl's Modular tiers** (before, every effort coded as 7). Sizes are within a few
  percent of `cjxl` at the same effort; 8-bit efforts 7–9 are unchanged byte for byte.
- **Smaller 16-bit lossless files:** the palette accepts samples above 32767 and sparse channels are compacted, so
  8-bit content saved as 16 bits codes about as small as the 8-bit image (screenshot 339 → 111 KB; `cjxl` 116 KB).
  Full-range 16-bit photos are unchanged.
- **Faster lossy encoding:** entropy sums run in eight lanes, as in libjxl. 6 of 247 test files differ, with the same
  size and decoded pixels.

Speed-up from 0.8.2 to 0.8.3 (warm medians, Intel Core i5-12500H, 16 logical CPUs, JIT, Linux):

| Encode | Effort | 0.8.2 → 0.8.3, 1 thread | 0.8.2 → 0.8.3, 16 threads |
| --- | :---: | ---: | ---: |
| Lossy, 1 MP photo | 7 | **1.10×** | 1.05× |
| Lossy, 12 MP photo | 7 | **1.09×** | 1.05× |
| Lossy, screenshot | 7 | **1.10×** | 1.04× |
| Lossless, 1 MP photo | 1 | **14.2×** | 9.2× |
| Lossless, 1 MP photo | 3 | **3.0×** | 3.9× |
| Lossless, 1 MP photo | 5 | **1.44×** | — |
| Lossless, 1 MP photo | 7 | 1.02× | 1.00× |
| Lossless, screenshot | 7 | **1.25×** | 1.21× |
| Lossless, 16-bit screenshot | 7 | **1.81×** | 1.54× |
| Lossless, 12 MP photo | 7 | 1.01× | 1.11× |

Details: [speed](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.3/docs/benchmarks/speed.md#encoder-work-083),
[effort tiers](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.3/docs/encoding.md#lossless-frames),
[design](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.3/docs/design/0.8.3-encoder.md),
[raw data](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.3/docs/measurements/encoder-0.8.3.json).

Validation: 521 tests, djxl round trips of every lossless tier, 1433 golden results (new baseline, identical at
1 and 16 threads), identical bytes on AVX2, SSE4.1 and scalar.

NuGet package: [PictureSharp.Jxl 0.8.3](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.3).
`dotnet add package PictureSharp.Jxl`.
