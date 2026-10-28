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
        private SpriteRenderer disc;   // 咚/咔圆盘（Roll 为气球）
        private GameObject rollBody;   // 连击条身体（剩余时长参考线）
        private SpriteRenderer rollBodyRenderer;
        private GameObject counterGo;  // 气球进度计数（仅 Roll）
        private TextMesh counter;
        private TextMesh counterShadow; // 数字黑投影层（键帽字母同款双层）
        private float rollLen;         // 连击条全长（世界单位）
        private float rollFill;
        private float speed;
        private float counterPunch;    // 气球数字跨档弹跳（30/60/90 判档线）
        private int counterZone;       // 当前判档（0=不足30 1=Bad 2=Good 3=Perfect）

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
            // 皮肤包贴图自带配色（不重染）；程序化圆盘/气球为灰阶底，按类型染色（黄=连击气球）
            disc.sprite = spawner.GetKindSprite(kind, out bool skinned);
            disc.color = skinned ? Color.white
                : kind == Kind.Don ? DonColor
                : kind == Kind.Katsu ? KatsuColor
                : RollColor;
            disc.transform.localScale = Vector3.one * (kind == Kind.Roll ? 0.34f : 0.5f);
            rollBody.SetActive(kind == Kind.Roll);
            counterGo.SetActive(kind == Kind.Roll);
            if (kind == Kind.Roll) { SetCounterText("0"); counterZone = 0; counterPunch = 0f; }
            if (kind == Kind.Roll)
            {
                rollLen = (float)note.Duration * speed;
                var rt = rollBody.transform;
                rt.localScale = new Vector3(rollLen, 0.42f, 1f);
                rt.localPosition = new Vector3(rollLen * 0.5f, 0f, 0f); // 条身从头向右延伸
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
            rollBodyRenderer = body;
            rollBody = bodyGo;

            // 气球进度计数：Impact 计分板字体（键帽/鼓垫同款）+ 黑投影双层，白字太素、Arial 太软
            counterGo = new GameObject("RollCounter");
            counterGo.transform.SetParent(transform, false);
            counterGo.transform.localPosition = new Vector3(0f, 0.05f, 0f);
            counter = CreateCounterText("Counter", 11);
            counter.color = new Color(0.42f, 0.28f, 0.08f, 0.96f); // 深棕字：黄气球上比白字清晰
            counterShadow = CreateCounterText("CounterShadow", 10);
            counterShadow.transform.localPosition = new Vector3(0.02f, -0.028f, 0f);
            counterShadow.color = new Color(0f, 0f, 0f, 0.55f);
            counterGo.SetActive(false);
        }

        private TextMesh CreateCounterText(string name, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(counterGo.transform, false);
            var tm = go.AddComponent<TextMesh>();
            tm.font = NoteSpawner.GetKeycapFont();
            tm.fontSize = 48;
            tm.characterSize = 0.085f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = tm.font.material;
            renderer.sortingOrder = order;
            return tm;
        }

        // 主字与投影同步设值（防两频闪不对称）
        private void SetCounterText(string value)
        {
            if (counter.text != value) counter.text = value;
            if (counterShadow.text != value) counterShadow.text = value;
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
                    UpdateRollVisuals();
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
                UpdateRollVisuals();
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

        // 连击条按填充比例判尾（≥0.9 Perfect / ≥0.6 Good / ≥0.3 Bad / 其余 Miss）；吹起来爆掉，没吹起来泄气掉落
        private void JudgeRoll()
        {
            Judgement j = rollFill >= 0.9f ? Judgement.Perfect
                : rollFill >= 0.6f ? Judgement.Good
                : rollFill >= 0.3f ? Judgement.Bad
                : Judgement.Miss;
            spawner.RegisterTail(data, j);
            state = State.Done;
            StartCoroutine(rollFill >= 0.3f ? PopRoutine() : DeflateRoutine());
        }

        private void PopAndFinish()
        {
            if (state == State.Done) return;
            state = State.Done;
            StartCoroutine(PopRoutine());
        }

        private System.Collections.IEnumerator PopRoutine()
        {
            Vector3 startScale = disc.transform.localScale;
            float t = 0f;
            while (t < 0.18f)
            {
                t += Time.deltaTime;
                float k = t / 0.18f;
                disc.transform.localScale = startScale * (1f + 1.1f * k);
                var c = disc.color;
                disc.color = new Color(c.r, c.g, c.b, 1f - k);
                if (counterGo.activeSelf)
                {
                    var cc = counter.color;
                    counter.color = new Color(cc.r, cc.g, cc.b, 1f - k);
                    var sc = counterShadow.color;
                    counterShadow.color = new Color(sc.r, sc.g, sc.b, sc.a * (1f - k));
                }
                yield return null;
            }
            spawner.OnNoteFinished(data, this);
        }

        /// <summary>气球没吹起来：灰化、泄气缩小并掉落淡出后回收。</summary>
        private System.Collections.IEnumerator DeflateRoutine()
        {
            Vector3 startScale = disc.transform.localScale;
            Vector3 pos = transform.position;
            var c0 = disc.color;
            float t = 0f;
            while (t < 0.28f)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / 0.28f);
                disc.transform.localScale = startScale * (1f - 0.55f * k);
                transform.position = pos + new Vector3(0f, -0.9f * k * k, 0f);
                disc.color = new Color(c0.r * (1f - 0.4f * k), c0.g * (1f - 0.4f * k), c0.b * (1f - 0.4f * k), 1f - k);
                if (counterGo.activeSelf)
                {
                    var cc = counter.color;
                    counter.color = new Color(cc.r, cc.g, cc.b, 1f - k);
                    var sc = counterShadow.color;
                    counterShadow.color = new Color(sc.r, sc.g, sc.b, sc.a * (1f - k));
                }
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

        /// <summary>连击（气球）进行中的可视化：气球随填充充气、白字百分比计数、黄带=剩余时长、亮度=已累计。</summary>
        private void UpdateRollVisuals()
        {
            bool held = spawner.Game != null && spawner.Game.IsLaneHeld(2);

            string pctText = Mathf.RoundToInt(rollFill * 100f).ToString();
            SetCounterText(pctText);

            // 数字动态：跨 30/60/90 判档线 → 弹跳+白闪+气球本体鼓一下；按住时随打气节奏脉动
            int zone = rollFill >= 0.9f ? 3 : rollFill >= 0.6f ? 2 : rollFill >= 0.3f ? 1 : 0;
            if (zone != counterZone)
            {
                counterZone = zone;
                counterPunch = 1f;
            }
            counterPunch = Mathf.Max(0f, counterPunch - Time.deltaTime * 3.5f);
            float pump = held ? 0.5f + 0.5f * Mathf.Sin(Time.time * 14f) : 0f;
            float s = 1f + 0.06f * pump + 0.45f * counterPunch;
            counterGo.transform.localScale = new Vector3(s, s, 1f);

            // 数字颜色随判档推进：深棕 → 琥珀 → 橙红（90+ 最热），跨档瞬间向白闪
            Color zoneColor;
            switch (counterZone)
            {
                case 3: zoneColor = new Color(0.85f, 0.22f, 0.05f); break;
                case 2: zoneColor = new Color(0.72f, 0.40f, 0.05f); break;
                case 1: zoneColor = new Color(0.52f, 0.30f, 0.07f); break;
                default: zoneColor = new Color(0.42f, 0.28f, 0.08f); break;
            }
            float flash = counterPunch * 0.7f;
            counter.color = new Color(
                zoneColor.r + (1f - zoneColor.r) * flash,
                zoneColor.g + (1f - zoneColor.g) * flash,
                zoneColor.b + (1f - zoneColor.b) * flash, 0.96f);

            // 气球本体：充气放大 + 跨档同步微鼓
            disc.transform.localScale = Vector3.one * (0.34f + 0.28f * rollFill) * (1f + 0.10f * counterPunch);

            float remain = 1f;
            if (data.Duration > 0.0 && spawner.Game != null && spawner.Game.Conductor != null)
                remain = Mathf.Clamp01(1f - (float)((spawner.Game.Conductor.SongTimeSeconds - data.Time) / data.Duration));
            float len = Mathf.Max(0.02f, rollLen * remain);
            var rt = rollBody.transform;
            rt.localScale = new Vector3(len, 0.42f, 1f);
            rt.localPosition = new Vector3(len * 0.5f, 0f, 0f);
            if (rollBodyRenderer != null)
            {
                var c = rollBodyRenderer.color;
                rollBodyRenderer.color = new Color(c.r, c.g, c.b, 0.30f + 0.50f * rollFill);
            }
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
