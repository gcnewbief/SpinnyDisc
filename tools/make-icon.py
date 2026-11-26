#!/usr/bin/env py
"""Generate SpinnyDisc mascot: CD with laser probe, VoltCheck palette."""
from PIL import Image, ImageDraw
import math, os

OUT = os.path.join(os.path.dirname(__file__), "..", "src", "SpinnyDisc", "Assets")
os.makedirs(OUT, exist_ok=True)

S = 1024  # supersample
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

CX, CY, R = S // 2, S // 2, int(S * 0.44)
HOLE_R = int(S * 0.115)
RING_R = int(S * 0.17)

def rgba(h, a=255):
    h = h.lstrip('#')
    return tuple(int(h[i:i+2], 16) for i in (0, 2, 4)) + (a,)

# --- disc body: concentric groove rings, dark metal-green ---
for r in range(R, RING_R, -2):
    t = r / R
    sheen = 0.5 + 0.5 * math.sin(t * 60)
    base = 26 + int(34 * t) + int(14 * sheen)
    col = (base // 3, base + 20, base // 2 + 12)
    d.ellipse([CX - r, CY - r, CX + r, CY + r], fill=col + (255,))

# rim highlight
d.ellipse([CX - R, CY - R, CX + R, CY + R], outline=rgba("3f7d52"), width=4)
d.ellipse([CX - R + 6, CY - R + 6, CX + R - 6, CY + R - 6], outline=(90, 160, 110, 90), width=3)

# --- specular sweep: teal sheen wedge, top-left ---
sweep = Image.new("RGBA", (S, S), (0, 0, 0, 0))
sd = ImageDraw.Draw(sweep)
sd.pieslice([CX - R, CY - R, CX + R, CY + R], 195, 265, fill=rgba("69f0ae", 70))
sd.pieslice([CX - int(R*0.82), CY - int(R*0.82), CX + int(R*0.82), CY + int(R*0.82)], 200, 258, fill=rgba("d9ffe4", 60))
mask = Image.new("L", (S, S), 0)
md = ImageDraw.Draw(mask)
md.ellipse([CX - R, CY - R, CX + R, CY + R], fill=255)
md.ellipse([CX - RING_R, CY - RING_R, CX + RING_R, CY + RING_R], fill=0)
img.paste(sweep, (0, 0), Image.composite(sweep.split()[3], Image.new("L", (S, S), 0), mask))

# --- hub ring + spindle hole ---
d.ellipse([CX - RING_R, CY - RING_R, CX + RING_R, CY + RING_R], fill=rgba("1d2b1d"), outline=rgba("3f7d52"), width=3)
d.ellipse([CX - HOLE_R, CY - HOLE_R, CX + HOLE_R, CY + HOLE_R], fill=rgba("0a0e0a"), outline=rgba("6f8f76"), width=3)

# --- laser probe housing, bottom right ---
px, py, pw, ph = int(S*0.62), int(S*0.74), int(S*0.30), int(S*0.16)
d.rounded_rectangle([px, py, px + pw, py + ph], radius=int(S*0.03),
                    fill=rgba("101510"), outline=rgba("1b5e20"), width=4)
# lens barrel
lx, ly, lr = px + int(pw*0.18), py + int(ph*0.5), int(S*0.035)
d.ellipse([lx - lr*2, ly - lr*2, lx + lr*2, ly + lr*2], fill=rgba("1d2b1d"), outline=rgba("69f0ae"), width=3)
d.ellipse([lx - lr, ly - lr, lx + lr, ly + lr], fill=rgba("0a0e0a"))

# --- laser beam: lens -> disc surface ---
target = (int(S*0.40), int(S*0.62))
beam_glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
bd = ImageDraw.Draw(beam_glow)
bd.line([lx, ly, *target], fill=rgba("00e676", 60), width=int(S*0.028))
bd.line([lx, ly, *target], fill=rgba("00e676", 120), width=int(S*0.012))
img = Image.alpha_composite(img, beam_glow)
d = ImageDraw.Draw(img)
d.line([lx, ly, *target], fill=rgba("b9ffc9"), width=max(2, int(S*0.005)))
# impact glow on the disc
gx, gy, gr = target[0], target[1], int(S*0.035)
for rr, aa in ((gr, 50), (int(gr*0.6), 110), (int(gr*0.3), 200)):
    d.ellipse([gx-rr, gy-rr, gx+rr, gy+rr], fill=rgba("00e676", aa))

# scan ticks on the probe housing (activity indicator)
for i in range(3):
    x0 = px + pw - int(S*0.02) - i*int(S*0.035)
    d.line([x0, py + int(ph*0.3), x0, py + int(ph*0.7)], fill=rgba("00e676", 140 - i*40), width=3)

def emit(size, name):
    img.resize((size, size), Image.LANCZOS).save(os.path.join(OUT, name))

for size in (256, 128, 64, 48, 32, 16):
    emit(size, f"mascot-{size}.png")
emit(256, "mascot.png")

# multi-res ICO for exe + window icon
img.save(os.path.join(OUT, "SpinnyDisc.ico"),
         sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("icons written to", os.path.abspath(OUT))
