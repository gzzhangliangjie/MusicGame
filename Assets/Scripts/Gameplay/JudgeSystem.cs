using System.Collections.Generic;
using MusicGame.Pure.Model;

namespace MusicGame.Gameplay
{
    /// <summary>
    /// 判定系统（纯逻辑，无 MonoBehaviour）：
    /// 头判在该轨 ±Bad 窗内查最近未判定音符，按 |Δ| 分四档（Perfect/Good/Bad）；
    /// 超窗音符由外部每帧调用 TryMiss 自动 Miss 回收。仅 Miss 断连击。
    /// </summary>
    public class JudgeSystem
    {
        /// <summary>Perfect 判定窗（秒）。</summary>
        public const double PerfectWindowSeconds = 0.040;
        /// <summary>Good 判定窗（秒）。</summary>
        public const double GoodWindowSeconds = 0.080;
        /// <summary>Bad 判定窗（秒），超过即 Miss。</summary>
        public const double BadWindowSeconds = 0.140;

        private readonly List<NoteData>[] pending = new List<NoteData>[4];
        private int remainingHeads;

        /// <summary>尚未完成头判的音符数。</summary>
        public int RemainingHeads => remainingHeads;

        /// <summary>载入谱面：按 lane 分队列并按时间升序排列。</summary>
        public void Setup(ChartData chart)
        {
            for (int i = 0; i < pending.Length; i++) pending[i] = new List<NoteData>();
            remainingHeads = 0;
            if (chart == null || chart.Notes == null) return;
            foreach (NoteData note in chart.Notes)
            {
                if (note == null || note.Lane < 0 || note.Lane > 3) continue;
                pending[note.Lane].Add(note);
                remainingHeads++;
            }
            for (int i = 0; i < pending.Length; i++)
            {
                pending[i].Sort((a, b) => a.Time.CompareTo(b.Time));
            }
        }

        /// <summary>
        /// 头判：在该轨判定窗内找与 songTimeSeconds 最近且未判定的音符。
        /// 命中即从待判队列移除（防重复判定）；|Δ|≤0.040 Perfect，≤0.080 Good，≤0.140 Bad。
        /// </summary>
        public bool TryHit(int lane, double songTimeSeconds, out NoteData note, out Judgement judgement)
        {
            note = null;
            judgement = Judgement.Miss;
            if (lane < 0 || lane > 3) return false;
            List<NoteData> list = pending[lane];
            double bestAbs = double.MaxValue;
            int bestIndex = -1;
            for (int i = 0; i < list.Count; i++)
            {
                NoteData candidate = list[i];
                if (candidate.Time > songTimeSeconds + BadWindowSeconds) break; // 已排序，后面更晚
                double delta = System.Math.Abs(candidate.Time - songTimeSeconds);
                if (delta <= BadWindowSeconds && delta < bestAbs)
                {
                    bestAbs = delta;
                    bestIndex = i;
                }
            }
            if (bestIndex < 0) return false;
            note = list[bestIndex];
            list.RemoveAt(bestIndex);
            remainingHeads--;
            judgement = bestAbs <= PerfectWindowSeconds ? Judgement.Perfect
                : bestAbs <= GoodWindowSeconds ? Judgement.Good
                : Judgement.Bad;
            return true;
        }

        /// <summary>
        /// 自动 Miss：取出任意轨最早已超过 Bad 窗仍未头判的音符（外部循环调用直到返回 false）。
        /// </summary>
        public bool TryMiss(double songTimeSeconds, out NoteData note)
        {
            note = null;
            for (int lane = 0; lane < pending.Length; lane++)
            {
                List<NoteData> list = pending[lane];
                if (list.Count == 0) continue;
                NoteData head = list[0];
                if (songTimeSeconds - head.Time > BadWindowSeconds)
                {
                    note = head;
                    list.RemoveAt(0);
                    remainingHeads--;
                    return true;
                }
            }
            return false;
        }
    }
}
