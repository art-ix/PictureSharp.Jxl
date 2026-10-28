# Speed measurements — 0.7.3–0.7.7 (scalar)

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
