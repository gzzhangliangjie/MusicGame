using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MusicGame.Analysis;
using MusicGame.Core;
using MusicGame.Pure.Model;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

namespace MusicGame.Gameplay
{
    /// <summary>
    /// 玩法总装：载谱(ChartIO)→音频(AudioImportService，失败走静音无声测试谱模式)→
    /// Conductor(dspTime 时钟)→NoteSpawner→JudgeSystem/ScoreSystem→谱面结束回调结算(OnSongFinished)。
    /// 输入：D/F/J/K 四轨键盘或触屏四轨。
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        /// <summary>默认下落时间（秒）。</summary>
        public const float DefaultTravelTimeSeconds = 1.2f;
        /// <summary>延迟校准 PlayerPrefs 键（CalibrationScreen 写入）。</summary>
        public const string AudioOffsetPrefKey = "MusicGame.AudioOffset";
        private const float GetReadySeconds = 0.6f;
        private const float EndTailSeconds = 2.0f;
        private const int ClockStallFrameLimit = 600;

        /// <summary>音符下落时间（秒，从生成点落到判定线）。</summary>
        public float noteTravelTimeSeconds = DefaultTravelTimeSeconds;
        /// <summary>太鼓模式：PlaySong 前置位。音符横向卷动，咚/咔/连击条，复用同一判定计分。</summary>
        public bool TaikoMode;
        /// <summary>Auto 观赏模式：PlaySong 前置位。自动以 Perfect 演奏整谱，不读取任何输入设备。</summary>
        public bool AutoMode;

        /// <summary>谱面结束/出错/停止回调，携带有结算数据。</summary>
        public event Action<PlayResult> OnSongFinished;
        /// <summary>每次判定的反馈：判定结果、轨道、是否 hold 尾判（供 HUD/特效使用）。</summary>
        public event Action<Judgement, int, bool> OnJudged;
        /// <summary>同 OnJudged，另携带本次判定的早/晚偏移（秒，早负晚正；Miss 时为超窗时刻的近似值）。</summary>
        public event Action<Judgement, int, bool, double> OnJudgedWithDelta;

        /// <summary>计分系统（游玩中/结束后可读）。</summary>
        public ScoreSystem Score => score;
        /// <summary>时钟。</summary>
        public Conductor Conductor => conductor;
        /// <summary>是否正在游玩。</summary>
        public bool IsPlaying => playing;
        /// <summary>当前歌曲 Id。</summary>
        public string CurrentSongId => lastSongId;
        /// <summary>当前难度。</summary>
        public string CurrentDifficulty => lastDifficulty;
        /// <summary>判定用时钟（已扣除用户校准偏移）。</summary>
        public double JudgedTime => conductor != null ? conductor.SongTimeSeconds - audioOffsetSeconds : 0.0;
        /// <summary>已加载的歌词（歌曲目录下无 .lrc 时为 null）。</summary>
        public Lyrics Lyrics => lyrics;

        private readonly bool[] lanePressed = new bool[4];
        private readonly bool[] laneHeld = new bool[4];
        private readonly List<NoteData> autoActiveHolds = new List<NoteData>(); // Auto：头已命中、尾未到的 hold/气球
        private AudioSource audioSource;
        private Conductor conductor;
        private NotePool pool;
        private NoteSpawner spawner;
        private JudgeSystem judge;
        private ScoreSystem score;
        private HitEffects hitEffects;
        private SpectrumVisualizer visualizer;
        private Taiko.TaikoSpawner taikoSpawner;
        private Lyrics lyrics;
        private MvBackdrop mvBackdrop;
        private GameObject runtimeRoot;
        private bool playing;
        private bool paused;
        private bool ownsSilentClip;
        private float audioOffsetSeconds;
        private string lastSongId = "";
        private string lastDifficulty = "";

        /// <summary>单次游玩的结算数据。</summary>
        public class PlayResult
        {
            public string SongId = "";
            public string Difficulty = "";
            public double Score;
            public double Accuracy = 1.0;
            public int MaxCombo;
            public int PerfectCount;
            public int GoodCount;
            public int BadCount;
            public int MissCount;
            public bool Completed;
            public string Error;
        }

        private void Awake()
        {
            audioSource = GetComponent<AudioSource>();
            if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            SetupCamera();
        }

        private void OnDestroy()
        {
            StopAllCoroutines();
            Cleanup();
        }

        /// <summary>开始播放一首歌：载谱→组装→无声或有声驱动，结束时触发 OnSongFinished。</summary>
        public void PlaySong(SongMeta meta, string difficulty)
        {
            StopAllCoroutines();
            Cleanup();
            lastSongId = meta != null ? meta.Id : "";
            lastDifficulty = difficulty ?? "";
            StartCoroutine(PlaySongRoutine(meta, difficulty));
        }

        /// <summary>手动停止（立即回调一次 Completed=false 的结算）。</summary>
        public void StopSong()
        {
            StopAllCoroutines();
            PlayResult result = BuildResult(false, "stopped");
            Cleanup();
            OnSongFinished?.Invoke(result);
        }

        /// <summary>是否处于暂停。</summary>
        public bool IsPaused => paused;

        /// <summary>
        /// 暂停/恢复（Esc 暂停菜单用）：同步冻结 Conductor 时钟与音频，并挂起判定输入与结束检测。
        /// 方法名含 "pause" 且带 bool 参数，GameFlow 的 TryCallPauseApi 反射可直接命中。
        /// </summary>
        public void SetPaused(bool pausedState)
        {
            if (!playing) return;
            paused = pausedState;
            if (pausedState) conductor?.Pause();
            else conductor?.Resume();
            if (mvBackdrop != null)
            {
                if (pausedState) mvBackdrop.Pause();
                else mvBackdrop.Resume();
            }
        }

        private IEnumerator PlaySongRoutine(SongMeta meta, string difficulty)
        {
            audioOffsetSeconds = PlayerPrefs.GetFloat(AudioOffsetPrefKey, 0f);

            // 1) 载谱：Songs/<songId>/chart_<difficulty>.json；太鼓模式重映射轨道（0/1→咚 2/3→咔 hold→连击）
            string chartPath = FindChartPath(lastSongId, lastDifficulty);
            ChartData chart = null;
            if (chartPath != null)
            {
                try { chart = ChartIO.Load(chartPath); }
                catch (Exception e) { Debug.LogException(e); }
            }
            if (chart != null && TaikoMode) chart = RemapChartForTaiko(chart);
            if (chart == null || chart.Notes == null || chart.Notes.Count == 0)
            {
                FinishPlay(false, "chart not found or empty: Songs/" + lastSongId + "/chart_" + lastDifficulty + ".json");
                yield break;
            }
            lyrics = LoadLyrics(chartPath, meta);

            // 2) 组装判定/计分（hold = 头+尾两次判定）
            double chartEnd = 0.0;
            int totalJudgements = 0;
            foreach (NoteData note in chart.Notes)
            {
                if (note == null || note.Lane < 0 || note.Lane > 3) continue;
                bool hold = string.Equals(note.Type, "hold", StringComparison.OrdinalIgnoreCase) && note.Duration > 0.0;
                chartEnd = Math.Max(chartEnd, note.Time + (hold ? note.Duration : 0.0));
                totalJudgements += hold ? 2 : 1;
            }
            judge = new JudgeSystem();
            judge.Setup(chart);
            score = new ScoreSystem();
            score.Setup(totalJudgements);
            EnsureRuntimeObjects(chart);
            mvBackdrop = CreateMvBackdrop(chartPath, meta); // 必须在 runtimeRoot 建好之后（内部挂靠其下）

            // 3) 音频：AudioPath 为空或加载失败 → 静音无声测试谱模式（生成静音 clip 驱动时钟）
            AudioClip clip = null;
            if (meta != null && !string.IsNullOrEmpty(meta.AudioPath))
            {
                Task<AudioClip> loadTask = null;
                try { loadTask = AudioImportService.LoadClip(meta.AudioPath); }
                catch (Exception e) { Debug.LogWarning("[GameManager] LoadClip threw: " + e.Message); }
                if (loadTask != null)
                {
                    while (!loadTask.IsCompleted) yield return null;
                    if (loadTask.Status == TaskStatus.RanToCompletion) clip = loadTask.Result;
                    if (clip == null) Debug.LogWarning("[GameManager] audio load failed, falling back to silent clock");
                }
            }
            if (clip == null)
            {
                clip = CreateSilentClip((float)(chartEnd + EndTailSeconds + 3.0));
                ownsSilentClip = clip != null;
                Debug.Log("[GameManager] silent mode (empty AudioPath or load failure)");
            }
            if (clip != null) audioSource.clip = clip;

            // 4) 短暂准备时间后启动时钟
            yield return new WaitForSecondsRealtime(GetReadySeconds);
            paused = false;
            playing = true;
            conductor.Setup(chart.Bpm, chart.OffsetSeconds, audioSource); // Setup 在 source 带 clip 时会自行从头播放
            if (mvBackdrop != null) mvBackdrop.Play(); // MV 与音乐同步开播

            // 5) 等待谱面结束（含尾部缓冲），期间 Update 处理输入与自动 Miss
            double endTime = chartEnd + EndTailSeconds;
            double lastTime = double.MinValue;
            int stallFrames = 0;
            while (conductor.SongTimeSeconds < endTime)
            {
                if (paused) { stallFrames = 0; lastTime = double.MinValue; yield return null; continue; } // 暂停期间不计停滞，也不做自然播完判定
                // 音频自然播完 → Conductor 冻结时钟并置 IsPlaying=false：提前正常结算
                if (!conductor.IsPlaying && conductor.SongTimeSeconds > 1.0) break;
                double t = conductor.SongTimeSeconds;
                if (t <= lastTime + 1e-9)
                {
                    stallFrames++;
                    if (stallFrames > ClockStallFrameLimit) break; // 时钟长时间不推进的兜底
                }
                else stallFrames = 0;
                lastTime = t;
                yield return null;
            }
            FinishPlay(true, null);
        }

        private void Update()
        {
            if (!playing || paused || judge == null || score == null || conductor == null) return;
            if (!TaikoMode && (spawner == null || hitEffects == null)) return;
            if (TaikoMode && taikoSpawner == null) return;
            PollInput();
            double now = JudgedTime;

            // 头判：太鼓=咚(0)/咔(1)/连击(2 任一鼓)，四轨=lane 0..3；Auto 观赏逐帧清空到点队列（恒 Perfect）
            for (int lane = 0; lane < (TaikoMode ? 3 : lanePressed.Length); lane++)
            {
                if (AutoMode)
                {
                    while (judge.TryAutoHit(lane, now, out NoteData autoNote))
                        RegisterHit(autoNote, Judgement.Perfect);
                    continue;
                }
                if (!lanePressed[lane]) continue;
                if (judge.TryHit(lane, now, out NoteData note, out Judgement judgement))
                    RegisterHit(note, judgement);
            }

            // 超窗自动 Miss 回收
            while (judge.TryMiss(now, out NoteData missed))
            {
                score.Register(Judgement.Miss, false);
                if (TaikoMode) taikoSpawner.NotifyMissed(missed);
                else
                {
                    spawner.NotifyMissed(missed);
                    hitEffects.Spawn(missed.Lane, Judgement.Miss, false, true);
                }
                OnJudged?.Invoke(Judgement.Miss, missed.Lane, false);
                OnJudgedWithDelta?.Invoke(Judgement.Miss, missed.Lane, false, JudgeSystem.BadWindowSeconds);
            }
        }

        /// <summary>头判命中统一登记：计分、表现反馈、事件；Auto 模式把 hold/气球登记为持续按住直到尾。</summary>
        private void RegisterHit(NoteData note, Judgement judgement)
        {
            double delta = JudgedTime - note.Time;
            score.Register(judgement, false);
            if (TaikoMode) taikoSpawner.NotifyJudged(note, judgement);
            else
            {
                spawner.NotifyJudged(note, judgement);
                hitEffects.Spawn(note.Lane, judgement, false, false);
            }
            if (AutoMode && IsHold(note)) autoActiveHolds.Add(note);
            OnJudged?.Invoke(judgement, note.Lane, false);
            OnJudgedWithDelta?.Invoke(judgement, note.Lane, false, delta);
        }

        private static bool IsHold(NoteData note)
        {
            return string.Equals(note.Type, "hold", StringComparison.OrdinalIgnoreCase) && note.Duration > 0.0;
        }

        /// <summary>某轨当前是否被按住（键盘或触屏，供 NoteController hold 尾判使用）。</summary>
        public bool IsLaneHeld(int lane)
        {
            if (lane < 0 || lane >= laneHeld.Length) return false;
            PollInput(); // 实时刷新，保证松手判定精确
            return laneHeld[lane];
        }

        // 尾判等内部反馈入口（NoteSpawner.RegisterTail / TaikoSpawner.RegisterTail 调用）
        internal void NotifyJudgementFeedback(NoteData note, Judgement judgement, bool isTail)
        {
            OnJudged?.Invoke(judgement, note != null ? note.Lane : -1, isTail);
            if (note == null) return;
            double target = note.Time + (isTail ? note.Duration : 0.0);
            double delta = JudgedTime - target;
            OnJudgedWithDelta?.Invoke(judgement, note.Lane, isTail, delta);
            if (judgement != Judgement.Miss && !TaikoMode) hitEffects.Spawn(note.Lane, judgement, isTail, false);
        }

        private void PollInput()
        {
            for (int i = 0; i < lanePressed.Length; i++)
            {
                lanePressed[i] = false;
                laneHeld[i] = false;
            }

            if (AutoMode) { AutoPollInput(); return; } // 观赏模式不采样任何输入设备
            if (TaikoMode) { PollTaikoInput(); return; }

#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                lanePressed[0] |= keyboard.dKey.wasPressedThisFrame;
                laneHeld[0] |= keyboard.dKey.isPressed;
                lanePressed[1] |= keyboard.fKey.wasPressedThisFrame;
                laneHeld[1] |= keyboard.fKey.isPressed;
                lanePressed[2] |= keyboard.jKey.wasPressedThisFrame;
                laneHeld[2] |= keyboard.jKey.isPressed;
                lanePressed[3] |= keyboard.kKey.wasPressedThisFrame;
                laneHeld[3] |= keyboard.kKey.isPressed;
            }
            // 鼠标：点击/按住哪条轨道就判定哪条（桌面端可直接用鼠标玩）
            UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null)
            {
                int mouseLane = ScreenXToLane(mouse.position.ReadValue().x);
                if (mouseLane >= 0)
                {
                    laneHeld[mouseLane] |= mouse.leftButton.isPressed;
                    lanePressed[mouseLane] |= mouse.leftButton.wasPressedThisFrame;
                }
            }
            Touchscreen touchscreen = Touchscreen.current;
            if (touchscreen != null)
            {
                foreach (TouchControl touch in touchscreen.touches)
                {
                    UnityEngine.InputSystem.TouchPhase phase = touch.phase.ReadValue();
                    if (phase != UnityEngine.InputSystem.TouchPhase.Began &&
                        phase != UnityEngine.InputSystem.TouchPhase.Moved &&
                        phase != UnityEngine.InputSystem.TouchPhase.Stationary) continue;
                    int lane = ScreenXToLane(touch.position.ReadValue().x);
                    if (lane < 0) continue;
                    laneHeld[lane] = true;
                    if (phase == UnityEngine.InputSystem.TouchPhase.Began) lanePressed[lane] = true;
                }
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            lanePressed[0] = Input.GetKeyDown(KeyCode.D);
            laneHeld[0] = Input.GetKey(KeyCode.D);
            lanePressed[1] = Input.GetKeyDown(KeyCode.F);
            laneHeld[1] = Input.GetKey(KeyCode.F);
            lanePressed[2] = Input.GetKeyDown(KeyCode.J);
            laneHeld[2] = Input.GetKey(KeyCode.J);
            lanePressed[3] = Input.GetKeyDown(KeyCode.K);
            laneHeld[3] = Input.GetKey(KeyCode.K);
            // 鼠标：点击/按住哪条轨道就判定哪条
            if (Input.GetMouseButton(0))
            {
                int mouseLane = ScreenXToLane(Input.mousePosition.x);
                if (mouseLane >= 0)
                {
                    laneHeld[mouseLane] = true;
                    lanePressed[mouseLane] |= Input.GetMouseButtonDown(0);
                }
            }
            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch touch = Input.GetTouch(i);
                if (touch.phase == TouchPhase.Began || touch.phase == TouchPhase.Moved || touch.phase == TouchPhase.Stationary)
                {
                    int lane = ScreenXToLane(touch.position.x);
                    if (lane >= 0)
                    {
                        laneHeld[lane] = true;
                        if (touch.phase == TouchPhase.Began) lanePressed[lane] = true;
                    }
                }
            }
#endif
        }

        // Auto 观赏：不采样键盘/鼠标/触屏；进行中的 hold/气球持续视为按住（IsLaneHeld 供尾判与气球充气读取）
        private void AutoPollInput()
        {
            double now = JudgedTime;
            for (int i = autoActiveHolds.Count - 1; i >= 0; i--)
            {
                NoteData hold = autoActiveHolds[i];
                if (now >= hold.Time + hold.Duration)
                {
                    autoActiveHolds.RemoveAt(i);
                    continue;
                }
                laneHeld[hold.Lane] = true;
            }
        }

        // 太鼓输入：F/J=咚(0)，D/K=咔(1)，任意鼓=连击(2)；鼠标/触屏按屏幕左右半区分咚咔
        private void PollTaikoInput()
        {
            bool don = false, katsu = false;
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                don |= keyboard.fKey.wasPressedThisFrame || keyboard.jKey.wasPressedThisFrame;
                katsu |= keyboard.dKey.wasPressedThisFrame || keyboard.kKey.wasPressedThisFrame;
                laneHeld[0] |= keyboard.fKey.isPressed || keyboard.jKey.isPressed;
                laneHeld[1] |= keyboard.dKey.isPressed || keyboard.kKey.isPressed;
            }
            UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null && !UiBlocked())
            {
                bool leftHalf = mouse.position.ReadValue().x < Screen.width * 0.5f;
                int pad = leftHalf ? 0 : 1;
                lanePressed[pad] |= mouse.leftButton.wasPressedThisFrame;
                laneHeld[pad] |= mouse.leftButton.isPressed;
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            don = Input.GetKeyDown(KeyCode.F) || Input.GetKeyDown(KeyCode.J);
            katsu = Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.K);
            laneHeld[0] = Input.GetKey(KeyCode.F) || Input.GetKey(KeyCode.J);
            laneHeld[1] = Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.K);
            if (Input.GetMouseButton(0) && !UiBlocked())
            {
                int pad = Input.mousePosition.x < Screen.width * 0.5f ? 0 : 1;
                lanePressed[pad] |= Input.GetMouseButtonDown(0);
                laneHeld[pad] = true;
            }
#endif
            lanePressed[0] |= don;
            lanePressed[1] |= katsu;
            bool any = don || katsu;
            lanePressed[2] |= any;
            laneHeld[2] |= laneHeld[0] || laneHeld[1];
        }

        // 触屏（新旧后端）也按左右半映射咚咔
        private bool UiBlocked()
        {
            return false; // 太鼓模式 HUD 无交互元素，点击总是视为打鼓
        }

        // 屏幕横坐标 → 轨道号（0..3，越界返回 -1）
        private int ScreenXToLane(float screenX)
        {
            Camera cam = Camera.main;
            if (cam == null) return -1;
            Vector3 world = cam.ScreenToWorldPoint(new Vector3(screenX, 0f, -cam.transform.position.z));
            int lane = Mathf.FloorToInt(world.x / NoteSpawner.LaneWidth + NoteSpawner.LaneCount * 0.5f);
            return lane >= 0 && lane < NoteSpawner.LaneCount ? lane : -1;
        }

        private void EnsureRuntimeObjects(ChartData chart)
        {
            if (runtimeRoot != null) return;
            runtimeRoot = new GameObject("GameplayRuntime");
            runtimeRoot.transform.SetParent(transform, false);

            conductor = runtimeRoot.AddComponent<Conductor>();

            GameObject prefab = BuildNotePrefab(TaikoMode);
            prefab.transform.SetParent(runtimeRoot.transform, false); // 挂到 runtimeRoot 下随 Cleanup 统一销毁，避免每局泄漏一个游离模板
            pool = new GameObject("NotePool").AddComponent<NotePool>();
            pool.transform.SetParent(runtimeRoot.transform, false);
            pool.Setup(prefab, 32);

            if (TaikoMode)
            {
                taikoSpawner = new GameObject("TaikoSpawner").AddComponent<Taiko.TaikoSpawner>();
                taikoSpawner.transform.SetParent(runtimeRoot.transform, false);
                taikoSpawner.Score = score;
                taikoSpawner.Game = this;
                taikoSpawner.Setup(chart, conductor, pool, noteTravelTimeSeconds);
            }
            else
            {
                spawner = new GameObject("NoteSpawner").AddComponent<NoteSpawner>();
                spawner.transform.SetParent(runtimeRoot.transform, false);
                spawner.Score = score;
                spawner.Game = this;
                spawner.Setup(chart, conductor, pool, noteTravelTimeSeconds);

                hitEffects = new GameObject("HitEffects").AddComponent<HitEffects>();
                hitEffects.transform.SetParent(runtimeRoot.transform, false);
            }

            visualizer = new GameObject("SpectrumVisualizer").AddComponent<SpectrumVisualizer>();
            visualizer.transform.SetParent(runtimeRoot.transform, false);
            visualizer.Setup(audioSource, conductor);
        }

        private static GameObject BuildNotePrefab(bool taiko)
        {
            var prefab = new GameObject("NotePrefab");
            prefab.AddComponent<SpriteRenderer>();
            if (taiko) prefab.AddComponent<Taiko.TaikoNote>();
            else prefab.AddComponent<NoteController>();
            prefab.SetActive(false);
            return prefab;
        }

        private static GameObject BuildNotePrefab()
        {
            var prefab = new GameObject("NotePrefab");
            prefab.AddComponent<SpriteRenderer>();
            prefab.AddComponent<NoteController>();
            prefab.SetActive(false);
            return prefab;
        }

        private static AudioClip CreateSilentClip(float seconds)
        {
            const int sampleRate = 8000; // 低采样率省内存，仅用于驱动时钟
            int samples = Mathf.CeilToInt(Mathf.Max(1f, seconds) * sampleRate);
            AudioClip clip = AudioClip.Create("Silence", samples, 1, sampleRate, false);
            if (clip != null) clip.SetData(new float[samples], 0);
            return clip;
        }

        // 谱面路径：优先工程根 Songs/<id>/，再试 Assets 与 persistentDataPath
        private static string FindChartPath(string songId, string difficulty)
        {
            if (string.IsNullOrEmpty(songId) || string.IsNullOrEmpty(difficulty)) return null;
            string fileName = "chart_" + difficulty + ".json";
            var candidates = new List<string>();
            try
            {
                DirectoryInfo parent = Directory.GetParent(Application.dataPath);
                if (parent != null) candidates.Add(Path.Combine(parent.FullName, "Songs", songId, fileName));
            }
            catch { }
            candidates.Add(Path.Combine(Application.dataPath, "Songs", songId, fileName));
            candidates.Add(Path.Combine(Application.persistentDataPath, "Songs", songId, fileName));
            foreach (string path in candidates)
            {
                if (File.Exists(path)) return path;
            }
            return null;
        }

        // 歌词：优先 <歌曲名>.lrc（与歌曲同名），回退 lyrics.lrc；缺失/损坏静默降级为无歌词
        // 太鼓轨道重映射：lane 0/1 → 0(咚)，lane 2/3 → 1(咔)，hold → 2(连击条)；其余字段原样复制
        private static ChartData RemapChartForTaiko(ChartData source)
        {
            var mapped = new ChartData
            {
                SongId = source.SongId,
                Difficulty = source.Difficulty,
                Bpm = source.Bpm,
                OffsetSeconds = source.OffsetSeconds,
                Notes = new List<NoteData>(source.Notes.Count)
            };
            foreach (NoteData note in source.Notes)
            {
                if (note == null || note.Lane < 0 || note.Lane > 3) continue;
                bool hold = string.Equals(note.Type, "hold", StringComparison.OrdinalIgnoreCase) && note.Duration > 0.0;
                mapped.Notes.Add(new NoteData
                {
                    Time = note.Time,
                    Lane = hold ? 2 : (note.Lane <= 1 ? 0 : 1),
                    Type = hold ? "hold" : "tap",
                    Duration = note.Duration
                });
            }
            return mapped;
        }

        // MV 背景：歌曲目录 mv.mp4 存在时创建半透明打底视频；缺文件/缺相机静默跳过（零影响）
        private MvBackdrop CreateMvBackdrop(string chartPath, SongMeta meta)
        {
            try
            {
                if (chartPath == null || runtimeRoot == null) return null;
                string dir = Path.GetDirectoryName(chartPath);
                if (string.IsNullOrEmpty(dir)) return null;
                string mvPath = Path.Combine(dir, "mv.mp4");
                if (!File.Exists(mvPath)) return null;
                return MvBackdrop.Create(runtimeRoot.transform, mvPath, Camera.main,
                    meta != null ? meta.MvStartSeconds : 0.0);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GameManager] MV backdrop create failed: " + e.Message);
                return null;
            }
        }

        private static Lyrics LoadLyrics(string chartPath, SongMeta meta)
        {
            try
            {
                if (chartPath == null) return null;
                string dir = Path.GetDirectoryName(chartPath);
                if (string.IsNullOrEmpty(dir)) return null;
                var candidates = new List<string>();
                if (meta != null && !string.IsNullOrEmpty(meta.Title))
                    candidates.Add(Path.Combine(dir, meta.Title + ".lrc"));
                candidates.Add(Path.Combine(dir, "lyrics.lrc"));
                foreach (string path in candidates)
                {
                    if (!File.Exists(path)) continue;
                    Lyrics parsed = Lyrics.LoadFile(path);
                    if (parsed != null && parsed.Count > 0) return parsed;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GameManager] lyrics load failed: " + e.Message);
            }
            return null;
        }

        private PlayResult BuildResult(bool completed, string error)
        {
            var result = new PlayResult
            {
                SongId = lastSongId,
                Difficulty = lastDifficulty,
                Completed = completed,
                Error = error
            };
            if (score != null)
            {
                result.Score = score.Score;
                result.Accuracy = score.Accuracy;
                result.MaxCombo = score.MaxCombo;
                result.PerfectCount = score.PerfectCount;
                result.GoodCount = score.GoodCount;
                result.BadCount = score.BadCount;
                result.MissCount = score.MissCount;
            }
            return result;
        }

        private void FinishPlay(bool completed, string error)
        {
            PlayResult result = BuildResult(completed, error);
            playing = false;
            if (audioSource != null) audioSource.Stop();
            Cleanup();
            OnSongFinished?.Invoke(result);
        }

        private void Cleanup()
        {
            playing = false;
            paused = false;
            if (audioSource != null)
            {
                audioSource.Stop();
                if (ownsSilentClip && audioSource.clip != null) Destroy(audioSource.clip);
                audioSource.clip = null;
            }
            ownsSilentClip = false;
            if (mvBackdrop != null)
            {
                Destroy(mvBackdrop.gameObject);
                mvBackdrop = null;
            }
            if (spawner != null) spawner.ReleaseAll();
            if (runtimeRoot != null) Destroy(runtimeRoot);
            runtimeRoot = null;
            conductor = null;
            pool = null;
            spawner = null;
            judge = null;
            hitEffects = null;
            visualizer = null;
            taikoSpawner = null;
            autoActiveHolds.Clear();
            lyrics = null;
            // score 保留，供结算后查询
        }

        // 保证相机适合 2D 下落玩法（正交、朝向 +z）
        private static void SetupCamera()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            if (!cam.orthographic || cam.orthographicSize < 3f || cam.orthographicSize > 8f)
            {
                cam.orthographic = true;
                cam.orthographicSize = 6f;
            }
            if (cam.transform.rotation != Quaternion.identity) cam.transform.rotation = Quaternion.identity;
            if (cam.transform.position.z > -1f) cam.transform.position = new Vector3(0f, 0f, -10f);
        }
    }
}
