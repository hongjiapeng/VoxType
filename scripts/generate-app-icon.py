"""Generate VoxType's original vector mark, multi-resolution ICO and MSIX artwork.

Requires Pillow. No downloaded fonts, stock artwork or raster tracing.
Run from any directory: python scripts/generate-app-icon.py
"""
from pathlib import Path
from PIL import Image, ImageDraw

ASSETS = Path(__file__).resolve().parents[1] / "src" / "VoxType.App" / "Assets"
SVG = '''<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <title>VoxType — voice to text</title>
  <defs><linearGradient id="teal" x2=".8" y2="1"><stop stop-color="#17B7AF"/><stop offset="1" stop-color="#087F8C"/></linearGradient></defs>
  <rect x="8" y="8" width="240" height="240" rx="56" fill="url(#teal)"/>
  <rect x="88" y="52" width="48" height="88" rx="24" fill="white"/>
  <path d="M64 118v8a48 48 0 0 0 96 0v-8M112 174v24M88 198h48M176 74h16m-8 0v54m-8 0h16"
        fill="none" stroke="white" stroke-width="10" stroke-linecap="round" stroke-linejoin="round"/>
</svg>'''


def mark(size: int) -> Image.Image:
    # Render well above the final resolution; the ICO's small entries remain crisp.
    side = max(1024, size * 4)
    scale = side / 256
    tile = Image.new("RGBA", (side, side))
    draw = ImageDraw.Draw(tile)
    for y in range(side):
        t = y / max(1, side - 1)
        color = tuple(round(a + (b - a) * t) for a, b in zip((23, 183, 175), (8, 127, 140)))
        draw.line((0, y, side, y), fill=(*color, 255))
    mask = Image.new("L", (side, side))
    ImageDraw.Draw(mask).rounded_rectangle(tuple(round(v * scale) for v in (8, 8, 248, 248)), radius=56 * scale, fill=255)
    tile.putalpha(mask)
    draw = ImageDraw.Draw(tile)
    offset = 16 if size < 32 else 0

    def box(values):
        return tuple(round((v + (offset if index % 2 == 0 else 0)) * scale) for index, v in enumerate(values))

    def line(points, width=10):
        pixels = [(round((x + offset) * scale), round(y * scale)) for x, y in points]
        stroke = round(width * scale)
        draw.line(pixels, fill="white", width=stroke, joint="curve")
        radius = stroke / 2
        for x, y in (pixels[0], pixels[-1]):
            draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill="white")

    draw.rounded_rectangle(box((88, 52, 136, 140)), radius=24 * scale, fill="white")
    draw.arc(box((64, 78, 160, 174)), 0, 180, fill="white", width=round(10 * scale))
    line([(64, 118), (64, 126)])
    line([(160, 118), (160, 126)])
    line([(112, 174), (112, 198)])
    line([(88, 198), (136, 198)])
    # Optical simplification for 16–24px tray/taskbar icons.
    if size >= 32:
        line([(176, 74), (192, 74)], 8)
        line([(184, 74), (184, 128)], 8)
        line([(176, 128), (192, 128)], 8)
    return tile.resize((size, size), Image.Resampling.LANCZOS)


def centered(width: int, height: int, symbol: int) -> Image.Image:
    canvas = Image.new("RGBA", (width, height))
    canvas.alpha_composite(mark(symbol), ((width - symbol) // 2, (height - symbol) // 2))
    return canvas


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    (ASSETS / "VoxType.svg").write_text(SVG, encoding="utf-8")
    mark(256).save(ASSETS / "VoxType.png")
    sizes = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    images = [mark(size) for size in sizes]
    images[-1].save(ASSETS / "AppIcon.ico", format="ICO", sizes=[(size, size) for size in sizes], append_images=images[:-1])
    for name, width, height, symbol in (
        ("Square44x44Logo.scale-200.png", 88, 88, 80),
        ("Square150x150Logo.scale-200.png", 300, 300, 224),
        ("Square44x44Logo.targetsize-24_altform-unplated.png", 24, 24, 24),
        ("Square44x44Logo.targetsize-48_altform-lightunplated.png", 48, 48, 48),
        ("LockScreenLogo.scale-200.png", 48, 48, 40),
        ("StoreLogo.png", 50, 50, 46),
        ("Wide310x150Logo.scale-200.png", 620, 300, 224),
        ("SplashScreen.scale-200.png", 1240, 600, 256),
    ):
        centered(width, height, symbol).save(ASSETS / name)
    preview = Image.new("RGB", (640, 320), "#eef1f2")
    preview.paste(mark(256), (24, 32), mark(256))
    for x, size in ((344, 64), (440, 48), (520, 32), (576, 16)):
        icon = mark(size)
        preview.paste(icon, (x, (320 - size) // 2), icon)
    preview_path = ASSETS.parents[2] / "ui-test-artifacts" / "app-icon-preview.png"
    preview_path.parent.mkdir(parents=True, exist_ok=True)
    preview.save(preview_path)
    print("Generated SVG, ICO (10 sizes), and 8 MSIX image assets.")


if __name__ == "__main__":
    main()
