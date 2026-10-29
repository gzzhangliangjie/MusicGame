using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MusicGame.Gameplay;
using MusicGame.Pure.Model;

namespace MusicGame.EditorTools
{
    /// <summary>
    /// 无头冒烟测试（batchmode 下进 Play 模式跑一遍核心玩法链路）：
    /// 载入 sample easy 谱 → GameManager.PlaySong（无声模式）→ 等待谱面自然结束。
    /// 通过条件：判定事件数达到谱面判定总数、时钟推进、OnSongFinished 正常回调且 Completed=true。
    /// 用法：Unity.exe -batchmode -projectPath &lt;工程&gt; -executeMethod MusicGame.EditorTools.HeadlessSmokeTest.Run -logFile &lt;日志&gt;
    /// 注意：游戏对象必须在进入 Play 之后创建（场景重载会销毁编辑期对象）；物理键盘无法无头模拟，DFJK 手感验收仍需真人 Play。
    /// </summary>
    public static class HeadlessSmokeTest
    {
        static GameManager gm;
        static float startedAt;
        static bool booted;
        static bool finished;
        static int judgedTicks;
        static double maxSongTime;
        static GameManager.PlayResult result;
        static bool autoMode; // AutoRun 入口置位：开启 AutoMode，预期全部 Perfect
        static bool taikoMode; // AutoTaikoRun 入口置位：太鼓重映射轨道 + 气球连击条路径
        static bool osuMode; // *OsuRun 入口置位：缩圈模式（点击/自动判定圆圈）
        static bool museMode; // *MuseRun 入口置位：跑酷模式（重映射去重后判定数动态计算）
        static int expectedJudgements = ExpectedJudgements; // 跑酷入口按重映射后谱面重算

        // easy 谱共 44 音符含 3 hold → 判定总数 47；无输入时全部自动 Miss，判定事件应恰好走满
        const int ExpectedJudgements = 47;
        const float TimeoutSeconds = 180f;

        public static void Run()
        {
            autoMode = false;
            taikoMode = false;
            osuMode = false;
            museMode = false;
            StartPlay();
        }

        /// <summary>Auto 观赏冒烟：同谱开启 AutoMode，预期 47 次判定全部 Perfect、零 Miss。</summary>
        public static void AutoRun()
        {
            autoMode = true;
            taikoMode = false;
            osuMode = false;
            museMode = false;
            StartPlay();
        }

        /// <summary>太鼓 + Auto 观赏冒烟：覆盖轨道重映射与气球连击条（hold→lane2）的自动充气尾判。</summary>
        public static void AutoTaikoRun()
        {
            autoMode = true;
            taikoMode = true;
            osuMode = false;
            museMode = false;
            StartPlay();
        }

        /// <summary>缩圈模式冒烟（无输入）：预期全部自动 Miss，判定事件数仍走满（扫尾回收链路）。</summary>
        public static void OsuRun()
        {
            autoMode = false;
            taikoMode = false;
            osuMode = true;
            museMode = false;
            StartPlay();
        }

        /// <summary>缩圈 + Auto 观赏冒烟：圆圈自动命中（含 hold 头/尾两圈），预期全部 Perfect、零 Miss。</summary>
        public static void AutoOsuRun()
        {
            autoMode = true;
            taikoMode = false;
            osuMode = true;
            museMode = false;
            StartPlay();
        }

        /// <summary>跑酷模式冒烟（无输入）：预期全部自动 Miss，判定数按重映射去重后的谱面口径。</summary>
        public static void MuseRun()
        {
            autoMode = false;
            taikoMode = false;
            osuMode = false;
            museMode = true;
            StartPlay();
        }

        /// <summary>跑酷 + Auto 观赏冒烟：地面/空中两轨自动命中（含长条充气尾判），预期全部 Perfect、零 Miss。</summary>
        public static void AutoMuseRun()
        {
            autoMode = true;
            taikoMode = false;
            osuMode = false;
            museMode = true;
            StartPlay();
        }

        static void StartPlay()
        {
            // 关闭进 Play 的域重载：静态状态与 update 订阅得以存活（场景会重载，对象改为进 Play 后创建）
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorApplication.isPlaying = true;
            EditorApplication.update += Bootstrap;
            Debug.Log("[Smoke] 请求进入 Play 模式");
        }

        static void Bootstrap()
        {
            if (!EditorApplication.isPlaying || booted) return;
            booted = true;
            EditorApplication.update -= Bootstrap;
            try
            {
                if (Camera.main == null)
                {
                    var camGo = new GameObject("SmokeCamera");
                    camGo.tag = "MainCamera";
                    var cam = camGo.AddComponent<Camera>();
                    cam.orthographic = true;
                    cam.orthographicSize = 6f;
                    cam.transform.position = new Vector3(0f, 0f, -10f);
                }

                string metaPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Songs", "sample", "meta.json");
                if (!File.Exists(metaPath)) { Fail("找不到 Songs/sample/meta.json"); return; }
                SongMeta meta = JsonUtility.FromJson<SongMeta>(File.ReadAllText(metaPath));
                if (meta == null || string.IsNullOrEmpty(meta.Id)) { Fail("meta.json 解析失败"); return; }

                // 跑酷模式：判定总数按重映射后口径（同刻同轨去重会减少音符）
                if (museMode)
                {
                    string chartPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Songs", "sample", "chart_easy.json");
                    ChartData raw = MusicGame.Core.ChartIO.Load(chartPath);
                    ChartData remapped = GameManager.RemapChartForMuse(raw);
                    int total = 0;
                    foreach (NoteData n in remapped.Notes)
                        total += (n.Type == "hold" && n.Duration > 0.0) ? 2 : 1;
                    expectedJudgements = total;
                    Debug.Log("[Smoke] 跑酷重映射后判定总数 " + expectedJudgements + "（原始 47）");
                }

                var go = new GameObject("SmokeRoot");
                gm = go.AddComponent<GameManager>();
                gm.TaikoMode = taikoMode;
                gm.OsuMode = osuMode;
                gm.MuseMode = museMode;
                gm.AutoMode = autoMode;
                gm.OnSongFinished += r => { finished = true; result = r; };
                gm.OnJudged += (j, lane, isTail) => judgedTicks++;
                gm.PlaySong(meta, "easy");

                startedAt = Time.realtimeSinceStartup;
                EditorApplication.update += Pump;
                Debug.Log("[Smoke] Play 模式冒烟测试启动：sample easy，无声模式，"
                    + (taikoMode ? "太鼓 " : "") + (osuMode ? "缩圈 " : "") + (museMode ? "跑酷 " : "")
                    + (autoMode ? "AUTO 观赏（预期全部 Perfect）" : "无输入（预期全部 Miss）"));
            }
            catch (Exception e)
            {
                Fail("启动异常：" + e);
            }
        }

        static void Pump()
        {
            try
            {
                if (!EditorApplication.isPlaying) return;
                EditorApplication.QueuePlayerLoopUpdate(); // batchmode 下推进播放器循环（协程/时间依赖它）

                float t = Time.realtimeSinceStartup - startedAt;
                if (gm == null) { Fail("GameManager 被销毁"); return; }
                if (gm.Conductor != null && gm.IsPlaying)
                {
                    double song = gm.Conductor.SongTimeSeconds;
                    if (song > maxSongTime) maxSongTime = song;
                }

                if (finished)
                {
                    bool pass = result != null && result.Completed
                        && judgedTicks >= expectedJudgements
                        && maxSongTime > 30.0
                        && (!autoMode || (result.MissCount == 0 && result.PerfectCount >= expectedJudgements));
                    string summary = "[Smoke] 判定事件 " + judgedTicks + "/" + expectedJudgements
                        + "，时钟推进至 " + maxSongTime.ToString("F1") + "s"
                        + "，结算 Completed=" + (result != null && result.Completed)
                        + "，Perfect=" + (result != null ? result.PerfectCount : -1)
                        + "，Miss=" + (result != null ? result.MissCount : -1)
                        + "，用时 " + t.ToString("F0") + "s";
                    if (pass)
                    {
                        Debug.Log(summary);
                        Debug.Log("[Smoke] PASS");
                        EditorApplication.Exit(0);
                    }
                    else
                    {
                        Debug.LogError(summary);
                        Fail("通过条件不满足");
                    }
                    return;
                }

                if (t > TimeoutSeconds) { Fail("超时（" + TimeoutSeconds + "s）未收到结算回调；判定事件 " + judgedTicks + "，时钟 " + maxSongTime.ToString("F1") + "s"); }
            }
            catch (Exception e)
            {
                Fail("Pump 异常：" + e);
            }
        }

        static void Fail(string reason)
        {
            Debug.LogError("[Smoke] FAIL：" + reason);
            EditorApplication.Exit(1);
        }
    }
}
