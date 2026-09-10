"""Generate src/AltHenkan/AltHenkan.ico: two keycaps, "A" (grey, IME off) and "あ" (blue, IME on).

Requires Pillow (pip install pillow) and the Windows fonts Segoe UI Bold and Yu Gothic Bold.
Rendered at 1024 px and downsampled per size so the small sizes stay crisp.

    python scripts\\make-icon.py            # writes the .ico
    python scripts\\make-icon.py preview.png  # also writes a preview sheet
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFont

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_ICO = os.path.join(REPO_ROOT, 'src', 'AltHenkan', 'AltHenkan.ico')
OUT_PNG = sys.argv[1] if len(sys.argv) > 1 else None
FONT_LATIN = r'C:\Windows\Fonts\segoeuib.ttf'
FONT_KANA = r'C:\Windows\Fonts\YuGothB.ttc'

GREY = (96, 100, 108, 255)
GREY_DARK = (70, 74, 82, 255)
BLUE = (0, 120, 212, 255)
BLUE_DARK = (0, 90, 165, 255)
WHITE = (255, 255, 255, 255)


def keycap(draw, box, fill, shadow, radius):
    x0, y0, x1, y1 = box
    # Shadow / bottom edge first, then the cap face slightly raised.
    draw.rounded_rectangle((x0, y0 + radius * 0.35, x1, y1), radius=radius, fill=shadow)
    draw.rounded_rectangle((x0, y0, x1, y1 - radius * 0.35), radius=radius, fill=fill)


def render(size):
    scale = 1024
    img = Image.new('RGBA', (scale, scale), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    margin = 40
    gap = 48
    cap_w = (scale - 2 * margin - gap) // 2
    top = 128
    bottom = scale - 128
    radius = 110

    left = (margin, top, margin + cap_w, bottom)
    right = (margin + cap_w + gap, top, margin + cap_w + gap + cap_w, bottom)

    keycap(d, left, GREY, GREY_DARK, radius)
    keycap(d, right, BLUE, BLUE_DARK, radius)

    f_latin = ImageFont.truetype(FONT_LATIN, 520)
    f_kana = ImageFont.truetype(FONT_KANA, 500)

    def center_text(box, text, font, dy=0):
        x0, y0, x1, y1 = box
        cx = (x0 + x1) / 2
        cy = (y0 + y1 - radius * 0.35) / 2 + dy
        d.text((cx, cy), text, font=font, fill=WHITE, anchor='mm')

    center_text(left, 'A', f_latin, dy=-10)
    center_text(right, 'あ', f_kana, dy=-10)

    return img.resize((size, size), Image.LANCZOS)


sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
frames = [render(s) for s in sizes]
frames[-1].save(OUT_ICO, format='ICO', sizes=[(s, s) for s in sizes],
                append_images=frames[:-1])
print('wrote', OUT_ICO)

if OUT_PNG:
    # Preview sheet: 256, 48, 32, 16 side by side on a light background.
    sheet = Image.new('RGBA', (256 + 48 + 32 + 16 + 5 * 16, 256 + 32), (240, 240, 240, 255))
    x = 16
    for s in (256, 48, 32, 16):
        fr = next(f for f in frames if f.size[0] == s)
        sheet.alpha_composite(fr, (x, 16 + (256 - s) // 2))
        x += s + 16
    sheet.save(OUT_PNG)
    print('wrote', OUT_PNG)
