#!/usr/bin/env python3
"""Compare two frozen speed-harness builds at the same thread counts, alternating A/B then B/A.

Example: measure-paired.py old.dll new.dll --case encode/photo1-d0-e7 --out artifacts/paired.json
Default: 1 and -1 threads, CPU 0 and CPUs 0-15, two rounds of five warm calls (three for 12 MP).
Do not run builds, tracing or other benchmarks concurrently. The harness warms up each fresh process.
Results are saved after every process, with input/build hashes, allocations, GC and /usr/bin/time peak RSS.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('baseline', type=Path)
    parser.add_argument('current', type=Path)
    parser.add_argument('--case', action='append', required=True, dest='cases')
    parser.add_argument('--threads', action='append', type=int)
    parser.add_argument('--rounds', type=int, default=2)
    parser.add_argument('--runs', type=int, default=5)
    parser.add_argument('--large-runs', type=int, default=3)
    parser.add_argument('--small-runs', type=int, default=200)
    parser.add_argument('--cpu', default='0')
    parser.add_argument('--cpus', default='0-15')
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    if min(args.rounds, args.runs, args.large_runs, args.small_runs) < 1:
        parser.error('rounds and run counts must be positive')
    degrees = args.threads or [1, -1]
    if any(t == 0 or t < -1 for t in degrees):
        parser.error('threads must be -1 or positive')
    env = dict(os.environ, DOTNET_TieredCompilation='0')
    for key in ('DOTNET_EnableAVX', 'DOTNET_EnableAVX2', 'DOTNET_EnableHWIntrinsic', 'JXL_THREADS'):
        env.pop(key, None)
    builds = {'baseline': args.baseline.resolve(), 'current': args.current.resolve()}
    listing = subprocess.run(['dotnet', str(builds['current']), 'list', str(ROOT)], env=env,
                             capture_output=True, text=True, check=True).stdout
    inputs = {parts[0]: Path(parts[1]) for line in listing.splitlines()
              if len(parts := line.split('\t')) >= 3}
    record = {
        'startedUtc': datetime.now(timezone.utc).isoformat(), 'platform': platform.platform(),
        'cpu': next(line.split(':', 1)[1].strip() for line in Path('/proc/cpuinfo').read_text().splitlines()
                    if line.startswith('model name')),
        'runtimes': subprocess.check_output(['dotnet', '--list-runtimes'], text=True).splitlines(),
        'tieredCompilation': False, 'rounds': args.rounds, 'threads': degrees,
        'affinity': {'single': args.cpu, 'parallel': args.cpus},
        'builds': {key: {'harnessSha256': sha(path), 'jxlSha256': sha(path.with_name('PictureSharp.Jxl.dll'))}
                   for key, path in builds.items()},
        'inputs': {case: sha(inputs[case]) for case in args.cases},
        'measurements': [], 'summary': [],
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)

    def save():
        args.out.write_text(json.dumps(record, indent=2) + '\n')

    for case in args.cases:
        runs = (args.small_runs if case in ('decode/tiny-1x1', 'decode/small-64x64')
                else args.large_runs if 'photo12' in case else args.runs)
        for degree in degrees:
            for round_index in range(args.rounds):
                for key in (['baseline', 'current'] if round_index % 2 == 0 else ['current', 'baseline']):
                    with tempfile.NamedTemporaryFile() as rss:
                        proc = subprocess.run([
                            '/usr/bin/time', '-f', '%M', '-o', rss.name, 'taskset', '-c',
                            args.cpu if degree == 1 else args.cpus, 'dotnet', str(builds[key]),
                            'case', str(ROOT), case, str(runs),
                        ], env=dict(env, JXL_THREADS=str(degree)), capture_output=True, text=True)
                        if proc.returncode:
                            raise RuntimeError(f'{case} / {key} / {degree}: {proc.stderr}\n{proc.stdout}')
                        result = json.loads(proc.stdout)
                        result.update(variant=key, round=round_index, runs=runs,
                                      peakRssMiB=int(Path(rss.name).read_text()) / 1024)
                    record['measurements'].append(result)
                    save()
            summary = {'case': case, 'threads': degree}
            for key in builds:
                rows = [r for r in record['measurements']
                        if r['id'] == case and r['threads'] == degree and r['variant'] == key]
                summary[key + 'Ms'] = statistics.median(r['medianMs'] for r in rows)
                summary[key + 'AllocatedBytes'] = statistics.median(r['allocatedBytes'] for r in rows)
                summary[key + 'PeakRssMiB'] = max(r['peakRssMiB'] for r in rows)
                sizes = {r['bytes'] for r in rows}
                if len(sizes) != 1:
                    raise RuntimeError(f'{case}: output size varies between runs')
                summary[key + 'Bytes'] = sizes.pop()
            if summary['baselineBytes'] != summary['currentBytes']:
                raise RuntimeError(f'{case}: output size differs between builds')
            summary['deltaPercent'] = (summary['currentMs'] / summary['baselineMs'] - 1) * 100
            record['summary'].append(summary)
            save()
            print(f"{case} t={degree}: {summary['baselineMs']:.3f} -> {summary['currentMs']:.3f} ms "
                  f"({summary['deltaPercent']:+.2f}%), RSS {summary['baselinePeakRssMiB']:.0f} -> "
                  f"{summary['currentPeakRssMiB']:.0f} MiB", flush=True)


if __name__ == '__main__':
    main()
