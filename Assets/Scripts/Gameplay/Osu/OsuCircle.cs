using System.Collections.Generic;
using UnityEngine;

namespace MusicGame.Gameplay.Osu
{
    /// <summary>
    /// 单个缩圈圆圈：圈体（连击组色）+ 缩圈环（3x 收拢到 1x 即判定点）+ 圈上数字。
    /// 淡入 → 等待判定（GameManager 经 OsuSpawner 调度）→ 命中爆闪 / Miss 灰化，动画毕归还对象池。
    /// </summary>
    public class OsuCircle : MonoBehaviour
    {
        private enum State { Idle, Hit, Missed }

        private static Sprite circleSprite, ringSprite;
        private static Font keycapFont;

        private OsuSpawner spawner;
        private double time;
        private Color baseColor;
        private SpriteRenderer body, ring;
        private TextMesh number, numberShadow;
        private State state;
        private bool consumed;
        private float numberFadeAge = -1f; // ≥0：数字渐隐中（更新的圈出现后，旧圈数字逐渐变透明）

        /// <summary>是否已被判定消费（命中或 Miss，供 TryHit/TryMiss 跳过）。</summary>
        internal bool Consumed
        {
            get { return consumed; }
        }

        internal void Consume() { consumed = true; }

    /// <summary>更新的圈已出现：本圈数字从现在起渐隐（时长≈缩圈时长的一半），圈体保留。</summary>
    internal void FadeOutNumber()
    {
        if (numberFadeAge < 0f) numberFadeAge = 0f;
    }

        internal void Initialize(double hitTime, int numberValue, int colorIndex, OsuSpawner owner)
        {
            spawner = owner;
            time = hitTime;
            state = State.Idle;
            consumed = false;

            EnsureVisuals();
            baseColor = OsuSpawner.GroupColors[Mathf.Clamp(colorIndex, 0, 3)];
            body.color = new Color(baseColor.r, baseColor.g, baseColor.b, 0f);
            ring.color = new Color(baseColor.r, baseColor.g, baseColor.b, 0f);
            number.text = numberValue.ToString();
            numberShadow.text = numberValue.ToString();
            ApplyNumberCentering();
            number.color = new Color(1f, 1f, 1f, 0f);
            numberShadow.color = new Color(0f, 0f, 0f, 0f);

            transform.localScale = Vector3.one * OsuSpawner.CircleRadius * 2f;
            transform.rotation = Quaternion.identity;
        }

        private void EnsureVisuals()
        {
            if (body != null) return;
            body = GetComponent<SpriteRenderer>();
            if (body == null) body = gameObject.AddComponent<SpriteRenderer>();
            if (circleSprite == null) circleSprite = BuildCircleSprite();
            body.sprite = circleSprite;
            body.sortingOrder = 10;

            var ringGo = new GameObject("Ring");
            ringGo.transform.SetParent(transform, false);
            ring = ringGo.AddComponent<SpriteRenderer>();
            if (ringSprite == null) ringSprite = BuildRingSprite();
            ring.sprite = ringSprite;
            ring.sortingOrder = 11;

            // 数字：太鼓同款 Impact 键帽字体 + 近零投影双层（白字黑影）。
            // 基准位 = 客观实测（金圈盘心 vs 白字质心 +4/+13px @75.7px每单位 → 本地 +0.029/+0.096，取反补偿）
            if (keycapFont == null) keycapFont = NoteSpawner.GetKeycapFont();
            var shadowGo = new GameObject("NumberShadow");
            shadowGo.transform.SetParent(transform, false);
            numberShadow = shadowGo.AddComponent<TextMesh>();
            FillNumberText(numberShadow, 12);
            var numGo = new GameObject("Number");
            numGo.transform.SetParent(transform, false);
            number = numGo.AddComponent<TextMesh>();
            FillNumberText(number, 13);
        }

        private static void FillNumberText(TextMesh tm, int order)
        {
            tm.font = keycapFont;
            tm.fontSize = 64;
            tm.characterSize = 0.075f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            var renderer = tm.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = keycapFont.material;
            renderer.sortingOrder = order;
        }

        /// <summary>数字定位 = 实测基准位（消除 OS Impact 在 MiddleAnchor 下的墨迹沉降）。
        /// 四个数字共用同一水平位置（不做逐字补偿——逐字修会引入左右乱偏），垂直统一上抬实测值。</summary>
        private void ApplyNumberCentering()
        {
            var pos = new Vector3(-0.029f, +0.096f, 0f); // 实测数字偏下 13px → 向上补偿（Y 向上为正）
            number.transform.localPosition = pos;
            numberShadow.transform.localPosition = pos + new Vector3(0.008f, -0.010f, 0f); // 近零投影：只留一丝深度感
        }

        // 圈体：灰阶圆盘（深色外沿+内侧微亮），运行时按连击组染色（同 TaikoSpawner 圆盘做法）
        private static Sprite BuildCircleSprite()
        {
            const int size = 256;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[size * size];
            float c = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - c) / c, dy = (y + 0.5f - c) / c;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01((0.985f - r) * size * 0.5f / 1.5f);
                    float shade = r > 0.86f ? 0.70f : r < 0.45f ? 1.10f : 1f;
                    byte v = (byte)Mathf.Clamp(255f * shade, 0f, 255f);
                    pixels[y * size + x] = new Color32(v, v, v, (byte)(a * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size); // PPU=256：原生 1 单位
            sprite.name = "OsuCircle";
            return sprite;
        }

        // 缩圈环：贴圈体外沿的细圆环（白色，运行时染色）
        private static Sprite BuildRingSprite()
        {
            const int size = 256;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[size * size];
            float c = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - c) / c, dy = (y + 0.5f - c) / c;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    // 环带 r∈[0.90,0.99]，两侧各 1.5px 抗锯齿
                    float aa = size * 0.5f / 1.5f;
                    float a = Mathf.Clamp01((r - 0.895f) * aa) * Mathf.Clamp01((0.995f - r) * aa);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
            sprite.name = "OsuRing";
            return sprite;
        }

        private void Update()
        {
            if (spawner == null || spawner.Game == null || state != State.Idle || consumed) return;
            if (spawner.Game.IsPaused) return; // 暂停冻结缩圈与淡入

            double now = spawner.Game.JudgedTime;
            double untilHit = time - now;

            // 淡入：出现后 0.35s 内从全透到全显
            float age = (float)(now - (time - spawner.ApproachSeconds));
            float fadeIn = Mathf.Clamp01(age / 0.35f);
            float alpha = fadeIn * fadeIn * (3f - 2f * fadeIn); // smoothstep
            body.color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
            ring.color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha * 0.6f); // 环降透明度：跨过邻圈时不抢主体
            number.color = new Color(1f, 1f, 1f, alpha);
            numberShadow.color = new Color(0f, 0f, 0f, 0.55f * alpha);

            // 缩圈环：ApproachScale → 1，合拢瞬间即判定点（晚到不再收缩，贴边等待判定）
            float k = Mathf.Clamp01((float)(untilHit / spawner.ApproachSeconds));
            float scale = 1f + (OsuSpawner.ApproachScale - 1f) * k;
            ring.transform.localScale = Vector3.one * scale;
        }

        /// <summary>命中：立即消失（无动画——动画曾叠住下一个圈，用户拍板去掉）。</summary>
        internal void OnHit(Judgement judgement)
        {
            if (state != State.Idle) return;
            state = State.Hit;
            spawner.OnNoteFinished(this);
        }

        /// <summary>Miss：立即消失（同上）。</summary>
        internal void OnMissed()
        {
            if (state != State.Idle) return;
            state = State.Missed;
            spawner.OnNoteFinished(this);
        }

        /// <summary>强制回收（停止/切歌）。</summary>
        internal void ForceRelease()
        {
            StopAllCoroutines();
            if (spawner != null && spawner.Pool != null) spawner.Pool.Return(gameObject);
        }
    }
}
