#!/usr/bin/env python3
"""Generate public, synthetic JPEG XL fixtures and their jxlinfo and djxl oracle (no external images).

Requires Pillow and the libjxl v0.12.0 tools (cjxl, djxl, jxlinfo) on PATH or in JXL_TOOLS. The outputs are
committed, so tests and CI do not need the tools. Lossy fixtures also get djxl's 8-bit rendering in djxl/, which
tests compare within a tolerance. Run: python3 tests/assets/generate-corpus.py
"""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import tempfile
import zlib

from PIL import Image, ImageCms

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / 'tests/assets/corpus'
TOOLS = os.environ.get('JXL_TOOLS')


def tool(name):
    path = Path(TOOLS) / name if TOOLS else shutil.which(name)
    if not path:
        raise SystemExit(f'{name} not found; set JXL_TOOLS')
    return str(path)


def run(*args):
    return subprocess.run(args, check=True, capture_output=True, text=True).stdout


def pixel(x, y, width, height):
    return [x * 255 // max(width - 1, 1), y * 255 // max(height - 1, 1), (x ^ y) * 8 % 256]


def png(path, width, height, depth=8, alpha=False, gray=False):
    def chunk(kind, payload):
        return struct.pack('>I', len(payload)) + kind + payload + struct.pack('>I', zlib.crc32(kind + payload))
    data = bytearray()
    for y in range(height):
        data.append(0)
        for x in range(width):
            channels = pixel(x, y, width, height)
            if gray:
                channels = [sum(channels) // 3]
            if alpha:
                channels.append((x + y) * 255 // max(width + height - 2, 1))
            for value in channels:
                data.extend(struct.pack('>H', value * 257) if depth == 16 else bytes([value]))
    color_type = (4 if alpha else 0) if gray else (6 if alpha else 2)
    path.write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, depth, color_type, 0, 0, 0))
                     + chunk(b'IDAT', zlib.compress(bytes(data))) + chunk(b'IEND', b''))


def pfm(path, width, height):
    rows = [struct.pack('<3f', *(v / 255 for v in pixel(x, y, width, height))) for y in reversed(range(height)) for x in range(width)]
    path.write_bytes(f'PF\n{width} {height}\n-1.0\n'.encode() + b''.join(rows))


def jpeg(path, width, height, orientation):
    image = Image.new('RGB', (width, height))
    image.putdata([tuple(pixel(x, y, width, height)) for y in range(height) for x in range(width)])
    exif = Image.Exif()
    exif[0x0112] = orientation
    image.save(path, exif=exif.tobytes(), quality=95)


def animation(path):
    frames = [Image.new('RGBA', (16, 16), (i * 60, 40, 200 - i * 50, 128 + i * 40)) for i in range(3)]
    frames[0].save(path, save_all=True, append_images=frames[1:], duration=100, loop=0)


def boxes(data):
    offset = 12
    while offset < len(data):
        size, kind = struct.unpack_from('>I4s', data, offset)
        yield kind, data[offset:offset + size]
        offset += size


def swap_partial_codestreams(data):
    """Moves the last jxlp box before the first one: valid only with ftyp minor version 1."""
    items = list(boxes(data))
    parts = [i for i, (kind, _) in enumerate(items) if kind == b'jxlp']
    assert len(parts) >= 2, 'expected several jxlp boxes'
    first, last = parts[0], parts[-1]
    items[first], items[last] = items[last], items[first]
    return data[:12] + b''.join(box for _, box in items)


def few_colors(path, width, height):
    image = Image.new('RGB', (width, height))
    palette = [(0, 0, 0), (255, 255, 255), (200, 30, 30), (30, 160, 60), (40, 60, 220), (240, 200, 0)]
    image.putdata([palette[(x // 5 + y // 3) % len(palette)] for y in range(height) for x in range(width)])
    image.save(path)


def text(path, width, height):
    """Repeated glyphs on a plain background: content for which cjxl codes patches from a reference frame."""
    from PIL import ImageDraw
    image = Image.new('RGB', (width, height), (250, 250, 245))
    draw = ImageDraw.Draw(image)
    for row in range(height // 12):
        draw.text((2, row * 12), 'JPEG XL patches 0123 ' * 4, fill=(20, 20, 60))
    image.save(path)


def pixels(path, temp, reference=None):
    """SHA-256 of the RGBA pixels djxl renders at 8 bits (orientation applied), or None if djxl fails."""
    png = Path(temp) / 'pixels.png'
    result = subprocess.run([tool('djxl'), str(path), str(png), '--bits_per_sample=8', '--quiet'], capture_output=True)
    if result.returncode != 0:
        return None
    rendered = Image.open(png).convert('RGBA')
    if reference is not None:
        reference.parent.mkdir(exist_ok=True)
        rendered.save(reference, optimize=True)
    return hashlib.sha256(rendered.tobytes()).hexdigest()


def info(path):
    text = run(tool('jxlinfo'), '-v', str(path))
    head = re.search(r'JPEG XL (image|animation), (\d+)x(\d+), .*?(\d+)-bit (float \()?.*?(RGB|Grayscale)(\+Alpha)?', text)
    field = lambda name: (re.search(rf'^{name}: (.+)$', text, re.M) or [None, None])[1]
    return {
        'width': int(head[2]), 'height': int(head[3]), 'bits': int(head[4]), 'float': head[5] is not None,
        'alpha': head[7] is not None, 'animated': head[1] == 'animation',
        'extraChannels': int(field('Number of extra channels') or 0),
        'orientation': int(field('Orientation').split()[0]),
        'colorSpace': field('Color space'), 'whitePoint': field('White point'), 'primaries': field('Primaries'),
        'transferFunction': field('Transfer function'), 'renderingIntent': field('Rendering intent'),
        'icc': re.search(r'^\d+-byte ICC profile', text, re.M) is not None,
        'intensityTarget': float((field('Intensity target') or '255').split()[0]),
        'boxes': re.findall(r'^Box:\n  type: "(....)"', text, re.M),
    }


def main():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    for old in [*OUTPUT.glob('*.jxl'), *OUTPUT.glob('djxl/*.png')]:
        old.unlink()
    cjxl, djxl = tool('cjxl'), tool('djxl')
    with tempfile.TemporaryDirectory() as temp:
        tmp = Path(temp)
        png(tmp / 'rgb.png', 40, 24)
        png(tmp / 'rgba.png', 40, 24, alpha=True)
        png(tmp / 'gray.png', 40, 24, gray=True)
        png(tmp / 'rgb16.png', 40, 24, depth=16)
        png(tmp / 'gray16a.png', 33, 17, depth=16, gray=True, alpha=True)
        png(tmp / 'small.png', 64, 64)
        png(tmp / 'ratio.png', 160, 90)
        png(tmp / 'tiny.png', 1, 1)
        png(tmp / 'wide.png', 1025, 3)
        png(tmp / 'groups.png', 300, 200)
        png(tmp / 'lf-groups.png', 1100, 40, alpha=True)
        few_colors(tmp / 'few.png', 40, 24)
        png(tmp / 'photo.png', 64, 48)
        png(tmp / 'photo-alpha.png', 64, 48, alpha=True)
        png(tmp / 'photo-gray.png', 64, 48, gray=True)
        png(tmp / 'groups-lossy.png', 300, 200)
        text(tmp / 'text.png', 200, 96)
        Image.open(tmp / 'photo.png').save(tmp / 'q90-420.jpg', quality=90, subsampling=2)
        Image.open(tmp / 'photo.png').save(tmp / 'q90-444.jpg', quality=90, subsampling=0)
        pfm(tmp / 'float.pfm', 20, 12)
        animation(tmp / 'anim.png')
        # Lossless encoding keeps an embedded profile as ICC instead of an enumerated colour encoding.
        srgb = ImageCms.ImageCmsProfile(ImageCms.createProfile('sRGB')).tobytes()
        Image.open(tmp / 'rgb.png').save(tmp / 'icc.png', icc_profile=srgb)
        for orientation in range(1, 9):
            jpeg(tmp / f'orient{orientation}.jpg', 40, 24, orientation)

        cases = {
            'rgb8-lossless': ('rgb.png', '-d', '0'),
            'rgb8-lossy': ('rgb.png', '-d', '1'),
            'rgba8-lossless': ('rgba.png', '-d', '0'),
            'gray8-lossless': ('gray.png', '-d', '0'),
            'rgb16-lossless': ('rgb16.png', '-d', '0'),
            'gray16-alpha': ('gray16a.png', '-d', '0'),
            'float32': ('float.pfm', '-d', '0'),
            'small-64x64': ('small.png', '-d', '0'),
            'ratio-160x90': ('ratio.png', '-d', '0'),
            'tiny-1x1': ('tiny.png', '-d', '0'),
            'wide-1025x3': ('wide.png', '-d', '0'),
            'pq-bt2100': ('rgb.png', '-d', '0', '-x', 'color_space=RGB_D65_202_Rel_PeQ'),
            'p3-perceptual': ('rgb.png', '-d', '0', '-x', 'color_space=RGB_D65_DCI_Per_SRG'),
            'gamma-custom': ('rgb.png', '-d', '0', '-x', 'color_space=RGB_D65_SRG_Rel_g0.45455'),
            'animation': ('anim.png', '-d', '0'),
            'container-jxlc': ('rgb.png', '-d', '0', '--container=1'),
            'container-exif': ('orient1.jpg', '-j', '0', '-d', '0', '--compress_boxes=0'),
            'icc-profile': ('icc.png', '-d', '0'),
            'jxlp-v1': ('rgb.png', '-d', '1', '--output_mode=2'),
            # Modular coding tools: palettes, squeeze, passes, effort extremes, predictors, groups, LF groups.
            'palette': ('few.png', '-d', '0'),
            'delta-palette': ('rgb.png', '-d', '0', '-m', '1', '--modular_lossy_palette', '--modular_palette_colors=0'),
            'squeeze': ('rgba.png', '-d', '0', '-R', '1'),
            'progressive': ('rgb.png', '-d', '0', '-p'),
            'effort1': ('rgba.png', '-d', '0', '-e', '1'),
            'effort9': ('rgb.png', '-d', '0', '-e', '9'),
            'weighted': ('rgb.png', '-d', '0', '-P', '6'),
            'groups-300x200': ('groups.png', '-d', '0', '-g', '0'),
            'lf-groups': ('lf-groups.png', '-d', '0', '-g', '0', '-R', '1'),
        }
        cases.update({f'orient{o}': (f'orient{o}.jpg', '-j', '0', '-d', '0') for o in range(1, 9)})
        # Lossy (VarDCT, XYB) coding tools: rendered by djxl into djxl/*.png and compared within a tolerance.
        lossy = {
            'lossy-d0.5': ('photo.png', '-d', '0.5'),
            'lossy-d4-e9': ('photo.png', '-d', '4', '-e', '9'),
            'lossy-e1': ('photo.png', '-d', '1', '-e', '1'),
            'lossy-container': ('photo.png', '-d', '2', '--container=1'),
            'lossy-alpha': ('photo-alpha.png', '-d', '1'),
            'lossy-gray': ('photo-gray.png', '-d', '1'),
            'lossy-groups': ('groups-lossy.png', '-d', '2'),
            'lossy-progressive-dc': ('photo.png', '-d', '1', '-p'),
            'lossy-progressive-ac': ('photo.png', '-d', '1', '--progressive_ac'),
            'lossy-resampling': ('photo.png', '-d', '2', '--resampling=2'),
            'lossy-epf3': ('photo.png', '-d', '3', '--epf=3'),
            'lossy-no-filters': ('photo.png', '-d', '2', '--epf=0', '--gaborish=0'),
            'lossy-modular': ('photo.png', '-d', '1', '-m', '1'),
            'lossy-p3': ('photo.png', '-d', '1', '-x', 'color_space=RGB_D65_DCI_Rel_SRG'),
            'lossy-patches': ('text.png', '-d', '1'),
            'jpeg-420': ('q90-420.jpg', '--lossless_jpeg=1'),
            'jpeg-444': ('q90-444.jpg', '--lossless_jpeg=1'),
            # Valid but not rendered yet: photon noise.
            'lossy-noise': ('photo.png', '-d', '1', '--photon_noise_iso=3200'),
        }
        cases.update(lossy)
        for name, (source, *options) in cases.items():
            run(cjxl, str(tmp / source), str(OUTPUT / f'{name}.jxl'), *options)

        swapped = OUTPUT / 'jxlp-v1-out-of-order.jxl'
        swapped.write_bytes(swap_partial_codestreams((OUTPUT / 'jxlp-v1.jxl').read_bytes()))
        # The reordered file must decode to the same pixels as the original with libjxl.
        run(djxl, str(OUTPUT / 'jxlp-v1.jxl'), str(tmp / 'a.ppm'))
        run(djxl, str(swapped), str(tmp / 'b.ppm'))
        assert (tmp / 'a.ppm').read_bytes() == (tmp / 'b.ppm').read_bytes()

    with tempfile.TemporaryDirectory() as temp:
        oracle = {path.name: info(path) | {'rgba8Sha256': pixels(path, temp, OUTPUT / 'djxl' / f'{path.stem}.png' if path.stem in lossy else None)}
                  for path in sorted(OUTPUT.glob('*.jxl'))}
    (OUTPUT / 'corpus.json').write_text(json.dumps({'generator': run(tool('cjxl'), '--version').splitlines()[0], 'files': oracle}, indent=1) + '\n')
    print(f'{len(oracle)} files, {sum(p.stat().st_size for p in OUTPUT.glob("*.jxl"))} bytes')


if __name__ == '__main__':
    main()
