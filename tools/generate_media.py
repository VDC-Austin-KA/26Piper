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


ICONS = {
    "assemblies": icon_assemblies,
    "copy": icon_copy,
    "paste": icon_paste,
    "combine": icon_combine,
    "align": icon_align,
    "color": icon_color,
    "olet": icon_olet,
    "copy_olet": icon_copy_olet,
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


SCENES = {
    "assemblies": scene_assemblies,
    "copy_paste": scene_copy_paste,
    "combine": scene_combine,
    "align": scene_align,
    "color": scene_color,
    "olet": scene_olet,
    "copy_olet": scene_copy_olet,
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
