"""Generates ribbon icons (16/32 px PNG) and tooltip preview clips (MP4 + still PNG).

Run from the repo root:  python3 tools/generate_media.py
Requires Pillow and ffmpeg (libx264). Output is committed so Windows builds need neither.
"""
import math
import os
import subprocess
import tempfile

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ICON_DIR = os.path.join(ROOT, "src", "PipingSuite", "Resources", "Icons")
TIP_DIR = os.path.join(ROOT, "src", "PipingSuite", "Resources", "Tooltips")

# One palette for everything so the tab reads as a family.
PIPE = (47, 102, 144)        # steel blue
PIPE_LIGHT = (126, 170, 204)
GHOST = (176, 190, 202)
ORANGE = (232, 135, 30)
GREEN = (63, 155, 90)
RED = (200, 66, 59)
PURPLE = (124, 92, 173)
INK = (38, 50, 56)
BG = (246, 248, 250)
GRID = (230, 234, 238)
WHITE = (255, 255, 255)

FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
FONT_BOLD = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"


def font(size, bold=False):
    return ImageFont.truetype(FONT_BOLD if bold else FONT, size)


# ---------------------------------------------------------------- drawing helpers

def pipe(d, p1, p2, w, color=PIPE):
    """Pipe segment: rounded body plus a lighter highlight stripe for a 3D read."""
    d.line([p1, p2], fill=color, width=w)
    for p in (p1, p2):
        d.ellipse([p[0] - w / 2, p[1] - w / 2, p[0] + w / 2, p[1] + w / 2], fill=color)
    if w >= 8:
        dx, dy = p2[0] - p1[0], p2[1] - p1[1]
        length = math.hypot(dx, dy) or 1
        nx, ny = -dy / length * w * 0.22, dx / length * w * 0.22
        hl = tuple(min(255, c + 70) for c in color)
        d.line([(p1[0] - nx, p1[1] - ny), (p2[0] - nx, p2[1] - ny)], fill=hl, width=max(2, w // 5))


def tag(d, x, y, w, h, color, text=None, f=None, text_color=WHITE):
    """Label tag: rectangle with a pointed left end and a punched hole."""
    p = h / 2
    d.polygon([(x, y + p), (x + p, y), (x + w, y), (x + w, y + h), (x + p, y + h)], fill=color)
    r = max(2, h * 0.12)
    d.ellipse([x + p * 0.75 - r, y + p - r, x + p * 0.75 + r, y + p + r], fill=WHITE)
    if text and f:
        tw = d.textlength(text, font=f)
        d.text((x + p + (w - p - tw) / 2, y + h / 2), text, font=f, fill=text_color, anchor="lm")


def arrow(d, p1, p2, w, color):
    d.line([p1, p2], fill=color, width=w)
    ang = math.atan2(p2[1] - p1[1], p2[0] - p1[0])
    s = w * 2.6
    d.polygon([p2,
               (p2[0] - s * math.cos(ang - 0.5), p2[1] - s * math.sin(ang - 0.5)),
               (p2[0] - s * math.cos(ang + 0.5), p2[1] - s * math.sin(ang + 0.5))], fill=color)


def check(d, cx, cy, s, color, w):
    d.line([(cx - s, cy), (cx - s * 0.3, cy + s * 0.7), (cx + s, cy - s * 0.8)], fill=color, width=w, joint="curve")


def dashed_rect(d, box, color, w, dash, progress=1.0):
    """Rectangle outline drawn as dashes, revealed clockwise up to `progress`."""
    x0, y0, x1, y1 = box
    pts = [(x0, y0), (x1, y0), (x1, y1), (x0, y1), (x0, y0)]
    total = 2 * ((x1 - x0) + (y1 - y0))
    budget = total * progress
    for a, b in zip(pts, pts[1:]):
        seg = math.hypot(b[0] - a[0], b[1] - a[1])
        t = 0.0
        while t < seg and budget > 0:
            e = min(t + dash, seg, t + budget)
            d.line([(a[0] + (b[0] - a[0]) * t / seg, a[1] + (b[1] - a[1]) * t / seg),
                    (a[0] + (b[0] - a[0]) * e / seg, a[1] + (b[1] - a[1]) * e / seg)], fill=color, width=w)
            budget -= (e - t) + dash
            t += dash * 2


def ease(t):
    t = max(0.0, min(1.0, t))
    return 4 * t * t * t if t < 0.5 else 1 - (-2 * t + 2) ** 3 / 2


def phase(t, start, end):
    return ease((t - start) / (end - start))


def mix(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


# ---------------------------------------------------------------- icons (drawn at 128, downsampled)

def icon_assemblies(d):
    dashed_rect(d, (8, 14, 120, 114), ORANGE, 7, 13)
    pipe(d, (28, 44), (72, 44), 18)
    pipe(d, (72, 44), (72, 92), 18)
    pipe(d, (72, 92), (104, 92), 18, PIPE_LIGHT)
    d.ellipse([60, 32, 84, 56], fill=INK)
    d.ellipse([60, 80, 84, 104], fill=INK)


def icon_copy(d):
    pipe(d, (14, 104), (114, 104), 20)
    tag(d, 30, 14, 82, 38, PIPE_LIGHT)
    tag(d, 14, 34, 82, 38, ORANGE)
    d.line([(52, 75), (52, 92)], fill=INK, width=5)


def icon_paste(d):
    d.rounded_rectangle([18, 18, 104, 118], 10, fill=(150, 112, 74))
    d.rounded_rectangle([28, 30, 94, 110], 6, fill=WHITE)
    d.rounded_rectangle([42, 8, 80, 28], 6, fill=INK)
    tag(d, 34, 52, 82, 34, ORANGE)
    arrow(d, (74, 96), (112, 96), 8, GREEN)


def icon_combine(d):
    tag(d, 4, 12, 58, 32, PIPE)
    tag(d, 4, 84, 58, 32, ORANGE)
    d.line([(64, 28), (82, 64)], fill=INK, width=7)
    d.line([(64, 100), (82, 64)], fill=INK, width=7)
    tag(d, 72, 46, 54, 36, GREEN)
    d.line([(100, 54), (100, 74)], fill=WHITE, width=6)
    d.line([(90, 64), (110, 64)], fill=WHITE, width=6)


def icon_align(d):
    pipe(d, (6, 92), (122, 92), 26)               # main stays
    d.rectangle([70, 64, 98, 92], fill=INK)        # tee outlet
    pipe(d, (52, 8), (52, 40), 14, GHOST)          # where it was
    arrow(d, (58, 30), (76, 30), 6, ORANGE)
    pipe(d, (84, 8), (84, 60), 14)                 # aligned branch
    check(d, 30, 58, 13, GREEN, 8)


def icon_color(d):
    pipe(d, (12, 30), (92, 30), 18, RED)
    pipe(d, (12, 64), (92, 64), 18, GREEN)
    pipe(d, (12, 98), (92, 98), 18, PIPE)
    for (x, y, r, c) in [(106, 26, 16, ORANGE), (112, 58, 9, PURPLE), (100, 84, 11, ORANGE), (118, 92, 6, RED)]:
        d.ellipse([x - r, y - r, x + r, y + r], fill=c)


def icon_olet(d):
    pipe(d, (6, 96), (122, 96), 34)                           # main
    d.line([(64, 10), (64, 120)], fill=ORANGE, width=4)        # centreline: olet stays centred
    d.polygon([(42, 80), (86, 80), (78, 50), (50, 50)], fill=INK)  # olet body
    d.rectangle([52, 38, 76, 50], fill=GREEN)                  # open branch connector
    pipe(d, (64, 6), (64, 26), 14, GHOST)                      # temporary branch (removed)


def _mini_olet(d, cx, y, w, color):
    d.polygon([(cx - w, y), (cx + w, y), (cx + w * 0.6, y - w * 1.2), (cx - w * 0.6, y - w * 1.2)], fill=color)


def icon_copy_olet(d):
    pipe(d, (6, 34), (78, 34), 20)                 # source with olet
    _mini_olet(d, 42, 24, 12, INK)
    pipe(d, (50, 76), (122, 76), 20)               # targets receiving copies
    _mini_olet(d, 86, 66, 12, ORANGE)
    pipe(d, (50, 114), (122, 114), 20)
    _mini_olet(d, 86, 104, 12, ORANGE)
    arrow(d, (20, 60), (20, 100), 6, GREEN)


def _iso(x, y, z, ox, oy, k):
    """Isometric projection (30°) of plan x/y and height z."""
    return (ox + (x + y) * k * 0.866, oy + (x - y) * k * 0.5 - z * k)


def icon_iso_map(d):
    d.rectangle([6, 6, 122, 122], outline=INK, width=5)                  # square crop
    run = [(0, 0, 0), (3, 0, 0), (3, 0, 3), (3, 3, 3)]
    pts = [_iso(*p, 22, 58, 15) for p in run]
    for (a, b), c in zip(zip(pts, pts[1:]), (RED, GREEN, PURPLE)):    # rainbow by line segment
        pipe(d, a, b, 13, c)
    tag(d, 44, 92, 66, 22, ORANGE)


YELLOW, BLUE = (240, 190, 60), (90, 140, 210)
KEY = [YELLOW, GREEN, RED, BLUE]


def icon_overall_map(d):
    for r in range(3):                                                    # translucent box per spool
        for c in range(3):
            col = KEY[(r * 3 + c) % 4]
            x, y = 10 + c * 40, 10 + r * 40
            d.rectangle([x, y, x + 30, y + 30], fill=col + (150,), outline=col, width=3)
            d.line([(x + 15, y + 4), (x + 15, y + 26)], fill=INK, width=4)


def _valve(d, cx, cy, r, color):
    """Bow-tie valve symbol on a vertical run."""
    d.polygon([(cx - r, cy - r), (cx + r, cy - r), (cx - r, cy + r), (cx + r, cy + r)], fill=color)


def icon_replace(d):
    pipe(d, (6, 18), (122, 18), 20)                                       # header
    pipe(d, (34, 28), (34, 112), 12, GHOST)                               # old branch
    pipe(d, (94, 28), (94, 112), 12, GREEN)                               # correct branch
    _valve(d, 94, 64, 12, INK)
    arrow(d, (48, 70), (78, 70), 6, ORANGE)


def icon_heal(d):
    pipe(d, (6, 64), (58, 64), 22)                                        # two runs touching, not joined
    pipe(d, (70, 64), (122, 64), 22)
    d.ellipse([46, 34, 82, 94], outline=RED, width=6)                     # the disconnect
    check(d, 64, 108, 12, GREEN, 7)                                       # joined
    pipe(d, (64, 6), (64, 30), 12, PIPE_LIGHT)


def icon_ez_iso(d):
    pipe(d, (6, 30), (122, 30), 20)                                       # header
    pipe(d, (36, 40), (36, 84), 12, PIPE_LIGHT)                           # two branches off the header
    pipe(d, (92, 40), (92, 84), 12, PIPE_LIGHT)
    f = font(18, True)
    tag(d, 14, 80, 46, 26, GREEN, "05", f)                                # numbered
    tag(d, 70, 80, 46, 26, RED, "05", f)                                  # duplicate
    check(d, 64, 118, 10, GREEN, 7)                                       # fixed


def icon_iso_views(d):
    f = font(18, True)
    run = [(0, 0, 0), (3, 0, 0), (3, 0, 3)]                                # elbow: run, then rise
    for x0, label in ((6, "A"), (66, "B")):
        d.rectangle([x0, 14, x0 + 56, 70], fill=WHITE, outline=INK, width=5)   # square view frame
        pts = [_iso(*p, x0 + 13.7, 42, 11) for p in run]                    # bend centred in the frame
        for a, b in zip(pts, pts[1:]):
            pipe(d, a, b, 10, PIPE)
        tag(d, x0 + 8, 80, 40, 22, ORANGE, label, f)


def icon_auto_dim(d):
    K = 22
    run = [(0, 0, 0), (4, 0, 0), (4, 0, 4)]                                # elbow: leg along x, then rise
    raw = [_iso(*p, 0, 0, K) for p in run]
    mx = (min(x for x, _ in raw) + max(x for x, _ in raw)) / 2
    my = (min(y for _, y in raw) + max(y for _, y in raw)) / 2
    pts = [(64 - mx + x, 56 - my + y) for x, y in raw]
    for a, b in zip(pts, pts[1:]):
        pipe(d, a, b, 12, PIPE)
    # dimension line parallel to the x leg, offset outside the bend, ticks at both ends
    (ax, ay), (bx, by) = pts[0], pts[1]
    L = math.hypot(bx - ax, by - ay)
    ux, uy = (bx - ax) / L, (by - ay) / L
    nx, ny = -uy, ux
    cxm = sum(x for x, _ in pts) / len(pts)
    cym = sum(y for _, y in pts) / len(pts)
    if (cxm - (ax + bx) / 2) * nx + (cym - (ay + by) / 2) * ny > 0:
        nx, ny = -nx, -ny
    off = 26
    Ax, Ay = ax + nx * off, ay + ny * off
    Bx, By = bx + nx * off, by + ny * off
    for px, py in ((ax, ay), (bx, by)):
        d.line([(px + nx * 4, py + ny * 4), (px + nx * (off + 4), py + ny * (off + 4))], fill=INK, width=4)
    d.line([(Ax, Ay), (Bx, By)], fill=INK, width=5)
    tvx, tvy = (ux + nx) / 1.414, (uy + ny) / 1.414
    for px, py in ((Ax, Ay), (Bx, By)):
        d.line([(px - tvx * 6, py - tvy * 6), (px + tvx * 6, py + tvy * 6)], fill=INK, width=5)


def icon_iso_bom(d):
    K = 13
    run = [(0, 0, 0), (4, 0, 0), (4, 0, 4)]                                # small iso spool, top left
    raw = [_iso(*p, 0, 0, K) for p in run]
    mx = (min(x for x, _ in raw) + max(x for x, _ in raw)) / 2
    my = (min(y for _, y in raw) + max(y for _, y in raw)) / 2
    pts = [(40 - mx + x, 40 - my + y) for x, y in raw]
    for a, b in zip(pts, pts[1:]):
        pipe(d, a, b, 10, PIPE)
    # BOM table, bottom right: header row filled, ruled rows, item column
    x0, y0, x1, y1, rows = 46, 62, 120, 120, 4
    d.rectangle([x0, y0, x1, y1], fill=WHITE, outline=INK, width=4)
    rh = (y1 - y0) / (rows + 1)
    d.rectangle([x0, y0, x1, y0 + rh], fill=ORANGE, outline=INK, width=4)
    for i in range(2, rows + 1):
        d.line([(x0, y0 + i * rh), (x1, y0 + i * rh)], fill=INK, width=3)
    d.line([(x0 + 18, y0), (x0 + 18, y1)], fill=INK, width=3)


def icon_auto_tag(d):
    K = 12
    hdr = [(0, 0, 0), (8, 0, 0)]                                           # header with three identical branches
    pts = lambda p: (14 + _iso(*p, 0, 0, K)[0], 60 + _iso(*p, 0, 0, K)[1])
    pipe(d, pts(hdr[0]), pts((6.5, 0, 0)), 10, PIPE)
    for x in (1, 3, 5):
        pipe(d, pts((x, 0, 0)), pts((x, 0, 4)), 8, GREEN)
    # one tag for the header, one for all the branches
    for (x, y, color), (lx, ly) in zip(((78, 8, GREEN), (78, 98, PIPE)), (pts((5, 0, 3.5)), pts((6, 0, 0)))):
        d.line([(lx, ly), (x - 6, y + 11), (x, y + 11)], fill=INK, width=3)
        tag(d, x, y, 46, 22, color)


ICONS = {
    "assemblies": icon_assemblies,
    "copy": icon_copy,
    "paste": icon_paste,
    "combine": icon_combine,
    "align": icon_align,
    "color": icon_color,
    "olet": icon_olet,
    "copy_olet": icon_copy_olet,
    "iso_map": icon_iso_map,
    "overall_map": icon_overall_map,
    "replace": icon_replace,
    "heal": icon_heal,
    "ez_iso": icon_ez_iso,
    "iso_views": icon_iso_views,
    "auto_dim": icon_auto_dim,
    "iso_bom": icon_iso_bom,
    "auto_tag": icon_auto_tag,
}


def render_icons():
    os.makedirs(ICON_DIR, exist_ok=True)
    for name, draw_fn in ICONS.items():
        img = Image.new("RGBA", (128, 128), (0, 0, 0, 0))
        draw_fn(ImageDraw.Draw(img))
        for size in (16, 32):
            img.resize((size, size), Image.LANCZOS).save(os.path.join(ICON_DIR, f"{name}_{size}.png"))


# ---------------------------------------------------------------- tooltip clips

W, H, SS, FPS, SECONDS = 400, 240, 2, 24, 5


def canvas():
    img = Image.new("RGB", (W * SS, H * SS), BG)
    d = ImageDraw.Draw(img)
    for x in range(0, W * SS, 20 * SS):
        d.line([(x, 0), (x, H * SS)], fill=GRID, width=1)
    for y in range(0, H * SS, 20 * SS):
        d.line([(0, y), (W * SS, y)], fill=GRID, width=1)
    return img, d


def caption(d, text):
    d.rectangle([0, (H - 30) * SS, W * SS, H * SS], fill=INK)
    d.text((12 * SS, (H - 15) * SS), text, font=font(12 * SS), fill=WHITE, anchor="lm")


def s(*v):
    return tuple(x * SS for x in v)


def scene_assemblies(t, d):
    runs = [
        ("PA-L1-001", PIPE, [(40, 70), (180, 70), (180, 150), (260, 150)], (28, 52, 274, 168)),
        ("PA-L2-001", GREEN, [(220, 40), (360, 40), (360, 120)], (208, 22, 376, 134)),
    ]
    for i, (label, color, pts, box) in enumerate(runs):
        p = phase(t, 0.8 + i * 1.4, 1.8 + i * 1.4)
        col = mix(GHOST, color, p)
        for a, b in zip(pts, pts[1:]):
            pipe(d, s(*a), s(*b), 12 * SS, col)
        for j in pts[1:-1]:
            d.ellipse([*s(j[0] - 9, j[1] - 9), *s(j[0] + 9, j[1] + 9)], fill=INK)
        if p > 0:
            dashed_rect(d, s(*box), ORANGE, 2 * SS, 6 * SS, p)
        if p > 0.95:
            tag(d, *s(box[0], box[3] - 2 if i == 0 else box[1] - 16, 90, 18), ORANGE, label, font(10 * SS, True))
    caption(d, "Create Assemblies: one assembly per run / line")


def scene_copy_paste(t, d):
    f = font(10 * SS, True)
    pipe(d, s(30, 160), s(130, 160), 14 * SS)
    glow = phase(t, 0.2, 0.8) * (1 - phase(t, 3.6, 4.2))
    if glow > 0:
        d.rounded_rectangle([*s(18, 140), *s(142, 180)], 8 * SS, outline=ORANGE, width=int(2 * SS * glow) or 1)
    tag(d, *s(40, 108, 92, 24), ORANGE, "PKG9 | L1", f)

    targets = [(250, 60), (250, 120), (250, 180)]
    for i, (x, y) in enumerate(targets):
        pipe(d, s(x, y + 22), s(x + 110, y + 22), 12 * SS)
        p = phase(t, 1.0 + i * 0.6, 1.9 + i * 0.6)
        if p >= 1:
            tag(d, *s(x + 10, y - 8, 92, 24), ORANGE, "PKG9 | L1", f)
        else:
            tag(d, *s(x + 10, y - 8, 92, 24), GHOST, "—", f)
            if p > 0:
                fx, fy = 40 + (x + 10 - 40) * p, 108 + (y - 8 - 108) * p - math.sin(p * math.pi) * 30
                tag(d, *s(fx, fy, 92, 24), ORANGE, "PKG9 | L1", f)
    caption(d, "Copy from a source pipe, paste onto targets")


def scene_combine(t, d):
    f = font(11 * SS, True)
    p = phase(t, 0.8, 2.2)
    a_y = 50 + (100 - 50) * p
    b_y = 150 + (100 - 150) * p
    fade = 1 - phase(t, 2.0, 2.4)
    if fade > 0:
        tag(d, *s(40 + 60 * p, a_y, 130, 30), mix(BG, PIPE, fade), "PKG = PKG9", f)
        tag(d, *s(40 + 60 * p, b_y, 130, 30), mix(BG, ORANGE, fade), "LINENUMBER = 1", f)
    pop = phase(t, 2.2, 2.8)
    if pop > 0:
        w, h = 236 * (0.9 + 0.1 * pop), 36 * (0.9 + 0.1 * pop)
        tag(d, *s(200 - w / 2, 105 - h / 2, w, h), mix(BG, GREEN, pop), "PKG_LINENUMBER = PKG9-1", f)
    if t > 3.0:
        for i in range(3):
            q = phase(t, 3.0 + i * 0.3, 3.4 + i * 0.3)
            x = 60 + i * 110
            pipe(d, s(x, 185), s(x + 90, 185), 10 * SS)
            if q > 0:
                d.text(s(x + 45, 168), "PKG9-1", font=font(10 * SS, True), fill=mix(BG, INK, q), anchor="mm")
    caption(d, "Combine Parameters: A + separator + B")


def scene_align(t, d):
    pipe(d, s(20, 160), s(380, 160), 30 * SS)                # main: never moves
    d.rectangle([*s(212, 126), *s(248, 146)], fill=INK)      # tee outlet
    slide = phase(t, 1.0, 2.0)
    drop = phase(t, 2.1, 2.8)
    x = 205 + (230 - 205) * slide
    bottom = 96 + (126 - 96) * drop
    if slide < 1:
        d.line([*s(205, 20), *s(205, 110)], fill=RED, width=SS)
        d.line([*s(230, 20), *s(230, 110)], fill=RED, width=SS)
        d.text(s(218, 14), "offset", font=font(9 * SS), fill=RED, anchor="mm")
    pipe(d, s(x, 20), s(x, bottom), 14 * SS)
    ok = phase(t, 2.8, 3.2)
    if ok > 0:
        r = 26 * ok
        d.ellipse([*s(230 - r, 136 - r), *s(230 + r, 136 + r)], outline=GREEN, width=3 * SS)
        check(d, *s(276, 70), 14 * SS * ok, GREEN, 5 * SS)
        d.text(s(298, 72), "Connected", font=font(11 * SS, True), fill=GREEN, anchor="lm")
    caption(d, "Align & Connect: the smaller pipe moves, the main stays put")


def scene_color(t, d):
    values = [("L1", RED), ("L2", GREEN), ("L3", PIPE), ("L4", ORANGE)]
    rows = [(30, 40, 0), (30, 80, 1), (150, 80, 1), (30, 120, 2), (150, 120, 3), (30, 160, 3), (150, 160, 0)]
    for i, (x, y, v) in enumerate(rows):
        p = phase(t, 0.8 + i * 0.18, 1.4 + i * 0.18)
        pipe(d, s(x, y), s(x + 100, y), 12 * SS, mix(GHOST, values[v][1], p))
    lp = phase(t, 2.4, 3.0)
    if lp > 0:
        d.rounded_rectangle([*s(290, 30), *s(380, 180)], 6 * SS, fill=mix(BG, WHITE, lp), outline=mix(BG, GRID, lp))
        for i, (name, color) in enumerate(values):
            y = 50 + i * 32
            d.rectangle([*s(302, y - 8), *s(326, y + 8)], fill=mix(WHITE, color, lp))
            d.text(s(336, y), name, font=font(11 * SS, True), fill=mix(WHITE, INK, lp), anchor="lm")
    caption(d, "Color Splasher: color the view by any parameter value")


def scene_olet(t, d):
    cx = 200
    pipe(d, s(20, 165), s(380, 165), 40 * SS)                       # main never moves
    d.line([*s(cx, 20), *s(cx, 205)], fill=ORANGE, width=SS)        # centreline
    branch = phase(t, 0.3, 1.0) * (1 - phase(t, 1.8, 2.4))          # temp branch draws in, then is removed
    if branch > 0:
        pipe(d, s(cx, 140 - 110 * branch), s(cx, 140), 16 * SS, mix(BG, GHOST, branch))
    grow = phase(t, 3.0, 3.8)                                       # resize: stays centred
    olet = phase(t, 1.1, 1.6)
    if olet > 0:
        half_top, half_bot, height = 12 + 10 * grow, 22 + 14 * grow, 30 + 6 * grow
        col = mix(BG, INK, olet)
        d.polygon([s(cx - half_bot, 147), s(cx + half_bot, 147), s(cx + half_top, 147 - height), s(cx - half_top, 147 - height)], fill=col)
        d.rectangle([*s(cx - half_top, 147 - height - 8), *s(cx + half_top, 147 - height)], fill=mix(BG, GREEN, olet))
        size = "1\"" if grow < 0.5 else "2\""
        d.text(s(cx + 46, 100), size, font=font(14 * SS, True), fill=col, anchor="lm")
    caption(d, "Insert Olet: centred on the main, no branch pipe left")


def scene_copy_olet(t, d):
    f = font(10 * SS, True)
    pipe(d, s(30, 60), s(190, 60), 18 * SS)                    # source
    _mini_olet(d, *s(90, 51), 14 * SS, INK)
    d.text(s(110, 30), "source", font=f, fill=INK, anchor="mm")
    rows = [(200, 30, 190), (220, 230, 360), (260, 230, 300)]  # (y, x0, x1) – last one is short
    for i, (y, x0, x1) in enumerate(rows):
        if i == 2:
            grow = phase(t, 2.4, 3.0)                           # short pipe is extended to fit
            x1 = 300 + (330 - 300) * grow
        pipe(d, s(x0, y - 60 if i == 0 else y - 100), s(x1, y - 60 if i == 0 else y - 100), 18 * SS)
    targets = [(90, 140), (290, 120), (290, 160)]
    for i, (x, y) in enumerate(targets):
        p = phase(t, 1.0 + i * 0.5 + (0.9 if i == 2 else 0), 1.5 + i * 0.5 + (0.9 if i == 2 else 0))
        if p > 0:
            _mini_olet(d, *s(x, y - 9), 14 * SS * (0.9 + 0.1 * p), mix(BG, ORANGE, p))
    if t > 3.0:
        d.text(s(345, 160), "extended", font=font(9 * SS), fill=GREEN, anchor="lm")
    caption(d, "Copy Olet: same spot on every selected pipe")


def scene_iso_map(t, d):
    f = font(9 * SS, True)
    run = [(0, 0, 0), (5, 0, 0), (5, 0, 4), (5, 5, 4), (9, 5, 4)]
    colors = [RED, ORANGE, GREEN, PURPLE]
    pts = [s(*_iso(*p, 99, 112, 11)) for p in run]
    for i, (a, b) in enumerate(zip(pts, pts[1:])):
        pipe(d, a, b, 8 * SS, mix(GHOST, colors[i], phase(t, 0.6 + i * 0.25, 1.1 + i * 0.25)))
    for i, (a, b) in enumerate(zip(pts, pts[1:])):                   # one tag per segment, stacked in a column
        p = phase(t, 1.9 + i * 0.25, 2.3 + i * 0.25)
        if p > 0:
            mx, my = (a[0] + b[0]) / 2, (a[1] + b[1]) / 2
            y = s(0, 40 + [3, 2, 0, 1][i] * 26)[1]                     # row order that keeps leaders uncrossed
            col = mix(BG, INK, p)
            d.line([(mx, my), s(236, 0)[0:1] + (y,), s(250, 0)[0:1] + (y,)], fill=col, width=SS)
            d.text((s(252, 0)[0], y), f"CW-PKG9-{i + 1:02d}", font=f, fill=col, anchor="lm")
    sq = phase(t, 3.2, 3.8)                                          # crop snaps to a square
    if sq > 0:
        side = 190 * (0.9 + 0.1 * sq)
        x0, y0 = 70 + (190 - side) / 2, 10 + (190 - side) / 2
        dashed_rect(d, (*s(x0, y0), *s(x0 + side, y0 + side)), ORANGE, 2 * SS, 6 * SS, sq)
    caption(d, "ISO Map: locked iso, colored and tagged by line segment, square")


def scene_overall_map(t, d):
    f = font(9 * SS, True)
    for i in range(24):                                                   # boxes fade in per spool, under the piping
        r, c = divmod(i, 6)
        p = phase(t, 0.6 + i * 0.06, 1.0 + i * 0.06)
        x, y = 130 + c * 42, 20 + r * 44
        if p > 0:
            col = KEY[(r * 2 + c) % 4]
            d.rectangle([*s(x + 2, y - 3), *s(x + 26, y + 37)], fill=mix(BG, col, 0.45 * p), outline=mix(BG, col, p), width=SS)
        d.line([*s(x + 14, y), *s(x + 14, y + 34)], fill=PIPE, width=3 * SS)
        d.line([*s(x + 6, y + 17), *s(x + 22, y + 17)], fill=INK, width=2 * SS)
    lp = phase(t, 2.6, 3.2)
    if lp > 0:                                                            # legend
        for i, col in enumerate(KEY):
            y = 40 + i * 30
            d.rectangle([*s(20, y), *s(100, y + 22)], fill=mix(BG, mix(WHITE, col, 0.55), lp), outline=mix(BG, col, lp), width=SS)
            d.text(s(26, y + 11), f"PKG 6 - {'ABCD'[i]}", font=f, fill=mix(BG, INK, lp), anchor="lm")
    caption(d, "Overall Map: a translucent box per spool, colored by package")


def scene_replace(t, d):
    f = font(10 * SS, True)
    pipe(d, s(20, 40), s(380, 40), 16 * SS)                               # header stays
    for i, x in enumerate((80, 200, 320)):
        swap = phase(t, 1.2 + i * 0.5, 1.7 + i * 0.5)
        if swap < 1:                                                      # old branch fades out
            pipe(d, s(x, 48), s(x, 180), 10 * SS, mix(GHOST, BG, swap))
        if swap > 0:                                                      # correct one drops in, same spot and direction
            y = 30 * (1 - swap)
            col = mix(BG, GREEN, swap)
            pipe(d, s(x, 48 - y), s(x, 180 - y), 10 * SS, col)
            _valve(d, *s(x, 100 - y), 9 * SS, mix(BG, INK, swap))
            d.ellipse([*s(x - 5, 140 - y), *s(x + 5, 150 - y)], fill=mix(BG, ORANGE, swap))   # olet
            d.text(s(x + 16, 165 - y), f"DETAIL {'ABC'[i]}", font=f, fill=mix(BG, INK, swap), anchor="lm")
    caption(d, "Replace Config: swap every matching branch, numbered")


def scene_heal(t, d):
    f = font(9 * SS, True)
    pipe(d, s(20, 60), s(380, 60), 16 * SS)                               # header
    spots = [(90, 66, "connect"), (200, 66, "takeoff"), (310, 140, "elbow")]
    pipe(d, s(90, 68), s(90, 180), 10 * SS)                               # branch end touching a pipe end
    pipe(d, s(200, 70), s(200, 180), 10 * SS)                             # pipe end on the header side
    pipe(d, s(310, 68), s(310, 140), 10 * SS)                             # pipes meeting at an angle
    pipe(d, s(316, 140), s(380, 140), 10 * SS)
    for i, (x, y, label) in enumerate(spots):
        found = phase(t, 0.5 + i * 0.3, 0.9 + i * 0.3)
        fixed = phase(t, 2.4 + i * 0.4, 2.8 + i * 0.4)
        if found > 0 and fixed < 1:
            r = 16
            d.ellipse([*s(x - r, y - r), *s(x + r, y + r)], outline=mix(BG, RED, found * (1 - fixed)), width=3 * SS)
        if fixed > 0:
            d.rectangle([*s(x - 9, y - 7), *s(x + 9, y + 7)], fill=mix(BG, GREEN, fixed))
            d.text(s(x + 14, y + 22), label, font=f, fill=mix(BG, INK, fixed), anchor="lm")
    caption(d, "Connect Disconnects: find touching ends, then join them")


def scene_ez_iso(t, d):
    f = font(11 * SS, True)
    pipe(d, s(20, 60), s(380, 60), 16 * SS)                               # header
    # (branch x, phase-1 tag text, phase-1 tag colour, phase-2 tag text)
    lanes = [(90, "05", PIPE, "05"), (200, "05", RED, "06"), (310, "--", ORANGE, "07")]
    for i, (x, old, c0, new) in enumerate(lanes):
        grow = phase(t, 0.4 + i * 0.3, 0.9 + i * 0.3)                     # branch drops in
        if grow > 0:
            pipe(d, s(x, 68), s(x, 68 + 82 * grow), 10 * SS)
        if grow >= 1:                                                     # tag at the branch end
            p = phase(t, 2.4 + i * 0.2, 2.9 + i * 0.2)                    # renumber to next free line
            tag(d, *s(x - 6, 146, 74, 24), mix(c0, GREEN, p), old if p < 0.5 else new, f)
    ok = phase(t, 3.2, 3.7)                                               # check appears
    if ok > 0:
        check(d, *s(350, 110), 14 * SS * ok, GREEN, 5 * SS)
    caption(d, "Duplicate and unnumbered lines get the next free number")


def scene_iso_views(t, d):
    f = font(9 * SS, True)
    run_a = [(0, 0, 0), (4, 0, 0), (4, 0, 3)]                             # DETAIL A: leg, then rise
    run_b = [(0, 0, 0), (0, 3, 0), (0, 3, 3)]                             # DETAIL B: other leg, then rise

    def place(run, cx, cy, k):                                            # iso points, bbox centred on (cx, cy)
        raw = [_iso(*p, 0, 0, k) for p in run]
        mx = (min(x for x, _ in raw) + max(x for x, _ in raw)) / 2
        my = (min(y for _, y in raw) + max(y for _, y in raw)) / 2
        return [(cx - mx + x, cy - my + y) for x, y in raw]

    def draw_run(pts, col):
        for a, b in zip(pts, pts[1:]):
            pipe(d, s(*a), s(*b), 8 * SS, col)

    gone = phase(t, 2.1, 2.5)                                             # phase-1 labels and the duplicate clear out
    mv = phase(t, 2.4, 3.2)                                               # DETAIL A and B slide into their view frames
    # (run, phase-1 centre x, tag colour, tag text, view frame x0, small tag, dimension text)
    configs = [
        (run_a, 90, PIPE, "DETAIL A", 30, "A-01", "1200"),
        (run_a, 200, PIPE, "DETAIL A", None, None, None),                 # duplicate DETAIL A: dropped
        (run_b, 310, ORANGE, "DETAIL B", 220, "B-01", "900"),
    ]
    for i, (run, cx, col, label, x0, sub, dim_text) in enumerate(configs):
        grow = phase(t, 0.3 + i * 0.35, 1.0 + i * 0.35)
        if grow <= 0:
            continue
        if x0 is None:
            if gone < 1:
                draw_run(place(run, cx, 105, 12), mix(BG, PIPE, grow * (1 - gone)))
            continue
        x = cx + (x0 + 75 - cx) * mv                                      # frame centre is (x0 + 75, 125)
        y = 105 + (125 - 105) * mv
        pts = place(run, x, y, 12 + 8 * mv)
        draw_run(pts, mix(GHOST, PIPE, grow))
        lab = phase(t, 0.9 + i * 0.35, 1.2 + i * 0.35) * (1 - gone)
        if lab > 0:
            tag(d, *s(cx - 35, 142, 70, 18), mix(BG, col, lab), label, f, text_color=mix(BG, WHITE, lab))
        fr = phase(t, 2.4, 3.0)                                           # square view frame with its title
        if fr > 0:
            dashed_rect(d, (*s(x0, 50), *s(x0 + 150, 200)), ORANGE, 2 * SS, 6 * SS, fr)
        if fr > 0.95:
            tag(d, *s(x0 + 10, 41, 118, 18), ORANGE, "ISO - " + label, font(10 * SS, True))
        if t > 3.2:
            tag(d, *s(x0 + 8, 64, 46, 16), col, sub, font(8 * SS, True))  # small tag inside the view
        dp = phase(t, 3.4, 3.9)                                           # dimension line draws along leg 1
        if dp > 0:
            (ax, ay), (bx, by) = pts[0], pts[1]
            L = math.hypot(bx - ax, by - ay) or 1
            ux, uy = (bx - ax) / L, (by - ay) / L
            nx, ny = -uy, ux
            ccx = sum(p[0] for p in pts) / len(pts)
            ccy = sum(p[1] for p in pts) / len(pts)
            if (ccx - (ax + bx) / 2) * nx + (ccy - (ay + by) / 2) * ny > 0:
                nx, ny = -nx, -ny                                         # offset away from the configuration
            off = 12
            Ax, Ay = ax + nx * off, ay + ny * off
            Bx, By = bx + nx * off, by + ny * off
            Ex, Ey = Ax + (Bx - Ax) * dp, Ay + (By - Ay) * dp
            dc = mix(BG, INK, dp)
            for px, py in ((ax, ay), (bx, by)):                           # extension lines
                d.line([s(px + nx * 3, py + ny * 3), s(px + nx * (off + 3), py + ny * (off + 3))], fill=dc, width=SS)
            d.line([s(Ax, Ay), s(Ex, Ey)], fill=dc, width=SS)
            tvx, tvy = (ux + nx) / 1.414, (uy + ny) / 1.414               # 45 degree architectural ticks
            for px, py in ((Ax, Ay), (Bx, By)):
                d.line([s(px - tvx * 3, py - tvy * 3), s(px + tvx * 3, py + tvy * 3)], fill=dc, width=2 * SS)
            for j in (1, 2, 3):                                           # ticks along the leg, revealed as it draws
                if j / 4 <= dp:
                    px, py = Ax + (Bx - Ax) * j / 4, Ay + (By - Ay) * j / 4
                    d.line([s(px - nx * 3, py - ny * 3), s(px + nx * 3, py + ny * 3)], fill=dc, width=SS)
            if dp > 0.99:
                d.text(s((Ax + Bx) / 2 + nx * 9, (Ay + By) / 2 + ny * 9), dim_text, font=font(8 * SS, True),
                       fill=INK, anchor="mm")
    caption(d, "One tagged, dimensioned ISO view per DETAIL type")


def scene_auto_dim(t, d):
    K = 15
    # plan nodes: pipe ends and elbow centres; 1 plan unit = 4 in, so the legs read 2'-6", 1'-4", 3'-0"
    nodes = [(0, 0, 0), (7.5, 0, 0), (7.5, 4, 0), (7.5, 4, 9)]
    raw = [_iso(*p, 0, 0, K) for p in nodes]
    mx = (min(x for x, _ in raw) + max(x for x, _ in raw)) / 2
    my = (min(y for _, y in raw) + max(y for _, y in raw)) / 2

    def sc(p):                                                            # iso point, spool bbox centred on the frame
        x, y = _iso(*p, 0, 0, K)
        return (200 - mx + x, 100 - my + y)

    def along(a, b, k):
        return (a[0] + (b[0] - a[0]) * k, a[1] + (b[1] - a[1]) * k)

    legs = [(nodes[0], nodes[1], "2'-6\""), (nodes[1], nodes[2], "1'-4\""), (nodes[2], nodes[3], "3'-0\"")]
    for i, (a, b, _) in enumerate(legs):                                  # phase 1: spool draws leg by leg
        grow = phase(t, 0.2 + i * 0.5, 0.9 + i * 0.5)
        if grow > 0:
            pa = sc(a)
            pipe(d, s(*pa), s(*along(pa, sc(b), grow)), 10 * SS)
    vp = phase(t, 1.5, 1.9)                                               # valve pops in mid-way along leg B
    if vp > 0:
        pa, pb = sc(nodes[1]), sc(nodes[2])
        vx, vy = (pa[0] + pb[0]) / 2, (pa[1] + pb[1]) / 2
        L = math.hypot(pb[0] - pa[0], pb[1] - pa[1])
        ux, uy = (pb[0] - pa[0]) / L, (pb[1] - pa[1]) / L
        nx, ny = -uy, ux
        r, h = 5 * vp, 9 * vp                                             # bow-tie valve: bases across the pipe
        for sign in (-1, 1):
            bxv, byv = vx + ux * r * sign, vy + uy * r * sign
            d.polygon([s(bxv + nx * h, byv + ny * h), s(bxv - nx * h, byv - ny * h), s(vx, vy)], fill=INK)

    cen = (sum(sc(p)[0] for p in nodes) / len(nodes), sum(sc(p)[1] for p in nodes) / len(nodes))
    for i, (a, b, text) in enumerate(legs):                               # phase 2: one string per leg
        dp = phase(t, 2.2 + i * 0.9, 2.8 + i * 0.9)
        if dp <= 0:
            continue
        pa, pb = sc(a), sc(b)
        L = math.hypot(pb[0] - pa[0], pb[1] - pa[1])
        ux, uy = (pb[0] - pa[0]) / L, (pb[1] - pa[1]) / L
        nx, ny = -uy, ux
        if (cen[0] - (pa[0] + pb[0]) / 2) * nx + (cen[1] - (pa[1] + pb[1]) / 2) * ny > 0:
            nx, ny = -nx, -ny                                             # offset outside the spool
        off = 12
        A = (pa[0] + nx * off, pa[1] + ny * off)
        B = (pb[0] + nx * off, pb[1] + ny * off)
        E = along(A, B, dp)
        dc = mix(BG, INK, dp)
        for px, py in (pa, pb):                                           # extension lines
            d.line([s(px + nx * 3, py + ny * 3), s(px + nx * (off + 3), py + ny * (off + 3))], fill=dc, width=SS)
        d.line([s(*A), s(*E)], fill=dc, width=SS)
        tvx, tvy = (ux + nx) / 1.414, (uy + ny) / 1.414                   # 45 degree architectural ticks
        ticks = [A, B]
        if i == 1:                                                        # valve centre tick on leg B
            vx, vy = (pa[0] + pb[0]) / 2, (pa[1] + pb[1]) / 2
            d.line([s(vx + nx * 3, vy + ny * 3), s(vx + nx * (off + 3), vy + ny * (off + 3))], fill=dc, width=SS)
            ticks.append(((A[0] + B[0]) / 2, (A[1] + B[1]) / 2))
        for px, py in ticks:
            d.line([s(px - tvx * 3, py - tvy * 3), s(px + tvx * 3, py + tvy * 3)], fill=dc, width=2 * SS)
        if dp > 0.99:
            d.text(s((A[0] + B[0]) / 2 + nx * 9, (A[1] + B[1]) / 2 + ny * 9), text,
                   font=font(8 * SS, True), fill=INK, anchor="mm")
    caption(d, "Auto Dimension: centre-to-centre strings on every leg")


def scene_iso_bom(t, d):
    K = 13
    nodes = [(0, 0, 0), (7.5, 0, 0), (7.5, 4, 0), (7.5, 4, 9)]
    raw = [_iso(*p, 0, 0, K) for p in nodes]
    mx = (min(x for x, _ in raw) + max(x for x, _ in raw)) / 2
    my = (min(y for _, y in raw) + max(y for _, y in raw)) / 2
    pts = [(95 - mx + x, 104 - my + y) for x, y in raw]
    for a, b in zip(pts, pts[1:]):
        pipe(d, s(*a), s(*b), 9 * SS)

    rows = [("1", "6'-11\"", "2\"", "PIPE, CS SCH 40"), ("2", "2", "2\"", "ELBOW 90"), ("3", "1", "2\"", "GATE VALVE")]
    # the same table at three sizes: small, medium, large
    sizes = [(1.6, 0.9), (3.0, 1.25), (4.4, 1.6)]
    k = 0 if t < sizes[0][0] else 1 if t < sizes[1][0] else 2
    start = 0.6 if k == 0 else sizes[k - 1][0]
    grow = phase(t, start, start + 0.5)
    prev = sizes[k - 1][1] if k else 0.6
    z = prev + (sizes[k][1] - prev) * grow
    widths = [19, 26, 19, 56]
    rh = 11 * z
    x0, y0 = 180, 104 - rh * (len(rows) + 1) / 2
    col = mix(BG, INK, min(1.0, phase(t, 0.6, 1.0) * 2))
    f, fb = font(int(6 * z * SS)), font(int(6 * z * SS), True)
    x = x0
    for c, w in enumerate(widths):
        w *= z
        d.rectangle([s(x, y0), s(x + w, y0 + rh * (len(rows) + 1))], outline=col, width=SS)
        d.text(s(x + w / 2, y0 + rh / 2), ["ITEM", "QTY", "SIZE", "DESCRIPTION"][c], font=fb, fill=col, anchor="mm")
        d.line([s(x + 2, y0 + rh - 2), s(x + w - 2, y0 + rh - 2)], fill=col, width=SS)
        for r, row in enumerate(rows):
            d.text(s(x + w / 2, y0 + rh * (r + 1.5)), row[c], font=f, fill=col, anchor="mm")
        x += w
    caption(d, "ISO BOM: table drawn in the view" if k == 0 else "Pick a size (or fit it) to suit the sheet")


def scene_auto_tag(t, d):
    f = font(8 * SS, True)
    K = 13

    def pt(x, y, z):
        return s(*_iso(x, y, z, 50, 112, K))

    pipe(d, pt(0, 0, 0), pt(10, 0, 0), 9 * SS, PIPE)                       # header TWCS-PKG6-01
    branches = [(1.5, GREEN), (4, GREEN), (6.5, GREEN), (9, PURPLE)]      # three DETAIL A, then a DETAIL B
    for i, (x, color) in enumerate(branches):
        grow = phase(t, 0.2 + i * 0.25, 0.7 + i * 0.25) if i < 3 else phase(t, 3.0, 3.5)
        if grow > 0:
            pipe(d, pt(x, 0, 0), pt(x, 0, 5 * grow), 7 * SS, color)
    rows = [("TWCS-PKG6-DETAIL A", pt(6.5, 0, 4), GREEN, 1.4), ("TWCS-PKG6-01", pt(8, 0, 0), PIPE, 1.8),
            ("TWCS-PKG6-DETAIL B", pt(9, 0, 4), PURPLE, 3.8)]
    for i, (text, anchor, color, start) in enumerate(rows):               # one tag per unique line number
        p = phase(t, start, start + 0.4)
        if p <= 0:
            continue
        y = s(0, 40 + [0, 2, 1][i] * 30)[1]
        col = mix(BG, INK, p)
        x0 = s(262, 0)[0]
        d.line([anchor, (x0 - 8 * SS, y), (x0, y)], fill=col, width=SS)
        d.rectangle([x0, y - 7 * SS, x0 + 4 * SS, y + 7 * SS], fill=mix(BG, color, p))
        d.text((x0 + 7 * SS, y), text, font=f, fill=col, anchor="lm")
    caption(d, "Auto Tag: one tag per line number; identical branches share one" if t < 3.0 else "A DETAIL B branch adds just one more tag")


SCENES = {
    "assemblies": scene_assemblies,
    "copy_paste": scene_copy_paste,
    "combine": scene_combine,
    "align": scene_align,
    "color": scene_color,
    "olet": scene_olet,
    "copy_olet": scene_copy_olet,
    "iso_map": scene_iso_map,
    "overall_map": scene_overall_map,
    "replace": scene_replace,
    "heal": scene_heal,
    "ez_iso": scene_ez_iso,
    "iso_views": scene_iso_views,
    "auto_dim": scene_auto_dim,
    "iso_bom": scene_iso_bom,
    "auto_tag": scene_auto_tag,
}


def render_clips():
    os.makedirs(TIP_DIR, exist_ok=True)
    for name, scene in SCENES.items():
        with tempfile.TemporaryDirectory() as tmp:
            frames = FPS * SECONDS
            for i in range(frames):
                img, d = canvas()
                scene(i / FPS, d)
                img = img.resize((W, H), Image.LANCZOS)
                img.save(os.path.join(tmp, f"f{i:04d}.png"))
                if i == frames - 1:
                    img.save(os.path.join(TIP_DIR, f"{name}.png"))  # still for the static tooltip
            subprocess.run(
                ["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(FPS), "-i", os.path.join(tmp, "f%04d.png"),
                 "-c:v", "libx264", "-pix_fmt", "yuv420p", "-profile:v", "main", "-crf", "24",
                 "-movflags", "+faststart", os.path.join(TIP_DIR, f"{name}.mp4")],
                check=True)


if __name__ == "__main__":
    render_icons()
    render_clips()
    print("icons ->", ICON_DIR)
    print("clips ->", TIP_DIR)
