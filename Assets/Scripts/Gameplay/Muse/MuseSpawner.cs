using System.Collections.Generic;
using MusicGame.Core;
using MusicGame.Pure.Model;
using UnityEngine;

namespace MusicGame.Gameplay.Muse
{
    /// <summary>
    /// 跑酷模式（Muse Dash 式）生成器：角色在左侧奔跑，敌人从右向左滚来，分地面/空中两条轨道。
    /// 绘制滚动条纹跑步带/空中虚线轨道/键位标签，负责生成调度、harmony 双押标记与角色/斩击反馈。
    /// 玩法参考 Muse Dash（Perpendicular 出品），实现为独立 Unity 代码。
    /// </summary>
    public class MuseSpawner : MonoBehaviour
    {
        /// <summary>角色判定横坐标（敌人滚到此处判定）。</summary>
        public const float CharacterX = -3.2f;
        /// <summary>音符生成横坐标（屏幕右外）。</summary>
        public const float SpawnX = 8.6f;
        /// <summary>地面轨道纵坐标（lane 0）。</summary>
        public const float GroundY = -2.0f;
        /// <summary>空中轨道纵坐标（lane 1）。</summary>
        public const float AirY = 1.6f;

        internal ScoreSystem Score { get; set; }
        internal GameManager Game { get; set; }
        internal NotePool Pool { get; set; }
        internal float TravelTime => travelTime;

        private readonly List<NoteData> sortedNotes = new List<NoteData>();
        private readonly Dictionary<NoteData, MuseNote> live = new Dictionary<NoteData, MuseNote>();
        private readonly HashSet<NoteData> harmony = new HashSet<NoteData>();
        private ChartData chart;
        private Conductor conductor;
        private NotePool pool;
        private float travelTime = 1f;
        private int nextIndex;
        private GameObject playfieldRoot;
        private MuseRunner runner;
        private readonly List<Transform> stripes = new List<Transform>();
        private readonly List<Transform> dashes = new List<Transform>();
        private float bandLeft = -9.6f, bandRight = 9.6f;
        // 红白机风按钮垫（lane0=地面黑帽在下 / lane1=空中红帽在上）
        private readonly Transform[] padCaps = new Transform[2];
        private readonly SpriteRenderer[] padCapRenderers = new SpriteRenderer[2];
        private readonly Color[] padCapColors = new Color[2];
        private readonly Vector3[] padCapBaseScale = new Vector3[2];
        private readonly Vector3[] padPos = new Vector3[2];
        private readonly bool[] padWasHeld = new bool[2];
        private readonly float[] padPunchAge = { 99f, 99f }; // 弹跳 age（秒，0=刚按下/刚命中）

        private static readonly Color PadBaseColor = new Color(0.10f, 0.10f, 0.12f, 0.96f);
        private static readonly Color PadGroundCap = new Color(0.24f, 0.24f, 0.28f, 1f); // 下黑
        private static readonly Color PadAirCap = new Color(0.86f, 0.20f, 0.22f, 1f);    // 上红

        private static Sprite groundEnemySprite;
        private static Sprite airEnemySprite;

        /// <summary>装配谱面（Lane 已重映射 0=地面 1=空中）、时钟与对象池，绘制场地。</summary>
        public void Setup(ChartData chart, Conductor conductor, NotePool pool, float travelTimeSeconds)
        {
            this.chart = chart;
            this.conductor = conductor;
            this.pool = pool;
            travelTime = Mathf.Max(0.05f, travelTimeSeconds);
            sortedNotes.Clear();
            nextIndex = 0;
            live.Clear();
            harmony.Clear();
            if (chart != null && chart.Notes != null)
            {
                sortedNotes.AddRange(chart.Notes);
                sortedNotes.Sort((a, b) => a.Time.CompareTo(b.Time));
                MarkHarmony();
            }
            BuildPlayfield();
        }

        /// <summary>同刻跨轨成对（0+1 各一个）的音符标为 harmony 双押（金色描边，判定仍按各自轨道独立进行）。</summary>
        private void MarkHarmony()
        {
            for (int i = 0; i < sortedNotes.Count; i++)
            {
                NoteData a = sortedNotes[i];
                if (a.Type == "hold" && a.Duration > 0.0) continue;
                for (int j = i + 1; j < sortedNotes.Count; j++)
                {
                    NoteData b = sortedNotes[j];
                    if (b.Time > a.Time) break; // 已排序，时刻不同即止
                    if (b.Type == "hold" && b.Duration > 0.0) continue;
                    if (b.Time == a.Time && a.Lane != b.Lane)
                    {
                        harmony.Add(a);
                        harmony.Add(b);
                    }
                }
            }
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
            UpdateScenery();
            UpdatePads();
        }

        private void Spawn(NoteData data)
        {
            if (data == null || data.Lane < 0 || data.Lane > 1 || pool == null) return;
            GameObject go = pool.Rent();
            if (go == null) return;
            go.SetActive(true);
            MuseNote note = go.GetComponent<MuseNote>();
            if (note == null) note = go.AddComponent<MuseNote>();
            note.Initialize(data, this);
            live[data] = note;
        }

        /// <summary>头判结果转发；命中（非 Miss）附带角色攻击动画与按钮垫命中特效。</summary>
        public void NotifyJudged(NoteData note, Judgement judgement)
        {
            if (note != null && live.TryGetValue(note, out MuseNote n))
            {
                n.OnHeadJudged(judgement);
                if (judgement != Judgement.Miss)
                {
                    if (runner != null) runner.Attack(note.Lane);
                    PadHitFx(note.Lane);
                }
            }
        }

        /// <summary>自动 Miss 转发（敌人撞过角色，附带角色受击抖动）。</summary>
        public void NotifyMissed(NoteData note)
        {
            if (note != null && live.TryGetValue(note, out MuseNote n)) n.OnMissed();
        }

        /// <summary>敌人撞过角色的回调（MuseNote.OnMissed 调用）：角色抖动闪红。</summary>
        internal void NotifyEnemyPassed()
        {
            if (runner != null) runner.Flinch();
        }

        /// <summary>长条怪尾判上报（由 MuseNote 调用）。</summary>
        internal void RegisterTail(NoteData note, Judgement judgement)
        {
            if (Score != null) Score.Register(judgement, true);
            if (Game != null) Game.NotifyJudgementFeedback(note, judgement, true);
        }

        /// <summary>音符结束，归还对象池。</summary>
        internal void OnNoteFinished(NoteData note, MuseNote noteComponent)
        {
            live.Remove(note);
            if (pool != null) pool.Return(noteComponent.gameObject);
        }

        internal bool IsHarmony(NoteData note) => note != null && harmony.Contains(note);

        /// <summary>敌人本体贴图装配，两级回退：① 原版图块（本地 Rom/CHR：栗子怪/金币，不入仓库）
        /// ② 程序化自绘贴图（保底，仓库缺失 Rom 时也有完整视觉）。</summary>
        internal void FitEnemy(SpriteRenderer renderer, int lane, float targetHeight)
        {
            if (MuseSmbSkin.Available)
                renderer.sprite = lane == 0 ? MuseSmbSkin.Goomba() : MuseSmbSkin.Coin();
            else
                renderer.sprite = lane == 0 ? GetGroundEnemySprite() : GetAirEnemySprite();
            var size = renderer.sprite.bounds.size;
            float k = targetHeight / Mathf.Max(0.01f, size.y);
            renderer.transform.localScale = new Vector3(size.x * k, size.y * k, 1f);
        }

        /// <summary>强制回收全部存活敌人。</summary>
        public void ReleaseAll()
        {
            var notes = new List<MuseNote>(live.Values);
            live.Clear();
            foreach (MuseNote note in notes) note.ForceRelease();
        }

        // 地面条纹带随卷动速度向左滚动（与敌人同速 → 跑步带），空中虚线以 0.35 倍速视差滚动。
        // 回卷距离必须等于整排周期（间距 × 个数），否则滚动一段时间后间距会错乱。
        private void UpdateScenery()
        {
            float speed = (SpawnX - CharacterX) / Mathf.Max(0.05f, travelTime);
            float stripeWrap = stripes.Count * 3.2f;
            for (int i = 0; i < stripes.Count; i++)
            {
                var p = stripes[i].localPosition;
                p.x -= speed * Time.deltaTime;
                if (p.x < bandLeft - 1.6f) p.x += stripeWrap;
                stripes[i].localPosition = p;
            }
            float dashWrap = dashes.Count * 1.4f;
            for (int i = 0; i < dashes.Count; i++)
            {
                var p = dashes[i].localPosition;
                p.x -= speed * 0.35f * Time.deltaTime;
                if (p.x < bandLeft - 1f) p.x += dashWrap;
                dashes[i].localPosition = p;
            }
        }

        private void BuildPlayfield()
        {
            if (playfieldRoot != null) Destroy(playfieldRoot);
            playfieldRoot = new GameObject("MusePlayfield");
            playfieldRoot.transform.SetParent(transform, false);

            // 地面跑步带：深色底板 + 浅色条纹（条纹在 UpdateScenery 里滚动）
            var band = CreateSprite("GroundBand", playfieldRoot.transform,
                new Vector3((bandLeft + bandRight) * 0.5f, GroundY - 0.34f, 0f),
                new Vector2(bandRight - bandLeft, 0.68f), new Color(0.13f, 0.15f, 0.22f, 0.92f), -3);
            band.sprite = NoteSpawner.GetSquareSprite();
            stripes.Clear();
            for (float x = bandLeft; x <= bandRight; x += 3.2f)
            {
                var s = CreateSprite("Stripe" + x.ToString("0"), playfieldRoot.transform,
                    new Vector3(x, GroundY - 0.34f, 0f), new Vector2(1.6f, 0.10f),
                    new Color(1f, 1f, 1f, 0.13f), -2);
                s.sprite = NoteSpawner.GetSquareSprite();
                stripes.Add(s.transform);
            }

            // 空中轨道虚线（视差滚动）
            dashes.Clear();
            for (float x = bandLeft; x <= bandRight; x += 1.4f)
            {
                var d = CreateSprite("Dash" + x.ToString("0"), playfieldRoot.transform,
                    new Vector3(x, AirY, 0f), new Vector2(0.62f, 0.055f),
                    new Color(1f, 1f, 1f, 0.16f), -2);
                d.sprite = NoteSpawner.GetSquareSprite();
                dashes.Add(d.transform);
            }

            // 角色挂到场地根下（随 Cleanup 统一销毁）
            runner = new GameObject("MuseRunner").AddComponent<MuseRunner>();
            runner.transform.SetParent(playfieldRoot.transform, false);
            runner.Setup(this);

            // 红白机风圆形按钮垫：上（D K 空中）红帽 / 下（F J 地面）黑帽，按压有响应、命中爆环
            BuildPadButton(0, new Vector3(-6.6f, GroundY - 1.2f, 0f), PadGroundCap, "F J");
            BuildPadButton(1, new Vector3(-6.6f, AirY + 1.15f, 0f), PadAirCap, "D K");
        }

        /// <summary>红白机手柄风按钮：深色圆底座 + 彩色圆帽（红白机 A/B 键样式）+ 帽面键位字母。</summary>
        private void BuildPadButton(int lane, Vector3 pos, Color capColor, string letters)
        {
            var baseR = CreateSprite("PadBase" + lane, playfieldRoot.transform,
                pos, new Vector2(0.58f, 0.58f), PadBaseColor, 2);
            baseR.sprite = Taiko.TaikoSpawner.GetDiscSprite(); // 圆底座（disc 原生 2 单位 × 0.58 ≈ 1.16 直径）
            var capGo = new GameObject("PadCap" + lane);
            capGo.transform.SetParent(playfieldRoot.transform, false);
            capGo.transform.localPosition = pos;
            var capR = capGo.AddComponent<SpriteRenderer>();
            capR.sprite = Taiko.TaikoSpawner.GetDiscSprite();
            capR.color = capColor;
            capR.sortingOrder = 3;
            capGo.transform.localScale = new Vector3(0.41f, 0.41f, 1f); // 帽径 ≈0.82，压在底座上
            padCaps[lane] = capGo.transform;
            padCapRenderers[lane] = capR;
            padCapColors[lane] = capColor;
            padCapBaseScale[lane] = capGo.transform.localScale;
            padPos[lane] = pos;
            padWasHeld[lane] = false;
            // 字母挂在按钮帽下：按下下沉/弹跳时文字随帽一起动（独立挂放会显得"只有字在闪"）
            // TextMesh MiddleCenter 按行盒居中，Impact 字形实测偏低 ~0.1 单位 → 上移补偿
            CreateLaneKeyLabel(letters, capGo.transform, new Vector3(0f, 0.1f, 0f), 0.07f);
        }

        /// <summary>按钮垫按压反馈：按住时帽子下沉压扁提亮，按下瞬间与命中瞬间弹跳回弹（帽+字母一体）。</summary>
        private void UpdatePads()
        {
            if (Game == null || padCaps[0] == null) return;
            for (int lane = 0; lane < 2; lane++)
            {
                bool held = Game.IsLaneHeld(lane);
                if (held && !padWasHeld[lane]) padPunchAge[lane] = 0f; // 按下瞬间弹一下
                padWasHeld[lane] = held;
                padPunchAge[lane] += Time.deltaTime;
                float punch = Mathf.Exp(-padPunchAge[lane] * 12f);
                float press = held ? 1f : 0f;
                padCaps[lane].localScale = padCapBaseScale[lane] * ((held ? 0.82f : 1f) * (1f + 0.3f * punch));
                padCaps[lane].localPosition = padPos[lane] + new Vector3(0f, -0.07f * press - 0.06f * punch, 0f);
                var c = padCapColors[lane];
                var col = held ? Color.Lerp(c, Color.white, 0.45f) : c;
                padCapRenderers[lane].color = Color.Lerp(col, Color.white, 0.6f * punch);
            }
        }

        /// <summary>命中特效：对应按钮帽弹跳提亮 + 炸一圈金环（地面）/红环（空中）。</summary>
        internal void PadHitFx(int lane)
        {
            if (lane < 0 || lane > 1 || padCaps[lane] == null || playfieldRoot == null) return;
            padPunchAge[lane] = 0f;
            var go = new GameObject("PadHitFx");
            go.transform.SetParent(playfieldRoot.transform, false);
            go.transform.localPosition = padCaps[lane].localPosition;
            go.transform.localScale = new Vector3(0.5f, 0.5f, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = GetRingSprite();
            sr.sortingOrder = 12;
            sr.color = lane == 0 ? new Color(1f, 0.85f, 0.40f, 1f) : new Color(1f, 0.55f, 0.50f, 1f);
            StartCoroutine(PadHitFxRoutine(sr));
        }

        private static System.Collections.IEnumerator PadHitFxRoutine(SpriteRenderer sr)
        {
            const float Dur = 0.28f;
            float t = 0f;
            while (t < Dur && sr != null)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / Dur);
                sr.transform.localScale = Vector3.one * (0.5f + 1.1f * k);
                var c = sr.color;
                sr.color = new Color(c.r, c.g, c.b, 1f - k);
                yield return null;
            }
            if (sr != null) Destroy(sr.gameObject);
        }

        /// <summary>按钮帽面键位字母：Impact + 黑投影双层（NoteSpawner 键帽同款），可指定字号与父级（挂按钮帽下随动）。</summary>
        private void CreateLaneKeyLabel(string text, Transform parent, Vector3 localPos, float characterSize)
        {
            Font font = NoteSpawner.GetKeycapFont();
            var shadowGo = new GameObject("LaneKeyShadow");
            shadowGo.transform.SetParent(parent, false);
            shadowGo.transform.localPosition = localPos + new Vector3(0.025f, -0.035f, 0f);
            FillLaneKeyText(shadowGo, text, font, 3, new Color(0f, 0f, 0f, 0.5f), characterSize);
            var go = new GameObject("LaneKeyLabel");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            FillLaneKeyText(go, text, font, 4, new Color(1f, 1f, 1f, 0.88f), characterSize);
        }

        private static void FillLaneKeyText(GameObject go, string text, Font font, int order, Color color, float characterSize)
        {
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.font = font;
            tm.fontSize = 64;
            tm.characterSize = characterSize;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = font.material;
            renderer.sortingOrder = order;
        }

        /// <summary>地面敌人贴图（程序化自绘致敬版"蘑菇仔"）：棕菌盖+奶斑、怒目小脸、两只小脚——原创绘制，非任天堂素材。</summary>
        internal static Sprite GetGroundEnemySprite()
        {
            if (groundEnemySprite != null) return groundEnemySprite;
            const int size = 96;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[size * size];
            var capBrown = new Color32(128, 76, 42, 255);
            var spot = new Color32(238, 220, 190, 255);
            var face = new Color32(240, 222, 188, 255);
            var pupil = new Color32(40, 32, 30, 255);
            var eyeWhite = new Color32(252, 250, 246, 255);
            var brow = new Color32(52, 32, 20, 255);
            var foot = new Color32(74, 44, 24, 255);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    Color32 col;
                    // 自上而下绘制（链首在最上层）：瞳→眼白→怒眉→菌盖斑点→脸→菌盖→脚
                    if (InEllipse(px, py, 58, 46, 3.2f, 4.2f) || InEllipse(px, py, 74, 46, 3.2f, 4.2f)) col = pupil;
                    else if (InEllipse(px, py, 57, 47, 6.5f, 8.5f) || InEllipse(px, py, 73, 47, 6.5f, 8.5f)) col = eyeWhite;
                    else if (InEllipse(px, py, 56, 57, 7f, 2.6f) || InEllipse(px, py, 74, 57, 7f, 2.6f)) col = brow;
                    else if (InEllipse(px, py, 34, 76, 8f, 6f) || InEllipse(px, py, 58, 82, 6f, 5f) || InEllipse(px, py, 74, 70, 6.5f, 5f)) col = spot;
                    else if (InEllipse(px, py, 52, 46, 30f, 20f)) col = face;
                    else if (InEllipse(px, py, 48, 66, 38f, 28f)) col = capBrown;
                    else if (InEllipse(px, py, 34, 7, 13f, 6.5f) || InEllipse(px, py, 62, 7, 13f, 6.5f)) col = foot;
                    else col = new Color32(0, 0, 0, 0);
                    pixels[y * size + x] = col;
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            groundEnemySprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 96f);
            groundEnemySprite.name = "MuseGroundEnemy";
            return groundEnemySprite;
        }

        /// <summary>椭圆内含判定（贴图像素绘制辅助）。</summary>
        private static bool InEllipse(float px, float py, float cx, float cy, float rx, float ry)
        {
            float dx = (px - cx) / rx, dy = (py - cy) / ry;
            return dx * dx + dy * dy <= 1f;
        }

        /// <summary>空中敌人贴图（程序化自绘金币）：外圈深金边 + 金身 + 内圈线 + 高光，运行时带旋转动画。</summary>
        internal static Sprite GetAirEnemySprite()
        {
            if (airEnemySprite != null) return airEnemySprite;
            const int size = 96;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[size * size];
            var body = new Color32(244, 196, 66, 255);
            var light = new Color32(255, 234, 148, 255);
            var dark = new Color32(178, 130, 34, 255);
            var edge = new Color32(140, 98, 22, 255);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f - 48f, py = y + 0.5f - 48f;
                    float r = Mathf.Sqrt(px * px + py * py);
                    Color32 col;
                    if (r > 44f) col = new Color32(0, 0, 0, 0);
                    else if (r > 36f) col = edge;                       // 外圈深边
                    else if (r > 27f && r < 31f) col = dark;            // 内圈刻线
                    else if (InCircle(px, py, 10f, 14f, 8f)) col = light; // 内芯提亮
                    else if (px < -14f && py > 8f && r < 30f && r > 18f) col = light; // 左上高光弧
                    else col = body;
                    pixels[y * size + x] = col;
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            airEnemySprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 96f);
            airEnemySprite.name = "MuseAirEnemy";
            return airEnemySprite;
        }

        private static bool InCircle(float px, float py, float cx, float cy, float r)
        {
            float dx = px - cx, dy = py - cy;
            return dx * dx + dy * dy <= r * r;
        }

        /// <summary>harmony 金描边圆环（灰阶白环，运行时染金色）。</summary>
        internal static Sprite GetRingSprite()
        {
            const int size = 96;
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
                    float band = Mathf.SmoothStep(0.76f, 0.84f, r) * (1f - Mathf.SmoothStep(0.94f, 0.99f, r));
                    byte a = (byte)(Mathf.Clamp01(band) * 255f);
                    pixels[y * size + x] = new Color32(255, 255, 255, a);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 96f);
            sprite.name = "MuseRing";
            return sprite;
        }

        /// <summary>斩击弧贴图（程序化）：右向扇形白光，角度与半径双向柔和衰减。</summary>
        internal static Sprite GetSlashSprite()
        {
            const int size = 160;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[size * size];
            float c = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - c, dy = y + 0.5f - c;
                    float r = Mathf.Sqrt(dx * dx + dy * dy) / c;
                    float ang = Mathf.Atan2(dy, dx); // -π..π，0=正右
                    float angK = Mathf.Clamp01(1f - Mathf.Abs(ang) / (Mathf.PI * 0.30f));
                    float radK = Mathf.SmoothStep(0.18f, 0.45f, r) * (1f - Mathf.SmoothStep(0.72f, 1.0f, r));
                    byte a = (byte)(Mathf.Clamp01(angK * radK) * 255f);
                    pixels[y * size + x] = new Color32(255, 255, 255, a);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 96f);
            sprite.name = "MuseSlash";
            return sprite;
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
