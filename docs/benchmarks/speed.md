# Speed measurements — 0.7.3–0.8.5

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
python3 scripts/measure-paired.py OLD.dll NEW.dll --case decode/photo1-d1-e7 --out paired.json   # A/B, B/A (0.8.2+)
python3 scripts/measure-paired.py OLD.dll NEW.dll --collect --cool-below 60 --case ... --out m.json  # per-operation peak, cooled (0.8.6)
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
Not measured for the release: AOT speed and SSE4.1-only speed (measured afterwards, see the next section), scalar-only
speed, CPUs without AVX2, ARM64. Rejected in the last pass: merging EPF copies of consecutive unfiltered blocks (no gain ≥ 2%).

## SIMD after 0.8.0 (released in 0.8.1)

Measured on 2026-10-29 with the same machine, runtime and method as above (paired alternating processes, CPU 0,
2 rounds × 5 warm calls, borderline results rechecked with 4 rounds). [Raw data](../measurements/simd-after-0.8.0.json).
Output is still identical to 0.7.10: the 1433-result all-efforts golden set has SHA-256 92d936fd…e06, as before.

### Native AOT and SSE4.1 (0.7.10 vs released 0.8.0)

AOT harnesses were published with `PublishAot` from the original 0.7.10 package and from the 0.8.0 source; the
SSE4.1 rows are JIT runs with `DOTNET_EnableAVX=0` for both builds (AVX2/FMA off, SSE4.1 on, verified per process).

| Case | AOT default (x86-64-v2), 0.7.10 → 0.8.0 (ms) | AOT x86-64-v3, 0.7.10 → 0.8.0 (ms) | JIT SSE4.1 only, 0.7.10 → 0.8.0 (ms) |
| --- | ---: | ---: | ---: |
| `decode/photo1-d1-e7` | 134.3 → 125.2 (−6.8%) | 60.7 → 20.0 (−67.1%) | 143.5 → 136.6 (−4.8%) |
| `decode/screen-d1-e7` | 294.7 → 280.8 (−4.7%) | 111.7 → 35.8 (−68.0%) | 321.4 → 311.7 (−3.0%) |
| `decode16/photo1-16-d1-e7` | 177.8 → 166.7 (−6.2%) | 62.8 → 21.4 (−65.9%) | 187.6 → 185.8 (−0.9%) |
| `encode/photo1-d1-e3` | 84.0 → 77.0 (−8.3%) | 73.7 → 52.1 (−29.3%) | 81.9 → 78.3 (−4.3%) |
| `encode/photo1-d1-e7` | 2103 → 2025 (−3.7%) | 1289 → 514 (−60.2%) | 2268 → 2143 (−5.5%) |
| `decode/photo1-d0-e7` (control) | 270.2 → 273.2 (+1.1%) | 269.7 → 263.3 (−2.4%) | 275.7 → 275.3 (−0.2%) |

Without hardware FMA (the default AOT instruction set, or a CPU without AVX2/FMA) the scalar code's exact
`MathF.FusedMultiplyAdd` is computed in software: 0.7.10 itself is about 2× slower there than under JIT
(photo decode 134 ms vs 62 ms), and the FMA kernels cannot run. Applications that publish with Native AOT should use
`-p:IlcInstructionSet=x86-64-v3` (or `native`) where the target CPUs allow it; that build matches or beats JIT.

### Kernels added after 0.8.0

Each change measured against the build without it (the variant with its dispatch disabled, or the previous snapshot
for scalar restructuring); all kept changes have bit-identical output in the scalar/SSE4.1/AVX2 differential tests.

| Change | Result |
| --- | --- |
| DCT n ≥ 16: the recursive IDCT stores straight into the destination, odd rows copied already summed; the DCT reads the source rows and stores scaled, summed rows (no copy/scale passes; SSE4.1 helpers too) | decode −4.1% photo, −4.9% screen; encode e7 ±0.4% |
| 5×5 symmetric convolution (encoder inverse Gaborish, mask blur), AVX2 and SSE4.1, unfused as the scalar code | encode e7 −3.9% photo, −3.7% screen; e3 −0.2% |
| DCT n = 2/4 (4×4, 4×8, AFV, DC-to-LLF) read the source and store scaled results directly, no row copies | encode e7 −10.0% photo, −9.2% screen; decode screen −6.9%, photo +0.6% |
| EPF sharpness search block errors: eight blocks per vector after an 8×8 transpose, each block's ordered float sum kept | encode e7 −3.9% photo, −2.8% screen (4-round recheck) |
| Encoder Y dequantization with the decoder's vector bias, fused with the CfL subtraction | encode e3 −10.2%, e1 −4.1%, e7 −2.4% |
| Rejected: SIMD zero counts for coefficient orders | e3 −2.0%, recheck −0.1% |

### Current vs released 0.8.0

| Case | 0.8.0 (ms) | current (ms) | Change |
| --- | ---: | ---: | ---: |
| `decode/photo1-d1-e7` | 19.7 | 18.6 | −5.6% |
| `decode/photo1-d3-e3` | 17.1 | 17.0 | −0.5% |
| `decode/screen-d1-e7` (recheck) | 34.7 | 33.5 | −3.6% |
| `decode16/photo1-16-d1-e7` | 20.9 | 20.3 | −2.7% |
| `decode16/screen-16-d1-e7` (recheck) | 35.2 | 34.4 | −2.1% |
| `decode/photo12-d1-e7` | 245.9 | 246.4 | +0.2% |
| `encode/photo1-d1-e1` | 50.9 | 45.3 | −11.0% |
| `encode/photo1-d1-e3` | 51.8 | 46.5 | −10.4% |
| `encode/photo1-d1-e5` | 292.3 | 256.2 | −12.3% |
| `encode/photo1-d1-e7` | 540.8 | 443.6 | −18.0% |
| `encode/screen-d1-e7` | 941.6 | 800.2 | −15.0% |
| `encode16/photo1-16-d1-e7` | 556.2 | 457.1 | −17.8% |
| Controls (Modular decode/encode, JPEG reconstruction, small images), rechecked where above 1% | | | −1.0..+0.8% |
| AOT default: decode photo / screen, encode e3 / e7 | | | −5.0 / −2.9 / −0.6 / −3.5% |
| AOT x86-64-v3: decode photo / screen, encode e3 / e7 | | | −7.3 / −4.6 / −10.7 / −17.4% |
| JIT SSE4.1 only: decode photo / screen, encode e3 / e7 | | | −2.1 / +0.2 / −0.7 / −8.2% |

Warm allocations are unchanged in every case; the new kernels use no heap scratch (BlockErrors: 24 stack floats).
`decode/small-64x64` first showed +1.7% and +3.3% with 5 calls per process; with 200 calls it is −1.1% and −0.3%.
`decode/tiny-1x1` (0.010 ms) varies by ±4%. Open: the lossless decode control `decode/photo1-d0-e7` is reproducibly
+1.4% / +1.5% under default AOT (4-round recheck), +0.9% under x86-64-v3 AOT and +0.8% under JIT. No changed code runs
on that path (Modular decoding and integer output); whole-program code layout is the likely cause, not verified.
Remaining profile leaders:
encode e7 ordered entropy sums (~16%), CfL derivatives (~6%); e3 exact XYB cube root (~17%) and tokenization;
decode entropy reading (~13%) and EPF (~11%), which are sequential or already vectorized.

## Group parallelism (0.8.1)

`MaxDegreeOfParallelism` (default -1) parallelizes independent groups, tiles, streams and row bands; output is
byte-identical at every degree (all-efforts golden, 1433 results, identical at 1 and 16 threads). Measured
2026-10-29 with `scripts/measure-parallel.py` on an Intel Core i5-12500H (4 P-cores with SMT + 8 E-cores, 16
logical CPUs), Linux, .NET 10.0.12, tiered compilation off, no concurrent load: the released 0.8.0 package and the
0.8.1 sources at 1 thread pinned to CPU 0, the sources at -1 pinned to CPUs 0–15; 2 rounds × 5 warm calls in rotated
order (12 MP encodes: 3 calls). The 1-thread column includes the SIMD kernels added after 0.8.0. libjxl 0.12.0 is
timed by cjxl/djxl themselves (`--num_threads=1` on CPU 0, `16` on CPUs 0–15, PNG I/O excluded).
[Raw data](../measurements/parallel-0.8.1.json).

| Case | 0.8.0 (ms) | 1 thread (ms) | 16 threads (ms) | Speed-up vs 1 thread | libjxl 1 / 16 threads (ms) | Peak RSS 1 → 16 threads (MiB) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| decode/photo1-d1-e7 | 20.4 | 19.7 | 10.6 | 1.86× | 16.3 / 5.2 | 132 → 189 |
| decode/photo1-d0-e7 | 265 | 269 | 37.5 | 7.16× | 141 / 18.4 | 91 → 143 |
| decode/screen-d1-e7 | 36.0 | 36.6 | 15.7 | 2.33× | 27.7 / 10.3 | 137 → 255 |
| decode/screen-d0-e7 | 99.7 | 102 | 20.4 | 5.00× | 60.4 / 10.2 | 133 → 244 |
| decode/photo12-d1-e7 | 252 | 247 | 78.2 | 3.16× | 213 / 50.0 | 518 → 515 |
| decode/photo12-d0-e7 | 3286 | 3266 | 418 | 7.82× | 1716 / 199 | 561 → 376 |
| decode16/photo12-16-d1-e7 | 260 | 258 | 81.1 | 3.18× | 225 / 48.5 | 589 → 846 |
| decode/IMG_0160-jpeg | 232 | 230 | 86.7 | 2.65× | 196 / 38.0 | 896 → 1238 |
| tojpeg/IMG_0160-jpeg | 389 | 391 | 250 | 1.57× | 169 / 75.8 | 296 → 251 |
| encode/photo1-d1-e3 | 51.6 | 46.5 | 27.6 | 1.68× | 24.3 / 12.2 | 224 → 490 |
| encode/photo1-d1-e7 | 549 | 450 | 122 | 3.68× | 213 / 53.1 | 218 → 562 |
| encode/photo1-d0-e7 | 814 | 799 | 480 | 1.66× | 640 / 96.2 | 389 → 664 |
| encode/screen-d1-e7 | 971 | 812 | 264 | 3.07× | 376 / 98.8 | 788 → 752 |
| encode/screen-d0-e7 | 474 | 478 | 365 | 1.31× | 272 / 43.9 | 291 → 591 |
| encode/photo12-d1-e7 | 6568 | 5406 | 1184 | 4.57× | 2404 / 561 | 1442 → 1564 |
| encode/photo12-d0-e7 | 4569 | 4569 | 1153 | 3.96× | 8503 / 1104 | 2492 → 1717 |

- Lossless (Modular) decode scales 5–8×; VarDCT decode 1.9–3.2× (LF global, DC smoothing, EPF sigma, mirroring
  and container work stay sequential); lossy encode e7 3.1–4.6×. Lossless encode gains 1.3–1.7× at 1 MP and 4×
  at 12 MP: MA-tree splitting (`FindSplit`), palette/RCT choice and histograms remain sequential by design.
- `photo1`/`screen` are 1280×800 and 1600×1000; the only 12 MP input is the photo. `IMG_0160` is a private 12 MP
  JPEG recompression (pixel decode and `tojpeg` reconstruction).
- 1 thread vs the 8f4d64b sources (SIMD after 0.8.0, single-threaded; paired, 4 rounds, CPU 0): lossless decode
  photo −1.5%, screen +3.0% then +0.26% on a 6-round recheck, lossy decode −1.8%, lossless encode −0.2%: no
  measurable cost of the parallel structure at degree 1.
- Peak RSS grows with the worker count where workers hold group coefficients, search scratch or band buffers, and
  falls in some 12 MP cases (not investigated; 0.8.2 profiles memory with threading). `ReservePixels` budgets are
  unchanged and charged before workers start.

## Scheduling and scratch (0.8.2)

Parallel JPEG scan writing, parallel MA-tree learning, RCT choice and palette discovery, and encoder search scratch
sized for DCT64×64 (see the [scheduling design](../design/0.8.2-scheduling.md)). Output is byte-identical to 0.8.1:
the all-efforts golden set (1433 results, SHA-256 92d936fd…e06) at 1 and 16 threads. Measured 2026-10-29 with
`scripts/measure-paired.py` on the machine and runtime of the 0.8.1 section: v0.8.1 and 0.8.2 sources in separate
processes, A/B then B/A, 2 rounds × 5 warm calls (12 MP: 3), tiered compilation off, 1 thread on CPU 0 and -1 on
CPUs 0–15; values are medians over rounds. Cases above +1% were rechecked with 6 rounds.
[Raw data](../measurements/scheduling-0.8.2.json).

| Case | 1 thread 0.8.1 → 0.8.2 (ms) | 16 threads 0.8.1 → 0.8.2 (ms) | Peak RSS 16 threads (MiB) | Allocated 16 threads (MiB) |
| --- | ---: | ---: | ---: | ---: |
| tojpeg/IMG_0160-jpeg | 398 → 316 (−20.5%) | 256 → 61.4 (−76.0%) | 251 → 442 | 108 → 125 |
| encode/photo1-d0-e7 | 811 → 797 (−1.8%) | 495 → 215 (−56.5%) | 599 → 542 | 43 → 46 |
| encode/screen-d0-e7 | 482 → 486 (+0.8%) | 371 → 168 (−54.8%) | 588 → 648 | 43 → 44 |
| encode16/photo1-16-d0-e7 | 1096 → 1071 (−2.3%) | 760 → 273 (−64.1%) | 657 → 834 | 55 → 63 |
| encode16/screen-16-d0-e7 | 767 → 757 (−1.4%) | 457 → 244 (−46.6%) | 902 → 1139 | 67 → 70 |
| encode/photo12-d0-e7 | 4678 → 4691 (+0.3%) | 1203 → 927 (−22.9%) | 1934 → 2122 | 518 → 534 |
| encode16/photo12-16-d0-e7 | 5291 → 5220 (−1.4%) | 1661 → 1087 (−34.5%) | 2165 → 2442 | 582 → 592 |
| encode/photo1-d1-e1 | 46.5 → 47.7, recheck −0.3% | 27.8 → 25.7 (−7.7%) | 491 → 172 | 58 → 20 |
| encode/photo1-d1-e3 | 46.9 → 47.2 (+0.6%) | 28.0 → 25.8 (−7.6%) | 436 → 180 | 58 → 20 |
| encode/photo1-d1-e5 | 258 → 259 (+0.2%) | 77.3 → 74.2 (−3.9%) | 473 → 245 | 132 → 35 |
| encode/photo1-d1-e7 | 451 → 447 (−0.7%) | 126 → 123 (−2.9%) | 529 → 251 | 168 → 45 |
| encode/screen-d1-e7 | 815 → 807 (−0.9%) | 272 → 266 (−2.0%) | 608 → 781 | 281 → 156 |
| encode16/photo1-16-d1-e7 | 461 → 462 (+0.3%) | +2.1%, recheck −2.4% | 483 → 262 | 168 → 46 |
| encode16/screen-16-d1-e7 | 803 → 796 (−0.9%) | +1.2%, recheck −4.9% | 721 → 760 | 280 → 156 |
| encode/photo12-d1-e7 | 5434 → 5433 (0.0%) | +2.0%, recheck +0.5% | 1521 → 1428 | 321 → 199 |
| encode16/photo12-16-d1-e7 | 6387 → 6403 (+0.2%) | +3.5%, recheck −0.1% | 3287 → 3730 | 1511 → 1389 |

- Decoding is not changed by 0.8.2 (no decoder source differs). All 22 decode configurations are within −4.4..+4.4%
  in the first pass; every one above +1% was within +0.7% (decode/screen-d1-e7, 1 thread, 10 rounds × 15 calls) or
  faster on recheck: screen-d1-e7 16 threads +0.2%, decode16/photo1-16-d0-e7 −2.6%, recompressed JPEG +0.4%,
  tiny-1x1 −0.9%.
- JPEG reconstruction: the 64-bit bit accumulator gives the 1-thread gain; DC-first scans (restart intervals, or MCU
  rows without them) give the parallel one. Each chunk keeps its encoded bytes until the in-order merge, so 16-thread
  peak RSS grows (251 → 442 MiB for 12 MP; the 1-thread figure stays below 0.8.1's).
- Lossless encoding at 16 threads is 2.2–2.8× (1 MP) and 1.3–1.5× (12 MP) faster than 0.8.1: tree splitting no
  longer runs on one thread.
  At 1 thread it is within −2.3..+0.8%.
- Lossy encoding: the AC search, CfL and quantizer scratch use 4096 instead of 65536 coefficients. Per-worker Large
  Object Heap scratch was the main allocation: 16-thread allocations drop 168 → 45 MiB (photo1 e7) and RSS
  529 → 251 MiB, with e1/e3 7.6% faster. Screen content allocates less but peaks higher (fewer, later Gen2
  collections); peak RSS is not a controlled quantity.

Rejected after measurement (not included):

| Candidate | Result |
| --- | --- |
| JPEG output streamed through a 4 KiB buffer instead of per-chunk byte buffers | tojpeg +0.2% (1 thread), +3.0% (16 threads); RSS and allocations not better than the kept writer |
| 2–4 decode row bands per worker | 12 MP decode 4–6% slower |
| Cache of histogram-pair merge costs in cluster merging | no gain |
| Parallel or skipped zero counting for coefficient orders | e3 no gain, 1 thread +1.4% |
| Core image buffer without initial fill | changes `HasAlpha`; needs a core API |

Parallel palette discovery was also measured in isolation on a photo that exceeds the palette limit: −0.2% (1 thread)
and +0.3% (16 threads). It is kept: its gain applies to palette content, and the output is identical.
A 1 MP VarDCT frame has one LF group whose DC entropy stream is sequential; scheduling cannot split it.

## Encoder work (0.8.3)

Lossless effort tiers like libjxl, 16-bit palette and channel compaction, lane-ordered lossy entropy sums and faster
lossless stream writing (see the [encoder design](../design/0.8.3-encoder.md)). Decoding is unchanged. Encoded bytes
change for lossless efforts 1–6, for lossless input of more than 8 bits, and for a few lossy files (6 of 247 test
encodes: same size, identical decoded pixels); 8-bit lossless efforts 7–9 are byte-identical to 0.8.2. New golden
baseline: 1433 results, identical at 1 and 16 threads (SHA-256 88eccd4b…e2cc).

Measured 2026-10-29 with `scripts/measure-paired.py` (new `--allow-size-change` for intended output changes) on the
machine and runtime of the earlier sections: v0.8.2 and 0.8.3 sources in separate processes, A/B then B/A, 2 rounds ×
5 warm calls (12 MP: 3), tiered compilation off, 1 thread on CPU 0 and -1 on CPUs 0–15. Controls above +1% were
rechecked with 6 rounds; memory rows come from a confirmation run of the final source (the full matrix ran before
two allocation fixes, below). libjxl 0.12.0: `cjxl --num_threads=1 --num_reps=5` on CPU 0, its own timing (lossless)
or the 0.8.1 section (lossy). [Raw data](../measurements/encoder-0.8.3.json).

Lossy encoding (sizes unchanged):

| Case | 1 thread 0.8.2 → 0.8.3 (ms) | 16 threads 0.8.2 → 0.8.3 (ms) | libjxl 1 / 16 threads (ms) |
| --- | ---: | ---: | ---: |
| encode/photo1-d1-e1 | 47.2 → 45.2 (−4.2%) | | 26 (0.7.3) |
| encode/photo1-d1-e3 | 48.4 → 46.9 (−3.1%) | 26.7 → 26.2 (−1.7%) | 24.3 / 12.2 |
| encode/photo1-d1-e5 | 261 → 238 (−8.8%) | | 136 (0.7.3) |
| encode/photo1-d1-e7 | 449 → 409 (−8.9%) | 122.7 → 117.4 (−4.3%) | 213 / 53.1 |
| encode/screen-d1-e7 | 806 → 734 (−9.0%) | 274 → 264 (−3.6%) | 376 / 98.8 |
| encode/photo12-d1-e7 | 5438 → 4984 (−8.3%) | 1316 → 1249 (−5.1%) | 2404 / 561 |
| encode16/photo1-16-d1-e7 | 462 → 423 (−8.4%) | | |
| encode16/screen-16-d1-e7 | 807 → 734 (−9.0%) | | |
| encode16/photo1w-16-d1-e7 | 458 → 418 (−8.7%) | | |

Lossless encoding by effort, 1 thread (v0.8.2 coded every effort as 7):

| Case | 0.8.2 (ms) | 0.8.3 (ms) | 0.8.3 bytes | libjxl (ms) | libjxl bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| encode/photo1-d0-e1 | 797 | 56.0 | 908 156 | 3.8 | 1 015 203 |
| encode/photo1-d0-e2 | 813 | 78.9 | 895 470 | 47 | 880 332 |
| encode/photo1-d0-e3 | 810 | 267 | 853 183 | 96 | 854 203 |
| encode/photo1-d0-e4 | 800 | 377 | 842 444 | 241 | 840 469 |
| encode/photo1-d0-e5 | 806 | 559 | 817 412 | 316 | 830 459 |
| encode/photo1-d0-e6 | 829 | 683 | 793 336 | 408 | 814 919 |
| encode/photo1-d0-e7 | 813 | 787 (−1.8% on confirmation) | 785 105 | 653 | 785 474 |
| encode/screen-d0-e1 | 494 | 37.5 | 207 130 | 2.5 | 245 462 |
| encode/screen-d0-e2 | 495 | 48.6 | 178 723 | 66 | 218 166 |
| encode/screen-d0-e3 | 498 | 107 | 198 170 | 135 | 237 860 |
| encode/screen-d0-e4 | 495 | 153 | 140 252 | 310 | 200 927 |
| encode/screen-d0-e5 | 494 | 193 | 126 351 | 203 | 127 520 |
| encode/screen-d0-e7 | 488 | 391 | 108 866 | 260 | 109 392 |
| encode/photo12-d0-e7 | 4718 | 4611 (−0.9% on confirmation) | 11 203 882 | 8503 (0.8.1 section) | |

At 16 threads: photo1 e1 220 → 23.8 ms, e3 234 → 59.8 ms, e7 −0.1% (6-round recheck) and −4.8% (confirmation);
screen e1 173 → 18.1 ms, e7 169 → 139 ms (palette table); photo12 e7 1081 → 971 ms. libjxl's effort 1 is its separate
fast-lossless encoder; here effort 1 keeps the ANS bitstream, smaller files and about 15–20× libjxl's time.

16-bit input (decision 6: ≤ 1.1× the 8-bit time of the same image). `photo1w-16` is `photo1` widened × 257,
`screen-16` the widened screen; the palette and channel compaction now apply to them:

| Case | 0.8.2 (ms) | 0.8.3 (ms) | 16-bit / 8-bit time | Bytes 0.8.2 → 0.8.3 | libjxl (ms, bytes) |
| --- | ---: | ---: | ---: | ---: | ---: |
| encode16/screen-16-d0-e1 | 762 | 37.2 | 0.99× | 339 246 → 211 968 | 3.9, 551 407 |
| encode16/screen-16-d0-e3 | 761 | 108 | 1.01× | 339 246 → 195 350 | 145, 719 643 |
| encode16/screen-16-d0-e7 | 758 | 418 (16 threads: 252 → 163) | 1.07× | 339 246 → 111 411 | 257, 116 006 |
| encode16/photo1w-16-d0-e1 | 864 | 66.7 | 1.19× | 2 375 720 → 910 788 | 8.1 |
| encode16/photo1w-16-d0-e3 | 871 | 247 | 0.92× | 2 375 720 → 860 930 | 114 |
| encode16/photo1w-16-d0-e7 | 862 | 790 (16 threads: −0.7%) | 1.00× | 2 375 720 → 785 764 | 672, 792 199 |
| encode16/photo1w-16-d1-e7 | 458 | 418 | 1.02× | unchanged | |
| encode16/photo1-16-d0-e7 (full range) | 1085 | 1033 | — | unchanged | |
| encode16/photo12-16-d0-e7 (full range) | 5324 | 5001 | — | unchanged | |

Lossless effort 1 misses the target on the photo (1.19×): reading and compacting 16-bit samples is a larger share
of a 60 ms encode. Lossless efforts 3–7 and lossy encoding meet it.

Controls: decode/photo1-d1-e7 +0.9% (1 thread), +0.0% (16 threads); decode/photo1-d0-e7 +1.5%, recheck −0.9%;
tojpeg +2.7%, recheck +0.1% (no decoder or transcoder file differs).

Memory: the first matrix showed two allocation regressions, both fixed before the confirmation run. `WritePacked`
reserved 7 bytes per symbol, so the 192 retained group sections of a 12 MP lossless encode grew to several times
their content (allocated 934 → 1219 MiB, peak RSS 2439 → 3028 MiB); it now reserves the exact length (934 MiB,
2438 MiB). A flat contexts × 128 count table in `TokenHistograms` added about 7 MiB to every lossy encode; rows are
now allocated for the contexts that occur (photo1 e1 17.7 → 18.3 MiB). Lossless effort 1–3 encodes allocate about half
of v0.8.2's effort-7 amount. At 16 threads the 1 MP lossless effort-7 peak RSS is higher (530 → 737 MiB) with equal
allocations, while the 12 MP one is lower (2172 → 1838 MiB); peak RSS at 16 threads follows GC timing and is not a
controlled quantity.

Rejected: both predictors' tree-learner histograms in one pass (lossless photo e7 −0.3%, 16-bit +0.0%).

## Modular decoding (0.8.4)

Lossless decoding work (see the [Modular design](../design/0.8.4-modular.md)): a branch-free weighted predictor,
prefix-code rows with the bit buffer in locals, and vectorized output of integer samples. Decoded pixels and encoded
bytes are identical to 0.8.3 (golden: 1433 results, SHA-256 88eccd4b…, at 1 and 16 threads). The lossless encoder
runs the same predictor and is faster at efforts 3–7.

Measured 2026-10-30 with `scripts/measure-paired.py` on the machine and runtime of the earlier sections: 0.8.3 and
0.8.4 sources in separate processes, A/B then B/A, 2 rounds × 5 warm calls (12 MP: 3), tiered compilation off,
1 thread on CPU 0 and -1 on CPUs 0–15. libjxl: `djxl --num_threads=0 --num_reps=5` 0.12.0 on CPU 0.
[Raw data](../measurements/modular-0.8.4.json).

Lossless decoding:

| Case | 1 thread 0.8.3 → 0.8.4 (ms) | Speed-up | 16 threads 0.8.3 → 0.8.4 (ms) | libjxl 1 thread (ms) | 0.8.4 / libjxl |
| --- | ---: | ---: | ---: | ---: | ---: |
| decode/photo1-d0-e3 | 185 → 99.4 | 1.86× | 28.8 → 19.4 | 72 | 1.38× |
| decode/photo1-d0-e7 | 277 → 181 | 1.53× | 38.7 → 32.2 | 140 | 1.30× |
| decode/photo1-16-d0-e7 | 369 → 267 | 1.38× | 50.6 → 42.5 | 182 | 1.47× |
| decode16/photo1-16-d0-e7 | 365 → 266 | 1.37× | 50.7 → 39.8 | 182 | 1.46× |
| decode/screen-d0-e7 | 98.8 → 78.1 | 1.27× | 20.2 → 15.1 | 57 | 1.37× |
| decode16/screen-16-d0-e7 | 98.4 → 76.4 | 1.29× | 21.5 → 15.3 | 59 | 1.29× |
| decode/photo12-d0-e1 | 610 → 458 | 1.33× | 107 → 82.6 | 335 | 1.37× |
| decode/photo12-d0-e2 | 621 → 536 | 1.16× | 103 → 95.1 | 411 | 1.30× |
| decode/photo12-d0-e7 | 3351 → 2161 | 1.55× | 433 → 303 | 1685 | 1.28× |
| decode16/photo12-16-d0-e7 | 4361 → 3086 | 1.41× | 535 → 397 | 2161 | 1.43× |

Lossless encoding (same bytes):

| Case | 1 thread 0.8.3 → 0.8.4 (ms) | 16 threads 0.8.3 → 0.8.4 (ms) |
| --- | ---: | ---: |
| encode/photo1-d0-e3 | 265 → 182 (−31.5%) | 50.0 → 39.1 (−21.8%) |
| encode/photo1-d0-e7 | 775 → 697 (−10.0%) | 203 → 197 (−3.1%) |
| encode/screen-d0-e7 | 397 → 372 (−6.4%) | 135 → 133 (−1.3%) |
| encode16/photo1w-16-d0-e7 | 776 → 698 (−10.0%) | 213 → 208 (−2.3%) |
| encode/photo12-d0-e7 | 4496 → 3628 (−19.3%) | 843 → 751 (−10.8%) |

16-bit decoding (decision 6: ≤ 1.1× the 8-bit time of the same image): `photo1-16` and `photo12-16` hold real 16-bit
noise rather than the 8-bit content, and `cjxl` builds trees 5–10 times larger for them; their decodes take 1.47× and
1.43× the 8-bit time (libjxl: 1.30× and 1.28×). The widened `screen-16` meets the target (0.98×).

Controls (VarDCT decoding, JPEG transcoding, lossy encoding, small images): all faster or within noise at 1 thread
(−1% to −6%). At 16 threads `decode/screen-d1-e7` +6.0% rechecked at −2.3% (6 rounds); `encode/photo1-d0-e1` +2.5%
rechecked at +2.1% and −3.8%, with `encode/screen-d0-e1` −0.0% / +5.8% in the same run: 20–25 ms encodes at 16 threads
vary by several percent, and no code on the effort-1 encoding path changed. Allocations are unchanged (the output
writer rents one row per channel and worker); peak RSS follows GC timing with equal allocations (12 MP e1 at 1 thread
585 → 632 MiB, 16-bit 12 MP e7 762 → 864 MiB; most other rows within a few MiB).

Small candidates measured against a build without them (4 rounds, 1 thread): photo1 e7 −3.1%, photo12 e7 −3.0%,
16-bit −0.4% to −1.1%, screen −0.7% to −1.2%; kept for the 8-bit e7 target.

## Consolidation (0.8.5)

Fixed per-image costs and concurrency (see the [consolidation design](../design/0.8.5-consolidation.md)): the RCT search
costs each distinct transformed channel once, library quantization tables are shared by the process, coefficient
orders cover only the buckets a frame uses, the decoder parses the ICC output target on first use, and per-worker
state is created with a worker's first item. Decoded pixels and encoded bytes are identical to 0.8.4 (golden: the 1433
earlier results, SHA-256 88eccd4b…, at 1 and 16 threads; 1469 with the new small inputs).

Measured 2026-10-30 with `scripts/measure-paired.py` on the machine and runtime of the earlier sections: 0.8.4 and
0.8.5 sources in separate processes, A/B then B/A, 2 rounds × 5 warm calls (12 MP: 3; 64×64: 200), tiered
compilation off, 1 thread on CPU 0 and -1 on CPUs 0–15. New inputs `small64`/`small256` are centre crops of the 12 MP
photo. libjxl: `cjxl`/`djxl --num_threads=0 --num_reps=50` 0.12.0 on CPU 0.
[Raw data](../measurements/consolidation-0.8.5.json).

Small images:

| Case | 1 thread 0.8.4 → 0.8.5 (ms) | Speed-up | 16 threads 0.8.4 → 0.8.5 (ms) | Allocated 1 thread (KiB) | libjxl 1 thread (ms) |
| --- | ---: | ---: | ---: | ---: | ---: |
| decode/small64-d0-e7 | 0.507 → 0.340 | 1.49× | 0.574 → 0.358 | 110 → 47 | 0.27 |
| decode/small64-d1-e7 | 0.174 → 0.143 | 1.22× | 0.186 → 0.162 | 134 → 98 | 0.20 |
| decode/small256-d0-e7 | 12.6 → 12.0 | 1.05× | 12.3 → 12.1 | 565 → 502 | 9.1 |
| decode/small256-d1-e7 | 1.41 → 1.34 | 1.05× | 1.78 → 1.37 | 557 → 482 | 0.91 |
| encode/small64-d0-e7 | 5.12 → 2.58 | 1.98× | 2.70 → 1.83 | 1076 → 992 | 2.5 |
| encode/small64-d1-e7 | 3.73 → 2.57 | 1.45× | 3.84 → 2.76 | 7328 → 2269 | 2.7 |
| encode/small256-d0-e7 | 96.6 → 56.1 | 1.72× | 41.0 → 34.1 | 5614 → 5406 | 45 |
| encode/small256-d1-e7 | 27.5 → 26.1 | 1.05× | 15.1 → 13.9 | 9042 → 4009 | 13.7 |

Larger images, 1 thread: lossless encoding `photo1-d0-e7` 717 → 663 ms (−7.4%) and `photo1w-16-d0-e7` 742 → 694 ms
(−6.5%) from the RCT search; `decode/photo1-d1-e7` −4.3%; lossy encodes allocate about 5 MB less per frame. Every
other case is within ±1.4% at 1 thread. At 16 threads most cases are within ±3%; `decode/photo1-d0-e3` −11.9%,
`encode/photo12-d1-e7` −8.5%. The largest single-run increase at 16 threads, `decode16/screen-16-d0-e7` +22.3%
(14.8 → 18.1 ms), was rechecked after the release with 6 paired rounds
(`artifacts/perf-0.8.5/recheck-screen16.json`): 15.9 → 16.2 ms (+1.3%), single rounds 15.5–18.1 ms in both builds,
and `decode/screen-d0-e7` +0.4% — noise. The other single-run increases were not rechecked (user decision):
`encode/photo1-d1-e7` +6.6%, `decode/small-64x64` +6.4%,
`decode16/photo1-16-d0-e7` +4.6%, `decode/screen-d1-e7` +4.5%, `encode/photo1-d0-e3` +4.4%, `tojpeg/IMG_0160-jpeg`
+4.1%; all are unchanged or faster at 1 thread, and the lossless decoding and JPEG reconstruction code they run did not
change. Earlier 16-thread rechecks of such single runs (0.8.4) came back within noise.

Concurrency (`JXL_CONCURRENT=16`: 16 independent operations at once on CPUs 0–15, tiered compilation off, 9 calls):
with the library default (-1) throughput matches one thread per image for decoding (`small256-d1` 0.27 ms/image,
`photo1-d0-e7` 28.6 ms/image both ways). Lossy encoding of 256×256 images at -1 took 5.90 ms and 13.2 MB per image in
0.8.4 sources (5.02 ms / 4.1 MB at one thread per image): every potential worker created its scratch. With the state
created on a worker's first item: 5.08 ms and 5.8 MB.

## Memory use (0.8.6)

Peak memory and allocations of large images (see the [memory design](../design/0.8.6-memory.md)): 4:2:0 chroma is
upsampled in one pass, and subsampled YCbCr frames are upsampled and converted per row band inside the pixel writer;
lossless encoding applies the colour transform to its group crops, sizes token storage once and records the weighted
errors in it; the lossy encoder releases its image after quantization; closures, list growth and per-histogram arrays
left the per-block and per-group paths; JPEG reconstruction pools its coefficients. Decoded pixels and encoded bytes
are identical to 0.8.5 (golden: 1469 results, SHA-256 7f0990ec…, at 1 and 16 threads).

Measured 2026-10-30 with `scripts/measure-paired.py` on the machine and runtime of the earlier sections, 2 rounds,
tiered compilation off, 1 thread on CPU 0 and -1 on CPUs 0–15. [Raw data](../measurements/memory-0.8.6.json).

Peak RSS needs care. In the default harness mode the process peak mostly follows the large-object garbage that has
not been collected yet, chiefly the decoded images' pixel buffers, and so the number of runs: a 12 MP decode that
allocates 55 instead of 245 MB per image still peaked at ~0.9 GB, after fewer gen-2 collections. The harness option
`JXL_COLLECT=1` (`measure-paired.py --collect`) runs a full compacting collection before every operation, outside the
timing, so the peak is that of one operation. That matrix compares the 0.8.5 library built with the 0.8.6 harness
(`artifacts/perf-0.8.6/build.sh base v0.8.5`) with 0.8.6.

Per operation (`--collect`), 12 MP unless noted:

| Case | Threads | Time (ms) | Allocated (MB) | Peak RSS (MiB) |
| --- | ---: | ---: | ---: | ---: |
| decode, JPEG reconstructed 4:2:0 | 1 | 217 → 195 (−10%) | 244 → 53 | **332 → 180** |
| | 16 | 82.8 → 45.2 (−45%) | 244 → 55 | **375 → 235** |
| JPEG reconstruction (`tojpeg`) | 1 | 301 → 292 | 118 → 38 | 143 → 132 |
| | 16 | 57.5 → 53.3 | 131 → 51 | 197 → 193 |
| encode lossless e7 | 1 | 3526 → 3518 | **976 → 576** | **1192 → 864** |
| | 16 | 743 → 743 | **567 → 111** | **1593 → 1035** |
| encode lossy d1 e7 | 1 | 4926 → 4775 | 376 → 282 | **959 → 817** |
| | 16 | 1093 → 1091 | 200 → 91 | **994 → 847** |

16-bit token contexts (added after this matrix; Modular streams with at most 65536 leaves and lossy AC streams store a
context in 2 bytes) against the 0.8.6 build above, `--collect --cool-below 60`, 2 rounds; encoded bytes identical:

| Case | Threads | Time (ms) | Allocated (MB) | Peak RSS (MiB) |
| --- | ---: | ---: | ---: | ---: |
| encode 12 MP lossless e7 | 1 | 3617 → 3574 | 576 → 458 | **864 → 804** |
| | 16 | 765 → 736 | 114 → 113 | **1032 → 944** |
| encode 12 MP lossy d1 e7 | 1 | 4816 → 4813 | 282 → 257 | 817 → 791 |
| | 16 | 1113 → 1133 (+1.8%) | 91 → 90 | 848 → 834 |
| encode 1 MP lossless e7 | 1 / 16 | 663 → 660 / 199 → 191 | 30 → 26 / 24 → 24 | 205 → 197 / 296 → 292 |
| encode 1 MP lossless e1 | 1 / 16 | 57.2 → 58.8 / 26.5 → 26.3 | unchanged | 122 → 114 / 145 → 141 |
| encode screen lossless e7 | 1 / 16 | 401 → 398 / 132 → 132 | 38 → 32 / 28 → 28 | 193 → 191 / 251 → 248 |

The 1-thread e1 increase was noise: −1.6% with 6 rounds. Raw data: `contextsPaired` and `contextsRecheckE1` in
[memory-0.8.6.json](../measurements/memory-0.8.6.json).
| decode 16-bit lossless e7 | 1 | 3040 → 3026 | **341 → 170** | 369 → 351 |
| | 16 | 434 → 398 | 341 → 171 | 441 → 442 |
| decode lossless e7 | 1 | 2149 → 2122 | 136 → 76 | 267 → 267 |
| decode lossless e1 / lossy d1 | 1 | within ±1.6% | unchanged | unchanged |
| encode 1 MP lossless e7 | 16 | 189 → 183 | 49 → 24 | 361 → 297 |
| encode 1 MP 16-bit lossless e7 | 16 | 201 → 203 | 63 → 38 | 390 → 336 |

Standard matrix against the frozen 0.8.5 harness build (all 34 cases): allocations fall most in lossless encoding
(1 MP e1 30 → 5 MB, e3 32 → 8 MB, e7 61 → 30 MB; 12 MP e7 976 → 576 MB at 1 thread, 559 → 107 MB at 16), Modular
decoding (12 MP e2 84 → 62 MB, e7 136 → 76 MB, 16-bit e7 341 → 170 MB) and the JPEG cases (244 → 53 and 118 → 38 MB);
small images are unchanged. Default-mode peak RSS: lossless 12 MP encode 2492 → 1615 MiB (1 thread) and 2100 → 1002
MiB (16), lossy 1667 → 1201 and 1507 → 983 MiB, JPEG decode 895 → 376 and 1284 → 600 MiB, 1 MP lossless encodes at
16 threads 620–720 → 180–460 MiB. Some 16-thread lossless decodes peaked higher (12 MP e1 892 → 1310 MiB, 16-bit e7
395 → 1027 MiB) and `tojpeg` 461 → 554 MiB, with fewer collections; their per-operation peaks above are unchanged.

Times: every case is within ±3% at 1 thread except faster ones (JPEG decode −10.9%, `tojpeg` −3.8%,
`encode/photo1-d0-e3` −3.9%). At 16 threads the JPEG decode is 1.9× faster (87.6 → 45.8 ms; the row bands of the
pixel writer replace the sequential chroma upsampling) and most Modular decodes are 1–13% faster. Two 16-thread increases,
`decode/photo12-d1-e7` +9.8% and `encode/photo1-d0-e1` +4.7%, were rechecked with 6 rounds: +7.5% and +5.0% in default
mode, but −2.3% and +1.2% with `--collect`, and +0.06% and −0.04% when every process started below 60 °C
(`measure-paired.py --cool-below 60`, new in 0.8.6). Long 16-thread runs heat this laptop's CPU to its limit; the
earlier single-run increases were thermal and GC-timing noise.
