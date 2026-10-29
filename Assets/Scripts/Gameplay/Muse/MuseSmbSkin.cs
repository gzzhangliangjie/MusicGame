using System;
using System.IO;
using UnityEngine;

namespace MusicGame.Gameplay.Muse
{
    /// <summary>
    /// 跑酷模式原版图块皮肤（可选）：从本地 Rom/CHR 运行时解码 2bpp 图块 → 马里奥/栗子怪/金币 Sprite。
    /// 与 PixelGame 工程同一套机制与约定：CHR 数据不入仓库（.gitignore /Rom/），缺文件时自动回退自绘贴图。
    /// 查找顺序：① 本工程 Rom/*.nes（自备合法 ROM，抽 CHR 段）② 本工程 Rom/smb1-disasm/gfx/*.chr
    /// ③ 本机 PixelGame 工程的同名 CHR（懒人路径）。
    /// 图块号/调色板表来自公开反汇编 smb1-disasm（与 PixelGame/SmbAssets 同源）。
    /// </summary>
    public static class MuseSmbSkin
    {
        private static byte[] chrBg, chrSpr;
        private static bool loaded;
        private static readonly System.Collections.Generic.Dictionary<int, Sprite> cache = new System.Collections.Generic.Dictionary<int, Sprite>();

        /// <summary>原版图块是否可用（懒加载，进程内只探一次盘）。</summary>
        public static bool Available => loaded || TryLoad();

        // ---- 标准 NES 2C02 调色板（64 色） ----
        private static readonly Color32[] NesLut =
        {
            new Color32(0x54,0x54,0x54,255), new Color32(0x00,0x1E,0x74,255), new Color32(0x08,0x10,0x90,255), new Color32(0x30,0x00,0x88,255),
            new Color32(0x44,0x00,0x64,255), new Color32(0x5C,0x00,0x30,255), new Color32(0x54,0x04,0x00,255), new Color32(0x3C,0x18,0x00,255),
            new Color32(0x20,0x2A,0x00,255), new Color32(0x08,0x3A,0x00,255), new Color32(0x00,0x40,0x00,255), new Color32(0x00,0x3C,0x00,255),
            new Color32(0x00,0x32,0x3C,255), new Color32(0x00,0x00,0x00,255), new Color32(0x00,0x00,0x00,255), new Color32(0x00,0x00,0x00,255),
            new Color32(0x98,0x96,0x98,255), new Color32(0x08,0x4C,0xC4,255), new Color32(0x30,0x32,0xEC,255), new Color32(0x5C,0x1E,0xE4,255),
            new Color32(0x88,0x14,0xB0,255), new Color32(0xA0,0x14,0x64,255), new Color32(0x98,0x22,0x20,255), new Color32(0x78,0x3C,0x00,255),
            new Color32(0x54,0x5A,0x00,255), new Color32(0x28,0x72,0x00,255), new Color32(0x08,0x7C,0x00,255), new Color32(0x00,0x76,0x28,255),
            new Color32(0x00,0x66,0x78,255), new Color32(0x00,0x00,0x00,255), new Color32(0x00,0x00,0x00,255), new Color32(0x00,0x00,0x00,255),
            new Color32(0xEC,0xEE,0xEC,255), new Color32(0x4C,0x9A,0xEC,255), new Color32(0x78,0x7C,0xEC,255), new Color32(0xB0,0x62,0xEC,255),
            new Color32(0xE4,0x5A,0xE8,255), new Color32(0xEC,0x58,0xB4,255), new Color32(0xEC,0x6A,0x64,255), new Color32(0xD4,0x88,0x20,255),
            new Color32(0xA0,0xAA,0x00,255), new Color32(0x74,0xC4,0x00,255), new Color32(0x4C,0xD0,0x20,255), new Color32(0x38,0xCC,0x6C,255),
            new Color32(0x38,0xB4,0xCC,255), new Color32(0x3C,0x3C,0x3C,255), new Color32(0x00,0x00,0x00,255), new Color32(0x00,0x00,0x00,255),
            new Color32(0xEC,0xEE,0xEC,255), new Color32(0xA8,0xCC,0xEC,255), new Color32(0xBC,0xBC,0xEC,255), new Color32(0xD4,0xB2,0xEC,255),
            new Color32(0xEC,0xAE,0xE4,255), new Color32(0xEC,0xAE,0xD4,255), new Color32(0xEC,0xB4,0xB0,255), new Color32(0xE4,0xC4,0x90,255),
            new Color32(0xCC,0xD2,0x78,255), new Color32(0xB4,0xDE,0x78,255), new Color32(0xA8,0xE2,0x90,255), new Color32(0x98,0xE2,0xB4,255),
            new Color32(0xA0,0xD6,0xE0,255), new Color32(0xA0,0xA2,0xA0,255), new Color32(0x00,0x00,0x00,255), new Color32(0x00,0x00,0x00,255),
        };

        // 精灵调色板 0=马里奥 1=栗子怪；背景调色板 3=金币（0 号位为背景色，金币贴图里作透明）
        private static readonly byte[,] SprPal = { { 0x0F, 0x16, 0x27, 0x18 }, { 0x0F, 0x1A, 0x30, 0x27 } };
        private static readonly byte[] CoinBgPal = { 0x0F, 0x27, 0x17, 0x0F };

        // 小马里奥姿态（图块号抄自反汇编 PlayerGraphicsTable，成对=左/右 8x8，行序自顶向下）
        // 0站立 1-3走路 4跳 5急停；同行左右图块相同时右半镜像（站立右半 ora #$40 的 ROM 证据，与 PixelGame 同）
        private static readonly int[][] SmallPose =
        {
            new[] { 0x3a, 0x37, 0x4f, 0x4f },
            new[] { 0x32, 0x33, 0x34, 0x35 },
            new[] { 0x36, 0x37, 0x38, 0x39 },
            new[] { 0x3a, 0x37, 0x3b, 0x3c },
            new[] { 0x32, 0x41, 0x42, 0x43 },
            new[] { 0x3d, 0x3e, 0x3f, 0x40 },
        };

        public static bool TryLoad()
        {
            if (loaded) return true;
            try
            {
                string projRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                // ① 自备 .nes：从 iNES 抽 CHR（8KB = 前 4K 背景 + 后 4K 精灵）
                string romDir = Path.Combine(projRoot, "Rom");
                if (Directory.Exists(romDir))
                {
                    foreach (var f in Directory.GetFiles(romDir, "*.nes"))
                    {
                        var d = File.ReadAllBytes(f);
                        if (d.Length < 16 || d[0] != 'N' || d[1] != 'E' || d[2] != 'S') continue;
                        int prg = d[4] * 16384;
                        int chr = d[5] * 8192;
                        if (chr == 0) continue;
                        int off = 16 + ((d[6] & 4) != 0 ? 512 : 0) + prg;
                        if (off + 8192 > d.Length) continue;
                        chrBg = new byte[4096];
                        chrSpr = new byte[4096];
                        Array.Copy(d, off, chrBg, 0, 4096);
                        Array.Copy(d, off + 4096, chrSpr, 0, 4096);
                        loaded = true;
                        Debug.Log("[MuseSmbSkin] 已从自备 ROM 加载原版图块: " + f);
                        return true;
                    }
                    // ② 本工程 Rom/smb1-disasm/gfx 的公开 CHR（bg.chr 尾部空白图块被裁剪，补齐到 4K）
                    if (LoadChrDir(Path.Combine(romDir, "smb1-disasm", "gfx"))) return true;
                }
                // ③ 本机 PixelGame 工程同款 CHR（懒人路径：装过 PixelGame 即生效）
                if (LoadChrDir(Path.Combine("E:", "PixelGame", "Rom", "smb1-disasm", "gfx"))) return true;
                return false;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MuseSmbSkin] CHR 加载失败（回退自绘贴图）: " + e.Message);
                return false;
            }
        }

        private static bool LoadChrDir(string dir)
        {
            string bgPath = Path.Combine(dir, "bg.chr");
            string sprPath = Path.Combine(dir, "sprites.chr");
            if (!File.Exists(bgPath) || !File.Exists(sprPath)) return false;
            chrBg = File.ReadAllBytes(bgPath);
            chrSpr = File.ReadAllBytes(sprPath);
            if (chrBg.Length < 1024 || chrSpr.Length < 1024)
            {
                Debug.LogWarning("[MuseSmbSkin] CHR 文件过小: bg=" + chrBg.Length + " spr=" + chrSpr.Length);
                return false;
            }
            Array.Resize(ref chrBg, 4096); // 补零 = 空白图块，与原版一致
            Array.Resize(ref chrSpr, 4096);
            loaded = true;
            Debug.Log("[MuseSmbSkin] 原版 CHR 已加载: " + dir);
            return true;
        }

        /// <summary>小马里奥。pose: 0站立 1-3走路 4跳 5急停（调色板 0）。</summary>
        public static Sprite Mario(int pose)
        {
            int key = 0x100 | pose;
            if (cache.TryGetValue(key, out var s)) return s;
            var t = SmallPose[Mathf.Clamp(pose, 0, SmallPose.Length - 1)];
            bool dupTop = t[0] == t[1], dupBot = t[2] == t[3];
            s = Compose16(t[0], t[1], t[2], t[3], dupTop, dupBot, SprPal4(0));
            cache[key] = s;
            return s;
        }

        /// <summary>栗子怪（sprites.chr $70-73，调色板 1，四象限原样）。</summary>
        public static Sprite Goomba()
        {
            if (cache.TryGetValue(1, out var s)) return s;
            s = Compose16(0x70, 0x71, 0x72, 0x73, false, false, SprPal4(1));
            cache[1] = s;
            return s;
        }

        /// <summary>被踩扁的栗子怪（原版 defeated 帧：$fc,$fc / $ef,$ef）。</summary>
        public static Sprite GoombaFlat()
        {
            if (cache.TryGetValue(3, out var s)) return s;
            s = Compose16(0xfc, 0xfc, 0xef, 0xef, false, false, SprPal4(1));
            cache[3] = s;
            return s;
        }

        /// <summary>金币（bg.chr 元块 $C2=P3[2]，四象限 {A5,A7,A6,A8} 列序；背景色像素作透明）。</summary>
        public static Sprite Coin()
        {
            if (cache.TryGetValue(2, out var s)) return s;
            int[] t = { 0xA5, 0xA7, 0xA6, 0xA8 };
            var pal = new[]
            {
                new Color(0, 0, 0, 0), // 元块 0 号色 = 天空背景，作透明
                (Color)NesLut[CoinBgPal[1]],
                (Color)NesLut[CoinBgPal[2]],
                (Color)NesLut[CoinBgPal[3]],
            };
            var buf = new Color[256];
            void Put(int tile, int ox, int oy)
            {
                var px = DecodeTile(chrBg, tile, pal);
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        buf[(oy + 7 - y) * 16 + ox + x] = px[y * 8 + x]; // 数组行0=贴图底
            }
            Put(t[0], 0, 8); // 左上
            Put(t[2], 8, 8); // 右上
            Put(t[1], 0, 0); // 左下
            Put(t[3], 8, 0); // 右下
            s = ToSprite(buf, 16);
            cache[2] = s;
            return s;
        }

        // 四个 8x8 图块拼 16x16（列序字节表；右半可选镜像以复用单图块）
        private static Sprite Compose16(int tl, int tr, int bl, int br, bool flipTR, bool flipBR, Color[] pal)
        {
            var buf = new Color[256];
            void Put(int tile, int ox, int oy, bool flipX)
            {
                var px = DecodeTile(chrSpr, tile, pal);
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int sx = flipX ? 7 - x : x;
                        buf[(oy + 7 - y) * 16 + ox + x] = px[y * 8 + sx]; // 数组行0=贴图底
                    }
            }
            Put(tl, 0, 8, false);
            Put(tr, 8, 8, flipTR);
            Put(bl, 0, 0, false);
            Put(br, 8, 0, flipBR);
            return ToSprite(buf, 16);
        }

        /// <summary>2bpp 平面解码一个 8x8 图块（bit 平面 1+2 合成 2 位色号）。</summary>
        private static Color[] DecodeTile(byte[] chr, int tile, Color[] pal4)
        {
            var px = new Color[64];
            int b = tile * 16;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int bit = 7 - x;
                    int c = ((chr[b + y] >> bit) & 1) | (((chr[b + 8 + y] >> bit) & 1) << 1);
                    px[y * 8 + x] = pal4[c];
                }
            return px;
        }

        private static Color[] SprPal4(int set)
        {
            return new[]
            {
                new Color(0, 0, 0, 0),
                (Color)NesLut[SprPal[set, 1]],
                (Color)NesLut[SprPal[set, 2]],
                (Color)NesLut[SprPal[set, 3]],
            };
        }

        private static Sprite ToSprite(Color[] buf, int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            tex.SetPixels(buf);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 16f); // PPU16：原生 1 单位
        }
    }
}
