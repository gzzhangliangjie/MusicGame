#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""结算评级美术字 S/A/B/C + 「结算」标题美术字。
填充掩码（无描边，字腔完整）与描边掩码分离——A/B 的字腔不会被描边+膨胀吞掉；
大字号（720/560）渲染后 LANCZOS 缩小，细节清晰。"""
from PIL import Image, ImageDraw, ImageFont, ImageFilter
import os

OUT = r"E:\MusicGame\Assets\Resources\Ui\Rank"
FONT_LATIN = r"C:\Windows\Fonts\impact.ttf"
FONT_CN = r"E:\MusicGame\Assets\Resources\Fonts\SourceHanSansCN-Heavy.otf"

GRADES = {
    "S": ((255, 244, 214), (245, 160, 30), (255, 214, 100), (96, 54, 8)),
    "A": ((236, 255, 216), (110, 205, 80), (160, 240, 130), (22, 84, 28)),
    "B": ((214, 238, 255), (74, 160, 230), (140, 205, 255), (14, 54, 108)),
    "C": ((236, 232, 246), (140, 122, 190), (200, 190, 235), (54, 44, 92)),
}

def tint(mask, color, alpha_mul=1.0):
    layer = Image.new("RGBA", mask.size, color + (0,))
    layer.putalpha(mask.point(lambda v: int(v * alpha_mul)))
    return layer

def star(draw, cx, cy, r, color=(255, 255, 255, 235)):
    draw.polygon([(cx, cy - r), (cx + r * 0.22, cy - r * 0.22), (cx + r, cy),
                  (cx + r * 0.22, cy + r * 0.22), (cx, cy + r), (cx - r * 0.22, cy + r * 0.22),
                  (cx - r, cy), (cx - r * 0.22, cy - r * 0.22)], fill=color)

def text_masks(text, font, stroke, shear_k=0.0, spacing=0):
    """返回 (fill_mask, outline_mask)：fill 无描边字腔完整；outline 带描边大一圈。同尺寸同原点。"""
    pads = 160

    def draw(stroke_width):
        if spacing > 0:
            widths = []
            for ch in text:
                b = font.getbbox(ch, stroke_width=stroke_width)
                widths.append(b[2] - b[0])
            b0 = font.getbbox(text[0], stroke_width=stroke_width)
            h = b0[3] - b0[1]
            img = Image.new("L", (sum(widths) + spacing * (len(text) - 1) + pads * 2, h + pads * 2), 0)
            d = ImageDraw.Draw(img)
            x = pads
            for ch, wd in zip(text, widths):
                cb = font.getbbox(ch, stroke_width=stroke_width)
                d.text((x - cb[0], pads), ch, font=font, fill=255, stroke_width=stroke_width, stroke_fill=255)
                x += wd + spacing
        else:
            b = font.getbbox(text, stroke_width=stroke_width)
            img = Image.new("L", (b[2] - b[0] + pads * 2, b[3] - b[1] + pads * 2), 0)
            ImageDraw.Draw(img).text((pads - b[0], pads), text, font=font, fill=255,
                                     stroke_width=stroke_width, stroke_fill=255)
        if shear_k:
            img = img.transform((img.width + int(shear_k * img.height), img.height), Image.AFFINE,
                                (1, shear_k, -shear_k * img.height / 2, 0, 1, 0), resample=Image.BICUBIC)
        return img

    return draw(0), draw(stroke)

def compose(fill, outl, top, bottom, glow, glow_layers, star_spots=None):
    W, H = fill.width + 240, fill.height + 240
    ox = oy = 120
    content = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    for radius, alpha in glow_layers:  # 辉光基于描边掩码（外围大一圈，光晕更匀）
        content.alpha_composite(tint(outl.filter(ImageFilter.GaussianBlur(radius)), glow, alpha / 255.0), (ox, oy))
    content.alpha_composite(tint(outl, glow), (ox, oy))       # 描边底层（用辉光色，比深色描边更亮更 cartoon）
    content.alpha_composite(tint(outl, outline), (ox, oy))    # 深色细描边压出轮廓层次
    grad = Image.linear_gradient("L").resize(fill.size).transpose(Image.FLIP_TOP_BOTTOM)
    fill_rgb = Image.new("RGB", fill.size, bottom)
    fill_rgb.paste(Image.new("RGB", fill.size, top), (0, 0), grad)
    content.alpha_composite(Image.composite(fill_rgb.convert("RGBA"), Image.new("RGBA", fill.size, (0, 0, 0, 0)), fill), (ox, oy))
    hl = Image.new("L", fill.size, 0)
    ImageDraw.Draw(hl).rectangle([0, 0, fill.width, int(fill.height * 0.45)], fill=70)
    hl_layer = Image.new("RGBA", fill.size, (255, 255, 255, 0))
    hl_layer.putalpha(Image.composite(hl, Image.new("L", fill.size, 0), fill))
    content.alpha_composite(hl_layer, (ox, oy))
    if star_spots:
        dd = ImageDraw.Draw(content)
        for sx, sy, sr in star_spots:
            star(dd, ox + sx, oy + sy, sr)
    return content.crop(content.getbbox())

def save_crisp(content, path, target_h):
    scale = target_h / content.height
    content = content.resize((max(1, int(content.width * scale)), target_h), Image.LANCZOS)
    content.save(path)
    print("saved", path, content.size)

# ---- 评级字母：720 号渲染 → 缩到高 400 ----
for letter, (top, bottom, glow, outline) in GRADES.items():
    fill, outl = text_masks(letter, ImageFont.truetype(FONT_LATIN, 720), stroke=16, shear_k=0.20)
    stars = [(fill.width + 150, 150, 55), (fill.width + 235, 265, 26)] if letter == "S" else \
            [(fill.width + 155, 165, 38)] if letter in ("A", "B") else None
    content = compose(fill, outl, top, bottom, glow,
                      glow_layers=((46, 55), (26, 100), (12, 145)), star_spots=stars)
    save_crisp(content, os.path.join(OUT, f"rank_{letter.lower()}.png"), 400)

# ---- 「结算」标题美术字：思源黑体 Heavy + 金渐变，直立 ----
fill, outl = text_masks("结算", ImageFont.truetype(FONT_CN, 560), stroke=14, spacing=28)
content = compose(fill, outl, (255, 248, 214), (245, 166, 40), (255, 216, 110),
                  glow_layers=((40, 55), (20, 95), (9, 135)))
save_crisp(content, r"E:\MusicGame\Assets\Resources\Ui\title_settle.png", 150)
