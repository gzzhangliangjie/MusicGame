using MusicGame.Pure.Model;
using UnityEngine;

namespace MusicGame.Gameplay.Taiko
{
    /// <summary>
    /// 太鼓音符（横向卷动）：咚(红)/咔(蓝)圆盘命中即爆，连击条(黄)按住任意鼓累计填充。
    /// 玩法与外观参考 osu!lazer Taiko ruleset / OpenTaiko（均为 MIT），实现为独立 Unity 代码。
    /// </summary>
    public class TaikoNote : MonoBehaviour
    {
        internal enum Kind { Don, Katsu, Roll }
        private enum State { Falling, Rolling, Dead, Done }

        private static readonly Color DonColor = new Color(0.92f, 0.28f, 0.24f);
        private static readonly Color KatsuColor = new Color(0.25f, 0.65f, 0.95f);
        private static readonly Color RollColor = new Color(0.95f, 0.80f, 0.25f);
        private static readonly Color MissedColor = new Color(0.5f, 0.5f, 0.5f, 0.45f);

        private NoteData data;
        private TaikoSpawner spawner;
        private State state;
        private Kind kind;
        private SpriteRenderer disc;   // 咚/咔圆盘（Roll 为条带头）
        private GameObject rollBody;   // 连击条身体
        private float rollFill;
        private float speed;
        private Color baseDiscColor;

        /// <summary>卷动速度（世界单位/秒，向左为负）。</summary>
        private float Vx => -speed;

        /// <summary>从对象池租出后初始化。note.Lane 已重映射：0=咚 1=咔 2=连击。</summary>
        internal void Initialize(NoteData note, TaikoSpawner owner)
        {
            data = note;
            spawner = owner;
            state = State.Falling;
            kind = note.Lane == 0 ? Kind.Don : note.Lane == 1 ? Kind.Katsu : Kind.Roll;
            rollFill = 0f;
            speed = (TaikoSpawner.SpawnX - TaikoSpawner.HitX) / Mathf.Max(0.05f, spawner.TravelTime);

            EnsureVisuals();
            // 皮肤包贴图自带配色（不重染）；程序化圆盘按类型着色
            disc.sprite = spawner.GetKindSprite(kind, out bool skinned);
            disc.color = skinned ? Color.white
                : kind == Kind.Don ? DonColor
                : kind == Kind.Katsu ? KatsuColor
                : RollColor;
            baseDiscColor = disc.color;
            disc.transform.localScale = Vector3.one * (kind == Kind.Roll ? 0.42f : 0.5f);
            rollBody.SetActive(kind == Kind.Roll);
            if (kind == Kind.Roll)
            {
                float len = (float)note.Duration * speed;
                var rt = rollBody.transform;
                rt.localScale = new Vector3(len, 0.42f, 1f);
                rt.localPosition = new Vector3(len * 0.5f, 0f, 0f); // 条身从头向右延伸
            }
            transform.position = new Vector3(TaikoSpawner.SpawnX, TaikoSpawner.NoteY, 0f);
        }

        private void EnsureVisuals()
        {
            if (disc != null) return;
            disc = GetComponent<SpriteRenderer>();
            if (disc == null) disc = gameObject.AddComponent<SpriteRenderer>();
            disc.sprite = TaikoSpawner.GetDiscSprite();
            disc.sortingOrder = 10;
            var bodyGo = new GameObject("RollBody");
            bodyGo.transform.SetParent(transform, false);
            var body = bodyGo.AddComponent<SpriteRenderer>();
            body.sprite = TaikoSpawner.GetRollStripSprite();
            body.sortingOrder = 9;
            body.color = new Color(RollColor.r, RollColor.g, RollColor.b, 0.55f);
            rollBody = bodyGo;
        }

        private void Update()
        {
            if (data == null || spawner == null || state == State.Done) return;
            float dt = Time.deltaTime;
            var pos = transform.position;
            pos.x += Vx * dt;

            switch (state)
            {
                case State.Falling:
                    transform.position = pos;
                    // 咚/咔：越过鼓面左侧即 Miss；连击条：头部到鼓面就停住开始响应
                    if (kind != Kind.Roll && pos.x < TaikoSpawner.HitX - 0.6f) Miss();
                    else if (kind == Kind.Roll && pos.x <= TaikoSpawner.HitX) pos.x = TaikoSpawner.HitX;
                    break;

                case State.Rolling:
                    transform.position = new Vector3(TaikoSpawner.HitX, TaikoSpawner.NoteY, 0f);
                    bool held = spawner.Game != null && spawner.Game.IsLaneHeld(2);
                    if (held) rollFill = Mathf.Min(1f, rollFill + dt / Mathf.Max(0.05f, (float)data.Duration));
                    SetRollIntensity(0.45f + 0.55f * rollFill);
                    if (spawner.Game != null && spawner.Game.Conductor != null
                        && spawner.Game.Conductor.SongTimeSeconds > data.Time + data.Duration)
                        JudgeRoll();
                    break;

                case State.Dead:
                    transform.position = pos;
                    if (pos.x < TaikoSpawner.SpawnX - 24f) Finish();
                    break;
            }
        }

        /// <summary>头判回调（咚/咔命中爆闪回收；连击条登记头判进入 Rolling）。</summary>
        internal void OnHeadJudged(Judgement judgement)
        {
            if (state != State.Falling) return;
            if (kind == Kind.Roll)
            {
                state = State.Rolling;
                SetRollIntensity(0.7f);
                return;
            }
            PopAndFinish();
        }

        /// <summary>超窗 Miss 回调。</summary>
        internal void OnMissed()
        {
            if (state == State.Rolling) { JudgeRoll(); return; }
            if (state == State.Done) return;
            Miss();
        }

        // 连击条按填充比例判尾（≥0.9 Perfect / ≥0.6 Good / ≥0.3 Bad / 其余 Miss）
        private void JudgeRoll()
        {
            Judgement j = rollFill >= 0.9f ? Judgement.Perfect
                : rollFill >= 0.6f ? Judgement.Good
                : rollFill >= 0.3f ? Judgement.Bad
                : Judgement.Miss;
            spawner.RegisterTail(data, j);
            Finish();
        }

        private void PopAndFinish()
        {
            if (state == State.Done) return;
            state = State.Done;
            StartCoroutine(PopRoutine());
        }

        private System.Collections.IEnumerator PopRoutine()
        {
            float t = 0f;
            while (t < 0.18f)
            {
                t += Time.deltaTime;
                float k = t / 0.18f;
                disc.transform.localScale = Vector3.one * (0.5f + 0.55f * k);
                var c = disc.color;
                disc.color = new Color(c.r, c.g, c.b, 1f - k);
                yield return null;
            }
            spawner.OnNoteFinished(data, this);
        }

        private void Miss()
        {
            if (state == State.Done || state == State.Dead) return;
            state = State.Dead;
            disc.color = MissedColor;
        }

        private void SetRollIntensity(float a)
        {
            disc.color = new Color(baseDiscColor.r, baseDiscColor.g, baseDiscColor.b, Mathf.Clamp01(a));
        }

        /// <summary>强制回收（停止/切歌）。</summary>
        internal void ForceRelease()
        {
            if (state == State.Done) return;
            state = State.Done;
            if (spawner != null && spawner.Pool != null) spawner.Pool.Return(gameObject);
        }

        private void Finish()
        {
            if (state == State.Done) return;
            state = State.Done;
            spawner.OnNoteFinished(data, this);
        }
    }
}
