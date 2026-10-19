"""Генерирует assets/app.ico — иконку EXE: синий огонёк с белой руной Bluetooth.

Тот же рисунок, что IconArt.cs рисует в трее (там цвет круга = состояние).
Каждый размер рисуется отдельно с 4-кратным сглаживанием, у мелких руна толще.
Запуск: python tools/make_icon.py  (нужен Pillow)
"""
from pathlib import Path

from PIL import Image, ImageDraw

BLUE = (30, 120, 230, 255)
RING = tuple(int(c * 0.62) for c in BLUE[:3]) + (255,)
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
RUNE = [(7, 7), (17, 17), (12, 22), (12, 2), (17, 7), (7, 17)]


def render(size: int) -> Image.Image:
    ss = 4
    big = size * ss
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    g = ImageDraw.Draw(img)
    pad = big * 0.03
    ring = max(ss, big * 0.045)
    g.ellipse([pad, pad, big - pad, big - pad], fill=BLUE)
    g.ellipse([pad, pad, big - pad, big - pad], outline=RING, width=round(ring))  # PIL ведёт обводку внутрь

    u = big / 24 * 0.82
    o = (big - 24 * u) / 2
    pts = [(o + x * u, o + y * u) for x, y in RUNE]
    w = round(max(ss * 1.4, big * (0.11 if size <= 20 else 0.085)))
    g.line(pts, fill="white", width=w, joint="curve")
    r = w / 2
    for x, y in (pts[0], pts[-1]):          # круглые концы линии
        g.ellipse([x - r, y - r, x + r, y + r], fill="white")
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    out = Path(__file__).resolve().parent.parent / "assets" / "app.ico"
    out.parent.mkdir(exist_ok=True)
    images = [render(s) for s in SIZES]
    images[-1].save(out, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    images[-1].save(out.with_name("app-256.png"))
    print(out)


if __name__ == "__main__":
    main()
