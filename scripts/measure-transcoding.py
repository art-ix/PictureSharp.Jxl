#!/usr/bin/env python3
"""Measure JPEG transcoding against pinned cjxl/djxl, one thread, on Linux.

Measures the median of three complete command invocations and /usr/bin/time peak RSS (includes runtime/startup).
Also reports warmed .NET API time/allocation separately; do not compare it directly with CLI wall time.
Builds the managed harness first. Outputs JSON under ignored artifacts/. Accepts an optional directory of JPEGs, or --synthetic-large to generate eight 1024x768 JPEGs.
"""
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / 'artifacts/transcoding-measurements'
LARGE = len(sys.argv) > 1 and sys.argv[1] == '--synthetic-large'
CORPUS = ROOT / 'artifacts/transcoding-large' if LARGE else Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / 'tests/assets/corpus/transcoding'
HARNESS = ROOT / 'benchmarks/PictureSharp.Jxl.Speed'


def run(command, env):
    records = []
    with tempfile.TemporaryDirectory() as temp:
        for n in range(3):
            metrics = Path(temp) / f'{n}.txt'
            start = time.perf_counter()
            p = subprocess.run(['/usr/bin/time', '-f', '%M', '-o', str(metrics), *command], check=True,
                               capture_output=True, text=True, env=env)
            records.append(dict(wallMs=(time.perf_counter() - start) * 1000,
                                peakRssKiB=int(metrics.read_text()), output=p.stdout))
    records.sort(key=lambda r: r['wallMs'])
    record = records[1]
    output = record.pop('output')
    if output.strip().startswith('{'):
        record.update(json.loads(output))
    return record


def main():
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    if LARGE:
        import runpy
        from PIL import ImageFile
        ImageFile.MAXBLOCK = 8 << 20
        make_image = runpy.run_path(str(ROOT / 'tests/assets/generate-transcoding.py'))['image']
        CORPUS.mkdir(exist_ok=True)
        image = make_image(1024, 768)
        for progressive in (False, True):
            for sampling in (0, 1, 2):
                image.save(CORPUS / f'color-{sampling}-{int(progressive)}.jpg', quality=87, subsampling=sampling, progressive=progressive)
            image.convert('L').save(CORPUS / f'gray-{int(progressive)}.jpg', quality=93, progressive=progressive)
    env = dict(os.environ)  # no DOTNET_PROCESSOR_COUNT=1, see measure-speed.py
    subprocess.run(['dotnet', 'build', str(HARNESS), '-c', 'Release', '-v', 'q'], check=True, env=env)
    dll = HARNESS / 'bin/Release/net10.0/PictureSharp.Jxl.Speed.dll'
    tools = Path(os.environ['JXL_TOOLS']) if 'JXL_TOOLS' in os.environ else None
    cjxl = str(tools / 'cjxl') if tools else 'cjxl'
    djxl = str(tools / 'djxl') if tools else 'djxl'
    result = dict(corpus=str(CORPUS), version=(ROOT / 'VERSION').read_text().strip(),
                  reference=subprocess.run([cjxl, '--version'], capture_output=True, text=True).stdout.strip(),
                  notes='CLI wall time and peak RSS include runtime/startup; .NET API medians exclude JIT. Scalar, one thread.', files=[])
    for jpeg in sorted(CORPUS.glob('*.jpg')):
        managed = ARTIFACTS / (jpeg.stem + '.managed.jxl')
        native = ARTIFACTS / (jpeg.stem + '.cjxl.jxl')
        restored = ARTIFACTS / (jpeg.stem + '.jpg')
        record = dict(name=jpeg.name, jpegBytes=jpeg.stat().st_size)
        record['managedEncode'] = run(['dotnet', str(dll), 'encode', str(jpeg), str(managed)], env)
        warm = subprocess.run(['dotnet', str(dll), 'encode', str(jpeg), str(managed), 'warm'], check=True, capture_output=True, text=True, env=env)
        record['managedEncode'].update(json.loads(warm.stdout))
        record['cjxlEncode'] = run([cjxl, str(jpeg), str(native), '-e', '7', '--lossless_jpeg=1', '--num_threads=1', '--quiet'], env)
        record['cjxlEncode']['bytes'] = native.stat().st_size
        record['managedDecodeCjxl'] = run(['dotnet', str(dll), 'decode', str(native), str(restored)], env)
        warm = subprocess.run(['dotnet', str(dll), 'decode', str(native), str(restored), 'warm'], check=True, capture_output=True, text=True, env=env)
        record['managedDecodeCjxl'].update(json.loads(warm.stdout))
        assert restored.read_bytes() == jpeg.read_bytes()
        record['djxlDecodeManaged'] = run([djxl, str(managed), str(restored), '--num_threads=1', '--quiet'], env)
        assert restored.read_bytes() == jpeg.read_bytes()
        result['files'].append(record)
        print(jpeg.name, flush=True)
    output = ARTIFACTS / ('results-' + CORPUS.name + '.json')
    output.write_text(json.dumps(result, indent=2) + '\n')
    print(output)


if __name__ == '__main__':
    main()
