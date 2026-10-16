using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MusicGame.Pure.Dsp;
using MusicGame.Pure.Model;
using UnityEngine;

namespace MusicGame.Analysis
{
    /// <summary>
    /// 整曲离线分析组件：AudioClip.GetData 取全量采样并混单声道，
    /// 后台线程跑纯 DSP（频谱流 onset + BPM + 节拍相位），主线程回调结果与进度。
    /// 结果缓存于 Songs/&lt;songId&gt;/analysis.json，缓存命中直接返回（不重算）。
    /// 缓存目录位于工作区根（编辑器下即工程根），.gitignore 不忽略。
    /// </summary>
    public class SongAnalyzer : MonoBehaviour
    {
        private const string CacheFileName = "analysis.json";
        private const float ProgressStart = 0.05f;  // 采样拷贝完成后进入分析阶段
        private const float ProgressSpan = 0.90f;   // 分析阶段映射到 0.05..0.95

        // 后台线程 → 主线程的动作队列（完成/异常回调）
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        // 进行中的分析任务（进度轮询）
        private readonly object _jobsLock = new object();
        private readonly List<AnalysisJob> _jobs = new List<AnalysisJob>();

        private sealed class AnalysisJob
        {
            public SpectralFluxOnsetDetector Detector;
            public Action<float> OnProgress;
            public volatile bool Done;
        }

        /// <summary>
        /// 分析一首歌；onDone / onProgress 均在主线程回调。
        /// 缓存命中时同步回调（进度直接 1）。
        /// </summary>
        public void Analyze(AudioClip clip, string songId, Action<AnalysisResult> onDone, Action<float> onProgress)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (string.IsNullOrEmpty(songId)) throw new ArgumentNullException(nameof(songId));

            string dir = Path.Combine(ResolveCacheRoot(), SanitizeId(songId));
            string cachePath = Path.Combine(dir, CacheFileName);

            // —— 缓存命中：直接返回 ——
            if (File.Exists(cachePath))
            {
                try
                {
                    var cached = JsonUtility.FromJson<AnalysisResult>(File.ReadAllText(cachePath));
                    if (cached != null && cached.Onsets != null)
                    {
                        if (onProgress != null) onProgress(1f);
                        if (onDone != null) onDone(cached);
                        return;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[SongAnalyzer] Cache read failed, re-analyzing: " + e.Message);
                }
            }

            // —— 主线程取全量采样并混单声道（Unity API 仅主线程可用） ——
            float[] raw = new float[clip.samples * clip.channels];
            if (!clip.GetData(raw, 0))
                throw new InvalidOperationException("AudioClip.GetData failed for " + songId);
            float[] mono = DownmixToMono(raw, clip.channels);
            int sampleRate = clip.frequency;
            if (onProgress != null) onProgress(ProgressStart);

            // —— 后台线程跑纯 DSP ——
            var job = new AnalysisJob
            {
                Detector = new SpectralFluxOnsetDetector(),
                OnProgress = onProgress
            };
            lock (_jobsLock) { _jobs.Add(job); }

            Task.Run(delegate
            {
                AnalysisResult result;
                try
                {
                    List<Onset> onsets = job.Detector.Detect(mono, sampleRate);
                    double bpm = BpmEstimator.Estimate(onsets);
                    result = new AnalysisResult
                    {
                        Bpm = bpm,
                        OffsetSeconds = EstimateBeatOffset(bpm, onsets),
                        Onsets = onsets
                    };
                }
                catch (Exception e)
                {
                    job.Done = true;
                    EnqueueOnMainThread(delegate
                    {
                        RemoveJob(job);
                        Debug.LogError("[SongAnalyzer] Analysis failed for " + songId + ": " + e);
                    });
                    return;
                }
                job.Done = true;
                EnqueueOnMainThread(delegate
                {
                    RemoveJob(job);
                    try { SaveCache(cachePath, dir, result); }
                    catch (Exception e) { Debug.LogWarning("[SongAnalyzer] Cache write failed: " + e.Message); }
                    if (onProgress != null) onProgress(1f);
                    if (onDone != null) onDone(result);
                });
            });
        }

        private void Update()
        {
            // 主线程队列：完成/异常回调
            while (_mainThreadQueue.TryDequeue(out Action action))
                action();

            // 进行中任务：汇报分析进度（检测器在后台线程更新 Progress）
            AnalysisJob[] snapshot;
            lock (_jobsLock)
            {
                if (_jobs.Count == 0) return;
                snapshot = _jobs.ToArray();
            }
            for (int i = 0; i < snapshot.Length; i++)
            {
                var job = snapshot[i];
                if (!job.Done && job.OnProgress != null)
                    job.OnProgress(ProgressStart + job.Detector.Progress * ProgressSpan);
            }
        }

        private void RemoveJob(AnalysisJob job)
        {
            lock (_jobsLock) { _jobs.Remove(job); }
        }

        private void EnqueueOnMainThread(Action action)
        {
            _mainThreadQueue.Enqueue(action);
        }

        // 多声道平均混为单声道
        private static float[] DownmixToMono(float[] raw, int channels)
        {
            if (channels <= 1) return raw;
            int frames = raw.Length / channels;
            var mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                double sum = 0.0;
                int baseIdx = i * channels;
                for (int c = 0; c < channels; c++) sum += raw[baseIdx + c];
                mono[i] = (float)(sum / channels);
            }
            return mono;
        }

        // 节拍相位：onset 时间对拍长取模的强度圆直方图取峰（5ms 分箱）
        private static double EstimateBeatOffset(double bpm, List<Onset> onsets)
        {
            if (bpm <= 0.0 || onsets == null || onsets.Count == 0) return 0.0;
            double period = 60.0 / bpm;
            const double step = 0.005;
            int bins = Math.Max(1, (int)Math.Ceiling(period / step));
            var hist = new double[bins];
            for (int i = 0; i < onsets.Count; i++)
            {
                double phase = onsets[i].TimeSeconds % period;
                int idx = Math.Min(bins - 1, (int)(phase / step));
                hist[idx] += onsets[i].Strength;
            }
            // ±1 bin 平滑后取峰
            double best = 0.0; int bestIdx = 0;
            for (int i = 0; i < bins; i++)
            {
                double v = hist[i]
                           + (i > 0 ? 0.5 * hist[i - 1] : 0.0)
                           + (i + 1 < bins ? 0.5 * hist[i + 1] : 0.0);
                if (v > best) { best = v; bestIdx = i; }
            }
            return bestIdx * step;
        }

        // 缓存根：与 GameFlow/GameManager 同一口径（工程根/可执行文件目录下的 Songs/），
        // 避免独立构建后 CWD 漂移导致缓存判断失效、每次启动重分析
        private static string ResolveCacheRoot()
        {
            try
            {
                var parent = Directory.GetParent(Application.dataPath);
                if (parent != null) return Path.Combine(parent.FullName, "Songs");
            }
            catch (Exception) { }
            return "Songs";
        }

        private static void SaveCache(string path, string dir, AnalysisResult result)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonUtility.ToJson(result, true));
        }

        // 去掉文件名非法字符
        private static string SanitizeId(string songId)
        {
            var chars = songId.ToCharArray();
            var invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < chars.Length; i++)
            {
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (chars[i] == invalid[j]) { chars[i] = '_'; break; }
                }
            }
            return new string(chars);
        }
    }
}
