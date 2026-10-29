#!/usr/bin/env python3
"""Paired before/after measurements of frozen builds, never building or tracing while timing.

Usage: measure-simd.py <before.dll> <after.dll> --label name --case decode/photo1-d1-e7 [--case ...]
Both builds run with identical ISA masks. SSE4.1 results on a masked modern CPU are not old-CPU measurements.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/simd-0.8.0'


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('before', type=Path)
    parser.add_argument('after', type=Path)
    parser.add_argument('--label', required=True)
    parser.add_argument('--case', action='append', required=True, dest='cases')
    parser.add_argument('--rounds', type=int, default=2)
    parser.add_argument('--runs', type=int, default=5)
    parser.add_argument('--cpu', default='0')
    parser.add_argument('--mode', choices=['native', 'sse41', 'scalar'], default='native')
    args = parser.parse_args()
    env = dict(os.environ, DOTNET_TieredCompilation='0')
    for name in ['DOTNET_EnableAVX', 'DOTNET_EnableAVX2', 'DOTNET_EnableHWIntrinsic', 'DOTNET_EnableFMA', 'DOTNET_EnableFma']:
        env.pop(name, None)
    if args.mode == 'sse41':
        env['DOTNET_EnableAVX'] = '0'
    elif args.mode == 'scalar':
        env['DOTNET_EnableHWIntrinsic'] = '0'
    files = {'before': args.before.resolve(), 'after': args.after.resolve()}
    record = {
        'date': '2026-10-29', 'cpu': 'Intel Core i5-12500H', 'affinity': args.cpu, 'runtime': '10.0.12',
        'tieredCompilation': False, 'mode': args.mode, 'rounds': args.rounds, 'runs': args.runs,
        'builds': {key: {'dll': str(path.relative_to(ROOT)), 'jxlSha256': sha(path.with_name('PictureSharp.Jxl.dll'))} for key, path in files.items()},
        'measurements': [], 'summary': [],
    }
    listing = subprocess.run(['dotnet', str(files['after']), 'list', str(ROOT)], env=env, text=True, capture_output=True, check=True).stdout
    inputs = {parts[0]: Path(parts[1]) for line in listing.splitlines() if len(parts := line.split('\t')) >= 2}
    record['inputs'] = {case: sha(inputs[case]) for case in args.cases}
    OUT.mkdir(parents=True, exist_ok=True)
    output = OUT / (args.label + '.json')
    for case in args.cases:
        for round_index in range(args.rounds):
            for key in (['before', 'after'] if round_index % 2 == 0 else ['after', 'before']):
                with tempfile.NamedTemporaryFile() as rss:
                    p = subprocess.run(['/usr/bin/time', '-f', '%M', '-o', rss.name, 'taskset', '-c', args.cpu,
                                        'dotnet', str(files[key]), 'case', str(ROOT), case, str(args.runs)],
                                       env=env, capture_output=True, text=True, check=True)
                    result = json.loads(p.stdout)
                    result.update(variant=key, round=round_index, peakRssMiB=int(Path(rss.name).read_text()) / 1024)
                if args.mode == 'sse41' and result.get('hardware') != {'avx2': False, 'sse41': True, 'fma': False}:
                    raise RuntimeError(f'Incorrect SSE4.1 mask: {result.get("hardware")}')
                record['measurements'].append(result)
                output.write_text(json.dumps(record, indent=2) + '\n')
        before = statistics.median(r['medianMs'] for r in record['measurements'] if r['id'] == case and r['variant'] == 'before')
        after = statistics.median(r['medianMs'] for r in record['measurements'] if r['id'] == case and r['variant'] == 'after')
        record['summary'].append({'case': case, 'beforeMs': before, 'afterMs': after, 'changePercent': (after / before - 1) * 100})
        output.write_text(json.dumps(record, indent=2) + '\n')
        print(f'{case}: {before:.3f} -> {after:.3f} ms ({(after / before - 1) * 100:+.2f}%)', flush=True)


if __name__ == '__main__':
    main()
