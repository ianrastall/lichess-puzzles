"""Draws the app icon (a light pawn on a two-by-two board) into src/LichessPuzzles.App/Assets/app.ico."""
from pathlib import Path
from PIL import Image, ImageDraw

S = 1024  # supersampled canvas
OUT = Path(__file__).resolve().parents[1] / "src" / "LichessPuzzles.App" / "Assets" / "app.ico"


def draw(size: int) -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    light, dark = (232, 228, 220, 255), (28, 27, 25, 255)
    board_light, board_dark = (214, 186, 140, 255), (150, 112, 74, 255)
    outline = max(28, S // 22)

    # Board: a rounded square of four squares.
    m, r = S * 0.04, S * 0.12
    d.rounded_rectangle((m, m, S - m, S - m), radius=r, fill=board_dark)
    half = S / 2
    d.rounded_rectangle((m, m, half, half), radius=r, fill=board_light, corners=(True, False, False, False))
    d.rounded_rectangle((half, half, S - m, S - m), radius=r, fill=board_light, corners=(False, False, True, False))

    def pawn(fill, grow):
        g = grow
        d.ellipse((S * 0.39 - g, S * 0.16 - g, S * 0.61 + g, S * 0.38 + g), fill=fill)          # head
        d.polygon([(S * 0.42 - g, S * 0.41 - g), (S * 0.58 + g, S * 0.41 - g),
                   (S * 0.66 + g, S * 0.72 + g), (S * 0.34 - g, S * 0.72 + g)], fill=fill)          # body
        d.rounded_rectangle((S * 0.34 - g, S * 0.37 - g, S * 0.66 + g, S * 0.45 + g), radius=S * 0.03, fill=fill)  # collar
        d.rounded_rectangle((S * 0.24 - g, S * 0.70 - g, S * 0.76 + g, S * 0.86 + g), radius=S * 0.05, fill=fill)  # base

    pawn(dark, outline)
    pawn(light, 0)
    return img.resize((size, size), Image.LANCZOS)


sizes = [16, 20, 24, 32, 40, 48, 64, 256]
images = [draw(s) for s in sizes]
images[-1].save(OUT, format="ICO", sizes=[(s, s) for s in sizes], append_images=images[:-1])
print(OUT)
