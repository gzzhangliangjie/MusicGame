using System;
using System.Collections.Generic;
using MusicGame.Core;
using MusicGame.Pure.Model;
using UnityEngine;

namespace MusicGame.Gameplay
{
    /// <summary>
    /// 音符生成器：按 travelTimeSeconds 提前从对象池生成音符，维护 NoteData→NoteController 映射，
    /// 并绘制轨道参考线与判定线（最简 2D 方块视觉，无资源依赖）。
    /// </summary>
    public class NoteSpawner : MonoBehaviour
    {
        /// <summary>单轨宽度（世界单位）。</summary>
        public const float LaneWidth = 2f;
        /// <summary>判定线 Y。</summary>
        public const float JudgeLineY = -4f;
        /// <summary>下落全程高度（生成点 → 判定线）。</summary>
        public const float FallHeight = 9f;
        /// <summary>低于此高度回收音符。</summary>
        public const float KillY = -7.5f;
        /// <summary>轨道数（lane 0–3）。</summary>
        public const int LaneCount = 4;

        private static Sprite squareSprite;
        private static Sprite noteSprite;
        private static Sprite holdBodySprite;
        private static Sprite padSprite;
        private static Sprite judgeGlowSprite;
        private static Sprite panelSprite;

        private readonly List<NoteData> sortedNotes = new List<NoteData>();
        private readonly Dictionary<NoteData, NoteController> live = new Dictionary<NoteData, NoteController>();
        private ChartData chart;
        private Conductor conductor;
        private NotePool pool;
        private float travelTime = 1f;
        private int nextIndex;
        private GameObject playfieldRoot;
        private SpriteRenderer judgeLine;
        private Vector3 judgeLineBaseScale;
        private TextMesh[] keycaps;
        private SpriteRenderer[] lanePads;

        private static readonly string[] LaneKeyNames = { "D", "F", "J", "K" };
        private static readonly Color KeycapIdleColor = new Color(1f, 1f, 1f, 0.55f);
        private static readonly Color KeycapPressedColor = new Color(0.85f, 1f, 1f, 1f);
        private static readonly Color PadIdleColor = new Color(1f, 1f, 1f, 0.10f);
        private static readonly Color PadPressedColor = new Color(0.20f, 0.85f, 0.95f, 0.38f);

        /// <summary>每轨主题色（当前主题的四轨色）：音符、轨道线、按键板同色系，视觉对应零歧义。</summary>
        public static Color[] LaneColors => Palette.Current.Lanes;

        /// <summary>计分系统（hold 尾判上报用，由 GameManager 注入）。</summary>
        internal ScoreSystem Score { get; set; }
        /// <summary>所属 GameManager（判定时钟与按住状态，由 GameManager 注入）。</summary>
        internal GameManager Game { get; set; }
        /// <summary>时钟。</summary>
        internal Conductor Conductor => conductor;
        /// <summary>下落时间（秒）。</summary>
        internal float TravelTime => travelTime;
        /// <summary>对象池。</summary>
        internal NotePool Pool => pool;

        /// <summary>当前存活音符数。</summary>
        public int LiveNoteCount => live.Count;

        /// <summary>轨道中心 X 坐标。</summary>
        public static float LaneX(int lane) => (lane - (LaneCount - 1) * 0.5f) * LaneWidth;

        /// <summary>生成点 Y 坐标。</summary>
        public static float SpawnY => JudgeLineY + FallHeight;

        /// <summary>共享 1x1 白色方块 sprite（代码生成）。</summary>
        internal static Sprite GetSquareSprite()
        {
            if (squareSprite == null)
            {
                var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false);
                texture.filterMode = FilterMode.Bilinear;
                var pixels = new Color32[texture.width * texture.height];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                squareSprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 8f);
            }
            return squareSprite;
        }

        // ---------------- 程序化皮肤（osu!mania 圆角渐变音符 + Phigros 场地氛围，无外部资源） ----------------

        /// <summary>音符：优先当前皮肤包 note.png，否则程序化圆角渐变（2x1 单位）。</summary>
        internal static Sprite GetNoteSprite()
        {
            Sprite skinned = SkinManager.Load("note");
            if (skinned != null) return skinned;
            if (noteSprite == null)
                noteSprite = Sprite.Create(
                    MakeRoundedRect(96, 48, 15, 3,
                        new Color32(150, 175, 215, 255), new Color32(255, 255, 255, 255), new Color32(210, 235, 255, 255)),
                    new Rect(0f, 0f, 96f, 48f), new Vector2(0.5f, 0.5f), 48f);
            return noteSprite;
        }

        /// <summary>hold 身体：1x1 纵向渐变光带，底部锚点，向上拉伸。</summary>
        internal static Sprite GetHoldBodySprite()
        {
            if (holdBodySprite == null)
            {
                const int w = 64, h = 64;
                var texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
                texture.filterMode = FilterMode.Bilinear;
                var pixels = new Color32[h * w];
                for (int y = 0; y < h; y++)
                {
                    float t = (float)y / h; // 0=底(贴头端,亮) 1=顶(尾端,淡)
                    byte a = (byte)((1f - t * 0.65f) * 235f);
                    byte r = (byte)(115 + 60 * t);
                    byte g = (byte)(185 + 30 * t);
                    byte b = 255;
                    for (int x = 0; x < w; x++) pixels[y * w + x] = new Color32(r, g, b, a);
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                holdBodySprite = Sprite.Create(texture, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0f), 64f);
            }
            return holdBodySprite;
        }

        /// <summary>按键板：优先皮肤包 pad.png，否则程序化圆角。</summary>
        internal static Sprite GetPadSprite()
        {
            Sprite skinned = SkinManager.Load("pad");
            if (skinned != null) return skinned;
            if (padSprite == null)
                padSprite = Sprite.Create(
                    MakeRoundedRect(96, 48, 14, 2,
                        new Color32(255, 255, 255, 22), new Color32(255, 255, 255, 48), new Color32(255, 255, 255, 95)),
                    new Rect(0f, 0f, 96f, 48f), new Vector2(0.5f, 0.5f), 50f); // 原生 1.92x0.96 ≈ 轨宽
            return padSprite;
        }

        /// <summary>判定线辉光：横向柔光带（加法混合，垫在判定线下）。</summary>
        internal static Sprite GetJudgeGlowSprite()
        {
            if (judgeGlowSprite == null)
            {
                const int w = 256, h = 32;
                var texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
                texture.filterMode = FilterMode.Bilinear;
                var pixels = new Color32[h * w];
                for (int y = 0; y < h; y++)
                {
                    float d = (y + 0.5f) / h - 0.5f;
                    float a = Mathf.Exp(-d * d * 60f) * 255f; // 高斯纵向衰减
                    for (int x = 0; x < w; x++) pixels[y * w + x] = new Color32(255, 255, 255, (byte)a);
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                judgeGlowSprite = Sprite.Create(texture, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 32f);
            }
            return judgeGlowSprite;
        }

        /// <summary>场地底板：纵向渐变（底部微亮深蓝 → 顶部透明），给下落区纵深。</summary>
        internal static Sprite GetPanelSprite()
        {
            if (panelSprite == null)
            {
                const int w = 64, h = 128;
                var texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
                texture.filterMode = FilterMode.Bilinear;
                var pixels = new Color32[h * w];
                for (int y = 0; y < h; y++)
                {
                    float t = (float)y / h; // 0=底 1=顶
                    byte a = (byte)((1f - t) * 105f);
                    byte r = (byte)(45 + 25 * (1f - t));
                    byte g = (byte)(65 + 30 * (1f - t));
                    byte b = (byte)(110 + 40 * (1f - t));
                    for (int x = 0; x < w; x++) pixels[y * w + x] = new Color32(r, g, b, a);
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                panelSprite = Sprite.Create(texture, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 16f);
            }
            return panelSprite;
        }

        /// <summary>加法混合材质工厂：Play 早期内置 Particles shader 可能未加载，必须带回退。</summary>
        internal static Material MakeAdditiveMaterial()
        {
            Shader shader = Shader.Find("Particles/Additive");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            return new Material(shader);
        }

        // 圆角矩形 SDF：内部按 y 从 bottom 渐变到 top，边缘 borderPx 内为边框色，1.5px 抗锯齿
        private static Texture2D MakeRoundedRect(int w, int h, int radius, int borderPx, Color32 top, Color32 bottom, Color32 border)
        {
            var texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[h * w];
            float halfW = w * 0.5f, halfH = h * 0.5f;
            float innerW = halfW - radius, innerH = halfH - radius;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float px = x + 0.5f - halfW, py = y + 0.5f - halfH;
                    float qx = Mathf.Max(Mathf.Abs(px) - innerW, 0f);
                    float qy = Mathf.Max(Mathf.Abs(py) - innerH, 0f);
                    float dist = Mathf.Sqrt(qx * qx + qy * qy) + Mathf.Min(Mathf.Max(Mathf.Abs(px) - innerW, Mathf.Abs(py) - innerH), 0f) - radius;
                    float fill = Mathf.Clamp01(0.5f - dist);           // 内部 1 → 外部 0
                    float edge = Mathf.Clamp01(-dist / borderPx);      // 0=贴边 1=内部深处
                    float v = (y + 0.5f) / h;
                    float r = Mathf.Lerp(bottom.r, top.r, v);
                    float g = Mathf.Lerp(bottom.g, top.g, v);
                    float b = Mathf.Lerp(bottom.b, top.b, v);
                    float a = Mathf.Lerp(bottom.a, top.a, v);
                    float br = Mathf.Lerp(border.r, r, edge);
                    float bg = Mathf.Lerp(border.g, g, edge);
                    float bb = Mathf.Lerp(border.b, b, edge);
                    float ba = Mathf.Lerp(Mathf.Max(border.a, a), a, edge);
                    pixels[y * w + x] = new Color32((byte)br, (byte)bg, (byte)bb, (byte)(ba * fill));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        /// <summary>装配谱面、时钟与对象池，并绘制判定线。</summary>
        public void Setup(ChartData chart, Conductor conductor, NotePool pool, float travelTimeSeconds)
        {
            this.chart = chart;
            this.conductor = conductor;
            this.pool = pool;
            travelTime = Mathf.Max(0.05f, travelTimeSeconds);
            sortedNotes.Clear();
            nextIndex = 0;
            live.Clear();
            if (chart != null && chart.Notes != null)
            {
                sortedNotes.AddRange(chart.Notes);
                sortedNotes.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
            BuildPlayfield();
        }

        private void Update()
        {
            if (chart == null || conductor == null) return;
            if (Game != null && !Game.IsPlaying) return; // 未开始/已停止不生成
            double now = conductor.SongTimeSeconds;
            while (nextIndex < sortedNotes.Count && sortedNotes[nextIndex].Time - now <= travelTime)
            {
                Spawn(sortedNotes[nextIndex]);
                nextIndex++;
            }
            UpdateJudgeLinePulse(now);
            UpdateKeycaps();
        }

        // 判定线随节拍呼吸脉冲（视觉设计受 Phira/Phigros 风格启发，自行实现）：
        // 每个 beat 时刻强度最高，随后按三次方衰减；仅缩放横轴与调亮，不改判定逻辑
        private void UpdateJudgeLinePulse(double songTime)
        {
            if (judgeLine == null || chart.Bpm <= 0.0) return;
            double period = 60.0 / chart.Bpm;
            double phase = (songTime - chart.OffsetSeconds) % period;
            if (phase < 0) phase += period;
            float k = 1f - (float)(phase / period);
            k = k * k * k; // 衰减曲线
            judgeLine.transform.localScale = new Vector3(
                judgeLineBaseScale.x * (1f + 0.05f * k),
                judgeLineBaseScale.y,
                1f);
            judgeLine.color = new Color(1f, 1f, 1f, 0.85f + 0.15f * k);
        }

        private void Spawn(NoteData data)
        {
            if (data == null || data.Lane < 0 || data.Lane >= LaneCount || pool == null) return;
            GameObject go = pool.Rent();
            if (go == null) return;
            go.SetActive(true);
            NoteController controller = go.GetComponent<NoteController>();
            if (controller == null) controller = go.AddComponent<NoteController>();
            controller.Initialize(data, this);
            live[data] = controller;
        }

        /// <summary>头判结果转发给对应音符控制器（表现）。</summary>
        public void NotifyJudged(NoteData note, Judgement judgement)
        {
            if (note != null && live.TryGetValue(note, out NoteController controller)) controller.OnHeadJudged(judgement);
        }

        /// <summary>自动 Miss 转发给对应音符控制器（变灰继续下落出屏）。</summary>
        public void NotifyMissed(NoteData note)
        {
            if (note != null && live.TryGetValue(note, out NoteController controller)) controller.OnMissed();
        }

        /// <summary>hold 尾判上报（由 NoteController 驱动调用）。</summary>
        internal void RegisterTail(NoteData note, Judgement judgement)
        {
            if (Score != null) Score.Register(judgement, true);
            if (Game != null) Game.NotifyJudgementFeedback(note, judgement, true);
        }

        /// <summary>音符结束（命中/漏过/出屏），归还对象池。</summary>
        internal void OnNoteFinished(NoteData note, NoteController controller)
        {
            live.Remove(note);
            if (pool != null) pool.Return(controller.gameObject);
        }

        /// <summary>强制回收全部存活音符（停止/切歌时）。</summary>
        public void ReleaseAll()
        {
            var controllers = new List<NoteController>(live.Values);
            live.Clear();
            foreach (NoteController controller in controllers) controller.ForceRelease();
        }

        // 绘制场地底板 + 4 条轨道参考线 + 判定线辉光/本体 + 按键板
        private void BuildPlayfield()
        {
            if (playfieldRoot != null) Destroy(playfieldRoot);
            playfieldRoot = new GameObject("Playfield");
            playfieldRoot.transform.SetParent(transform, false);

            // 场地底板：底部微亮的渐变，垫在频谱之下（Phigros 式纵深）
            var panel = CreateSprite("LanePanel", playfieldRoot.transform,
                new Vector3(0f, (JudgeLineY + SpawnY) * 0.5f, 0f),
                new Vector2(LaneWidth * LaneCount + 0.6f, FallHeight + 2.8f), Color.white, -4);
            panel.sprite = GetPanelSprite();

            for (int lane = 0; lane < LaneCount; lane++)
            {
                CreateSprite("LaneGuide" + lane, playfieldRoot.transform,
                    new Vector3(LaneX(lane), (JudgeLineY + SpawnY) * 0.5f, 0f),
                    new Vector2(0.06f, FallHeight + 1.5f), new Color(LaneColors[lane].r, LaneColors[lane].g, LaneColors[lane].b, 0.30f), 0);
            }

            // 判定线 = 加法辉光（宽柔光） + 白色本体（节拍脉冲只作用本体）
            var glow = CreateSprite("JudgeGlow", playfieldRoot.transform,
                new Vector3(0f, JudgeLineY, 0f),
                new Vector2(LaneWidth * LaneCount + 1.5f, 0.9f), new Color(0.65f, 0.85f, 1f, 0.55f), 1);
            glow.material = MakeAdditiveMaterial();
            glow.sprite = GetJudgeGlowSprite();

            CreateSprite("JudgeLine", playfieldRoot.transform,
                new Vector3(0f, JudgeLineY, 0f),
                new Vector2(LaneWidth * LaneCount, 0.14f), Color.white, 2);
            judgeLine = playfieldRoot.transform.Find("JudgeLine")?.GetComponent<SpriteRenderer>();
            if (judgeLine != null) judgeLineBaseScale = judgeLine.transform.localScale;

            BuildKeycaps();
        }

        // 判定区下方：每条轨道一个可点击的"按键板"（D/F/J/K），鼠标/触屏/键盘均可触发——
        // 按住时整板亮青、字母弹大，按钮与轨道一一对应
        private void BuildKeycaps()
        {
            keycaps = new TextMesh[LaneCount];
            lanePads = new SpriteRenderer[LaneCount];
            Font font = GetBuiltinFont();
            for (int lane = 0; lane < LaneCount; lane++)
            {
                lanePads[lane] = CreateSprite("KeyPad" + lane, playfieldRoot.transform,
                    new Vector3(LaneX(lane), JudgeLineY - 0.85f, 0f),
                    new Vector2(LaneWidth * 0.96f, 0.95f), PadIdleColor, 2);
                lanePads[lane].sprite = GetPadSprite();

                var go = new GameObject("Keycap" + lane);
                go.transform.SetParent(playfieldRoot.transform, false);
                go.transform.localPosition = new Vector3(LaneX(lane), JudgeLineY - 0.85f, 0f);
                var tm = go.AddComponent<TextMesh>();
                tm.text = LaneKeyNames[lane];
                tm.font = font;
                tm.fontSize = 64;
                tm.characterSize = 0.15f;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                tm.color = Color.Lerp(LaneColors[lane], Color.white, 0.45f);
                go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                keycaps[lane] = tm;
            }
        }

        private static Font GetBuiltinFont()
        {
            try { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
            catch (Exception) { return Resources.GetBuiltinResource<Font>("Arial.ttf"); }
        }

        // 按住反馈：按键板亮轨色 + 字母提亮放大（IsLaneHeld 内部会刷新输入状态，与判定同源）
        private void UpdateKeycaps()
        {
            if (keycaps == null || lanePads == null || Game == null) return;
            for (int lane = 0; lane < keycaps.Length; lane++)
            {
                bool held = Game.IsLaneHeld(lane);
                keycaps[lane].color = held ? KeycapPressedColor : Color.Lerp(LaneColors[lane], Color.white, 0.45f);
                keycaps[lane].transform.localScale = held ? new Vector3(1.18f, 1.18f, 1f) : Vector3.one;
                Color pad = held
                    ? new Color(LaneColors[lane].r * 0.8f, LaneColors[lane].g * 0.8f, LaneColors[lane].b * 0.8f, PadPressedColor.a)
                    : new Color(LaneColors[lane].r, LaneColors[lane].g, LaneColors[lane].b, PadIdleColor.a * 1.6f);
                lanePads[lane].color = pad;
            }
        }

        private static SpriteRenderer CreateSprite(string name, Transform parent, Vector3 localPosition, Vector2 size, Color color, int sortingOrder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = GetSquareSprite();
            renderer.color = color;
            renderer.sortingOrder = sortingOrder;
            return renderer;
        }
    }
}
