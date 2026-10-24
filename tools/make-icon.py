#!/usr/bin/env python3
"""生成启动闪屏图（splash.png）。

问题背景：WPF 的 SplashScreen 按「图片第一帧的像素尺寸」开窗。
原来闪屏直接指向 icon.ico，而 ico 的第一帧是 16px，
于是闪屏窗口只有 16 DIP —— 125% 缩放下实测 13×13，
在 1080p 屏幕上就是个点，用户形容「以为是蚂蚁」。

这里单独渲染一张 splash.png，尺寸是原来的 5 倍（16 × 5 = 80px），
画面内容与 icon.ico 完全一致，只是整体放大。

默认只写 splash.png；要连 icon.ico 一起重画，加 --write-ico。

用法：
    python tools/make-icon.py                  # 只生成 splash.png
    python tools/make-icon.py --write-ico      # 顺带重画 icon.ico
    python tools/make-icon.py --preview a.png  # 只出预览
"""

from __future__ import annotations

import argparse
import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
IMAGES = os.path.join(ROOT, "Plain Craft Launcher 2", "Images")
ICON_PATH = os.path.join(IMAGES, "icon.ico")
SPLASH_PATH = os.path.join(IMAGES, "splash.png")

# 取自原图标的渐变两端
GRADIENT_TOP = (57, 145, 227)
GRADIENT_BOTTOM = (25, 100, 194)
WHITE = (255, 255, 255, 255)

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
SUPERSAMPLE = 8

FONT_BOLD = r"C:\Windows\Fonts\segoeuib.ttf"
FONT_LABEL = r"C:\Windows\Fonts\msyhbd.ttc"

TILE_RADIUS_RATIO = 0.22      # 与原图标一致
WORDMARK_TEXT = "LITE"
WORDMARK_WIDTH_RATIO = 0.66   # 原图标里 LITE 占图块宽度 169/256

# 闪屏：原来按 16px 开窗，放大 5 倍
SPLASH_BASE_SIZE = 16
SPLASH_SCALE = 5.0
SPLASH_SIZE = int(SPLASH_BASE_SIZE * SPLASH_SCALE)


def _rounded_tile(size: int) -> Image.Image:
    """带纵向渐变的圆角方块，边长 size * SUPERSAMPLE。"""
    w = size * SUPERSAMPLE
    grad = Image.new("RGBA", (1, w))
    for y in range(w):
        t = y / max(1, w - 1)
        grad.putpixel((0, y), (
            round(GRADIENT_TOP[0] + (GRADIENT_BOTTOM[0] - GRADIENT_TOP[0]) * t),
            round(GRADIENT_TOP[1] + (GRADIENT_BOTTOM[1] - GRADIENT_TOP[1]) * t),
            round(GRADIENT_TOP[2] + (GRADIENT_BOTTOM[2] - GRADIENT_TOP[2]) * t),
            255,
        ))
    grad = grad.resize((w, w))

    mask = Image.new("L", (w, w), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, w - 1, w - 1], radius=int(w * TILE_RADIUS_RATIO), fill=255
    )

    tile = Image.new("RGBA", (w, w), (0, 0, 0, 0))
    tile.paste(grad, (0, 0), mask)
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


def _draw_centered(tile: Image.Image, text: str, font: ImageFont.FreeTypeFont) -> None:
    draw = ImageDraw.Draw(tile)
    left, top, right, bottom = draw.textbbox((0, 0), text, font=font)
    w, h = tile.size
    draw.text(
        ((w - (right - left)) / 2 - left, (h - (bottom - top)) / 2 - top),
        text, font=font, fill=WHITE,
    )


def render(size: int) -> Image.Image:
    """按目标尺寸单独排版后渲染，不是拿大图直接缩，小尺寸才不会糊。"""
    tile = _rounded_tile(size)
    font = _font_for_width(WORDMARK_TEXT, tile.size[0] * WORDMARK_WIDTH_RATIO)
    _draw_centered(tile, WORDMARK_TEXT, font)
    return tile.resize((size, size), Image.LANCZOS)


def build() -> dict[int, Image.Image]:
    return {size: render(size) for size in SIZES}


def write_ico(images: dict[int, Image.Image], path: str) -> None:
    images[256].save(
        path,
        format="ICO",
        sizes=[(s, s) for s in SIZES],
        append_images=[images[s] for s in SIZES],
    )


def write_preview(path: str) -> None:
    pad = 16
    sheet = Image.new("RGB", (560, 130), (246, 247, 250))
    draw = ImageDraw.Draw(sheet)
    label = ImageFont.truetype(FONT_LABEL, 13)
    x = pad
    for size in [16, 24, 32, 48, 64, 80]:
        im = render(size)
        sheet.paste(im, (x, 30), im)
        draw.text((x, 96), str(size), font=label, fill=(120, 128, 140))
        x += size + 14
    sheet.save(path)


def main() -> None:
    parser = argparse.ArgumentParser(description="生成启动闪屏图 / 图标")
    parser.add_argument("--write-ico", action="store_true", help="同时重画 icon.ico")
    parser.add_argument("--preview", help="只输出预览图到指定路径")
    args = parser.parse_args()

    if args.preview:
        write_preview(args.preview)
        print(f"preview -> {args.preview}")
        return

    render(SPLASH_SIZE).save(SPLASH_PATH)
    print(f"splash -> {SPLASH_PATH} ({SPLASH_SIZE}x{SPLASH_SIZE})")

    if args.write_ico:
        write_ico(build(), ICON_PATH)
        print(f"ico -> {ICON_PATH} ({os.path.getsize(ICON_PATH)} bytes, {len(SIZES)} sizes)")


if __name__ == "__main__":
    main()
