#!/usr/bin/env python3
"""Generate public, synthetic JPEG XL fixtures and their jxlinfo and djxl oracle (no external images).

Requires Pillow, NumPy and the libjxl v0.12.0 tools (cjxl, djxl, jxlinfo, jxl_from_tree, jxl_anim_gen,
jxl_dec_straight) on PATH or in JXL_TOOLS; jxl_from_tree is a libjxl dev tool, built from the pinned sources
(-DJPEGXL_ENABLE_DEVTOOLS=ON), and jxl_anim_gen and jxl_dec_straight are built from tests/assets/tools (see its README).
Images with premultiplied alpha take their oracle from jxl_dec_straight (djxl cannot unpremultiply). Animations also get the SHA-256 of every frame djxl
shows (and, when lossy, its 8-bit rendering in djxl/<name>-<frame>.png). The outputs are
committed, so tests and CI do not need the tools. Lossy fixtures also get djxl's 8-bit rendering in djxl/, which
tests compare within a tolerance. XYB images with an ICC profile get cms/: djxl's linear sRGB floats converted to
the profile in double precision (djxl itself would need a colour management system). XYB images outside sRGB get
srgb/: djxl's rendering for --color_space=sRGB (gray: the sRGB curve), which ColorConversion = ToSrgb matches.
Run: python3 tests/assets/generate-corpus.py
"""
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import tempfile
import zlib

import numpy as np
from PIL import Image, ImageCms, PngImagePlugin

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


def s15(value):
    return struct.pack('>i', round(value * 65536))


def bradford_to_d50(white):
    bradford = np.array([[0.8951, 0.2664, -0.1614], [-0.7502, 1.7135, 0.0367], [0.0389, -0.0685, 1.0296]])
    w = np.array([white[0] / white[1], 1, (1 - white[0] - white[1]) / white[1]])
    return np.linalg.inv(bradford) @ np.diag((bradford @ np.array([0.9642, 1, 0.8249])) / (bradford @ w)) @ bradford


def primaries_to_xyz(red, green, blue, white):
    p = np.array([[red[0], green[0], blue[0]], [red[1], green[1], blue[1]],
                  [1 - red[0] - red[1], 1 - green[0] - green[1], 1 - blue[0] - blue[1]]])
    return p * np.linalg.solve(p, np.array([white[0] / white[1], 1, (1 - white[0] - white[1]) / white[1]]))


D65 = (0.3127, 0.3290)
# Wide-gamut primaries (Adobe RGB-like) and a gamma-2.2-like tone curve with a wiggle, so that cjxl cannot map the
# profile to an enumerated colour encoding and keeps the ICC profile with XYB.
TEST_COLORANTS = bradford_to_d50(D65) @ primaries_to_xyz((0.64, 0.33), (0.21, 0.71), (0.15, 0.06), D65)
TEST_TRC = [min(1.0, (i / 1023) ** 2.2 * (1 + 0.04 * math.sin(math.pi * i / 1023))) for i in range(1024)]


def icc_profile(gray):
    """A version 2 matrix/TRC (or gray TRC) display profile with a fixed date, written tag by tag (ICC.1:2001-04)."""
    xyz = lambda v: b'XYZ \0\0\0\0' + b''.join(s15(c) for c in v)
    curve = b'curv\0\0\0\0' + struct.pack('>I', len(TEST_TRC)) + b''.join(struct.pack('>H', round(v * 65535)) for v in TEST_TRC)
    name = b'PictureSharp test\0'
    tags = [(b'desc', b'desc\0\0\0\0' + struct.pack('>I', len(name)) + name + b'\0' * 75), (b'cprt', b'text\0\0\0\0CC0\0'),
            (b'wtpt', xyz([0.9642, 1.0, 0.8249]))]
    if gray:
        tags.append((b'kTRC', curve))
    else:
        tags += [(b'rXYZ', xyz(TEST_COLORANTS[:, 0])), (b'gXYZ', xyz(TEST_COLORANTS[:, 1])), (b'bXYZ', xyz(TEST_COLORANTS[:, 2])),
                 (b'rTRC', curve), (b'gTRC', curve), (b'bTRC', curve)]
    start = 128 + 4 + 12 * len(tags)
    table, data, placed = b'', b'', {}
    for signature, body in tags:
        if body not in placed:
            data += b'\0' * (-(start + len(data)) % 4)
            placed[body] = (start + len(data), len(body))
            data += body
        table += signature + struct.pack('>II', *placed[body])
    header = struct.pack('>I4sI4s4s4s6H4s', start + len(data), b'\0' * 4, 0x02100000, b'mntr', b'GRAY' if gray else b'RGB ',
                         b'XYZ ', 2026, 1, 1, 0, 0, 0, b'acsp')
    header += b'\0' * 28 + s15(0.9642) + s15(1.0) + s15(0.8249) + b'\0' * 48
    assert len(header) == 128
    return header + struct.pack('>I', len(tags)) + table + data


def pfm_read(path):
    with open(path, 'rb') as f:
        kind = f.readline().strip()
        width, height = map(int, f.readline().split())
        scale = float(f.readline())
        data = np.frombuffer(f.read(), dtype='<f4' if scale < 0 else '>f4')
    return data.reshape(height, width, 3 if kind == b'PF' else 1)[::-1].astype(np.float64)


def cms_reference(path, temp, reference, gray):
    """djxl's linear sRGB output converted to the test profile: XYZ D50, the colourant inverse, the inverse TRC."""
    pfm = Path(temp) / 'linear.pfm'
    run(tool('djxl'), str(path), str(pfm), '--quiet')
    linear = pfm_read(pfm)
    if gray:
        device = linear[..., :1] if linear.shape[2] == 1 else linear @ np.array([[0.2126], [0.7152], [0.0722]])
    else:
        srgb_to_d50 = bradford_to_d50(D65) @ primaries_to_xyz((0.64, 0.33), (0.30, 0.60), (0.15, 0.06), D65)
        device = linear @ (np.linalg.inv(np.array(TEST_COLORANTS)) @ srgb_to_d50).T
    table = np.array([round(v * 65535) / 65535 for v in TEST_TRC])
    encoded = np.interp(np.clip(device, 0, 1), table, np.linspace(0, 1, len(table)))
    samples = np.clip(np.round(encoded * 255), 0, 255).astype(np.uint8)
    reference.parent.mkdir(exist_ok=True)
    Image.fromarray(samples[..., 0] if gray else samples, 'L' if gray else 'RGB').save(reference, optimize=True)


def srgb_reference(path, reference, gray):
    """djxl's 8-bit rendering of an XYB image straight to sRGB, without a colour management system."""
    reference.parent.mkdir(exist_ok=True)
    space = 'Gra_D65_Rel_SRG' if gray else 'RGB_D65_SRG_Rel_SRG'
    run(tool('djxl'), str(path), str(reference), '--bits_per_sample=8', '--quiet', f'--color_space={space}')
    Image.open(reference).save(reference, optimize=True)


def metadata_png(path):
    """A PNG with an eXIf chunk (orientation 6) and XMP, which cjxl stores as Exif and xml boxes."""
    image = Image.new('RGB', (40, 24))
    image.putdata([tuple(pixel(x, y, 40, 24)) for y in range(24) for x in range(40)])
    exif = Image.Exif()
    exif[0x0112] = 6
    exif[0x010F] = 'PictureSharp'
    info = PngImagePlugin.PngInfo()
    info.add_itxt('XML:com.adobe.xmp', '<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
                  '<rdf:Description xmlns:dc="http://purl.org/dc/elements/1.1/" dc:format="image/jxl"/></rdf:RDF></x:xmpmeta>')
    image.save(path, exif=exif.tobytes(), pnginfo=info)


def animation(path):
    frames = [Image.new('RGBA', (16, 16), (i * 60, 40, 200 - i * 50, 128 + i * 40)) for i in range(3)]
    frames[0].save(path, save_all=True, append_images=frames[1:], duration=100, loop=0)


def photo_animation(path, mode='RGB'):
    """Three photo-like frames of a moving gradient (lossy cases)."""
    frames = []
    for i in range(3):
        image = Image.new('RGB', (48, 32))
        image.putdata([tuple((c + 37 * i + (x * y) % 23) % 256 for c in pixel(x, y, 48, 32)) for y in range(32) for x in range(48)])
        frames.append(image.convert(mode))
    frames[0].save(path, save_all=True, append_images=frames[1:], duration=[80, 120, 40], loop=0)


def sprite_frames(width=32, height=24):
    """A square moving over a transparent canvas with a fixed bar: partial changes for cropped, blended frames."""
    frames = []
    for i in range(4):
        image = Image.new('RGBA', (width, height), (0, 0, 0, 0))
        image.paste((30, 160, 90, 255), (0, height - 4, width, height))
        image.paste((200, 40 + 40 * i, 60, 160 + 30 * i), (3 + 6 * i, 2 + 3 * i, 13 + 6 * i, 12 + 3 * i))
        frames.append(image)
    return frames


def sprite_apng(path):
    frames = sprite_frames()
    frames[0].save(path, save_all=True, append_images=frames[1:], duration=[60, 70, 80, 90], loop=0,
                   disposal=[0, 1, 2, 0], blend=[0, 1, 1, 0])


def sprite_gif(path):
    frames = []
    for source in sprite_frames():
        # 15 colours and a full 16-entry palette whose last index is transparent.
        frame = source.convert('RGB').quantize(15)
        palette = frame.getpalette()[:45]
        frame.putpalette(palette + [0] * (48 - len(palette)))
        frame.putdata([15 if a == 0 else i for i, (_, _, _, a) in zip(frame.getdata(), source.getdata())])
        frames.append(frame)
    frames[0].save(path, save_all=True, append_images=frames[1:], duration=[50, 100, 70, 30], loop=2, disposal=2,
                   transparency=15, optimize=False)


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


def premultiplied(path):
    """Whether the first alpha channel is premultiplied (associated): djxl would write premultiplied colour."""
    match = re.search(r'type: Alpha\n(?:  .*\n)*?  alpha premultiplied: (\d)', run(tool('jxlinfo'), '-v', str(path)))
    return match is not None and match[1] == '1'


def pam_read(path):
    data = path.read_bytes()
    end = data.index(b'ENDHDR\n') + 7
    header = dict(line.split(b' ', 1) for line in data[:end].split(b'\n')[1:-2])
    width, height, depth = int(header[b'WIDTH']), int(header[b'HEIGHT']), int(header[b'DEPTH'])
    samples = np.frombuffer(data[end:], np.uint8).reshape(height, width, depth)
    return Image.fromarray(samples[:, :, 0] if depth == 1 else samples, {1: 'L', 2: 'LA', 3: 'RGB', 4: 'RGBA'}[depth])


def straight(path, temp):
    """The 8-bit frames jxl_dec_straight renders like djxl, with premultiplied alpha made straight, or None."""
    prefix = Path(temp) / 'straight'
    for old in Path(temp).glob('straight-*.pam'):
        old.unlink()
    if subprocess.run([tool('jxl_dec_straight'), str(path), str(prefix)], capture_output=True).returncode != 0:
        return None
    return [pam_read(Path(f'{prefix}-{index}.pam')) for index in range(len(list(Path(temp).glob('straight-*.pam'))))]


def pixels(path, temp, reference=None):
    """SHA-256 of the RGBA pixels djxl renders at 8 bits (orientation applied), or None if djxl fails."""
    png = Path(temp) / 'pixels.png'
    if premultiplied(path):
        frames = straight(path, temp)
        if frames is None:
            return None
        rendered = frames[-1].convert('RGBA')
    else:
        result = subprocess.run([tool('djxl'), str(path), str(png), '--bits_per_sample=8', '--quiet'], capture_output=True)
        if result.returncode != 0:
            return None
        rendered = Image.open(png).convert('RGBA')
    if reference is not None:
        reference.parent.mkdir(exist_ok=True)
        rendered.save(reference, optimize=True)
    return hashlib.sha256(rendered.tobytes()).hexdigest()


def frames(path, temp, reference=None):
    """SHA-256 of the RGBA pixels of every frame djxl shows (coalesced, 8 bits); lossy files also keep the frames.
    djxl writes them as full APNG frames (its PAM output ignores --bits_per_sample)."""
    png = Path(temp) / 'frames.png'
    hashes = []

    def add(index, rendered):
        if reference is not None:
            reference.mkdir(exist_ok=True)
            rendered.save(reference / f'{path.stem}-{index}.png', optimize=True)
        hashes.append(hashlib.sha256(rendered.tobytes()).hexdigest())

    if premultiplied(path):
        for index, rendered in enumerate(straight(path, temp)):
            add(index, rendered.convert('RGBA'))
        return hashes
    run(tool('djxl'), str(path), str(png), '--bits_per_sample=8', '--quiet')
    with Image.open(png) as animation:
        for index in range(getattr(animation, 'n_frames', 1)):
            animation.seek(index)
            add(index, animation.convert('RGBA'))
    return hashes


def animation_info(text):
    """Ticks, loops and the durations of the frames libjxl shows: every frame with a duration, and the last one."""
    ticks = re.search(r'^Ticks per second \(numerator / denominator\): (\d+) / (\d+)$', text, re.M)
    durations = [float(ms) for ms in re.findall(r'^(?:Frame|Layer): .*?duration: ([0-9.]+) ms', text, re.M)]
    shown = [ms for i, ms in enumerate(durations) if ms > 0 or i == len(durations) - 1]
    return {'ticks': [int(ticks[1]), int(ticks[2])], 'loops': int(re.search(r'^Num loops: (\d+)$', text, re.M)[1]),
            'durationsMs': shown}


def info(path):
    text = run(tool('jxlinfo'), '-v', str(path))
    head = re.search(r'JPEG XL (image|animation), (\d+)x(\d+), .*?(\d+)-bit (float \()?.*?(RGB|Grayscale|CMY)(\+Alpha)?', text)
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
    } | (animation_info(text) if head[1] == 'animation' else {})


# Layered stills from jxl_from_tree (0.8.10): every frame but the last is saved to slot 1 and the next one blends onto
# it; the image has the first frame's size. The tool cannot give several frames an alpha channel.
BASE = 'Width 64\nHeight 48\nBitdepth {bits}\n{header}NotLast\nif c > 0\n  - Set 100\n  - Gradient +3\n'


def layer(width, height, x, y, mode, last=True, xyb=False):
    return (f'{"XYB" + chr(10) if xyb else ""}Width {width}\nHeight {height}\nFramePos {x} {y}\nBlendMode {mode}\n'
            f'{"" if last else "NotLast" + chr(10)}if c > 1\n  - Set 60\n  - W +7\n')


LAYERS = {
    'layers-replace-crop': BASE.format(bits=8, header='') + layer(40, 70, 30, -10, 'kReplace'),
    'layers-add-negative': BASE.format(bits=8, header='') + layer(24, 20, -6, 30, 'kAdd'),
    'layers-mul': BASE.format(bits=8, header='') + layer(40, 30, 10, 10, 'kMul'),
    'layers-blend-no-alpha': BASE.format(bits=8, header='') + layer(40, 30, 10, 10, 'kBlend'),
    'layers-alpha-weighted-add': BASE.format(bits=8, header='') + layer(40, 30, 10, 10, 'kAlphaWeightedAdd'),
    'layers-outside': BASE.format(bits=8, header='') + layer(8, 8, 100, 100, 'kAdd'),
    'layers-larger': BASE.format(bits=8, header='') + layer(100, 80, -10, -12, 'kAdd'),
    'layers-three': BASE.format(bits=8, header='') + layer(30, 20, 5, 5, 'kAdd', last=False) + layer(20, 40, 30, 4, 'kMul'),
    'layers-16bit': BASE.format(bits=16, header='') + layer(40, 30, 10, 10, 'kAdd'),
    'layers-xyb': BASE.format(bits=8, header='XYB\n') + layer(24, 20, 6, 10, 'kAdd', xyb=True),
    # 0.8.11: layers of oriented images are oriented and their offsets mapped onto the oriented canvas.
    'layers-orient6': BASE.format(bits=8, header='Orientation 6\n') + layer(30, 20, 5, 5, 'kAdd', last=False)
    + layer(20, 40, 30, -4, 'kReplace'),
    'layers-orient3': BASE.format(bits=8, header='Orientation 3\n') + layer(24, 20, -6, 30, 'kAdd'),
}


# 0.8.15: noise and splines from jxl_from_tree (Spline: 3 colour DCTs, a sigma DCT and the control points; Noise: the
# 8-entry LUT), rendered by djxl into djxl/*.png. noise-layers adds noise to frames that are not shown, which libjxl seeds
# with nonvisible_frame_index.
def spline(colors, sigma, points):
    dct = lambda values: ' '.join(str(v) for v in (list(values) + [0] * 32)[:32])
    return '\n'.join(['Spline', *[dct(c) for c in colors], dct(sigma), ' '.join(str(p) for p in points), 'EndSpline']) + '\n'


SPLINES = (spline([(0.5, 0.1, -0.2), (0.3, 0.05), (0.2,)], (2, 0.5), [5, 10, 60, 80, 133, 20, 90, -3])
           + spline([(-0.4,), (0.6,), (0.1,)], (1.2,), [5, 90, 40, 40]))
NOISE = 'Noise 0.1 0.2 0.3 0.35 0.4 0.45 0.5 0.6\n'
FEATURES = {
    'splines-xyb': 'Width 140\nHeight 100\nXYB\n' + SPLINES + '- Set 0\n',
    'splines-rgb': 'Width 140\nHeight 100\n' + SPLINES + 'if c > 0\n  - Set 100\n  - Gradient +3\n',
    'splines-up2': 'Width 141\nHeight 101\nXYB\nUpsample 2\n' + SPLINES + '- Set 0\n',
    'splines-noise': 'Width 140\nHeight 100\nXYB\nGaborish\nEPF 2\nSplineQuantizationAdjustment 3\n' + SPLINES + NOISE + '- Set 0\n',
    'noise-up4': 'Width 37\nHeight 59\nXYB\nUpsample 4\n' + NOISE + '- Set 0\n',
    'noise-groups': 'Width 300\nHeight 200\nXYB\nGroupShift 0\n' + NOISE + '- Set 0\n',
    'noise-layers': BASE.format(bits=8, header=NOISE) + layer(30, 20, 5, 5, 'kAdd', last=False) + layer(20, 40, 30, 4, 'kMul'),
}
LAYERS.update(FEATURES)
LOSSY_LAYERS = {'layers-xyb', *FEATURES}

# Animations cjxl cannot write, from jxl_anim_gen (0.8.12; spec format in tests/assets/tools/jxl_anim_gen.cc). The
# encoder keeps slot 3 for patches, so the specs use slots 0-2.
ANIMATIONS = {
    # NTSC ticks, loops, timecodes, names, internal frames of duration 0, every blend mode, crops beyond the canvas.
    'anim-ticks-loops': """size 40 24
alpha
tps 30000 1001
loops 3
timecodes
frame duration=0 save=1 name=base
frame duration=5 x0=-3 y0=4 w=16 h=10 blend=blend source=1 save=1 opacity=100 name=over timecode=7
frame duration=0 x0=10 y0=-2 w=12 h=12 blend=add source=1 save=2
frame duration=1001 x0=20 y0=10 w=30 h=20 blend=muladd source=2 save=0 opacity=200 timecode=9
frame duration=0 x0=1 y0=1 w=6 h=6 blend=mul source=0
""",
    # A shown frame with save_as_reference 0 is not saved: the last frame blends onto the first one (CanBeReferenced).
    'anim-unsaved': """size 24 16
alpha
frame duration=0 save=0 seed=1
frame duration=100 save=0 seed=2
frame duration=100 x0=4 y0=2 w=8 h=8 blend=blend source=0 seed=3 opacity=128
""",
    # One shown frame: an animation header, but a still result (LoopCount 0, not animated).
    'anim-single': """size 20 12
loops 5
frame duration=0 save=1
frame duration=0 x0=5 y0=3 w=8 h=6 blend=add source=1
""",
    # Ticks of 1/100 s, the largest duration, 16-bit samples with alpha.
    'anim-16bit': """size 32 20
bits 16
alpha
tps 100 1
frame duration=4294967295 save=1
frame duration=7 x0=6 y0=4 w=12 h=10 blend=blend source=1 save=1 opacity=90
frame duration=3 x0=-4 y0=8 w=16 h=16 blend=add source=1
""",
    # Lossy (VarDCT) frames blended with alpha; the first is shown without being saved (the still-image path per frame),
    # the others are blended canvases.
    'anim-vardct-alpha': """size 48 32
alpha
distance 1
frame duration=40 save=0
frame duration=40 save=1 seed=1
frame duration=40 x0=8 y0=8 w=24 h=16 blend=blend source=1 save=1 opacity=140
frame duration=40 x0=20 y0=4 w=24 h=24 blend=blend source=1 opacity=60
""",
    # 0.8.13 frame reader: 120 shown frames, each blended onto the previous canvas in slot 1, a few internal frames of
    # duration 0 in between; a reader needs MaxPixels of one canvas, Image.Load of all of them.
    'anim-long': 'size 16 12\nalpha\nloops 2\nframe duration=10 save=1 seed=0\n' + ''.join(
        f'frame duration={0 if i % 10 == 5 else 10} x0={i * 3 % 13 - 2} y0={i * 5 % 9 - 1} w=6 h=5 blend=blend source=1 save=1 '
        f'seed={i} opacity={40 + i * 7 % 200}\n' for i in range(1, 132)),
    # Orientation 6: every frame is oriented like a still.
    'anim-orient6': """size 24 16
orientation 6
frame duration=30 save=1
frame duration=30 x0=14 y0=2 w=8 h=12 blend=add source=1
""",
}

# 0.8.16: extra channels from jxl_anim_gen (`extra`, `alpha premultiplied`, `still` for layered stills). Premultiplied
# alpha is compared with jxl_dec_straight, spot colours (rendered) and the other types (dropped) with djxl.
SPOT = 'extra spot color=0.9,0.2,0.1,0.75\n'
# libjxl's encoder rejects the Unknown type ("output" fails), so it has no corpus file.
EXTRA_TYPES = ''.join(f'extra {kind}\n' for kind in ['depth', 'selection', 'thermal', 'optional'])
EXTRAS = {
    'premul-lossless': 'size 40 24\nstill\nalpha premultiplied\nframe ramp=1\n',
    'premul-16bit': 'size 33 17\nstill\nbits 16\nalpha premultiplied\nframe ramp=1\n',
    'premul-gray': 'size 40 24\nstill\ngray\nalpha premultiplied\nframe ramp=1\n',
    'premul-lossy': 'size 64 48\nstill\ndistance 1\nalpha premultiplied\nframe ramp=1\n',
    # Layers blended with Blend and AlphaWeightedAdd (muladd) onto premultiplied colour.
    'premul-layers': """size 48 32
still
alpha premultiplied
frame save=1 ramp=1
frame x0=8 y0=6 w=24 h=16 blend=blend source=1 save=1 opacity=140 seed=3
frame x0=20 y0=10 w=24 h=20 blend=muladd source=1 opacity=90 seed=5
""",
    'premul-anim': """size 32 24
alpha premultiplied
frame duration=50 save=1 ramp=1
frame duration=50 x0=6 y0=4 w=16 h=12 blend=blend source=1 save=1 opacity=120 seed=2
frame duration=50 x0=-4 y0=10 w=20 h=12 blend=muladd source=1 opacity=200 seed=4
""",
    'spot-rgb': 'size 40 24\nstill\n' + SPOT + 'frame\n',
    'spot-xyb': 'size 64 48\nstill\ndistance 1\n' + SPOT + 'frame\n',
    'spot-gray': 'size 40 24\nstill\ngray\n' + SPOT + 'frame\n',
    'spot-two': 'size 40 24\nstill\nalpha\nextra spot color=0.1,0.3,1,0.5 name=blue\nextra spot color=1,0.9,0,1\nframe ramp=1\n',
    'spot-upsampled': 'size 41 23\nstill\nec_resampling 2\n' + SPOT + 'frame\n',
    'spot-16bit': 'size 33 17\nstill\nbits 16\nalpha premultiplied\n' + SPOT + 'frame ramp=1\n',
    'spot-premul': 'size 40 24\nstill\nalpha premultiplied\n' + SPOT + 'frame ramp=1\n',
    # Blended layers: the ink is rendered on the blended canvas, the slot keeps the colour without it.
    'spot-layers': 'size 48 32\nstill\n' + SPOT + 'frame save=1\nframe x0=8 y0=6 w=24 h=16 blend=add source=1 seed=3\n',
    'spot-layers-xyb': 'size 48 32\nstill\ndistance 1\n' + SPOT + 'frame save=1\nframe x0=8 y0=6 w=24 h=16 blend=add source=1 seed=3\n',
    'spot-anim': """size 32 24
distance 1
extra spot color=0.9,0.2,0.1,0.75
frame duration=40 save=1
frame duration=40 x0=6 y0=4 w=16 h=12 blend=add source=1 save=1 seed=2
frame duration=40 seed=3
""",
    'extra-types': 'size 40 24\nstill\nalpha\n' + EXTRA_TYPES + 'frame ramp=1\n',
    'extra-types-lossy': 'size 64 48\nstill\ndistance 1\n' + EXTRA_TYPES + 'frame\n',
    'extra-alpha2': 'size 40 24\nstill\nalpha\nextra alpha premultiplied\nframe ramp=1\n',
    'extra-16bit': 'size 40 24\nstill\nextra depth bits=16\nframe\n',
    'extra-layers': 'size 48 32\nstill\n' + EXTRA_TYPES + 'frame save=1\nframe x0=8 y0=6 w=24 h=16 blend=add source=1 seed=3\n',
    # Still NotSupported: CMYK and float samples need PictureSharp core support.
    'extra-black': 'size 40 24\nstill\nextra black\nframe\n',
    'extra-float': 'size 40 24\nstill\nextra depth float\nframe\n',
}
ANIMATIONS.update(EXTRAS)
LOSSY_EXTRAS = {'premul-lossy', 'spot-xyb', 'spot-layers-xyb', 'extra-types-lossy'}
LOSSY_ANIMATIONS = {'anim-lossy', 'anim-gray', 'anim-vardct-alpha', 'anim-noise', 'spot-anim'}


def main():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    for old in [*OUTPUT.glob('*.jxl'), *OUTPUT.glob('djxl/*.png'), *OUTPUT.glob('cms/*.png'), *OUTPUT.glob('srgb/*.png')]:
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
        photo_animation(tmp / 'anim-photo.png')
        photo_animation(tmp / 'anim-gray.png', mode='L')
        sprite_apng(tmp / 'anim-sprite.png')
        sprite_gif(tmp / 'anim-sprite.gif')
        # Lossless encoding keeps an embedded profile as ICC instead of an enumerated colour encoding. The creation
        # date (bytes 24-35) is fixed so that regenerating the corpus reproduces the file.
        srgb = bytearray(ImageCms.ImageCmsProfile(ImageCms.createProfile('sRGB')).tobytes())
        srgb[24:36] = struct.pack('>6H', 2026, 1, 1, 0, 0, 0)
        srgb = bytes(srgb)
        Image.open(tmp / 'photo.png').save(tmp / 'photo-icc.png', icc_profile=icc_profile(gray=False))
        Image.open(tmp / 'photo-gray.png').save(tmp / 'gray-icc.png', icc_profile=icc_profile(gray=True))
        metadata_png(tmp / 'metadata.png')
        Image.open(tmp / 'rgb.png').save(tmp / 'icc.png', icc_profile=srgb)
        for orientation in range(1, 9):
            jpeg(tmp / f'orient{orientation}.jpg', 40, 24, orientation)

        cases = {
            'rgb8-lossless': ('rgb.png', '-d', '0'),
            'rgb8-lossy': ('rgb.png', '-d', '1'),
            'rgba8-lossless': ('rgba.png', '-d', '0'),
            'gray8-lossless': ('gray.png', '-d', '0'),
            'rgb16-lossless': ('rgb16.png', '-d', '0'),
            # Integer depths between 8 and 16 bits, reduced to 8 bits like djxl's 8-bit output.
            'rgb10-lossless': ('rgb16.png', '-d', '0', '--override_bitdepth=10'),
            'rgb12-lossless': ('rgb16.png', '-d', '0', '-e', '9', '--override_bitdepth=12'),
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
            # 0.8.12: APNG (ticks 1000/1) and GIF (100/1) through cjxl, cropped frames blending onto slot 1.
            'anim-lossy': ('anim-photo.png', '-d', '1'),
            'anim-gray': ('anim-gray.png', '-d', '1'),
            'anim-noise': ('anim-photo.png', '-d', '1', '--photon_noise_iso=6400'),
            'anim-crop': ('anim-sprite.png', '-d', '0'),
            'anim-gif': ('anim-sprite.gif', '-d', '0'),
            'container-jxlc': ('rgb.png', '-d', '0', '--container=1'),
            'container-exif': ('orient1.jpg', '-j', '0', '-d', '0', '--compress_boxes=0'),
            'icc-profile': ('icc.png', '-d', '0'),
            'metadata-brob': ('metadata.png', '-d', '0', '--compress_boxes=1'),
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
            'premul-cjxl-lossless': ('rgba.png', '-d', '0', '--premultiply=1'),
            'premul-cjxl-gray16': ('gray16a.png', '-d', '0', '--premultiply=1'),
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
            'lossy-16bit': ('rgb16.png', '-d', '1'),
            'lossy-patches': ('text.png', '-d', '1'),
            'jpeg-420': ('q90-420.jpg', '--lossless_jpeg=1'),
            'jpeg-444': ('q90-444.jpg', '--lossless_jpeg=1'),
            # 0.8.15: photon noise (cjxl --photon_noise_iso), also on alpha, gray, 16-bit, upsampled, Modular and groups.
            'lossy-noise': ('photo.png', '-d', '1', '--photon_noise_iso=3200'),
            'noise-iso100': ('photo.png', '-d', '1', '--photon_noise_iso=100'),
            'noise-iso51200': ('groups-lossy.png', '-d', '2', '--photon_noise_iso=51200'),
            'noise-alpha': ('photo-alpha.png', '-d', '1', '--photon_noise_iso=6400'),
            'noise-gray': ('photo-gray.png', '-d', '1', '--photon_noise_iso=6400'),
            'noise-16bit': ('rgb16.png', '-d', '1', '--photon_noise_iso=6400'),
            'noise-resampling': ('photo.png', '-d', '2', '--resampling=2', '--photon_noise_iso=6400'),
            'noise-modular': ('photo.png', '-d', '1', '-m', '1', '--photon_noise_iso=6400'),
            # 0.8.16: straight samples marked premultiplied (colour above alpha, colour where alpha is 0).
            'premul-cjxl-lossy': ('photo-alpha.png', '-d', '1', '--premultiply=1'),
        }
        cases.update(lossy)
        # XYB with an ICC profile: rendered in the profile's colour space and compared with cms/*.png.
        managed = {'icc-lossy-rgb': ('photo-icc.png', '-d', '1'), 'icc-lossy-gray': ('gray-icc.png', '-d', '1')}
        cases.update(managed)
        for name, (source, *options) in cases.items():
            run(cjxl, str(tmp / source), str(OUTPUT / f'{name}.jxl'), *options)

        for name, tree in LAYERS.items():
            (tmp / f'{name}.txt').write_text(tree)
            run(tool('jxl_from_tree'), str(tmp / f'{name}.txt'), str(OUTPUT / f'{name}.jxl'))

        for name, spec in ANIMATIONS.items():
            (tmp / f'{name}.txt').write_text(spec)
            run(tool('jxl_anim_gen'), str(tmp / f'{name}.txt'), str(OUTPUT / f'{name}.jxl'))

        swapped = OUTPUT / 'jxlp-v1-out-of-order.jxl'
        swapped.write_bytes(swap_partial_codestreams((OUTPUT / 'jxlp-v1.jxl').read_bytes()))
        # The reordered file must decode to the same pixels as the original with libjxl.
        run(djxl, str(OUTPUT / 'jxlp-v1.jxl'), str(tmp / 'a.ppm'))
        run(djxl, str(swapped), str(tmp / 'b.ppm'))
        assert (tmp / 'a.ppm').read_bytes() == (tmp / 'b.ppm').read_bytes()

    with tempfile.TemporaryDirectory() as temp:
        for name in managed:
            cms_reference(OUTPUT / f'{name}.jxl', temp, OUTPUT / 'cms' / f'{name}.png', gray='gray' in name)
        for name in [*managed, 'lossy-p3']:
            srgb_reference(OUTPUT / f'{name}.jxl', OUTPUT / 'srgb' / f'{name}.png', gray='gray' in name)
        oracle = {path.name: info(path) | {'rgba8Sha256': pixels(path, temp, OUTPUT / 'djxl' / f'{path.stem}.png' if path.stem in lossy or path.stem in LOSSY_LAYERS or path.stem in LOSSY_EXTRAS else None)}
                  for path in sorted(OUTPUT.glob('*.jxl'))}
        for name, entry in oracle.items():
            if entry['animated']:
                stem = Path(name).stem
                entry['frameSha256'] = frames(OUTPUT / name, temp, OUTPUT / 'djxl' if stem in LOSSY_ANIMATIONS else None)
    (OUTPUT / 'corpus.json').write_text(json.dumps({'generator': run(tool('cjxl'), '--version').splitlines()[0], 'files': oracle}, indent=1) + '\n')
    print(f'{len(oracle)} files, {sum(p.stat().st_size for p in OUTPUT.glob("*.jxl"))} bytes')
    import runpy
    runpy.run_path(str(Path(__file__).with_name('generate-transcoding.py')), run_name='__main__')


if __name__ == '__main__':
    main()
