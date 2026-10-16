using System.Collections.Generic;
using MusicGame.Core;
using MusicGame.Pure.Model;
using UnityEngine;

namespace MusicGame.Gameplay
{
    /// <summary>
    /// 音符生成器：按 travelTimeSeconds 提前从对象池生成音符，维护 NoteData→NoteController 映射，
    /// 并绘制轨道参考线与判定线（最简 2D 方块视觉，无资源依赖）。
    /// </summary>
    public class NoteSpawner : MonoBehaviour
    {
        /// <summary>单轨宽度（世界单位）。</summary>
        public const float LaneWidth = 2f;
        /// <summary>判定线 Y。</summary>
        public const float JudgeLineY = -4f;
        /// <summary>下落全程高度（生成点 → 判定线）。</summary>
        public const float FallHeight = 9f;
        /// <summary>低于此高度回收音符。</summary>
        public const float KillY = -7.5f;
        /// <summary>轨道数（lane 0–3）。</summary>
        public const int LaneCount = 4;

        private static Sprite squareSprite;

        private readonly List<NoteData> sortedNotes = new List<NoteData>();
        private readonly Dictionary<NoteData, NoteController> live = new Dictionary<NoteData, NoteController>();
        private ChartData chart;
        private Conductor conductor;
        private NotePool pool;
        private float travelTime = 1f;
        private int nextIndex;
        private GameObject playfieldRoot;

        /// <summary>计分系统（hold 尾判上报用，由 GameManager 注入）。</summary>
        internal ScoreSystem Score { get; set; }
        /// <summary>所属 GameManager（判定时钟与按住状态，由 GameManager 注入）。</summary>
        internal GameManager Game { get; set; }
        /// <summary>时钟。</summary>
        internal Conductor Conductor => conductor;
        /// <summary>下落时间（秒）。</summary>
        internal float TravelTime => travelTime;
        /// <summary>对象池。</summary>
        internal NotePool Pool => pool;

        /// <summary>当前存活音符数。</summary>
        public int LiveNoteCount => live.Count;

        /// <summary>轨道中心 X 坐标。</summary>
        public static float LaneX(int lane) => (lane - (LaneCount - 1) * 0.5f) * LaneWidth;

        /// <summary>生成点 Y 坐标。</summary>
        public static float SpawnY => JudgeLineY + FallHeight;

        /// <summary>共享 1x1 白色方块 sprite（代码生成）。</summary>
        internal static Sprite GetSquareSprite()
        {
            if (squareSprite == null)
            {
                var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false);
                texture.filterMode = FilterMode.Bilinear;
                var pixels = new Color32[texture.width * texture.height];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                squareSprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 8f);
            }
            return squareSprite;
        }

        /// <summary>装配谱面、时钟与对象池，并绘制判定线。</summary>
        public void Setup(ChartData chart, Conductor conductor, NotePool pool, float travelTimeSeconds)
        {
            this.chart = chart;
            this.conductor = conductor;
            this.pool = pool;
            travelTime = Mathf.Max(0.05f, travelTimeSeconds);
            sortedNotes.Clear();
            nextIndex = 0;
            live.Clear();
            if (chart != null && chart.Notes != null)
            {
                sortedNotes.AddRange(chart.Notes);
                sortedNotes.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
            BuildPlayfield();
        }

        private void Update()
        {
            if (chart == null || conductor == null) return;
            if (Game != null && !Game.IsPlaying) return; // 未开始/已停止不生成
            double now = conductor.SongTimeSeconds;
            while (nextIndex < sortedNotes.Count && sortedNotes[nextIndex].Time - now <= travelTime)
            {
                Spawn(sortedNotes[nextIndex]);
                nextIndex++;
            }
        }

        private void Spawn(NoteData data)
        {
            if (data == null || data.Lane < 0 || data.Lane >= LaneCount || pool == null) return;
            GameObject go = pool.Rent();
            if (go == null) return;
            go.SetActive(true);
            NoteController controller = go.GetComponent<NoteController>();
            if (controller == null) controller = go.AddComponent<NoteController>();
            controller.Initialize(data, this);
            live[data] = controller;
        }

        /// <summary>头判结果转发给对应音符控制器（表现）。</summary>
        public void NotifyJudged(NoteData note, Judgement judgement)
        {
            if (note != null && live.TryGetValue(note, out NoteController controller)) controller.OnHeadJudged(judgement);
        }

        /// <summary>自动 Miss 转发给对应音符控制器（变灰继续下落出屏）。</summary>
        public void NotifyMissed(NoteData note)
        {
            if (note != null && live.TryGetValue(note, out NoteController controller)) controller.OnMissed();
        }

        /// <summary>hold 尾判上报（由 NoteController 驱动调用）。</summary>
        internal void RegisterTail(NoteData note, Judgement judgement)
        {
            if (Score != null) Score.Register(judgement, true);
            if (Game != null) Game.NotifyJudgementFeedback(note, judgement, true);
        }

        /// <summary>音符结束（命中/漏过/出屏），归还对象池。</summary>
        internal void OnNoteFinished(NoteData note, NoteController controller)
        {
            live.Remove(note);
            if (pool != null) pool.Return(controller.gameObject);
        }

        /// <summary>强制回收全部存活音符（停止/切歌时）。</summary>
        public void ReleaseAll()
        {
            var controllers = new List<NoteController>(live.Values);
            live.Clear();
            foreach (NoteController controller in controllers) controller.ForceRelease();
        }

        // 绘制 4 条轨道参考线 + 1 条判定线
        private void BuildPlayfield()
        {
            if (playfieldRoot != null) Destroy(playfieldRoot);
            playfieldRoot = new GameObject("Playfield");
            playfieldRoot.transform.SetParent(transform, false);
            for (int lane = 0; lane < LaneCount; lane++)
            {
                CreateSprite("LaneGuide" + lane, playfieldRoot.transform,
                    new Vector3(LaneX(lane), (JudgeLineY + SpawnY) * 0.5f, 0f),
                    new Vector2(0.06f, FallHeight + 1.5f), new Color(1f, 1f, 1f, 0.16f), 0);
            }
            CreateSprite("JudgeLine", playfieldRoot.transform,
                new Vector3(0f, JudgeLineY, 0f),
                new Vector2(LaneWidth * LaneCount, 0.14f), Color.white, 1);
        }

        private static SpriteRenderer CreateSprite(string name, Transform parent, Vector3 localPosition, Vector2 size, Color color, int sortingOrder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = GetSquareSprite();
            renderer.color = color;
            renderer.sortingOrder = sortingOrder;
            return renderer;
        }
    }
}
