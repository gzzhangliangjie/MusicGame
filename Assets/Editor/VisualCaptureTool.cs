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
        private static bool flowTaiko;
        private static bool flowOsu;
        private static bool flowMuse;
        private static bool flowCountdown;
        private static bool countdownSeen;
        private static int countdownShotAt;
        private static int flowAutoPrefRestore = -1;
        private static bool osuComboReady;
        private static int osuShotAt;

        /// <summary>完全复刻用户路径：Main.unity 进 Play → 自动点 开始游戏→sample→easy → 截图。</summary>
        public static void CaptureFlow()
        {
            flowTaiko = false;
            flowCountdown = false;
            StartFlow();
        }

        /// <summary>开局 3-2-1 倒计时截图验收：四轨流程，播到倒计时中段拍一帧，再等到 GO 日志齐了退出。</summary>
        public static void CaptureCountdownFlow()
        {
            flowTaiko = false;
            flowOsu = false;
            flowCountdown = true;
            countdownSeen = false;
            countdownShotAt = 0;
            StartFlow();
        }

        /// <summary>同上，但先切到太鼓模式。</summary>
        public static void CaptureTaikoFlow()
        {
            flowTaiko = true;
            flowOsu = false;
            flowCountdown = false;
            StartFlow();
        }

        /// <summary>同上，切缩圈模式 + 临时开 AUTO（有命中才有 COMBO 可拍），截图后恢复用户原 AUTO 设置。</summary>
        public static void CaptureOsuFlow()
        {
            flowTaiko = false;
            flowOsu = true;
            flowMuse = false;
            flowCountdown = false;
            flowAutoPrefRestore = PlayerPrefs.GetInt("MusicGame.AutoMode", 0);
            osuComboReady = false;
            osuShotAt = 0;
            StartFlow();
        }

        /// <summary>同上，切跑酷模式 + 临时开 AUTO（地面/空中两轨敌人、角色、跑步带均走真实路径），截图后恢复 AUTO 设置。</summary>
        public static void CaptureMuseFlow()
        {
            flowTaiko = false;
            flowOsu = false;
            flowMuse = true;
            flowCountdown = false;
            flowAutoPrefRestore = PlayerPrefs.GetInt("MusicGame.AutoMode", 0);
            osuComboReady = false;
            osuShotAt = 0;
            StartFlow();
        }

        // ---------------- AUTO 热切换回归验收（倒计时中暂停 -> 开 AUTO -> 背后不偷跑 -> 继续后接管） ----------------

        private static int autoStep;
        private static int autoTicks;
        private static double autoFrozenTime;

        /// <summary>复现用户路径：开局倒计时中暂停 → 暂停菜单开 AUTO → 校验游戏真的被冻结 → 继续后 AUTO 接管。</summary>
        public static void CaptureAutoSwitchFlow()
        {
            flowTaiko = false;
            flowOsu = false;
            flowCountdown = false;
            autoStep = 0;
            autoTicks = 0;
            PlayerPrefs.SetInt("MusicGame.AutoMode", 0); // 先强制手动模式
            PlayerPrefs.Save();
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            Screen.SetResolution(1280, 720, false);
            EditorApplication.isPlaying = true;
            EditorApplication.update += AutoSwitchPump;
            Debug.Log("[VisCap] AUTO 热切换回归验收启动");
        }

        private static void AutoLog(string msg) { Debug.Log("[VisCap][AutoSwitch] " + msg); }

        private static void AutoFail(string msg)
        {
            Debug.LogError("[VisCap][AutoSwitch] 失败：" + msg);
            EditorApplication.Exit(1);
        }

        private static void AutoSwitchPump()
        {
            if (!EditorApplication.isPlaying) return;
            autoTicks++;
            try
            {
                var gf = UnityEngine.Object.FindFirstObjectByType<MusicGame.UI.GameFlow>();
                if (gf == null) return;
                var gm = UnityEngine.Object.FindFirstObjectByType<MusicGame.Gameplay.GameManager>();
                var cond = UnityEngine.Object.FindFirstObjectByType<MusicGame.Core.Conductor>();
                var comboGo = GameObject.Find("combo");
                var comboT = comboGo != null ? comboGo.GetComponent<UnityEngine.UI.Text>() : null;

                switch (autoStep)
                {
                    case 0: // 等主菜单就绪 -> 开始游戏
                        if (GameObject.Find("btnStart") != null)
                        {
                            ClickButton("btnStart");
                            AutoLog("已点开始游戏");
                            autoStep = 1; autoTicks = 0;
                        }
                        else if (autoTicks > 2000) AutoFail("等主菜单超时");
                        break;

                    case 1: // 等选曲列表 -> 点第一项（sample 无声谱，加载快、确定性强）
                        {
                            var item = GameObject.Find("songItem");
                            if (item != null)
                            {
                                item.GetComponent<UnityEngine.UI.Button>().onClick.Invoke();
                                AutoLog("已选曲 sample");
                                autoStep = 2; autoTicks = 0;
                            }
                            else if (autoTicks > 2000) AutoFail("等选曲列表超时");
                        }
                        break;

                    case 2: // 等难度页 -> easy
                        if (GameObject.Find("diff_easy") != null)
                        {
                            ClickButton("diff_easy");
                            AutoLog("已选 easy，等开局倒计时");
                            autoStep = 3; autoTicks = 0;
                        }
                        else if (autoTicks > 2000) AutoFail("等难度页超时");
                        break;

                    case 3: // 等 Conductor 建好并进入倒计时数字阶段（时钟 -3..-1）
                        if (cond != null && cond.SongTimeSeconds > -3.0 && cond.SongTimeSeconds < -1.0)
                        {
                            AutoLog("倒计时中 @songTime=" + cond.SongTimeSeconds.ToString("F2") + "，SetPaused(true)");
                            InvokeSetPaused(gf, true);
                            autoStep = 4; autoTicks = 0;
                        }
                        else if (autoTicks > 4000) AutoFail("等倒计时超时（cond=" + (cond != null) + " time=" + (cond != null ? cond.SongTimeSeconds.ToString("F2") : "-") + "）");
                        break;

                    case 4: // 校验暂停菜单弹出
                        if (GameObject.Find("btnResume") != null)
                        {
                            autoFrozenTime = cond.SongTimeSeconds;
                            AutoLog("暂停菜单已弹出 @songTime=" + autoFrozenTime.ToString("F2"));
                            autoStep = 5; autoTicks = 0;
                        }
                        else if (autoTicks > 300) AutoFail("暂停菜单未弹出");
                        break;

                    case 5: // 停 30 帧确认时钟冻结
                        if (autoTicks == 30)
                        {
                            if (System.Math.Abs(cond.SongTimeSeconds - autoFrozenTime) > 1e-6)
                                AutoFail("暂停菜单打开但时钟仍在走（" + autoFrozenTime.ToString("F2") + " -> " + cond.SongTimeSeconds.ToString("F2") + "）");
                            AutoLog("时钟已冻结");
                            autoStep = 6; autoTicks = 0;
                        }
                        break;

                    case 6: // AUTO 开
                        ClickButton("btnPauseAuto");
                        if (gm == null || !gm.AutoMode) AutoFail("开 AUTO 失败（gm.AutoMode=false）");
                        AutoLog("AUTO 已开（来回切 1/2）");
                        autoStep = 7; autoTicks = 0;
                        break;

                    case 7: // 停 40 帧：AUTO 开着也不得偷跑
                        if (autoTicks == 40)
                        {
                            if (System.Math.Abs(cond.SongTimeSeconds - autoFrozenTime) > 1e-6)
                                AutoFail("开 AUTO 后时钟偷跑");
                            if (comboT != null && !string.IsNullOrEmpty(comboT.text))
                                AutoFail("暂停期间 COMBO 在涨（背后偷打）：combo=" + comboT.text);
                            autoStep = 8; autoTicks = 0;
                        }
                        break;

                    case 8: // AUTO 关（来回切 2/2）
                        ClickButton("btnPauseAuto");
                        if (gm == null || gm.AutoMode) AutoFail("关 AUTO 失败（gm.AutoMode=true）");
                        AutoLog("AUTO 已关（来回切 2/2）");
                        autoStep = 9; autoTicks = 0;
                        break;

                    case 9: // 再开回来，以 AUTO 状态继续
                        ClickButton("btnPauseAuto");
                        if (gm == null || !gm.AutoMode) AutoFail("再开 AUTO 失败");
                        AutoLog("AUTO 再次开启，点继续");
                        ClickButton("btnResume");
                        autoStep = 10; autoTicks = 0;
                        break;

                    case 10: // 等恢复倒计时走完、时钟推进
                        if (cond.SongTimeSeconds > autoFrozenTime + 0.2)
                        {
                            AutoLog("已恢复推进 @songTime=" + cond.SongTimeSeconds.ToString("F2"));
                            autoStep = 11; autoTicks = 0;
                        }
                        else if (autoTicks > 4000) AutoFail("点继续后时钟未推进");
                        break;

                    case 11: // AUTO 应开始打键：COMBO 涨起来即接管成功
                        if (comboT != null && int.TryParse(comboT.text, out int cv) && cv > 0)
                        {
                            AutoLog("AUTO 已接管，combo=" + cv + "，截图存证");
                            ScreenCapture.CaptureScreenshot("E:/MusicGame/vis_autoswitch.png");
                            autoStep = 12; autoTicks = 0;
                        }
                        else if (autoTicks > 20000) AutoFail("恢复后 AUTO 未接管（combo 一直为 0）");
                        break;

                    case 12:
                        if (autoTicks > 40)
                        {
                            PlayerPrefs.SetInt("MusicGame.AutoMode", 0); // 恢复为进场时强制的手动档
                            PlayerPrefs.Save();
                            AutoLog("回归验收通过（含 AUTO 来回切换）");
                            EditorApplication.Exit(0);
                        }
                        break;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError("[VisCap][AutoSwitch] 异常 @step" + autoStep + ": " + e);
                EditorApplication.Exit(1);
            }
        }

        private static void InvokeSetPaused(MusicGame.UI.GameFlow gf, bool paused)
        {
            var m = gf.GetType().GetMethod("SetPaused",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(bool) }, null);
            if (m == null) AutoFail("反射找不到 GameFlow.SetPaused");
            else m.Invoke(gf, new object[] { paused });
        }

        private static int menuTicks;
        /// <summary>主菜单截图验收：Main.unity 进 Play，等入场动效播完截一帧后退出。</summary>
        public static void CaptureMenu()
        {
            menuTicks = 0;
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            Screen.SetResolution(1920, 1080, false);
            EditorApplication.isPlaying = true;
            EditorApplication.update += MenuPump;
            Debug.Log("[VisCap] 主菜单截图验收启动");
        }

        private static void MenuPump()
        {
            if (!EditorApplication.isPlaying) return;
            menuTicks++;
            if (menuTicks == 150)
            {
                ScreenCapture.CaptureScreenshot(CaptureDir + "/vis_menu.png");
                Debug.Log("[VisCap] 主菜单截图已请求（tick=150，入场动效已结束）");
            }
            else if (menuTicks > 180)
            {
                Debug.Log("[VisCap] 主菜单截图完成，退出");
                EditorApplication.Exit(0);
            }
        }

        // ---------------- 主界面回归验收（难度页窄窗布局 / 结算残留） ----------------

        private static int probeStep;
        private static int probeTicks;

        /// <summary>窄窗（832x907，复现用户竖长 Game 视图）截难度选择页，验收纪录面板不被难度按钮遮挡。</summary>
        public static void CaptureDiffPage()
        {
            probeStep = 0; probeTicks = 0;
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            Screen.SetResolution(832, 907, false);
            EditorApplication.isPlaying = true;
            EditorApplication.update += DiffPagePump;
            Debug.Log("[VisCap] 难度页窄窗验收启动");
        }

        private static void DiffPagePump()
        {
            if (!EditorApplication.isPlaying) return;
            probeTicks++;
            try
            {
                switch (probeStep)
                {
                    case 0:
                        if (probeTicks > 60) { ClickButton("btnStart"); AdvanceProbe("主菜单 -> 开始游戏"); }
                        break;
                    case 1:
                        if (probeTicks > 40) { ClickSecondSongItem(); AdvanceProbe("选曲 -> 真实歌曲"); }
                        break;
                    case 2:
                        if (probeTicks > 60)
                        {
                            DumpDiffGeometry();
                            ScreenCapture.CaptureScreenshot(CaptureDir + "/vis_diff_narrow.png");
                            Debug.Log("[VisCap] 难度页窄窗截图已请求");
                            probeStep = 3; probeTicks = 0;
                        }
                        break;
                    case 3:
                        if (probeTicks > 30) { Debug.Log("[VisCap] 难度页验收完成，退出"); EditorApplication.Exit(0); }
                        break;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError("[VisCap] 难度页验收异常 @step" + probeStep + ": " + e);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>结算残留验收：真打一局 → 强制弹结算（NEW RECORD）→ 点“再来一次”→ 校验结算面板已隐藏 + 截图。</summary>
        public static void CaptureRetryResidue()
        {
            probeStep = 0; probeTicks = 0;
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            Screen.SetResolution(1280, 720, false);
            EditorApplication.isPlaying = true;
            EditorApplication.update += RetryPump;
            Debug.Log("[VisCap] 结算残留验收启动");
        }

        private static void RetryPump()
        {
            if (!EditorApplication.isPlaying) return;
            probeTicks++;
            try
            {
                switch (probeStep)
                {
                    case 0:
                        if (probeTicks > 60) { ClickButton("btnStart"); AdvanceProbe("主菜单 -> 开始游戏"); }
                        break;
                    case 1:
                        if (probeTicks > 40) { ClickSecondSongItem(); AdvanceProbe("选曲 -> 真实歌曲"); }
                        break;
                    case 2:
                        if (probeTicks > 40) { ClickButton("diff_easy"); AdvanceProbe("难度 -> easy"); }
                        break;
                    case 3: // 玩 4 秒：中途探测频谱饱和度，之后强制弹结算（带 NEW RECORD 横幅）
                        if (probeTicks == 150 || probeTicks == 230) ProbeSpectrum();
                        if (probeTicks > 240)
                        {
                            var flow = Object.FindFirstObjectByType<GameFlow>();
                            var field = typeof(GameFlow).GetField("results",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            var results = field != null ? field.GetValue(flow) as ResultsScreen : null;
                            if (results == null) { Debug.LogError("[VisCap] 反射取 results 失败"); EditorApplication.Exit(1); return; }
                            results.Show("验收测试曲", "Easy", 96.5, 123, 456789, 130,
                                37, 12, 3, 1, "超越纪录！  得分 1,000 → 456,789", true);
                            AdvanceProbe("强制弹出结算（NEW RECORD）");
                        }
                        break;
                    case 4: // 结算展示中：dump 面板子项几何/颜色，再截一张带判定计数的结算屏
                        if (probeTicks > 30)
                        {
                            var rp = GameObject.Find("ResultsPanel");
                            if (rp != null)
                                foreach (Transform child in rp.transform)
                                {
                                    var crt = (RectTransform)child;
                                    var corners = new Vector3[4];
                                    crt.GetWorldCorners(corners);
                                    var g = child.GetComponent<UnityEngine.UI.Graphic>();
                                    Debug.Log("[VisCap] results." + child.name +
                                              " active=" + child.gameObject.activeInHierarchy +
                                              " c0=" + corners[0] + " c2=" + corners[2] +
                                              " color=" + (g != null ? g.color.ToString() : "n/a") +
                                              " graphicOn=" + (g != null && g.enabled));
                                }
                            ScreenCapture.CaptureScreenshot(CaptureDir + "/vis_results.png");
                            Debug.Log("[VisCap] 结算屏截图已请求（含 Perfect/Good/Bad/Miss 计数）");
                            probeStep = 5; probeTicks = 0;
                        }
                        break;
                    case 5:
                        if (probeTicks > 30) { ClickButton("btnRetry"); AdvanceProbe("结算 -> 再来一次"); }
                        break;
                    case 6: // 下一局进行中：结算面板必须已隐藏（Find 只命中激活对象，找得到即残留）
                        if (probeTicks > 180)
                        {
                            var panel = GameObject.Find("ResultsPanel");
                            if (panel != null)
                            {
                                Debug.LogError("[VisCap] 残留！下一局开始后 ResultsPanel 仍处于激活状态");
                                EditorApplication.Exit(1);
                                return;
                            }
                            ScreenCapture.CaptureScreenshot(CaptureDir + "/vis_retry.png");
                            Debug.Log("[VisCap] 结算无残留，截图已请求");
                            probeStep = 7; probeTicks = 0;
                        }
                        break;
                    case 7:
                        if (probeTicks > 30) { Debug.Log("[VisCap] 结算残留验收完成，退出"); EditorApplication.Exit(0); }
                        break;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError("[VisCap] 结算残留验收异常 @step" + probeStep + ": " + e);
                EditorApplication.Exit(1);
            }
        }

        private static void AdvanceProbe(string label)
        {
            Debug.Log("[VisCap] " + label);
            probeStep++;
            probeTicks = 0;
        }

        /// <summary>频谱饱和度探针：统计顶格（≥95% 最大高度）的光柱数量，验证 AGC 后不再整排全满。</summary>
        private static void ProbeSpectrum()
        {
            int total = 0, pegged = 0;
            float sum = 0f;
            for (int i = 0; i < 28; i++)
            {
                var bar = GameObject.Find("Bar" + i);
                if (bar == null) continue;
                total++;
                float h = bar.transform.localScale.y;
                sum += h;
                if (h >= 2.2f * 0.95f) pegged++;
            }
            Debug.Log(string.Format("[VisCap] 频谱探针: 柱数={0} 顶格={1} 平均高度={2:F2}/{3}",
                total, pegged, total > 0 ? sum / total : 0f, 2.2f));
        }

        /// <summary>难度页几何诊断：关键元素的世界四角（ScreenSpaceOverlay 画布下即屏幕像素，y 向上）。</summary>
        private static void DumpDiffGeometry()
        {
            Debug.Log("[VisCap] Screen=" + Screen.width + "x" + Screen.height);
            var canvas = Object.FindFirstObjectByType<Canvas>();
            if (canvas != null)
            {
                var crt = (RectTransform)canvas.transform;
                Debug.Log("[VisCap] Canvas rect=" + crt.rect.width + "x" + crt.rect.height +
                          " scaleFactor=" + canvas.scaleFactor);
            }
            string[] names = { "title", "song", "modeNormal", "modeTaiko", "diffRecords", "list", "btnBack" };
            foreach (var name in names)
            {
                var go = GameObject.Find(name);
                if (go == null) { Debug.Log("[VisCap] " + name + ": 不存在"); continue; }
                var rt = (RectTransform)go.transform;
                var c = new Vector3[4];
                rt.GetWorldCorners(c);
                Debug.Log("[VisCap] " + name + ": 左下=" + c[0] + " 右上=" + c[2]);
            }
        }

        private static void ClickSecondSongItem()
        {
            var buttons = Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsSortMode.None);
            UnityEngine.UI.Button target = null;
            foreach (var b in buttons)
            {
                if (b.name != "songItem") continue;
                // 跳过无声样例歌（FindObjectsByType 顺序不固定，不能按序号取）
                var label = b.GetComponentInChildren<UnityEngine.UI.Text>();
                if (label != null && label.text.Contains("Silent")) continue;
                target = b;
                break;
            }
            if (target == null) { Debug.LogError("[VisCap] 没有可用的真实歌曲项"); EditorApplication.Exit(1); return; }
            target.onClick.Invoke();
        }

        private static void StartFlow()
        {
            if (flowOsu || flowMuse)
            {
                // 缩圈/跑酷截图需要 Auto 命中把 COMBO 顶起来（结束后在收尾步骤恢复原值）
                PlayerPrefs.SetInt("MusicGame.AutoMode", 1);
                PlayerPrefs.Save();
            }
            if (flowTaiko)
            {
                // 确定性选中太鼓皮肤（btnSkin 是循环切换，皮肤已在 OpenTaiko 时会切走）
                PlayerPrefs.SetString("MusicGame.Skin", "OpenTaiko");
                PlayerPrefs.Save();
            }
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            Screen.SetResolution(1280, 720, false); // 横屏贴近真实桌面玩法（SpawnX=8.2 需要横向空间）
            EditorApplication.isPlaying = true;
            EditorApplication.update += FlowPump;
            Debug.Log("[VisCap] 真实路径复现启动：Main.unity + GameFlow，taiko=" + flowTaiko);
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
                        if (flowTicks > 60)
                        {
                            ClickButton("btnStart");
                            Advance("主菜单 -> 开始游戏");
                        }
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
                    case 2: // 难度选 easy（太鼓/缩圈/跑酷流程先切模式）
                        if (flowTicks > 40)
                        {
                            if (flowTaiko)
                            {
                                ClickButton("modeTaiko");
                                Debug.Log("[VisCap] 切换 -> 太鼓模式");
                            }
                            if (flowOsu)
                            {
                                ClickButton("modeOsu");
                                Debug.Log("[VisCap] 切换 -> 缩圈模式");
                            }
                            if (flowMuse)
                            {
                                ClickButton("modeMuse");
                                Debug.Log("[VisCap] 切换 -> 跑酷模式");
                            }
                            ClickButton("diff_easy");
                            Advance(flowTaiko ? "难度 -> easy（太鼓）"
                                : flowOsu ? "难度 -> easy（缩圈）"
                                : flowMuse ? "难度 -> easy（跑酷）" : "难度 -> easy");
                        }
                        break;
                    case 3: // 等音符入场，弹窗展示 + 诊断 + 截图
                        if (flowCountdown)
                        {
                            // 编辑器窗口失焦可能误触 Esc 弹暂停：检测到就点继续（顺带走一遍恢复倒计时）
                            var resumeBtn = GameObject.Find("btnResume");
                            if (resumeBtn != null && resumeBtn.activeInHierarchy)
                            {
                                Debug.Log("[VisCap] 检测到暂停菜单，点继续（验证恢复倒计时）");
                                resumeBtn.GetComponent<UnityEngine.UI.Button>().onClick.Invoke();
                            }
                            // 等倒计时数字真正出现（大音频文件加载耗时不定，40s 超时兜底），出现后 40 帧拍在数字弹缩中段
                            if (!countdownSeen)
                            {
                                var cdGo = GameObject.Find("countdown");
                                var cdText = cdGo != null ? cdGo.GetComponent<UnityEngine.UI.Text>() : null;
                                if (cdText != null && cdGo.activeInHierarchy && cdText.text.Length > 0)
                                {
                                    countdownSeen = true;
                                    countdownShotAt = flowTicks + 40;
                                    Debug.Log("[VisCap] 倒计时数字已出现：" + cdText.text);
                                }
                                else if (flowTicks > 2400) { countdownSeen = true; countdownShotAt = flowTicks + 1; }
                                else break;
                            }
                            if (flowTicks >= countdownShotAt)
                            {
                                DumpDiagnostics();
                                ScreenCapture.CaptureScreenshot("E:/MusicGame/vis_countdown.png");
                                flowStep = 4; flowTicks = 0;
                                Debug.Log("[VisCap] 倒计时截图已请求");
                            }
                            break;
                        }
                        if (flowOsu || flowMuse)
                        {
                            // 缩圈/跑酷：等 Auto 命中把 COMBO 顶到 ≥5 再拍（不同歌前奏长短不一，超时 30s 兜底）
                            if (!osuComboReady)
                            {
                                if (flowTicks >= 1800) { osuComboReady = true; osuShotAt = flowTicks + 12; }
                                else
                                {
                                    var comboGo = GameObject.Find("combo");
                                    var comboT = comboGo != null ? comboGo.GetComponent<UnityEngine.UI.Text>() : null;
                                    int cv;
                                    if (comboT != null && int.TryParse(comboT.text, out cv) && cv >= 5)
                                    {
                                        osuComboReady = true;
                                        osuShotAt = flowTicks + 60; // 60 帧后拍：数字完全显形、游戏中段
                                    }
                                    else break;
                                }
                            }
                            if (flowTicks == osuShotAt)
                            {
                                var pl = UnityEngine.Object.FindFirstObjectByType<JudgementPopupLayer>();
                                if (pl != null)
                                {
                                    pl.Show(Judgement.Perfect, 0, false);
                                    if (flowMuse) pl.Show(Judgement.Good, 1, false); // 跑酷：地/空两条锚点都验
                                }
                            }
                            if (flowTicks >= osuShotAt + 1)
                            {
                                DumpDiagnostics();
                                ScreenCapture.CaptureScreenshot(flowMuse ? "E:/MusicGame/vis_muse.png" : "E:/MusicGame/vis_osu.png");
                                flowStep = 4; flowTicks = 0;
                                Debug.Log("[VisCap] " + (flowMuse ? "跑酷" : "缩圈") + "路径截图已请求（combo=" +
                                    (GameObject.Find("combo") != null
                                        ? GameObject.Find("combo").GetComponent<UnityEngine.UI.Text>().text
                                        : "?") + "）");
                            }
                            break;
                        }
                        if (flowTicks == 228)
                        {
                            // 判定艺术字展示：截图前 12 帧触发，拍在弹出动画中段
                            var pl2 = UnityEngine.Object.FindFirstObjectByType<JudgementPopupLayer>();
                            if (pl2 != null) pl2.Show(Judgement.Perfect, 0, false);
                            else Debug.LogError("[VisCap] 找不到 JudgementPopupLayer");
                        }
                        if (flowTicks > 240)
                        {
                            DumpDiagnostics();
                            ScreenCapture.CaptureScreenshot("E:/MusicGame/vis_flow.png");
                            flowStep = 4; flowTicks = 0;
                            Debug.Log("[VisCap] 真实路径截图已请求");
                        }
                        break;
                    case 4:
                        if (flowOsu && flowAutoPrefRestore >= 0)
                        {
                            PlayerPrefs.SetInt("MusicGame.AutoMode", flowAutoPrefRestore); // 恢复用户 AUTO 设置
                            PlayerPrefs.Save();
                            flowAutoPrefRestore = -1;
                        }
                        if (flowCountdown)
                        {
                            // 编辑器帧率无上限，帧数等待不可靠：等乐曲时间过 0.6s（GO 已打出、音乐已出声）再退
                            var cond = UnityEngine.Object.FindFirstObjectByType<Conductor>();
                            if (cond != null && cond.SongTimeSeconds > 0.6)
                            {
                                Debug.Log("[VisCap] 倒计时验收完成，退出");
                                EditorApplication.Exit(0);
                            }
                            break;
                        }
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

            // 跑酷模式诊断：角色视口位置 + 存活敌人数（别的模式自然为 null/0）
            var runner = UnityEngine.Object.FindFirstObjectByType<MusicGame.Gameplay.Muse.MuseRunner>();
            Debug.Log("[VisCap] MuseRunner: " + (runner != null
                ? "pos=" + runner.transform.position + " viewport=" + cam.WorldToViewportPoint(runner.transform.position)
                : "null"));
            var enemies = UnityEngine.Object.FindObjectsByType<MusicGame.Gameplay.Muse.MuseNote>(
                UnityEngine.FindObjectsSortMode.None);
            Debug.Log("[VisCap] MuseNote 存活数: " + enemies.Length);
        }
    }
}
