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
        private SpriteRenderer drumFace;      // 左侧鼓面
        private Vector3 drumBaseScale;
        private Vector3 drumBasePos;
        private Coroutine drumPunch;
        private Vector3 padDonBaseScale, padDonBasePos, padKaBaseScale, padKaBasePos;
        private readonly bool[] padWasHeld = new bool[2];
        private readonly Coroutine[] padPunch = new Coroutine[2];

        private static readonly Color PadIdleDon = new Color(0.92f, 0.28f, 0.24f, 0.55f);
        private static readonly Color PadIdleKa = new Color(0.25f, 0.65f, 0.95f, 0.55f);
        private static readonly Color PadPressedDon = new Color(1f, 0.5f, 0.45f, 1f);
        private static readonly Color PadPressedKa = new Color(0.55f, 0.85f, 1f, 1f);

        private static Sprite discSprite;
        private static Sprite rollStripSprite;
        private static Sprite balloonSprite;

        /// <summary>取某类音符的贴图：优先皮肤包（don/katsu/roll），皮肤贴图自带配色；否则程序化圆盘/气球（由调用方着色）。</summary>
        internal Sprite GetKindSprite(TaikoNote.Kind kind, out bool skinned)
        {
            string key = kind == TaikoNote.Kind.Don ? "don" : kind == TaikoNote.Kind.Katsu ? "katsu" : "roll";
            Sprite s = SkinManager.Load(key);
            skinned = s != null;
            if (s != null) return s;
            return kind == TaikoNote.Kind.Roll ? GetBalloonSprite() : GetDiscSprite();
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

        /// <summary>气球贴图（程序化：粉色气球 + 左上高光 + 底部结扣），连击条的原版气球样式头。</summary>
        internal static Sprite GetBalloonSprite()
        {
            if (balloonSprite == null)
            {
                const int w = 128, h = 160;
                var texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
                texture.filterMode = FilterMode.Bilinear;
                var pixels = new Color32[w * h];
                float cx = w * 0.5f, cy = 74f, rx = 50f, ry = 58f;
                // 灰阶底色（白身 + 暗缘 + 左上高光），最终颜色由 TaikoNote 用 RollColor 染色——黄=连击
                var topCol = new Color32(240, 233, 218, 255);
                var botCol = new Color32(208, 196, 170, 255);
                var rimCol = new Color32(108, 90, 60, 255);
                var hiCol = new Color32(255, 255, 255, 255);
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float dx = (x + 0.5f - cx) / rx, dy = (y + 0.5f - cy) / ry;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        byte a = (byte)(Mathf.Clamp01((1f - r) * Mathf.Min(rx, ry) / 3f) * 255f);
                        Color32 col;
                        if (r > 0.88f)
                        {
                            col = rimCol; // 深色外缘
                        }
                        else
                        {
                            float grad = Mathf.Clamp01((y + 0.5f - (cy - ry)) / (2f * ry));
                            col = Color32.Lerp(botCol, topCol, grad);
                            // 左上高光斑
                            float hx = (x + 0.5f - (cx - rx * 0.34f)) / 15f;
                            float hy = (y + 0.5f - (cy + ry * 0.38f)) / 15f;
                            float hi = Mathf.Clamp01((1f - Mathf.Sqrt(hx * hx + hy * hy)) * 1.4f) * 0.85f;
                            col = Color32.Lerp(col, hiCol, hi);
                        }
                        // 底部结扣（倒三角）
                        if (y < cy - ry + 2f && y > cy - ry - 14f)
                        {
                            float half = Mathf.Max(1.5f, (y - (cy - ry - 14f)) * 0.45f);
                            if (Mathf.Abs(x + 0.5f - cx) <= half) { col = rimCol; a = 255; }
                        }
                        pixels[y * w + x] = new Color32(col.r, col.g, col.b, a);
                    }
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                balloonSprite = Sprite.Create(texture, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 80f);
                balloonSprite.name = "TaikoBalloon";
            }
            return balloonSprite;
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

        /// <summary>头判结果转发；命中（非 Miss）附带鼓面打击反馈。</summary>
        public void NotifyJudged(NoteData note, Judgement judgement)
        {
            if (note != null && live.TryGetValue(note, out TaikoNote n))
            {
                n.OnHeadJudged(judgement);
                if (judgement != Judgement.Miss) PunchDrum();
            }
        }

        /// <summary>鼓面打击反馈：先被敲扁（横向宽、纵向扁），再衰减回弹 + 左右震动 + 微摆。</summary>
        internal void PunchDrum()
        {
            if (drumFace == null) return;
            if (drumPunch != null) StopCoroutine(drumPunch);
            drumPunch = StartCoroutine(DrumPunchRoutine());
        }

        private System.Collections.IEnumerator DrumPunchRoutine()
        {
            const float Dur = 0.22f;
            float t = 0f;
            while (t < Dur)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / Dur);
                float decay = 1f - k;
                // 敲扁回弹：余弦衰减震荡，k=0 时压扁最大
                float wobble = Mathf.Cos(k * Mathf.PI * 3f) * decay;
                drumFace.transform.localScale = new Vector3(
                    drumBaseScale.x * (1f + 0.10f * wobble),
                    drumBaseScale.y * (1f - 0.14f * wobble), 1f);
                // 鼓面左右震动 + 微微摇摆
                drumFace.transform.localPosition = new Vector3(
                    drumBasePos.x + Mathf.Sin(k * Mathf.PI * 6f) * 0.06f * decay, drumBasePos.y, drumBasePos.z);
                drumFace.transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(k * Mathf.PI * 4f) * 2.5f * decay);
                yield return null;
            }
            drumFace.transform.localScale = drumBaseScale;
            drumFace.transform.localPosition = drumBasePos;
            drumFace.transform.localRotation = Quaternion.identity;
            drumPunch = null;
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

        // 底部咚/咔鼓垫：按住发亮，按下瞬间 PunchDrum 同款敲扁回弹+震动（用户反馈只闪一下不够）
        private void UpdatePads()
        {
            if (padDon == null || Game == null) return;
            bool donHeld = Game.IsLaneHeld(0);
            bool kaHeld = Game.IsLaneHeld(1);
            if (donHeld && !padWasHeld[0]) PunchPad(0);
            if (kaHeld && !padWasHeld[1]) PunchPad(1);
            padWasHeld[0] = donHeld;
            padWasHeld[1] = kaHeld;
            if (padsSkinned)
            {
                // 原色皮肤：按住提亮 + 不透明（敲击震动归 PadPunchRoutine）
                padDon.color = donHeld ? new Color(1.3f, 1.3f, 1.3f, 1f) : new Color(1f, 1f, 1f, 0.62f);
                padKa.color = kaHeld ? new Color(1.3f, 1.3f, 1.3f, 1f) : new Color(1f, 1f, 1f, 0.62f);
            }
            else
            {
                padDon.color = donHeld ? PadPressedDon : PadIdleDon;
                padKa.color = kaHeld ? PadPressedKa : PadIdleKa;
            }
        }

        private void PunchPad(int idx)
        {
            var pad = idx == 0 ? padDon : padKa;
            if (pad == null) return;
            if (padPunch[idx] != null) StopCoroutine(padPunch[idx]);
            padPunch[idx] = StartCoroutine(PadPunchRoutine(idx));
        }

        private System.Collections.IEnumerator PadPunchRoutine(int idx)
        {
            var pad = idx == 0 ? padDon : padKa;
            Vector3 baseScale = idx == 0 ? padDonBaseScale : padKaBaseScale;
            Vector3 basePos = idx == 0 ? padDonBasePos : padKaBasePos;
            const float Dur = 0.2f;
            float t = 0f;
            while (t < Dur && pad != null)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / Dur);
                float decay = 1f - k;
                float wobble = Mathf.Cos(k * Mathf.PI * 3f) * decay;
                pad.transform.localScale = new Vector3(
                    baseScale.x * (1f + 0.10f * wobble),
                    baseScale.y * (1f - 0.12f * wobble), 1f);
                pad.transform.localPosition = new Vector3(
                    basePos.x + Mathf.Sin(k * Mathf.PI * 6f) * 0.05f * decay, basePos.y, basePos.z);
                yield return null;
            }
            if (pad != null)
            {
                pad.transform.localScale = baseScale;
                pad.transform.localPosition = basePos;
            }
            padPunch[idx] = null;
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

            // 左侧鼓面：中心正对判定点 HitX——音符滚到鼓面中心即判定，视觉与判定一致（用户反馈原先是鼓面右缘）
            // 优先皮肤包 drum.png（原色，按高度等比缩放防拉伸），否则程序化红圆盘
            Sprite drumTex = SkinManager.Load("drum");
            var drum = CreateSprite("Drum", playfieldRoot.transform,
                new Vector3(HitX, NoteY, 0f), new Vector2(1.8f, 1.8f),
                drumTex != null ? Color.white : new Color(0.85f, 0.2f, 0.18f, 0.95f), 1);
            if (drumTex != null) FitSprite(drum, drumTex, 1.8f);
            else drum.sprite = GetDiscSprite();
            drumFace = drum;
            drumBaseScale = drum.transform.localScale;
            drumBasePos = drum.transform.localPosition;

            // 底部咚/咔鼓垫：优先皮肤包 don/katsu 圆形小鼓（原色，等比缩放，按下提亮）
            Sprite donTex = SkinManager.Load("don");
            Sprite kaTex = SkinManager.Load("katsu");
            padsSkinned = donTex != null && kaTex != null;
            padDon = CreateSprite("PadDon", playfieldRoot.transform,
                new Vector3(-2.0f, -5.0f, 0f), new Vector2(1.5f, 1.5f),
                padsSkinned ? new Color(1f, 1f, 1f, 0.62f) : PadIdleDon, 2);
            if (padsSkinned) FitSprite(padDon, donTex, 1.5f);
            else padDon.sprite = GetDiscSprite();
            padKa = CreateSprite("PadKa", playfieldRoot.transform,
                new Vector3(2.0f, -5.0f, 0f), new Vector2(1.5f, 1.5f),
                padsSkinned ? new Color(1f, 1f, 1f, 0.62f) : PadIdleKa, 2);
            if (padsSkinned) FitSprite(padKa, kaTex, 1.5f);
            else padKa.sprite = GetDiscSprite();
            // 记录鼓垫基准变换：敲击 punch 动画（敲扁回弹+震动）基于此
            padDonBaseScale = padDon.transform.localScale;
            padDonBasePos = padDon.transform.localPosition;
            padKaBaseScale = padKa.transform.localScale;
            padKaBasePos = padKa.transform.localPosition;

            // 键位标识（与节奏大师键帽同款字母样式）：咚=F/J（左） 咔=D/K（右），不然不知道按哪个垫
            CreatePadKeyLabel("F J", new Vector3(-2.0f, -5.0f, 0f));
            CreatePadKeyLabel("D K", new Vector3(2.0f, -5.0f, 0f));
        }

        /// <summary>鼓垫键位字母：Impact + 黑投影双层（NoteSpawner 键帽同款），小号半透明压在鼓面中心。</summary>
        private void CreatePadKeyLabel(string text, Vector3 localPos)
        {
            Font font = NoteSpawner.GetKeycapFont();
            var shadowGo = new GameObject("PadKeyShadow");
            shadowGo.transform.SetParent(playfieldRoot.transform, false);
            shadowGo.transform.localPosition = localPos + new Vector3(0.025f, -0.035f, 0f);
            FillPadKeyText(shadowGo, text, font, 3, new Color(0f, 0f, 0f, 0.5f));
            var go = new GameObject("PadKeyLabel");
            go.transform.SetParent(playfieldRoot.transform, false);
            go.transform.localPosition = localPos;
            FillPadKeyText(go, text, font, 4, new Color(1f, 1f, 1f, 0.82f));
        }

        private static void FillPadKeyText(GameObject go, string text, Font font, int order, Color color)
        {
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.font = font;
            tm.fontSize = 64;
            tm.characterSize = 0.085f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = font.material;
            renderer.sortingOrder = order;
        }

        /// <summary>皮肤贴图替换占位方块后按目标高度等比缩放（贴图自带宽高比，禁止非均匀拉伸）。</summary>
        private static void FitSprite(SpriteRenderer renderer, Sprite sprite, float targetHeight)
        {
            renderer.sprite = sprite;
            var size = sprite.bounds.size;
            float k = targetHeight / Mathf.Max(0.01f, size.y);
            renderer.transform.localScale = new Vector3(size.x * k, size.y * k, 1f);
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
