# Speed measurements — 0.7.3–0.8.0

Measured on 2026-10-27, Linux x64, Intel Core i5-12500H, .NET 10, one thread (`DOTNET_PROCESSOR_COUNT=1`).
libjxl 0.12.0 (`4128790`) times are its own `--num_reps=3` geomean with `--num_threads=1`, PNG I/O excluded, AVX2 enabled.
Managed times are the warm median of 5 calls after ~2 s of warm-up (tiered JIT), PNG decoding excluded for encoding.
These are measurements, not guarantees; each case was measured once, so differences under ~5 % are within noise.

Inputs come from `scripts/make-speed-inputs.py`: `photo12` is a 4032×3024 photo, `photo1` a 1280×800 crop,
`screen` a synthetic 1600×1000 UI image; decode inputs were encoded by cjxl (`dN-eM` = distance N, effort M).
`IMG_0160-jpeg` is a recompressed JPEG. 0.7.3 output is byte-identical to 0.7.2 (`scripts/golden-hash.sh`, 1142 results).

| Case | Before (ms) | After (ms) | Speed-up | libjxl 1 thread (ms) | After / libjxl | Allocated before → after (MiB) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| decode/photo1-d0-e3 | 346 | 271 | 1.28× | 76 | 3.6× | 44 → 44 |
| decode/photo1-d0-e7 | 373 | 299 | 1.25× | 139 | 2.2× | 56 → 56 |
| decode/photo1-d1-e7 | 107 | 81 | 1.32× | 16 | 5.1× | 43 → 43 |
| decode/photo1-d3-e3 | 128 | 83 | 1.55× | 16 | 5.1× | 41 → 41 |
| decode/photo12-d0-e1 | 1015 | 996 | 1.02× | 342 | 2.9× | 467 → 467 |
| decode/photo12-d0-e2 | 1182 | 1170 | 1.01× | 421 | 2.8× | 505 → 505 |
| decode/photo12-d0-e7 | 3768 | 3216 | 1.17× | 1713 | 1.9× | 596 → 596 |
| decode/photo12-d1-e7 | 1226 | 1059 | 1.16× | 221 | 4.8× | 498 → 498 |
| decode/screen-d0-e7 | 204 | 140 | 1.46× | 59 | 2.4× | 79 → 79 |
| decode/screen-d1-e7 | 186 | 152 | 1.23× | 28 | 5.4× | 67 → 67 |
| decode-srgb/photo12-d1-e7 | 1295 | 1019 | 1.27× | 248 | 4.1× | 498 → 498 |
| encode/photo1-d0-e1 | 1323 | 1275 | 1.04× | 5 | 283.4× | 251 → 252 |
| encode/photo1-d0-e3 | 1341 | 1246 | 1.08× | 94 | 13.3× | 251 → 252 |
| encode/photo1-d0-e7 | 1326 | 1255 | 1.06× | 634 | 2.0× | 251 → 252 |
| encode/photo1-d1-e1 | 126 | 100 | 1.26× | 26 | 3.8× | 70 → 70 |
| encode/photo1-d1-e3 | 126 | 99 | 1.27× | 32 | 3.1× | 70 → 70 |
| encode/photo1-d1-e5 | 1648 | 1092 | 1.51× | 136 | 8.1× | 90 → 90 |
| encode/photo1-d1-e7 | 2906 | 2050 | 1.42× | 219 | 9.4× | 180 → 180 |
| encode/screen-d0-e7 | 689 | 732 | 0.94× | 260 | 2.8× | 205 → 205 |
| encode/screen-d1-e7 | 4416 | 3016 | 1.46× | 388 | 7.8× | 371 → 371 |
| encode/photo12-d0-e7 | 7746 | 6885 | 1.12× | 8485 | 0.8× | 1743 → 1743 |
| encode/photo12-d1-e7 | 36603 | 24932 | 1.47× | 2490 | 10.0× | 1989 → 1989 |
| decode/IMG_0160-jpeg | 747 | 473 | 1.58× | 200 | 2.4× | 449 → 449 |
| tojpeg/IMG_0160-jpeg | 459 | 423 | 1.08× | 176 | 2.4× | 252 → 252 |

## Batched DCT (0.7.3, second step)

1D DCT/IDCT passes now transform all columns of a varblock at once, row by row, instead of copying each column out,
recursing and copying it back; 4- and 8-point leaves stay unrolled. Same operations per column, so output is
unchanged (412 VarDCT golden results identical to 0.7.2). VarDCT cases only, 3 runs (photo1-d1-e7 decode: 7 runs):

| Case | Before (ms) | After (ms) | Speed-up |
| --- | ---: | ---: | ---: |
| decode/photo1-d1-e7 | 81 | 66 | 1.22× |
| decode/photo1-d3-e3 | 83 | 76 | 1.09× |
| decode/photo12-d1-e7 | 1059 | 965 | 1.10× |
| decode/screen-d1-e7 | 152 | 128 | 1.19× |
| decode-srgb/photo12-d1-e7 | 1019 | 954 | 1.07× |
| decode/IMG_0160-jpeg | 473 | 421 | 1.12× |
| encode/photo1-d1-e3 | 99 | 99 | 1.00× |
| encode/photo1-d1-e7 | 2050 | 1430 | 1.43× |

Small decodes allocate ~45 MB per call and trigger gen2 collections, which makes single runs noisy.

Notes: lossless encoding currently ignores `Effort` (every effort runs the e7 search), hence the large gap to cjxl at
e1/e3. `encode/screen-d0-e7` (0.94×) was not re-measured. Peak RSS: up to 1.4 GB for 12 MP lossless decoding and
about 2.8–3.7 GB for 12 MP encoding.

## Entropy and Modular decoding (0.7.4)

Entropy decoding turned out to be a small share (about 10 % of lossless and 1–3 % of lossy decoding), so 0.7.4 also
covers the Modular pixel loop: trees without decisions predict directly, trees deciding on one property use a lookup
table instead of walking the tree, tree nodes are packed into one array, and the weighted predictor's error update is
unrolled. Alias table entries are packed into 8 bytes. Decoded pixels and encoded files are unchanged (515 golden
results identical to 0.7.2). Lossless decoding, 5 runs, compared with the 0.7.3 release measurement:

| Case | 0.7.3 (ms) | Now (ms) | Speed-up | libjxl 1 thread (ms) |
| --- | ---: | ---: | ---: | ---: |
| decode/photo12-d0-e1 | 1002 | 824 | 1.22× | 345 |
| decode/photo12-d0-e2 | 1137 | 820 | 1.39× | 428 |
| decode/photo12-d0-e7 | 3118 | 2968 | 1.05× | 1691 |
| decode/photo1-d0-e3 | 243 | 163 | 1.49× | 74 |
| decode/photo1-d0-e7 | 299 | 286 | 1.05× | 142 |
| decode/screen-d0-e7 | 140 | 107 | 1.31× | 60 |

At effort 7 about a fifth of the time is the tree walk itself (data-dependent branches); lossy decoding is unchanged.
Tried for effort 7 and not kept, compared by minimum time in alternating runs: libjxl's two-level flattened tree (no
faster with branches, about 10 % slower branch-free) and an edge-free neighbour path for interior pixels (no
measurable difference). Computing only the properties a tree uses was not pursued: the local properties are a dozen
integer operations per pixel, while the cost is in branches that depend on the data (tree walk, leaf predictor).

## Modular decoding (0.7.5)

Trees without decisions read the residuals of a whole row first (prefix codes and LZ77 runs in one loop, the window
position kept in a local) and then predict the row. Trees where every leaf predicts the clamped gradient and the
tree splits on the unclamped gradient (libjxl's gradient tree, effort 2) get their own row loop. Both prediction loops
are compiled fully optimised from the start: with tiered PGO the single-leaf gradient pass took about twice as long.
The clamped gradient is branch-free. Bit positions are derived on demand instead of being counted on every read. Decoded
pixels and encoded files are unchanged (all 1142 golden results identical to 0.7.2).
Lossless decoding, 5 runs, two alternating rounds against the 0.7.4 package (mean of the two medians):

| Case | 0.7.4 (ms) | Now (ms) | Speed-up | libjxl 1 thread (ms) |
| --- | ---: | ---: | ---: | ---: |
| decode/photo12-d0-e1 | 833 | 621 | 1.34× | 345 |
| decode/photo12-d0-e2 | 813 | 715 | 1.14× | 428 |
| decode/photo12-d0-e7 | 2978 | 2921 | 1.02× | 1691 |
| decode/photo1-d0-e7 | 282 | 263 | 1.07× | 142 |

photo1-d0-e3 and screen-d0-e7 stay within noise (about ±10 % on this machine). Not kept: full optimisation for the
single-property lookup loop (slower at effort 7), inlining the weighted predictor (e3 about 20 % slower), branch-free
selection of its largest error (no change). Converting decoded integers to float planes is about 8 % of lossless
decoding; most of it is first writes to newly allocated planes, which the memory work of a later milestone addresses.

## VarDCT rendering (0.7.6)

Each EPF stage runs as its own pixel loop (the kernel is a struct type parameter instead of a per-pixel delegate),
and Gaborish and the EPF stages filter in place: each output row is written back once no later row reads its
unfiltered values, so no second set of planes is allocated. Gaborish slides its 3x3 window along the row (three loads
per pixel instead of nine). The degree-4 rational polynomials of the sRGB and PQ output curves are unrolled. Decoded
pixels and encoded files are unchanged (all 1142 golden results identical to 0.7.2).
Lossy decoding, 7 runs pinned to one core, two alternating rounds against the 0.7.5 package (mean of the two medians):

| Case | 0.7.5 (ms) | Now (ms) | Speed-up | libjxl 1 thread (ms) |
| --- | ---: | ---: | ---: | ---: |
| decode/photo12-d1-e7 | 720 | 652 | 1.10× | 221 |
| decode-srgb/photo12-d1-e7 | 844 | 722 | 1.17× | 248 |
| decode/photo1-d1-e7 | 75 | 69 | 1.09× | 16 |
| decode/screen-d1-e7 | 138 | 123 | 1.12× | 28 |

Memory for the 12 MP decode: 528 → 381 MB allocated per decode, peak RSS of the process 1120 → 643 MB. Time per
stage of the 12 MP decode now (Stopwatch around each stage, median per run): dequantisation and IDCT about 240 ms,
EPF 89, AC tokens 71, colour conversion 71, Gaborish 68, Rgba32 writing 56. Not kept: a dedicated 8×8 IDCT without
transposes and an IDCT writing its last butterfly straight into the plane (no measurable change; most of the area of
this image is DCT32 and DCT64, and the IDCT costs about 3 ns per sample).

## Modular encoding (0.7.7)

The scan that samples pixels for tree learning records the weighted predictor's error of every pixel, and
tokenization rebuilds the weighted prediction and its error property from those errors instead of running the
predictor a second time (a channel whose errors would not reproduce every prediction falls back to the predictor).
Tokenization resolves the channel and stream decisions of the tree per channel and walks a whole row through the tree
together, one level at a time without branches, so the steps of different pixels do not wait for each other. The
sampling scan computes the local properties of sampled pixels only. Tree learning quantizes properties with a
counting sort, reuses its buffers between nodes and sums the costs over the node's symbols only (the same float
operations in the same order). ANS encoding divides by an exact 64-bit reciprocal, the bit writer stores up to seven
bytes at once, and the forward RCT no longer goes through a span per pixel. Encoded files are unchanged (all 1142
golden results identical to 0.7.2).
Lossless encoding, 3 runs pinned to one core, two alternating rounds against the 0.7.2 build (mean of the two medians):

| Case | 0.7.2 (ms) | Now (ms) | Speed-up | libjxl 1 thread (ms) |
| --- | ---: | ---: | ---: | ---: |
| encode/photo12-d0-e7 | 8070 | 4428 | 1.82× | 8485 |
| encode/photo1-d0-e7 | 1525 | 867 | 1.76× | 634 |
| encode/photo1-d0-e3 | 1480 | 870 | 1.70× | 94 |
| encode/screen-d0-e7 | 670 | 678 | 0.99× | 260 |

Allocations of the 12 MP encode grow from 1828 to 1947 MB: the recorded errors take four bytes per pixel until
tokenization. Time per stage of the 12 MP encode before (Stopwatch around each stage, median per run): sampling scan
1565 ms, tokenization 3137, tree learning 646 (quantization 250, split search 500), group ANS streams 545, colour
transforms 256. Now: sampling scan about 1450, tokenization 1750, tree learning 415, ANS streams 410, transforms 150.
The plan's target of half the 0.7.2 time (4.15 s) is not quite reached. Not kept: a branch-free walk of one pixel
at a time (slower), walking rows in chunks of 16–64 pixels (no change), writing properties in place or as a compact
row of the learner's six properties (no gain), and one sample pass for both predictors in the split search (slower).

## 16-bit baseline (0.7.8, step 0.7.9a)

Measured on 2026-10-28 with the 0.7.8 sources, same machine and method as above, two full runs of the suite (12 MP
encodes once). New inputs from `scripts/make-speed-inputs.py` (suffix `-16`): `photo12-16` is the photo decoded by
djxl to float sRGB and rounded to 16 bits, `photo1-16` its 1280×800 crop, `screen-16` the screen image widened from
8 bits (v × 257). `decode/*-16-*` decodes the 16-bit files to Rgba32 (default), `decode16/*` with `PreserveBitDepth`
to Rgba64, `encode16/*` encodes Rgba64 sources. libjxl references read and write 16-bit PNGs
(`djxl --bits_per_sample=16`). Golden harness: 1283 results identical to the 0.7.8 package; 8-bit results equal the
0.7.2 baseline except the three intended high-depth ToSrgb changes.

| Case | Run 1 (ms) | Run 2 (ms) | libjxl 1 thread (ms) | Allocated (MiB) | Peak RSS (MiB) |
| --- | ---: | ---: | ---: | ---: | ---: |
| decode/photo1-d0-e7 | 284 | 276 | 142 | 54 | 97 |
| decode/photo1-16-d0-e7 | 378 | 376 | 185 | 61 | 170 |
| decode16/photo1-16-d0-e7 | 345 | 343 | 185 | 65 | 110 |
| decode/photo1-d1-e7 | 57 | 59 | 16 | 32 | 127 |
| decode/photo1-16-d1-e7 | 64 | 150 ¹ | 16 | 32 | 124 |
| decode16/photo1-16-d1-e7 | 269 ¹ | 271 ¹ | 16 | 36 | 128 |
| decode/photo12-d0-e7 | 3042 | 3030 | 1731 | 577 | 1033 |
| decode/photo12-16-d0-e7 | 4016 | 3949 | 2169 | 707 | 1099 |
| decode16/photo12-16-d0-e7 | 3913 | 3945 | 2173 | 753 | 1661 |
| decode/photo12-d1-e7 | 648 | 656 | 216 | 363 | 767 |
| decode/photo12-16-d1-e7 | 721 | 724 | 233 | 363 | 783 |
| decode16/photo12-16-d1-e7 | 729 | 694 | 226 | 410 | 663 |
| decode-srgb/photo12-d1-e7 | 726 | 816 | 247 | 363 | 643 |
| decode16-srgb/photo12-16-d1-e7 | 695 | 689 | 226 | 410 | 574 |
| decode/screen-d0-e7 | 137 | 146 | 58 | 76 | 211 |
| decode16/screen-16-d0-e7 | 106 | 109 | 60 | 83 | 217 |
| decode/screen-d1-e7 | 300 ¹ | 386 ¹ | 28 | 49 | 152 |
| decode/screen-16-d1-e7 | 399 ¹ | 403 ¹ | 28 | 49 | 152 |
| decode16/screen-16-d1-e7 | 108 | 107 | 31 | 55 | 166 |
| encode/photo1-d0-e7 | 817 | 822 | 640 | 228 | 524 |
| encode16/photo1-16-d0-e7 | 1103 | 1137 | 1724 | 258 | 651 |
| encode/photo1-d1-e7 | 1403 | 1419 | 217 | 147 | 503 |
| encode16/photo1-16-d1-e7 | 1439 | 1464 | 224 | 151 | 476 |
| encode/screen-d0-e7 | 650 | 667 | 256 | 177 | 446 |
| encode16/screen-16-d0-e7 | 727 | 745 | 254 | 385 | 898 |
| encode/screen-d1-e7 | 2138 | 2159 | 378 | 318 | 531 |
| encode16/screen-16-d1-e7 | 2078 | 2124 | 373 | 324 | 774 |
| encode/photo12-d0-e7 | 4337 | – | 8444 | 1857 | 3095 |
| encode16/photo12-16-d0-e7 | 4859 | – | 19292 | 2130 | 5586 |
| encode/photo12-d1-e7 | 18612 | – | 2487 | 1589 | 2917 |
| encode16/photo12-16-d1-e7 | 19789 | – | 2696 | 2824 | 5545 |

¹ Harness artefact, not a 16-bit cost: with `DOTNET_PROCESSOR_COUNT=1` the runtime often never promoted any method
to Tier1 by call counting (only OSR), so VarDCT decoding took ~265–400 ms in most processes and ~65/105 ms in the
others (`DOTNET_JitDisasmSummary=1`). The scripts no longer set it (the codec is single-threaded); two runs since:
decode/photo1-16-d1-e7 66/64, decode16/photo1-16-d1-e7 63/62, decode/screen-d1-e7 103/104,
decode/screen-16-d1-e7 103/105, decode16/screen-16-d1-e7 103/103, decode-srgb/photo12-d1-e7 712/698 ms.
Earlier tables in this file were measured with the setting; their VarDCT screen numbers may contain the slow mode.

Findings: 16-bit decoding costs 0–30 % more than 8-bit on the same image (larger residuals in Modular, no dithering
with Rgba64); lossy encoding is unchanged; lossless encoding of the 16-bit photo is 35 % slower (1 MP) and 12 % (12 MP).
Peak RSS of the 12 MP encodes nearly doubles (5.5 GB): full Rgba64 copy plus 16-bit planes. Profiles (top exclusive):

- `decode16` Modular: `JxlMaTree.DecodeChannel` 44 %, `FilteredTree.Lookup` 16 % (8-bit: 45 %, 13 %), weighted
  predictor 15 %, `ReadClustered` 8 %; `ToPlane` 1 %, `WriteWide` 1 % — the int → float → ushort round trip is cheap.
- `decode16` VarDCT: IDCT 30 %, `JxlColorOutput.Apply` (XYB → sRGB polynomial per sample, the same path as 8-bit)
  21 % inclusive; `WriteWide` 3.5 %.
- `encode16` lossless: `FindSplit` 39 % inclusive, of which `ZeroMemoryInternal` 20 % (8-bit 6 %) — `Reuse` clears
  `increase` (bins × symbols) for every property and predictor; 16-bit residuals have more symbols, so clearing
  dominates small nodes. Then `Scan` 26 %, `TokenizeRecorded` 9 %, `ChooseRct` 7.5 %.
- `encode16` lossy: same as 8-bit (`EstimateEntropy` 56 %, `ChromaFromLumaSearch` 22 %, DCT/IDCT 24 %); `ToXyb` 2 %.

## Lossy encoding (0.7.9)

Chroma from luma computes the slope and offset of each coefficient's residual once per search instead of in each of
its up to 20 Newton steps, and flips the sign of its derivative terms with a bit operation instead of a branch on a
close to random sign. The AC strategy entropy estimate no longer recomputes two `Math.Pow` constants per call and
reads its rows through spans. The 8-point DCT and IDCT leaves read their source rows and write scaled results
directly, without the copy and scaling passes. The encoder reads the image's own pixel buffer instead of a full copy
(12 MP: 48 MB Rgba32, 96 MB Rgba64 less). The XYB conversion takes its cube roots from a port of CORE-MATH's correctly rounded cbrtf (MIT,
`Codestream/Numerics/JxlCbrt.cs`, 4.3 instead of 10.6 ns for `MathF.Cbrt`, the same result on every platform).
Output is unchanged (all encode and decode golden results identical to
0.7.8) except for the cbrt: glibc 2.39's cbrtf is not correctly rounded (it differs on 10.6 % of floats by one ulp),
which changes 32 of 521 lossy golden results (sizes −0.0055 % in total, at most 0.18 % for one file; ssimulacra2
within ±0.04 and butteraugli unchanged on 15 test encodes, all decoded by djxl). These results are the new lossy
baseline (`scripts/golden-hash.sh 0.7.9`, file artifacts/golden/baseline-0.7.9.txt). Three runs pinned to one core, tiering off, three alternating rounds against the 0.7.8 package (median):

| Case | 0.7.8 (ms) | Now (ms) | Speed-up |
| --- | ---: | ---: | ---: |
| encode/photo1-d1-e7 | 1651 | 1390 | 1.19× |
| encode16/photo1-16-d1-e7 | 1700 | 1421 | 1.20× |
| encode/screen-d1-e7 | 2413 | 2369 | 1.02× |
| encode/photo1-d1-e3 | 94 | 91 | 1.03× |
| encode/photo1-d1-e3, with cbrt (5 runs) | 96 | 79 | 1.22× |
| encode/photo1-d1-e1, with cbrt (5 runs) | 96 | 79 | 1.22× |
| encode/photo1-d1-e7, with cbrt (5 runs) | 1692 | 1423 | 1.19× |
| encode/photo1-d0-e7 | 811 | 813 | 1.00× |
| decode/photo1-d1-e7 | 66 | 63 | 1.04× |
| decode/screen-d1-e7 | 125 | 118 | 1.06× |

Before the cbrt change 35 % of the e3 encode was the XYB conversion, almost all of it `MathF.Cbrt`. Not kept:
replacing the threshold branch of the chroma-from-luma derivative by a mask (slower), caching entropy estimates per
tile (only 1.2 % of the calls repeat a transform and position), cached 16-bit transfer tables (building one costs
~1 ms per encode). The plan's target (12 MP d1 e7 within 2× of cjxl, now ~6×) is out of reach without changing the
search itself.

## Reproduce

```bash
python3 scripts/make-speed-inputs.py
python3 scripts/measure-speed.py --package 0.7.2          # baseline
python3 scripts/measure-speed.py                          # current sources, about 3.5 minutes
python3 scripts/measure-speed.py --all                    # adds the 12 MP encodes (several more minutes)
python3 scripts/measure-speed.py decode/photo1            # only cases whose id contains a filter
python3 scripts/compare-speed.py artifacts/speed-measurements/baseline-0.7.2.json artifacts/speed-measurements/current.json
scripts/profile-speed.sh decode/photo1-d1-e7              # hotspots of one case
scripts/golden-hash.sh                                    # output identical to 0.7.8 (8-bit and 16-bit)
```

## Allocations and memory (0.7.10)

Measured on 2026-10-28 against the unchanged 0.7.9 build, .NET 10, Linux x64,
three warm operations per isolated process after warmup. Encoding loads PNG inputs
outside the measured operation. Output images/arrays remain included. See the
[raw allocation, GC and RSS data](../measurements/memory-0.7.10-x64.json).

All 20 main photo/screen cases below allocate at least 50% fewer bytes per operation
(50.3–89.1%). This is a warm allocation result, not a bound on cold calls or peak RSS.
ArrayPool retention, rounding to buckets and GC scheduling affect RSS independently;
four cases have a higher process peak RSS, including the 12 MP 16-bit VarDCT decode.

| Case | Allocated before → after (MiB/op) | Reduction | Gen0/op before → after | Gen2/op before → after | Peak RSS before → after (MiB) |
| --- | ---: | ---: | ---: | ---: | ---: |
| `decode/photo1-d0-e7` | 54.0 → 10.6 | 80.3% | 3.00 → 1.00 | 2.00 → 0.33 | 147 → 90 |
| `decode/photo1-d1-e7` | 32.0 → 5.6 | 82.6% | 1.00 → 0.33 | 0.67 → 0.00 | 145 → 139 |
| `decode/photo12-d0-e7` | 576.9 → 129.9 | 77.5% | 11.33 → 9.00 | 2.00 → 0.00 | 839 → 460 |
| `decode/photo12-d1-e7` | 363.2 → 56.0 | 84.6% | 4.67 → 0.33 | 1.67 → 0.00 | 651 → 574 |
| `decode16/photo1-16-d0-e7` | 64.7 → 28.8 | 55.5% | 4.00 → 2.33 | 2.00 → 0.67 | 109 → 124 |
| `decode16/photo1-16-d1-e7` | 35.9 → 9.5 | 73.5% | 1.33 → 0.33 | 1.00 → 0.00 | 139 → 177 |
| `decode16/photo12-16-d0-e7` | 753.2 → 324.7 | 56.9% | 22.67 → 21.67 | 0.67 → 0.00 | 1622 → 864 |
| `decode16/photo12-16-d1-e7` | 409.7 → 102.5 | 75.0% | 4.67 → 1.00 | 1.67 → 0.67 | 664 → 688 |
| `encode/photo1-d0-e7` | 224.4 → 43.0 | 80.8% | 4.33 → 1.00 | 2.00 → 0.00 | 487 → 391 |
| `encode/photo1-d1-e7` | 143.5 → 29.7 | 79.3% | 3.67 → 1.67 | 1.33 → 0.33 | 473 → 173 |
| `encode/screen-d0-e7` | 170.7 → 42.9 | 74.9% | 3.67 → 1.67 | 1.33 → 0.33 | 413 → 324 |
| `encode/screen-d1-e7` | 311.8 → 139.5 | 55.3% | 6.00 → 2.67 | 2.00 → 0.67 | 416 → 478 |
| `encode/photo12-d0-e7` | 1810.4 → 485.1 | 73.2% | 15.33 → 2.67 | 1.33 → 0.00 | 2593 → 2508 |
| `encode/photo12-d1-e7` | 1542.3 → 167.5 | 89.1% | 17.67 → 3.00 | 1.33 → 0.00 | 3157 → 1279 |
| `encode16/photo1-16-d0-e7` | 250.2 → 54.8 | 78.1% | 4.00 → 1.33 | 1.33 → 0.00 | 515 → 410 |
| `encode16/photo1-16-d1-e7` | 143.6 → 29.9 | 79.2% | 3.33 → 1.33 | 0.67 → 0.00 | 409 → 199 |
| `encode16/screen-16-d0-e7` | 372.4 → 66.4 | 82.2% | 4.33 → 1.33 | 1.67 → 0.33 | 805 → 482 |
| `encode16/screen-16-d1-e7` | 312.1 → 139.8 | 55.2% | 5.67 → 3.00 | 1.67 → 1.00 | 549 → 355 |
| `encode16/photo12-16-d0-e7` | 2036.0 → 548.5 | 73.1% | 16.33 → 5.00 | 1.00 → 0.33 | 4941 → 2500 |
| `encode16/photo12-16-d1-e7` | 2730.6 → 1358.4 | 50.3% | 18.00 → 3.67 | 1.67 → 0.67 | 4856 → 3444 |

The implementation pools channels, group coefficients, predictor and scratch rows,
encoder tokens, tree samples and encoded sections. Superseded token/sample/writer
rentals return during growth. Output sections retain their own scope while encoder
working planes are released before destination I/O. Single-group frames still append
bits before padding; multi-group TOCs and jxlc bytes retain their previous layout.
Eligible lossless frames normalize integers directly into pixels with the original
float operations and dither, without intermediate float planes.

The initial per-cell FindSplit clearing caused a controlled 16-bit lossless slowdown
and was replaced: dense predictors clear only the active bin interval with Span.Clear;
sparse predictors reset individual active cells. Cost accumulation and split selection
remain unchanged. An initial pooled tree-sample Add path also needed its growth path
separated and the common append operation inlined. Normal-tiering exploratory times
are therefore omitted from this table.

Validation: 474 tests, complete 1,283-result golden comparison against the released
0.7.9 package, Release pack and actual-package JIT/full-trimming/linux-x64 Native AOT
consumer. The golden harness now uses invariant culture for metadata formatting.

Controlled 1 MP timing check: CPU 0, tiered compilation disabled, five timed
operations per process, alternating before/after then after/before. Values are means
of two process medians. Lossy cases were repeated after an exploratory AOT-build-overlap
outlier; all raw medians used here are in the [controlled timing data](../measurements/memory-0.7.10-controlled-x64.json).

| Case | Before → after (ms) | Time change |
| --- | ---: | ---: |
| `decode/photo1-d0-e7` | 284.0 → 273.3 | -3.79% |
| `decode/photo1-d1-e7` | 66.5 → 62.0 | -6.82% |
| `decode16/photo1-16-d0-e7` | 370.4 → 362.4 | -2.17% |
| `decode16/photo1-16-d1-e7` | 70.8 → 62.9 | -11.15% |
| `encode/photo1-d0-e7` | 830.1 → 803.3 | -3.23% |
| `encode16/photo1-16-d0-e7` | 1098.5 → 1103.9 | +0.50% |
| `encode/photo1-d1-e7` | 1389.7 → 1396.1 | +0.46% |
| `encode16/photo1-16-d1-e7` | 1423.6 → 1411.9 | -0.83% |

The small encoding differences remain within about 1%, except the roughly 3% faster
8-bit lossless case. Decoder reductions are roughly 2–11%. These observations do not
establish a timing guarantee for every image, effort or machine.

Additional single-pair, five-operation screen controls: lossless 8-bit 479.3→463.5 ms,
lossless 16-bit 755.1→750.6 ms, lossy 8-bit 2369.5→2343.6 ms and lossy 16-bit
2382.7→2372.7 ms. These are regression spot checks, not evidence for sub-2% speed claims.

## SIMD (0.8.0)

Measured on 2026-10-29 against the original 0.7.10 package build (commit 6cd7f9b, same speed harness), Intel Core
i5-12500H (AVX2, SSE4.1, FMA), Linux x64, SDK 10.0.401 / runtime 10.0.12, pinned to CPU 0,
`DOTNET_TieredCompilation=0`, two alternating rounds of 3 warm calls per process, nothing else building or tracing.
Median of the process medians; allocations are per warm call, RSS is the process peak. See the
[raw data](../measurements/simd-0.8.0-final.json). Output is unchanged: the 1433-result all-efforts golden set
(decode, ToSrgb, 8/16-bit, metadata edits, JPEG reconstruction, encodes at efforts 1–9) is identical to 0.7.10.

| Case | 0.7.10 (ms) | 0.8.0 (ms) | Speed-up | Allocated (MiB/op) | Peak RSS (MiB) |
| --- | ---: | ---: | ---: | ---: | ---: |
| `decode/tiny-1x1` | 0.014 | 0.010 | 1.47× | 0.0 → 0.0 | 48 → 48 |
| `decode/small-64x64` | 0.67 | 0.63 | 1.06× | 0.1 → 0.1 | 52 → 52 |
| `decode/lossy-e1` | 0.27 | 0.14 | 1.98× | 0.1 → 0.1 | 52 → 52 |
| `decode/photo1-d1-e7` | 62.2 | 20.7 | 3.01× | 5.6 → 5.6 | 134 → 131 |
| `decode/photo1-d3-e3` | 78.2 | 20.7 | 3.78× | 4.9 → 4.9 | 103 → 103 |
| `decode/screen-d1-e7` | 115.7 | 35.0 | 3.31× | 8.2 → 8.2 | 130 → 139 |
| `decode/photo12-d1-e7` | 741.7 | 250.8 | 2.96× | 56.0 → 56.0 | 467 → 550 |
| `decode-srgb/photo12-d1-e7` | 735.0 | 258.8 | 2.84× | 56.0 → 56.0 | 467 → 550 |
| `decode16/photo1-16-d1-e7` | 61.5 | 21.4 | 2.87× | 9.5 → 9.5 | 174 → 170 |
| `decode16/screen-16-d1-e7` | 111.9 | 36.7 | 3.05× | 14.3 → 14.3 | 161 → 162 |
| `decode16/photo12-16-d1-e7` | 705.9 | 256.5 | 2.75× | 102.5 → 102.5 | 587 → 589 |
| `decode/IMG_0160-jpeg` (12 MP JPEG transcode, private) | 428.2 | 225.4 | 1.90× | 232.3 → 232.3 | 884 → 895 |
| `encode/photo1-d1-e1` | 73.8 | 51.7 | 1.43× | 21.9 → 21.9 | 203 → 203 |
| `encode/photo1-d1-e3` | 73.2 | 52.3 | 1.40× | 21.9 → 21.9 | 203 → 203 |
| `encode/photo1-d1-e5` | 711.3 | 295.7 | 2.41× | 27.6 → 27.9 | 171 → 188 |
| `encode/photo1-d1-e7` | 1388.4 | 543.4 | 2.56× | 29.7 → 30.0 | 167 → 188 |
| `encode/screen-d1-e7` | 2377.6 | 954.7 | 2.49× | 139.5 → 139.8 | 551 → 749 |
| `encode16/photo1-16-d1-e7` | 1432.5 | 555.8 | 2.58× | 29.9 → 30.2 | 192 → 189 |
| `encode16/screen-16-d1-e7` | 2390.2 | 932.5 | 2.56× | 139.8 → 140.1 | 462 → 490 |
| `encode/photo12-d1-e7` | 17206.9 | 6591.8 | 2.61× | 368.0 → 368.3 | 1669 → 1450 |

Controls without SIMD paths (Modular decode and encode, JPEG reconstruction) are unchanged:

| Case | 0.7.10 (ms) | 0.8.0 (ms) | Change |
| --- | ---: | ---: | ---: |
| `decode/photo1-d0-e7` | 264.9 | 265.0 | +0.04% |
| `decode/screen-d0-e7` | 99.5 | 98.5 | −1.00% |
| `decode/photo12-d0-e1` | 587.9 | 588.0 | +0.02% |
| `decode16/photo1-16-d0-e7` | 353.0 | 353.1 | +0.03% |
| `tojpeg/IMG_0160-jpeg` | 388.1 | 388.9 | +0.19% |
| `encode/photo1-d0-e7` | 797.7 | 805.0 | +0.92% |
| `encode/photo1-d0-e1` | 798.7 → recheck 809.1 | 817.6 → recheck 810.1 | +2.36% → +0.13% |
| `encode/screen-d0-e7` | 474.8 → recheck 475.2 | 480.7 → recheck 471.2 | +1.25% → −0.84% |
| `encode16/photo1-16-d0-e7` | 1075.8 → recheck 1095.0 | 1095.7 → recheck 1084.1 | +1.85% → −0.99% |

The three lossless encodes above 1% were rechecked with four rounds of five calls
([recheck data](../measurements/simd-0.8.0-lossless-recheck.json)) and are within ±1%.

The kernels (AVX2 + FMA where the scalar code fuses, non-fused AVX2/SSE4.1 otherwise; scalar fallback):
DCT/IDCT columns (2/4/8/16-point leaves, 8×8 fully in registers, AVX/SSE transposes, recursive row steps,
AFV basis, coefficient gather, 2×2 Haar), XYB/YCbCr output colour (sRGB and linear transfer), Gaborish and EPF0/1/2
(EPF1 with sliding SAD windows), dequantization, Rgba32/Rgba64 pixel writing (SSE4.1 path too), and in the encoder XYB
input with an exact vector CORE-MATH cbrt, AC strategy entropy terms (six ordered sums), chroma-from-luma slopes and
derivatives, and AC quantization. Every change was kept only after a paired variant run showed at least 2% on its
target case; raw data per change is under ignored `artifacts/simd-0.8.0/`.

Extra scratch: the AC strategy search keeps 6 floats per transform coefficient (96 KiB at 64×64), the XYB input
24 stack floats per row; the output writer has one fixed 16 KiB dither table. Warm allocations rise by ≤ 0.3 MiB/op.
Peak RSS is not a controlled quantity (GC scheduling and ArrayPool retention): several cases are higher, the
largest `encode/screen-d1-e7` (+198 MiB) and the 12 MP VarDCT decodes (+83 MiB), while the 12 MP encode is lower.
Native AOT: with the default linux-x64 instruction set (x86-64-v2) `Avx2.IsSupported` and `Fma.IsSupported` are
compile-time false, so an AOT application runs only the SSE4.1 kernels (pixel output, quantization) and the scalar
DCT/colour/filter/encoder code; the numbers above are JIT numbers. Publishing with
`-p:IlcInstructionSet=x86-64-v3` (or `native`) enables the AVX2/FMA kernels, and the binary then requires such a CPU.
The package consumer passes under JIT, default AOT (AVX2=False, SSE4.1=True, FMA=False) and x86-64-v3 AOT (all True).
Not measured: AOT speed, SSE4.1-only and scalar-only speed (the masked runs are correctness evidence only), CPUs without
AVX2, ARM64. Rejected in the last pass: merging EPF copies of consecutive unfiltered blocks (no gain ≥ 2%).
