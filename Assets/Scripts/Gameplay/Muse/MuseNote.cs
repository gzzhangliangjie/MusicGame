using MusicGame.Pure.Model;
using UnityEngine;

namespace MusicGame.Gameplay.Muse
{
    /// <summary>
    /// 跑酷模式敌人（横向卷动）：地面怪(粉)/空中怪(青)命中即爆，长条怪按住对应键充气、按比例判尾。
    /// 同刻跨轨双押由 Spawner 标记为 harmony，附金色描边。玩法参考 Muse Dash，实现为独立 Unity 代码。
    /// </summary>
    public class MuseNote : MonoBehaviour
    {
        private enum Kind { Ground, Air, Bar }
        private enum State { Moving, Holding, Dead, Done }

        private static readonly Color MissedColor = new Color(0.5f, 0.5f, 0.5f, 0.45f);

        private NoteData data;
        private MuseSpawner spawner;
        private State state;
        private Kind kind;
        private Transform head;              // 敌人本体（爆裂/充气缩放只作用于此，不带动长条身体）
        private Vector3 headBaseScale;       // 装配后的本体基准缩放（金币旋转/长条充气基于此）
        private SpriteRenderer headRenderer;
        private SpriteRenderer ringRenderer; // harmony 描边（随本体同淡出/灰化）
        private GameObject barBody;          // 长条身体（从头向右延伸的剩余时长参考带）
        private SpriteRenderer barBodyRenderer;
        private Coroutine animHandle;        // 命中/失败动画协程（池复用前强制停止，防串局）
        private float speed;
        private float barLen;                // 长条全长（世界单位）
        private float barFill;

        /// <summary>卷动速度（世界单位/秒，向左为负）。</summary>
        private float Vx => -speed;

        /// <summary>从对象池租出后初始化。note.Lane 已重映射：0=地面 1=空中；hold=长条怪（轨道不变）。</summary>
        internal void Initialize(NoteData note, MuseSpawner owner)
        {
            if (animHandle != null) { StopCoroutine(animHandle); animHandle = null; } // 池复用：掐断上一局未完动画
            data = note;
            spawner = owner;
            state = State.Moving;
            bool hold = note.Type == "hold" && note.Duration > 0.0;
            kind = hold ? Kind.Bar : (note.Lane == 0 ? Kind.Ground : Kind.Air);
            barFill = 0f;
            speed = (MuseSpawner.SpawnX - MuseSpawner.CharacterX) / Mathf.Max(0.05f, spawner.TravelTime);

            EnsureVisuals();
            float targetHeight = kind == Kind.Bar ? 0.66f : kind == Kind.Air ? 0.8f : 0.85f;
            spawner.FitEnemy(headRenderer, note.Lane, targetHeight); // 装配贴图（皮肤优先）并按高度等比缩放
            headBaseScale = head.localScale;
            head.localPosition = Vector3.zero;
            head.localRotation = Quaternion.identity;
            headRenderer.color = Color.white;
            if (ringRenderer != null)
            {
                ringRenderer.gameObject.SetActive(spawner.IsHarmony(data));
                ringRenderer.color = new Color(1f, 1f, 1f, 0.92f); // 白描边：金金币/棕蘑菇上都读得出"特殊"
            }

            barBody.SetActive(kind == Kind.Bar);
            if (kind == Kind.Bar)
            {
                barLen = Mathf.Max(0.05f, (float)note.Duration * speed);
                var rt = barBody.transform;
                rt.localScale = new Vector3(barLen, 0.34f, 1f);
                rt.localPosition = new Vector3(barLen * 0.5f, 0f, 0f); // 条身从头向右延伸
            }
            transform.position = new Vector3(MuseSpawner.SpawnX, note.Lane == 0 ? MuseSpawner.GroundY : MuseSpawner.AirY, 0f);
        }

        private void EnsureVisuals()
        {
            if (head != null) return;
            var headGo = new GameObject("Head");
            headGo.transform.SetParent(transform, false);
            head = headGo.transform;
            headRenderer = headGo.AddComponent<SpriteRenderer>();
            headRenderer.sortingOrder = 10;

            // harmony 白描边（挂在 Head 下随本体一起缩放）
            var ringGo = new GameObject("HarmonyRing");
            ringGo.transform.SetParent(headGo.transform, false);
            ringRenderer = ringGo.AddComponent<SpriteRenderer>();
            ringRenderer.sprite = MuseSpawner.GetRingSprite();
            ringRenderer.sortingOrder = 9;
            ringRenderer.color = new Color(1f, 1f, 1f, 0.92f);
            ringRenderer.transform.localScale = Vector3.one * 1.18f;
            ringRenderer.gameObject.SetActive(spawner != null && spawner.IsHarmony(data));

            var bodyGo = new GameObject("BarBody");
            bodyGo.transform.SetParent(transform, false);
            var body = bodyGo.AddComponent<SpriteRenderer>();
            body.sprite = Taiko.TaikoSpawner.GetRollStripSprite();
            body.sortingOrder = 8;
            body.color = new Color(0.95f, 0.80f, 0.25f, 0.50f);
            barBodyRenderer = body;
            barBody = bodyGo;
        }

        private void Update()
        {
            if (data == null || spawner == null || state == State.Done) return;
            float dt = Time.deltaTime;
            var pos = transform.position;
            pos.x += Vx * dt;

            switch (state)
            {
                case State.Moving:
                    transform.position = pos;
                    // 地面/空中怪：滚过角色左侧即 Miss；长条怪：头部到角色处就停住等待响应
                    if (kind != Kind.Bar && pos.x < MuseSpawner.CharacterX - 0.6f) Miss();
                    else if (kind == Kind.Bar && pos.x <= MuseSpawner.CharacterX) pos.x = MuseSpawner.CharacterX;
                    break;

                case State.Holding:
                    // 头未到角色处继续滚，到了钉住；按住对应键累计充气，到时判尾
                    if (pos.x > MuseSpawner.CharacterX) transform.position = pos;
                    else transform.position = new Vector3(MuseSpawner.CharacterX, transform.position.y, 0f);
                    bool held = spawner.Game != null && spawner.Game.IsLaneHeld(data.Lane);
                    if (held) barFill = Mathf.Min(1f, barFill + dt / Mathf.Max(0.05f, (float)data.Duration));
                    UpdateBarVisuals();
                    if (spawner.Game != null && spawner.Game.Conductor != null
                        && spawner.Game.Conductor.SongTimeSeconds > data.Time + data.Duration)
                        JudgeBar();
                    break;

                case State.Dead:
                    transform.position = pos;
                    if (pos.x < MuseSpawner.SpawnX - 24f) Finish();
                    break;
            }

            // 原版细节还原：蘑菇仔走路 = 左右镜像翻面（SMB1 每 8 帧翻转一次，这里 0.2s）；
            // 金币绕竖轴旋转（scale.x 余弦摆动，侧面压扁留 0.2 倍防消失）
            if (kind == Kind.Ground && state != State.Done && headBaseScale.sqrMagnitude > 0f)
            {
                float flip = (Mathf.FloorToInt(Time.time / 0.2f) & 1) == 0 ? 1f : -1f;
                head.localScale = new Vector3(headBaseScale.x * flip, headBaseScale.y, 1f);
            }
            else if (kind == Kind.Air && state != State.Done && headBaseScale.sqrMagnitude > 0f)
            {
                float spin = Mathf.Abs(Mathf.Cos(Time.time * 5f + (float)data.Time * 3.1f));
                head.localScale = new Vector3(
                    Mathf.Max(0.2f, spin) * headBaseScale.x, headBaseScale.y, 1f);
            }
        }

        /// <summary>头判回调：怪命中爆闪回收；长条怪登记头判进入 Holding。</summary>
        internal void OnHeadJudged(Judgement judgement)
        {
            if (state != State.Moving) return;
            if (kind == Kind.Bar)
            {
                state = State.Holding;
                UpdateBarVisuals();
                return;
            }
            PopAndFinish();
        }

        /// <summary>超窗 Miss 回调：长条怪（无论头判与否）立即按当前充气比例判尾（头未判即 0 充气 → 尾 Miss），
        /// 保证 hold=头+尾两次判定口径与四轨一致；普通怪灰化穿场。</summary>
        internal void OnMissed()
        {
            if (kind == Kind.Bar && state != State.Done) { JudgeBar(); return; }
            if (state == State.Done) return;
            spawner.NotifyEnemyPassed();
            Miss();
        }

        // 长条按充气比例判尾（≥0.9 Perfect / ≥0.6 Good / ≥0.3 Bad / 其余 Miss）；充满爆掉，没充满泄气掉落
        private void JudgeBar()
        {
            Judgement j = barFill >= 0.9f ? Judgement.Perfect
                : barFill >= 0.6f ? Judgement.Good
                : barFill >= 0.3f ? Judgement.Bad
                : Judgement.Miss;
            spawner.RegisterTail(data, j);
            state = State.Done;
            animHandle = StartCoroutine(barFill >= 0.3f ? PopRoutine() : DeflateRoutine());
        }

        private void PopAndFinish()
        {
            if (state == State.Done) return;
            state = State.Done;
            // 分类型命中动画：金币=向上抛起旋转（顶块经典），蘑菇仔=原版踩扁帧压扁淡出，长条=充气爆掉
            if (kind == Kind.Air) animHandle = StartCoroutine(CoinCollectedRoutine());
            else if (kind == Kind.Ground) animHandle = StartCoroutine(GoombaStompedRoutine());
            else animHandle = StartCoroutine(PopRoutine());
        }

        /// <summary>金币被吃：向上抛起（减速）+ 快速旋转 + 末段淡出——经典"顶 ? 块金币"动画。</summary>
        private System.Collections.IEnumerator CoinCollectedRoutine()
        {
            Vector3 baseScale = headBaseScale;
            Vector3 basePos = head.localPosition;
            const float Dur = 0.30f;
            float t = 0f;
            while (t < Dur)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / Dur);
                float ease = 1f - (1f - k) * (1f - k); // 减速上抛
                head.localPosition = basePos + new Vector3(0f, 1.15f * ease, 0f);
                head.localScale = new Vector3(
                    Mathf.Max(0.15f, Mathf.Abs(Mathf.Sin(k * 21f))) * baseScale.x, baseScale.y, 1f);
                SetHeadAlpha(k > 0.55f ? 1f - (k - 0.55f) / 0.45f : 1f);
                yield return null;
            }
            spawner.OnNoteFinished(data, this);
        }

        /// <summary>蘑菇仔被抓：压扁（原版踩扁帧）→ 停顿 → 淡出——经典"踩栗子怪"动画。</summary>
        private System.Collections.IEnumerator GoombaStompedRoutine()
        {
            if (MuseSmbSkin.Available) headRenderer.sprite = MuseSmbSkin.GoombaFlat(); // 原版压扁帧（回退模式仅压缩变形）
            Vector3 baseScale = head.localScale;
            const float SquashDur = 0.10f, HoldDur = 0.18f, FadeDur = 0.16f;
            float t = 0f;
            while (t < SquashDur)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / SquashDur);
                head.localScale = new Vector3(baseScale.x * (1f + 0.18f * k), baseScale.y * (1f - 0.72f * k), 1f);
                yield return null;
            }
            head.localScale = new Vector3(baseScale.x * 1.18f, baseScale.y * 0.28f, 1f);
            yield return new WaitForSecondsRealtime(HoldDur);
            t = 0f;
            while (t < FadeDur)
            {
                t += Time.deltaTime;
                SetHeadAlpha(1f - Mathf.Clamp01(t / FadeDur));
                yield return null;
            }
            spawner.OnNoteFinished(data, this);
        }

        /// <summary>本体与 harmony 描边统一设透明度。</summary>
        private void SetHeadAlpha(float a)
        {
            a = Mathf.Clamp01(a);
            var c = headRenderer.color;
            headRenderer.color = new Color(c.r, c.g, c.b, a);
            if (ringRenderer != null && ringRenderer.gameObject.activeSelf)
            {
                var rc = ringRenderer.color;
                ringRenderer.color = new Color(rc.r, rc.g, rc.b, a * 0.92f);
            }
        }

        private System.Collections.IEnumerator PopRoutine()
        {
            Vector3 startScale = head.localScale;
            float t = 0f;
            while (t < 0.18f)
            {
                t += Time.deltaTime;
                float k = t / 0.18f;
                head.localScale = startScale * (1f + 1.1f * k);
                var c = headRenderer.color;
                headRenderer.color = new Color(c.r, c.g, c.b, 1f - k);
                if (ringRenderer != null && ringRenderer.gameObject.activeSelf)
                {
                    var rc = ringRenderer.color;
                    ringRenderer.color = new Color(rc.r, rc.g, rc.b, rc.a * (1f - k));
                }
                if (barBody.activeSelf)
                {
                    var cc = barBodyRenderer.color;
                    barBodyRenderer.color = new Color(cc.r, cc.g, cc.b, cc.a * (1f - k * 0.4f));
                }
                yield return null;
            }
            spawner.OnNoteFinished(data, this);
        }

        /// <summary>长条没充满：灰化、泄气缩小并掉落淡出后回收。</summary>
        private System.Collections.IEnumerator DeflateRoutine()
        {
            Vector3 startScale = head.localScale;
            Vector3 pos = transform.position;
            var c0 = headRenderer.color;
            float t = 0f;
            while (t < 0.28f)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / 0.28f);
                head.localScale = startScale * (1f - 0.55f * k);
                transform.position = pos + new Vector3(0f, -0.9f * k * k, 0f);
                headRenderer.color = new Color(c0.r * (1f - 0.4f * k), c0.g * (1f - 0.4f * k), c0.b * (1f - 0.4f * k), 1f - k);
                var cc = barBodyRenderer.color;
                barBodyRenderer.color = new Color(cc.r, cc.g, cc.b, cc.a * (1f - k));
                yield return null;
            }
            spawner.OnNoteFinished(data, this);
        }

        private void Miss()
        {
            if (state == State.Done || state == State.Dead) return;
            state = State.Dead;
            headRenderer.color = MissedColor;
            if (ringRenderer != null && ringRenderer.gameObject.activeSelf)
                ringRenderer.color = new Color(MissedColor.r, MissedColor.g, MissedColor.b, MissedColor.a);
        }

        // 长条充气中的可视化：头部随填充鼓起 + 条身随剩余时长缩短、亮度随填充提高
        private void UpdateBarVisuals()
        {
            // 等比鼓起：头部贴图宽高比在装配时已定，按统一高度系数缩放
            float aspect = 1f;
            if (headRenderer.sprite != null)
            {
                var s = headRenderer.sprite.bounds.size;
                aspect = Mathf.Max(0.01f, s.x / Mathf.Max(0.01f, s.y));
            }
            float hgt = 0.66f + 0.30f * barFill;
            head.localScale = new Vector3(hgt * aspect, hgt, 1f);

            float remain = 1f;
            if (data.Duration > 0.0 && spawner.Game != null && spawner.Game.Conductor != null)
                remain = Mathf.Clamp01(1f - (float)((spawner.Game.Conductor.SongTimeSeconds - data.Time) / data.Duration));
            float len = Mathf.Max(0.02f, barLen * remain);
            var rt = barBody.transform;
            rt.localScale = new Vector3(len, 0.34f, 1f);
            rt.localPosition = new Vector3(len * 0.5f, 0f, 0f);
            if (barBodyRenderer != null)
            {
                var c = barBodyRenderer.color;
                barBodyRenderer.color = new Color(c.r, c.g, c.b, 0.30f + 0.50f * barFill);
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
