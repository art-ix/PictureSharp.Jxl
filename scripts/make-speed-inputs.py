#!/usr/bin/env python3
"""Create the speed benchmark / golden-hash inputs under ignored artifacts/speed-inputs.

Needs Pillow 10.2+ and pinned cjxl/djxl 0.12.0 (PATH or JXL_TOOLS). The photo comes from the ignored
samples/private/IMG_0160_q80.jxl (12 MP); without it only the synthetic screen image is created.
Decode inputs are encoded by cjxl so they exercise the streams libjxl writes; encode inputs are PNGs.
"""
import os
from pathlib import Path
import subprocess

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
    plans = {
        'photo12': [('d1-e7', '-d', '1', '-e', '7'), ('d0-e1', '-d', '0', '-e', '1'), ('d0-e2', '-d', '0', '-e', '2'), ('d0-e7', '-d', '0', '-e', '7')],
        'photo1': [('d1-e7', '-d', '1', '-e', '7'), ('d3-e3', '-d', '3', '-e', '3'), ('d0-e3', '-d', '0', '-e', '3'), ('d0-e7', '-d', '0', '-e', '7')],
        'screen': [('d1-e7', '-d', '1', '-e', '7'), ('d0-e7', '-d', '0', '-e', '7')],
    }
    for name, source in pngs.items():
        for label, *options in plans[name]:
            target = OUT / f'{name}-{label}.jxl'
            if not target.exists():
                cjxl(source, target, *options)
                print(target.name, target.stat().st_size)


if __name__ == '__main__':
    main()
