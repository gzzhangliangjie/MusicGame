using System;
using System.Collections.Generic;
using MusicGame.Pure.Model;

namespace MusicGame.Pure.Dsp
{
    /// <summary>
    /// 分频带频谱流（spectral flux）onset 检测器。
    /// Hann 窗 2048 / hop 512；低(20-150Hz)、中(150-2000Hz)、高(2000Hz-Nyquist)
    /// 三频带各自计算半波整流频谱流，并用滑动窗口自适应阈值（均值 + k·标准差）
    /// 检出候选帧；跨频带在最小间隔内合并取最强者，强度最终归一化到 0..1。
    /// 参数已用合成信号（440Hz 正弦、120BPM 脉冲轨）离线验证。
    /// </summary>
    public class SpectralFluxOnsetDetector
    {
        private const int FftSize = 2048;
        private const int HopSize = 512;
        private const double ThresholdK = 1.5;        // 自适应阈值：均值 + k·标准差
        private const int ThresholdHalfWindow = 10;  // 阈值滑动窗口 ±10 帧（约 ±116ms）
        private const double MinOnsetGapSeconds = 0.05; // 合并/去重最小 onset 间隔

        private volatile float _progress;

        /// <summary>上次 Detect 的整体进度（0..1），供上层在主线程汇报进度条。</summary>
        public float Progress
        {
            get { return _progress; }
        }

        /// <summary>输入单声道采样与采样率，返回按时间升序的 onset 列表。</summary>
        public List<Onset> Detect(float[] monoSamples, int sampleRate)
        {
            if (monoSamples == null) throw new ArgumentNullException(nameof(monoSamples));
            if (sampleRate <= 0) throw new ArgumentException("sampleRate must be positive: " + sampleRate);
            _progress = 0f;
            if (monoSamples.Length < FftSize) { _progress = 1f; return new List<Onset>(); }

            int numFrames = 1 + (monoSamples.Length - FftSize) / HopSize;
            int specBins = FftSize / 2 + 1;
            double binHz = (double)sampleRate / FftSize;

            // 三频带的 bin 区间（含两端），按采样率换算
            BandRange lowBand = MakeBand(20.0, 150.0, binHz, specBins);
            BandRange midBand = MakeBand(150.0, 2000.0, binHz, specBins);
            BandRange highBand = MakeBand(2000.0, sampleRate * 0.5, binHz, specBins);
            var bands = new[]
            {
                new BandInfo("low", lowBand),
                new BandInfo("mid", midBand),
                new BandInfo("high", highBand)
            };

            // Hann 窗
            var window = new double[FftSize];
            for (int i = 0; i < FftSize; i++)
                window[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / FftSize);

            // 每频带每帧的频谱流
            var flux = new double[bands.Length][];
            for (int b = 0; b < bands.Length; b++) flux[b] = new double[numFrames];

            var re = new double[FftSize];
            var im = new double[FftSize];
            var prevMag = new double[specBins];
            var curMag = new double[specBins];
            for (int f = 0; f < numFrames; f++)
            {
                int start = f * HopSize;
                for (int i = 0; i < FftSize; i++)
                {
                    re[i] = monoSamples[start + i] * window[i];
                    im[i] = 0.0;
                }
                Fft.Forward(re, im);
                for (int j = 0; j < specBins; j++)
                    curMag[j] = Math.Sqrt(re[j] * re[j] + im[j] * im[j]);

                for (int b = 0; b < bands.Length; b++)
                {
                    double sum = 0.0;
                    BandRange r = bands[b].Range;
                    for (int j = r.Lo; j <= r.Hi; j++)
                    {
                        double d = curMag[j] - prevMag[j];
                        if (d > 0.0) sum += d; // 半波整流
                    }
                    flux[b][f] = sum;
                }

                // 交换缓冲，下一帧的"上一频谱"
                var tmp = prevMag; prevMag = curMag; curMag = tmp;

                if ((f & 63) == 0) _progress = (float)f / numFrames;
            }

            // 自适应阈值 + 局部极大 → 候选 onset
            var candidates = new List<Candidate>();
            for (int b = 0; b < bands.Length; b++)
            {
                double[] fl = flux[b];
                for (int t = 0; t < numFrames; t++)
                {
                    double v = fl[t];
                    if (v <= 0.0) continue;
                    int wa = Math.Max(0, t - ThresholdHalfWindow);
                    int wb = Math.Min(numFrames - 1, t + ThresholdHalfWindow);
                    double mean = 0.0;
                    for (int i = wa; i <= wb; i++) mean += fl[i];
                    mean /= (wb - wa + 1);
                    double variance = 0.0;
                    for (int i = wa; i <= wb; i++)
                    {
                        double d = fl[i] - mean;
                        variance += d * d;
                    }
                    variance /= (wb - wa + 1);
                    double threshold = mean + ThresholdK * Math.Sqrt(variance);
                    if (v <= threshold) continue;
                    double prev = t > 0 ? fl[t - 1] : 0.0;
                    double next = t < numFrames - 1 ? fl[t + 1] : 0.0;
                    if (v >= prev && v > next)
                    {
                        // 用窗中心作为 onset 时刻，抵消窗位置偏置
                        double time = (t * HopSize + FftSize * 0.5) / sampleRate;
                        candidates.Add(new Candidate(time, v, bands[b].Name));
                    }
                }
            }

            // 时间升序，并在最小间隔内聚类合并（保留最强者）
            candidates.Sort((x, y) => x.Time.CompareTo(y.Time));
            var merged = new List<Candidate>();
            int idx = 0;
            while (idx < candidates.Count)
            {
                int j = idx;
                Candidate best = candidates[idx];
                while (j + 1 < candidates.Count &&
                       candidates[j + 1].Time - candidates[idx].Time <= MinOnsetGapSeconds)
                {
                    j++;
                    if (candidates[j].Flux > best.Flux) best = candidates[j];
                }
                merged.Add(best);
                idx = j + 1;
            }

            // 强度归一化到 0..1
            var onsets = new List<Onset>(merged.Count);
            double maxFlux = 0.0;
            for (int i = 0; i < merged.Count; i++) maxFlux = Math.Max(maxFlux, merged[i].Flux);
            if (maxFlux <= 0.0) maxFlux = 1.0;
            for (int i = 0; i < merged.Count; i++)
            {
                var c = merged[i];
                onsets.Add(new Onset
                {
                    TimeSeconds = c.Time,
                    Strength = c.Flux / maxFlux,
                    Band = c.Band
                });
            }
            _progress = 1f;
            return onsets;
        }

        private static BandRange MakeBand(double loHz, double hiHz, double binHz, int specBins)
        {
            int lo = Math.Max(1, (int)Math.Ceiling(loHz / binHz)); // 跳过直流
            int hi = Math.Min(specBins - 1, (int)(hiHz / binHz));
            return new BandRange(lo, Math.Max(lo, hi));
        }

        private struct BandRange
        {
            public readonly int Lo;
            public readonly int Hi;
            public BandRange(int lo, int hi) { Lo = lo; Hi = hi; }
        }

        private struct BandInfo
        {
            public readonly string Name;
            public readonly BandRange Range;
            public BandInfo(string name, BandRange range) { Name = name; Range = range; }
        }

        private struct Candidate
        {
            public readonly double Time;
            public readonly double Flux;
            public readonly string Band;
            public Candidate(double time, double flux, string band)
            {
                Time = time;
                Flux = flux;
                Band = band;
            }
        }
    }
}
