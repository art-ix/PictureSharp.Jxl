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

libjxl's encoder keeps reference slot 3 for patches and fails when a frame is saved there, so specs use slots 0–2.
The output is deterministic (no parallel runner).
