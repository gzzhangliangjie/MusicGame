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

        // easy 谱共 44 音符含 3 hold → 判定总数 47；无输入时全部自动 Miss，判定事件应恰好走满
        const int ExpectedJudgements = 47;
        const float TimeoutSeconds = 180f;

        public static void Run()
        {
            autoMode = false;
            taikoMode = false;
            StartPlay();
        }

        /// <summary>Auto 观赏冒烟：同谱开启 AutoMode，预期 47 次判定全部 Perfect、零 Miss。</summary>
        public static void AutoRun()
        {
            autoMode = true;
            taikoMode = false;
            StartPlay();
        }

        /// <summary>太鼓 + Auto 观赏冒烟：覆盖轨道重映射与气球连击条（hold→lane2）的自动充气尾判。</summary>
        public static void AutoTaikoRun()
        {
            autoMode = true;
            taikoMode = true;
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

                var go = new GameObject("SmokeRoot");
                gm = go.AddComponent<GameManager>();
                gm.TaikoMode = taikoMode;
                gm.AutoMode = autoMode;
                gm.OnSongFinished += r => { finished = true; result = r; };
                gm.OnJudged += (j, lane, isTail) => judgedTicks++;
                gm.PlaySong(meta, "easy");

                startedAt = Time.realtimeSinceStartup;
                EditorApplication.update += Pump;
                Debug.Log("[Smoke] Play 模式冒烟测试启动：sample easy，无声模式，"
                    + (taikoMode ? "太鼓 " : "")
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
                        && judgedTicks >= ExpectedJudgements
                        && maxSongTime > 30.0
                        && (!autoMode || (result.MissCount == 0 && result.PerfectCount >= ExpectedJudgements));
                    string summary = "[Smoke] 判定事件 " + judgedTicks + "/" + ExpectedJudgements
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
