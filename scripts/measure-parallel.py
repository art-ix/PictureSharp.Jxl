#!/usr/bin/env python3
"""Thread scaling of frozen builds: a baseline package, the sources at 1 thread and at N threads, plus cjxl/djxl.

Usage: measure-parallel.py <baseline.dll> <current.dll> --label name --case decode/photo1-d1-e7 [--case ...]
       [--rounds 2] [--runs 5] [--cpu 0] [--cpus 0-15] [--threads -1] [--no-reference]
Variants alternate inside every round (rotated order), each in its own process with tiered compilation off:
baseline (single-threaded package, pinned to --cpu), current-1 (JXL_THREADS=1, --cpu) and current-N
(JXL_THREADS=--threads, pinned to --cpus). The reference runs cjxl/djxl 0.12.0 with --num_threads=1 on --cpu and
--num_threads=<number of --cpus> on --cpus, timed by libjxl itself (--num_reps=3, PNG I/O excluded). Nothing else
may run meanwhile. Writes artifacts/parallel-0.8.1/<label>.json.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import statistics
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/parallel-0.8.1'


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def cpu_count(cpus):
    return sum(int(b) - int(a) + 1 if '-' in part else 1
               for part in cpus.split(',') for a, b in [part.split('-') if '-' in part else (part, part)])


def reference(command, source, cpus, threads):
    """libjxl's own geomean MP/s over three repetitions, converted to milliseconds."""
    with tempfile.TemporaryDirectory() as temp:
        parts = command.replace('{in}', str(source)).replace('{out}', str(Path(temp) / 'ref')).split()
        if parts[0] == 'djxl' and parts[2].endswith('.png'):
            parts = [parts[0], parts[1], '--disable_output', *parts[3:]]
        p = subprocess.run(['taskset', '-c', cpus, *parts, '--num_reps=3', f'--num_threads={threads}'],
                           capture_output=True, text=True, check=True)
    m = re.search(r'(\d+) x (\d+), geomean: ([\d.]+) MP/s', p.stdout + p.stderr)
    return int(m[1]) * int(m[2]) / float(m[3]) / 1000 if m else None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('baseline', type=Path)
    parser.add_argument('current', type=Path)
    parser.add_argument('--label', required=True)
    parser.add_argument('--case', action='append', required=True, dest='cases')
    parser.add_argument('--rounds', type=int, default=2)
    parser.add_argument('--runs', type=int, default=5)
    parser.add_argument('--cpu', default='0')
    parser.add_argument('--cpus', default='0-15')
    parser.add_argument('--threads', type=int, default=-1)
    parser.add_argument('--no-reference', action='store_true')
    args = parser.parse_args()
    base_env = dict(os.environ, DOTNET_TieredCompilation='0')
    for name in ['DOTNET_EnableAVX', 'DOTNET_EnableAVX2', 'DOTNET_EnableHWIntrinsic', 'JXL_THREADS']:
        base_env.pop(name, None)
    variants = {
        'baseline': (args.baseline.resolve(), args.cpu, None),
        'current-1': (args.current.resolve(), args.cpu, 1),
        'current-N': (args.current.resolve(), args.cpus, args.threads),
    }
    record = {
        'date': '2026-10-29', 'cpu': 'Intel Core i5-12500H (4P+8E cores, 16 logical CPUs)', 'runtime': '10.0.12',
        'tieredCompilation': False, 'rounds': args.rounds, 'runs': args.runs,
        'variants': {key: {'dll': str(path.relative_to(ROOT)), 'jxlSha256': sha(path.with_name('PictureSharp.Jxl.dll')),
                           'affinity': cpus, 'threads': threads} for key, (path, cpus, threads) in variants.items()},
        'measurements': [], 'summary': [],
    }
    listing = subprocess.run(['dotnet', str(variants['current-1'][0]), 'list', str(ROOT)], env=base_env, text=True,
                             capture_output=True, check=True).stdout
    inputs = {parts[0]: (Path(parts[1]), parts[2]) for line in listing.splitlines() if len(parts := line.split('\t')) >= 3}
    record['inputs'] = {case: sha(inputs[case][0]) for case in args.cases}
    OUT.mkdir(parents=True, exist_ok=True)
    output = OUT / (args.label + '.json')
    keys = list(variants)
    for case in args.cases:
        for round_index in range(args.rounds):
            order = keys[round_index % 3:] + keys[:round_index % 3]
            for key in order:
                dll, cpus, threads = variants[key]
                env = dict(base_env, **({'JXL_THREADS': str(threads)} if threads is not None else {}))
                with tempfile.NamedTemporaryFile() as rss:
                    p = subprocess.run(['/usr/bin/time', '-f', '%M', '-o', rss.name, 'taskset', '-c', cpus,
                                        'dotnet', str(dll), 'case', str(ROOT), case, str(args.runs)],
                                       env=env, capture_output=True, text=True, check=True)
                    result = json.loads(p.stdout)
                    result.update(variant=key, round=round_index, peakRssMiB=int(Path(rss.name).read_text()) / 1024)
                record['measurements'].append(result)
                output.write_text(json.dumps(record, indent=2) + '\n')
        summary = {'case': case}
        for key in keys:
            rows = [r for r in record['measurements'] if r['id'] == case and r['variant'] == key]
            summary[key + 'Ms'] = statistics.median(r['medianMs'] for r in rows)
            summary[key + 'Bytes'] = rows[0]['bytes']
            summary[key + 'PeakRssMiB'] = max(r['peakRssMiB'] for r in rows)
        if len({summary[key + 'Bytes'] for key in ('current-1', 'current-N')}) != 1:
            raise RuntimeError(f'{case}: output size differs between 1 and N threads')
        if not args.no_reference:
            source, command = inputs[case]
            summary['libjxl1Ms'] = reference(command, source, args.cpu, 1)
            summary['libjxlNMs'] = reference(command, source, args.cpus, cpu_count(args.cpus))
        record['summary'].append(summary)
        output.write_text(json.dumps(record, indent=2) + '\n')
        line = (f"{case}: 0.8.0 {summary['baselineMs']:.1f} | 1 thread {summary['current-1Ms']:.1f} | "
                f"N {summary['current-NMs']:.1f} ms ({summary['current-1Ms'] / summary['current-NMs']:.2f}x)")
        if not args.no_reference:
            line += f" | libjxl 1 {summary['libjxl1Ms'] or 0:.1f} / N {summary['libjxlNMs'] or 0:.1f} ms"
        print(line, flush=True)


if __name__ == '__main__':
    main()
