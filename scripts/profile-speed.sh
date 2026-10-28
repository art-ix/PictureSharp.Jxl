#!/usr/bin/env bash
# Samples one speed case (benchmarks/PictureSharp.Jxl.Speed, see `list`) with dotnet-trace and prints the methods with
# the most exclusive and inclusive CPU samples. Usage: scripts/profile-speed.sh <case id> [runs] [top]
# Needs dotnet-trace (dotnet tool install -g dotnet-trace). Traces go to ignored artifacts/traces.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
id="$1"; runs="${2:-3}"; top="${3:-25}"
out="$root/artifacts/traces/${id//\//_}"
mkdir -p "$root/artifacts/traces"
dotnet build "$root/benchmarks/PictureSharp.Jxl.Speed" -c Release -v q --artifacts-path "$root/artifacts/speed-build/source" >/dev/null
dotnet-trace collect --profile dotnet-sampled-thread-time,dotnet-common --format Speedscope -o "$out.nettrace" -- \
  dotnet "$root/artifacts/speed-build/source/bin/PictureSharp.Jxl.Speed/release/PictureSharp.Jxl.Speed.dll" case "$root" "$id" "$runs" >/dev/null
python3 "$root/scripts/speedscope-top.py" "$out.speedscope.json" "$top"
