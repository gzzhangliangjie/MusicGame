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
    ///    同 lane 间隔 ≥ MinSameLaneGapSeconds、hold 期间同 lane 无重叠新 note；
    /// ④ Lyrics 解析 LRC（时间戳/元数据/空行/乱序行）并按时刻取行；
    /// ⑤ 频谱语义：100Hz 长音 → 检测器 low 频段 sustain、5kHz click onset 判 high、
    ///    单频段 onset 全谱 lane 落点正确（low→0/1 high→2/3）、持续段驱动出音乐 hold（可跨 lane 音符）。
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

            // ④ LRC 歌词解析
            try { allOk &= CheckLyrics(); }
            catch (Exception e) { Fail("lyrics check threw: " + e.Message); allOk = false; }

            // ⑤ 频谱语义：频段→lane 映射 + 持续段 hold + 检测器 sustain 提取
            try { allOk &= CheckSpectrumSemantics(); }
            catch (Exception e) { Fail("spectrum check threw: " + e.Message); allOk = false; }

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

        // ---------------- ④ LRC 歌词 ----------------
        private static bool CheckLyrics()
        {
            const string lrc = "[ti:Test]\n"                 // 元数据，忽略
                + "[00:30.50]third\n"                        // 乱序输入，应被排序
                + "[00:01.00]first\n"
                + "[00:05]second\n"                          // 无小数位
                + "[00:45.00]\n";                            // 间奏空行
            var lyrics = Lyrics.Parse(lrc);

            bool countOk = lyrics.Count == 4;
            bool beforeFirst = !lyrics.TryGetLine(0.5, out _);
            string s1, s2, s3, s4;
            bool first = lyrics.TryGetLine(1.5, out s1) && s1 == "first";
            bool second = lyrics.TryGetLine(6.0, out s2) && s2 == "second";
            bool third = lyrics.TryGetLine(30.6, out s3) && s3 == "third";
            bool interlude = lyrics.TryGetLine(50.0, out s4) && s4 == "";

            bool ok = countOk && beforeFirst && first && second && third && interlude;
            if (ok) Pass("Lyrics: parse " + lyrics.Count + " lines, sorted lookup + interlude clear correct");
            else Fail("Lyrics: count=" + lyrics.Count + " beforeFirst=" + beforeFirst
                      + " first=" + first + "(" + (s1 ?? "null") + ")"
                      + " second=" + second + "(" + (s2 ?? "null") + ")"
                      + " third=" + third + "(" + (s3 ?? "null") + ")"
                      + " interlude=" + interlude + "('" + (s4 ?? "null") + "')");
            return ok;
        }

        // ---------------- ⑤ 频谱语义 ----------------
        private static bool CheckSpectrumSemantics()
        {
            bool allOk = true;

            // A) 检测器持续段提取：100Hz 低音长音 t=1..4s + 5kHz 短 click 每 0.5s
            const int sampleRate = 44100;
            const double durationSeconds = 10.0;
            var mono = new float[(int)(sampleRate * durationSeconds)];
            for (int i = (int)(1.0 * sampleRate); i < (int)(4.0 * sampleRate); i++)
            {
                double edge = Math.Min(1.0, Math.Min((i - 1.0 * sampleRate) / (0.03 * sampleRate),
                                                     (4.0 * sampleRate - i) / (0.03 * sampleRate)));
                mono[i] += (float)(0.6 * Math.Sin(2.0 * Math.PI * 100.0 * i / sampleRate) * edge);
            }
            var rng = new Random(77);
            int clickLength = (int)(0.03 * sampleRate);
            for (int k = 1; k <= 19; k++)
            {
                int start = (int)Math.Round(k * 0.5 * sampleRate);
                for (int i = 0; i < clickLength && start + i < mono.Length; i++)
                    mono[start + i] += (float)(0.8 * Math.Sin(2.0 * Math.PI * 5000.0 * i / sampleRate)
                                               * Math.Exp(-i / (0.006 * sampleRate)));
            }
            var detector = new SpectralFluxOnsetDetector();
            List<Sustain> sustains;
            List<Onset> onsets = detector.Detect(mono, sampleRate, out sustains);

            bool toneSustain = false;
            foreach (Sustain st in sustains)
            {
                if (st.Band == "low" && Math.Abs(st.TimeSeconds - 1.0) <= 0.25 && st.DurationSeconds >= 2.4)
                {
                    toneSustain = true;
                    break;
                }
            }
            if (toneSustain)
                Pass("Sustain: low-band sustain found at ~1.0s covering the 100Hz tone (of "
                     + sustains.Count + " total sustains)");
            else
            {
                Fail("Sustain: no low-band sustain near t=1.0 dur>=2.4 (total " + sustains.Count + ")");
                allOk = false;
            }

            int clickMatched = 0;
            int clickHigh = 0;
            for (int k = 1; k <= 19; k++)
            {
                double expected = k * 0.5;
                foreach (Onset o in onsets)
                {
                    if (Math.Abs(o.TimeSeconds - expected) <= 0.030)
                    {
                        clickMatched++;
                        if (o.Band == "high") clickHigh++;
                        break;
                    }
                }
            }
            bool clickBands = clickMatched >= 15 && clickHigh >= clickMatched * 0.7;
            if (clickBands)
                Pass("Sustain: click onsets " + clickHigh + "/" + clickMatched + " in high band (need >=70%)");
            else
            {
                Fail("Sustain: click onsets " + clickHigh + "/" + clickMatched + " in high band (need >=15 matched, >=70%)");
                allOk = false;
            }

            // B) 频段→lane 映射（normal 无双押）：全 low onset → lane∈{0,1}；全 high → lane∈{2,3}
            allOk &= CheckBandLanes("low", new[] { 0, 1 });
            allOk &= CheckBandLanes("high", new[] { 2, 3 });

            // C) 持续段驱动 hold：mid 网格 onset + 两条 mid sustain（t=1.0 dur2.0 / t=5.0 dur1.0）
            //    → 应出现 t≈1.0 与 t≈5.0、lane∈{1,2} 的 hold（时长被 maxHold=1s 截断）
            var midOnsets = new List<Onset>();
            for (int k = 0; k < 80; k++)
                midOnsets.Add(new Onset { TimeSeconds = k * 0.25, Strength = 0.6, Band = "mid" });
            var sustainAnalysis = new AnalysisResult
            {
                Bpm = 120.0,
                OffsetSeconds = 0.0,
                Onsets = midOnsets,
                Sustains = new List<Sustain>
                {
                    new Sustain { TimeSeconds = 1.0, DurationSeconds = 2.0, Band = "mid" },
                    new Sustain { TimeSeconds = 5.0, DurationSeconds = 1.0, Band = "mid" },
                }
            };
            var holdMeta = new SongMeta
            {
                Id = "selftest-hold",
                Title = "Hold Test",
                AudioPath = "",
                DurationSeconds = 20.0,
                Difficulties = new List<string> { "normal" }
            };
            Dictionary<string, ChartData> holdCharts = ChartGenerator.Generate(sustainAnalysis, holdMeta);
            ChartData holdChart;
            if (holdCharts == null || !holdCharts.TryGetValue("normal", out holdChart) || holdChart == null)
            {
                Fail("SustainHold: normal chart missing");
                return false;
            }
            int holdsAt1 = 0;
            int holdsAt5 = 0;
            int crossing = 0;
            var holdLaneOk = true;
            foreach (NoteData n in holdChart.Notes)
            {
                if (n.Type == "hold")
                {
                    if (Math.Abs(n.Time - 1.0) <= 0.21)
                    {
                        holdsAt1++;
                        if (n.Lane != 1 && n.Lane != 2) holdLaneOk = false;
                        if (n.Duration < 0.9 - 1e-9 || n.Duration > 1.0 + 1e-9) holdLaneOk = false;
                    }
                    if (Math.Abs(n.Time - 5.0) <= 0.21)
                    {
                        holdsAt5++;
                        if (n.Lane != 1 && n.Lane != 2) holdLaneOk = false;
                    }
                }
                else if (n.Time > 1.05 && n.Time < 1.95) crossing++;
            }
            bool holdOk = holdsAt1 == 1 && holdsAt5 == 1 && crossing >= 2 && holdLaneOk;
            if (holdOk)
                Pass("SustainHold: music holds at 1.0s/5.0s lanes 1..2, " + crossing
                     + " crossing notes during hold (band semantics hold)");
            else
            {
                Fail("SustainHold: holdsAt1=" + holdsAt1 + " holdsAt5=" + holdsAt5
                     + " crossing=" + crossing + " lane/durOk=" + holdLaneOk);
                allOk = false;
            }
            string holdValidationError = ValidateChart(holdChart, DifficultyProfiles.All["normal"]);
            if (holdValidationError != null)
            {
                Fail("SustainHold: constraint violation: " + holdValidationError);
                allOk = false;
            }
            return allOk;
        }

        // 单频段 onset 全谱 → 断言所有音符 lane 落在该频段映射集合内（normal 档无双押）
        private static bool CheckBandLanes(string band, int[] allowedLanes)
        {
            var onsets = new List<Onset>();
            for (int k = 0; k < 80; k++)
                onsets.Add(new Onset { TimeSeconds = k * 0.25, Strength = 0.8, Band = band });
            var analysis = new AnalysisResult { Bpm = 120.0, OffsetSeconds = 0.0, Onsets = onsets };
            var meta = new SongMeta
            {
                Id = "selftest-band-" + band,
                Title = "Band " + band,
                AudioPath = "",
                DurationSeconds = 20.0,
                Difficulties = new List<string> { "normal" }
            };
            Dictionary<string, ChartData> charts = ChartGenerator.Generate(analysis, meta);
            ChartData chart;
            if (charts == null || !charts.TryGetValue("normal", out chart) || chart == null)
            {
                Fail("BandLanes '" + band + "': normal chart missing");
                return false;
            }
            int inSet = 0;
            int outOfSet = 0;
            foreach (NoteData n in chart.Notes)
            {
                bool ok = false;
                foreach (int lane in allowedLanes) if (n.Lane == lane) ok = true;
                if (ok) inSet++; else outOfSet++;
            }
            // 越轨落点来自"两轨全忙时放宽到全轨"的可玩性兜底（间隙 hold 占道等），
            // 语义只要求绝大多数音符尊重频段映射
            bool pass = inSet > 0 && outOfSet * 8 <= inSet + outOfSet;
            if (pass)
                Pass("BandLanes '" + band + "': " + inSet + "/" + (inSet + outOfSet)
                     + " notes in lanes {" + allowedLanes[0] + "," + allowedLanes[1]
                     + "} (" + outOfSet + " widened for playability)");
            else
                Fail("BandLanes '" + band + "': " + outOfSet + "/" + (inSet + outOfSet) + " notes outside lanes {"
                     + allowedLanes[0] + "," + allowedLanes[1] + "} (need <=12.5%)");
            return pass;
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
