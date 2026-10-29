using System;
using System.Collections.Generic;
using System.Linq;
using MusicGame.Pure.Model;

namespace MusicGame.Pure.Generator
{
    /// <summary>
    /// 谱面生成器（纯 C#，不依赖 Unity 引擎 API）。
    /// 管线：onset 按强度排序 → 量化到 1/GridDivision 节拍网格（容差随难度收紧）→
    /// 密度预算选点（强拍优先，任意 1 秒窗口不超过 MaxNotesPerSecond）→
    /// lane 分配（按 onset 频段映射：低频→0/1 高频→2/3 中频→1/2，太鼓重映射后即咚=低音/咔=高音；
    /// 同 lane 最小间隔、hold 期间静默、双押按 DoubleTapChance 且跨频段取对侧）→
    /// hold 由持续音段驱动（长音=长条；无持续段数据的旧缓存回退为间隙启发式）→
    /// 可玩性后处理兜底修正。
    /// 随机数使用 songId+难度派生的固定种子，同一份 AnalysisResult 生成结果可复现。
    /// </summary>
    public class ChartGenerator
    {
        private const int LaneCount = 4;

        // 频段 → 轨道集合：低音(底鼓/贝斯)走左两轨、高频(军鼓/踩镲)走右两轨、中频(人声/旋律)走中间。
        // 四轨 0/1 在太鼓重映射下=咚(红)，2/3=咔(蓝)——低频音符天然落在咚上。
        private static readonly int[] LowLanes = { 0, 1 };
        private static readonly int[] MidLanes = { 1, 2 };
        private static readonly int[] HighLanes = { 2, 3 };
        private static readonly int[] AllLanes = { 0, 1, 2, 3 };

        /// <summary>量化吸附容差（占一个网格步长的比例），难度越高越紧。</summary>
        private static readonly Dictionary<string, double> QuantizeToleranceFraction =
            new Dictionary<string, double>
            {
                { "easy", 0.50 },
                { "normal", 0.40 },
                { "hard", 0.33 },
                { "expert", 0.25 },
            };

        /// <summary>由一份分析结果一次性生成全部难度谱面。</summary>
        public static Dictionary<string, ChartData> Generate(AnalysisResult analysis, SongMeta meta)
        {
            var charts = new Dictionary<string, ChartData>();
            foreach (KeyValuePair<string, DifficultyProfile> pair in DifficultyProfiles.All)
                charts[pair.Key] = GenerateDifficulty(pair.Key, pair.Value, analysis, meta);
            return charts;
        }

        /// <summary>生成单档难度谱面。</summary>
        private static ChartData GenerateDifficulty(string difficulty, DifficultyProfile profile,
            AnalysisResult analysis, SongMeta meta)
        {
            double bpm = analysis != null && analysis.Bpm > 0 ? analysis.Bpm : 120.0;
            double offset = analysis != null ? analysis.OffsetSeconds : 0.0;
            string songId = meta != null && meta.Id != null ? meta.Id : "";

            var chart = new ChartData
            {
                SongId = songId,
                Difficulty = difficulty,
                Bpm = bpm,
                OffsetSeconds = offset,
                Notes = new List<NoteData>(),
            };

            List<Onset> onsets = (analysis != null && analysis.Onsets != null ? analysis.Onsets : new List<Onset>())
                .Where(o => o != null)
                .ToList();
            if (onsets.Count == 0) return chart; // 无 onset：返回空谱而不是崩溃

            double beatSeconds = 60.0 / bpm;
            int division = Math.Max(1, profile.GridDivision);
            double gridStep = beatSeconds / division; // 一格时长（秒）
            double lastOnset = onsets.Max(o => o.TimeSeconds);
            double duration = meta != null && meta.DurationSeconds > 0 ? meta.DurationSeconds : lastOnset + 2.0;

            // ---- 1) 量化：onset 吸附到网格槽（容差内），同一槽只保留最强 onset ----
            double tolerance = ToleranceFraction(difficulty, division) * gridStep;
            var slots = new Dictionary<long, Slot>();
            foreach (Onset o in onsets)
            {
                double beats = (o.TimeSeconds - offset) / beatSeconds;
                long index = (long)Math.Round(beats * division, MidpointRounding.AwayFromZero);
                double time = offset + index * gridStep;
                if (time < 0.0 || time > duration) continue;             // 曲目范围之外
                if (Math.Abs(o.TimeSeconds - time) > tolerance) continue; // 容差外不吸附
                Slot existing;
                if (slots.TryGetValue(index, out existing))
                {
                    if (o.Strength > existing.Strength)
                    {
                        existing.Strength = o.Strength;
                        existing.Band = NormalizeBand(o.Band);
                        existing.RawTime = o.TimeSeconds;
                    }
                }
                else
                {
                    slots[index] = new Slot
                    {
                        Index = index,
                        Time = time,
                        Strength = o.Strength,
                        Band = NormalizeBand(o.Band),
                        RawTime = o.TimeSeconds
                    };
                }
            }
            if (slots.Count == 0) return chart;

            // ---- 2) 密度预算选点：按强度降序贪心，弱拍被预算裁剪 ----
            Random rng = new Random(SeedFor(songId, difficulty));
            List<Slot> ordered = slots.Values
                .OrderByDescending(s => s.Strength)
                .ThenBy(s => s.Index)
                .ToList();
            var accepted = new List<Slot>();
            var acceptedTimes = new List<double>(); // 已选音符时间（升序，含双押重复），供窗口计数
            foreach (Slot s in ordered)
            {
                bool wantDouble = profile.DoubleTapChance > 0.0 &&
                                  rng.NextDouble() < profile.DoubleTapChance;
                int count;
                if (wantDouble)
                {
                    // 双押放不进预算时退化为单点
                    count = FitsBudget(s.Time, 2, profile, acceptedTimes) ? 2
                        : (FitsBudget(s.Time, 1, profile, acceptedTimes) ? 1 : 0);
                }
                else
                {
                    count = FitsBudget(s.Time, 1, profile, acceptedTimes) ? 1 : 0;
                }
                if (count == 0) continue;
                s.Double = count == 2;
                accepted.Add(s);
                InsertSorted(acceptedTimes, s.Time, count);
            }
            if (accepted.Count == 0) return chart;

            // ---- 3) lane 分配（按时间顺序处理，便于跟踪 lane 占用/hold；持续段驱动音乐 hold）----
            accepted.Sort((a, b) => a.Index.CompareTo(b.Index));
            List<Sustain> sustains = analysis != null && analysis.Sustains != null
                ? analysis.Sustains
                : new List<Sustain>();
            List<NoteData> notes = AssignLanes(accepted, profile, beatSeconds, rng, sustains);

            // ---- 4) 可玩性后处理兜底修正 ----
            chart.Notes = PostProcess(notes, profile);
            return chart;
        }

        /// <summary>按时间顺序分配 lane、hold 与双押，产出时间递增的音符列表。
        /// hold 优先由同频段持续音段驱动（长音=长条，时长=能量持续），无匹配回退为间隙启发式。</summary>
        private static List<NoteData> AssignLanes(List<Slot> accepted, DifficultyProfile profile,
            double beatSeconds, Random rng, List<Sustain> sustains)
        {
            var notes = new List<NoteData>();
            var laneFreeAt = new double[LaneCount]; // 每个 lane 下一个可用时间（含同 lane 间隔/hold 占用）
            int lastLane = -1;

            int totalNotes = 0;
            foreach (Slot s in accepted) totalNotes += s.Double ? 2 : 1;
            int holdQuota = (int)Math.Round(totalNotes * profile.HoldRatio);
            int holdsPlaced = 0;

            // 持续段按频带分组（升序），配已消费标记：一个持续段最多产出一条 hold
            var bandSustains = new Dictionary<string, List<Sustain>>();
            var consumedSustains = new HashSet<Sustain>();
            foreach (Sustain st in sustains)
            {
                if (st == null || st.DurationSeconds <= 0.0) continue;
                string band = NormalizeBand(st.Band);
                List<Sustain> list;
                if (!bandSustains.TryGetValue(band, out list))
                {
                    list = new List<Sustain>();
                    bandSustains[band] = list;
                }
                list.Add(st);
            }
            foreach (List<Sustain> list in bandSustains.Values)
                list.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));

            // 音乐 hold 走满额配额；间隙启发式 hold 便宜且贪婪（时间序见缝就插），
            // 有持续段数据时只允许它用一半配额，避免把音乐 hold 饿死
            int fallbackHoldCap = bandSustains.Count > 0 ? holdQuota / 2 : holdQuota;
            int fallbackHoldsPlaced = 0;

            double minHold = Math.Max(beatSeconds * 0.25, 0.15); // 短于此不如 tap
            double maxHold = beatSeconds * 2.0;
            double tailGap = Math.Max(0.03, profile.MinSameLaneGapSeconds * 0.25); // hold 结尾距下一音符的空隙
            var activeHoldEnds = new List<double>(); // 进行中的音乐 hold 结束时刻（并发限制用）

            for (int i = 0; i < accepted.Count; i++)
            {
                Slot s = accepted[i];
                double t = s.Time;
                double nextTime = i + 1 < accepted.Count ? accepted[i + 1].Time : t + maxHold + 1.0;

                if (s.Double)
                {
                    // 双押：主 lane 取本频段侧，副 lane 取对侧（低频+高频同时响的和弦感）；
                    // 对侧满时回退任意可用 lane
                    int[] side = BandLanes(s.Band);
                    int a = ChooseLane(laneFreeAt, t, lastLane, rng, side);
                    if (a >= 0)
                    {
                        notes.Add(Tap(t, a));
                        laneFreeAt[a] = t + profile.MinSameLaneGapSeconds;
                    }
                    int b = ChooseLane(laneFreeAt, t, a, rng, ComplementLanes(s.Band));
                    if (b >= 0)
                    {
                        notes.Add(Tap(t, b));
                        laneFreeAt[b] = t + profile.MinSameLaneGapSeconds;
                    }
                    if (a >= 0 || b >= 0) lastLane = b >= 0 ? b : a;
                    continue; // 极端情况下两个 lane 都不可用则整槽丢弃
                }

                int lane = ChooseLane(laneFreeAt, t, lastLane, rng, BandLanes(s.Band));
                if (lane < 0) continue; // 无 lane 可用：丢弃该点（后处理兜底统计口径）

                // 音乐 hold（优先）：同频段持续段 → 长音=长条，可跨其他 lane 的音符
                // （只占本 lane；约束=最大时长、并发 ≤2 防四键全按住）
                activeHoldEnds.RemoveAll(end => end <= t + 1e-9);
                if (holdsPlaced < holdQuota && activeHoldEnds.Count < 2)
                {
                    Sustain match = FindSustain(bandSustains, consumedSustains, s.Band, s.RawTime, 0.20);
                    if (match != null)
                    {
                        double dur = Math.Min(match.TimeSeconds + match.DurationSeconds - t, maxHold);
                        if (dur >= minHold)
                        {
                            notes.Add(new NoteData { Time = t, Lane = lane, Type = "hold", Duration = dur });
                            holdsPlaced++;
                            consumedSustains.Add(match);
                            activeHoldEnds.Add(t + dur);
                            laneFreeAt[lane] = t + dur + profile.MinSameLaneGapSeconds;
                            lastLane = lane;
                            continue;
                        }
                    }
                }

                // 旧启发式 hold（无持续段匹配时的回退）：与下一音符的间隔容得下才放，
                // 时长截止到下一音符之前 → hold 期间整谱静默，杜绝交叉配置
                double gapToNext = nextTime - t;
                if (holdsPlaced < holdQuota && fallbackHoldsPlaced < fallbackHoldCap
                    && gapToNext >= minHold + tailGap)
                {
                    double holdDuration = Math.Min(gapToNext - tailGap, maxHold);
                    if (holdDuration >= minHold)
                    {
                        notes.Add(new NoteData { Time = t, Lane = lane, Type = "hold", Duration = holdDuration });
                        holdsPlaced++;
                        fallbackHoldsPlaced++;
                        activeHoldEnds.Add(t + holdDuration);
                        laneFreeAt[lane] = t + holdDuration + profile.MinSameLaneGapSeconds;
                        lastLane = lane;
                        continue;
                    }
                }

                notes.Add(Tap(t, lane));
                laneFreeAt[lane] = t + profile.MinSameLaneGapSeconds;
                lastLane = lane;
            }
            return notes;
        }

        /// <summary>找一条可与该音符配对的同频段持续段：未被消费，且起点距 onset 原始时刻 ≤0.20s（取最早者）。</summary>
        private static Sustain FindSustain(Dictionary<string, List<Sustain>> bandSustains,
            HashSet<Sustain> consumed, string band, double rawTime, double tolerance)
        {
            List<Sustain> list;
            if (!bandSustains.TryGetValue(NormalizeBand(band), out list)) return null;
            foreach (Sustain st in list)
            {
                if (consumed.Contains(st)) continue;
                if (Math.Abs(st.TimeSeconds - rawTime) <= tolerance) return st;
                if (st.TimeSeconds > rawTime + tolerance) break; // 已排序，后面更晚
            }
            return null;
        }

        private static int[] BandLanes(string band)
        {
            switch (NormalizeBand(band))
            {
                case "low": return LowLanes;
                case "high": return HighLanes;
                default: return MidLanes;
            }
        }

        /// <summary>双押副 lane 的对侧轨道：低↔高互为对侧，中频配外侧两轨。</summary>
        private static int[] ComplementLanes(string band)
        {
            switch (NormalizeBand(band))
            {
                case "low": return HighLanes;
                case "high": return LowLanes;
                default: return new[] { 0, 3 };
            }
        }

        private static string NormalizeBand(string band)
        {
            return band == "low" || band == "high" ? band : "mid";
        }

        /// <summary>挑一个可用 lane：在候选轨道集合内优先久未使用、避开与上一音同 lane（减少同键连打）；
        /// 候选集全忙时放宽到全部轨道（可玩性优先于频段语义）。</summary>
        private static int ChooseLane(double[] laneFreeAt, double t, int avoidLane, Random rng, int[] allowedLanes)
        {
            int best = ChooseLaneWithin(laneFreeAt, t, avoidLane, rng, allowedLanes);
            if (best < 0) best = ChooseLaneWithin(laneFreeAt, t, avoidLane, rng, AllLanes);
            return best;
        }

        private static int ChooseLaneWithin(double[] laneFreeAt, double t, int avoidLane, Random rng, int[] lanes)
        {
            int best = -1;
            double bestScore = double.MinValue;
            for (int i = 0; i < lanes.Length; i++)
            {
                int lane = lanes[i];
                if (lane < 0 || lane >= LaneCount) continue;
                if (laneFreeAt[lane] > t + 1e-9) continue; // 该 lane 仍被占用
                double score = Math.Min(t - laneFreeAt[lane], 2.0); // 空闲越久越优先（封顶，避免压过惩罚项）
                if (lane == avoidLane) score -= 10.0;               // 强烈避免与上一音同 lane
                score += rng.NextDouble() * 0.05;                   // 少量随机，避免机械
                if (score > bestScore)
                {
                    bestScore = score;
                    best = lane;
                }
            }
            return best;
        }

        /// <summary>
        /// 可玩性后处理兜底：同 lane 最小间隔、hold 期间不出新 note、
        /// 同一时刻最多 2 个音符（无双押交叉/多押）。
        /// 构造阶段已按这些规则生成，此处防御浮点误差与异常输入，违规时移除后者。
        /// </summary>
        private static List<NoteData> PostProcess(List<NoteData> notes, DifficultyProfile profile)
        {
            notes.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Lane.CompareTo(b.Lane));

            var cleaned = new List<NoteData>(notes.Count);
            var laneOccupiedUntil = new double[LaneCount]; // 该 lane 上一个音符的占用结束点
            double lastTime = double.MinValue;
            int sameTimeCount = 0;

            foreach (NoteData n in notes)
            {
                if (Math.Abs(n.Time - lastTime) > 1e-6)
                {
                    lastTime = n.Time;
                    sameTimeCount = 0;
                }
                sameTimeCount++;
                if (sameTimeCount > 2) continue; // 同刻超过 2 个音符：丢弃多余

                if (n.Time < laneOccupiedUntil[n.Lane] - 1e-9) continue; // 同 lane 间隔不足或落在 hold 期间

                double occupyEnd = n.Type == "hold" ? n.Time + n.Duration : n.Time;
                laneOccupiedUntil[n.Lane] = occupyEnd + profile.MinSameLaneGapSeconds;
                cleaned.Add(n);
            }
            return cleaned;
        }

        /// <summary>
        /// 在 t 时刻新增 count 个音符后，所有受影响的 1 秒窗是否仍在密度预算内。
        /// 新音符只可能进入左端点 τ ∈ (t-1, t] 的窗 [τ, τ+1)，因此逐一校验这些锚点
        /// （含 t 自身）即可维持“任意 1 秒窗 ≤ MaxNotesPerSecond”的全局不变量——
        /// 仅查 (t-1, t] 尾窗会漏掉“插在两个既有音符之间、撑爆首窗”的情形。
        /// </summary>
        private static bool FitsBudget(double t, int count, DifficultyProfile profile, List<double> sortedTimes)
        {
            double maxNps = Math.Max(1.0, profile.MaxNotesPerSecond);
            int lo = UpperBound(sortedTimes, t - 1.0); // 第一个 > t-1 的既有音符
            int hi = UpperBound(sortedTimes, t);       // 第一个 > t 的既有音符（t 处槽位唯一，不会有既有音符）
            for (int a = lo; a <= hi; a++)             // a == hi 对应锚点 t 自身
            {
                double anchor = a < hi ? sortedTimes[a] : t;
                double windowEnd = anchor + 1.0 - 1e-9; // 与自检一致的半开窗 [anchor, windowEnd)
                int existing = UpperBound(sortedTimes, windowEnd) - LowerBound(sortedTimes, anchor);
                if (t < windowEnd && existing + count > maxNps + 1e-9) return false;
            }
            return true;
        }

        /// <summary>把 value 重复 repeat 次插入升序列表并保持有序。</summary>
        private static void InsertSorted(List<double> sorted, double value, int repeat)
        {
            int pos = UpperBound(sorted, value);
            for (int i = 0; i < repeat; i++) sorted.Insert(pos, value);
        }

        /// <summary>二分：第一个大于等于 value 的元素下标（即前缀里都是 &lt; value）。</summary>
        private static int LowerBound(List<double> sorted, double value)
        {
            int lo = 0, hi = sorted.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (sorted[mid] < value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>二分：第一个大于 value 的元素下标（即前缀里都是 &lt;= value）。</summary>
        private static int UpperBound(List<double> sorted, double value)
        {
            int lo = 0, hi = sorted.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (sorted[mid] <= value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>未知难度名的量化容差回退：网格越细容差越紧。</summary>
        private static double ToleranceFraction(string difficulty, int division)
        {
            double fraction;
            if (QuantizeToleranceFraction.TryGetValue(difficulty, out fraction)) return fraction;
            if (division <= 4) return 0.50;
            if (division <= 8) return 0.40;
            return 0.30;
        }

        /// <summary>由 songId+难度派生随机种子，保证同一输入生成可复现。</summary>
        private static int SeedFor(string songId, string difficulty)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in songId ?? "") hash = hash * 31 + c;
                foreach (char c in difficulty ?? "") hash = hash * 31 + c;
                return hash;
            }
        }

        private static NoteData Tap(double time, int lane)
        {
            return new NoteData { Time = time, Lane = lane, Type = "tap", Duration = 0.0 };
        }

        /// <summary>量化后的节拍网格槽位。</summary>
        private sealed class Slot
        {
            public long Index;     // 网格序号（相对第 0 拍，可为负）
            public double Time;    // 量化后的绝对时间（秒）
            public double Strength; // 槽内最强 onset 的归一化强度
            public string Band;    // 槽内最强 onset 的频段（low/mid/high）
            public double RawTime; // 槽内最强 onset 的原始时刻（持续段匹配用）
            public bool Double;    // 是否双押（占 2 个音符预算）

            public Slot()
            {
                Band = "mid";
                RawTime = 0.0;
            }
        }
    }
}
