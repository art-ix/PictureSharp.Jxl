#!/usr/bin/env python3
"""Measure decode/encode speed of the managed codec against pinned cjxl/djxl, one thread, Linux.

Inputs: scripts/make-speed-inputs.py (artifacts/speed-inputs) and the ignored samples/private photos.
Each case runs in its own process (DOTNET_PROCESSOR_COUNT=1): one cold call (includes JIT), ~2 s of warm-up for tiered
JIT, then N timed calls (default 5);
reports the warm median, allocations, GC counts and peak RSS of the process (/usr/bin/time, includes the runtime).
The reference is libjxl's own timing (MP/s over --num_reps=3, PNG I/O excluded, its SIMD enabled).
Usage: measure-speed.py [substring filter...] [--runs N] [--no-reference] [--package x.y.z] [--all]. --package measures a
released package (artifacts/ or nuget.org) instead of the sources, e.g. the baseline of an optimisation series.
The default set takes under 5 minutes; the 12 MP encodes (LONG, several minutes on their own) run only with --all or
when a filter names them. Writes artifacts/speed-measurements/<label>.json
(label = $SPEED_LABEL or 'current') and prints a Markdown table.
"""
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
HARNESS = ROOT / 'benchmarks/PictureSharp.Jxl.Speed'
OUT = ROOT / 'artifacts/speed-measurements'
LONG = ('encode/photo12-',)
TOOLS = Path(os.environ['JXL_TOOLS']) if 'JXL_TOOLS' in os.environ else None


def tool(name):
    return str(TOOLS / name) if TOOLS else name


def reference(command, source, temp):
    """Runs the cjxl/djxl command with --num_reps=3 and converts its reported geomean MP/s to milliseconds."""
    parts = command.replace('{in}', str(source)).replace('{out}', str(Path(temp) / 'ref')).split()
    parts[0] = tool(parts[0])
    if parts[0].endswith('djxl') and parts[2].endswith('.png'):
        parts = [parts[0], parts[1], '--disable_output', *parts[3:]]
    p = subprocess.run([*parts, '--num_reps=3', '--num_threads=1'], capture_output=True, text=True, check=True)
    m = re.search(r'(\d+) x (\d+), geomean: ([\d.]+) MP/s', p.stdout + p.stderr)
    return int(m[1]) * int(m[2]) / float(m[3]) / 1000 if m else None


def selected(case_id, filters, everything):
    """Filters match substrings of the case id; without filters, every case except LONG ones (unless --all)."""
    if filters:
        return any(f in case_id for f in filters)
    return everything or not case_id.startswith(LONG)


def main():
    args = sys.argv[1:]
    runs = int(args[args.index('--runs') + 1]) if '--runs' in args else 5
    package = args[args.index('--package') + 1] if '--package' in args else None
    with_reference = '--no-reference' not in args
    everything = '--all' in args
    started = time.monotonic()
    filters = [a for i, a in enumerate(args) if not a.startswith('--') and (i == 0 or args[i - 1] not in ('--runs', '--package'))]
    env = dict(os.environ, DOTNET_PROCESSOR_COUNT='1', DOTNET_TieredPGO='1')
    build = ROOT / 'artifacts/speed-build' / (package or 'source')
    subprocess.run(['dotnet', 'build', str(HARNESS), '-c', 'Release', '-v', 'q', '--artifacts-path', str(build),
                    *([f'-p:JxlPackageVersion={package}'] if package else [])], check=True, env=env, stdout=subprocess.DEVNULL)
    dll = build / 'bin/PictureSharp.Jxl.Speed/release/PictureSharp.Jxl.Speed.dll'
    listing = subprocess.run(['dotnet', str(dll), 'list', str(ROOT)], capture_output=True, text=True, check=True, env=env).stdout
    cases = [line.split('\t') for line in listing.splitlines() if line and selected(line.split('\t')[0], filters, everything)]
    results = []
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as temp:
        for case_id, source, command in cases:
            metrics = Path(temp) / 'time.txt'
            p = subprocess.run(['/usr/bin/time', '-f', '%M', '-o', str(metrics), 'dotnet', str(dll), 'case', str(ROOT), case_id, str(runs)],
                               capture_output=True, text=True, check=True, env=env)
            record = json.loads(p.stdout)
            record['peakRssMiB'] = int(metrics.read_text().split()[-1]) / 1024
            record['referenceMs'] = reference(command, source, temp) if with_reference else None
            results.append(record)
            print(f"{case_id:32} {record['medianMs']:9.1f} ms  ref {record['referenceMs'] or 0:8.1f} ms  "
                  f"alloc {record['allocatedBytes'] / 2**20:8.1f} MiB  rss {record['peakRssMiB']:7.1f} MiB", file=sys.stderr)
    print(f'{len(results)} cases in {time.monotonic() - started:.0f} s', file=sys.stderr)
    label = os.environ.get('SPEED_LABEL', 'current')
    (OUT / f'{label}.json').write_text(json.dumps(results, indent=1))
    print('| Case | Warm median (ms) | libjxl 1 thread (ms) | Ratio | Allocated (MiB) | Peak RSS (MiB) |')
    print('| --- | ---: | ---: | ---: | ---: | ---: |')
    for r in results:
        ref = r['referenceMs']
        ratio = f"{r['medianMs'] / ref:.1f}×" if ref else '–'
        print(f"| {r['id']} | {r['medianMs']:.0f} | {ref:.0f} | {ratio} | {r['allocatedBytes'] / 2**20:.0f} | {r['peakRssMiB']:.0f} |"
              if ref else f"| {r['id']} | {r['medianMs']:.0f} | – | – | {r['allocatedBytes'] / 2**20:.0f} | {r['peakRssMiB']:.0f} |")


if __name__ == '__main__':
    main()
