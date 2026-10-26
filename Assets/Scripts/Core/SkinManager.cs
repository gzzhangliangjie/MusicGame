using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MusicGame.Core
{
    /// <summary>
    /// 运行时皮肤包：把 PNG 放进 工程根/Skins/&lt;皮肤名&gt;/ 即生效（下一局生效），零打包零导入。
    /// 支持的文件名（缺哪个用哪个的程序化默认）：
    ///   note.png(音符) holdbody.png(hold 光带) pad.png(按键板) disc.png(圆盘/太鼓通用)
    ///   don.png(咚) katsu.png(咔) drum.png(太鼓鼓面) rollstrip.png(连击条) keycap.png(太鼓垫底)
    /// 皮肤来源：CC0 素材站（kenney.nl / opengameart.org，CC0 可自由使用）下载 PNG 改成上述文件名即可。
    /// </summary>
    public static class SkinManager
    {
        private const string PrefKey = "MusicGame.Skin";

        // 每个键的 osu! 皮肤文件名回退链：把任意 osu 皮肤解压进皮肤目录，音符/太鼓圆盘直接可用
        private static readonly Dictionary<string, string[]> FallbackNames = new Dictionary<string, string[]>
        {
            { "note", new[] { "note", "mania-note1T", "mania-note1", "mania-note2T", "mania-note3T", "mania-note4T" } },
            { "pad", new[] { "pad" } },
            { "holdbody", new[] { "holdbody" } },
            { "disc", new[] { "disc", "taikohitcircle", "taikohitcircle-k" } },
            { "rollstrip", new[] { "rollstrip", "taikobigroll", "taikoroll" } },
            { "don", new[] { "don", "taikohitcircle" } },
            { "katsu", new[] { "katsu", "taikohitcircle-k", "taikohitcircle" } },
            { "drum", new[] { "drum", "taikobase" } },
        };

        private static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();
        private static string[] skinNames;
        private static int skinIndex = -1;

        /// <summary>扫描可用皮肤（目录名列表），"默认" 恒为第一项（程序化皮肤）。</summary>
        public static string[] SkinNames
        {
            get
            {
                if (skinNames != null) return skinNames;
                var names = new List<string> { "默认" };
                foreach (string root in Roots())
                {
                    try
                    {
                        if (!Directory.Exists(root)) continue;
                        foreach (string dir in Directory.GetDirectories(root))
                            if (!names.Contains(Path.GetFileName(dir)))
                                names.Add(Path.GetFileName(dir));
                    }
                    catch (IOException) { }
                }
                skinNames = names.ToArray();
                return skinNames;
            }
        }

        /// <summary>当前皮肤名。</summary>
        public static string Current
        {
            get
            {
                string saved = PlayerPrefs.GetString(PrefKey, "默认");
                string[] names = SkinNames;
                foreach (string n in names)
                    if (n == saved) return n;
                return "默认";
            }
        }

        /// <summary>循环切换皮肤并持久化，返回新皮肤名（切换后下一局生效）。</summary>
        public static string Cycle()
        {
            string[] names = SkinNames;
            skinIndex = -1;
            for (int i = 0; i < names.Length; i++)
                if (names[i] == Current) { skinIndex = i; break; }
            skinIndex = (skinIndex + 1) % names.Length;
            PlayerPrefs.SetString(PrefKey, names[skinIndex]);
            PlayerPrefs.Save();
            cache.Clear();
            return names[skinIndex];
        }

        /// <summary>
        /// 取皮肤贴图：当前皮肤目录下按回退链找 &lt;文件名&gt;.png（含 osu! 皮肤名），带缓存；
        /// 都没有则返回 null（调用方回退程序化贴图）。
        /// </summary>
        public static Sprite Load(string key)
        {
            string current = Current;
            if (current == "默认") return null;
            string cacheKey = current + "/" + key;
            if (cache.TryGetValue(cacheKey, out Sprite cached)) return cached;

            string[] names = FallbackNames.TryGetValue(key, out string[] fallbacks)
                ? fallbacks
                : new[] { key };
            foreach (string root in Roots())
            {
                foreach (string name in names)
                {
                    string path = Path.Combine(root, Path.Combine(current, name + ".png"));
                    if (!File.Exists(path)) continue;
                    try
                    {
                        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!texture.LoadImage(File.ReadAllBytes(path))) continue;
                        texture.filterMode = FilterMode.Bilinear;
                        var sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                            new Vector2(0.5f, 0.5f), Mathf.Max(texture.width, texture.height) / 2f);
                        sprite.name = "Skin_" + key;
                        cache[cacheKey] = sprite;
                        return sprite;
                    }
                    catch (IOException) { }
                }
            }
            cache[cacheKey] = null; // 记住缺失，避免每帧扫盘
            return null;
        }

        // 皮肤根目录：工程根/Skins 与 persistentDataPath/Skins
        private static IEnumerable<string> Roots()
        {
            var roots = new List<string>();
            try
            {
                DirectoryInfo parent = Directory.GetParent(Application.dataPath);
                if (parent != null) roots.Add(Path.Combine(parent.FullName, "Skins"));
            }
            catch (IOException) { }
            roots.Add(Path.Combine(Application.persistentDataPath, "Skins"));
            return roots;
        }
    }
}
