using System.Collections.Generic;
using MusicGame.Core;
using MusicGame.Pure.Model;
using UnityEngine;

namespace MusicGame.Gameplay.Osu
{
    /// <summary>
    /// osu! 缩圈模式场地：谱面时间轴摆成圆圈——位置按时间间隔确定性游走（间距∝间隔，osu! AR 思路，
    /// 同一歌+难度每次进图摆位一致），缩圈环合拢瞬间点击圆圈判定，复用 JudgeSystem 四档时间窗。
    /// hold 拆成头/尾两个圆圈（对齐 hold=头+尾两次判定口径）。玩法参考 osu!lazer（MIT），实现为独立代码。
    /// 输入由 GameManager 提供（点击事件+光标世界坐标），本类负责摆放/命中匹配/回收。
    /// </summary>
    public class OsuSpawner : MonoBehaviour
    {
        /// <summary>缩圈环合拢时间（秒）：随下落速度档设置（GameManager 建场时写入）。</summary>
        public float ApproachSeconds = 1.2f;
        /// <summary>圆圈视觉半径（世界单位）。</summary>
        public const float CircleRadius = 0.9f;
        /// <summary>点击有效半径（略大于视觉圈，判定宽松）。</summary>
        public const float HitRadius = 1.2f;
        /// <summary>缩圈环初始倍率（3x 收到 1x 时正好到判定点）。</summary>
        public const float ApproachScale = 3.0f;

        /// <summary>连击组四色（红/金/绿/蓝，色相全分离）：按组号给圈体染色，独立于主题四轨色，一眼可分。</summary>
        public static readonly Color[] GroupColors =
        {
            new Color(0.95f, 0.30f, 0.35f),
            new Color(0.98f, 0.78f, 0.25f),
            new Color(0.30f, 0.85f, 0.45f),
            new Color(0.30f, 0.55f, 0.98f),
        };

        private const float PlayHalfX = 5.8f; // 圈心活动范围半宽/半高（正交相机 size6 下 4:3 也放得下）
        private const float PlayHalfY = 3.0f;
        private const float MinJump = 2.0f;   // 相邻圈最小间距（>两圈半径和 1.8，避免视觉重叠）
        private const float MaxJump = 5.0f;
        private const float JumpSpeed = 4.0f; // 间距 = Clamp(Δt × JumpSpeed, MinJump, MaxJump)

        internal GameManager Game { get; set; }
        internal NotePool Pool { get; set; }

        /// <summary>单个圆圈条目：判定时刻 + 合成 NoteData（tail 为新建对象，供事件/判定流复用）。</summary>
        private class Entry
        {
            public double Time;
            public NoteData Note;
            public int Number;      // 圈上数字（1..4 循环，osu! combo 组风格）
            public int ColorIndex;  // 连击组颜色（LaneColors 循环）
            public OsuCircle View;  // 已生成时的视觉实例；命中/Miss 后置空
        }

        private readonly List<Entry> entries = new List<Entry>();
        private readonly Dictionary<NoteData, Entry> noteToEntry = new Dictionary<NoteData, Entry>();
        private int nextSpawn;
        private Sprite glowSprite;

        /// <summary>当前判定的圆圈世界坐标（供弹窗层锚定；命中/Miss 前置位，事件随后触发）。</summary>
        public Vector3 AnchorWorld { get; private set; }

        public void Setup(ChartData chart, Conductor conductor, NotePool pool, string seedKey)
        {
            Pool = pool;
            entries.Clear();
            noteToEntry.Clear();
            nextSpawn = 0;
            if (chart == null || chart.Notes == null) return;

            var notes = new List<NoteData>(chart.Notes);
            notes.Sort((a, b) => a.Time.CompareTo(b.Time));

            // 位置游走：随机数种子=歌+难度，摆位可复现（osu! 同图同摆）
            var rng = new System.Random(StableHash(seedKey ?? ""));
            Vector2 prev = new Vector2(0f, -0.6f);
            double prevTime = 0.0;
            int number = 1;
            int colorIndex = 0;
            foreach (NoteData note in notes)
            {
                if (note == null || note.Lane < 0 || note.Lane > 3) continue;
                bool hold = string.Equals(note.Type, "hold", System.StringComparison.OrdinalIgnoreCase) && note.Duration > 0.0;

                PlaceEntry(note.Time, note, number, colorIndex, ref prev, ref prevTime, rng);
                if (hold)
                {
                    var tail = new NoteData { Time = note.Time + note.Duration, Lane = note.Lane, Type = "tail", Duration = 0.0 };
                    PlaceEntry(tail.Time, tail, number, colorIndex, ref prev, ref prevTime, rng);
                }
                number = number % 4 + 1;
                colorIndex = (colorIndex + 1) % 4;
            }
            entries.Sort((a, b) => a.Time.CompareTo(b.Time)); // hold 尾可能晚于后续音符，重排保证时间序

            CreateBackdrop();
        }

        // 摆一个条目：从上一圈按 Δt 走一步（头尾都算步进），并更新 prev/prevTime
        private void PlaceEntry(double time, NoteData note, int number, int colorIndex,
            ref Vector2 prev, ref double prevTime, System.Random rng)
        {
            float dt = Mathf.Max(0f, (float)(time - prevTime));
            float dist = Mathf.Clamp(dt * JumpSpeed, MinJump, MaxJump);
            Vector2 pos = Walk(prev, dist, rng);
            var entry = new Entry { Time = time, Note = note, Number = number, ColorIndex = colorIndex };
            entries.Add(entry);
            noteToEntry[note] = entry;
            prev = pos;
            prevTime = time;
        }

        // 从 from 出发走 dist：随机方向最多试 10 次取在场内的；全越界则朝场心压线收边（角落兜底）
        private static Vector2 Walk(Vector2 from, float dist, System.Random rng)
        {
            for (int i = 0; i < 10; i++)
            {
                float ang = (float)(rng.NextDouble() * Mathf.PI * 2.0);
                Vector2 next = from + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * dist;
                if (Mathf.Abs(next.x) <= PlayHalfX && Mathf.Abs(next.y) <= PlayHalfY) return next;
            }
            Vector2 fallback = from + (-from).normalized * dist;
            fallback.x = Mathf.Clamp(fallback.x, -PlayHalfX, PlayHalfX);
            fallback.y = Mathf.Clamp(fallback.y, -PlayHalfY, PlayHalfY);
            return fallback;
        }

        private static int StableHash(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                foreach (char c in s) { h ^= c; h *= 16777619u; }
                return (int)h;
            }
        }

        // 场地柔光：深色径向渐变垫底，把注意力聚到圆圈区（程序化贴图，零外部资源）
        private void CreateBackdrop()
        {
            if (glowSprite == null)
            {
                const int size = 256;
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
                        byte v = (byte)(a * a * 90f); // 中心最深 90/255，向外平方衰减
                        pixels[y * size + x] = new Color32(0, 0, 0, v);
                    }
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                // PPU 32 → 原生 8 单位（程序化贴图 PPU 口径：原生尺寸≈世界单位整数倍便于推缩放）
                glowSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 32f);
                glowSprite.name = "OsuPlayGlow";
            }
            var go = new GameObject("PlayGlow");
            go.transform.SetParent(transform, false);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = glowSprite;
            renderer.sortingOrder = 1;
            go.transform.localScale = new Vector3(2.0f, 1.3f, 1f); // 16 x 10.4 单位，罩住整个场地
            go.transform.localPosition = Vector3.zero;
        }

        private void Update()
        {
            if (Game == null || Game.Conductor == null) return;
            double now = Game.JudgedTime;
            while (nextSpawn < entries.Count && entries[nextSpawn].Time - now <= ApproachSeconds)
                Spawn(entries[nextSpawn++]);
        }

        private void Spawn(Entry entry)
        {
            if (Pool == null) return;
            GameObject go = Pool.Rent();
            var circle = go != null ? go.GetComponent<OsuCircle>() : null;
            if (circle == null) { if (go != null) Destroy(go); return; }
            entry.View = circle;
            circle.Initialize(entry.Time, entry.Number, entry.ColorIndex, this);
        }

        // 生成条目的当前摆位（View 生成时记录；未生成的用不到）
        internal Vector3 EntryPosition(OsuCircle view)
        {
            return view != null ? view.transform.position : Vector3.zero;
        }

        /// <summary>
        /// 点击命中匹配：在时间窗（±Bad）内且点击落在 HitRadius 内的未判圈里，
        /// 选 |Δt| 最小者（并列取更近的）。仅标记消费，命中表现在 NotifyJudged。
        /// </summary>
        public bool TryHit(double now, Vector2 worldPos, out NoteData note, out Judgement judgement)
        {
            note = null;
            judgement = Judgement.Miss;
            Entry best = null;
            double bestDt = double.MaxValue;
            float bestDist = float.MaxValue;
            foreach (Entry e in entries)
            {
                if (e.View == null || e.View.Consumed) continue;
                double dt = System.Math.Abs(now - e.Time);
                if (dt > JudgeSystem.BadWindowSeconds) continue;
                float dist = Vector2.Distance(worldPos, e.View.transform.position);
                if (dist > HitRadius) continue;
                if (dt < bestDt - 1e-9 || (System.Math.Abs(dt - bestDt) <= 1e-9 && dist < bestDist))
                {
                    best = e; bestDt = dt; bestDist = dist;
                }
            }
            if (best == null) return false;
            best.View.Consume();
            AnchorWorld = best.View.transform.position;
            note = best.Note;
            judgement = bestDt <= JudgeSystem.PerfectWindowSeconds ? Judgement.Perfect
                : bestDt <= JudgeSystem.GoodWindowSeconds ? Judgement.Good
                : Judgement.Bad;
            return true;
        }

        /// <summary>Auto 观赏命中：最早已到点（Time ≤ now）的未判圈，恒 Perfect；与手点同走 NotifyJudged 表现。</summary>
        public bool TryAutoHit(double now, out NoteData note)
        {
            note = null;
            foreach (Entry e in entries)
            {
                if (e.View == null || e.View.Consumed) continue;
                if (e.Time > now) break; // 已按时间排序，后面更晚
                e.View.Consume();
                AnchorWorld = e.View.transform.position;
                note = e.Note;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 扫尾 Miss：最早已超 Bad 窗的未判圈（外部循环调用直到 false）。
        /// 直接灰化退场（Miss 无需二次通知），并前置 AnchorWorld 供 MISS 弹窗锚定。
        /// </summary>
        public bool TryMiss(double now, out NoteData note)
        {
            note = null;
            foreach (Entry e in entries)
            {
                if (e.View == null || e.View.Consumed) continue;
                if (now - e.Time <= JudgeSystem.BadWindowSeconds) break;
                e.View.Consume();
                AnchorWorld = e.View.transform.position;
                e.View.OnMissed();
                note = e.Note;
                return true;
            }
            return false;
        }

        /// <summary>命中表现：圈体爆闪退场 + 记录弹窗锚点（GameManager.RegisterHit 统一调用）。
        /// TryHit/TryAutoHit 已置 Consumed，这里仍要播 OnHit——防重入由 OsuCircle 状态机保证。</summary>
        public void NotifyJudged(NoteData note, Judgement judgement)
        {
            if (note == null) return;
            if (noteToEntry.TryGetValue(note, out Entry e) && e.View != null)
            {
                AnchorWorld = e.View.transform.position;
                e.View.OnHit(judgement);
            }
        }

        /// <summary>音符动画结束回收（OsuCircle 调用）：归还池并断开条目引用。</summary>
        internal void OnNoteFinished(OsuCircle circle)
        {
            foreach (Entry e in entries)
            {
                if (e.View == circle) { e.View = null; break; }
            }
            if (Pool != null && circle != null) Pool.Return(circle.gameObject);
        }

        /// <summary>强制回收全部（停止/切歌）。</summary>
        public void ReleaseAll()
        {
            foreach (Entry e in entries)
            {
                if (e.View != null)
                {
                    e.View.ForceRelease();
                    e.View = null;
                }
            }
        }
    }
}
