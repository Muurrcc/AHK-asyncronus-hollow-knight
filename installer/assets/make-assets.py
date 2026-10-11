"""Generates the Asyncronus installer graphics (Pillow). Usage: python make-assets.py
Original shapes (no Team Cherry artwork). Reproducible: fixed seeds."""
import math, random, os
from PIL import Image, ImageDraw, ImageFilter, ImageChops

OUT = os.path.dirname(os.path.abspath(__file__))
VOID = (6, 8, 12); DEEP = (14, 20, 29); MIST = (34, 48, 62); BLUE = (143, 163, 181)
BONE = (232, 226, 208); SOUL = (207, 232, 245); PAPER = (236, 232, 220)
S = 4  # supersampling


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def vgrad(w, h, top, bot):
    g = Image.new("RGB", (1, h))
    for y in range(h):
        g.putpixel((0, y), lerp(top, bot, y / max(1, h - 1)))
    return g.resize((w, h))


def add_fog(img, seed, color, strength, scale):
    random.seed(seed)
    w, h = img.size
    sw, sh = max(2, w // scale), max(2, h // scale)
    n = Image.new("L", (sw, sh))
    n.putdata([random.randint(0, 255) for _ in range(sw * sh)])
    n = n.resize((w, h), Image.BICUBIC).filter(ImageFilter.GaussianBlur(scale * 0.8))
    n = n.point(lambda v: int(max(0, v - 90) * strength))
    return Image.composite(Image.new("RGB", (w, h), color), img, n)


def glow(img, cx, cy, r, color, a=1.0):
    m = Image.new("L", img.size, 0)
    ImageDraw.Draw(m).ellipse((cx - r, cy - r, cx + r, cy + r), fill=int(255 * a))
    m = m.filter(ImageFilter.GaussianBlur(r / 2))
    return Image.composite(Image.new("RGB", img.size, color), img, m)


def shell_polys(cx, cy, w, h):
    """Mascara original: ovoide con dos puntas superiores asimetricas."""
    pts = []
    for i in range(0, 361, 4):
        t = math.radians(i)
        x = math.sin(t) * w / 2
        y = -math.cos(t) * h / 2
        if y > 0:
            x *= 1 - 0.25 * (y / (h / 2))
        pts.append((cx + x, cy + y * 0.9 + h * 0.05))
    tl = [(cx - w * 0.40, cy - h * 0.30), (cx - w * 0.52, cy - h * 0.66), (cx - w * 0.22, cy - h * 0.44)]
    tr = [(cx + w * 0.22, cy - h * 0.44), (cx + w * 0.52, cy - h * 0.66), (cx + w * 0.40, cy - h * 0.30)]
    return [pts, tl, tr]


def shell(d, cx, cy, w, h, fill):
    for p in shell_polys(cx, cy, w, h):
        d.polygon(p, fill=fill)


def shell_face(d, cx, cy, w, h, eye):
    """Ojos almendrados inclinados + fisura (sobre la mascara)."""
    for sgn in (-1, 1):
        ex = cx + sgn * w * 0.19
        ey = cy + h * 0.02
        d.polygon([(ex - sgn * w * 0.14, ey + h * 0.10), (ex + sgn * w * 0.12, ey - h * 0.04),
                   (ex + sgn * w * 0.07, ey + h * 0.06), (ex - sgn * w * 0.10, ey + h * 0.16)], fill=eye)
    d.line([(cx, cy - h * 0.40), (cx - w * 0.03, cy - h * 0.1)], fill=eye, width=max(1, int(w * 0.02)))


def mask_on(img, cx, cy, w, h, body, eye):
    m = Image.new("L", img.size, 0)
    shell(ImageDraw.Draw(m), cx, cy, w, h, 255)
    out = Image.composite(Image.new("RGB", img.size, body), img, m)
    shell_face(ImageDraw.Draw(out), cx, cy, w, h, eye)
    return out, m


def echo_on(img, cx, cy, w, h, color, alpha, exclude=None):
    e = Image.new("L", img.size, 0)
    shell(ImageDraw.Draw(e), cx, cy, w, h, alpha)
    if exclude is not None:
        e = ImageChops.subtract(e, exclude)
    e = e.filter(ImageFilter.GaussianBlur(S))
    return Image.composite(Image.new("RGB", img.size, color), img, e)


def icon_image(size=256):
    W = size * S
    img = vgrad(W, W, DEEP, VOID)
    img = glow(img, W * 0.5, W * 0.45, W * 0.42, MIST, 0.9)
    img = add_fog(img, 3, BLUE, 0.5, W // 8)
    img = echo_on(img, W * 0.60, W * 0.55, W * 0.46, W * 0.54, BLUE, 150)
    img, _ = mask_on(img, W * 0.42, W * 0.54, W * 0.50, W * 0.58, BONE, VOID)
    img = glow(img, W * 0.80, W * 0.20, W * 0.05, SOUL, 0.9)
    ImageDraw.Draw(img).ellipse((W * 0.785, W * 0.185, W * 0.815, W * 0.215), fill=(255, 255, 255))
    mask = Image.new("L", (W, W), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, W - 1, W - 1), radius=int(W * 0.18), fill=255)
    out = img.convert("RGBA")
    out.putalpha(mask)
    return out.resize((size, size), Image.LANCZOS)


def stalactites(img, seed, count, maxh, color, bottom=False):
    W, H = img.size
    random.seed(seed)
    d = ImageDraw.Draw(img)
    x = -W * 0.02
    step = W / count
    for _ in range(count + 2):
        bw = step * random.uniform(0.5, 1.1)
        hh = maxh * random.uniform(0.25, 1)
        tip = x + bw / 2 + random.uniform(-bw * 0.15, bw * 0.15)
        if bottom:
            d.polygon([(x, H), (tip, H - hh), (x + bw, H)], fill=color)
        else:
            d.polygon([(x, 0), (tip, hh), (x + bw, 0)], fill=color)
        x += step * random.uniform(0.7, 1.0)


def banner(path, dark):
    w, h = 493, 58
    W, H = w * S, h * S
    if dark:
        img = vgrad(W, H, DEEP, VOID)
        img = add_fog(img, 11, BLUE, 0.4, H // 2)
        body, eye, spark, line = BONE, VOID, SOUL, BLUE
    else:
        img = vgrad(W, H, PAPER, (205, 208, 200))
        img = add_fog(img, 12, (255, 255, 255), 0.7, H // 2)
        body, eye, spark, line = (52, 66, 82), PAPER, (255, 255, 255), (110, 128, 146)
    img = echo_on(img, W * 0.955, H * 0.60, H * 0.62, H * 0.70, BLUE, 120)
    img, _ = mask_on(img, W * 0.93, H * 0.56, H * 0.62, H * 0.70, body, eye)
    img = glow(img, W * 0.84, H * 0.30, H * 0.09, spark, 0.9)
    ImageDraw.Draw(img).rectangle((0, H - S * 2, W, H), fill=line)
    img.resize((w, h), Image.LANCZOS).save(path, "BMP")


def dialog(path, dark):
    w, h = 493, 312
    W, H = w * S, h * S
    pw = 164 * S
    if dark:
        img = vgrad(W, H, DEEP, VOID)
        img = add_fog(img, 21, BLUE, 0.25, H // 4)
    else:
        img = Image.new("RGB", (W, H), PAPER)
    panel = vgrad(W, H, DEEP, VOID)
    panel = glow(panel, pw * 0.5, H * 0.32, pw * 1.0, MIST, 0.8)
    panel = add_fog(panel, 5, BLUE, 0.45, H // 5)
    stalactites(panel, 7, 7, H * 0.30, VOID)
    stalactites(panel, 8, 5, H * 0.12, (10, 14, 20), bottom=True)
    panel = echo_on(panel, pw * 0.66, H * 0.60, pw * 0.60, H * 0.30, BLUE, 110)
    panel, _ = mask_on(panel, pw * 0.50, H * 0.56, pw * 0.70, H * 0.34, BONE, VOID)
    random.seed(9)
    for _ in range(14):
        x = random.uniform(0.1, 0.9) * pw
        y = random.uniform(0.08, 0.85) * H
        panel = glow(panel, x, y, random.uniform(2, 5) * S, SOUL, 0.7)
    img.paste(panel.crop((0, 0, pw, H)), (0, 0))
    ImageDraw.Draw(img).rectangle((pw - S, 0, pw, H), fill=BLUE)
    img.resize((w, h), Image.LANCZOS).save(path, "BMP")


if __name__ == "__main__":
    banner(os.path.join(OUT, "banner.bmp"), dark=False)
    banner(os.path.join(OUT, "banner-dark.bmp"), dark=True)
    dialog(os.path.join(OUT, "dialog.bmp"), dark=False)
    dialog(os.path.join(OUT, "dialog-dark.bmp"), dark=True)
    big = icon_image(256)
    big.save(os.path.join(OUT, "asyncronus.ico"), format="ICO",
             sizes=[(s, s) for s in (16, 24, 32, 48, 64, 128, 256)])
    big.save(os.path.join(OUT, "asyncronus-256.png"))
    print("ok")
