using MusicGame.Pure.Model;
using UnityEngine;

namespace MusicGame.Gameplay
{
    /// <summary>
    /// 单个音符：下落表现（tap/hold 白色方块，hold 用蓝色身体区分）、
    /// hold 头/尾判驱动（头判结果由 JudgeSystem 经 NoteSpawner 回调进来，尾判在本类结算）。
    /// </summary>
    public class NoteController : MonoBehaviour
    {
        private enum State { Falling, Holding, Dead, Done }

        private static readonly Color TapColor = Color.white;
        private static readonly Color HoldHeadColor = new Color(0.40f, 0.85f, 1.00f);
        private static readonly Color HoldBodyColor = new Color(0.25f, 0.60f, 1.00f, 0.85f);
        private static readonly Color MissedColor = new Color(0.55f, 0.55f, 0.55f, 0.45f);

        private NoteData data;
        private NoteSpawner spawner;
        private State state;
        private bool isHold;
        private SpriteRenderer head;
        private SpriteRenderer body;

        /// <summary>对应谱面音符数据。</summary>
        public NoteData Data => data;

        /// <summary>下落速度（世界单位/秒）。</summary>
        private float UnitsPerSecond => NoteSpawner.FallHeight / Mathf.Max(0.05f, spawner != null ? spawner.TravelTime : 1f);

        /// <summary>从对象池租出后初始化。</summary>
        internal void Initialize(NoteData note, NoteSpawner owner)
        {
            data = note;
            spawner = owner;
            state = State.Falling;
            isHold = string.Equals(note.Type, "hold", System.StringComparison.OrdinalIgnoreCase) && note.Duration > 0.0;
            EnsureVisuals();
            head.color = isHold ? HoldHeadColor : TapColor;
            body.color = HoldBodyColor;
            transform.localScale = Vector3.one;
            transform.position = new Vector3(NoteSpawner.LaneX(note.Lane), NoteSpawner.SpawnY, 0f);
            if (isHold) SetBodyLength((float)(note.Duration * UnitsPerSecond));
            body.gameObject.SetActive(isHold);
        }

        private void EnsureVisuals()
        {
            if (head != null) return;
            head = GetComponent<SpriteRenderer>();
            if (head == null) head = gameObject.AddComponent<SpriteRenderer>();
            head.sprite = NoteSpawner.GetSquareSprite();
            head.sortingOrder = 10;
            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(transform, false);
            body = bodyGo.AddComponent<SpriteRenderer>();
            body.sprite = NoteSpawner.GetSquareSprite();
            body.sortingOrder = 9;
        }

        // hold 身体：底端贴着头顶向上延伸，长度随剩余时长收缩
        private void SetBodyLength(float length)
        {
            length = Mathf.Max(0f, length);
            body.transform.localScale = new Vector3(0.92f, length, 1f);
            body.transform.localPosition = new Vector3(0f, 0.5f + length * 0.5f, 0f);
        }

        private void Update()
        {
            if (data == null || spawner == null || state == State.Done) return;
            if (spawner.Game != null && spawner.Game.IsPaused) return; // 暂停时冻结表现与 hold 尾判
            // 判定/表现统一使用扣除用户校准偏移后的时钟
            double now = spawner.Game != null ? spawner.Game.JudgedTime : spawner.Conductor.SongTimeSeconds;
            float y = NoteSpawner.JudgeLineY + (float)((data.Time - now) / spawner.TravelTime) * NoteSpawner.FallHeight;

            switch (state)
            {
                case State.Falling:
                    transform.position = new Vector3(transform.position.x, y, 0f);
                    if (y < NoteSpawner.KillY) Finish();
                    break;

                case State.Holding:
                    {
                        double tailTime = data.Time + data.Duration;
                        double delta = now - tailTime; // 负值 = 早松
                        transform.position = new Vector3(transform.position.x, NoteSpawner.JudgeLineY, 0f);
                        SetBodyLength((float)(tailTime - now) * UnitsPerSecond);
                        bool held = spawner.Game != null && spawner.Game.IsLaneHeld(data.Lane);
                        if (!held) JudgeTail(delta);                      // 松手：按与尾时刻的差值判
                        else if (delta > JudgeSystem.GreatWindowSeconds) JudgeTail(0.0); // 按住到底：Perfect
                        break;
                    }

                case State.Dead: // 已 Miss：变灰继续下坠出屏
                    transform.position = new Vector3(transform.position.x, y, 0f);
                    if (y < NoteSpawner.KillY) Finish();
                    break;
            }
        }

        /// <summary>头判回调（来自 JudgeSystem，经 NoteSpawner 转发）。</summary>
        internal void OnHeadJudged(Judgement judgement)
        {
            if (state != State.Falling) return;
            if (isHold)
            {
                state = State.Holding;
                head.color = Color.white; // 头命中高亮
            }
            else
            {
                Finish(); // tap 直接回收
            }
        }

        /// <summary>超窗自动 Miss 回调。</summary>
        internal void OnMissed()
        {
            if (state == State.Done) return;
            if (state == State.Holding)
            {
                // hold 进行中被判 Miss（异常兜底）：尾判直接 Miss 并回收
                spawner.RegisterTail(data, Judgement.Miss);
                Finish();
                return;
            }
            state = State.Dead;
            head.color = MissedColor;
            body.color = MissedColor;
            if (isHold) spawner.RegisterTail(data, Judgement.Miss); // 漏头即丢尾：补记尾判 Miss，对齐 hold=头+尾两次判定口径
        }

        /// <summary>强制回收（停止/切歌，由 ReleaseAll 调用）。</summary>
        internal void ForceRelease()
        {
            if (state == State.Done) return;
            state = State.Done;
            if (spawner != null && spawner.Pool != null) spawner.Pool.Return(gameObject);
        }

        private void JudgeTail(double deltaSeconds)
        {
            double abs = System.Math.Abs(deltaSeconds);
            Judgement j = abs <= JudgeSystem.PerfectWindowSeconds ? Judgement.Perfect
                : abs <= JudgeSystem.GreatWindowSeconds ? Judgement.Great
                : Judgement.Miss;
            spawner.RegisterTail(data, j);
            Finish();
        }

        private void Finish()
        {
            if (state == State.Done) return;
            state = State.Done;
            spawner.OnNoteFinished(data, this);
        }
    }
}
