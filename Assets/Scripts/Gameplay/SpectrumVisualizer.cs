using MusicGame.Core;
using UnityEngine;

namespace MusicGame.Gameplay
{
    /// <summary>
    /// 背景频谱可视化（世界空间，垫在判定区之下）：
    /// AudioSource.GetSpectrumData 实时取频谱 → 对数分桶成一排低透明度加法光柱（快攻慢收），
    /// 低频能量驱动整体背景辉光。视觉上不与音符/判定抢焦点（sortingOrder 在轨道线之下）。
    /// 无声模式（静音 clip / 安静段）自动退化为随节拍呼吸的波浪，保持画面活性。
    /// 贴图与材质代码生成，无资源依赖；随 runtimeRoot 每局销毁。
    /// </summary>
    public class SpectrumVisualizer : MonoBehaviour
    {
        private const int BarCount = 28;
        private const int SpectrumSize = 64;          // GetSpectrumData 采样数（2 的幂）
        private const float FieldHalfWidth = 5f;      // 光柱排布总宽的一半（略宽于 4 轨区）
        private const float MaxBarHeight = 2.2f;      // 光柱最大高度（世界单位）
        private const float RiseSpeed = 24f;          // 攻：上升插值速度（快）
        private const float FallSpeed = 5f;           // 收：衰减插值速度（慢）
        private const float SilenceEpsilon = 0.0004f; // 低于视为无声
        private const float BarBaseAlpha = 0.35f;

        private static readonly Color LowColor = new Color(0.20f, 0.85f, 0.95f);   // 低频（青）
        private static readonly Color HighColor = new Color(0.55f, 0.30f, 0.85f);  // 高频（紫）

        private AudioSource source;
        private Conductor conductor;
        private readonly float[] spectrum = new float[SpectrumSize];
        private readonly float[] smoothed = new float[BarCount];
        private SpriteRenderer[] bars;
        private SpriteRenderer glow;
        private float glowSmoothed;
        private float lastAudibleTime = -10f;
        private Material additive;

        /// <summary>装配：绑定音源与时钟，构建光柱与背景辉光。</summary>
        public void Setup(AudioSource audioSource, Conductor beatClock)
        {
            source = audioSource;
            conductor = beatClock;

            Shader shader = Shader.Find("Particles/Additive");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            additive = new Material(shader);
            Sprite circle = Sprite.Create(HitEffects.MakeCircleTexture(), new Rect(0f, 0f, 64f, 64f), new Vector2(0.5f, 0.5f), 64f);
            circle.name = "SpectrumGlow";
            // 底部锚点方形 sprite：光柱从判定线向上生长
            Sprite square = Sprite.Create(NoteSpawner.GetSquareSprite().texture,
                new Rect(0f, 0f, 8f, 8f), new Vector2(0.5f, 0f), 8f);
            square.name = "SpectrumBar";

            glow = new GameObject("BassGlow").AddComponent<SpriteRenderer>();
            glow.transform.SetParent(transform, false);
            glow.transform.position = new Vector3(0f, NoteSpawner.JudgeLineY + 0.8f, 0f);
            glow.transform.localScale = new Vector3(13f, 9f, 1f);
            glow.sprite = circle;
            glow.material = additive;
            glow.sortingOrder = -3;
            glow.color = new Color(LowColor.r, LowColor.g, LowColor.b, 0f);

            bars = new SpriteRenderer[BarCount];
            float barWidth = FieldHalfWidth * 2f / BarCount;
            for (int i = 0; i < BarCount; i++)
            {
                float x = -FieldHalfWidth + (i + 0.5f) * barWidth;
                var bar = new GameObject("Bar" + i).AddComponent<SpriteRenderer>();
                bar.transform.SetParent(transform, false);
                bar.transform.position = new Vector3(x, NoteSpawner.JudgeLineY - 0.06f, 0f);
                bar.transform.localScale = new Vector3(barWidth * 0.72f, 0.02f, 1f);
                bar.sprite = square;
                bar.material = additive;
                bar.sortingOrder = -2;
                float t = i / (float)(BarCount - 1);
                bar.color = Color.Lerp(LowColor, HighColor, t);
                bars[i] = bar;
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            bool audible = false;

            if (source != null && source.clip != null)
            {
                source.GetSpectrumData(spectrum, 0, FFTWindow.BlackmanHarris);
                float total = 0f;
                for (int i = 1; i < SpectrumSize / 2; i++) total += spectrum[i];
                audible = total > SilenceEpsilon;
            }
            if (audible) lastAudibleTime = Time.time;
            bool fallback = Time.time - lastAudibleTime > 0.5f; // 无声 clip 或安静段

            float bass = 0f;
            if (!fallback)
            {
                for (int i = 1; i <= 4; i++) bass += spectrum[i];
                bass /= 4f;
                bass = Mathf.Clamp01(Mathf.Sqrt(bass) * 8f);
                for (int i = 0; i < BarCount; i++)
                {
                    float target = BarEnergy(i);
                    UpdateBar(i, target, dt);
                }
            }
            else
            {
                // 无声模式：随节拍呼吸的行波
                float beat = 0f;
                if (conductor != null && conductor.IsPlaying && conductor.Bpm > 0.0)
                {
                    double period = 60.0 / conductor.Bpm;
                    double phase = (conductor.SongTimeSeconds - conductor.FirstBeatOffsetSeconds) % period;
                    if (phase < 0) phase += period;
                    float k = 1f - (float)(phase / period);
                    beat = k * k * k;
                }
                bass = beat * 0.5f;
                for (int i = 0; i < BarCount; i++)
                {
                    float wave = 0.5f + 0.5f * Mathf.Sin(Time.time * 2.5f + i * 0.45f);
                    UpdateBar(i, beat * (0.15f + 0.55f * wave), dt);
                }
            }

            // 低频驱动的背景辉光（快攻慢收）
            glowSmoothed = Mathf.Max(bass, Mathf.Lerp(glowSmoothed, bass, FallSpeed * dt));
            var gc = glow.color;
            glow.color = new Color(gc.r, gc.g, gc.b, Mathf.Clamp01(0.04f + glowSmoothed * 0.16f));
        }

        private void UpdateBar(int i, float target, float dt)
        {
            // 快攻慢收：目标更高立刻跟上，回落走慢插值
            float value = target > smoothed[i]
                ? Mathf.Lerp(smoothed[i], target, Mathf.Clamp01(RiseSpeed * dt))
                : Mathf.Lerp(smoothed[i], target, Mathf.Clamp01(FallSpeed * dt));
            smoothed[i] = value;

            var bar = bars[i];
            bar.transform.localScale = new Vector3(bar.transform.localScale.x,
                Mathf.Max(0.02f, value * MaxBarHeight), 1f);
            var c = bar.color;
            bar.color = new Color(c.r, c.g, c.b, BarBaseAlpha * (0.35f + 0.65f * value));
        }

        // 第 i 根光柱的能量：对数分桶（偏置低频，音乐能量集中区），平方根提升小值可见度
        private float BarEnergy(int i)
        {
            float usable = SpectrumSize / 2f - 1f;
            float t0 = i / (float)BarCount;
            float t1 = (i + 1) / (float)BarCount;
            int lo = Mathf.Clamp(Mathf.FloorToInt(Mathf.Lerp(1f, usable, t0 * t0)), 0, SpectrumSize / 2 - 1);
            int hi = Mathf.Clamp(Mathf.CeilToInt(Mathf.Lerp(1f, usable, t1 * t1)), lo + 1, SpectrumSize / 2);
            float sum = 0f;
            for (int b = lo; b < hi; b++) sum += spectrum[b];
            float energy = sum / (hi - lo);
            return Mathf.Clamp01(Mathf.Sqrt(energy) * 9f);
        }
    }
}
