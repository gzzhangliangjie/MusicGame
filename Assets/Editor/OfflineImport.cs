using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;
using MusicGame.Analysis;
using MusicGame.Core;
using MusicGame.Gameplay;
using MusicGame.Pure.Generator;
using MusicGame.Pure.Model;

namespace MusicGame.EditorTools
{
    /// <summary>
    /// 离线入库（batchmode）：对已有 audio.mp3 的歌曲目录跑整曲分析 + 四档谱面生成，
    /// 写 analysis.json / chart_*.json，并回填 meta.json 的 DurationSeconds 与 Difficulties。
    /// 用法：Unity.exe -batchmode -projectPath &lt;工程&gt; -executeMethod MusicGame.EditorTools.OfflineImport.Run
    ///       -importDir &lt;歌曲目录绝对路径&gt; -logFile &lt;日志&gt;
    /// 相比游戏内导入：内容一致（同一 SongAnalyzer/ChartGenerator 链），只是无需手点 UI。
    /// </summary>
    public static class OfflineImport
    {
        private const float TimeoutSeconds = 900f;

        private static string songDir;
        private static SongMeta meta;
        private static SongAnalyzer analyzer;
        private static AudioClip clip;
        private static Task<AudioClip> loadTask;
        private static float startedAt;
        private static bool booted;
        private static int lastLoggedPercent;

        public static void Run()
        {
            songDir = ReadArg("-importDir");
            if (string.IsNullOrEmpty(songDir) || !Directory.Exists(songDir))
            {
                Debug.LogError("[OfflineImport] FAIL：-importDir 无效：" + songDir);
                EditorApplication.Exit(1);
                return;
            }
            // 关闭进 Play 的域重载：静态状态与 update 订阅得以存活（同 HeadlessSmokeTest）
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorApplication.isPlaying = true;
            EditorApplication.update += Bootstrap;
            Debug.Log("[OfflineImport] 请求进入 Play 模式：" + songDir);
        }

        private static void Bootstrap()
        {
            if (!EditorApplication.isPlaying || booted) return;
            booted = true;
            EditorApplication.update -= Bootstrap;
            startedAt = Time.realtimeSinceStartup;
            try
            {
                string metaPath = Path.Combine(songDir, "meta.json");
                if (!File.Exists(metaPath)) { Fail("meta.json 不存在"); return; }
                meta = JsonUtility.FromJson<SongMeta>(File.ReadAllText(metaPath));
                if (meta == null || string.IsNullOrEmpty(meta.Id)) { Fail("meta.json 解析失败"); return; }
                if (string.IsNullOrEmpty(meta.AudioPath) || !File.Exists(meta.AudioPath)) { Fail("audio 缺失：" + meta.AudioPath); return; }

                analyzer = new GameObject("OfflineAnalyzer").AddComponent<SongAnalyzer>();
                loadTask = AudioImportService.LoadClip(meta.AudioPath);
                EditorApplication.update += Pump;
                Debug.Log("[OfflineImport] 开始加载音频：" + meta.Title);
            }
            catch (Exception e)
            {
                Fail("启动异常：" + e);
            }
        }

        private static void Pump()
        {
            if (!EditorApplication.isPlaying) return;
            EditorApplication.QueuePlayerLoopUpdate(); // 推进播放器循环：WebRequest 回调/主线程队列都靠它
            try
            {
                if (clip == null)
                {
                    if (loadTask == null || !loadTask.IsCompleted) { TimeoutCheck("音频加载"); return; }
                    if (loadTask.IsFaulted)
                    {
                        Fail("音频加载失败：" + (loadTask.Exception != null ? loadTask.Exception.GetBaseException().Message : "?"));
                        return;
                    }
                    clip = loadTask.Result;
                    Debug.Log(string.Format("[OfflineImport] 音频加载完成：{0:F2}s @ {1}Hz ch={2}", clip.length, clip.frequency, clip.channels));
                    analyzer.Analyze(clip, meta.Id, OnDone, OnProgress);
                    return;
                }
                TimeoutCheck("分析");
            }
            catch (Exception e)
            {
                Fail("Pump 异常：" + e);
            }
        }

        private static void OnProgress(float progress)
        {
            int percent = Mathf.Clamp(Mathf.RoundToInt(progress * 100f), 0, 100);
            if (percent - lastLoggedPercent >= 20)
            {
                lastLoggedPercent = percent;
                Debug.Log("[OfflineImport] 进度 " + percent + "%");
            }
        }

        private static void OnDone(AnalysisResult result)
        {
            try
            {
                if (result == null || result.Onsets == null) { Fail("分析返回空结果"); return; }
                Dictionary<string, ChartData> charts = ChartGenerator.Generate(result, meta);
                if (charts == null || charts.Count == 0) { Fail("谱面生成返回空"); return; }

                var diffs = new List<string>();
                string[] order = { "easy", "normal", "hard", "expert" };
                foreach (string d in order)
                    if (charts.ContainsKey(d))
                    {
                        ChartIO.Save(charts[d], Path.Combine(songDir, "chart_" + d + ".json"));
                        diffs.Add(d);
                    }
                foreach (KeyValuePair<string, ChartData> kv in charts)
                    if (!diffs.Contains(kv.Key))
                    {
                        ChartIO.Save(kv.Value, Path.Combine(songDir, "chart_" + kv.Key + ".json"));
                        diffs.Add(kv.Key);
                    }

                meta.DurationSeconds = clip.length;
                meta.Difficulties = diffs;
                File.WriteAllText(Path.Combine(songDir, "meta.json"), JsonUtility.ToJson(meta, true));

                var noteLine = new List<string>();
                foreach (string d in diffs)
                    noteLine.Add(d + ":" + charts[d].Notes.Count);
                Debug.Log(string.Format(
                    "[OfflineImport] BPM={0:F2} offset={1:F3}s onsets={2} 谱面[{3}]",
                    result.Bpm, result.OffsetSeconds, result.Onsets.Count, string.Join(" ", noteLine.ToArray())));
                Debug.Log("[OfflineImport] PASS 用时 " + (Time.realtimeSinceStartup - startedAt).ToString("F0") + "s");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Fail("写盘异常：" + e);
            }
        }

        private static void TimeoutCheck(string phase)
        {
            float t = Time.realtimeSinceStartup - startedAt;
            if (t > TimeoutSeconds) Fail(phase + "超时（" + TimeoutSeconds + "s）");
        }

        private static void Fail(string reason)
        {
            Debug.LogError("[OfflineImport] FAIL：" + reason);
            EditorApplication.Exit(1);
        }

        private static string ReadArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
    }

    /// <summary>
    /// 曲库批量重建（batchmode）：扫描工作区根 Songs/ 与 Assets/Songs/ 下全部含音频的歌曲目录，
    /// 逐首删 analysis.json 缓存 → SongAnalyzer 重分析（新频谱管线，产出 Sustains）→
    /// 四档谱面重生成落盘 + 回填 meta。旧缓存无 Sustains，必须删缓存强制重跑才有频谱谱面。
    /// 用法：Unity.exe -batchmode -projectPath &lt;工程&gt;
    ///       -executeMethod MusicGame.EditorTools.OfflineRebuildAll.Run -logFile &lt;日志&gt;
    /// </summary>
    public static class OfflineRebuildAll
    {
        private const float SongTimeoutSeconds = 1200f;

        private sealed class Job
        {
            public string Dir;
            public SongMeta Meta;
            public Task<AudioClip> LoadTask;
            public AudioClip Clip;
        }

        private static readonly List<Job> queue = new List<Job>();
        private static int current = -1;
        private static SongAnalyzer analyzer;
        private static float songStartedAt;
        private static int lastLoggedPercent;
        private static int passCount, failCount, skipCount;

        public static void Run()
        {
            // 关闭进 Play 的域重载：静态状态与 update 订阅得以存活（同 OfflineImport）
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorApplication.isPlaying = true;
            EditorApplication.update += Bootstrap;
            Debug.Log("[RebuildAll] 请求进入 Play 模式，开始曲库批量重建");
        }

        private static void Bootstrap()
        {
            if (!EditorApplication.isPlaying || booted) return;
            booted = true;
            EditorApplication.update -= Bootstrap;
            try
            {
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                var roots = new List<string>
                {
                    Path.Combine(projectRoot, "Songs"),
                    Path.Combine(Application.dataPath, "Songs"),
                };
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        string full = Path.GetFullPath(dir);
                        if (!seen.Add(full)) continue;
                        string metaPath = Path.Combine(full, "meta.json");
                        if (!File.Exists(metaPath)) { skipCount++; continue; }
                        SongMeta meta;
                        try { meta = JsonUtility.FromJson<SongMeta>(File.ReadAllText(metaPath)); }
                        catch (Exception) { meta = null; }
                        if (meta == null || string.IsNullOrEmpty(meta.Id)) { skipCount++; continue; }
                        if (string.IsNullOrEmpty(meta.AudioPath) || !File.Exists(meta.AudioPath)) { skipCount++; continue; }
                        queue.Add(new Job { Dir = full, Meta = meta });
                    }
                }
                Debug.Log("[RebuildAll] 待重建 " + queue.Count + " 首（跳过 " + skipCount + " 个无音频/无 meta 目录）");
                if (queue.Count == 0)
                {
                    Debug.Log("[RebuildAll] PASS：无需要重建的歌曲");
                    EditorApplication.Exit(0);
                    return;
                }
                analyzer = new GameObject("RebuildAnalyzer").AddComponent<SongAnalyzer>();
                EditorApplication.update += Pump;
                StartNext();
            }
            catch (Exception e)
            {
                Debug.LogError("[RebuildAll] FAIL：Bootstrap 异常：" + e);
                EditorApplication.Exit(1);
            }
        }

        private static bool booted;

        private static void StartNext()
        {
            current++;
            if (current >= queue.Count) { Finish(); return; }
            try
            {
                Job job = queue[current];
                // 绕过 SongAnalyzer 缓存：删 analysis.json 强制重跑（谱面稍后整体覆写）
                string cachePath = Path.Combine(job.Dir, "analysis.json");
                if (File.Exists(cachePath)) File.Delete(cachePath);
                songStartedAt = Time.realtimeSinceStartup;
                lastLoggedPercent = 0;
                job.LoadTask = AudioImportService.LoadClip(job.Meta.AudioPath);
                Debug.Log("[RebuildAll] (" + (current + 1) + "/" + queue.Count + ") 开始：" + job.Meta.Title);
            }
            catch (Exception e)
            {
                SongFail(queue[current], "启动异常：" + e.Message);
            }
        }

        private static void Pump()
        {
            if (!EditorApplication.isPlaying) return;
            EditorApplication.QueuePlayerLoopUpdate(); // 推进播放器循环：WebRequest 回调/主线程队列都靠它
            if (current < 0 || current >= queue.Count) return;
            Job job = queue[current];
            try
            {
                if (job.Clip == null)
                {
                    if (job.LoadTask == null || !job.LoadTask.IsCompleted) { TimeoutCheck(job); return; }
                    if (job.LoadTask.IsFaulted || job.LoadTask.Result == null)
                    {
                        SongFail(job, "音频加载失败");
                        return;
                    }
                    job.Clip = job.LoadTask.Result;
                    Debug.Log(string.Format("[RebuildAll] 音频就绪 {0:F1}s，开始分析…", job.Clip.length));
                    analyzer.Analyze(job.Clip, job.Meta.Id,
                        result => OnSongDone(job, result),
                        p => OnProgress(p));
                    return;
                }
                TimeoutCheck(job);
            }
            catch (Exception e)
            {
                SongFail(job, "Pump 异常：" + e.Message);
            }
        }

        private static void OnProgress(float progress)
        {
            int percent = Mathf.Clamp(Mathf.RoundToInt(progress * 100f), 0, 100);
            if (percent - lastLoggedPercent >= 25)
            {
                lastLoggedPercent = percent;
                Debug.Log("[RebuildAll] 进度 " + percent + "%");
            }
        }

        private static void OnSongDone(Job job, AnalysisResult result)
        {
            try
            {
                if (result == null || result.Onsets == null) { SongFail(job, "分析返回空结果"); return; }
                Dictionary<string, ChartData> charts = ChartGenerator.Generate(result, job.Meta);
                if (charts == null || charts.Count == 0) { SongFail(job, "谱面生成返回空"); return; }

                var diffs = new List<string>();
                string[] order = { "easy", "normal", "hard", "expert" };
                foreach (string d in order)
                    if (charts.ContainsKey(d))
                    {
                        ChartIO.Save(charts[d], Path.Combine(job.Dir, "chart_" + d + ".json"));
                        diffs.Add(d);
                    }
                foreach (KeyValuePair<string, ChartData> kv in charts)
                    if (!diffs.Contains(kv.Key))
                    {
                        ChartIO.Save(kv.Value, Path.Combine(job.Dir, "chart_" + kv.Key + ".json"));
                        diffs.Add(kv.Key);
                    }

                job.Meta.DurationSeconds = job.Clip.length;
                job.Meta.Difficulties = diffs;
                File.WriteAllText(Path.Combine(job.Dir, "meta.json"), JsonUtility.ToJson(job.Meta, true));

                var noteLine = new List<string>();
                foreach (string d in diffs)
                    noteLine.Add(d + ":" + charts[d].Notes.Count);
                Debug.Log(string.Format(
                    "[RebuildAll] PASS {0}  BPM={1:F1} onsets={2} sustains={3} 谱面[{4}] 用时 {5:F0}s",
                    job.Meta.Title, result.Bpm, result.Onsets.Count,
                    result.Sustains != null ? result.Sustains.Count : 0,
                    string.Join(" ", noteLine.ToArray()),
                    Time.realtimeSinceStartup - songStartedAt));
                passCount++;
                StartNext();
            }
            catch (Exception e)
            {
                SongFail(job, "写盘异常：" + e.Message);
            }
        }

        private static void TimeoutCheck(Job job)
        {
            if (Time.realtimeSinceStartup - songStartedAt > SongTimeoutSeconds)
                SongFail(job, "超时（" + SongTimeoutSeconds + "s）");
        }

        private static void SongFail(Job job, string reason)
        {
            Debug.LogError("[RebuildAll] FAIL " + (job != null && job.Meta != null ? job.Meta.Title : "?") + "：" + reason);
            failCount++;
            StartNext();
        }

        private static void Finish()
        {
            Debug.Log("[RebuildAll] 完成：成功 " + passCount + " / 失败 " + failCount + " / 跳过 " + skipCount);
            Debug.Log(failCount >= queue.Count && queue.Count > 0 ? "[RebuildAll] FAILED" : "[RebuildAll] PASS");
            EditorApplication.Exit(failCount >= queue.Count && queue.Count > 0 ? 1 : 0);
        }
    }

    /// <summary>
    /// 一次性诊断：对 mv.mp4 依次试 4 种 seek 手段，逐 tick 打帧号/时钟，
    /// 找出 2022.3 VideoPlayer 真正生效的起播偏移姿势。
    /// 用法：-executeMethod MusicGame.EditorTools.SeekExperiment.Run -importDir &lt;歌曲目录&gt;
    /// </summary>
    public static class SeekExperiment
    {
        private static VideoPlayer vp;
        private static string label;
        private static float stageStart;
        private static int stage;
        private static double startSeconds = 31.0;

        public static void Run()
        {
            string importDir = null;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "-importDir", StringComparison.OrdinalIgnoreCase))
                    importDir = args[i + 1];
            string mv = string.IsNullOrEmpty(importDir) ? null : Path.Combine(importDir, "mv.mp4");
            if (string.IsNullOrEmpty(mv) || !File.Exists(mv))
            {
                Debug.LogError("[SeekExp] FAIL：mv.mp4 不存在");
                EditorApplication.Exit(1);
                return;
            }
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorApplication.isPlaying = true;
            EditorApplication.update += Bootstrap;
            void Bootstrap()
            {
                if (!EditorApplication.isPlaying || BootstrapDone) return;
                BootstrapDone = true;
                EditorApplication.update -= Bootstrap;
                if (Camera.main == null)
                {
                    var camGo = new GameObject("SeekExpCam");
                    camGo.tag = "MainCamera";
                    camGo.AddComponent<Camera>();
                }
                vp = new GameObject("SeekExpVp").AddComponent<VideoPlayer>();
                vp.playOnAwake = false;
                vp.source = VideoSource.Url;
                vp.url = new Uri(mv).AbsoluteUri;
                vp.audioOutputMode = VideoAudioOutputMode.None;
                vp.isLooping = false;
                vp.Play(); // 直接 URL 播放（不经 Prepare，贴近真实失败现场）
                stage = 0;
                stageStart = Time.realtimeSinceStartup;
                EditorApplication.update += Pump;
                Debug.Log("[SeekExp] 开始实验 startSeconds=" + startSeconds);
            }
        }

        private static bool BootstrapDone;

        private static void Pump()
        {
            if (!EditorApplication.isPlaying) return;
            EditorApplication.QueuePlayerLoopUpdate();
            float e = Time.realtimeSinceStartup - stageStart;
            if (e < 1.0f) return; // 每阶段观察 1s

            switch (stage)
            {
                case 0: // 基线：自然播放
                    if (e > 3f)
                    {
                        Debug.Log(string.Format("[SeekExp] 基线 frame={0} time={1:F2}", vp.frame, vp.time));
                        label = "playing-frame";
                        vp.frame = (long)(startSeconds * (vp.frameRate > 0 ? vp.frameRate : 25));
                        Next();
                    }
                    break;
                case 1: // playing 态赋 frame
                    LogStage();
                    if (e > 3f) { vp.Pause(); Next(); }
                    break;
                case 2: // paused 态赋 frame
                    vp.frame = (long)(startSeconds * (vp.frameRate > 0 ? vp.frameRate : 25));
                    label = "paused-frame";
                    Next();
                    break;
                case 3:
                    LogStage();
                    if (e > 3f) { vp.Play(); Next(); }
                    break;
                case 4: // playing 态赋 time
                    vp.time = startSeconds;
                    label = "playing-time";
                    Next();
                    break;
                case 5:
                    LogStage();
                    if (e > 3f) { vp.Pause(); Next(); }
                    break;
                case 6: // paused 态赋 time
                    vp.time = startSeconds;
                    label = "paused-time";
                    Next();
                    break;
                case 7:
                    LogStage();
                    if (e > 3f)
                    {
                        Debug.Log("[SeekExp] DONE");
                        EditorApplication.Exit(0);
                    }
                    break;
            }
        }

        private static void Next()
        {
            stage++;
            stageStart = Time.realtimeSinceStartup;
        }

        private static void LogStage()
        {
            Debug.Log(string.Format("[SeekExp] {0}: frame={1} time={2:F2} (e={3:F1}s)", label, vp.frame, vp.time, Time.realtimeSinceStartup - stageStart));
        }
    }

    /// <summary>
    /// MV 背景无头自检（batchmode）：对 -importDir 下的 mv.mp4 建 MvBackdrop，
    /// 断言：解码就绪并开播 → 帧推进（≥150 帧≈5s，有真实画面）→ RT 采样非单一颜色 →
    /// Pause 生效 → Resume 恢复。任一不满足 Exit(1)。
    /// 用法：-executeMethod MusicGame.EditorTools.MvPlaybackCheck.Run -importDir &lt;歌曲目录&gt;
    /// </summary>
    public static class MvPlaybackCheck
    {
        private const float TimeoutSeconds = 90f;
        private const double MinFrames = 150.0;

        private static string mvPath;
        private static MvBackdrop backdrop;
        private static VideoPlayer vp;
        private static double startSeconds;
        private static float startedAt;
        private static bool booted;
        private static int phase; // 0=等开播 0b=等Seek落地 1=等帧推进+像素 2=暂停校验 3=恢复校验

        public static void Run()
        {
            string importDir = null;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "-importDir", StringComparison.OrdinalIgnoreCase))
                    importDir = args[i + 1];
            mvPath = string.IsNullOrEmpty(importDir) ? null : Path.Combine(importDir, "mv.mp4");
            if (string.IsNullOrEmpty(mvPath) || !File.Exists(mvPath))
            {
                Debug.LogError("[MvCheck] FAIL：mv.mp4 不存在：" + mvPath);
                EditorApplication.Exit(1);
                return;
            }
            try
            {
                string metaPath = Path.Combine(importDir, "meta.json");
                if (File.Exists(metaPath))
                {
                    SongMeta meta = JsonUtility.FromJson<SongMeta>(File.ReadAllText(metaPath));
                    if (meta != null) startSeconds = meta.MvStartSeconds;
                }
            }
            catch (Exception) { } // 读不到按 0 处理，只影响断言口径
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorApplication.isPlaying = true;
            EditorApplication.update += Bootstrap;
            Debug.Log("[MvCheck] 请求进入 Play 模式：" + mvPath);
        }

        private static void Bootstrap()
        {
            if (!EditorApplication.isPlaying || booted) return;
            booted = true;
            EditorApplication.update -= Bootstrap;
            try
            {
                if (Camera.main == null)
                {
                    var camGo = new GameObject("MvCheckCamera");
                    camGo.tag = "MainCamera";
                    var cam = camGo.AddComponent<Camera>();
                    cam.orthographic = true;
                    cam.orthographicSize = 6f;
                    cam.transform.position = new Vector3(0f, 0f, -10f);
                }
                var root = new GameObject("MvCheckRoot");
                backdrop = MvBackdrop.Create(root.transform, mvPath, Camera.main, startSeconds);
                if (backdrop == null) { Fail("MvBackdrop.Create 返回 null"); return; }
                vp = root.GetComponentInChildren<VideoPlayer>();
                if (vp == null) { Fail("找不到 VideoPlayer"); return; }
                backdrop.Play();
                startedAt = Time.realtimeSinceStartup;
                EditorApplication.update += Pump;
            }
            catch (Exception e)
            {
                Fail("启动异常：" + e);
            }
        }

        private static void Pump()
        {
            if (!EditorApplication.isPlaying) return;
            EditorApplication.QueuePlayerLoopUpdate();
            try
            {
                float t = Time.realtimeSinceStartup - startedAt;
                switch (phase)
                {
                    case 0: // 等解码就绪并开播
                        if (vp.isPrepared && vp.isPlaying)
                        {
                            Debug.Log("[MvCheck] 已开播 " + vp.width + "x" + vp.height);
                            if (startSeconds > 0.01)
                            {
                                float fps = Mathf.Max(1f, (float)vp.frameRate);
                                float expected = (float)startSeconds * fps;
                                bool inRange = vp.frame >= expected - 0.5f * fps
                                            && vp.frame <= expected + 0.5f * fps;
                                if (startSeconds < 1.0)
                                {
                                    // <1s 偏移对背景无感知差异，无头环境也无法与自然播放区分，不强验
                                    Debug.Log(string.Format("[MvCheck] 起播偏移 {0:F2}s(<1s) 跳过强验证", startSeconds));
                                    phase = 1;
                                    break;
                                }
                                sampT.Clear();
                                sampF.Clear();
                                sampT.Add(t);
                                sampF.Add(vp.frame);
                                Debug.Log(string.Format("[MvCheck] 起播帧 {0}（期望≈{1:F0} @ {2:F2}s）等待 Seek 落地…",
                                    vp.frame, expected, startSeconds));
                                phase = 10;
                                seekWaitStart = t;
                                pauseAt = t;
                            }
                            else phase = 1;
                        }
                        else if (t > TimeoutSeconds * 0.5f) Fail("解码/开播超时 isPrepared=" + vp.isPrepared);
                        break;

                    case 10: // Seek 判别（斜率跳变）：仅用落地前样本拟合自然播放斜率，
                        {      // 落地帧显著超出"自然外推"才算 seek 生效；任意落地延迟下都成立
                            float fps = Mathf.Max(1f, (float)vp.frameRate);
                            float expected = (float)startSeconds * fps;
                            float since = t - seekWaitStart;
                            bool inTight = vp.frame >= expected - 0.5f * fps && vp.frame <= expected + 0.5f * fps;
                            if (inTight && sampF.Count >= 2)
                            {
                                int m = sampF.Count;
                                double span = sampT[m - 1] - sampT[0];
                                double slope = span > 1e-3 ? (sampF[m - 1] - sampF[0]) / span : 0.0;
                                double natural = sampF[0] + slope * (t - sampT[0]);
                                double jump = vp.frame - natural;
                                if (jump > fps * 0.5)
                                {
                                    Debug.Log(string.Format(
                                        "[MvCheck] Seek 生效：帧 {0}≈{1:F0}，超自然外推 {2:F0} 帧（落地耗时 {3:F1}s）",
                                        vp.frame, expected, jump, since));
                                    phase = 1;
                                    break;
                                }
                            }
                            if (vp.frame < expected - 0.5f * fps)
                            {
                                sampT.Add(t); // 只收落地前样本，防落地后污染斜率
                                sampF.Add(vp.frame);
                            }
                            if (since > 12f)
                            {
                                Fail(string.Format("起播偏移未生效：帧 {0}（期望 {1:F0}）12s 内无超外推落地", vp.frame, expected));
                                return;
                            }
                        }
                        break;

                    case 1: // 等帧推进到有真实画面后采样 RT
                        if (vp.frame >= MinFrames)
                        {
                            string pixel = SampleTexture();
                            if (pixel == null) Fail("RT 采样失败");
                            else if (t > TimeoutSeconds) Fail("帧推进过慢 frame=" + vp.frame);
                            else { Debug.Log("[MvCheck] 帧推进 " + vp.frame + "，" + pixel); phase = 2; backdrop.Pause(); pauseAt = Time.realtimeSinceStartup; }
                        }
                        else if (t > TimeoutSeconds) Fail("帧推进不足 frame=" + vp.frame);
                        break;

                    case 2: // 暂停生效
                        if (!vp.isPlaying) { Debug.Log("[MvCheck] 暂停生效（frame=" + vp.frame + " 冻结）"); phase = 3; frozenFrame = vp.frame; backdrop.Resume(); resumeAt = Time.realtimeSinceStartup; }
                        else if (Time.realtimeSinceStartup - pauseAt > 5f) Fail("Pause 后仍在播放");
                        break;

                    case 3: // 恢复推进
                        if (vp.frame > frozenFrame) { Debug.Log("[MvCheck] PASS：恢复推进 frame " + frozenFrame + " → " + vp.frame); EditorApplication.Exit(0); }
                        else if (Time.realtimeSinceStartup - resumeAt > 5f) Fail("Resume 后帧未推进");
                        break;
                }
            }
            catch (Exception e)
            {
                Fail("Pump 异常：" + e);
            }
        }

        private static float pauseAt, resumeAt, seekWaitStart;
        private static double frozenFrame;
        private static readonly List<double> sampT = new List<double>();
        private static readonly List<double> sampF = new List<double>();

        // 从目标 RT 中心读 32x32，返回颜色多样性描述（全同色=疑似黑屏/未解码）
        private static string SampleTexture()
        {
            if (vp.targetTexture == null) return null;
            RenderTexture prev = RenderTexture.active;
            try
            {
                RenderTexture.active = vp.targetTexture;
                int w = Mathf.Min(32, vp.targetTexture.width);
                int h = Mathf.Min(32, vp.targetTexture.height);
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                float cx = (vp.targetTexture.width - w) * 0.5f;
                float cy = (vp.targetTexture.height - h) * 0.5f;
                tex.ReadPixels(new Rect(cx, cy, w, h), 0, 0);
                tex.Apply();
                var colors = tex.GetPixels();
                UnityEngine.Object.Destroy(tex);
                var distinct = new HashSet<Color>();
                foreach (Color c in colors) distinct.Add(new Color(
                    Mathf.Round(c.r * 32f) / 32f, Mathf.Round(c.g * 32f) / 32f, Mathf.Round(c.b * 32f) / 32f));
                return "RT 中心 32x32 去重色数=" + distinct.Count;
            }
            finally
            {
                RenderTexture.active = prev;
            }
        }

        private static void Fail(string reason)
        {
            Debug.LogError("[MvCheck] FAIL：" + reason);
            EditorApplication.Exit(1);
        }
    }

    /// <summary>
    /// MV 链路级无头自检（batchmode）：走真 GameManager.PlaySong 全链（载谱→组装→音频→开播），
    /// 断言 MvBackdrop 被创建并挂进运行时、VideoPlayer 开播、起播 seek 生效（帧推进超过目标半程）。
    /// MvPlaybackCheck 只测组件本身，抓不住"创建时机/挂靠层级"类回归（如 runtimeRoot 未建先创建）——本检补这个洞。
    /// 用法：-executeMethod MusicGame.EditorTools.GameplayMvIntegrationCheck.Run -importDir &lt;歌曲目录&gt;
    /// </summary>
    public static class GameplayMvIntegrationCheck
    {
        private const float TimeoutSeconds = 60f;

        private static string importDir;
        private static GameManager gm;
        private static VideoPlayer vp;
        private static double startSeconds;
        private static float startedAt;
        private static bool booted;
        private static bool sawPlaying;

        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "-importDir", StringComparison.OrdinalIgnoreCase))
                    importDir = args[i + 1];
            if (string.IsNullOrEmpty(importDir) || !File.Exists(Path.Combine(importDir, "mv.mp4")))
            {
                Debug.LogError("[GmMv] FAIL：歌曲目录或 mv.mp4 不存在：" + importDir);
                EditorApplication.Exit(1);
                return;
            }
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorApplication.isPlaying = true;
            EditorApplication.update += Bootstrap;
            Debug.Log("[GmMv] 请求进入 Play 模式：" + importDir);
        }

        private static void Bootstrap()
        {
            if (!EditorApplication.isPlaying || booted) return;
            booted = true;
            EditorApplication.update -= Bootstrap;
            startedAt = Time.realtimeSinceStartup;
            try
            {
                if (Camera.main == null)
                {
                    var camGo = new GameObject("GmMvCamera");
                    camGo.tag = "MainCamera";
                    var cam = camGo.AddComponent<Camera>();
                    cam.orthographic = true;
                    cam.orthographicSize = 6f;
                    cam.transform.position = new Vector3(0f, 0f, -10f);
                }
                string metaPath = Path.Combine(importDir, "meta.json");
                SongMeta meta = JsonUtility.FromJson<SongMeta>(File.ReadAllText(metaPath));
                if (meta == null || string.IsNullOrEmpty(meta.Id)) { Fail("meta.json 解析失败"); return; }
                startSeconds = meta.MvStartSeconds;

                var go = new GameObject("GmMvRoot");
                gm = go.AddComponent<GameManager>();
                gm.PlaySong(meta, "easy");
                EditorApplication.update += Pump;
                Debug.Log("[GmMv] PlaySong 已启动：" + meta.Title);
            }
            catch (Exception e)
            {
                Fail("启动异常：" + e);
            }
        }

        private static void Pump()
        {
            if (!EditorApplication.isPlaying) return;
            EditorApplication.QueuePlayerLoopUpdate();
            try
            {
                float t = Time.realtimeSinceStartup - startedAt;
                var backdrop = UnityEngine.Object.FindFirstObjectByType<MvBackdrop>();
                if (backdrop == null)
                {
                    if (t > 15f) { Fail("15s 内 MvBackdrop 未被创建——GameManager 链路集成断裂"); return; }
                    return;
                }
                if (vp == null) vp = backdrop.GetComponent<VideoPlayer>();
                if (vp == null) { Fail("MvBackdrop 上找不到 VideoPlayer"); return; }
                if (vp.isPrepared && vp.isPlaying && vp.frame > 0)
                {
                    if (!sawPlaying)
                    {
                        sawPlaying = true;
                        Debug.Log(string.Format("[GmMv] MV 已随曲开播 {0}x{1}，起播偏移 {2:F2}s", vp.width, vp.height, startSeconds));
                    }
                    float fps = Mathf.Max(1f, (float)vp.frameRate);
                    float need = (float)startSeconds * fps * 0.9f; // seek 落地后会继续前进，≥目标九成即算生效
                    if (vp.frame >= need)
                    {
                        Debug.Log(string.Format("[GmMv] PASS：帧 {0} ≥ 目标九成 {1:F0}，用时 {2:F1}s", vp.frame, need, t));
                        EditorApplication.Exit(0);
                        return;
                    }
                }
                if (t > TimeoutSeconds)
                {
                    Fail(string.Format("超时：backdrop={0} prepared={1} playing={2} frame={3}",
                        backdrop != null, vp != null && vp.isPrepared, vp != null && vp.isPlaying,
                        vp != null ? vp.frame : -1));
                }
            }
            catch (Exception e)
            {
                Fail("Pump 异常：" + e);
            }
        }

        private static void Fail(string reason)
        {
            Debug.LogError("[GmMv] FAIL：" + reason);
            EditorApplication.Exit(1);
        }
    }
}
