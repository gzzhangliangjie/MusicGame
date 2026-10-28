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
        bool taikoMode;
        bool autoMode; // AUTO 观赏模式（难度选择页切换，PlayerPrefs 持久化）
        const string AutoModePrefKey = "MusicGame.AutoMode";
        Text autoBtnText;
        Image autoBtnImg;
        Image modeNormalImg, modeTaikoImg;
        Text menuTitle;
        TextVerticalGradient menuTitleGrad;
        Image titleGlow, menuUnderline, startGlow;
        UiBackdrop backdrop;

        // 主菜单动效编排（入场淡入/上滑 + 待机呼吸/辉光脉动）
        const byte FxAlpha = 0, FxSlide = 1, FxScale = 2;
        static readonly Color GhostBtn = new Color(0.17f, 0.20f, 0.29f);
        readonly List<CanvasGroup> menuFxGroups = new List<CanvasGroup>();
        readonly List<RectTransform> menuFxRts = new List<RectTransform>();
        readonly List<Vector2> menuFxTargets = new List<Vector2>();
        readonly List<byte> menuFxKinds = new List<byte>();
        readonly List<float> menuFxDelays = new List<float>();
        Coroutine menuFxHandle, menuIdleHandle;
        float menuFxEndTime;

        // 选曲/分析/难度/HUD 控件
        Transform songListContent;
        InputField pathInput;
        Button browseButton, importButton;
        Text selectStatus, analyzeTitle, progressLabel;
        Image progressFill;
        Transform diffListHolder;
        Text diffSongLabel;
        Text hudScore, hudCombo, hudAcc, hudSong, hudHint, hudLyric;
        LyricKaraokeEffect lyricKaraoke;
        LyricScatterEffect lyricScatter;
        Coroutine lyricAnimHandle;

        // 连击分阶段形态：档位阈值 / 颜色（500+ 为彩虹流彩，颜色运行时算）/ 基准字号倍率
        private static readonly int[] ComboStageAt = { 0, 10, 50, 100, 200, 500 };
        private static readonly Color[] ComboStageColor =
        {
            new Color(0.85f, 0.88f, 0.95f), new Color(0.42f, 0.78f, 1.00f), new Color(0.70f, 0.52f, 1.00f),
            new Color(1.00f, 0.82f, 0.30f), new Color(1.00f, 0.55f, 0.25f), Color.white,
        };
        private static readonly float[] ComboStageScale = { 0.62f, 0.74f, 0.86f, 1.00f, 1.14f, 1.28f };
        private int shownCombo = -1;
        private Coroutine comboVisualHandle;
        private int comboStage;
        private Color comboStageColorRef = Color.white;
        private float comboPunchAmp;
        private float comboRotAmp;
        private float comboTiltDir = 1f;
        private float comboPunchAge = 99f;
        private bool comboBreaking;
        private Text comboCaption;
        private string lyricTarget = "";
        private RectTransform helpScreen;
        private Text diffRecordText;
        private readonly System.Collections.Generic.List<string> shownDifficulties = new System.Collections.Generic.List<string>();

        // ---------------------------------------------------------------- 基础

        void Awake()
        {
            Palette.Restore();
            autoMode = PlayerPrefs.GetInt(AutoModePrefKey, 0) == 1;
            canvas = UiKit.EnsureCanvas();
            var backdropGo = new GameObject("UiBackdrop", typeof(RectTransform));
            backdropGo.transform.SetParent(canvas.transform, false);
            backdrop = backdropGo.AddComponent<UiBackdrop>();
            backdrop.Setup((RectTransform)canvas.transform); // 最底层动态背景
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
            helpScreen = CreateScreen("Help");
            BuildHelp();
        }

        RectTransform CreateScreen(string name, Color? bg = null)
        {
            // bg 仅 HUD 屏传入透底色：ScreenSpaceOverlay 画布盖在世界空间音符之上，不透明背景会遮住整个下落区。
            // 菜单屏用渐变底+半透明（0.90），透出动态背景
            var panel = UiKit.Panel(canvas.transform, name, bg ?? UiKit.Theme.Bg);
            if (bg == null)
            {
                panel.sprite = UiKit.ScreenBgSprite;
                var c = panel.color;
                panel.color = new Color(c.r * 3.2f, c.g * 3.2f, c.b * 3.2f, 0.90f);
            }
            else
            {
                panel.raycastTarget = false;
            }
            var rt = panel.rectTransform;
            screens.Add(rt);
            return rt;
        }

        void CreateResultsScreen()
        {
            var go = new GameObject("ResultsScreen", typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);
            UiKit.Stretch(go.GetComponent<RectTransform>()); // 根节点默认是居中 100x100，不铺满的话面板内所有百分比布局全部塌缩（结算文字/背景一直没渲染出来的根因）
            results = go.AddComponent<ResultsScreen>();
            results.Setup();
            results.RetryRequested += OnResultsRetry;
            results.SongSelectRequested += OnResultsSongSelect;
            results.MenuRequested += ShowMain;
        }

        void BuildMainMenu()
        {
            // 标题背后一团主题色柔光，让大标题从背景里浮起来
            titleGlow = UiKit.SoftGlow(menuScreen, "titleGlow", new Vector2(0f, 259f), 1150f, UiKit.Theme.Backdrop1, 0.14f);

            // 主标题：计分板字体 + 白→主题色竖向渐变 + 双层描边 + 投影（判定艺术字同款语言）
            var title = UiKit.Label(menuScreen, "title", "JJ MUSIC GAME", 104, TextAnchor.MiddleCenter, Color.white);
            title.font = UiKit.DigitFont;
            UiKit.Stretch(title.rectTransform, new Vector2(0f, 0.62f), new Vector2(1f, 0.86f));
            menuTitleGrad = title.gameObject.AddComponent<TextVerticalGradient>();
            menuTitleGrad.SetColors(Color.white, UiKit.Theme.Accent);
            var titleOutline1 = title.gameObject.AddComponent<Outline>();
            titleOutline1.effectColor = new Color(0f, 0f, 0f, 0.85f);
            titleOutline1.effectDistance = new Vector2(3f, -3f);
            var titleOutline2 = title.gameObject.AddComponent<Outline>();
            titleOutline2.effectColor = new Color(0f, 0f, 0f, 0.9f);
            titleOutline2.effectDistance = new Vector2(1.5f, -1.5f);
            var titleShadow = title.gameObject.AddComponent<Shadow>();
            titleShadow.effectColor = new Color(0f, 0f, 0f, 0.55f);
            titleShadow.effectDistance = new Vector2(0f, -5f);
            menuTitle = title;

            // 标题下主题色装饰条
            var underlineRt = UiKit.Node(menuScreen, "underline");
            underlineRt.anchorMin = new Vector2(0.5f, 0.5f);
            underlineRt.anchorMax = new Vector2(0.5f, 0.5f);
            underlineRt.anchoredPosition = new Vector2(0f, 150f);
            underlineRt.sizeDelta = new Vector2(430f, 6f);
            menuUnderline = underlineRt.gameObject.AddComponent<Image>();
            menuUnderline.sprite = UiKit.RoundedSprite;
            menuUnderline.type = Image.Type.Sliced;
            menuUnderline.color = UiKit.Theme.Accent;
            menuUnderline.raycastTarget = false;

            // 主题 / 皮肤：紧贴标题下方并排
            UiKit.ButtonRect(menuScreen, "btnTheme", "主题：" + Palette.Current.Name, new Vector2(-174f, 88f), new Vector2(320f, 56f), OnThemeClicked, UiKit.Theme.PanelBg, 24);
            UiKit.ButtonRect(menuScreen, "btnSkin", "皮肤：" + SkinManager.Current, new Vector2(174f, 88f), new Vector2(320f, 56f), OnSkinClicked, UiKit.Theme.PanelBg, 24);

            var sub = UiKit.Label(menuScreen, "subtitle", "本地导入 · 自动谱面 · 四轨下落 / 太鼓咚咔", 24, TextAnchor.MiddleCenter, UiKit.Theme.Dim);
            UiKit.Stretch(sub.rectTransform, new Vector2(0f, 0.505f), new Vector2(1f, 0.542f));

            // 主按钮列：开始（最大最亮，背后辉光脉动）→ 校准 → 帮助 → 退出
            startGlow = UiKit.SoftGlow(menuScreen, "startGlow", new Vector2(0f, -85f), 820f, UiKit.Theme.Accent, 0.20f);
            UiKit.ButtonRect(menuScreen, "btnStart", "开 始 游 戏", new Vector2(0f, -85f), new Vector2(520f, 104f), OnStartClicked, UiKit.Theme.Primary, 38);
            UiKit.ButtonRect(menuScreen, "btnCalibrate", "延 迟 校 准", new Vector2(0f, -205f), new Vector2(520f, 90f), OnCalibrateClicked, UiKit.Theme.Secondary, 32);
            UiKit.ButtonRect(menuScreen, "btnHelp", "帮 助", new Vector2(0f, -310f), new Vector2(520f, 80f), OnHelpClicked, GhostBtn, 28);
            UiKit.ButtonRect(menuScreen, "btnQuit", "退 出", new Vector2(0f, -402f), new Vector2(520f, 72f), OnQuitClicked, UiKit.Theme.Quit, 26);

            var help = UiKit.Label(menuScreen, "help", "导入 mp3 / ogg / wav 自动生成谱面 · D F J K 或鼠标点击 · 游戏中 Esc 暂停", 20, TextAnchor.MiddleCenter, UiKit.Theme.Dim);
            UiKit.Stretch(help.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0.06f));

            // 入场编排：标题/辉光先到，装饰条、主题/皮肤、标语、四个按钮依次上滑
            AddMenuFx(titleGlow.gameObject, FxAlpha, 0.00f);
            AddMenuFx(title.gameObject, FxScale, 0.00f);
            AddMenuFx(underlineRt.gameObject, FxAlpha, 0.10f);
            AddMenuFx(menuScreen.Find("btnTheme").gameObject, FxSlide, 0.14f);
            AddMenuFx(menuScreen.Find("btnSkin").gameObject, FxSlide, 0.18f);
            AddMenuFx(sub.gameObject, FxSlide, 0.24f);
            AddMenuFx(startGlow.gameObject, FxAlpha, 0.30f);
            AddMenuFx(menuScreen.Find("btnStart").gameObject, FxSlide, 0.30f);
            AddMenuFx(menuScreen.Find("btnCalibrate").gameObject, FxSlide, 0.37f);
            AddMenuFx(menuScreen.Find("btnHelp").gameObject, FxSlide, 0.44f);
            AddMenuFx(menuScreen.Find("btnQuit").gameObject, FxSlide, 0.51f);
            AddMenuFx(help.gameObject, FxAlpha, 0.60f);
        }

        /// <summary>重启主菜单入场+待机动效（重复调用安全：先停旧协程，入场首帧重置初态）。</summary>
        void RestartMenuFx()
        {
            if (menuFxGroups.Count == 0) return;
            if (menuFxHandle != null) StopCoroutine(menuFxHandle);
            if (menuIdleHandle != null) StopCoroutine(menuIdleHandle);
            menuFxHandle = StartCoroutine(MenuEnterFx());
            menuIdleHandle = StartCoroutine(MenuIdleFx());
        }

        void AddMenuFx(GameObject go, byte kind, float delay)
        {
            var cg = go.GetComponent<CanvasGroup>();
            if (cg == null) cg = go.AddComponent<CanvasGroup>(); // Unity 假 null 不能用 ?? 判空
            menuFxGroups.Add(cg);
            var rt = (RectTransform)go.transform;
            menuFxRts.Add(rt);
            menuFxTargets.Add(rt.anchoredPosition);
            menuFxKinds.Add(kind);
            menuFxDelays.Add(delay);
            cg.blocksRaycasts = true;
        }

        /// <summary>主菜单入场：淡入 + 按钮自下方上滑 + 标题 OutBack 弹出，元素错峰；期间挡射线避免误点透明按钮。</summary>
        IEnumerator MenuEnterFx()
        {
            for (int i = 0; i < menuFxGroups.Count; i++)
            {
                var cg = menuFxGroups[i];
                cg.alpha = 0f;
                cg.blocksRaycasts = false;
                if (menuFxKinds[i] == FxSlide) menuFxRts[i].anchoredPosition = menuFxTargets[i] + new Vector2(0f, -34f);
                else if (menuFxKinds[i] == FxScale) menuFxRts[i].localScale = Vector3.one * 0.92f;
            }
            float start = Time.unscaledTime;
            float total = 0f;
            for (int i = 0; i < menuFxDelays.Count; i++) total = Mathf.Max(total, menuFxDelays[i]);
            total += 0.34f;
            menuFxEndTime = start + total;

            while (Time.unscaledTime - start < total)
            {
                float now = Time.unscaledTime - start;
                for (int i = 0; i < menuFxGroups.Count; i++)
                {
                    float k = Mathf.Clamp01((now - menuFxDelays[i]) / 0.32f);
                    if (k <= 0f) continue;
                    float e = OutCubic(k);
                    menuFxGroups[i].alpha = e;
                    if (menuFxKinds[i] == FxSlide)
                        menuFxRts[i].anchoredPosition = menuFxTargets[i] + new Vector2(0f, -34f * (1f - e));
                    else if (menuFxKinds[i] == FxScale)
                    {
                        float s = Mathf.Lerp(0.92f, 1f, OutBack(k));
                        menuFxRts[i].localScale = new Vector3(s, s, 1f);
                    }
                    if (k >= 1f) menuFxGroups[i].blocksRaycasts = true;
                }
                yield return null;
            }
            for (int i = 0; i < menuFxGroups.Count; i++)
            {
                menuFxGroups[i].alpha = 1f;
                menuFxGroups[i].blocksRaycasts = true;
                if (menuFxKinds[i] == FxSlide) menuFxRts[i].anchoredPosition = menuFxTargets[i];
                else if (menuFxKinds[i] == FxScale) menuFxRts[i].localScale = Vector3.one;
            }
        }

        /// <summary>主菜单待机：标题缓慢呼吸，开始按钮辉光脉动；入场结束后才接管，避免打架。</summary>
        IEnumerator MenuIdleFx()
        {
            while (state == FlowState.MainMenu)
            {
                float t = Time.unscaledTime;
                if (t < menuFxEndTime) { yield return null; continue; }
                if (menuTitle != null)
                {
                    float s = 1f + Mathf.Sin(t * 1.4f) * 0.006f;
                    menuTitle.rectTransform.localScale = new Vector3(s, s, 1f);
                }
                if (startGlow != null)
                {
                    var c = UiKit.Theme.Accent;
                    float a = 0.14f + 0.10f * (0.5f + 0.5f * Mathf.Sin(t * 2.1f));
                    startGlow.color = new Color(c.r, c.g, c.b, a);
                }
                yield return null;
            }
        }

        void OnThemeClicked()
        {
            string name = Palette.Cycle();
            var btn = menuScreen.Find("btnTheme")?.GetComponentInChildren<Text>();
            if (btn != null) btn.text = "主题：" + name;
            // 标题渐变 / 装饰条 / 标题辉光跟随新主题重染
            if (menuTitleGrad != null) menuTitleGrad.SetColors(Color.white, UiKit.Theme.Accent);
            if (menuUnderline != null) menuUnderline.color = UiKit.Theme.Accent;
            if (titleGlow != null)
            {
                var c = UiKit.Theme.Backdrop1;
                titleGlow.color = new Color(c.r, c.g, c.b, titleGlow.color.a);
            }
            if (backdrop != null) backdrop.ApplyPalette();
        }

        void OnSkinClicked()
        {
            string name = SkinManager.Cycle();
            var btn = menuScreen.Find("btnSkin")?.GetComponentInChildren<Text>();
            if (btn != null) btn.text = "皮肤：" + name; // 新皮肤下一局生效
        }

        void OnHelpClicked()
        {
            ShowOnly(helpScreen);
        }

        void BuildHelp()
        {
            var title = UiKit.Label(helpScreen, "title", "帮 助", 46, TextAnchor.MiddleCenter, Color.white);
            title.font = UiKit.HeavyFont;
            UiKit.Stretch(title.rectTransform, new Vector2(0f, 0.88f), new Vector2(1f, 0.97f));
            var body = UiKit.Label(helpScreen, "body",
                "【玩法】\n" +
                "四轨模式：音符落到判定线时按对应键 D F J K，或鼠标点击轨道；长条按住直到尾端\n" +
                "太鼓模式：F/J=咚(红)  D/K=咔(蓝)，也可按住鼠标左右半屏；气球连击条滚到鼓面后按住任意鼓充气，吹满爆掉得分\n" +
                "AUTO 观赏：难度选择页可开启，自动完美演奏整谱（成绩不计入纪录），随时 Esc 暂停\n\n" +
                "【判定】\n" +
                "Perfect ±40ms · Good ±80ms · Bad ±140ms · 超出为 Miss（只有 Miss 断连击）\n" +
                "气球按充气比例判尾：≥90% Perfect / ≥60% Good / ≥30% Bad\n\n" +
                "【记录】\n" +
                "每首歌 x 模式 x 难度自动保存本地最佳（得分/连击/准确率），难度选择页右侧可查看；\n" +
                "超越历史纪录时结算界面会有 NEW RECORD 庆祝\n\n" +
                "【个性化】\n" +
                "主菜单可切换主题配色与皮肤（Skins 文件夹放贴图即成皮肤，兼容 osu! 皮肤文件名）；\n" +
                "感觉打击时机不准？主菜单「延迟校准」",
                26, TextAnchor.UpperLeft, new Color(0.92f, 0.94f, 1f), true);
            UiKit.Stretch(body.rectTransform, new Vector2(0.08f, 0.13f), new Vector2(0.92f, 0.86f), new Vector2(20f, 0f), new Vector2(-20f, 0f));
            UiKit.ButtonRect(helpScreen, "btnHelpBack", "返 回", new Vector2(0f, -466f), new Vector2(320f, 72f), ShowMain, UiKit.Theme.PanelBg, 30);
        }

        void BuildSongSelect()
        {
            var title = UiKit.Label(selectScreen, "title", "选 曲", 46, TextAnchor.MiddleCenter, Color.white);
            title.font = UiKit.HeavyFont;
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
            // 顶部三行（标题/模式行/歌名）全部按画布高度百分比定位：
            // 固定像素与百分比混用在非 16:9 窗口会相对漂移相撞（歌名曾被模式按钮盖住）
            var title = UiKit.Label(diffScreen, "title", "选择难度", 46, TextAnchor.MiddleCenter, Color.white);
            title.font = UiKit.HeavyFont;
            UiKit.Stretch(title.rectTransform, new Vector2(0f, 0.90f), new Vector2(1f, 0.995f));
            diffSongLabel = UiKit.Label(diffScreen, "song", "", 28, TextAnchor.MiddleCenter, UiKit.Theme.Accent, true);
            UiKit.Stretch(diffSongLabel.rectTransform, new Vector2(0f, 0.735f), new Vector2(1f, 0.80f));

            // 模式切换：通常（四轨下落）/ 太鼓（咚咔横卷）——锚点钉在画布高度百分比，
            // 构建期量画布尺寸不可靠（CanvasScaler 的 scaleFactor 迟到，Start 时量到的是失真值）
            var normalBtn = UiKit.ButtonRect(diffScreen, "modeNormal", "通 常 模 式", new Vector2(-250f, 0f), new Vector2(430f, 88f), () => SetTaikoMode(false), UiKit.Theme.Primary, 28);
            var taikoBtn = UiKit.ButtonRect(diffScreen, "modeTaiko", "太 鼓 模 式", new Vector2(250f, 0f), new Vector2(430f, 88f), () => SetTaikoMode(true), UiKit.Theme.Secondary, 28);
            PinFractionY((RectTransform)normalBtn.transform, -250f, 0.845f);
            PinFractionY((RectTransform)taikoBtn.transform, 250f, 0.845f);
            modeNormalImg = normalBtn.image;
            modeTaikoImg = taikoBtn.image;

            // AUTO 观赏开关：模式行下方居中（0.757 高度分数钉位，落在模式行与难度首钮之间的空档）
            var autoBtn = UiKit.ButtonRect(diffScreen, "btnAuto", "AUTO 观赏：关",
                new Vector2(0f, 0f), new Vector2(380f, 58f), OnAutoToggleClicked, GhostBtn, 22);
            PinFractionY((RectTransform)autoBtn.transform, 0f, 0.757f);
            autoBtnImg = autoBtn.image;
            autoBtnText = autoBtn.GetComponentInChildren<Text>();
            RefreshAutoButton();

            // 难度列容器全屏拉伸（无底图），子按钮用画布高度分数钉位
            var holderRt = UiKit.Node(diffScreen, "list");
            UiKit.Stretch(holderRt);
            diffListHolder = holderRt;
            UiKit.ButtonRect(diffScreen, "btnBack", "返回选曲", new Vector2(0f, -466f), new Vector2(320f, 64f), OnDiffBackClicked, UiKit.Theme.PanelBg, 26);
            // 右侧本地最佳纪录面板（随模式/歌曲刷新）
            diffRecordText = UiKit.Label(diffScreen, "diffRecords", "", 24, TextAnchor.UpperLeft, UiKit.Theme.Dim, true);
            UiKit.Stretch(diffRecordText.rectTransform, new Vector2(0.60f, 0.12f), new Vector2(0.97f, 0.74f), new Vector2(18f, 0f), new Vector2(-12f, 0f));
            SetTaikoMode(false);
        }

        /// <summary>切换游玩模式并刷新两个切换按钮的高亮。</summary>
        void SetTaikoMode(bool taiko)
        {
            taikoMode = taiko;
            if (modeNormalImg != null) modeNormalImg.color = taiko ? new Color(0.25f, 0.28f, 0.36f) : UiKit.Theme.Primary;
            if (modeTaikoImg != null) modeTaikoImg.color = taiko ? new Color(0.95f, 0.45f, 0.25f) : new Color(0.25f, 0.28f, 0.36f);
            UpdateDiffRecordPanel();
        }

        /// <summary>切换 AUTO 观赏模式并刷新开关按钮（持久化，下一局生效）。</summary>
        void OnAutoToggleClicked()
        {
            autoMode = !autoMode;
            PlayerPrefs.SetInt(AutoModePrefKey, autoMode ? 1 : 0);
            PlayerPrefs.Save();
            RefreshAutoButton();
        }

        void RefreshAutoButton()
        {
            if (autoBtnText != null) autoBtnText.text = autoMode ? "AUTO 观赏：开" : "AUTO 观赏：关";
            if (autoBtnImg != null) autoBtnImg.color = autoMode ? UiKit.Theme.Accent : GhostBtn;
        }

        void BuildHud()
        {
            var img = UiKit.Panel(hudScreen, "hudBg", new Color(0f, 0f, 0f, 0f));
            img.raycastTarget = false;
            hudAcc = UiKit.Label(hudScreen, "acc", "--", 36, TextAnchor.MiddleLeft, UiKit.Theme.Dim);
            UiKit.Stretch(hudAcc.rectTransform, new Vector2(0.01f, 0.88f), new Vector2(0.30f, 0.985f), new Vector2(24f, 0f), new Vector2(0f, 0f));
            hudScore = UiKit.Label(hudScreen, "score", "0", 44, TextAnchor.MiddleRight, Color.white);
            UiKit.Stretch(hudScore.rectTransform, new Vector2(0.70f, 0.88f), new Vector2(0.99f, 0.985f), new Vector2(0f, 0f), new Vector2(-24f, 0f));
            // 连击：大数字（Impact 计分板字体）+ 上方小字 COMBO；阶段形态由 ComboVisualLoop 逐帧驱动
            hudCombo = UiKit.Label(hudScreen, "combo", "", 88, TextAnchor.MiddleCenter, Color.white);
            hudCombo.font = UiKit.DigitFont;
            UiKit.Stretch(hudCombo.rectTransform, new Vector2(0.30f, 0.86f), new Vector2(0.70f, 0.955f));
            hudCombo.gameObject.AddComponent<CanvasGroup>();
            comboCaption = UiKit.Label(hudScreen, "comboCaption", "C O M B O", 18, TextAnchor.MiddleCenter, new Color(1f, 1f, 1f, 0.75f));
            UiKit.Stretch(comboCaption.rectTransform, new Vector2(0.38f, 0.955f), new Vector2(0.62f, 0.985f));
            comboCaption.raycastTarget = false;
            hudSong = UiKit.Label(hudScreen, "song", "", 24, TextAnchor.MiddleLeft, UiKit.Theme.Dim, true);
            UiKit.Stretch(hudSong.rectTransform, new Vector2(0.01f, 0.01f), new Vector2(0.55f, 0.07f), new Vector2(24f, 0f), new Vector2(0f, 0f));
            hudHint = UiKit.Label(hudScreen, "hint", "D F J K / 鼠标点击轨道 · Esc 暂停", 24, TextAnchor.MiddleCenter, UiKit.Theme.Dim);
            UiKit.Stretch(hudHint.rectTransform, new Vector2(0.25f, 0.01f), new Vector2(0.75f, 0.07f));

            // 歌词行：屏幕上部大号显示——思源黑体 Heavy（字重已够，不再叠合成加粗以免发糊）+ 白→金竖向渐变 + 双层描边 + 投影
            hudLyric = UiKit.Label(hudScreen, "lyric", "", 56, TextAnchor.MiddleCenter, Color.white, true);
            hudLyric.font = UiKit.HeavyFont;
            UiKit.Stretch(hudLyric.rectTransform, new Vector2(0.05f, 0.74f), new Vector2(0.95f, 0.86f));
            hudLyric.raycastTarget = false;
            lyricKaraoke = hudLyric.gameObject.AddComponent<LyricKaraokeEffect>(); // 须最先执行：后续修饰会展开/复制顶点流
            hudLyric.gameObject.AddComponent<TextVerticalGradient>();
            var lyricOutline1 = hudLyric.gameObject.AddComponent<Outline>();
            lyricOutline1.effectColor = new Color(0f, 0f, 0f, 0.85f);
            lyricOutline1.effectDistance = new Vector2(3.2f, -3.2f);
            var lyricOutline2 = hudLyric.gameObject.AddComponent<Outline>();
            lyricOutline2.effectColor = new Color(0f, 0f, 0f, 0.9f);
            lyricOutline2.effectDistance = new Vector2(1.6f, -1.6f);
            // 消散效果：必须挂在 Shadow 之前（投影跟随消散后的字形一起走），Karaoke 之后字符分组才对
            lyricScatter = hudLyric.gameObject.AddComponent<LyricScatterEffect>();
            var lyricShadow = hudLyric.gameObject.AddComponent<Shadow>();
            lyricShadow.effectColor = new Color(0f, 0f, 0f, 0.6f);
            lyricShadow.effectDistance = new Vector2(0f, -4.5f);

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
            title.font = UiKit.HeavyFont;
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
            RestartMenuFx();
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
            UiKit.Stretch(go.GetComponent<RectTransform>()); // 同结算屏：根节点不铺满会导致百分比布局全部塌缩
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
            bool hasMv = File.Exists(Path.Combine(entry.Dir, "mv.mp4"));
            bool silent = string.IsNullOrEmpty(entry.Meta.AudioPath);
            return left + dur + (hasMv ? " · MV" : "") + (silent ? " · 无声测试" : "");
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

        /// <summary>难度列横位置：画布窄于 1920（非 16:9 游戏视图）时按比例左移（上限 360），给右侧纪录面板让位。</summary>
        static float DifficultyListX(float canvasWidth)
        {
            return canvasWidth < 1920f ? -Mathf.Min(360f, (1920f - canvasWidth) * 0.5f) : 0f;
        }

        /// <summary>把控件钉在父容器高度的分数处（锚点定位，任何窗口比例自适应）。
        /// 教训：构建期量 canvas.rect 是失真的——Start 时 CanvasScaler 的 scaleFactor 可能尚未生效。</summary>
        static void PinFractionY(RectTransform rt, float x, float yFraction)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, yFraction);
            rt.anchoredPosition = new Vector2(x, 0f);
        }

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
                float cw = ((RectTransform)canvas.transform).rect.width;
                float listX = DifficultyListX(cw);
                int idx = 0;
                foreach (var d in diffs)
                {
                    var captured = d;
                    int count = NoteCountOf(entry, captured);
                    string label = UiKit.PrettyDifficulty(captured) + (count >= 0 ? "\n" + count + " notes" : "");
                    var btn = UiKit.ButtonRect(diffListHolder, "diff_" + captured, label, Vector2.zero, new Vector2(420f, 118f),
                        () => StartCoroutine(StartGameRoutine(entry, captured)), DiffColor(captured), 32);
                    PinFractionY((RectTransform)btn.transform, listX, 0.655f - idx * 0.135f); // 高度分数钉位：间距随窗口等比缩放
                    idx++;
                }
                // 纪录面板左缘动态贴住难度列右缘 +32px：窄窗口（非 16:9 视图）下按钮不再盖住纪录文字
                if (diffRecordText != null)
                {
                    var rr = diffRecordText.rectTransform;
                    float minX = Mathf.Clamp01((cw * 0.5f + listX + 242f) / cw);
                    rr.anchorMin = new Vector2(minX, 0.12f);
                    rr.anchorMax = new Vector2(0.97f, 0.74f);
                    rr.offsetMin = new Vector2(18f, 0f);
                    rr.offsetMax = new Vector2(-12f, 0f);
                }
            }
            shownDifficulties.Clear();
            shownDifficulties.AddRange(diffs);
            UpdateDiffRecordPanel();
            ShowOnly(diffScreen);
        }

        /// <summary>刷新难度页右侧的本地最佳纪录面板（当前歌曲 + 当前模式）。</summary>
        void UpdateDiffRecordPanel()
        {
            if (diffRecordText == null || currentEntry == null) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("★ 最高纪录（" + (taikoMode ? "太鼓" : "四轨") + "）");
            sb.AppendLine();
            foreach (var d in shownDifficulties)
            {
                var rec = PlayRecords.Get(PlayRecords.Key(currentEntry.Dir, taikoMode, d));
                string name = UiKit.PrettyDifficulty(d);
                if (rec == null || (rec.bestScore <= 0 && rec.bestCombo <= 0 && rec.bestAcc <= 0))
                    sb.AppendLine(name + "  ——");
                else
                    sb.AppendLine(string.Format("{0}  {1:F2}% · {2} combo · {3:N0}", name, rec.bestAcc, rec.bestCombo, rec.bestScore));
            }
            sb.AppendLine();
            sb.AppendLine("超越纪录结算时会提醒");
            diffRecordText.text = sb.ToString();
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
            if (comboVisualHandle != null) { StopCoroutine(comboVisualHandle); comboVisualHandle = null; }
            lyricTarget = "";
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
            gameManager.TaikoMode = taikoMode;
            gameManager.AutoMode = autoMode;
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

            // 订阅判定反馈：弹窗 + 误差条 + 连击打点（先退订旧局，避免重复）
            if (judgedHandler != null && gameManager != null) gameManager.OnJudgedWithDelta -= judgedHandler;
            if (popupLayer != null) popupLayer.TaikoMode = gameManager != null && gameManager.TaikoMode;
            judgedHandler = (j, lane, isTail, delta) =>
            {
                if (popupLayer != null) popupLayer.Show(j, lane, isTail);
                if (!isTail && errorBar != null) errorBar.AddTick(j, delta);
            };
            gameManager.OnJudgedWithDelta += judgedHandler;

            state = FlowState.Playing;
            isPaused = false;
            pauseScreen.gameObject.SetActive(false);
            hudSong.text = entry.Meta.Title + " · " + UiKit.PrettyDifficulty(diff)
                + (taikoMode ? " · 太鼓" : "") + (autoMode ? " · AUTO" : "");
            hudHint.text = autoMode
                ? "AUTO 观赏模式 · 自动演奏中 · Esc 暂停"
                : string.IsNullOrEmpty(entry.Meta.AudioPath)
                    ? "无声测试模式 · D F J K / 鼠标点击 · Esc 暂停"
                    : "D F J K / 鼠标点击轨道 · Esc 暂停";
            hudScore.text = "0";
            hudCombo.text = "";
            hudAcc.text = "--";
            shownCombo = -1;
            ShowOnly(hudScreen);
            hudRoutineHandle = StartCoroutine(HudRoutine());
            comboVisualHandle = StartCoroutine(ComboVisualLoop());
            StartCoroutine(LyricKaraokeLoop());
            watchRoutineHandle = StartCoroutine(SongWatchRoutine(chartEnd, entry.Meta.DurationSeconds));
        }

        /// <summary>歌词行切换：旧行整字消散（逐字错峰飘散+旋转+淡出，曲末更从容），新行 OutBack 弹出 + 下往上浮入场。</summary>
        IEnumerator LyricSwap(string newLine)
        {
            var cg = hudLyric.GetComponent<CanvasGroup>();
            if (cg == null) cg = hudLyric.gameObject.AddComponent<CanvasGroup>(); // Unity 假 null 不能用 ?? 判空
            var rt = hudLyric.rectTransform;

            if (hudLyric.text.Length > 0)
            {
                float dur = newLine.Length == 0 ? 0.5f : 0.34f; // 曲末换行更从容
                if (lyricScatter != null)
                {
                    // 整字消散：逐字沿各自方向飘散+微旋转+淡出（消散期间整体保持不透明，淡出由逐字顶点做）
                    if (cg != null) cg.alpha = 1f;
                    float t = 0f;
                    while (t < dur)
                    {
                        t += Time.unscaledDeltaTime;
                        lyricScatter.Progress = t / dur;
                        yield return null;
                    }
                    lyricScatter.Progress = 0f;
                }
                else
                {
                    // 回退：上飘淡出
                    float t = 0f;
                    while (t < dur)
                    {
                        t += Time.unscaledDeltaTime;
                        float k = Mathf.Clamp01(t / dur);
                        cg.alpha = 1f - k;
                        rt.anchoredPosition = new Vector2(0f, 26f * k);
                        yield return null;
                    }
                }
            }

            rt.anchoredPosition = Vector2.zero;
            rt.localScale = Vector3.one;
            if (newLine.Length == 0)
            {
                hudLyric.text = "";
                cg.alpha = 0f;
                yield break;
            }

            if (lyricKaraoke != null) lyricKaraoke.Progress = 0f; // 新行从全暗起步，逐字扫亮
            hudLyric.text = newLine;
            const float Dur = 0.28f;
            float ti = 0f;
            while (ti < Dur)
            {
                ti += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(ti / Dur);
                cg.alpha = k;
                float s = Mathf.Lerp(0.78f, 1f, OutBack(k));
                rt.localScale = new Vector3(s, s, 1f);
                rt.anchoredPosition = new Vector2(0f, -26f * (1f - OutCubic(k)));
                yield return null;
            }
            cg.alpha = 1f;
            rt.localScale = Vector3.one;
            rt.anchoredPosition = Vector2.zero;
        }

        private static float OutBack(float t)
        {
            const float c1 = 1.70158f;
            float k = t - 1f;
            return 1f + (c1 + 1f) * k * k * k + c1 * k * k;
        }

        private static float OutCubic(float t) { float k = 1f - t; return 1f - k * k * k; }

        /// <summary>连击视觉循环（每帧）：跳动/白闪/彩虹流彩全由字段驱动的连续动画，轮询只改字段不重启动画，无撕裂。</summary>
        IEnumerator ComboVisualLoop()
        {
            var rt = hudCombo.rectTransform;
            while (state == FlowState.Playing && hudCombo != null)
            {
                if (comboBreaking) { yield return null; continue; }
                comboPunchAge += Time.unscaledDeltaTime;
                float decay = Mathf.Exp(-comboPunchAge * 7f);
                float wobble = comboPunchAge < 2f ? Mathf.Cos(comboPunchAge * 18f) * decay : 0f;
                float s = ComboStageScale[comboStage] * (1f + comboPunchAmp * wobble);
                rt.localScale = new Vector3(s, s, 1f);
                rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(comboPunchAge * 22f) * comboRotAmp * decay * comboTiltDir);
                if (comboPunchAge < 0.22f)
                    hudCombo.color = Color.Lerp(Color.white, comboStageColorRef, comboPunchAge / 0.22f);
                else if (comboStage == ComboStageColor.Length - 1)
                    hudCombo.color = Color.HSVToRGB((Time.unscaledTime * 0.35f) % 1f, 0.65f, 1f); // 500+ 彩虹流彩
                else
                    hudCombo.color = comboStageColorRef;
                yield return null;
            }
        }

        /// <summary>连击变化应用：更新数字/阶段形态；命中给弹性跳动，升阶爆发冲击环，断连播放消散。</summary>
        void ApplyComboStage(int combo, bool increased, int prevStage)
        {
            if (combo < 2)
            {
                if (hudCombo.text.Length > 0) { comboBreaking = true; StartCoroutine(ComboBreakAnim()); }
                return;
            }
            comboStage = ComboStageOf(combo);
            bool milestone = increased && comboStage > prevStage;
            comboStageColorRef = comboStage == ComboStageColor.Length - 1
                ? Color.HSVToRGB((Time.unscaledTime * 0.35f) % 1f, 0.65f, 1f)
                : ComboStageColor[comboStage];
            hudCombo.text = combo.ToString();
            if (comboCaption != null)
                comboCaption.color = new Color(comboStageColorRef.r, comboStageColorRef.g, comboStageColorRef.b, 0.75f);
            comboPunchAmp = milestone ? 0.5f : 0.3f;
            comboRotAmp = milestone ? 4.5f : 1.8f;
            comboTiltDir = -comboTiltDir;
            comboPunchAge = 0f;
            if (milestone) SpawnComboRing(comboStageColorRef);
        }

        /// <summary>断连：变红 + 水平抖动 + 淡出消散后清空（期间暂停视觉循环写入）。</summary>
        IEnumerator ComboBreakAnim()
        {
            var cg = hudCombo.GetComponent<CanvasGroup>();
            if (cg == null) cg = hudCombo.gameObject.AddComponent<CanvasGroup>();
            var rt = hudCombo.rectTransform;
            var c0 = hudCombo.color;
            Vector2 basePos = rt.anchoredPosition;
            float t = 0f;
            while (t < 0.3f)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / 0.3f);
                rt.anchoredPosition = basePos + new Vector2(Mathf.Sin(t * 70f) * 7f * (1f - k), 0f);
                rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(t * 55f) * 3f * (1f - k));
                hudCombo.color = Color.Lerp(c0, new Color(1f, 0.25f, 0.25f), k);
                cg.alpha = 1f - k;
                yield return null;
            }
            hudCombo.text = "";
            hudCombo.color = c0;
            rt.anchoredPosition = basePos;
            rt.localRotation = Quaternion.identity;
            cg.alpha = 1f;
            comboBreaking = false;
        }

        private static int ComboStageOf(int combo)
        {
            int s = 0;
            for (int i = 0; i < ComboStageAt.Length; i++)
                if (combo >= ComboStageAt[i]) s = i;
            return s;
        }

        /// <summary>升阶冲击环：数字背后扩散一圈同色光环后销毁。</summary>
        void SpawnComboRing(Color color)
        {
            var go = new GameObject("ComboRing", typeof(RectTransform));
            go.transform.SetParent(hudCombo.transform.parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, (0.92f - 0.5f) * 1080f);
            rt.sizeDelta = new Vector2(300f, 300f);
            var img = go.AddComponent<Image>();
            img.sprite = UiKit.RingSprite;
            img.color = new Color(color.r, color.g, color.b, 0.85f);
            img.raycastTarget = false;
            go.transform.SetSiblingIndex(hudCombo.transform.GetSiblingIndex()); // 数字之下

            var host = go;
            StartCoroutine(RingRoutine(img, host));
        }

        IEnumerator RingRoutine(Image img, GameObject host)
        {
            float t = 0f;
            while (t < 0.45f && img != null)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / 0.45f);
                float s = Mathf.Lerp(0.45f, 1.6f, OutCubic(k));
                img.rectTransform.localScale = new Vector3(s, s, 1f);
                var c = img.color;
                img.color = new Color(c.r, c.g, c.b, 0.85f * (1f - k));
                yield return null;
            }
            if (host != null) Destroy(host);
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
                    hudAcc.text = FormatAccuracy(AccuracyToPercent(scoreSystem.Accuracy));
                    int combo = scoreSystem.Combo;
                    if (combo != shownCombo)
                    {
                        int prevStage = ComboStageOf(Mathf.Max(0, shownCombo));
                        bool increased = combo > shownCombo;
                        shownCombo = combo;
                        ApplyComboStage(combo, increased, prevStage);
                    }
                }
                if (gameManager != null && gameManager.Lyrics != null && gameManager.Conductor != null)
                {
                    string line = gameManager.Lyrics.TryGetLine(gameManager.Conductor.SongTimeSeconds, out string lyric)
                        ? lyric : "";
                    // 用目标行字段做变更检测：动画期间控件文本仍是旧行，读控件会每 0.1s 重启动画导致狂闪
                    if (lyricTarget != line)
                    {
                        lyricTarget = line;
                        if (lyricAnimHandle != null) StopCoroutine(lyricAnimHandle);
                        lyricAnimHandle = StartCoroutine(LyricSwap(line));
                    }
                }
                yield return wait;
            }
            if (hudLyric != null && hudLyric.text.Length > 0)
            {
                if (lyricAnimHandle != null) StopCoroutine(lyricAnimHandle);
                lyricTarget = "";
                lyricAnimHandle = StartCoroutine(LyricSwap("")); // 曲末歌词从容退场
            }
        }

        /// <summary>卡拉OK逐字渐亮：每帧把本行时间窗（行戳→下一行戳）内的唱到比例喂给 LyricKaraokeEffect。</summary>
        IEnumerator LyricKaraokeLoop()
        {
            while (state == FlowState.Playing)
            {
                if (lyricKaraoke != null && gameManager != null && gameManager.Lyrics != null && gameManager.Conductor != null)
                {
                    double now = gameManager.Conductor.SongTimeSeconds;
                    if (gameManager.Lyrics.TryGetLineSpan(now, out _, out double start, out double end) && end > start)
                        lyricKaraoke.Progress = Mathf.Clamp01((float)((now - start) / (end - start)));
                    else
                        lyricKaraoke.Progress = 1f; // 首句前 / 零宽窗口：满亮兜底
                }
                yield return null;
            }
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

            // 提交本地最佳成绩，生成结算对比文案（AUTO 观赏局不提交，避免污染纪录）
            string recordLine = "";
            bool newRecord = false;
            if (autoMode)
            {
                recordLine = "AUTO 观赏模式 · 成绩不计入本地纪录";
            }
            else if (ss != null && currentEntry != null && !string.IsNullOrEmpty(currentDifficulty))
            {
                var key = PlayRecords.Key(currentEntry.Dir, taikoMode, currentDifficulty);
                newRecord = PlayRecords.Submit(key, score, maxCombo, acc, out var prev);
                if (newRecord)
                    recordLine = prev != null
                        ? string.Format("超越纪录！  得分 {0:N0} → {1:N0}", prev.bestScore, score)
                        : "首次游玩，成绩已记入本地最佳";
                else if (prev != null)
                    recordLine = string.Format("历史最佳  得分 {0:N0} · 连击 {1} · {2:F2}%", prev.bestScore, prev.bestCombo, prev.bestAcc);
            }
            if (results != null)
                results.Show(title, UiKit.PrettyDifficulty(currentDifficulty) + (autoMode ? " · AUTO" : ""), acc, maxCombo, score, total,
                    ss != null ? ss.PerfectCount : 0, ss != null ? ss.GoodCount : 0,
                    ss != null ? ss.BadCount : 0, ss != null ? ss.MissCount : 0,
                    recordLine, newRecord);
        }

        void OnResultsRetry()
        {
            if (results != null) results.Hide(); // 结算面板不在 screens 列表，必须显式隐藏，否则残留盖住下一局
            if (currentEntry != null && !string.IsNullOrEmpty(currentDifficulty))
                StartCoroutine(StartGameRoutine(currentEntry, currentDifficulty));
        }

        void OnResultsSongSelect()
        {
            if (results != null) results.Hide();
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
