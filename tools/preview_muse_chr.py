# -*- coding: utf-8 -*-
"""MuseSmbSkin 逻辑预览：用与 C# 相同的图块表/调色板/镜像规则解码 CHR 渲染 PNG，验证移植正确性。"""
from PIL import Image

NES_LUT = [
    (0x54,0x54,0x54),(0x00,0x1E,0x74),(0x08,0x10,0x90),(0x30,0x00,0x88),
    (0x44,0x00,0x64),(0x5C,0x00,0x30),(0x54,0x04,0x00),(0x3C,0x18,0x00),
    (0x20,0x2A,0x00),(0x08,0x3A,0x00),(0x00,0x40,0x00),(0x00,0x3C,0x00),
    (0x00,0x32,0x3C),(0x00,0x00,0x00),(0x00,0x00,0x00),(0x00,0x00,0x00),
    (0x98,0x96,0x98),(0x08,0x4C,0xC4),(0x30,0x32,0xEC),(0x5C,0x1E,0xE4),
    (0x88,0x14,0xB0),(0xA0,0x14,0x64),(0x98,0x22,0x20),(0x78,0x3C,0x00),
    (0x54,0x5A,0x00),(0x28,0x72,0x00),(0x08,0x7C,0x00),(0x00,0x76,0x28),
    (0x00,0x66,0x78),(0x00,0x00,0x00),(0x00,0x00,0x00),(0x00,0x00,0x00),
    (0xEC,0xEE,0xEC),(0x4C,0x9A,0xEC),(0x78,0x7C,0xEC),(0xB0,0x62,0xEC),
    (0xE4,0x5A,0xE8),(0xEC,0x58,0xB4),(0xEC,0x6A,0x64),(0xD4,0x88,0x20),
    (0xA0,0xAA,0x00),(0x74,0xC4,0x00),(0x4C,0xD0,0x20),(0x38,0xCC,0x6C),
    (0x38,0xB4,0xCC),(0x3C,0x3C,0x3C),(0x00,0x00,0x00),(0x00,0x00,0x00),
    (0xEC,0xEE,0xEC),(0xA8,0xCC,0xEC),(0xBC,0xBC,0xEC),(0xD4,0xB2,0xEC),
    (0xEC,0xAE,0xE4),(0xEC,0xAE,0xD4),(0xEC,0xB4,0xB0),(0xE4,0xC4,0x90),
    (0xCC,0xD2,0x78),(0xB4,0xDE,0x78),(0xA8,0xE2,0x90),(0x98,0xE2,0xB4),
    (0xA0,0xD6,0xE0),(0xA0,0xA2,0xA0),(0x00,0x00,0x00),(0x00,0x00,0x00),
]
SPR_PAL = {0: [0x0F,0x16,0x27,0x18], 1: [0x0F,0x1A,0x30,0x27]}
COIN_BG_PAL = [0x0F,0x27,0x17,0x0F]
SMALL_POSE = [
    (0x3a,0x37,0x4f,0x4f), (0x32,0x33,0x34,0x35), (0x36,0x37,0x38,0x39),
    (0x3a,0x37,0x3b,0x3c), (0x32,0x41,0x42,0x43), (0x3d,0x3e,0x3f,0x40),
]
GOOMBA_TILES = (0x70,0x71,0x72,0x73)
COIN_TILES = (0xA5,0xA7,0xA6,0xA8)  # 元块 $C2 = P3[2]，列序：左上/左下/右上/右下

CHR_BG = open(r"E:/MusicGame/Rom/smb1-disasm/gfx/bg.chr", "rb").read()
CHR_SPR = open(r"E:/MusicGame/Rom/smb1-disasm/gfx/sprites.chr", "rb").read()
CHR_BG = CHR_BG + b"\x00" * (4096 - len(CHR_BG))
CHR_SPR = CHR_SPR + b"\x00" * (4096 - len(CHR_SPR))

def decode_tile(chr_data, tile, pal4):
    """2bpp 平面解码 8x8；返回 64 项 RGBA 列表（行序=图块行）。"""
    px = []
    b = tile * 16
    for y in range(8):
        for x in range(8):
            bit = 7 - x
            c = ((chr_data[b + y] >> bit) & 1) | (((chr_data[b + 8 + y] >> bit) & 1) << 1)
            px.append(pal4[c])
    return px

def spr_pal4(s):
    return [(0,0,0,0)] + [NES_LUT[SPR_PAL[s][i]] + (255,) for i in (1,2,3)]

def compose16(tl, tr, bl, br, flip_tr, flip_br, pal4):
    """四个 8x8 拼 16x16；数组行0=贴图底（与 C# 一致）。"""
    buf = [None] * 256
    def put(tile, ox, oy, flip_x):
        px = decode_tile(CHR_SPR, tile, pal4)
        for y in range(8):
            for x in range(8):
                sx = 7 - x if flip_x else x
                buf[(oy + 7 - y) * 16 + ox + x] = px[y * 8 + sx]
    put(tl, 0, 8, False)
    put(tr, 8, 8, flip_tr)
    put(bl, 0, 0, False)
    put(br, 8, 0, flip_br)
    return buf

def mario(pose):
    t = SMALL_POSE[pose]
    return compose16(t[0], t[1], t[2], t[3], t[0]==t[1], t[2]==t[3], spr_pal4(0))

def goomba():
    return compose16(*GOOMBA_TILES, False, False, spr_pal4(1))

def coin():
    pal = [(0,0,0,0)] + [NES_LUT[COIN_BG_PAL[i]] + (255,) for i in (1,2,3)]
    buf = [None] * 256
    def put(tile, ox, oy):
        px = decode_tile(CHR_BG, tile, pal)
        for y in range(8):
            for x in range(8):
                buf[(oy + 7 - y) * 16 + ox + x] = px[y * 8 + x]
    # 列序：左上 t[0] 右上 t[2] 左下 t[1] 右下 t[3]
    put(COIN_TILES[0], 0, 8)
    put(COIN_TILES[2], 8, 8)
    put(COIN_TILES[1], 0, 0)
    put(COIN_TILES[3], 8, 0)
    return buf

def to_img(buf, w=16, h=16, scale=6):
    """buf 行0=贴图底部（Unity SetPixels 约定），PIL 图像行0=顶部 → 绘制时上下翻转还原真实朝向。"""
    img = Image.new("RGBA", (w * scale, h * scale), (30, 32, 44, 255))
    for y in range(h):
        for x in range(w):
            c = buf[(h - 1 - y) * w + x]
            if c is None or c[3] == 0:
                continue
            for dy in range(scale):
                for dx in range(scale):
                    img.putpixel((x * scale + dx, y * scale + dy), c)
    return img

frames = [("S", to_img(mario(0))), ("W1", to_img(mario(1))), ("W2", to_img(mario(2))), ("W3", to_img(mario(3))),
          ("J", to_img(mario(4))), ("SKID", to_img(mario(5))), ("GOOMBA", to_img(goomba())), ("COIN", to_img(coin()))]
PAD = 8
total_w = sum(im.width for _, im in frames) + PAD * (len(frames) + 1)
max_h = max(im.height for _, im in frames) + PAD * 2 + 16
sheet = Image.new("RGBA", (total_w, max_h), (30, 32, 44, 255))
x = PAD
from PIL import ImageDraw
draw = ImageDraw.Draw(sheet)
for name, im in frames:
    sheet.paste(im, (x, PAD))
    draw.text((x, max_h - 14), name, fill=(230, 230, 230, 255))
    x += im.width + PAD
sheet.save(r"E:/MusicGame/vis_muse_chr_preview2.png")
print("OK -> vis_muse_chr_preview2.png", sheet.size)
