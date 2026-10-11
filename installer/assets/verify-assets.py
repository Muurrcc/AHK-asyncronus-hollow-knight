"""Comprueba dimensiones/formato de los recursos."""
import os, sys, struct
from PIL import Image

D = os.path.dirname(os.path.abspath(__file__))
bad = 0
for n, sz in [("banner.bmp", (493, 58)), ("banner-dark.bmp", (493, 58)),
              ("dialog.bmp", (493, 312)), ("dialog-dark.bmp", (493, 312))]:
    p = os.path.join(D, n)
    im = Image.open(p)
    with open(p, "rb") as f:
        bpp = struct.unpack("<H", f.read(30)[28:30])[0]
    ok = im.format == "BMP" and im.size == sz and bpp == 24
    bad += not ok
    print(n, im.format, im.size, bpp, "OK" if ok else "FAIL")
ico = Image.open(os.path.join(D, "asyncronus.ico"))
got = sorted(ico.info.get("sizes", []))
ok = {(s, s) for s in (16, 24, 32, 48, 64, 128, 256)} <= set(got)
bad += not ok
print("asyncronus.ico", got, "OK" if ok else "FAIL")
sys.exit(1 if bad else 0)
