## Lossless effort tiers and faster encoding

The first release in which encoded bytes may change, provided file size and ssimulacra2/butteraugli do not get
worse. Decoding is unchanged; no API changes.

- **Lossless efforts 1–6 follow libjxl's Modular tiers** (before, every effort coded as 7): effort 1 uses one
  gradient-predictor context per channel, 2 and 3 libjxl's fixed gradient and weighted-predictor trees, 4–6 learn
  smaller trees from fewer samples; efforts 1–4 code RGB as YCoCg. 8-bit efforts 7–9 are unchanged byte for byte.
- **Smaller 16-bit lossless files:** the palette accepts samples above 32767 and sparse channels are compacted, so
  8-bit content saved as 16 bits codes about as small as the 8-bit image. Full-range 16-bit photos are unchanged.
- **Faster lossy encoding:** entropy sums run in eight lanes, as in libjxl. 6 of 247 test files differ, with the same
  size and decoded pixels.
- **Faster lossless writing** with identical output: ANS streams, token histograms and palette lookups.

Speed from 0.8.2 to 0.8.3 (warm medians, Intel Core i5-12500H, 16 logical CPUs, JIT, Linux):

| Encode | Effort | 1 thread: 0.8.2 → 0.8.3 | Speed-up | 16 threads: 0.8.2 → 0.8.3 | Speed-up |
| --- | :---: | ---: | ---: | ---: | ---: |
| Lossy, 1 MP photo | 7 | 449 → 409 ms | **1.10×** | 123 → 117 ms | 1.05× |
| Lossy, 12 MP photo | 7 | 5.44 → 4.98 s | **1.09×** | 1.32 → 1.25 s | 1.05× |
| Lossy, screenshot | 7 | 806 → 734 ms | **1.10×** | 274 → 264 ms | 1.04× |
| Lossless, 1 MP photo | 1 | 797 → 56 ms | **14.2×** | 220 → 24 ms | 9.2× |
| Lossless, 1 MP photo | 3 | 810 → 267 ms | **3.0×** | 234 → 60 ms | 3.9× |
| Lossless, 1 MP photo | 5 | 806 → 559 ms | **1.44×** | — | — |
| Lossless, 1 MP photo | 7 | 816 → 802 ms | 1.02× | 223 → 223 ms | 1.00× |
| Lossless, screenshot | 7 | 488 → 391 ms | **1.25×** | 169 → 139 ms | 1.21× |
| Lossless, 16-bit screenshot | 7 | 758 → 418 ms | **1.81×** | 252 → 163 ms | 1.54× |
| Lossless, 12 MP photo | 7 | 4.71 → 4.67 s | 1.01× | 1.08 → 0.97 s | 1.11× |

Lossless file sizes in 0.8.3 against `cjxl` 0.12.0 at the same effort (KB):

| Image | e1 | e2 | e3 | e4 | e5 | e6 | e7 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 MP photo, 0.8.3 | 908 | 895 | 853 | 842 | 817 | 793 | 785 |
| 1 MP photo, `cjxl` | 1015 | 880 | 854 | 840 | 830 | 815 | 785 |
| Screenshot, 0.8.3 | 207 | 179 | 198 | 140 | 126 | 119 | 109 |
| Screenshot, `cjxl` | 245 | 218 | 238 | 201 | 128 | 119 | 109 |

16-bit lossless at effort 7: a screenshot widened to 16 bits shrinks from 339 to 111 KB (`cjxl` 116 KB) and an 8-bit
photo saved as 16 bits from 2.38 MB to 786 KB (`cjxl` 792 KB). Encoding 16-bit input takes 1.00–1.07× the 8-bit time
of the same image at effort 7. Effort 1 is smaller but slower than `cjxl -e 1`, whose separate fast-lossless encoder
is not ported.

Details: [speed](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.3/docs/benchmarks/speed.md#encoder-work-083),
[effort tiers](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.3/docs/encoding.md#lossless-frames),
[design](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.3/docs/design/0.8.3-encoder.md),
[raw data](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.3/docs/measurements/encoder-0.8.3.json).

Validation: 521 tests, djxl round trips of every lossless tier, 1433 golden results (new baseline, identical at
1 and 16 threads), identical bytes on AVX2, SSE4.1 and scalar.

NuGet package: [PictureSharp.Jxl 0.8.3](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.3).
`dotnet add package PictureSharp.Jxl`.
