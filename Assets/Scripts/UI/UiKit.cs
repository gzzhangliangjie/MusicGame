using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MusicGame.UI
{
    /// <summary>
    /// uGUI 代码构建工具：字体、控件工厂与输入后端适配（旧 Input / 新 Input System 双兼容）。
    /// 仅使用 Unity 标准 API，不依赖任何预制体或资源文件。
    /// </summary>
    public static class UiKit
    {
        /// <summary>统一配色表。</summary>
        public static class Theme
        {
            public static readonly Color Bg = new Color(0.07f, 0.08f, 0.12f, 0.98f);
            public static readonly Color PanelBg = new Color(0.13f, 0.15f, 0.22f, 0.96f);
            public static readonly Color Primary = new Color(0.15f, 0.55f, 0.95f);
            public static readonly Color Secondary = new Color(0.55f, 0.30f, 0.85f);
            public static readonly Color Accent = new Color(0.20f, 0.85f, 0.95f);
            public static readonly Color Warn = new Color(0.95f, 0.40f, 0.30f);
            public static readonly Color Dim = new Color(0.62f, 0.66f, 0.74f);
            public static readonly Color Quit = new Color(0.38f, 0.20f, 0.26f);
        }

        public const string UiRootName = "MusicGameUiRoot";

        static Font cachedFont;
        static Sprite roundedSprite;   // 白色圆角矩形（九宫格），按钮/面板通用
        static Sprite screenBgSprite;  // 全屏渐变底（上深下微亮）

        /// <summary>白色圆角九宫格 sprite：Image.type=Sliced 任意尺寸圆角。</summary>
        public static Sprite RoundedSprite
        {
            get
            {
                if (roundedSprite != null) return roundedSprite;
                const int size = 64, r = 14, b = 2;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                texture.filterMode = FilterMode.Bilinear;
                var pixels = new Color32[size * size];
                float half = size * 0.5f, inner = half - r;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float px = x + 0.5f - half, py = y + 0.5f - half;
                        float qx = Mathf.Max(Mathf.Abs(px) - inner, 0f);
                        float qy = Mathf.Max(Mathf.Abs(py) - inner, 0f);
                        float dist = Mathf.Sqrt(qx * qx + qy * qy) + Mathf.Min(Mathf.Max(Mathf.Abs(px) - inner, Mathf.Abs(py) - inner), 0f) - r;
                        float fill = Mathf.Clamp01(0.5f - dist);
                        float edge = Mathf.Clamp01(-dist / b);
                        byte v = (byte)((80f + 175f * edge) * fill); // 边缘亮、内部稍暗，形成细腻层次
                        pixels[y * size + x] = new Color32(v, v, v, (byte)(255f * fill));
                    }
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                roundedSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size,
                    0, SpriteMeshType.FullRect, new Vector4(r, r, r, r)); // 九宫格边框
                return roundedSprite;
            }
        }

        /// <summary>全屏渐变底（上暗下微亮的深蓝），替代平面色块。</summary>
        public static Sprite ScreenBgSprite
        {
            get
            {
                if (screenBgSprite != null) return screenBgSprite;
                const int w = 32, h = 128;
                var texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
                texture.filterMode = FilterMode.Bilinear;
                var pixels = new Color32[h * w];
                for (int y = 0; y < h; y++)
                {
                    float t = (float)y / h; // 0=底 1=顶
                    byte r = (byte)(16 + 10 * t), g = (byte)(20 + 12 * t), b = (byte)(34 + 18 * t);
                    for (int x = 0; x < w; x++) pixels[y * w + x] = new Color32(r, g, b, 255);
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                screenBgSprite = Sprite.Create(texture, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 32f);
                return screenBgSprite;
            }
        }

        /// <summary>默认字体：优先系统中文字体，回退 Unity 内置 LegacyRuntime.ttf。</summary>
        public static Font FontOrDefault
        {
            get { if (cachedFont == null) cachedFont = LoadFont(); return cachedFont; }
        }

        static Font LoadFont()
        {
            try
            {
                return Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "PingFang SC", "Noto Sans CJK SC", "Arial" }, 28);
            }
            catch (Exception) { }
            try { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
            catch (Exception) { }
            return Font.CreateDynamicFontFromOSFont("Arial", 28);
        }

        static Sprite softDotSprite;

        /// <summary>径向柔光点（白色，中心实边缘透明）：背景光斑/粒子通用。</summary>
        public static Sprite SoftDotSprite
        {
            get
            {
                if (softDotSprite != null) return softDotSprite;
                const int size = 64;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                texture.filterMode = FilterMode.Bilinear;
                var pixels = new Color32[size * size];
                float c = size * 0.5f;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dx = (x + 0.5f - c) / c, dy = (y + 0.5f - c) / c;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = Mathf.Clamp01(1f - r);
                        byte v = (byte)(a * a * 255f);
                        pixels[y * size + x] = new Color32(255, 255, 255, v);
                    }
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                softDotSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 64f);
                return softDotSprite;
            }
        }

        /// <summary>确保存在本项目专用 Canvas（ScreenSpaceOverlay，参考分辨率 1920x1080）。</summary>
        public static Canvas EnsureCanvas()
        {
            var found = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var c in found)
                if (c.name == UiRootName) { EnsureEventSystem(); return c; }
            var go = new GameObject(UiRootName, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100; // 界面层盖在玩法对象之上
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            EnsureEventSystem();
            return canvas;
        }

        /// <summary>确保存在可用 EventSystem（按项目输入后端选择 UI 模块）。</summary>
        public static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }

        /// <summary>创建空节点（RectTransform，已挂到 parent 下）。</summary>
        public static RectTransform Node(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        /// <summary>设置锚点/偏移；缺省为全拉伸铺满父物体。</summary>
        public static void Stretch(RectTransform rt,
            Vector2? anchorMin = null, Vector2? anchorMax = null,
            Vector2? offsetMin = null, Vector2? offsetMax = null)
        {
            rt.anchorMin = anchorMin ?? Vector2.zero;
            rt.anchorMax = anchorMax ?? Vector2.one;
            rt.offsetMin = offsetMin ?? Vector2.zero;
            rt.offsetMax = offsetMax ?? Vector2.zero;
        }

        /// <summary>全屏面板（Image 背景，可阻挡射线）。</summary>
        public static Image Panel(Transform parent, string name, Color color)
        {
            var rt = Node(parent, name);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            Stretch(rt);
            return img;
        }

        /// <summary>文本（全拉伸，由调用方用 Stretch 定区域）。</summary>
        public static Text Label(Transform parent, string name, string text,
            int fontSize, TextAnchor align, Color? color = null, bool bestFit = false)
        {
            var rt = Node(parent, name);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = FontOrDefault;
            t.text = text;
            t.fontSize = fontSize;
            t.alignment = align;
            t.color = color ?? Color.white;
            if (bestFit)
            {
                t.resizeTextForBestFit = true;
                t.resizeTextMinSize = 12;
                t.resizeTextMaxSize = fontSize;
            }
            Stretch(rt);
            return t;
        }

        /// <summary>居中定位按钮（圆角九宫格底 + 按压变色 + 文本投影）。</summary>
        public static Button ButtonRect(Transform parent, string name, string label,
            Vector2 anchoredPosition, Vector2 size, UnityAction onClick,
            Color? bg = null, int fontSize = 30)
        {
            var rt = Node(parent, name);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = size;
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = RoundedSprite;
            img.type = Image.Type.Sliced;
            img.color = bg ?? Theme.Primary;
            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = img;
            button.transition = Selectable.Transition.ColorTint;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.image = img;
            var tint = button.colors;
            tint.highlightedColor = new Color(1.15f, 1.15f, 1.18f, 1f);
            tint.pressedColor = new Color(0.78f, 0.82f, 0.9f, 1f);
            tint.fadeDuration = 0.08f;
            button.colors = tint;
            var txt = Label(rt, "label", label, fontSize, TextAnchor.MiddleCenter, Color.white, true);
            Stretch(txt.rectTransform, new Vector2(0f, 0.08f), new Vector2(1f, 0.92f), new Vector2(8f, 0f), new Vector2(-8f, 0f));
            var textShadow = txt.gameObject.AddComponent<Shadow>();
            textShadow.effectColor = new Color(0f, 0f, 0f, 0.45f);
            textShadow.effectDistance = new Vector2(0f, -2f);
            if (onClick != null) button.onClick.AddListener(onClick);
            return button;
        }

        /// <summary>居中定位单行输入框（含占位文本）。</summary>
        public static InputField InputRect(Transform parent, string name, string placeholder,
            Vector2 anchoredPosition, Vector2 size)
        {
            var rt = Node(parent, name);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = size;
            var img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(0.09f, 0.11f, 0.17f);
            var input = rt.gameObject.AddComponent<InputField>();
            input.navigation = new Navigation { mode = Navigation.Mode.None };
            var txt = Label(rt, "text", "", 24, TextAnchor.MiddleLeft, Color.white);
            Stretch(txt.rectTransform, Vector2.zero, Vector2.one, new Vector2(14f, 3f), new Vector2(-14f, -3f));
            var ph = Label(rt, "placeholder", placeholder, 24, TextAnchor.MiddleLeft, new Color(1f, 1f, 1f, 0.35f));
            Stretch(ph.rectTransform, Vector2.zero, Vector2.one, new Vector2(14f, 3f), new Vector2(-14f, -3f));
            input.textComponent = txt;
            input.placeholder = ph;
            return input;
        }

        /// <summary>难度键显示名。</summary>
        public static string PrettyDifficulty(string difficulty)
        {
            if (string.IsNullOrEmpty(difficulty)) return "?";
            switch (difficulty.ToLowerInvariant())
            {
                case "easy": return "Easy";
                case "normal": return "Normal";
                case "hard": return "Hard";
                case "expert": return "Expert";
                default: return char.ToUpper(difficulty[0]) + difficulty.Substring(1);
            }
        }

        // ---- 输入适配（同时兼容旧 Input 与新 Input System 后端） ----

        public static bool EscDown()
        {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && kb.escapeKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Escape);
#endif
        }

        /// <summary>任意键盘键按下（不含鼠标，用于校准击打）。</summary>
        public static bool AnyKeyPressed()
        {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && kb.anyKey.wasPressedThisFrame;
#else
            if (!Input.anyKeyDown) return false;
            return !(Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2));
#endif
        }

        public static bool MouseLeftDown()
        {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var mouse = UnityEngine.InputSystem.Mouse.current;
            return mouse != null && mouse.leftButton.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(0);
#endif
        }

        public static bool PointerOverUi()
        {
            var es = EventSystem.current;
            return es != null && es.IsPointerOverGameObject();
        }
    }
}
