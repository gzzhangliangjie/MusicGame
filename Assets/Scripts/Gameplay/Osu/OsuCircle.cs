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
            numberShadow.text = numberValue.ToString();
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

            // 数字：太鼓同款 Impact 键帽字体 + 黑投影双层（与键帽/气球计数统一），主字白、投影黑
            if (keycapFont == null) keycapFont = NoteSpawner.GetKeycapFont();
            var shadowGo = new GameObject("NumberShadow");
            shadowGo.transform.SetParent(transform, false);
            shadowGo.transform.localPosition = new Vector3(0.025f, -0.035f, 0f);
            numberShadow = shadowGo.AddComponent<TextMesh>();
            FillNumberText(numberShadow, keycapFont, 12);

            var numGo = new GameObject("Number");
            numGo.transform.SetParent(transform, false);
            numGo.transform.localPosition = new Vector3(0f, 0f, 0f);
            number = numGo.AddComponent<TextMesh>();
            FillNumberText(number, keycapFont, 13);
        }

        private static void FillNumberText(TextMesh tm, Font font, int order)
        {
            tm.font = font;
            tm.fontSize = 64;
            tm.characterSize = 0.075f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            var renderer = tm.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = font.material;
            renderer.sortingOrder = order;
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
            ring.color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
            number.color = new Color(1f, 1f, 1f, alpha);
            numberShadow.color = new Color(0f, 0f, 0f, 0.55f * alpha);

            // 缩圈环：ApproachScale → 1，合拢瞬间即判定点（晚到不再收缩，贴边等待判定）
            float k = Mathf.Clamp01((float)(untilHit / spawner.ApproachSeconds));
            float scale = 1f + (OsuSpawner.ApproachScale - 1f) * k;
            ring.transform.localScale = Vector3.one * scale;
        }

        /// <summary>命中：太鼓鼓面同款敲扁回弹 + 0.18s 大爆放大淡出（Perfect 更爆），动画毕回收。</summary>
        internal void OnHit(Judgement judgement)
        {
            if (state != State.Idle) return;
            state = State.Hit;
            StartCoroutine(PopRoutine(judgement == Judgement.Perfect ? 2.1f : 1.85f));
        }

        /// <summary>Miss：灰化缩小淡出后回收。</summary>
        internal void OnMissed()
        {
            if (state != State.Idle) return;
            state = State.Missed;
            StartCoroutine(MissRoutine());
        }

        // 命中爆开：余弦衰减敲扁（横宽/纵扁+微摆，DrumPunchRoutine 同构）叠加爆开放大淡出（TaikoNote 0.18s 节奏）
        private System.Collections.IEnumerator PopRoutine(float popScale)
        {
            Vector3 startScale = transform.localScale;
            Color bodyC = body.color, ringC = ring.color, numC = number.color, shC = numberShadow.color;
            const float Dur = 0.18f;
            float t = 0f;
            while (t < Dur)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / Dur);
                float decay = 1f - k;
                float wobble = Mathf.Cos(k * Mathf.PI * 3f) * decay;
                float grow = 1f + (popScale - 1f) * k;
                transform.localScale = Vector3.Scale(startScale,
                    new Vector3(grow * (1f + 0.10f * wobble), grow * (1f - 0.14f * wobble), 1f));
                transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(k * Mathf.PI * 4f) * 2.5f * decay);
                body.color = new Color(bodyC.r, bodyC.g, bodyC.b, bodyC.a * (1f - k));
                ring.color = new Color(ringC.r, ringC.g, ringC.b, ringC.a * (1f - k));
                number.color = new Color(numC.r, numC.g, numC.b, numC.a * (1f - k));
                numberShadow.color = new Color(shC.r, shC.g, shC.b, shC.a * (1f - k));
                yield return null;
            }
            transform.localRotation = Quaternion.identity;
            spawner.OnNoteFinished(this);
        }

        private System.Collections.IEnumerator MissRoutine()
        {
            Vector3 startScale = transform.localScale;
            Color bodyC = body.color, ringC = ring.color, numC = number.color, shC = numberShadow.color;
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
                numberShadow.color = new Color(shC.r, shC.g, shC.b, shC.a * (1f - k));
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
