#!/usr/bin/env python3
"""Generate synthetic JPEG transcoding fixtures with Pillow 10.2+ and pinned cjxl/djxl.

Fixtures include JPEG bytes, cjxl's reconstructable JXL, and djxl's rendered PNG, so CI needs no native tools.
Run separately, or from generate-corpus.py. No external photos are used.
"""
from pathlib import Path
from io import BytesIO
from PIL import Image, ImageCms
import os
import shutil
import struct
import subprocess
import tempfile

OUTPUT = Path(__file__).resolve().parent / 'corpus' / 'transcoding'


def tool(name):
    return str(Path(os.environ['JXL_TOOLS']) / name) if 'JXL_TOOLS' in os.environ else shutil.which(name) or name


def marker(kind, payload):
    return bytes([255, kind]) + struct.pack('>H', len(payload) + 2) + payload


def image(width, height, gray=False):
    im = Image.new('RGB', (width, height))
    im.putdata([((x * 11 + y * 31) % 256, (x * 27 + y * 3) % 256, (x * 7 + y * 19) % 256)
                for y in range(height) for x in range(width)])
    return im.convert('L') if gray else im


def main():
    OUTPUT.mkdir(exist_ok=True)
    cases = []
    for progressive in (False, True):
        for sampling in (0, 1, 2):
            cases.append((f'color-{sampling}-{int(progressive)}', 35, 19, False,
                          dict(quality=87, subsampling=sampling, progressive=progressive)))
        cases.append((f'gray-{int(progressive)}', 35, 19, True, dict(quality=93, progressive=progressive)))
        cases.append((f'restart-{int(progressive)}', 67, 43, False,
                      dict(quality=91, subsampling=2, progressive=progressive, restart_marker_blocks=3)))
        cases.append((f'metadata-{int(progressive)}', 39, 21, False,
                      dict(quality=85, subsampling=1, progressive=progressive)))
    for name, w, h, gray, args in [
        ('tiny', 1, 1, False, dict(quality=100, subsampling=0)),
        ('mcu-15x17', 15, 17, False, dict(quality=75, subsampling=2)),
        ('groups-257x265', 257, 265, False, dict(quality=89, subsampling=1, progressive=True)),
        ('lf-groups-2051x9', 2051, 9, False, dict(quality=90, subsampling=2)),
        ('huffman-optimized', 91, 55, False, dict(quality=73, subsampling=0, optimize=True)),
    ]:
        cases.append((name, w, h, gray, args))
    icc = ImageCms.ImageCmsProfile(ImageCms.createProfile('sRGB')).tobytes()
    # Fix the profile timestamp so generated fixture bytes are reproducible.
    icc = icc[:24] + struct.pack('>6H', 2026, 1, 1, 0, 0, 0) + icc[36:]
    synthetic = []
    dqt = marker(0xdb, bytes([0]) + bytes([1]) * 64)
    dc = bytes([0, 1] + [0] * 15 + [0])
    ac = bytes([16, 0, 2] + [0] * 14 + [240, 0])
    sof = marker(0xc0, bytes([8, 0, 8, 0, 8, 1, 1, 17, 0]))
    sos = marker(0xda, bytes([1, 1, 0, 0, 63, 0]))
    # Redundant ZRL, zero padding, and one empty DHT marker.
    synthetic.append(('extra-zrl-padding', b'\xff\xd8' + dqt + sof + marker(0xc4, b'')
                      + marker(0xc4, dc + ac) + sos + b'\x08' + b'\xff\xd9'))
    ac = bytes([16, 0, 0, 4] + [0] * 13 + [0, 16, 32, 240])
    sof = marker(0xc2, bytes([8, 0, 8, 0, 40, 1, 1, 17, 0]))
    dcscan = marker(0xda, bytes([1, 1, 0, 0, 0, 0])) + b'\x00'
    acscan = marker(0xda, bytes([1, 1, 0, 1, 63, 0]))
    entropy = '011' + '000' + '0010' + '0010' + '00'
    synthetic.append(('progressive-reset-padding', b'\xff\xd8' + dqt + sof + marker(0xc4, dc + ac)
                      + dcscan + acscan + int(entropy, 2).to_bytes(2, 'big') + b'\xff\xd9'))
    # 16-bit DQT entries are legal in an 8-bit sequential extended JPEG.
    sof = marker(0xc1, bytes([8, 0, 8, 0, 8, 1, 1, 17, 0]))
    synthetic.append(('quant16', b'\xff\xd8' + marker(0xdb, bytes([16]) + b'\x01\x01' * 64) + sof
                      + marker(0xc4, dc + bytes([16, 1] + [0] * 15 + [0])) + sos + b'\x3f' + b'\xff\xd9'))
    for name, w, h, gray, args in cases:
        stream = BytesIO()
        im = image(w, h, gray)
        im.save(stream, format='JPEG', **args)
        data = stream.getvalue()
        if name.startswith('metadata'):
            exif = Image.Exif(); exif[0x0112] = 6; exif[0x010f] = 'Synthetic'
            # Two canonical ICC chunks, plus duplicate/unknown APP and COM, marker fill bytes and an EOI tail.
            middle = len(icc) // 2
            data = data[:2] + marker(0xe2, b'ICC_PROFILE\0\1\2' + icc[:middle]) \
                + marker(0xe2, b'ICC_PROFILE\0\2\2' + icc[middle:]) \
                + marker(0xe1, exif.tobytes()) \
                + marker(0xe1, b'http://ns.adobe.com/xap/1.0/\0<xmp>synthetic</xmp>') \
                + marker(0xe3, b'unknown\0payload') + marker(0xe3, b'duplicate') \
                + marker(0xfe, b'synthetic comment') + b'\xff\xff' + data[2:] + b'after EOI\0\xff'
        synthetic.append((name, data))
    for name, data in synthetic:
        path = OUTPUT / (name + '.jpg'); path.write_bytes(data)
        reference = OUTPUT / (name + '.jxl')
        subprocess.run([tool('cjxl'), str(path), str(reference), '-e', '7', '--num_threads=1', '--lossless_jpeg=1', '--quiet'], check=True)
        subprocess.run([tool('djxl'), str(reference), str(OUTPUT / (name + '.png')), '--bits_per_sample=8', '--quiet'], check=True)
        with tempfile.TemporaryDirectory() as tmp:
            restored = Path(tmp) / 'restored.jpg'
            subprocess.run([tool('djxl'), str(reference), str(restored), '--quiet'], check=True)
            assert restored.read_bytes() == data, name
    print(f'{len(synthetic)} synthetic transcoding cases')


if __name__ == '__main__':
    main()
