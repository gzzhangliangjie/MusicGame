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
        private static Font numberFont;

        private OsuSpawner spawner;
        private double time;
        private Color baseColor;
        private SpriteRenderer body, ring;
        private TextMesh number;
        private State state;
        private bool consumed;

        /// <summary>是否已被判定消费（命中或 Miss，供 TryHit/TryMiss 跳过）。</summary>
        internal bool Consumed
        {
            get { return consumed; }
        }

        internal void Consume() { consumed = true; }

        internal void Initialize(double hitTime, int numberValue, int colorIndex, OsuSpawner owner)
        {
            spawner = owner;
            time = hitTime;
            state = State.Idle;
            consumed = false;

            EnsureVisuals();
            baseColor = NoteSpawner.LaneColors[Mathf.Clamp(colorIndex, 0, 3)];
            body.color = new Color(baseColor.r, baseColor.g, baseColor.b, 0f);
            ring.color = new Color(baseColor.r, baseColor.g, baseColor.b, 0f);
            number.text = numberValue.ToString();
            number.color = new Color(0.10f, 0.10f, 0.16f, 0f);

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

            var numGo = new GameObject("Number");
            numGo.transform.SetParent(transform, false);
            numGo.transform.localPosition = new Vector3(0f, 0f, 0f);
            number = numGo.AddComponent<TextMesh>();
            if (numberFont == null) numberFont = Taiko.TaikoSpawner.GetBuiltinFont();
            number.font = numberFont;
            number.fontSize = 64;
            number.characterSize = 0.055f;
            number.anchor = TextAnchor.MiddleCenter;
            number.alignment = TextAlignment.Center;
            var numRenderer = numGo.GetComponent<MeshRenderer>();
            numRenderer.sharedMaterial = number.font.material;
            numRenderer.sortingOrder = 12;
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
            float age = (float)(now - (time - OsuSpawner.ApproachSeconds));
            float fadeIn = Mathf.Clamp01(age / 0.35f);
            float alpha = fadeIn * fadeIn * (3f - 2f * fadeIn); // smoothstep
            body.color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
            ring.color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
            number.color = new Color(0.10f, 0.10f, 0.16f, alpha);

            // 缩圈环：ApproachScale → 1，合拢瞬间即判定点（晚到不再收缩，贴边等待判定）
            float k = Mathf.Clamp01((float)(untilHit / OsuSpawner.ApproachSeconds));
            float scale = 1f + (OsuSpawner.ApproachScale - 1f) * k;
            ring.transform.localScale = Vector3.one * scale;
        }

        /// <summary>命中：圈体/环/数字整体爆闪放大淡出后回收（osu! 命中消散风格）。</summary>
        internal void OnHit(Judgement judgement)
        {
            if (state != State.Idle) return;
            state = State.Hit;
            StartCoroutine(PopRoutine(judgement == Judgement.Perfect ? 1.35f : 1.22f));
        }

        /// <summary>Miss：灰化缩小淡出后回收。</summary>
        internal void OnMissed()
        {
            if (state != State.Idle) return;
            state = State.Missed;
            StartCoroutine(MissRoutine());
        }

        private System.Collections.IEnumerator PopRoutine(float popScale)
        {
            Vector3 startScale = transform.localScale;
            Color bodyC = body.color, ringC = ring.color, numC = number.color;
            float t = 0f;
            while (t < 0.22f)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / 0.22f);
                transform.localScale = startScale * (1f + (popScale - 1f) * k);
                body.color = new Color(bodyC.r, bodyC.g, bodyC.b, bodyC.a * (1f - k));
                ring.color = new Color(ringC.r, ringC.g, ringC.b, ringC.a * (1f - k));
                number.color = new Color(numC.r, numC.g, numC.b, numC.a * (1f - k));
                yield return null;
            }
            spawner.OnNoteFinished(this);
        }

        private System.Collections.IEnumerator MissRoutine()
        {
            Vector3 startScale = transform.localScale;
            Color bodyC = body.color, ringC = ring.color, numC = number.color;
            Color grey = new Color(0.55f, 0.55f, 0.55f);
            float t = 0f;
            while (t < 0.45f)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / 0.45f);
                transform.localScale = startScale * (1f - 0.25f * k);
                body.color = Color.Lerp(bodyC, new Color(grey.r, grey.g, grey.b, bodyC.a * (1f - k)), Mathf.Min(1f, k * 3f));
                ring.color = new Color(ringC.r, ringC.g, ringC.b, ringC.a * (1f - k));
                number.color = new Color(numC.r, numC.g, numC.b, numC.a * (1f - k));
                yield return null;
            }
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
