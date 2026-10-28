#!/usr/bin/env bash
# Compares every observable output of the current sources with a released package (default 0.7.9; the harness uses the
# PictureSharp 0.9.0 PreserveBitDepth API, so older baselines need the harness from their own tag).
# Usage: scripts/golden-hash.sh [baseline version] [regex]. A regex limits the check to results whose "name kind" matches
# (the baseline is always complete and filtered here). Inputs: corpus, samples/private, artifacts/speed-inputs
# (scripts/make-speed-inputs.py). Results under ignored artifacts/golden; exit code 1 on any difference.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
baseline="${1:-0.7.9}"
filter="${2:-}"
tool="$root/tools/PictureSharp.Jxl.Golden"
out="$root/artifacts/golden"
mkdir -p "$out"
run() { # $1 = artifacts dir, $2 = result file, rest = build properties
  local dir="$1" result="$2"; shift 2
  dotnet build "$tool" -c Release -v q --artifacts-path "$dir" "$@" >/dev/null
  dotnet "$dir/bin/PictureSharp.Jxl.Golden/release/PictureSharp.Jxl.Golden.dll" "$root" ${FILTER:+"$FILTER"} > "$result"
}
if [[ ! -s "$out/baseline-$baseline.txt" ]]; then
  FILTER= run "$out/build-$baseline" "$out/baseline-$baseline.txt" "-p:JxlPackageVersion=$baseline"
fi
FILTER="$filter" run "$out/build-current" "$out/current.txt"
expected="$out/baseline-$baseline.txt"
if [[ -n "$filter" ]]; then
  expected="$out/expected.txt"
  python3 -c 'import re, sys
f = re.compile(sys.argv[1])
for line in open(sys.argv[2]):
    if f.search(" ".join(line.split(" ", 2)[:2])): print(line, end="")' "$filter" "$out/baseline-$baseline.txt" > "$expected"
fi
if diff -u "$expected" "$out/current.txt" > "$out/diff.txt"; then
  echo "golden: identical to $baseline ($(wc -l < "$out/current.txt") results${filter:+ matching $filter})"
else
  echo "golden: $(grep -c '^[-+][^-+]' "$out/diff.txt") changed lines vs $baseline, see artifacts/golden/diff.txt" >&2
  exit 1
fi
