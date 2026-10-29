#!/usr/bin/env python3
"""Create the speed benchmark / golden-hash inputs under ignored artifacts/speed-inputs.

Needs Pillow 10.2+, NumPy and pinned cjxl/djxl 0.12.0 (PATH or JXL_TOOLS). The photo comes from the ignored
samples/private/IMG_0160_q80.jxl (12 MP); without it only the synthetic screen image is created.
Decode inputs are encoded by cjxl so they exercise the streams libjxl writes; encode inputs are PNGs.
16-bit inputs (name suffix -16, crop16-*): the photo decoded by djxl to float sRGB and rounded to 16 bits (real 16-bit values
from the lossy original) and the screen widened from 8 bits (v * 257). photo1w-16 is the 8-bit photo1 widened the same way: an
encode input with exactly the 8-bit image's content, for the 16-bit/8-bit time ratio.
"""
import os
from pathlib import Path
import struct
import subprocess
import zlib

import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/speed-inputs'
PHOTO = ROOT / 'samples/private/IMG_0160_q80.jxl'
TOOLS = Path(os.environ['JXL_TOOLS']) if 'JXL_TOOLS' in os.environ else None
CJXL = str(TOOLS / 'cjxl') if TOOLS else 'cjxl'
DJXL = str(TOOLS / 'djxl') if TOOLS else 'djxl'


def screen(width=1600, height=1000):
    """Deterministic UI-like image: flat panels, text, thin lines and one gradient (patches, palette-friendly)."""
    image = Image.new('RGB', (width, height), (246, 247, 249))
    draw = ImageDraw.Draw(image)
    try:
        font = ImageFont.truetype('DejaVuSans.ttf', 15)
    except OSError:
        font = ImageFont.load_default()
    draw.rectangle((0, 0, width, 48), fill=(32, 40, 56))
    draw.rectangle((0, 48, 260, height), fill=(232, 235, 240))
    for i in range(24):
        draw.text((20, 64 + i * 36), f'Navigation item {i + 1:02d}', fill=(40, 44, 52), font=font)
    for row in range(40):
        y = 72 + row * 22
        draw.line((280, y + 18, width - 20, y + 18), fill=(220, 223, 228))
        draw.text((290, y), f'{row:03d}  The quick brown fox jumps over the lazy dog  {row * 7919 % 100000:>6}  OK', fill=(30, 30, 30), font=font)
    for x in range(width - 420, width - 20):
        draw.line((x, 60, x, 300), fill=(x % 256, 120, 255 - x % 256))
    return image


def cjxl(source, target, *options):
    subprocess.run([CJXL, str(source), str(target), '--num_threads=1', '--quiet', *options], check=True)


def write_png16(path, pixels):
    """Writes an H x W x C uint16 array (C = 1 gray, 2 gray+alpha, 3 RGB, 4 RGBA) as a 16-bit PNG; Pillow cannot."""
    height, width, channels = pixels.shape
    rows = np.zeros((height, 1 + width * channels * 2), np.uint8)
    rows[:, 1:] = pixels.astype('>u2').reshape(height, -1).view(np.uint8)

    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))
    header = struct.pack('>IIBBBBB', width, height, 16, {1: 0, 2: 4, 3: 2, 4: 6}[channels], 0, 0, 0)
    Path(path).write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', header) + chunk(b'IDAT', zlib.compress(rows.tobytes(), 3))
                           + chunk(b'IEND', b''))


def read_pfm16(path):
    """Reads the float PFM written by djxl (rows bottom-up) and rounds it to 16 bits per sample."""
    data = Path(path).read_bytes()
    magic, width, height, scale = data.split(maxsplit=4)[:4]
    assert magic == b'PF'
    width, height = int(width), int(height)
    pixels = np.frombuffer(data, '<f4' if float(scale) < 0 else '>f4', offset=len(data) - width * height * 12)
    return (np.clip(pixels.reshape(height, width, 3)[::-1], 0, 1) * 65535 + 0.5).astype(np.uint16)


def inputs16(screen_png):
    """16-bit PNGs; returns the encode sources for the 16-bit JXL files."""
    pngs = {}
    widened = np.asarray(Image.open(screen_png).convert('RGB'), np.uint16) * 257
    write_png16(OUT / 'screen-16.png', widened)
    write_png16(OUT / 'crop16-screen.png', widened[0:300, 240:640])
    pngs['screen-16'] = OUT / 'screen-16.png'
    if PHOTO.exists():
        pfm = OUT / 'photo12-16.pfm'
        if not (OUT / 'photo12-16.png').exists():
            subprocess.run([DJXL, str(PHOTO), str(pfm), '--num_threads=1', '--quiet', '--color_space=RGB_D65_SRG_Rel_SRG'], check=True)
            photo = read_pfm16(pfm)
            pfm.unlink()
            h, w = photo.shape[:2]
            write_png16(OUT / 'photo12-16.png', photo)
            write_png16(OUT / 'photo1-16.png', photo[(h - 800) // 2:(h + 800) // 2, (w - 1280) // 2:(w + 1280) // 2])
            write_png16(OUT / 'crop16-photo.png', photo[h // 3:h // 3 + 256, w // 3:w // 3 + 384])
            crop = photo[h // 2:h // 2 + 192, w // 2:w // 2 + 256]
            alpha = np.broadcast_to(np.linspace(0, 65535, crop.shape[0]).round().astype(np.uint16)[:, None, None], (*crop.shape[:2], 1))
            write_png16(OUT / 'crop16-photo-alpha.png', np.concatenate([crop, alpha], axis=2))
            luma = (crop.astype(np.uint32) * [2126, 7152, 722]).sum(axis=2) // 10000
            write_png16(OUT / 'crop16-gray.png', luma.astype(np.uint16)[:, :, None])
        pngs |= {'photo12-16': OUT / 'photo12-16.png', 'photo1-16': OUT / 'photo1-16.png'}
        if not (OUT / 'photo1w-16.png').exists():
            write_png16(OUT / 'photo1w-16.png', np.asarray(Image.open(OUT / 'photo1.png').convert('RGB'), np.uint16) * 257)
    return pngs


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    screen().save(OUT / 'screen.png')
    pngs = {'screen': OUT / 'screen.png'}
    if PHOTO.exists():
        photo = OUT / 'photo12.png'
        if not photo.exists():
            subprocess.run([DJXL, str(PHOTO), str(photo), '--num_threads=1', '--quiet'], check=True)
        image = Image.open(photo).convert('RGB')
        w, h = image.size
        image.crop(((w - 1280) // 2, (h - 800) // 2, (w + 1280) // 2, (h + 800) // 2)).save(OUT / 'photo1.png')
        image.crop((w // 3, h // 3, w // 3 + 384, h // 3 + 256)).save(OUT / 'crop-photo.png')
        rgba = image.crop((w // 2, h // 2, w // 2 + 256, h // 2 + 192)).convert('RGBA')
        rgba.putalpha(Image.linear_gradient('L').resize(rgba.size))
        rgba.save(OUT / 'crop-photo-alpha.png')
        pngs |= {'photo12': photo, 'photo1': OUT / 'photo1.png'}
    screen().crop((240, 0, 640, 300)).save(OUT / 'crop-screen.png')
    pngs |= inputs16(OUT / 'screen.png')
    plans = {
        'photo12': [('d1-e7', '-d', '1', '-e', '7'), ('d0-e1', '-d', '0', '-e', '1'), ('d0-e2', '-d', '0', '-e', '2'), ('d0-e7', '-d', '0', '-e', '7')],
        'photo1': [('d1-e7', '-d', '1', '-e', '7'), ('d3-e3', '-d', '3', '-e', '3'), ('d0-e3', '-d', '0', '-e', '3'), ('d0-e7', '-d', '0', '-e', '7')],
        'screen': [('d1-e7', '-d', '1', '-e', '7'), ('d0-e7', '-d', '0', '-e', '7')],
    }
    plans |= {f'{name}-16': [p for p in plans[name] if p[0] in ('d1-e7', 'd0-e7')] for name in ('photo12', 'photo1', 'screen')}
    for name, source in pngs.items():
        for label, *options in plans[name]:
            target = OUT / f'{name}-{label}.jxl'
            if not target.exists():
                cjxl(source, target, *options)
                print(target.name, target.stat().st_size)


if __name__ == '__main__':
    main()
