using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MusicGame.Analysis;
using MusicGame.Core;
using MusicGame.Gameplay;
using MusicGame.Pure.Generator;
using MusicGame.Pure.Model;
using UnityEngine;
using UnityEngine.UI;

namespace MusicGame.UI
{
    /// <summary>
    /// 游戏总流程：主菜单 → 选曲（本地音频复制进 Songs/ 导入 + 已缓存列表）
    /// → 分析进度条（SongAnalyzer.onProgress）→ ChartGenerator 生成并保存四档谱面
    /// → 难度选择 → GameManager.PlaySong → ResultsScreen 结算；游戏中 Esc 暂停/继续。
    /// 全部 uGUI 由代码创建；跨模块只经由共享契约类型与成员。
    /// </summary>
    public class GameFlow : MonoBehaviour
    {
        enum FlowState { MainMenu, SongSelect, Analyzing, DifficultySelect, Playing, Results, Calibrating }

        class SongEntry
        {
            public SongMeta Meta;
            public string Dir;
        }

        // ---- 契约模块引用 ----
        SongAnalyzer analyzer;
        GameManager gameManager;
        GameObject gameplayRoot;
        ScoreSystem scoreSystem; // 契约未暴露访问器，经反射获取实例（成员 getter 均为契约内容）
        Conductor conductor;     // 契约类型，用其 IsPlaying 做结束检测

        FlowState state = FlowState.MainMenu;
        bool isPaused;
        bool importing;
        int lastTotalNotes;
        SongEntry currentEntry;
        string currentDifficulty;
        Coroutine hudRoutineHandle, watchRoutineHandle;

        Canvas canvas;
        readonly List<RectTransform> screens = new List<RectTransform>();
        RectTransform menuScreen, selectScreen, analyzeScreen, diffScreen, hudScreen, pauseScreen;
        ResultsScreen results;
        CalibrationScreen calibration;
        JudgementPopupLayer popupLayer;
        HitErrorBar errorBar;
        Action<Judgement, int, bool, double> judgedHandler;

        // 选曲/分析/难度/HUD 控件
        Transform songListContent;
        InputField pathInput;
        Button browseButton, importButton;
        Text selectStatus, analyzeTitle, progressLabel;
        Image progressFill;
        Transform diffListHolder;
        Text diffSongLabel;
        Text hudScore, hudCombo, hudAcc, hudSong, hudHint, hudLyric;

        // ---------------------------------------------------------------- 基础

        void Awake()
        {
            canvas = UiKit.EnsureCanvas();
            analyzer = FindFirstObjectByType<SongAnalyzer>();
            if (analyzer == null)
            {
                var go = new GameObject("SongAnalyzer");
                analyzer = go.AddComponent<SongAnalyzer>();
            }
            // GameManager 生命周期统一由本类管理（重建专用子物体）
            var existingGm = FindFirstObjectByType<GameManager>();
            if (existingGm != null) Destroy(existingGm);
        }

        void Start()
        {
            BuildScreens();
            CreateResultsScreen();
            ShowMain();
        }

        void OnDestroy() { AudioListener.pause = false; }

        void Update()
        {
            if (!UiKit.EscDown()) return;
            if (state == FlowState.Playing) SetPaused(!isPaused);
            else if (state == FlowState.Calibrating && calibration != null) calibration.RequestExit();
        }

        // ---------------------------------------------------------------- 界面构建

        void BuildScreens()
        {
            menuScreen = CreateScreen("MainMenu"); BuildMainMenu();
            selectScreen = CreateScreen("SongSelect"); BuildSongSelect();
            analyzeScreen = CreateScreen("Analyzing"); BuildAnalyzeScreen();
            diffScreen = CreateScreen("DifficultySelect"); BuildDiffScreen();
            hudScreen = CreateScreen("Hud", new Color(0f, 0f, 0f, 0f)); BuildHud();
            pauseScreen = UiKit.Panel(canvas.transform, "PauseOverlay", new Color(0f, 0f, 0f, 0.78f)).rectTransform;
            BuildPauseOverlay();
            pauseScreen.gameObject.SetActive(false);
        }

        RectTransform CreateScreen(string name, Color? bg = null)
        {
            // bg 仅 HUD 屏传入透底色：ScreenSpaceOverlay 画布盖在世界空间音符之上，不透明背景会遮住整个下落区
            var panel = UiKit.Panel(canvas.transform, name, bg ?? UiKit.Theme.Bg);
            if (bg != null) panel.raycastTarget = false;
            var rt = panel.rectTransform;
            screens.Add(rt);
            return rt;
        }

        void CreateResultsScreen()
        {
            var go = new GameObject("ResultsScreen", typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);
            results = go.AddComponent<ResultsScreen>();
            results.Setup();
            results.RetryRequested += OnResultsRetry;
            results.SongSelectRequested += OnResultsSongSelect;
            results.MenuRequested += ShowMain;
        }

        void BuildMainMenu()
        {
            var title = UiKit.Label(menuScreen, "title", "MUSIC GAME", 88, TextAnchor.MiddleCenter, UiKit.Theme.Accent, true);
            UiKit.Stretch(title.rectTransform, new Vector2(0f, 0.58f), new Vector2(1f, 0.88f));
            var sub = UiKit.Label(menuScreen, "subtitle", "本地导入 · 自动谱面 · 四轨下落 · D F J K", 28, TextAnchor.MiddleCenter, UiKit.Theme.Dim);
            UiKit.Stretch(sub.rectTransform, new Vector2(0f, 0.50f), new Vector2(1f, 0.58f));
            UiKit.ButtonRect(menuScreen, "btnStart", "开 始 游 戏", new Vector2(0f, -40f), new Vector2(480f, 96f), OnStartClicked, UiKit.Theme.Primary, 36);
            UiKit.ButtonRect(menuScreen, "btnCalibrate", "延 迟 校 准", new Vector2(0f, -160f), new Vector2(480f, 96f), OnCalibrateClicked, UiKit.Theme.Secondary, 36);
            UiKit.ButtonRect(menuScreen, "btnQuit", "退 出", new Vector2(0f, -280f), new Vector2(480f, 80f), OnQuitClicked, UiKit.Theme.Quit, 30);
            var help = UiKit.Label(menuScreen, "help", "导入 mp3 / ogg / wav → 自动分析生成四档谱面\nD F J K 或 鼠标点击轨道击打 · 游戏中 Esc 暂停", 22, TextAnchor.MiddleCenter, UiKit.Theme.Dim);
            UiKit.Stretch(help.rectTransform, new Vector2(0f, -0.03f), new Vector2(1f, 0.09f));
        }

        void BuildSongSelect()
        {
            var title = UiKit.Label(selectScreen, "title", "选 曲", 46, TextAnchor.MiddleCenter, Color.white);
            UiKit.Stretch(title.rectTransform, new Vector2(0f, 0.90f), new Vector2(1f, 0.99f));
            browseButton = UiKit.ButtonRect(selectScreen, "btnBrowse", "导入本地音频 (mp3 / ogg / wav)…",
                new Vector2(0f, 402f), new Vector2(640f, 84f), OnBrowseClicked, UiKit.Theme.Primary, 30);
            pathInput = UiKit.InputRect(selectScreen, "pathInput", "或粘贴音频文件完整路径…", new Vector2(-60f, 318f), new Vector2(620f, 52f));
            importButton = UiKit.ButtonRect(selectScreen, "btnImport", "导 入", new Vector2(330f, 318f), new Vector2(180f, 52f), OnImportPathClicked, UiKit.Theme.Secondary, 24);
            selectStatus = UiKit.Label(selectScreen, "status", "", 22, TextAnchor.MiddleCenter, UiKit.Theme.Warn, true);
            UiKit.Stretch(selectStatus.rectTransform, new Vector2(0f, 0.225f), new Vector2(1f, 0.29f));

            // 歌曲滚动列表
            var scrollRt = UiKit.Node(selectScreen, "songScroll");
            scrollRt.anchorMin = new Vector2(0.5f, 0.5f);
            scrollRt.anchorMax = new Vector2(0.5f, 0.5f);
            scrollRt.sizeDelta = new Vector2(940f, 420f);
            scrollRt.anchoredPosition = new Vector2(0f, -20f);
            var bg = scrollRt.gameObject.AddComponent<Image>(); bg.color = UiKit.Theme.PanelBg;
            var scroll = scrollRt.gameObject.AddComponent<ScrollRect>();
            var viewRt = UiKit.Node(scrollRt, "viewport");
            UiKit.Stretch(viewRt);
            viewRt.gameObject.AddComponent<RectMask2D>();
            var contentRt = UiKit.Node(viewRt, "content");
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.anchoredPosition = Vector2.zero;
            var vlg = contentRt.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(12, 12, 12, 12);
            vlg.spacing = 10;
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = false;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;
            var csf = contentRt.gameObject.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = contentRt;
            scroll.viewport = viewRt;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.scrollSensitivity = 40f;
            songListContent = contentRt;

            UiKit.ButtonRect(selectScreen, "btnBack", "返回主菜单", new Vector2(0f, -462f), new Vector2(320f, 64f), ShowMain, UiKit.Theme.PanelBg, 26);
        }

        void BuildAnalyzeScreen()
        {
            analyzeTitle = UiKit.Label(analyzeScreen, "title", "正在分析…", 40, TextAnchor.MiddleCenter, Color.white, true);
            UiKit.Stretch(analyzeTitle.rectTransform, new Vector2(0f, 0.60f), new Vector2(1f, 0.74f));
            var barRt = UiKit.Node(analyzeScreen, "barBg");
            barRt.anchorMin = new Vector2(0.5f, 0.5f);
            barRt.anchorMax = new Vector2(0.5f, 0.5f);
            barRt.sizeDelta = new Vector2(900f, 28f);
            barRt.anchoredPosition = new Vector2(0f, 30f);
            var barBg = barRt.gameObject.AddComponent<Image>(); barBg.color = UiKit.Theme.PanelBg;
            var fillRt = UiKit.Node(barRt, "fill");
            UiKit.Stretch(fillRt, Vector2.zero, Vector2.one, new Vector2(3f, 3f), new Vector2(-3f, -3f));
            progressFill = fillRt.gameObject.AddComponent<Image>();
            progressFill.color = UiKit.Theme.Accent;
            progressFill.type = Image.Type.Filled;
            progressFill.fillMethod = Image.FillMethod.Horizontal;
            progressFill.fillAmount = 0f;
            progressLabel = UiKit.Label(analyzeScreen, "percent", "0%", 34, TextAnchor.MiddleCenter, UiKit.Theme.Accent);
            UiKit.Stretch(progressLabel.rectTransform, new Vector2(0f, 0.44f), new Vector2(1f, 0.54f));
            var hint = UiKit.Label(analyzeScreen, "hint", "首次分析需要一些时间（后台线程），结果缓存到 Songs/ 目录，二次进入秒开", 22, TextAnchor.MiddleCenter, UiKit.Theme.Dim);
            UiKit.Stretch(hint.rectTransform, new Vector2(0f, 0.24f), new Vector2(1f, 0.36f));
        }

        void BuildDiffScreen()
        {
            var title = UiKit.Label(diffScreen, "title", "选择难度", 46, TextAnchor.MiddleCenter, Color.white);
            UiKit.Stretch(title.rectTransform, new Vector2(0f, 0.88f), new Vector2(1f, 0.99f));
            diffSongLabel = UiKit.Label(diffScreen, "song", "", 28, TextAnchor.MiddleCenter, UiKit.Theme.Accent, true);
            UiKit.Stretch(diffSongLabel.rectTransform, new Vector2(0f, 0.80f), new Vector2(1f, 0.88f));
            var holderRt = UiKit.Node(diffScreen, "list");
            UiKit.Stretch(holderRt, new Vector2(0f, 0.06f), new Vector2(1f, 0.76f));
            diffListHolder = holderRt;
            UiKit.ButtonRect(diffScreen, "btnBack", "返回选曲", new Vector2(0f, -466f), new Vector2(320f, 64f), OnDiffBackClicked, UiKit.Theme.PanelBg, 26);
        }

        void BuildHud()
        {
            var img = UiKit.Panel(hudScreen, "hudBg", new Color(0f, 0f, 0f, 0f));
            img.raycastTarget = false;
            hudAcc = UiKit.Label(hudScreen, "acc", "--", 36, TextAnchor.MiddleLeft, UiKit.Theme.Dim);
            UiKit.Stretch(hudAcc.rectTransform, new Vector2(0.01f, 0.88f), new Vector2(0.30f, 0.985f), new Vector2(24f, 0f), new Vector2(0f, 0f));
            hudScore = UiKit.Label(hudScreen, "score", "0", 44, TextAnchor.MiddleRight, Color.white);
            UiKit.Stretch(hudScore.rectTransform, new Vector2(0.70f, 0.88f), new Vector2(0.99f, 0.985f), new Vector2(0f, 0f), new Vector2(-24f, 0f));
            hudCombo = UiKit.Label(hudScreen, "combo", "", 60, TextAnchor.MiddleCenter, UiKit.Theme.Accent);
            UiKit.Stretch(hudCombo.rectTransform, new Vector2(0.30f, 0.86f), new Vector2(0.70f, 0.985f));
            hudSong = UiKit.Label(hudScreen, "song", "", 24, TextAnchor.MiddleLeft, UiKit.Theme.Dim, true);
            UiKit.Stretch(hudSong.rectTransform, new Vector2(0.01f, 0.01f), new Vector2(0.55f, 0.07f), new Vector2(24f, 0f), new Vector2(0f, 0f));
            hudHint = UiKit.Label(hudScreen, "hint", "D F J K / 鼠标点击轨道 · Esc 暂停", 24, TextAnchor.MiddleCenter, UiKit.Theme.Dim);
            UiKit.Stretch(hudHint.rectTransform, new Vector2(0.25f, 0.01f), new Vector2(0.75f, 0.07f));

            // 歌词行：屏幕中部大号显示（Phigros 风格：大白字 + 黑描边 + 投影）
            hudLyric = UiKit.Label(hudScreen, "lyric", "", 52, TextAnchor.MiddleCenter, new Color(1f, 1f, 1f, 0.95f), true);
            UiKit.Stretch(hudLyric.rectTransform, new Vector2(0.05f, 0.46f), new Vector2(0.95f, 0.64f));
            hudLyric.raycastTarget = false;
            var lyricOutline = hudLyric.gameObject.AddComponent<Outline>();
            lyricOutline.effectColor = new Color(0f, 0f, 0f, 0.8f);
            lyricOutline.effectDistance = new Vector2(2.6f, -2.6f);
            var lyricShadow = hudLyric.gameObject.AddComponent<Shadow>();
            lyricShadow.effectColor = new Color(0f, 0f, 0f, 0.55f);
            lyricShadow.effectDistance = new Vector2(0f, -3.5f);

            // 判定反馈层：弹窗 + hit-error 误差条（不挡射线）
            var popupGo = new GameObject("JudgementPopups", typeof(RectTransform));
            popupGo.transform.SetParent(hudScreen, false);
            popupLayer = popupGo.AddComponent<JudgementPopupLayer>();
            popupLayer.Setup(hudScreen);
            var errorGo = new GameObject("HitErrorBar", typeof(RectTransform));
            errorGo.transform.SetParent(hudScreen, false);
            errorBar = errorGo.AddComponent<HitErrorBar>();
            errorBar.Setup(hudScreen);
        }

        void BuildPauseOverlay()
        {
            var title = UiKit.Label(pauseScreen, "title", "暂 停", 64, TextAnchor.MiddleCenter, Color.white);
            UiKit.Stretch(title.rectTransform, new Vector2(0f, 0.55f), new Vector2(1f, 0.82f));
            var note = UiKit.Label(pauseScreen, "note", "（乐曲与判定已同步暂停）", 22, TextAnchor.MiddleCenter, UiKit.Theme.Dim);
            UiKit.Stretch(note.rectTransform, new Vector2(0f, 0.48f), new Vector2(1f, 0.55f));
            UiKit.ButtonRect(pauseScreen, "btnResume", "继 续", new Vector2(0f, 90f), new Vector2(420f, 88f), () => SetPaused(false), UiKit.Theme.Primary, 36);
            UiKit.ButtonRect(pauseScreen, "btnRestart", "重新开始", new Vector2(0f, -40f), new Vector2(420f, 88f), OnPauseRestartClicked, UiKit.Theme.Secondary, 32);
            UiKit.ButtonRect(pauseScreen, "btnQuit", "返回选曲", new Vector2(0f, -170f), new Vector2(420f, 88f), OnPauseQuitClicked, UiKit.Theme.PanelBg, 32);
        }

        // ---------------------------------------------------------------- 流程导航

        void ShowOnly(RectTransform screen)
        {
            for (int i = 0; i < screens.Count; i++)
                screens[i].gameObject.SetActive(screens[i] == screen);
        }

        void ShowMain()
        {
            if (results != null) results.Hide();
            state = FlowState.MainMenu;
            isPaused = false;
            if (pauseScreen != null) pauseScreen.gameObject.SetActive(false);
            AudioListener.pause = false;
            ShowOnly(menuScreen);
        }

        void OnStartClicked()
        {
            state = FlowState.SongSelect;
            SetSelectStatus("");
            RefreshSongList();
            ShowOnly(selectScreen);
        }

        void OnDiffBackClicked()
        {
            state = FlowState.SongSelect;
            SetSelectStatus("");
            RefreshSongList();
            ShowOnly(selectScreen);
        }

        void OnCalibrateClicked()
        {
            state = FlowState.Calibrating;
            ShowOnly(null);
            if (calibration != null) Destroy(calibration.gameObject);
            var go = new GameObject("CalibrationScreen", typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);
            calibration = go.AddComponent<CalibrationScreen>();
            calibration.Setup(OnCalibrationExit);
        }

        void OnCalibrationExit()
        {
            if (calibration != null)
            {
                Destroy(calibration.gameObject);
                calibration = null;
            }
            ShowMain();
        }

        void OnQuitClicked()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ---------------------------------------------------------------- 选曲与导入

        void SetSelectStatus(string msg)
        {
            if (selectStatus != null) selectStatus.text = msg;
        }

        void SetImportEnabled(bool on)
        {
            if (browseButton != null) browseButton.interactable = on;
            if (importButton != null) importButton.interactable = on;
            if (pathInput != null) pathInput.interactable = on;
        }

        void OnBrowseClicked()
        {
            string path = PickAudioFile();
            if (!string.IsNullOrEmpty(path))
            {
                pathInput.text = path;
                StartCoroutine(ImportRoutine(path));
            }
            else
            {
                SetSelectStatus("未选择文件；也可以在输入框粘贴完整路径后点“导入”");
            }
        }

        void OnImportPathClicked()
        {
            if (pathInput != null && !string.IsNullOrEmpty(pathInput.text))
                StartCoroutine(ImportRoutine(pathInput.text));
        }

        IEnumerator ImportRoutine(string rawPath)
        {
            if (importing) yield break;
            string srcPath = (rawPath ?? "").Trim().Trim('"', ' ', '\t');
            if (srcPath.Length == 0) yield break;
            string ext = Path.GetExtension(srcPath).ToLowerInvariant();
            if (ext != ".mp3" && ext != ".ogg" && ext != ".wav")
            {
                SetSelectStatus("仅支持 mp3 / ogg / wav：" + Path.GetFileName(srcPath));
                yield break;
            }
            if (!File.Exists(srcPath))
            {
                SetSelectStatus("文件不存在：" + srcPath);
                yield break;
            }

            importing = true;
            SetImportEnabled(false);
            SetSelectStatus("正在复制音频…");
            string id = "song_" + FileHashHex(srcPath); // 以内容哈希做 songId，重复导入自动去重
            string dir = Path.Combine(SongsRoot, id);
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, "audio" + ext);
            if (!string.Equals(Path.GetFullPath(srcPath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                File.Copy(srcPath, dest, true);

            SetSelectStatus("正在加载音频…");
            AudioClip clip = null;
            string error = null;
            yield return LoadClipRoutine(dest, c => clip = c, e => error = e);
            if (clip == null)
            {
                SetSelectStatus("音频加载失败：" + (error ?? "未知错误"));
                importing = false;
                SetImportEnabled(true);
                yield break;
            }

            var meta = new SongMeta
            {
                Id = id,
                Title = Path.GetFileNameWithoutExtension(srcPath),
                AudioPath = Path.GetFullPath(dest),
                DurationSeconds = clip.length,
                Difficulties = new List<string>()
            };
            var entry = new SongEntry { Meta = meta, Dir = dir };
            SaveMeta(entry);
            importing = false;
            SetImportEnabled(true);
            BeginAnalysis(entry, clip);
        }

        /// <summary>把 AudioImportService 的 Task 包装成协程（避免 async void）。</summary>
        IEnumerator LoadClipRoutine(string path, Action<AudioClip> onLoaded, Action<string> onFailed)
        {
            Task<AudioClip> task = null;
            string error = null;
            try { task = AudioImportService.LoadClip(path); }
            catch (Exception ex) { error = ex.Message; }
            if (task != null)
            {
                while (!task.IsCompleted) yield return null;
                if (task.IsFaulted)
                    error = task.Exception != null ? task.Exception.GetBaseException().Message : "加载失败";
                else if (task.IsCanceled)
                    error = "加载被取消";
                else if (task.Result == null)
                    error = "空音频";
                else
                    onLoaded(task.Result);
            }
            if (error != null && onFailed != null) onFailed(error);
        }

        /// <summary>扫描 Songs/ 与 Assets/Songs/ 下的 meta.json 生成缓存列表。</summary>
        List<SongEntry> ScanSongs()
        {
            var list = new List<SongEntry>();
            var seen = new HashSet<string>();
            string[] roots = { SongsRoot, Path.Combine(Application.dataPath, "Songs") };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                string[] dirs;
                try { dirs = Directory.GetDirectories(root); }
                catch (Exception) { continue; }
                foreach (var dir in dirs)
                {
                    var metaPath = Path.Combine(dir, "meta.json");
                    if (!File.Exists(metaPath)) continue;
                    SongMeta meta = null;
                    try { meta = JsonUtility.FromJson<SongMeta>(File.ReadAllText(metaPath)); }
                    catch (Exception) { }
                    if (meta == null) continue;
                    if (string.IsNullOrEmpty(meta.Id)) meta.Id = new DirectoryInfo(dir).Name;
                    if (!seen.Add(meta.Id)) continue;
                    list.Add(new SongEntry { Meta = meta, Dir = dir });
                }
            }
            return list;
        }

        void RefreshSongList()
        {
            if (songListContent == null) return;
            ClearChildren(songListContent);
            var songs = ScanSongs();
            if (songs.Count == 0)
            {
                var empty = UiKit.Label(songListContent, "empty", "暂无歌曲：点击上方“导入本地音频”，\n或向 Songs/ 目录放入已分析的歌曲", 24, TextAnchor.MiddleCenter, UiKit.Theme.Dim);
                UiKit.Stretch(empty.rectTransform, Vector2.zero, Vector2.one, new Vector2(16f, 60f), new Vector2(-16f, -60f));
                return;
            }
            foreach (var entry in songs)
            {
                var captured = entry;
                var btn = UiKit.ButtonRect(songListContent, "songItem", "", Vector2.zero, new Vector2(900f, 92f),
                    () => OnSongChosen(captured), UiKit.Theme.PanelBg, 24);
                var le = btn.gameObject.AddComponent<LayoutElement>();
                le.minHeight = 92f;
                le.preferredHeight = 92f;
                var title = UiKit.Label(btn.transform, "title", captured.Meta.Title, 30, TextAnchor.MiddleLeft, Color.white, true);
                UiKit.Stretch(title.rectTransform, new Vector2(0f, 0.45f), new Vector2(1f, 1f), new Vector2(24f, 0f), new Vector2(-24f, 0f));
                var sub = UiKit.Label(btn.transform, "sub", SongSubtitle(captured), 20, TextAnchor.MiddleLeft, UiKit.Theme.Dim, true);
                UiKit.Stretch(sub.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0.45f), new Vector2(24f, 2f), new Vector2(-24f, 0f));
            }
        }

        static string SongSubtitle(SongEntry entry)
        {
            var diffs = ProbeDifficulties(entry);
            bool cached = diffs.Count > 0 && File.Exists(Path.Combine(entry.Dir, "analysis.json"));
            string left = diffs.Count > 0
                ? (cached ? "已缓存 · " + diffs.Count + " 个难度" : diffs.Count + " 个难度")
                : "尚未生成谱面";
            string dur = entry.Meta.DurationSeconds > 0.0 ? " · " + FormatDuration(entry.Meta.DurationSeconds) : "";
            bool silent = string.IsNullOrEmpty(entry.Meta.AudioPath);
            return left + dur + (silent ? " · 无声测试" : "");
        }

        static string FormatDuration(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return (int)t.TotalMinutes + ":" + t.Seconds.ToString("D2");
        }

        void OnSongChosen(SongEntry entry)
        {
            currentEntry = entry;
            var diffs = ProbeDifficulties(entry);
            // 有谱面文件即可直接玩（无声谱/他人分享的目录都没有 analysis.json，不该被拒）
            if (diffs.Count > 0)
            {
                ShowDifficultySelect(entry); // 缓存命中：秒进难度选择
                return;
            }
            if (string.IsNullOrEmpty(entry.Meta.AudioPath) || !File.Exists(entry.Meta.AudioPath))
            {
                SetSelectStatus("该歌曲没有缓存谱面，且音频不可用：" + entry.Meta.Title);
                return;
            }
            StartCoroutine(AnalyzeExistingRoutine(entry));
        }

        IEnumerator AnalyzeExistingRoutine(SongEntry entry)
        {
            SetSelectStatus("正在加载音频：" + entry.Meta.Title);
            AudioClip clip = null;
            string error = null;
            yield return LoadClipRoutine(entry.Meta.AudioPath, c => clip = c, e => error = e);
            if (clip == null)
            {
                SetSelectStatus("音频加载失败：" + (error ?? "未知错误"));
                yield break;
            }
            if (entry.Meta.DurationSeconds <= 0.0)
            {
                entry.Meta.DurationSeconds = clip.length;
                SaveMeta(entry);
            }
            BeginAnalysis(entry, clip);
        }

        // ---------------------------------------------------------------- 分析与谱面生成

        void BeginAnalysis(SongEntry entry, AudioClip clip)
        {
            currentEntry = entry;
            state = FlowState.Analyzing;
            analyzeTitle.text = "正在分析：" + entry.Meta.Title;
            progressFill.fillAmount = 0f;
            progressLabel.text = "0%";
            ShowOnly(analyzeScreen);
            analyzer.Analyze(
                clip,
                entry.Meta.Id,
                result => OnAnalysisDone(entry, result),
                p =>
                {
                    if (state != FlowState.Analyzing) return;
                    float v = Mathf.Clamp01(p);
                    progressFill.fillAmount = v;
                    progressLabel.text = Mathf.RoundToInt(v * 100f) + "%";
                });
        }

        void OnAnalysisDone(SongEntry entry, AnalysisResult result)
        {
            if (result == null)
            {
                if (state == FlowState.Analyzing)
                {
                    state = FlowState.SongSelect;
                    SetSelectStatus("分析失败，请重试");
                    RefreshSongList();
                    ShowOnly(selectScreen);
                }
                return;
            }
            try
            {
                var charts = ChartGenerator.Generate(result, entry.Meta);
                if (charts != null)
                {
                    var diffs = new List<string>();
                    string[] order = { "easy", "normal", "hard", "expert" };
                    foreach (var d in order)
                        if (charts.ContainsKey(d))
                        {
                            ChartIO.Save(charts[d], Path.Combine(entry.Dir, "chart_" + d + ".json"));
                            diffs.Add(d);
                        }
                    foreach (var kv in charts)
                        if (!diffs.Contains(kv.Key))
                        {
                            ChartIO.Save(kv.Value, Path.Combine(entry.Dir, "chart_" + kv.Key + ".json"));
                            diffs.Add(kv.Key);
                        }
                    entry.Meta.Difficulties = diffs;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("谱面生成失败: " + ex.Message);
            }
            SaveMeta(entry);
            if (state == FlowState.Analyzing && currentEntry == entry) ShowDifficultySelect(entry);
            else if (state == FlowState.SongSelect) RefreshSongList();
        }

        // ---------------------------------------------------------------- 难度选择

        static readonly string[] DifficultyOrder = { "easy", "normal", "hard", "expert" };

        /// <summary>探测歌曲目录下已有的谱面文件（优先契约键，回退 meta.Difficulties）。</summary>
        static List<string> ProbeDifficulties(SongEntry entry)
        {
            var result = new List<string>();
            try
            {
                foreach (var d in DifficultyOrder)
                    if (File.Exists(Path.Combine(entry.Dir, "chart_" + d + ".json"))) result.Add(d);
                if (DifficultyProfiles.All != null)
                    foreach (var kv in DifficultyProfiles.All)
                        if (!result.Contains(kv.Key) && File.Exists(Path.Combine(entry.Dir, "chart_" + kv.Key + ".json")))
                            result.Add(kv.Key);
            }
            catch (Exception) { }
            if (result.Count == 0 && entry.Meta.Difficulties != null)
                foreach (var d in entry.Meta.Difficulties)
                    if (!string.IsNullOrEmpty(d)) result.Add(d);
            return result;
        }

        static int NoteCountOf(SongEntry entry, string difficulty)
        {
            try
            {
                var chart = ChartIO.Load(Path.Combine(entry.Dir, "chart_" + difficulty + ".json"));
                return chart != null && chart.Notes != null ? chart.Notes.Count : -1;
            }
            catch (Exception) { return -1; }
        }

        static Color DiffColor(string difficulty)
        {
            switch ((difficulty ?? "").ToLowerInvariant())
            {
                case "easy": return new Color(0.28f, 0.72f, 0.40f);
                case "normal": return UiKit.Theme.Primary;
                case "hard": return new Color(0.94f, 0.62f, 0.20f);
                case "expert": return new Color(0.90f, 0.26f, 0.36f);
                default: return UiKit.Theme.Secondary;
            }
        }

        void ShowDifficultySelect(SongEntry entry)
        {
            currentEntry = entry;
            state = FlowState.DifficultySelect;
            diffSongLabel.text = entry.Meta.Title;
            ClearChildren(diffListHolder);
            var diffs = ProbeDifficulties(entry);
            if (diffs.Count == 0)
            {
                var empty = UiKit.Label(diffListHolder, "empty", "该歌曲还没有可用谱面\n请返回选曲重新分析", 26, TextAnchor.MiddleCenter, UiKit.Theme.Dim);
                UiKit.Stretch(empty.rectTransform, Vector2.zero, Vector2.one);
            }
            else
            {
                float y = 250f;
                foreach (var d in diffs)
                {
                    var captured = d;
                    int count = NoteCountOf(entry, captured);
                    string label = UiKit.PrettyDifficulty(captured) + (count >= 0 ? "\n" + count + " notes" : "");
                    UiKit.ButtonRect(diffListHolder, "diff_" + captured, label, new Vector2(0f, y), new Vector2(420f, 118f),
                        () => StartCoroutine(StartGameRoutine(entry, captured)), DiffColor(captured), 32);
                    y -= 148f;
                }
            }
            ShowOnly(diffScreen);
        }

        // ---------------------------------------------------------------- 游戏进行

        IEnumerator StartGameRoutine(SongEntry entry, string diff)
        {
            currentEntry = entry;
            currentDifficulty = diff;
            ForceResumeAudio();
            // 停掉上一局的 HUD/结束监视协程（避免旧监视在重开的一局里误触发结算）
            if (hudRoutineHandle != null) { StopCoroutine(hudRoutineHandle); hudRoutineHandle = null; }
            if (watchRoutineHandle != null) { StopCoroutine(watchRoutineHandle); watchRoutineHandle = null; }
            if (gameplayRoot != null)
            {
                Destroy(gameplayRoot);
                gameplayRoot = null;
            }
            gameManager = null;
            scoreSystem = null;
            conductor = null;

            // 预读谱面：结算总数与结束检测要用
            ChartData chart = null;
            try { chart = ChartIO.Load(Path.Combine(entry.Dir, "chart_" + diff + ".json")); }
            catch (Exception) { }
            double chartEnd = 0.0;
            int total = 0;
            if (chart != null && chart.Notes != null)
            {
                foreach (var n in chart.Notes)
                {
                    bool hold = n.Type == "hold" && n.Duration > 0.0;
                    total += hold ? 2 : 1; // 按判定数计（hold=头+尾两次），供结算 FULL COMBO 判定
                    double endT = n.Time + (hold ? n.Duration : 0.0);
                    if (endT > chartEnd) chartEnd = endT;
                }
            }
            lastTotalNotes = total;

            yield return null; // 等待上一局的旧对象销毁完成

            gameplayRoot = new GameObject("GameplayRoot");
            // 绝不挂到本类所在 Canvas 下：ScreenSpaceOverlay 画布的世界空间是 GUI 像素坐标，
            // 世界对象（音符/判定线/按键板）挂进去会被带出相机视野（诊断实测 viewport=(38,38)）
            gameManager = gameplayRoot.AddComponent<GameManager>();
            try
            {
                gameManager.PlaySong(entry.Meta, diff);
            }
            catch (Exception ex)
            {
                Debug.LogError("PlaySong 失败: " + ex.Message);
                Destroy(gameplayRoot);
                gameplayRoot = null;
                gameManager = null;
                state = FlowState.SongSelect;
                SetSelectStatus("开始失败：" + ex.Message);
                RefreshSongList();
                ShowOnly(selectScreen);
                yield break;
            }

            // 订阅判定反馈：弹窗 + 误差条（先退订旧局，避免重复）
            if (judgedHandler != null && gameManager != null) gameManager.OnJudgedWithDelta -= judgedHandler;
            judgedHandler = (j, lane, isTail, delta) =>
            {
                if (popupLayer != null) popupLayer.Show(j, lane, isTail);
                if (!isTail && errorBar != null) errorBar.AddTick(j, delta);
            };
            gameManager.OnJudgedWithDelta += judgedHandler;

            state = FlowState.Playing;
            isPaused = false;
            pauseScreen.gameObject.SetActive(false);
            hudSong.text = entry.Meta.Title + " · " + UiKit.PrettyDifficulty(diff);
            hudHint.text = string.IsNullOrEmpty(entry.Meta.AudioPath)
                ? "无声测试模式 · D F J K / 鼠标点击 · Esc 暂停"
                : "D F J K / 鼠标点击轨道 · Esc 暂停";
            hudScore.text = "0";
            hudCombo.text = "";
            hudAcc.text = "--";
            ShowOnly(hudScreen);
            hudRoutineHandle = StartCoroutine(HudRoutine());
            watchRoutineHandle = StartCoroutine(SongWatchRoutine(chartEnd, entry.Meta.DurationSeconds));
        }

        /// <summary>每 0.1s 轮询成绩（ScoreSystem 经反射解析）刷新 HUD，并同步当前歌词行。</summary>
        IEnumerator HudRoutine()
        {
            var wait = new WaitForSecondsRealtime(0.1f);
            while (state == FlowState.Playing)
            {
                if (scoreSystem == null) scoreSystem = ResolveScoreSystem(gameManager);
                if (scoreSystem != null)
                {
                    hudScore.text = ((long)scoreSystem.Score).ToString("N0");
                    hudCombo.text = scoreSystem.Combo >= 2 ? scoreSystem.Combo + " combo" : "";
                    hudAcc.text = FormatAccuracy(AccuracyToPercent(scoreSystem.Accuracy));
                }
                if (gameManager != null && gameManager.Lyrics != null && gameManager.Conductor != null)
                {
                    string line = gameManager.Lyrics.TryGetLine(gameManager.Conductor.SongTimeSeconds, out string lyric)
                        ? lyric : "";
                    if (hudLyric.text != line) hudLyric.text = line;
                }
                yield return wait;
            }
            if (hudLyric != null) hudLyric.text = "";
        }

        /// <summary>
        /// 歌曲结束检测：优先契约成员 Conductor.IsPlaying（播放后停止），
        /// 辅以 GameplayRoot 下 AudioSource 播完，最后按谱面时长兜底超时。
        /// </summary>
        IEnumerator SongWatchRoutine(double chartEndSeconds, double audioSeconds)
        {
            double minEnd = Math.Max(chartEndSeconds + 3.0, 8.0);
            double expected = Math.Max(minEnd, audioSeconds > 0.0 ? audioSeconds + 2.0 : 0.0);
            float elapsed = 0f;
            float searchTimer = 0f;
            bool everPlayed = false;
            bool audioStarted = false;
            // 暂停时时钟/音频同步冻结，计时与结束判定一并挂起
            while (true)
            {
                if (isPaused) { yield return null; continue; }
                elapsed += Time.unscaledDeltaTime;
                if (conductor == null && elapsed < 12f)
                {
                    searchTimer += Time.unscaledDeltaTime;
                    if (searchTimer >= 0.25f)
                    {
                        searchTimer = 0f;
                        conductor = FindFirstObjectByType<Conductor>();
                    }
                }
                bool condAlive = conductor != null;
                if (condAlive)
                {
                    bool playing = conductor.IsPlaying;
                    if (playing) everPlayed = true;
                    else if (everPlayed) break; // Conductor 播放后停止 → 结束
                }
                else if (everPlayed && elapsed > minEnd)
                {
                    break; // Conductor 已销毁亦视为结束
                }
                bool anyPlaying = false;
                if (gameplayRoot != null)
                {
                    var sources = gameplayRoot.GetComponentsInChildren<AudioSource>(false);
                    foreach (var s in sources)
                        if (s != null && s.isPlaying) { anyPlaying = true; break; }
                }
                if (anyPlaying) audioStarted = true;
                else if (audioStarted && elapsed > minEnd) break; // 音频播完
                if (elapsed > expected + 8f) break; // 兜底超时
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.8f); // 留给尾判与收尾
            FinishSong();
        }

        void FinishSong()
        {
            if (state != FlowState.Playing) return;
            state = FlowState.Results;
            isPaused = false;
            AudioListener.pause = false;
            pauseScreen.gameObject.SetActive(false);

            double acc = -1.0;
            int maxCombo = 0;
            double score = 0.0;
            var ss = scoreSystem ?? ResolveScoreSystem(gameManager);
            if (ss != null)
            {
                acc = AccuracyToPercent(ss.Accuracy);
                maxCombo = ss.MaxCombo;
                score = ss.Score;
            }
            string title = currentEntry != null ? currentEntry.Meta.Title : "";
            int total = lastTotalNotes;
            StopGameplayObjects();
            ShowOnly(null);
            if (results != null)
                results.Show(title, UiKit.PrettyDifficulty(currentDifficulty), acc, maxCombo, score, total);
        }

        void OnResultsRetry()
        {
            if (currentEntry != null && !string.IsNullOrEmpty(currentDifficulty))
                StartCoroutine(StartGameRoutine(currentEntry, currentDifficulty));
        }

        void OnResultsSongSelect()
        {
            StopAllGameplay();
            state = FlowState.SongSelect;
            SetSelectStatus("");
            RefreshSongList();
            ShowOnly(selectScreen);
        }

        /// <summary>仅销毁玩法对象（不动协程，可在协程内安全调用）。</summary>
        void StopGameplayObjects()
        {
            if (gameplayRoot != null)
            {
                Destroy(gameplayRoot);
                gameplayRoot = null;
            }
            gameManager = null;
            scoreSystem = null;
            conductor = null;
            hudRoutineHandle = null;
            watchRoutineHandle = null;
        }

        /// <summary>彻底停止玩法（含协程；只能在按钮回调等非协程上下文调用）。</summary>
        void StopAllGameplay()
        {
            StopAllCoroutines();
            hudRoutineHandle = null;
            watchRoutineHandle = null;
            isPaused = false;
            AudioListener.pause = false;
            if (pauseScreen != null) pauseScreen.gameObject.SetActive(false);
            StopGameplayObjects();
        }

        // ---------------------------------------------------------------- 暂停（Esc）

        void ForceResumeAudio()
        {
            isPaused = false;
            if (pauseScreen != null) pauseScreen.gameObject.SetActive(false);
            AudioListener.pause = false;
            TryCallPauseApi(gameManager, false);
        }

        void SetPaused(bool paused)
        {
            if (state != FlowState.Playing) return;
            isPaused = paused;
            if (pauseScreen != null) pauseScreen.gameObject.SetActive(paused);
            // GameManager.SetPaused(bool) 会同步冻结 Conductor 时钟与音频，并挂起判定与结束检测
            TryCallPauseApi(gameManager, paused);
        }

        void OnPauseRestartClicked()
        {
            if (currentEntry == null || string.IsNullOrEmpty(currentDifficulty)) return;
            ForceResumeAudio();
            StartCoroutine(StartGameRoutine(currentEntry, currentDifficulty));
        }

        void OnPauseQuitClicked()
        {
            StopAllGameplay();
            state = FlowState.SongSelect;
            SetSelectStatus("");
            RefreshSongList();
            ShowOnly(selectScreen);
        }

        // ---------------------------------------------------------------- 跨模块软绑定（反射）

        /// <summary>
        /// 契约中 GameManager 只声明了 PlaySong，未暴露 ScoreSystem 访问器；
        /// 这里按类型扫描公共字段/属性取得实例（getter 均为契约成员）。找不到返回 null。
        /// </summary>
        static ScoreSystem ResolveScoreSystem(GameManager gm)
        {
            if (gm == null) return null;
            try
            {
                var type = gm.GetType();
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
                foreach (var f in type.GetFields(flags))
                    if (typeof(ScoreSystem).IsAssignableFrom(f.FieldType))
                    {
                        var v = f.GetValue(gm) as ScoreSystem;
                        if (v != null) return v;
                    }
                foreach (var p in type.GetProperties(flags))
                    if (typeof(ScoreSystem).IsAssignableFrom(p.PropertyType) && p.CanRead && p.GetIndexParameters().Length == 0)
                    {
                        var v = p.GetValue(gm, null) as ScoreSystem;
                        if (v != null) return v;
                    }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>尝试调用对端暂停 API（命名启发式；契约未约定）。</summary>
        static void TryCallPauseApi(GameManager gm, bool pause)
        {
            if (gm == null) return;
            try
            {
                var type = gm.GetType();
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
                foreach (var m in type.GetMethods(flags))
                {
                    var ps = m.GetParameters();
                    var name = m.Name.ToLowerInvariant();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(bool) && name.Contains("pause"))
                        m.Invoke(gm, new object[] { pause });
                    else if (ps.Length == 0 && pause && (name == "pause" || name == "pausegame" || name == "pausesong"))
                        m.Invoke(gm, null);
                    else if (ps.Length == 0 && !pause && (name == "resume" || name == "unpause" || name == "resumegame" || name == "continuesong"))
                        m.Invoke(gm, null);
                }
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- 工具

        /// <summary>工作区根的 Songs/ 缓存目录（编辑器下即工程根，与分析器缓存一致）。</summary>
        static string SongsRoot
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Songs")); }
        }

        /// <summary>文件内容 MD5 前 10 位十六进制，作为 songId。</summary>
        static string FileHashHex(string path)
        {
            try
            {
                using (var md5 = MD5.Create())
                using (var stream = File.OpenRead(path))
                {
                    var hash = md5.ComputeHash(stream);
                    var sb = new StringBuilder(hash.Length * 2);
                    foreach (var b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString(0, 10);
                }
            }
            catch (Exception)
            {
                return Path.GetFileNameWithoutExtension(path).GetHashCode().ToString("x8");
            }
        }

        static void SaveMeta(SongEntry entry)
        {
            try { File.WriteAllText(Path.Combine(entry.Dir, "meta.json"), JsonUtility.ToJson(entry.Meta, true)); }
            catch (Exception ex) { Debug.LogWarning("meta.json 写入失败: " + ex.Message); }
        }

        static void ClearChildren(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
                Destroy(parent.GetChild(i).gameObject);
        }

        /// <summary>兼容 0..1 与 0..100 两种准确率口径，统一为百分数。</summary>
        static double AccuracyToPercent(double accuracy)
        {
            if (accuracy < 0.0) return -1.0;
            return accuracy <= 1.0001 ? accuracy * 100.0 : accuracy;
        }

        static string FormatAccuracy(double percent)
        {
            return percent < 0.0 ? "--" : percent.ToString("F2") + "%";
        }

        // ---------------------------------------------------------------- 系统文件对话框（Windows）

        /// <summary>选择音频文件；非 Windows 或失败返回 null（用输入框兜底）。</summary>
        static string PickAudioFile()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            try { return OpenFileDialogWin(); }
            catch (Exception) { return null; }
#else
            return null;
#endif
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OpenFileNameNative
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public IntPtr lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public string lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("Comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetOpenFileNameW(ref OpenFileNameNative ofn);

        static string OpenFileDialogWin()
        {
            var ofn = new OpenFileNameNative();
            ofn.lStructSize = Marshal.SizeOf(typeof(OpenFileNameNative));
            ofn.hwndOwner = IntPtr.Zero;
            ofn.lpstrFilter = "音频文件 (mp3/ogg/wav)\0*.mp3;*.ogg;*.wav\0所有文件\0*.*\0";
            ofn.lpstrFile = new string('\0', 2048);
            ofn.nMaxFile = 2048;
            ofn.lpstrTitle = "选择要导入的音频";
            // EXPLORER | FILEMUSTEXIST | PATHMUSTEXIST | NOCHANGEDIR | HIDEREADONLY
            ofn.Flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008 | 0x00000004;
            if (!GetOpenFileNameW(ref ofn)) return null;
            var s = ofn.lpstrFile;
            int end = s.IndexOf('\0');
            var path = end >= 0 ? s.Substring(0, end) : s;
            return string.IsNullOrEmpty(path) ? null : path;
        }
#endif
    }
}
