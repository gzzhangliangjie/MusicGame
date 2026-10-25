using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MusicGame.Core;
using MusicGame.Gameplay;
using MusicGame.Pure.Model;
using MusicGame.UI;

namespace MusicGame.EditorTools
{
    /// <summary>
    /// 视觉验收工具（非 batchmode，需要渲染）：
    /// Capture      —— 进 Play 跑 sample 无声谱，脚本定时触发打击特效/弹窗/误差条，截两帧 PNG 后自动退出；
    /// OpenForUser  —— 打开 Main.unity 并直接进入 Play（留给用户试玩，不退出）。
    /// </summary>
    public static class VisualCaptureTool
    {
        private const string CaptureDir = "E:/MusicGame";

        // ---------------- 截图验收 ----------------

        private static int step;
        private static int ticks;
        private static GameManager gm;
        private static UnityEngine.UI.Text lyricText;

        public static void Capture()
        {
            // 干净场景：避免加载 Main.unity 时 GameFlow 主菜单盖住演示画面
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            Screen.SetResolution(720, 1280, false);
            EditorApplication.isPlaying = true;
            EditorApplication.update += CaptureBootstrap;
            Debug.Log("[VisCap] 请求进入 Play 模式开始视觉截图验收");
        }

        private static void CaptureBootstrap()
        {
            if (!EditorApplication.isPlaying) return;
            EditorApplication.update -= CaptureBootstrap;

            Camera cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("CaptureCamera");
                camGo.tag = "MainCamera";
                cam = camGo.AddComponent<Camera>();
            }
            cam.orthographic = true;
            cam.orthographicSize = 6f;
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.08f, 1f);

            // HUD 层：弹窗 + 误差条（与 GameFlow 相同的挂法）
            Canvas canvas = UiKit.EnsureCanvas();
            var popupGo = new GameObject("JudgementPopups", typeof(RectTransform));
            popupGo.transform.SetParent(canvas.transform, false);
            var popupLayer = popupGo.AddComponent<JudgementPopupLayer>();
            popupLayer.Setup((RectTransform)canvas.transform);
            var errorGo = new GameObject("HitErrorBar", typeof(RectTransform));
            errorGo.transform.SetParent(canvas.transform, false);
            var errorBar = errorGo.AddComponent<HitErrorBar>();
            errorBar.Setup((RectTransform)canvas.transform);

            // 歌词行（与 GameFlow HUD 同款式）
            lyricText = UiKit.Label(canvas.transform, "CaptureLyric", "", 34, TextAnchor.MiddleCenter, new Color(1f, 1f, 1f, 0.85f), true);
            UiKit.Stretch(lyricText.rectTransform, new Vector2(0.06f, 0.52f), new Vector2(0.94f, 0.62f));
            lyricText.raycastTarget = false;

            // 玩法：sample easy 无声谱（MISS 弹窗/灰色光柱/频谱波浪全部走真实路径）
            string metaPath = System.IO.Path.Combine(
                System.IO.Directory.GetParent(Application.dataPath).FullName, "Songs", "sample", "meta.json");
            SongMeta meta = JsonUtility.FromJson<SongMeta>(System.IO.File.ReadAllText(metaPath));
            gm = new GameObject("CaptureRoot").AddComponent<GameManager>();
            gm.OnJudgedWithDelta += (j, lane, isTail, delta) =>
            {
                if (popupLayer != null) popupLayer.Show(j, lane, isTail);
                if (!isTail && errorBar != null) errorBar.AddTick(j, delta);
            };
            gm.PlaySong(meta, "easy");

            EditorApplication.update += CapturePump;
        }

        private static void CapturePump()
        {
            if (!EditorApplication.isPlaying || gm == null) return;
            ticks++;

            if (lyricText != null && gm.Lyrics != null && gm.Conductor != null)
            {
                string line = gm.Lyrics.TryGetLine(gm.Conductor.SongTimeSeconds, out string lyric) ? lyric : "";
                if (lyricText.text != line) lyricText.text = line;
            }

            if (step == 0 && gm.Conductor != null && gm.Conductor.SongTimeSeconds > 2.6)
            {
                ShowcaseBurst();
                ScreenCapture.CaptureScreenshot(CaptureDir + "/vis_capture_1.png");
                step = 1; ticks = 0;
                Debug.Log("[VisCap] 第 1 帧截图已请求（爆炸+光柱+弹窗+误差条+频谱波浪）");
            }
            else if (step == 1 && ticks > 30 && gm.Conductor != null && gm.Conductor.SongTimeSeconds > 4.5)
            {
                ShowcaseBurst();
                ScreenCapture.CaptureScreenshot(CaptureDir + "/vis_capture_2.png");
                step = 2; ticks = 0;
                Debug.Log("[VisCap] 第 2 帧截图已请求");
            }
            else if (step == 2 && ticks > 30)
            {
                Debug.Log("[VisCap] 截图完成，退出");
                EditorApplication.Exit(0);
            }
        }

        // 交替四轨触发 PERFECT/GREAT 展示（真实特效/弹窗/误差条入口）
        private static void ShowcaseBurst()
        {
            HitEffects fx = Object.FindFirstObjectByType<HitEffects>();
            if (fx == null) return;
            Judgement[] js = { Judgement.Perfect, Judgement.Good, Judgement.Perfect, Judgement.Good };
            for (int lane = 0; lane < 4; lane++)
            {
                fx.Spawn(lane, js[lane], false, false);
            }
        }

        // ---------------- 给用户打开 ----------------

        public static void OpenForUser()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
            EditorApplication.isPlaying = true; // 直接进 Play，落地即主菜单
            Debug.Log("[VisCap] 已打开 Main 场景并进入 Play，交给用户试玩");
        }

        // ---------------- 真实路径复现（Main.unity + GameFlow 全流程） ----------------

        private static int flowStep;
        private static int flowTicks;

        /// <summary>完全复刻用户路径：Main.unity 进 Play → 自动点 开始游戏→sample→easy → 截图。</summary>
        public static void CaptureFlow()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            Screen.SetResolution(720, 1280, false);
            EditorApplication.isPlaying = true;
            EditorApplication.update += FlowPump;
            Debug.Log("[VisCap] 真实路径复现启动：Main.unity + GameFlow");
        }

        private static void FlowPump()
        {
            if (!EditorApplication.isPlaying) return;
            flowTicks++;

            try
            {
                switch (flowStep)
                {
                    case 0: // 主菜单就绪后点"开始游戏"
                        if (flowTicks > 60) { ClickButton("btnStart"); Advance("主菜单 -> 开始游戏"); }
                        break;
                    case 1: // 选曲列表：点第二项（真实歌曲，带音频与分析缓存；第一项 sample 无音频）
                        if (flowTicks > 40)
                        {
                            var buttons = UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Button>(
                                UnityEngine.FindObjectsSortMode.None);
                            UnityEngine.UI.Button target = null;
                            int seen = 0;
                            foreach (var b in buttons)
                            {
                                if (b.name != "songItem") continue;
                                if (seen == 1) { target = b; break; }
                                seen++;
                            }
                            if (target == null) { Debug.LogError("[VisCap] 选曲项不足两首"); EditorApplication.Exit(1); return; }
                            target.onClick.Invoke();
                            Advance("选曲 -> song_73d9b60f15");
                        }
                        break;
                    case 2: // 难度选 easy
                        if (flowTicks > 40) { ClickButton("diff_easy"); Advance("难度 -> easy"); }
                        break;
                    case 3: // 等 4 秒让音符下落，诊断 + 截图
                        if (flowTicks > 240)
                        {
                            DumpDiagnostics();
                            ScreenCapture.CaptureScreenshot("E:/MusicGame/vis_flow.png");
                            flowStep = 4; flowTicks = 0;
                            Debug.Log("[VisCap] 真实路径截图已请求");
                        }
                        break;
                    case 4:
                        if (flowTicks > 30)
                        {
                            Debug.Log("[VisCap] 真实路径复现完成，退出");
                            EditorApplication.Exit(0);
                        }
                        break;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError("[VisCap] 流程异常 @step" + flowStep + ": " + e);
                EditorApplication.Exit(1);
            }
        }

        private static void Advance(string label)
        {
            Debug.Log("[VisCap] " + label);
            flowStep++;
            flowTicks = 0;
        }

        private static void ClickButton(string name)
        {
            var go = GameObject.Find(name);
            if (go == null) { Debug.LogError("[VisCap] 找不到按钮: " + name); EditorApplication.Exit(1); return; }
            var button = go.GetComponent<UnityEngine.UI.Button>();
            if (button == null) { Debug.LogError("[VisCap] " + name + " 无 Button 组件"); EditorApplication.Exit(1); return; }
            button.onClick.Invoke();
        }

        private static void DumpDiagnostics()
        {
            Camera cam = Camera.main;
            Debug.Log("[VisCap] 相机: " + (cam != null
                ? "ortho=" + cam.orthographic + " size=" + cam.orthographicSize + " pos=" + cam.transform.position + " aspect=" + cam.aspect
                : "null"));

            var spawner = UnityEngine.Object.FindFirstObjectByType<NoteSpawner>();
            Debug.Log("[VisCap] NoteSpawner: " + (spawner != null ? "存在, 子物体=" + spawner.transform.childCount : "null"));

            for (int lane = 0; lane < 4; lane++)
            {
                var cap = GameObject.Find("Keycap" + lane);
                var pad = GameObject.Find("KeyPad" + lane);
                string capInfo = cap != null
                    ? "active=" + cap.activeInHierarchy + " visible=" + cap.GetComponent<MeshRenderer>().isVisible
                      + " viewport=" + cam.WorldToViewportPoint(cap.transform.position)
                    : "不存在";
                string padInfo = pad != null
                    ? "active=" + pad.activeInHierarchy + " visible=" + pad.GetComponent<SpriteRenderer>().isVisible
                    : "不存在";
                Debug.Log("[VisCap] lane" + lane + " 键帽[" + capInfo + "] 按键板[" + padInfo + "]");
            }
        }
    }
}
