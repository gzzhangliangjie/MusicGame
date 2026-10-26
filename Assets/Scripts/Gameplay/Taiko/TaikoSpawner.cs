using System.Collections.Generic;
using MusicGame.Core;
using MusicGame.Pure.Model;
using UnityEngine;

namespace MusicGame.Gameplay.Taiko
{
    /// <summary>
    /// 太鼓模式生成器：音符从右向左卷向左侧鼓面，绘制鼓面/命中区/底部咚咔鼓垫。
    /// 设计参考 osu!lazer Taiko ruleset 与 OpenTaiko（均为 MIT），代码为独立实现。
    /// </summary>
    public class TaikoSpawner : MonoBehaviour
    {
        /// <summary>鼓面命中横坐标。</summary>
        public const float HitX = -2.4f;
        /// <summary>音符生成横坐标（屏幕右外）。</summary>
        public const float SpawnX = 8.2f;
        /// <summary>音符纵坐标。</summary>
        public const float NoteY = 0.9f;

        internal ScoreSystem Score { get; set; }
        internal GameManager Game { get; set; }
        internal NotePool Pool { get; set; }
        internal float TravelTime => travelTime;

        private readonly List<NoteData> sortedNotes = new List<NoteData>();
        private readonly Dictionary<NoteData, TaikoNote> live = new Dictionary<NoteData, TaikoNote>();
        private ChartData chart;
        private Conductor conductor;
        private NotePool pool;
        private float travelTime = 1f;
        private int nextIndex;
        private GameObject playfieldRoot;
        private SpriteRenderer padDon;
        private SpriteRenderer padKa;
        private bool padsSkinned;

        private static readonly Color PadIdleDon = new Color(0.92f, 0.28f, 0.24f, 0.55f);
        private static readonly Color PadIdleKa = new Color(0.25f, 0.65f, 0.95f, 0.55f);
        private static readonly Color PadPressedDon = new Color(1f, 0.5f, 0.45f, 1f);
        private static readonly Color PadPressedKa = new Color(0.55f, 0.85f, 1f, 1f);

        private static Sprite discSprite;
        private static Sprite rollStripSprite;

        /// <summary>取某类音符的贴图：优先皮肤包（don/katsu/roll），皮肤贴图自带配色；否则程序化圆盘（由调用方着色）。</summary>
        internal Sprite GetKindSprite(TaikoNote.Kind kind, out bool skinned)
        {
            string key = kind == TaikoNote.Kind.Don ? "don" : kind == TaikoNote.Kind.Katsu ? "katsu" : "roll";
            Sprite s = SkinManager.Load(key);
            skinned = s != null;
            return s != null ? s : GetDiscSprite();
        }

        /// <summary>鼓面/音符圆盘（优先皮肤包 disc.png）。</summary>
        internal static Sprite GetDiscSprite()
        {
            Sprite skinned = SkinManager.Load("disc");
            if (skinned != null) return skinned;
            if (discSprite == null)
            {
                const int size = 128;
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
                        float a = Mathf.Clamp01((0.985f - r) * size * 0.5f / 1.5f); // 1.5px 抗锯齿
                        // 内圈略亮，边缘留一圈深色鼓沿
                        float shade = r > 0.86f ? 0.72f : r < 0.42f ? 1.12f : 1f;
                        pixels[y * size + x] = new Color32(
                            (byte)Mathf.Clamp(255 * shade, 0, 255),
                            (byte)Mathf.Clamp(255 * shade, 0, 255),
                            (byte)Mathf.Clamp(255 * shade, 0, 255),
                            (byte)(a * 255f));
                    }
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                discSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 64f);
                discSprite.name = "TaikoDisc";
            }
            return discSprite;
        }

        /// <summary>连击条身体（1x1 纵向柔边黄条）。</summary>
        internal static Sprite GetRollStripSprite()
        {
            if (rollStripSprite == null)
            {
                const int w = 32, h = 64;
                var texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
                texture.filterMode = FilterMode.Bilinear;
                var pixels = new Color32[h * w];
                for (int y = 0; y < h; y++)
                {
                    float dy = Mathf.Abs((y + 0.5f) / h - 0.5f) * 2f; // 0 中心 → 1 边缘
                    byte a = (byte)((1f - dy * dy) * 255f);
                    for (int x = 0; x < w; x++) pixels[y * w + x] = new Color32(255, 255, 255, a);
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                rollStripSprite = Sprite.Create(texture, new Rect(0f, 0f, w, h), new Vector2(0f, 0.5f), 64f);
                rollStripSprite.name = "TaikoRollStrip";
            }
            return rollStripSprite;
        }

        /// <summary>装配谱面（Lane 已重映射 0=咚 1=咔 2=连击）、时钟与对象池，绘制场地。</summary>
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
            if (Game != null && !Game.IsPlaying) return;
            double now = conductor.SongTimeSeconds;
            while (nextIndex < sortedNotes.Count && sortedNotes[nextIndex].Time - now <= travelTime)
            {
                Spawn(sortedNotes[nextIndex]);
                nextIndex++;
            }
            UpdatePads();
        }

        private void Spawn(NoteData data)
        {
            if (data == null || data.Lane < 0 || data.Lane > 2 || pool == null) return;
            GameObject go = pool.Rent();
            if (go == null) return;
            go.SetActive(true);
            TaikoNote note = go.GetComponent<TaikoNote>();
            if (note == null) note = go.AddComponent<TaikoNote>();
            note.Initialize(data, this);
            live[data] = note;
        }

        /// <summary>头判结果转发。</summary>
        public void NotifyJudged(NoteData note, Judgement judgement)
        {
            if (note != null && live.TryGetValue(note, out TaikoNote n)) n.OnHeadJudged(judgement);
        }

        /// <summary>自动 Miss 转发。</summary>
        public void NotifyMissed(NoteData note)
        {
            if (note != null && live.TryGetValue(note, out TaikoNote n)) n.OnMissed();
        }

        /// <summary>连击条尾判上报（由 TaikoNote 调用）。</summary>
        internal void RegisterTail(NoteData note, Judgement judgement)
        {
            if (Score != null) Score.Register(judgement, true);
            if (Game != null) Game.NotifyJudgementFeedback(note, judgement, true);
        }

        /// <summary>音符结束，归还对象池。</summary>
        internal void OnNoteFinished(NoteData note, TaikoNote noteComponent)
        {
            live.Remove(note);
            if (pool != null) pool.Return(noteComponent.gameObject);
        }

        /// <summary>强制回收全部存活音符。</summary>
        public void ReleaseAll()
        {
            var notes = new List<TaikoNote>(live.Values);
            live.Clear();
            foreach (TaikoNote note in notes) note.ForceRelease();
        }

        // 底部咚/咔鼓垫：按住发亮（皮肤贴图原色只做透明度闪烁，程序化垫做颜色切换）
        private void UpdatePads()
        {
            if (padDon == null || Game == null) return;
            if (padsSkinned)
            {
                float aDon = Game.IsLaneHeld(0) ? 1f : 0.62f;
                float aKa = Game.IsLaneHeld(1) ? 1f : 0.62f;
                var d = padDon.color; padDon.color = new Color(d.r, d.g, d.b, aDon);
                var k = padKa.color; padKa.color = new Color(k.r, k.g, k.b, aKa);
            }
            else
            {
                padDon.color = Game.IsLaneHeld(0) ? PadPressedDon : PadIdleDon;
                padKa.color = Game.IsLaneHeld(1) ? PadPressedKa : PadIdleKa;
            }
        }

        private void BuildPlayfield()
        {
            if (playfieldRoot != null) Destroy(playfieldRoot);
            playfieldRoot = new GameObject("TaikoPlayfield");
            playfieldRoot.transform.SetParent(transform, false);

            // 场地底板（复用四轨皮肤面板）
            var panel = CreateSprite("Panel", playfieldRoot.transform,
                new Vector3(2f, NoteY, 0f), new Vector2(14.5f, 3.2f), Color.white, -4);
            panel.sprite = NoteSpawner.GetPanelSprite();

            // 命中区竖标线
            CreateSprite("HitLine", playfieldRoot.transform,
                new Vector3(HitX, NoteY, 0f), new Vector2(0.08f, 3.4f), new Color(1f, 1f, 1f, 0.35f), 0);

            // 左侧鼓面：优先皮肤包 drum.png（原色），否则程序化红圆盘
            Sprite drumTex = SkinManager.Load("drum");
            var drum = CreateSprite("Drum", playfieldRoot.transform,
                new Vector3(HitX - 0.85f, NoteY, 0f), new Vector2(1.7f, 1.7f),
                drumTex != null ? Color.white : new Color(0.85f, 0.2f, 0.18f, 0.95f), 1);
            drum.sprite = drumTex != null ? drumTex : GetDiscSprite();

            // 底部咚/咔鼓垫：优先皮肤包 don/katsu 贴图（原色，按下提亮）
            Sprite donTex = SkinManager.Load("don");
            Sprite kaTex = SkinManager.Load("katsu");
            padsSkinned = donTex != null && kaTex != null;
            padDon = CreateSprite("PadDon", playfieldRoot.transform,
                new Vector3(-2.0f, -5.0f, 0f), new Vector2(2.2f, 1.15f),
                padsSkinned ? new Color(1f, 1f, 1f, 0.62f) : PadIdleDon, 2);
            padDon.sprite = padsSkinned ? donTex : GetDiscSprite();
            padKa = CreateSprite("PadKa", playfieldRoot.transform,
                new Vector3(2.0f, -5.0f, 0f), new Vector2(2.2f, 1.15f),
                padsSkinned ? new Color(1f, 1f, 1f, 0.62f) : PadIdleKa, 2);
            padKa.sprite = padsSkinned ? kaTex : GetDiscSprite();
            CreatePadLabel("咚", new Vector3(-2.0f, -5.0f, 0f), 1);
            CreatePadLabel("咔", new Vector3(2.0f, -5.0f, 0f), 1);
        }

        private void CreatePadLabel(string text, Vector3 localPos, int order)
        {
            var go = new GameObject("PadLabel_" + text);
            go.transform.SetParent(playfieldRoot.transform, false);
            go.transform.localPosition = localPos;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.font = GetBuiltinFont();
            tm.fontSize = 64;
            tm.characterSize = 0.14f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = new Color(1f, 1f, 1f, 0.95f);
            go.GetComponent<MeshRenderer>().sharedMaterial = tm.font.material;
            go.GetComponent<MeshRenderer>().sortingOrder = order + 3;
        }

        private static Font GetBuiltinFont()
        {
            try { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
            catch { return Resources.GetBuiltinResource<Font>("Arial.ttf"); }
        }

        private static SpriteRenderer CreateSprite(string name, Transform parent, Vector3 localPosition, Vector2 size, Color color, int sortingOrder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = NoteSpawner.GetSquareSprite();
            renderer.color = color;
            renderer.sortingOrder = sortingOrder;
            return renderer;
        }
    }
}
