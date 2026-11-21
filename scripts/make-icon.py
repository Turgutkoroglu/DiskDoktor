#!/usr/bin/env python3
"""Draws the DiskDoktor icon (needs Pillow) and writes every size the platforms need.

Design: blue-teal rounded square, a white disk platter whose green arc shows freed space,
and a red medical cross badge ("doctor"). Drawn at 1024 px and downsampled for clean edges.

Usage: python3 scripts/make-icon.py
"""
import math
import os
import struct
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "src", "DiskDoktor.App", "Assets")
S = 1024


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def gradient(size, top, bottom):
    g = Image.new("RGBA", (size, size))
    px = g.load()
    for y in range(size):
        for x in range(size):
            t = (x * 0.35 + y * 0.65) / size
            px[x, y] = lerp(top, bottom, min(1, max(0, t)))
    return g


def draw(size=S):
    k = size / S
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))

    # soft shadow + squircle background
    m = int(64 * k)
    shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((m, m + 18 * k, size - m, size - m + 18 * k), radius=int(210 * k), fill=(8, 20, 50, 110))
    img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(24 * k)))

    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((m, m, size - m, size - m), radius=int(210 * k), fill=255)
    bg = gradient(size, (34, 117, 245, 255), (13, 182, 170, 255))
    img.paste(bg, (0, 0), mask)

    d = ImageDraw.Draw(img)
    cx, cy, r = size * 0.46, size * 0.48, size * 0.27

    # platter
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(255, 255, 255, 255))
    # free-space arc (thick green ring segment)
    ring = r * 0.80
    w = int(r * 0.30)
    d.arc((cx - ring, cy - ring, cx + ring, cy + ring), start=-90, end=40, fill=(34, 197, 94, 255), width=w)
    # used part, light grey
    d.arc((cx - ring, cy - ring, cx + ring, cy + ring), start=40, end=270, fill=(214, 224, 238, 255), width=w)
    # spindle
    h = r * 0.22
    d.ellipse((cx - h, cy - h, cx + h, cy + h), fill=(34, 117, 245, 255))
    h2 = r * 0.08
    d.ellipse((cx - h2, cy - h2, cx + h2, cy + h2), fill=(255, 255, 255, 255))

    # sparkle (cleaned)
    sx, sy, sr = size * 0.75, size * 0.24, size * 0.055
    pts = []
    for i in range(8):
        a = math.pi / 4 * i - math.pi / 2
        rad = sr if i % 2 == 0 else sr * 0.32
        pts.append((sx + rad * math.cos(a), sy + rad * math.sin(a)))
    d.polygon(pts, fill=(255, 255, 255, 240))

    # medical cross badge
    bx, by, br = size * 0.70, size * 0.71, size * 0.135
    d.ellipse((bx - br, by - br, bx + br, by + br), fill=(255, 255, 255, 255))
    d.ellipse((bx - br * 0.86, by - br * 0.86, bx + br * 0.86, by + br * 0.86), fill=(239, 68, 68, 255))
    a, b = br * 0.52, br * 0.17
    d.rounded_rectangle((bx - b, by - a, bx + b, by + a), radius=int(b * 0.5), fill=(255, 255, 255, 255))
    d.rounded_rectangle((bx - a, by - b, bx + a, by + b), radius=int(b * 0.5), fill=(255, 255, 255, 255))
    return img


def write_icns(images, path):
    """Minimal ICNS with PNG payloads (macOS 10.7+)."""
    types = {16: b"icp4", 32: b"icp5", 64: b"icp6", 128: b"ic07", 256: b"ic08", 512: b"ic09", 1024: b"ic10"}
    chunks = b""
    for size, typ in types.items():
        import io
        buf = io.BytesIO()
        images(size).save(buf, "PNG")
        data = buf.getvalue()
        chunks += typ + struct.pack(">I", len(data) + 8) + data
    with open(path, "wb") as f:
        f.write(b"icns" + struct.pack(">I", len(chunks) + 8) + chunks)


def main():
    master = draw(S)
    cache = {}

    def at(size):
        if size not in cache:
            cache[size] = master.resize((size, size), Image.LANCZOS)
        return cache[size]

    os.makedirs(ASSETS, exist_ok=True)
    at(256).save(os.path.join(ASSETS, "diskdoktor.png"))
    at(512).save(os.path.join(ASSETS, "diskdoktor-512.png"))
    master.save(os.path.join(ASSETS, "diskdoktor-1024.png"))
    master.save(os.path.join(ASSETS, "diskdoktor.ico"), sizes=[(s, s) for s in (16, 24, 32, 48, 64, 128, 256)])
    write_icns(at, os.path.join(ASSETS, "diskdoktor.icns"))
    print("Simgeler yazıldı:", ASSETS)


if __name__ == "__main__":
    main()
