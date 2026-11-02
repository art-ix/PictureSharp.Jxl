# Corpus tools

`jxl_anim_gen.cc` writes the JPEG XL animations that cjxl cannot produce (loop counts, tick rates, timecodes, frame
names, zero-duration frames, crops, blend modes, reference slots) with the libjxl 0.12.0 encoder API. It is a
development tool for `tests/assets/generate-corpus.py`; nothing here is shipped, and the generated files are committed,
so tests and CI do not need it. The spec format is described at the top of the source.

Build it against the libjxl 0.12.0 build of the pinned sources (the same build that provides `jxl_from_tree`):

```bash
B=~/.cache/picturesharp-jxl/libjxl-0.12.0-build
g++ -std=c++17 -O2 -I"$B/lib/include" -Ireference/libjxl-0.12.0/lib/include tests/assets/tools/jxl_anim_gen.cc \
  -o "$B/tools/jxl_anim_gen" -Wl,-rpath,"$B/lib" "$B/lib/libjxl-internal.a" "$B/lib/libjxl_cms.so.0.12.0" \
  -lbrotlienc -lbrotlidec -lbrotlicommon -lhwy -lpthread
ln -sf "$B/tools/jxl_anim_gen" ~/.local/bin/jxl_anim_gen
```

Since 0.8.16 it also writes stills (`still`), gray images, premultiplied alpha and further extra channels of any type
(`extra spot color=r,g,b,a`, `extra depth`, …) with an alpha ramp per frame (`ramp=1`). libjxl's encoder rejects the
Unknown type.

`jxl_dec_straight.cc` is the oracle for images with premultiplied alpha: it decodes like djxl 0.12.0 (8-bit dithered
or 16-bit, orientation applied, spot colours rendered; `--no_coalescing` for layers) but with
`JxlDecoderSetUnpremultiplyAlpha`, which djxl has no option for, and writes one PAM per frame. Build it the same way:

```bash
g++ -std=c++17 -O2 -I"$B/lib/include" -Ireference/libjxl-0.12.0/lib/include tests/assets/tools/jxl_dec_straight.cc \
  -o "$B/tools/jxl_dec_straight" -Wl,-rpath,"$B/lib" "$B/lib/libjxl-internal.a" "$B/lib/libjxl_cms.so.0.12.0" \
  -lbrotlienc -lbrotlidec -lbrotlicommon -lhwy -lpthread
ln -sf "$B/tools/jxl_dec_straight" ~/.local/bin/jxl_dec_straight
```

This build uses GCC, while the released djxl is built with Clang: lossless output is identical to djxl, lossy output
differs from djxl by at most 1 of 255 in a few samples, which the lossy tolerance covers.

libjxl's encoder keeps reference slot 3 for patches and fails when a frame is saved there, so specs use slots 0–2.
The output is deterministic (no parallel runner).
