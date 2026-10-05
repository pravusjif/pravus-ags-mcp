"""Small helpers for drawing crisp pixel art with PIL, for AGS rooms and sprites.

Everything draws at 1x with no anti-aliasing. Colours are (r, g, b) tuples.

    from pixelart import new, rect, dither, shade, mix, jit, put, blend_px, shadow_ellipse, preview

Run as a script to composite a preview:
    python pixelart.py preview bg.png out.png 3 sprite.png,x,y_bottom [sprite2.png,x,y ...]
"""
import random
import sys
from pathlib import Path

from PIL import Image, ImageDraw

BAYER = [[0, 8, 2, 10], [12, 4, 14, 6], [3, 11, 1, 9], [15, 7, 13, 5]]
_R = random.Random(1337)


def seed(n):
    """Reseed the module RNG used by jit()/rect(j=...)."""
    global _R
    _R = random.Random(n)


def clamp(v):
    return max(0, min(255, int(v)))


def shade(c, d):
    """Lighten (d>0) or darken (d<0) a colour by a fixed amount per channel."""
    return (clamp(c[0] + d), clamp(c[1] + d), clamp(c[2] + d))


def mix(a, b, t):
    """Linear blend between two colours, t in 0..1."""
    return tuple(clamp(a[i] + (b[i] - a[i]) * t) for i in range(3))


def jit(c, amount):
    """Random per-pixel brightness noise."""
    return shade(c, _R.randint(-amount, amount))


def new(w, h, fill=(0, 0, 0, 0)):
    """New RGBA image; default fully transparent (sprites). Use (r,g,b,255) for backgrounds."""
    return Image.new("RGBA", (w, h), fill)


def put(img, x, y, c):
    """Set one opaque pixel; silently ignores out-of-bounds."""
    if 0 <= x < img.width and 0 <= y < img.height:
        img.putpixel((x, y), (c[0], c[1], c[2], 255))


def rect(img, x0, y0, x1, y1, c, j=0):
    """Inclusive filled rectangle; j>0 adds per-pixel noise (plaster, wood, stone)."""
    px = img.load()
    for y in range(max(0, y0), min(img.height - 1, y1) + 1):
        for x in range(max(0, x0), min(img.width - 1, x1) + 1):
            col = jit(c, j) if j else c
            px[x, y] = (col[0], col[1], col[2], 255)


def dither(img, x0, y0, x1, y1, ca, cb, vertical=True):
    """Ordered-dither gradient from ca to cb across the inclusive rect (vertical by default)."""
    px = img.load()
    span = max(1, (y1 - y0) if vertical else (x1 - x0))
    for y in range(y0, y1 + 1):
        for x in range(x0, x1 + 1):
            t = ((y - y0) if vertical else (x - x0)) / span
            col = cb if BAYER[y % 4][x % 4] / 16.0 < t else ca
            px[x, y] = (col[0], col[1], col[2], 255)


def blend_px(img, x, y, c, a):
    """Blend colour c over an opaque pixel with alpha a (0..1). For stepped shadows and light spill."""
    if 0 <= x < img.width and 0 <= y < img.height:
        p = img.getpixel((x, y))
        img.putpixel((x, y), tuple(clamp(p[i] + (c[i] - p[i]) * a) for i in range(3)) + (255,))


def shadow_ellipse(img, box, alpha=0.35, steps=4):
    """Stepped drop shadow (concentric ellipses) under furniture, onto an opaque image."""
    x0, y0, x1, y1 = box
    for k in range(steps):
        layer = new(img.width, img.height)
        ImageDraw.Draw(layer).ellipse((x0 + k * 2, y0 + k, x1 - k * 2, y1 - k),
                                      fill=(0, 0, 0, int(255 * alpha / steps)))
        img.alpha_composite(layer)


def outline_ellipse(img, box, fill, edge):
    """Ellipse with a 1-px edge colour."""
    d = ImageDraw.Draw(img)
    d.ellipse(box, fill=edge + (255,))
    x0, y0, x1, y1 = box
    d.ellipse((x0 + 1, y0 + 1, x1 - 1, y1 - 1), fill=fill + (255,))


def bevel_rect(img, x0, y0, x1, y1, c, j=0, light=28, dark=-30):
    """Filled rect with a light top/left edge and a dark bottom/right edge: boards, panels, boxes."""
    rect(img, x0, y0, x1, y1, c, j)
    rect(img, x0, y0, x1, y0, shade(c, light))
    rect(img, x0, y0, x0, y1, shade(c, light))
    rect(img, x0, y1, x1, y1, shade(c, dark))
    rect(img, x1, y0, x1, y1, shade(c, dark))


def preview(background, placements, scale=3, out="preview.png"):
    """Composite sprites onto a background at (x, y_bottom) — the same convention as AGS objects'
    StartX/StartY — and save a nearest-neighbour upscaled PNG to inspect before importing."""
    bg = Image.open(background).convert("RGBA")
    for path, x, y_bottom in placements:
        s = Image.open(path).convert("RGBA")
        bg.alpha_composite(s, (int(x), int(y_bottom) - s.height))
    bg = bg.resize((bg.width * scale, bg.height * scale), Image.NEAREST)
    bg.save(out)
    return out


def _cli(argv):
    if len(argv) >= 4 and argv[0] == "preview":
        bg, out, scale = argv[1], argv[2], int(argv[3])
        placements = []
        for spec in argv[4:]:
            p, x, y = spec.split(",")
            placements.append((p, int(x), int(y)))
        print("wrote", preview(bg, placements, scale, out))
        return 0
    print(__doc__)
    return 1


if __name__ == "__main__":
    sys.exit(_cli(sys.argv[1:]))
