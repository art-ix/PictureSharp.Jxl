#!/usr/bin/env python3
"""Compares two scripts/measure-speed.py results as a Markdown table (warm median and allocations).

Usage: compare-speed.py <before.json> <after.json>. The reference column comes from whichever file has it.
"""
import json
import sys


def main():
    before = {r['id']: r for r in json.load(open(sys.argv[1]))}
    after = {r['id']: r for r in json.load(open(sys.argv[2]))}
    print('| Case | Before (ms) | After (ms) | Speed-up | libjxl 1 thread (ms) | After / libjxl | Allocated before → after (MiB) |')
    print('| --- | ---: | ---: | ---: | ---: | ---: | ---: |')
    for case_id, b in before.items():
        a = after.get(case_id)
        if not a:
            continue
        ref = b.get('referenceMs') or a.get('referenceMs')
        print(f"| {case_id} | {b['medianMs']:.0f} | {a['medianMs']:.0f} | {b['medianMs'] / a['medianMs']:.2f}× | "
              f"{f'{ref:.0f}' if ref else '–'} | {f'{a['medianMs'] / ref:.1f}×' if ref else '–'} | "
              f"{b['allocatedBytes'] / 2**20:.0f} → {a['allocatedBytes'] / 2**20:.0f} |")


if __name__ == '__main__':
    main()
