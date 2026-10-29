## Lossless effort tiers and faster encoding

0.8.3 is the first release under the new encoder contract: encoded bytes may change when file size and
ssimulacra2/butteraugli do not get worse. Decoding is unchanged. No API changes; the `Effort` documentation
describes the new lossless tiers.

- **Lossless efforts 1–6 now follow libjxl's Modular tiers.** Before 0.8.3 every effort produced the effort-7 file.
  Effort 1 uses one gradient-predictor context per channel, 2 and 3 libjxl's fixed gradient and weighted-predictor
  trees, 4–6 learn smaller trees from fewer samples; efforts 1–4 code RGB as YCoCg. Efforts 7–9 are unchanged byte for
  byte for 8-bit images. Sizes are within a few percent of `cjxl` at the same effort; effort 1 is smaller and slower
  than `cjxl -e 1`, whose separate fast-lossless encoder is not ported.
- **Smaller 16-bit lossless files.** The palette now accepts 16-bit samples above 32767, and channels that use few of
  their values are stored as a per-channel palette (libjxl's channel compaction, with stricter conditions). A
  screenshot widened to 16 bits shrinks from 339 KB to 111 KB (`cjxl -e 7`: 116 KB); an 8-bit photo saved as 16 bits
  from 2.38 MB to 786 KB (`cjxl`: 792 KB). Full-range 16-bit photos are unchanged. This changes effort-7 output for
  input of more than 8 bits only.
- **Faster lossy encoding.** The AC strategy and chroma-from-luma searches add their per-coefficient terms in eight
  lanes, the order libjxl's vector code uses, instead of strictly sequentially. On the test set (247 encodes) 6 files
  differ, each by 1 to 946 bytes with the same size, and all decode to the same pixels as before.
- **Faster lossless encoding without output changes:** ANS stream writing, token histograms and palette lookups.

Warm medians, v0.8.2 vs 0.8.3, Intel Core i5-12500H (16 logical CPUs), JIT, Linux:

| Case | 0.8.2, 1 thread | 0.8.3, 1 thread | 0.8.2, 16 threads | 0.8.3, 16 threads |
| --- | ---: | ---: | ---: | ---: |
| Lossy encode, 1 MP photo, effort 7 | 449 ms | 409 ms | 123 ms | 117 ms |
| Lossy encode, 12 MP photo, effort 7 | 5.44 s | 4.98 s | 1.32 s | 1.25 s |
| Lossy encode, screenshot, effort 7 | 806 ms | 734 ms | 274 ms | 264 ms |
| Lossless encode, 1 MP photo, effort 1 | 797 ms | 56 ms | 220 ms | 24 ms |
| Lossless encode, 1 MP photo, effort 3 | 810 ms | 267 ms | 234 ms | 60 ms |
| Lossless encode, 1 MP photo, effort 5 | 806 ms | 559 ms | — | — |
| Lossless encode, 1 MP photo, effort 7 | 813 ms | 787 ms | 223 ms | 223 ms |
| Lossless encode, screenshot, effort 7 | 488 ms | 391 ms | 169 ms | 139 ms |
| Lossless encode, 16-bit screenshot, effort 7 | 758 ms | 418 ms | 252 ms | 163 ms |
| Lossless encode, 12 MP photo, effort 7 | 4.72 s | 4.61 s | 1.08 s | 0.97 s |

Lossless sizes of the 1 MP photo by effort (bytes): 908 156, 895 470, 853 183, 842 444, 817 412, 793 336, 785 105
(`cjxl`: 1 015 203, 880 332, 854 203, 840 469, 830 459, 814 919, 785 474). 16-bit encoding of the same image takes
1.00–1.07× the 8-bit time at effort 7 (target ≤ 1.1×); lossless effort 1 is 1.19×. See the
[speed measurements](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.3/docs/benchmarks/speed.md#encoder-work-083),
[raw data](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.3/docs/measurements/encoder-0.8.3.json), the
[effort tiers](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.3/docs/encoding.md#lossless-frames) and the
[encoder design](https://github.com/art-ix/PictureSharp.Jxl/blob/v0.8.3/docs/design/0.8.3-encoder.md).

Validation: 521 Release tests (new: lossless round trips at efforts 1–9 for 8/16-bit, gray and alpha; djxl oracle
for efforts 1–6; widened 16-bit sizes; vector vs scalar entropy estimates for every transform); djxl round trips
of 105 tier outputs at full sample depth; 1433 golden results identical at 1 and 16 threads, a new baseline for the
intended changes; native, SSE4.1-only and scalar execution producing identical bytes.

NuGet package: [PictureSharp.Jxl 0.8.3](https://www.nuget.org/packages/PictureSharp.Jxl/0.8.3).
`dotnet add package PictureSharp.Jxl`.
