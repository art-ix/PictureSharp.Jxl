#!/usr/bin/env bash
# Same-process frozen-scalar comparisons; ISA masks also change the core and are correctness evidence only.
# .NET 10 groups FMA with AVX2: EnableFMA=0 is ignored. A synthetic selector test covers that combination;
# never report an AVX2/no-FMA hardware run from that switch. See docs/design/0.8.0-simd.md.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build tests/PictureSharp.Jxl.Tests -c Release -v q
dll=tests/PictureSharp.Jxl.Tests/bin/Release/net10.0/PictureSharp.Jxl.Tests.dll
filter='/*/*/JxlSimdTests/*'
for mode in native sse41 scalar; do
  case "$mode" in
    native) envs=() ;;
    sse41) envs=(DOTNET_EnableAVX=0 JXL_TEST_CAPABILITIES=False,True,False) ;;
    scalar) envs=(DOTNET_EnableHWIntrinsic=0 JXL_TEST_CAPABILITIES=False,False,False) ;;
  esac
  env "${envs[@]}" dotnet "$dll" --treenode-filter "$filter" --report-trx \
    --results-directory "artifacts/simd-0.8.0/matrix/$mode"
done
