#!/usr/bin/env bash
# Fuzzing campaigns for 0.8.7 (docs/design/0.8.7-fuzzing.md) and the differential runs of 0.8.8
# (docs/design/0.8.8-differential.md). Runs over SSH: no hardcoded paths, nohup-friendly.
#   scripts/fuzz.sh custom   [options]   the mutation fuzzer, targets in parallel batches
#   scripts/fuzz.sh afl      [options]   AFL++ with SharpFuzz on the same batches
#   scripts/fuzz.sh campaign [options]   per batch: custom for --custom-time, then AFL++ for --afl-time (Orange Pi plan)
#   scripts/fuzz.sh diff     [options]   the diff-* targets against cjxl/djxl (JXL_TOOLS or PATH) with the mutation fuzzer;
#                                        defaults: all four diff targets, --out artifacts/fuzz-0.8.8, --timeout 60
#   scripts/fuzz.sh status   [--out DIR] findings per target and engine
#   scripts/fuzz.sh repro    [--out DIR] reruns every saved AFL++ crash/hang through the uninstrumented fuzzer
#   scripts/fuzz.sh boundary [--out DIR] dimension/SIMD boundary set: identical in native, SSE4.1 and scalar modes (x86;
#                                        native only on arm64); its files become extra seeds of later runs
# Options: --targets "a b ..." (default: all six), --parallel P targets at a time (default 6), --workers N per target
#   (default 2), --cpus "4-5 6-7" one CPU list per parallel slot (default: none), --time SEC per target for custom/afl,
#   --custom-time SEC and --afl-time SEC for campaign, --timeout SEC per input (default 10), --seed S, --out DIR
#   (default artifacts/fuzz-0.8.7), --heap-mb M (default 2048), --single: one run per input under AFL++ (no 1 vs 4
#   thread comparison; the FromJpeg round trip stays), several times faster on decode and fromjpeg.
# Examples: PC: scripts/fuzz.sh custom --time 3600
#   Orange Pi: nohup scripts/fuzz.sh campaign --parallel 2 --cpus "4-5 6-7" --custom-time 3600 --afl-time 7200 > fuzz.log 2>&1 &
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
command="${1:-}"; shift || true
targets="container headers decode entropy tojpeg fromjpeg"; parallel=6; workers=2; cpus=""; time=3600
custom_time=3600; afl_time=7200; timeout=10; seed=1; out="$root/artifacts/fuzz-0.8.7"; heap_mb=2048; single=0
if [[ "$command" == diff ]]; then
  targets="diff-decode diff-encode diff-tojpeg diff-fromjpeg"; parallel=4; timeout=60; out="$root/artifacts/fuzz-0.8.8"
fi
while [[ $# -gt 0 ]]; do
  case "$1" in
    --single) single=1; shift; continue ;;
    --targets) targets="$2" ;; --parallel) parallel="$2" ;; --workers) workers="$2" ;; --cpus) cpus="$2" ;;
    --time) time="$2" ;; --custom-time) custom_time="$2" ;; --afl-time) afl_time="$2" ;; --timeout) timeout="$2" ;;
    --seed) seed="$2" ;; --out) out="$(mkdir -p "$2" && cd "$2" && pwd)" ;; --heap-mb) heap_mb="$2" ;;
    *) echo "Unknown option $1" >&2; exit 2 ;;
  esac
  shift 2
done
mkdir -p "$out"
build="$out/build"
fuzzer="$build/fuzz/PictureSharp.Jxl.Fuzz.dll"
harness="$build/afl/PictureSharp.Jxl.SharpFuzz.dll"
corpus="$root/tests/assets/corpus"
extra=(); [[ -d "$out/boundary-seeds" ]] && extra=(--extra "$out/boundary-seeds")
read -r -a slots <<< "$cpus"

log() { echo "$(date '+%F %T') $*"; }

build_custom() {
  [[ -f "$fuzzer" ]] && return
  log "building the mutation fuzzer"
  dotnet publish "$root/tools/PictureSharp.Jxl.Fuzz" -c Release -v q -o "$build/fuzz" > "$build.custom.log" 2>&1 || { cat "$build.custom.log"; exit 1; }
}

build_afl() {
  [[ -f "$harness" ]] && return
  command -v afl-fuzz > /dev/null || { echo "afl-fuzz is not installed (apt install afl++)" >&2; exit 1; }
  log "building and instrumenting the SharpFuzz harness"
  dotnet publish "$root/tools/PictureSharp.Jxl.SharpFuzz" -c Release -v q -o "$build/afl" > "$build.afl.log" 2>&1 || { cat "$build.afl.log"; exit 1; }
  (cd "$root" && dotnet tool restore > /dev/null && dotnet tool run sharpfuzz "$build/afl/PictureSharp.Jxl.FuzzTargets.dll") >> "$build.afl.log" 2>&1 \
    || { cat "$build.afl.log"; exit 1; }
}

# $1 = slot index: the CPU list of that slot, if any.
pin() { if [[ ${#slots[@]} -gt 0 ]]; then echo "taskset -c ${slots[$1]}"; fi; }

run_custom() { # target slot seconds
  local target="$1" slot="$2" seconds="$3"
  log "custom $target for ${seconds}s (slot $slot)"
  $(pin "$slot") dotnet "$fuzzer" fuzz "$target" --time "$seconds" --workers "$workers" --timeout "$timeout" \
    --seed "$seed" --heap-mb "$heap_mb" --corpus "$corpus" "${extra[@]}" --out "$out/custom" > "$out/custom-$target.log" 2>&1
  log "custom $target: $(tail -1 "$out/custom-$target.log")"
}

run_afl() { # target slot seconds: one main and workers-1 secondary instances on the slot's CPUs
  local target="$1" slot="$2" seconds="$3"
  local input="$out/afl-seeds/$target" findings="$out/afl/$target"
  if [[ ! -d "$input" ]]; then
    dotnet "$fuzzer" export-seeds "$target" --corpus "$corpus" "${extra[@]}" --output "$input.raw" > /dev/null
    # Keeps the seeds with distinct coverage; all of them if afl-cmin fails.
    AFL_SKIP_BIN_CHECK=1 afl-cmin -i "$input.raw" -o "$input" -t $((timeout * 1000)) -m none -- dotnet "$harness" "$target" \
      > "$out/afl-cmin-$target.log" 2>&1 || { rm -rf "$input"; cp -r "$input.raw" "$input"; }
  fi
  mkdir -p "$findings"
  local cores=() core_pattern; core_pattern="$(cat /proc/sys/kernel/core_pattern 2>/dev/null || echo core)"
  if [[ ${#slots[@]} -gt 0 ]]; then
    IFS=- read -r first last <<< "${slots[$slot]}"
    for ((c = first; c <= ${last:-$first}; c++)); do cores+=("$c"); done
  fi
  log "afl $target for ${seconds}s (slot $slot)"
  local pids=()
  for ((i = 0; i < workers; i++)); do
    local role=(-S "s$i"); [[ $i == 0 ]] && role=(-M main)
    local taskset=(); [[ ${#cores[@]} -gt 0 ]] && taskset=(taskset -c "${cores[$((i % ${#cores[@]}))]}")
    # SharpFuzz runs .NET in AFL's persistent mode; the heap limit replaces afl-fuzz -m.
    AFL_SKIP_BIN_CHECK=1 AFL_SKIP_CPUFREQ=1 AFL_NO_UI=1 AFL_NO_AFFINITY=1 AFL_AUTORESUME=1 \
      AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES=$([[ "$core_pattern" == core ]] && echo 0 || echo 1) \
      DOTNET_GCHeapHardLimit="0x$(printf '%X' $((heap_mb << 20)))" JXL_FUZZ_SINGLE=$single \
      "${taskset[@]}" afl-fuzz "${role[@]}" -i "$input" -o "$findings" -x "$root/tools/PictureSharp.Jxl.SharpFuzz/jxl.dict" \
      -t $((timeout * 1000)) -m none -V "$seconds" -- dotnet "$harness" "$target" > "$out/afl-$target-$i.log" 2>&1 &
    pids+=($!)
  done
  wait "${pids[@]}" || true
  log "afl $target: $(afl_summary "$findings")"
}

afl_summary() { # findings dir
  local execs=0 crashes=0 hangs=0
  for stats in "$1"/*/fuzzer_stats; do
    [[ -f "$stats" ]] || continue
    execs=$((execs + $(awk -F: '/^execs_done/ {print $2+0}' "$stats")))
    crashes=$((crashes + $(find "$(dirname "$stats")/crashes" -name 'id:*' 2>/dev/null | wc -l)))
    hangs=$((hangs + $(find "$(dirname "$stats")/hangs" -name 'id:*' 2>/dev/null | wc -l)))
  done
  echo "$execs execs, $crashes crashes, $hangs hangs"
}

# Runs "$@" (a function taking target, slot) over the targets, $parallel at a time.
batches() {
  local list=($targets) i=0
  while [[ $i -lt ${#list[@]} ]]; do
    local pids=()
    for ((slot = 0; slot < parallel && i < ${#list[@]}; slot++, i++)); do
      "$@" "${list[$i]}" "$slot" &
      pids+=($!)
    done
    wait "${pids[@]}"
  done
}

campaign_one() { run_custom "$1" "$2" "$custom_time"; run_afl "$1" "$2" "$afl_time"; }
custom_one() { run_custom "$1" "$2" "$time"; }
afl_one() { run_afl "$1" "$2" "$time"; }

status() {
  for target in $targets; do
    local custom="none"
    [[ -d "$out/custom/$target" ]] && custom="$(cd "$out/custom/$target" && for k in */; do echo -n "${k%/} $(ls "$k" | grep -c '\.bin$') "; done)"
    printf '%-10s custom: %-40s afl: %s\n' "$target" "${custom:-none}" "$(afl_summary "$out/afl/$target")"
  done
}

repro() {
  build_custom
  for target in $targets; do
    for file in "$out/afl/$target"/*/crashes/id:* "$out/afl/$target"/*/hangs/id:*; do
      [[ -f "$file" ]] || continue
      timeout $((timeout * 3)) dotnet "$fuzzer" repro "$target" "$file" | head -20 || echo "$file: exit $? (hang or crash)"
    done
  done
}

boundary() {
  build_custom
  local modes=(native) result=0
  [[ "$(uname -m)" == x86_64 ]] && modes+=(sse41 scalar)
  for mode in "${modes[@]}"; do
    local env=() files=()
    case "$mode" in
      native) files=(--output "$out/boundary-seeds") ;;
      sse41) env=(DOTNET_EnableAVX=0) ;;
      scalar) env=(DOTNET_EnableHWIntrinsic=0) ;;
    esac
    log "boundary $mode"
    env "${env[@]}" dotnet "$fuzzer" boundary "${files[@]}" > "$out/boundary-$mode.txt" || { echo "boundary $mode: lossless round trip failed" >&2; result=1; }
    if [[ "$mode" != native ]] && ! cmp -s "$out/boundary-native.txt" "$out/boundary-$mode.txt"; then
      echo "boundary $mode differs from native:" >&2; diff "$out/boundary-native.txt" "$out/boundary-$mode.txt" | head -20 >&2; result=1
    fi
  done
  log "boundary: $(wc -l < "$out/boundary-native.txt") encodes, modes ${modes[*]}, $([[ $result == 0 ]] && echo identical || echo DIFFERENT)"
  return $result
}

case "$command" in
  custom) build_custom; batches custom_one ;;
  diff)
    command -v "${JXL_TOOLS:+$JXL_TOOLS/}djxl" > /dev/null || { echo "cjxl/djxl/ssimulacra2 v0.12.0 are required (JXL_TOOLS or PATH)" >&2; exit 1; }
    build_custom; batches custom_one ;;
  afl) build_custom; build_afl; batches afl_one ;;
  campaign) build_custom; build_afl; batches campaign_one ;;
  status) status ;;
  repro) repro ;;
  boundary) boundary ;;
  *) sed -n '2,18p' "$0"; exit 2 ;;
esac
log "done"
