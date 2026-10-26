"""生成托盘 / 程序图标与 README 配图（盾牌 + 闪电）。用法：python3 tools/gen-icons.py（需要 Pillow）。"""
from pathlib import Path
from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "src" / "PowerGuard.App" / "Assets"
COLORS = {"ok": "#005FB8", "warn": "#C27C0E", "bad": "#C42B1C", "paused": "#6B6B6B"}
S = 256


def shield(color: str) -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    # 盾牌轮廓：上沿平直、下端尖
    pts = [(128, 10), (232, 46), (226, 140), (190, 200), (128, 246), (66, 200), (30, 140), (24, 46)]
    d.polygon(pts, fill=color)
    # 闪电
    bolt = [(146, 44), (78, 140), (122, 140), (104, 214), (178, 108), (132, 108), (152, 44)]
    d.polygon(bolt, fill="white")
    return img


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
    for name, color in COLORS.items():
        shield(color).save(OUT / f"tray-{name}.ico", sizes=sizes)
    shield(COLORS["ok"]).save(OUT / "app.ico", sizes=sizes)
    # README 用图
    docs = OUT.parent.parent.parent / "docs" / "images"
    docs.mkdir(parents=True, exist_ok=True)
    shield(COLORS["ok"]).resize((192, 192), Image.LANCZOS).save(docs / "logo.png")
    strip = Image.new("RGBA", (4 * 72 - 8, 64), (0, 0, 0, 0))
    for i, name in enumerate(["ok", "warn", "bad", "paused"]):
        strip.paste(shield(COLORS[name]).resize((64, 64), Image.LANCZOS), (i * 72, 0))
    strip.save(docs / "tray-states.png")


if __name__ == "__main__":
    main()
