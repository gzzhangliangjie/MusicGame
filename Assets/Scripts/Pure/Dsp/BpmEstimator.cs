using System;
using System.Collections.Generic;
using MusicGame.Pure.Model;

namespace MusicGame.Pure.Dsp
{
    /// <summary>
    /// BPM 估计器（60–200 BPM）。
    /// 两条证据：① onset 强度包络（10ms 分辨率，强度摊到相邻 bin 以抗抖动）
    /// 在 0.3–1.0s 滞后上的自相关；② 相邻 onset 间隔直方图（折算到 60–200 后
    /// 0.25BPM 分箱并平滑）。综合分数上叠加一个以 120 为中心的轻度 log2 节拍
    /// 先验（Ellis 风格）；八度歧义（倍频/半频分数接近）时显式取更接近 120 的
    /// 分支。参数已用 120/200/100/90/80 BPM 合成信号验证。
    /// </summary>
    public class BpmEstimator
    {
        private const double EnvelopeStep = 0.01; // 包络时间分辨率（秒/bin）
        private const double MinBpm = 60.0;
        private const double MaxBpm = 200.0;
        private const double HistogramBinWidth = 0.25;
        private const int HistogramBins = 561;    // 60..200 含端点
        private const double FoldEpsilon = 1e-6;  // 折算边界的浮点容差

        /// <summary>由 onset 列表估计 BPM；onset 少于 2 个时返回 0 表示无法估计。</summary>
        public static double Estimate(List<Onset> onsets)
        {
            if (onsets == null || onsets.Count < 2) return 0.0;

            // ---- onset 强度包络 ----
            // 开方压缩：Strength 全局可比后低频 onset 的 per-bin flux 天然远大于高频
            // （底鼓集中能量 vs 镲片摊在几百个 bin），线性加权会让包络只剩低频节奏，
            // 自相关出假峰（实测 木鱼与金鱼 112.25→75.00）。开方保序削垄断，BPM 复原。
            double lastTime = 0.0;
            for (int i = 0; i < onsets.Count; i++)
                if (onsets[i].TimeSeconds > lastTime) lastTime = onsets[i].TimeSeconds;
            int length = (int)(lastTime / EnvelopeStep) + 2;
            if (length < 32) length = 32;
            var envelope = new double[length];
            for (int i = 0; i < onsets.Count; i++)
            {
                double pos = onsets[i].TimeSeconds / EnvelopeStep;
                int i0 = (int)Math.Floor(pos);
                double frac = pos - i0;
                double w = Math.Sqrt(Math.Max(0.0, onsets[i].Strength));
                if (i0 >= 0 && i0 < length) envelope[i0] += w * (1.0 - frac);
                if (i0 + 1 >= 0 && i0 + 1 < length) envelope[i0 + 1] += w * frac;
            }

            // ---- 自相关（无偏归一化），滞后对应 60..200 BPM ----
            int minLag = (int)Math.Round(60.0 / MaxBpm / EnvelopeStep);
            int maxLag = (int)Math.Round(60.0 / MinBpm / EnvelopeStep);
            var acf = new double[maxLag + 1];
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double acc = 0.0;
                int count = length - lag;
                for (int i = 0; i < count; i++) acc += envelope[i] * envelope[i + lag];
                acf[lag] = count > 0 ? acc / count : 0.0;
            }
            double acfMax = 0.0;
            for (int lag = minLag; lag <= maxLag; lag++)
                if (acf[lag] > acfMax) acfMax = acf[lag];
            if (acfMax <= 0.0) acfMax = 1.0;

            // ---- 相邻 onset 间隔直方图（折算进 60–200 后计票） ----
            var times = new List<double>(onsets.Count);
            for (int i = 0; i < onsets.Count; i++) times.Add(onsets[i].TimeSeconds);
            times.Sort();
            var histogram = new double[HistogramBins];
            for (int i = 1; i < times.Count; i++)
            {
                double interval = times[i] - times[i - 1];
                if (interval < 0.15 || interval > 2.0) continue;
                double bpm = 60.0 / interval;
                while (bpm > MaxBpm + FoldEpsilon) bpm *= 0.5;
                while (bpm < MinBpm - FoldEpsilon) bpm *= 2.0;
                int idx = (int)((bpm - MinBpm) / HistogramBinWidth);
                if (idx >= HistogramBins) idx = HistogramBins - 1;
                if (idx >= 0) histogram[idx] += 1.0;
            }
            // 直方图平滑（近似高斯的 [1,2,3,4,3,2,1] 核）
            var smooth = new double[HistogramBins];
            int[] offsets = { -3, -2, -1, 0, 1, 2, 3 };
            double[] kernelWeights = { 1, 2, 3, 4, 3, 2, 1 };
            for (int i = 0; i < HistogramBins; i++)
            {
                double sum = 0.0, weightSum = 0.0;
                for (int k = 0; k < offsets.Length; k++)
                {
                    int j = i + offsets[k];
                    if (j >= 0 && j < HistogramBins)
                    {
                        sum += histogram[j] * kernelWeights[k];
                        weightSum += kernelWeights[k];
                    }
                }
                smooth[i] = weightSum > 0.0 ? sum / weightSum : 0.0;
            }
            double histMax = 0.0;
            for (int i = 0; i < HistogramBins; i++)
                if (smooth[i] > histMax) histMax = smooth[i];
            if (histMax <= 0.0) histMax = 1.0;

            // ---- 综合评分（线性插值取任意 BPM 处的值） ----
            double AcfAt(double bpm)
            {
                double lag = 60.0 / bpm / EnvelopeStep;
                int l0 = (int)Math.Floor(lag);
                double frac = lag - l0;
                double v0 = acf[Clamp(l0, minLag, maxLag)] / acfMax;
                double v1 = acf[Clamp(l0 + 1, minLag, maxLag)] / acfMax;
                return v0 * (1.0 - frac) + v1 * frac;
            }
            double HistAt(double bpm)
            {
                double pos = (bpm - MinBpm) / HistogramBinWidth;
                int i0 = (int)Math.Floor(pos);
                double frac = pos - i0;
                double h0 = i0 >= 0 && i0 < HistogramBins ? smooth[i0] / histMax : 0.0;
                double h1 = i0 + 1 >= 0 && i0 + 1 < HistogramBins ? smooth[i0 + 1] / histMax : 0.0;
                return h0 * (1.0 - frac) + h1 * frac;
            }
            double Prior(double bpm)
            {
                // 轻度 120 BPM log 先验（1 个八度宽度），仅作用于自相关项
                double octaves = Math.Log(bpm / 120.0, 2.0);
                return Math.Exp(-0.5 * octaves * octaves);
            }
            double Score(double bpm) { return AcfAt(bpm) * Prior(bpm) + 0.6 * HistAt(bpm); }

            // ---- 粗网格搜最优（0.1 BPM 步进） ----
            const int gridSteps = 1401;
            double bestBpm = MinBpm, bestScore = double.NegativeInfinity;
            for (int i = 0; i < gridSteps; i++)
            {
                double bpm = MinBpm + i * ((MaxBpm - MinBpm) / (gridSteps - 1));
                double s = Score(bpm);
                if (s > bestScore) { bestScore = s; bestBpm = bpm; }
            }

            // ---- 八度歧义：倍频/半频折回范围内且分数接近时，取更接近 120 的分支 ----
            double chosen = bestBpm;
            double[] octaveSources = { bestBpm * 2.0, bestBpm * 0.5 };
            for (int i = 0; i < octaveSources.Length; i++)
            {
                double alt = octaveSources[i];
                while (alt > MaxBpm + FoldEpsilon) alt *= 0.5;
                while (alt < MinBpm - FoldEpsilon) alt *= 2.0;
                if (alt < MinBpm - 1e-3 || alt > MaxBpm + 1e-3) continue; // 折不进范围
                if (Math.Abs(alt - bestBpm) <= 0.2) continue;             // 折回了自身
                if (Score(alt) >= 0.9 * bestScore && Math.Abs(alt - 120.0) < Math.Abs(chosen - 120.0))
                    chosen = alt;
            }

            // ---- 0.05 BPM 细化 ----
            double fineBpm = chosen, fineScore = Score(chosen);
            double lo = Math.Max(MinBpm, chosen - 1.0);
            double hi = Math.Min(MaxBpm, chosen + 1.0);
            for (double b = lo; b <= hi + 1e-9; b += 0.05)
            {
                double s = Score(b);
                if (s > fineScore) { fineScore = s; fineBpm = b; }
            }
            return Math.Round(fineBpm, 3);
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
