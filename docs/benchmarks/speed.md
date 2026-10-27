# Speed measurements — 0.7.3 (scalar)

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

## Reproduce

```bash
python3 scripts/make-speed-inputs.py
python3 scripts/measure-speed.py --package 0.7.2          # baseline
python3 scripts/measure-speed.py                          # current sources, about 3.5 minutes
python3 scripts/measure-speed.py --all                    # adds the 12 MP encodes (several more minutes)
python3 scripts/measure-speed.py decode/photo1            # only cases whose id contains a filter
python3 scripts/compare-speed.py artifacts/speed-measurements/baseline-0.7.2.json artifacts/speed-measurements/current.json
scripts/profile-speed.sh decode/photo1-d1-e7              # hotspots of one case
scripts/golden-hash.sh 0.7.2                              # output identical to 0.7.2
```
