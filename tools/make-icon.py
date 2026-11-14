#!/usr/bin/env python3
"""生成 PCL Lite 图标与启动闪屏图（采用 pcl-LIGHT 白底黑字光影风格）。

任务栏主图标（icon.ico）使用单个大写「L」；
启动闪屏（splash.png）、应用内图标（icon.png）与文档展示图标（docs/assets/icon-*.png）使用「LITE」。

用法：
    python tools/make-icon.py                  # 只生成 splash.png
    python tools/make-icon.py --write-ico      # 同时重画 icon.ico、icon.png 与 docs/assets/icon-*.png
    python tools/make-icon.py --preview a.png  # 只出预览
"""

from __future__ import annotations

import argparse
import os

from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
IMAGES = os.path.join(ROOT, "Plain Craft Launcher 2", "Images")
DOCS_ASSETS = os.path.join(ROOT, "docs", "assets")
ICON_PATH = os.path.join(IMAGES, "icon.ico")
ICON_PNG_PATH = os.path.join(IMAGES, "icon.png")
SPLASH_PATH = os.path.join(IMAGES, "splash.png")

# 白色底图，左上方更亮、右下方受光较少；文字使用深黑。
TILE_TOP = (255, 255, 255)
TILE_BOTTOM = (229, 232, 238)
TEXT = (12, 14, 18, 255)

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
DOCS_SIZES = [256, 512, 1024]

FONT_BOLD = r"C:\Windows\Fonts\segoeuib.ttf"
FONT_LABEL = r"C:\Windows\Fonts\msyhbd.ttc"

TILE_RADIUS_RATIO = 0.22      # 与原图标一致
WORDMARK_TEXT = "LITE"
WORDMARK_WIDTH_RATIO = 0.68
ICO_TEXT = "L"
ICO_HEIGHT_RATIO = 0.54

# 闪屏：原来按 16px 开窗，放大 5 倍
SPLASH_BASE_SIZE = 16
SPLASH_SCALE = 5.0
SPLASH_SIZE = int(SPLASH_BASE_SIZE * SPLASH_SCALE)


def _supersample_for(size: int) -> int:
    return 4 if size >= 512 else 8


def _rounded_tile(size: int) -> Image.Image:
    """白色圆角底图，光线从左上方照入，阴影落向右下方。"""
    w = size * _supersample_for(size)
    grad = Image.new("RGBA", (1, w))
    for y in range(w):
        t = y / max(1, w - 1)
        grad.putpixel((0, y), (
            round(TILE_TOP[0] + (TILE_BOTTOM[0] - TILE_TOP[0]) * t),
            round(TILE_TOP[1] + (TILE_BOTTOM[1] - TILE_TOP[1]) * t),
            round(TILE_TOP[2] + (TILE_BOTTOM[2] - TILE_TOP[2]) * t),
            255,
        ))
    grad = grad.resize((w, w))

    mask = Image.new("L", (w, w), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, w - 1, w - 1], radius=int(w * TILE_RADIUS_RATIO), fill=255
    )

    pad = max(2, w // 22)
    # 左上方柔光从圆角块内部透出，右下方投影强化受光方向。
    glow = Image.new("RGBA", (w, w), (0, 0, 0, 0))
    glow_mask = mask.filter(ImageFilter.GaussianBlur(max(2, w // 24)))
    glow.paste((255, 255, 255, 150), (0, 0), glow_mask)

    shadow_mask = Image.new("L", (w, w), 0)
    shadow_mask.paste(mask, (max(2, w // 32), max(2, w // 26)))
    shadow_mask = shadow_mask.filter(ImageFilter.GaussianBlur(max(3, w // 26)))
    shadow = Image.new("RGBA", (w, w), (24, 30, 42, 0))
    shadow.putalpha(shadow_mask.point(lambda p: round(p * 0.62)))

    tile = Image.new("RGBA", (w, w), (0, 0, 0, 0))
    tile.alpha_composite(glow)
    tile.alpha_composite(shadow)
    tile.paste(grad, (0, 0), mask)

    # 左上角高光斑强化光源方向，蒙版限制在白色主体内。
    highlight = Image.new("RGBA", (w, w), (0, 0, 0, 0))
    highlight_draw = ImageDraw.Draw(highlight)
    highlight_draw.ellipse((pad - w * 0.12, pad - w * 0.16,
                            pad + w * 0.68, pad + w * 0.60), fill=(255, 255, 255, 145))
    highlight = highlight.filter(ImageFilter.GaussianBlur(max(2, w // 11)))
    transparent = Image.new("RGBA", (w, w), (0, 0, 0, 0))
    tile.alpha_composite(Image.composite(highlight, transparent, mask))

    # 细边缘高光把白色主体从透明背景中分离出来。
    edge = Image.new("RGBA", (w, w), (0, 0, 0, 0))
    edge_draw = ImageDraw.Draw(edge)
    edge_draw.rounded_rectangle([1, 1, w - 2, w - 2], radius=int(w * TILE_RADIUS_RATIO),
                                outline=(255, 255, 255, 230), width=max(1, w // 90))
    tile.alpha_composite(edge)
    return tile


def _font_for_width(text: str, target_width: float) -> ImageFont.FreeTypeFont:
    """二分找到让文字宽度最接近 target_width 的字号。"""
    lo, hi = 8, 4000
    best = ImageFont.truetype(FONT_BOLD, lo)
    while lo <= hi:
        mid = (lo + hi) // 2
        font = ImageFont.truetype(FONT_BOLD, mid)
        left, _, right, _ = font.getbbox(text)
        if right - left < target_width:
            lo = mid + 1
            best = font
        else:
            hi = mid - 1
    return best


def _font_for_height(text: str, target_height: float) -> ImageFont.FreeTypeFont:
    """二分找到让单个字符高度最接近 target_height 的字号。"""
    lo, hi = 8, 4000
    best = ImageFont.truetype(FONT_BOLD, lo)
    while lo <= hi:
        mid = (lo + hi) // 2
        font = ImageFont.truetype(FONT_BOLD, mid)
        _, top, _, bottom = font.getbbox(text)
        if bottom - top < target_height:
            lo = mid + 1
            best = font
        else:
            hi = mid - 1
    return best


def _draw_centered(tile: Image.Image, text: str, font: ImageFont.FreeTypeFont) -> None:
    draw = ImageDraw.Draw(tile)
    left, top, right, bottom = draw.textbbox((0, 0), text, font=font)
    w, h = tile.size
    # 黑色字样把阴影投向右下方，保持白底上的高对比度。
    x = (w - (right - left)) / 2 - left
    y = (h - (bottom - top)) / 2 - top + h * 0.018
    shadow = Image.new("RGBA", tile.size, (0, 0, 0, 0))
    shadow_draw = ImageDraw.Draw(shadow)
    shadow_draw.text((x + w * 0.014, y + h * 0.02), text, font=font,
                     fill=(22, 26, 33, 120), stroke_width=max(1, w // 180),
                     stroke_fill=(22, 26, 33, 100))
    shadow = shadow.filter(ImageFilter.GaussianBlur(max(1, w // 60)))
    tile.alpha_composite(shadow)
    draw.text(
        (x, y), text, font=font, fill=TEXT,
        stroke_width=max(1, w // 260), stroke_fill=(255, 255, 255, 220),
    )


def render(size: int, wordmark: bool = True) -> Image.Image:
    """按目标尺寸单独排版后渲染，不是拿大图直接缩，小尺寸才不会糊。"""
    tile = _rounded_tile(size)
    if wordmark:
        font = _font_for_width(WORDMARK_TEXT, tile.size[0] * WORDMARK_WIDTH_RATIO)
        _draw_centered(tile, WORDMARK_TEXT, font)
    else:
        font = _font_for_height(ICO_TEXT, tile.size[0] * ICO_HEIGHT_RATIO)
        _draw_centered(tile, ICO_TEXT, font)
    return tile.resize((size, size), Image.LANCZOS)


def build_ico_frames() -> dict[int, Image.Image]:
    return {size: render(size, wordmark=False) for size in SIZES}


def write_ico(images: dict[int, Image.Image], path: str) -> None:
    images[256].save(
        path,
        format="ICO",
        sizes=[(s, s) for s in SIZES],
        append_images=[images[s] for s in SIZES],
    )


def write_preview(path: str) -> None:
    sheet = Image.new("RGB", (760, 420), (246, 247, 250))
    draw = ImageDraw.Draw(sheet)
    label = ImageFont.truetype(FONT_LABEL, 13)
    for row, (title_prefix, is_wordmark) in enumerate([("LITE", True), ("L", False)]):
        ry = 18 + row * 198
        for x, bg, bg_name in [(18, (245, 246, 248), "浅色桌面"),
                               (390, (30, 34, 42), "深色桌面")]:
            draw.rounded_rectangle([x, ry, x + 350, ry + 186], radius=18, fill=bg)
            draw.text((x + 16, ry + 12), f"{title_prefix} - {bg_name}", font=label,
                      fill=(120, 128, 140) if sum(bg) > 500 else (200, 206, 216))
            for i, size in enumerate([32, 48, 64, 96]):
                im = render(size, wordmark=is_wordmark)
                px = x + 25 + i * 78
                py = ry + 52 + (96 - size) // 2
                sheet.paste(im, (px, py), im)
                draw.text((px + 5, ry + 158), str(size), font=label,
                          fill=(120, 128, 140) if sum(bg) > 500 else (200, 206, 216))
    sheet.save(path)


def main() -> None:
    parser = argparse.ArgumentParser(description="生成启动闪屏图 / 图标")
    parser.add_argument("--write-ico", action="store_true", help="同时重画 icon.ico、icon.png 与 docs/assets/icon-*.png")
    parser.add_argument("--preview", help="只输出预览图到指定路径")
    args = parser.parse_args()

    if args.preview:
        write_preview(args.preview)
        print(f"preview -> {args.preview}")
        return

    render(SPLASH_SIZE, wordmark=True).save(SPLASH_PATH)
    print(f"splash -> {SPLASH_PATH} ({SPLASH_SIZE}x{SPLASH_SIZE})")

    if args.write_ico:
        write_ico(build_ico_frames(), ICON_PATH)
        print(f"ico -> {ICON_PATH} ({os.path.getsize(ICON_PATH)} bytes, {len(SIZES)} sizes)")

        icon_256 = render(256, wordmark=True)
        icon_256.save(ICON_PNG_PATH, format="PNG")
        print(f"png -> {ICON_PNG_PATH}")

        os.makedirs(DOCS_ASSETS, exist_ok=True)
        for doc_size in DOCS_SIZES:
            doc_path = os.path.join(DOCS_ASSETS, f"icon-{doc_size}.png")
            im = icon_256 if doc_size == 256 else render(doc_size, wordmark=True)
            im.save(doc_path, format="PNG")
            print(f"docs -> {doc_path} ({doc_size}x{doc_size})")


if __name__ == "__main__":
    main()
