using System;
using System.Collections.Generic;
using MusicGame.Pure.Dsp;
using MusicGame.Pure.Generator;
using MusicGame.Pure.Model;

namespace AnalysisCore
{
    /// <summary>
    /// 纯层自检（不依赖 UnityEngine）：
    /// ① 合成 1 秒 440Hz 正弦(44100Hz) → FFT → 峰值频点 ≈ 440Hz(±2 bin)；
    /// ② 合成 10 秒 120BPM 节拍轨(每 0.5s 一个短脉冲) → SpectralFluxOnsetDetector
    ///    → ≥18 个 onset 落在预期时刻 ±30ms，且 BpmEstimator ≈ 120(±1)；
    /// ③ ChartGenerator 喂合成 onset → 四档谱均满足：时间升序、nps ≤ MaxNotesPerSecond、
    ///    同 lane 间隔 ≥ MinSameLaneGapSeconds、hold 期间同 lane 无重叠新 note。
    /// 每项打印 PASS/FAIL 行，任一失败 Environment.Exit(1)。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.WriteLine("=== MusicGame AnalysisCore self-check ===");
            bool allOk = true;

            // ① FFT 峰值频率
            try { allOk &= CheckFft(); }
            catch (Exception e) { Fail("FFT check threw: " + e.Message); allOk = false; }

            // ② onset 检测 + BPM 估计
            try { allOk &= CheckOnsetDetectorAndBpm(); }
            catch (Exception e) { Fail("onset/BPM check threw: " + e.Message); allOk = false; }

            // ③ 谱面生成器约束
            try { allOk &= CheckChartGenerator(); }
            catch (Exception e) { Fail("chart check threw: " + e.Message); allOk = false; }

            Console.WriteLine();
            if (!allOk)
            {
                Console.WriteLine("SELF-CHECK FAILED");
                Environment.Exit(1);
            }
            Console.WriteLine("SELF-CHECK PASSED");
            return 0;
        }

        private static void Pass(string message) { Console.WriteLine("[PASS] " + message); }
        private static void Fail(string message) { Console.WriteLine("[FAIL] " + message); }

        // ---------------- ① FFT ----------------
        private static bool CheckFft()
        {
            const int sampleRate = 44100;
            const double sineHz = 440.0;
            int n = 1 << 16; // 65536 ≥ 44100（1 秒），2 的幂
            var re = new double[n];
            var im = new double[n];
            int sampleCount = sampleRate; // 1 秒
            for (int i = 0; i < sampleCount; i++)
                re[i] = Math.Sin(2.0 * Math.PI * sineHz * i / sampleRate);

            Fft.Forward(re, im);

            int peakBin = 0;
            double peakMag = -1.0;
            for (int j = 1; j < n / 2; j++) // 跳过直流
            {
                double m = re[j] * re[j] + im[j] * im[j];
                if (m > peakMag) { peakMag = m; peakBin = j; }
            }
            double binHz = (double)sampleRate / n;
            double peakFreq = peakBin * binHz;
            bool ok = Math.Abs(peakFreq - sineHz) <= 2.0 * binHz;
            if (ok) Pass("FFT: peak bin " + peakBin + " -> " + peakFreq.ToString("F2")
                         + " Hz (expected " + sineHz.ToString("F0") + " +/- " + (2.0 * binHz).ToString("F2") + " Hz)");
            else Fail("FFT: peak bin " + peakBin + " -> " + peakFreq.ToString("F2")
                      + " Hz, expected " + sineHz.ToString("F0") + " +/- " + (2.0 * binHz).ToString("F2") + " Hz");
            return ok;
        }

        // ---------------- ② onset 检测 + BPM ----------------
        private static bool CheckOnsetDetectorAndBpm()
        {
            const int sampleRate = 44100;
            const double durationSeconds = 10.0;
            const double beatInterval = 0.5; // 120 BPM
            var mono = new float[(int)(sampleRate * durationSeconds)];

            // 每 0.5s 一个短脉冲：宽带噪声 click（高频）+ 80Hz 低频 thump
            var rng = new Random(1234);
            int clickLength = (int)(0.035 * sampleRate);
            int thumpLength = (int)(0.06 * sampleRate);
            for (int k = 1; k <= 19; k++) // 0.5 .. 9.5 共 19 个
            {
                int start = (int)Math.Round(k * beatInterval * sampleRate);
                for (int i = 0; i < clickLength; i++)
                    mono[start + i] += (float)((rng.NextDouble() * 2.0 - 1.0)
                                               * Math.Exp(-i / (0.006 * sampleRate)));
                for (int i = 0; i < thumpLength; i++)
                    mono[start + i] += (float)(Math.Sin(2.0 * Math.PI * 80.0 * i / sampleRate) * 0.8
                                               * Math.Exp(-i / (0.012 * sampleRate)));
            }

            var detector = new SpectralFluxOnsetDetector();
            List<Onset> onsets = detector.Detect(mono, sampleRate);
            Console.WriteLine("      detected " + onsets.Count + " onsets, "
                              + (onsets.Count > 0 ? onsets[0].TimeSeconds.ToString("F3") : "-") + " .. "
                              + (onsets.Count > 0 ? onsets[onsets.Count - 1].TimeSeconds.ToString("F3") : "-") + " s");

            // 每个 onset 找最近预期拍点，误差 ≤ 30ms 记为命中；要求 ≥18
            const double tolerance = 0.030;
            int hits = 0;
            for (int i = 0; i < onsets.Count; i++)
            {
                double nearest = double.MaxValue;
                for (int k = 1; k <= 19; k++)
                {
                    double expected = k * beatInterval;
                    double d = Math.Abs(onsets[i].TimeSeconds - expected);
                    if (d < nearest) nearest = d;
                }
                if (nearest <= tolerance) hits++;
            }
            bool onsetsOk = hits >= 18;
            if (onsetsOk) Pass("Onsets: " + hits + "/19 within +/-30 ms of expected beat times (need >=18)");
            else Fail("Onsets: only " + hits + "/19 within +/-30 ms (need >=18), total detected " + onsets.Count);

            double bpm = BpmEstimator.Estimate(onsets);
            bool bpmOk = Math.Abs(bpm - 120.0) <= 1.0;
            if (bpmOk) Pass("BPM: " + bpm.ToString("F2") + " (expected 120 +/- 1)");
            else Fail("BPM: " + bpm.ToString("F2") + ", expected 120 +/- 1");
            return onsetsOk && bpmOk;
        }

        // ---------------- ③ 谱面生成器约束 ----------------
        private static bool CheckChartGenerator()
        {
            // 合成 onset：20 秒、120 BPM 八分音网格（0.25s），强度按拍点强弱分层
            var onsets = new List<Onset>();
            var bands = new[] { "low", "mid", "high" };
            for (int k = 0; k < 80; k++)
            {
                double strength = k % 4 == 0 ? 1.0 : (k % 2 == 0 ? 0.7 : 0.45);
                onsets.Add(new Onset
                {
                    TimeSeconds = k * 0.25,
                    Strength = strength,
                    Band = bands[k % 3]
                });
            }
            var analysis = new AnalysisResult { Bpm = 120.0, OffsetSeconds = 0.0, Onsets = onsets };
            var meta = new SongMeta
            {
                Id = "selftest",
                Title = "Self Test",
                AudioPath = "",
                DurationSeconds = 20.0,
                Difficulties = new List<string> { "easy", "normal", "hard", "expert" }
            };

            Dictionary<string, ChartData> charts = ChartGenerator.Generate(analysis, meta);
            if (charts == null)
            {
                Fail("Chart: ChartGenerator.Generate returned null");
                return false;
            }

            string[] difficulties = { "easy", "normal", "hard", "expert" };
            bool allOk = true;
            foreach (string difficulty in difficulties)
            {
                DifficultyProfile profile;
                if (!DifficultyProfiles.All.TryGetValue(difficulty, out profile) || profile == null)
                {
                    Fail("Chart: no DifficultyProfiles.All entry for '" + difficulty + "'");
                    allOk = false;
                    continue;
                }
                ChartData chart;
                if (!charts.TryGetValue(difficulty, out chart) || chart == null)
                {
                    Fail("Chart '" + difficulty + "': missing from generated dictionary");
                    allOk = false;
                    continue;
                }
                string error = ValidateChart(chart, profile);
                if (error == null)
                {
                    int noteCount = chart.Notes != null ? chart.Notes.Count : 0;
                    Pass("Chart '" + difficulty + "': " + noteCount + " notes, constraints hold "
                         + "(maxNps<=" + profile.MaxNotesPerSecond.ToString("F1")
                         + ", laneGap>=" + profile.MinSameLaneGapSeconds.ToString("F2") + "s)");
                }
                else
                {
                    Fail("Chart '" + difficulty + "': " + error);
                    allOk = false;
                }
            }
            return allOk;
        }

        // 返回 null 表示通过；否则返回违规描述。音符须已按时间升序（第一项检查保证）。
        private static string ValidateChart(ChartData chart, DifficultyProfile profile)
        {
            var notes = chart.Notes != null ? chart.Notes : new List<NoteData>();

            // 1) 时间升序
            for (int i = 1; i < notes.Count; i++)
            {
                if (notes[i].Time < notes[i - 1].Time - 1e-9)
                    return "note " + i + " time " + notes[i].Time.ToString("F3")
                           + " < previous " + notes[i - 1].Time.ToString("F3");
            }

            // 2) lane/type/duration 合法性
            for (int i = 0; i < notes.Count; i++)
            {
                NoteData n = notes[i];
                if (n.Lane < 0 || n.Lane > 3) return "note " + i + " lane " + n.Lane + " out of 0..3";
                if (n.Type != "tap" && n.Type != "hold") return "note " + i + " unknown type '" + n.Type + "'";
                if (n.Type == "hold" && n.Duration <= 0.0) return "hold note " + i + " duration " + n.Duration;
                if (n.Time < -1e-9) return "note " + i + " negative time";
            }

            // 3) 任意 1 秒窗内音符数 ≤ MaxNotesPerSecond（双指针滑窗）
            int maxNps = 0;
            for (int i = 0, j = 0; i < notes.Count; i++)
            {
                if (j < i) j = i;
                double windowEnd = notes[i].Time + 1.0 - 1e-9;
                while (j < notes.Count && notes[j].Time < windowEnd) j++;
                if (j - i > maxNps) maxNps = j - i;
            }
            if (maxNps > profile.MaxNotesPerSecond + 1e-9)
                return "max notes/sec " + maxNps + " > budget " + profile.MaxNotesPerSecond;

            // 4) 同 lane 最小间隔；hold 期间同 lane 不得出现新 note（含 hold 重叠）
            var laneLastStart = new double[4];
            var laneHasNote = new bool[4];
            var laneHoldEnd = new double[4];
            for (int i = 0; i < notes.Count; i++)
            {
                NoteData n = notes[i];
                int lane = n.Lane;
                if (laneHasNote[lane] && n.Time - laneLastStart[lane] < profile.MinSameLaneGapSeconds - 1e-6)
                    return "note " + i + " same-lane gap " + (n.Time - laneLastStart[lane]).ToString("F3")
                           + "s < " + profile.MinSameLaneGapSeconds.ToString("F3") + "s (lane " + lane + ")";
                if (n.Time < laneHoldEnd[lane] - 1e-6)
                    return "note " + i + " at " + n.Time.ToString("F3")
                           + "s overlaps active hold in lane " + lane + " (ends " + laneHoldEnd[lane].ToString("F3") + "s)";
                laneLastStart[lane] = n.Time;
                laneHasNote[lane] = true;
                if (n.Type == "hold")
                {
                    double end = n.Time + n.Duration;
                    if (end > laneHoldEnd[lane]) laneHoldEnd[lane] = end;
                }
            }
            return null;
        }
    }
}
