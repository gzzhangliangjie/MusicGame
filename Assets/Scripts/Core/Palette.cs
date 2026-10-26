using System;
using UnityEngine;

namespace MusicGame.Core
{
    /// <summary>
    /// 全局配色主题（纯数据）：多套内置主题，主菜单可循环切换并持久化。
    /// 所有视觉模块（UI/音符/轨道/背景）在取色时读取 Current，换主题后下一局/刷新界面生效。
    /// </summary>
    public static class Palette
    {
        /// <summary>一套主题：强调色 + 背景双色 + 四轨色。</summary>
        public class Skin
        {
            public string Name;
            public Color Accent;      // 按钮主色/标题
            public Color AccentSoft;  // 次强调（判定线辉光/连击）
            public Color Backdrop1;   // 动态背景光 A
            public Color Backdrop2;   // 动态背景光 B
            public Color[] Lanes;     // 四轨色

            public Skin(string name, Color accent, Color accentSoft, Color backdrop1, Color backdrop2, Color[] lanes)
            {
                Name = name; Accent = accent; AccentSoft = accentSoft;
                Backdrop1 = backdrop1; Backdrop2 = backdrop2; Lanes = lanes;
            }
        }

        private const string PrefKey = "MusicGame.Theme";

        public static readonly Skin[] All =
        {
            new Skin("霓虹",
                new Color(0.20f, 0.85f, 0.95f), new Color(0.55f, 0.30f, 0.85f),
                new Color(0.20f, 0.60f, 0.95f), new Color(0.55f, 0.30f, 0.85f),
                new[] { new Color(0.25f, 0.78f, 0.95f), new Color(0.32f, 0.56f, 0.95f),
                        new Color(0.68f, 0.46f, 0.95f), new Color(0.95f, 0.46f, 0.75f) }),
            new Skin("樱花",
                new Color(0.98f, 0.62f, 0.75f), new Color(0.85f, 0.45f, 0.65f),
                new Color(0.98f, 0.55f, 0.70f), new Color(0.80f, 0.40f, 0.85f),
                new[] { new Color(0.98f, 0.62f, 0.75f), new Color(0.95f, 0.50f, 0.62f),
                        new Color(0.85f, 0.45f, 0.70f), new Color(0.75f, 0.42f, 0.85f) }),
            new Skin("落日",
                new Color(0.98f, 0.62f, 0.25f), new Color(0.95f, 0.35f, 0.30f),
                new Color(0.98f, 0.55f, 0.25f), new Color(0.95f, 0.30f, 0.40f),
                new[] { new Color(1.00f, 0.72f, 0.30f), new Color(0.98f, 0.55f, 0.32f),
                        new Color(0.95f, 0.40f, 0.38f), new Color(0.90f, 0.32f, 0.55f) }),
            new Skin("翡翠",
                new Color(0.30f, 0.88f, 0.60f), new Color(0.20f, 0.65f, 0.75f),
                new Color(0.30f, 0.80f, 0.60f), new Color(0.20f, 0.55f, 0.85f),
                new[] { new Color(0.35f, 0.90f, 0.62f), new Color(0.30f, 0.80f, 0.70f),
                        new Color(0.28f, 0.70f, 0.80f), new Color(0.30f, 0.62f, 0.90f) }),
        };

        private static int index;

        /// <summary>当前主题。</summary>
        public static Skin Current => All[Math.Min(index, All.Length - 1)];

        /// <summary>当前主题序号（加载后从存档恢复）。</summary>
        public static int Index
        {
            get { return index; }
            set
            {
                index = Math.Max(0, Math.Min(value, All.Length - 1));
                PlayerPrefs.SetInt(PrefKey, index);
                PlayerPrefs.Save();
            }
        }

        /// <summary>启动时调用一次，恢复上次选择。</summary>
        public static void Restore()
        {
            Index = PlayerPrefs.GetInt(PrefKey, 0);
        }

        /// <summary>循环到下一主题并持久化，返回新主题名。</summary>
        public static string Cycle()
        {
            Index = (Index + 1) % All.Length;
            return Current.Name;
        }
    }
}
