#!/usr/bin/env python3
"""Exclusive/inclusive sample share per method of a dotnet-trace Speedscope file (PictureSharp frames shortened).

Usage: speedscope-top.py <file.speedscope.json> [top]. Only the thread with the most samples is counted.
"""
import collections
import json
import sys


# dotnet-sampled-thread-time marks the leaf of every sample with one of these; exclusive time goes to its caller.
PSEUDO = {'CPU_TIME', 'UNMANAGED_CODE_TIME'}


def short(name):
    name = name.replace('PictureSharp.Formats.Jxl.', '').split('(')[0]
    return name.split('!')[-1]


def main():
    data = json.load(open(sys.argv[1]))
    top = int(sys.argv[2]) if len(sys.argv) > 2 else 25
    frames = [short(f['name']) for f in data['shared']['frames']]
    profile = max(data['profiles'], key=lambda p: sum(p.get('weights', [])) if p['type'] == 'sampled' else len(p.get('events', [])))
    exclusive, inclusive = collections.Counter(), collections.Counter()
    total = 0.0
    if profile['type'] == 'sampled':
        for stack, weight in zip(profile['samples'], profile['weights']):
            total += weight
            names = [frames[i] for i in stack if frames[i] not in PSEUDO]
            if names:
                exclusive[names[-1]] += weight
            for name in {frames[i] for i in stack}:
                inclusive[name] += weight
    else:  # evented: open/close frame events
        open_at, stack = {}, []
        last = profile['startValue']
        for event in profile['events']:
            at = event['at']
            names = [frames[i] for i in stack if frames[i] not in PSEUDO]
            if stack and frames[stack[-1]] != 'UNMANAGED_CODE_TIME' or names:
                exclusive[names[-1] if names else frames[stack[-1]]] += at - last
                total += at - last
            last = at
            if event['type'] == 'O':
                stack.append(event['frame'])
                open_at.setdefault(event['frame'], []).append(at)
            else:
                stack.pop()
                start = open_at[event['frame']].pop()
                if event['frame'] not in stack:
                    inclusive[frames[event['frame']]] += at - start
    print(f'{"exclusive":>9} {"inclusive":>9}  method')
    for name, value in exclusive.most_common(top):
        print(f'{100 * value / total:8.1f}% {100 * inclusive[name] / total:8.1f}%  {name}')
    print('\nTop inclusive (codec frames):')
    shown = 0
    for name, value in inclusive.most_common():
        if 'Jxl' in name and shown < top:
            print(f'{100 * value / total:8.1f}%  {name}')
            shown += 1


if __name__ == '__main__':
    main()
