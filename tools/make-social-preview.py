# -*- coding: utf-8 -*-
"""生成 GitHub 仓库的 Social preview 图（1200x630）。

为什么是画出来的：这个项目的品牌视觉本身就是代码画的（Avalonia 里的 LogoRing
与自绘字母徽标），仓库里没有任何 logo 图片。所以这张也直接画，改文案只要改下面
这几个常量，不用去动设计文件。

用法：
    python tools/make-social-preview.py docs/social-preview.png
"""

import os
import sys

from PIL import Image, ImageDraw, ImageFilter, ImageFont

WIDTH, HEIGHT = 1280, 640
BACKGROUND = (14, 14, 20)
BRAND = (111, 111, 255)
MARK = (232, 232, 240)
WORDMARK = "YEEYEEYEE"
SUBTITLE = "面向 AI 创作的 Windows 桌面工作区"
PIPELINE = "企划 → 章节 → 分镜 → 成品"

FONT_CANDIDATES = {
    "latin-bold": ["seguisb.ttf", "segoeuib.ttf", "segoeui.ttf"],
    "cjk": ["msyh.ttc", "msyhbd.ttc", "simhei.ttf"],
}


def load_font(kind, size):
    for name in FONT_CANDIDATES[kind]:
        path = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", name)
        if os.path.exists(path):
            try:
                return ImageFont.truetype(path, size)
            except OSError:
                continue
    raise SystemExit("找不到可用字体：" + str(FONT_CANDIDATES[kind]))


def text_width(draw, text, font, spacing=0):
    width = 0
    for char in text:
        width += draw.textlength(char, font=font) + spacing
    return width - spacing if text else 0


def draw_spaced(draw, center_x, center_y, text, font, spacing, fill):
    """按固定字距居中绘制：逐字排，才能做出宽字距的观感。"""
    width = text_width(draw, text, font, spacing)
    x = center_x - width / 2
    for char in text:
        draw.text((x, center_y), char, font=font, fill=fill, anchor="lm")
        x += draw.textlength(char, font=font) + spacing


def diamond(cx, cy, radius):
    return [(cx, cy - radius), (cx + radius, cy), (cx, cy + radius), (cx - radius, cy)]


def main():
    target = sys.argv[1] if len(sys.argv) > 1 else "docs/social-preview.png"

    image = Image.new("RGBA", (WIDTH, HEIGHT), BACKGROUND + (255,))

    # 背景那团光晕：先在一块透明层上画实心椭圆，再重度模糊，最后叠上去。
    glow = Image.new("RGBA", (WIDTH, HEIGHT), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse(
        [WIDTH / 2 - 360, 120, WIDTH / 2 + 360, 520], fill=BRAND + (120,)
    )
    image = Image.alpha_composite(image, glow.filter(ImageFilter.GaussianBlur(150)))

    draw = ImageDraw.Draw(image)

    # 徽记照应用里的样子画：外圈空心菱形 + 内圈实心菱形，也就是那个 ◈。
    draw.polygon(diamond(WIDTH / 2, 168, 46), outline=MARK + (255,), width=5)
    draw.polygon(diamond(WIDTH / 2, 168, 17), fill=BRAND + (255,))

    word_font = load_font("latin-bold", 84)
    draw_spaced(draw, WIDTH / 2, 308, WORDMARK, word_font, 10, MARK + (255,))

    sub_font = load_font("cjk", 31)
    draw.text((WIDTH / 2, 380), SUBTITLE, font=sub_font, fill=(154, 154, 170, 255), anchor="mm")

    pipe_font = load_font("cjk", 26)
    draw.text((WIDTH / 2, 545), PIPELINE, font=pipe_font, fill=BRAND + (255,), anchor="mm")

    directory = os.path.dirname(os.path.abspath(target))
    if directory:
        os.makedirs(directory, exist_ok=True)
    image.convert("RGB").save(target, "PNG", optimize=True)
    size = os.path.getsize(target)
    print("%s  %dx%d  %.1f KB" % (target, WIDTH, HEIGHT, size / 1024))


if __name__ == "__main__":
    main()
