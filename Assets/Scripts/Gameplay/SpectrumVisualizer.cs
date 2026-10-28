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
        private const int SpectrumSize = 2048;        // GetSpectrumData 采样数（2 的幂；64 的可用 bin 太少，28 根柱挤在同一批 bass bin 上）
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
        private readonly float[] rawEnergy = new float[BarCount];
        private SpriteRenderer[] bars;
        private SpriteRenderer glow;
        private float glowSmoothed;
        private float agcGain = 1f;
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
                for (int i = 2; i < 256; i++) total += spectrum[i]; // 覆盖 ~47Hz-6kHz 的可闻主体
                audible = total > SilenceEpsilon;
            }
            if (audible) lastAudibleTime = Time.time;
            bool fallback = Time.time - lastAudibleTime > 0.5f; // 无声 clip 或安静段

            float bass = 0f;
            if (!fallback)
            {
                for (int i = 2; i <= 6; i++) bass += spectrum[i];
                bass /= 5f;
                // 先取原始能量，再做 AGC 归一化：按本帧峰值动态调增益（快压慢放），
                // 音乐再响也只有最高的柱接近满格——修复固定增益下整排同时顶格的"全满"
                float frameMax = 0f;
                for (int i = 0; i < BarCount; i++)
                {
                    rawEnergy[i] = BarEnergy(i);
                    if (rawEnergy[i] > frameMax) frameMax = rawEnergy[i];
                }
                float targetGain = 0.88f / Mathf.Max(frameMax, 1e-4f);
                agcGain = targetGain < agcGain
                    ? Mathf.Lerp(agcGain, targetGain, Mathf.Clamp01(14f * dt))   // 峰值变响：增益快压
                    : Mathf.Lerp(agcGain, targetGain, Mathf.Clamp01(1.5f * dt)); // 变安静：慢放，保持层次
                for (int i = 0; i < BarCount; i++)
                    UpdateBar(i, Mathf.Clamp01(rawEnergy[i] * agcGain), dt);
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
                    UpdateBar(i, beat * (0.25f + 0.7f * wave), dt);
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

        // 第 i 根光柱的原始能量：指数（对数感知）分桶——低频柱带宽窄、高频柱带宽宽且按频率抬一点权重。
        // 不再内建固定增益（整排饱和的根源），动态范围交给 AGC 归一化
        private float BarEnergy(int i)
        {
            float t = (i + 0.5f) / BarCount;
            const int minBin = 2, maxBin = 620; // bin≈23.4Hz@48k → 47Hz~14.5kHz，音乐能量主体区
            int center = Mathf.Clamp(Mathf.RoundToInt(minBin * Mathf.Pow(maxBin / (float)minBin, t)), minBin, maxBin);
            int width = Mathf.Max(2, Mathf.RoundToInt(Mathf.Lerp(2f, 30f, t)));
            int lo = Mathf.Clamp(center - width / 2, minBin, Mathf.Max(minBin, maxBin - width));
            int hi = lo + width;
            float sum = 0f;
            for (int b = lo; b < hi; b++) sum += spectrum[b];
            float energy = sum / width;
            float tilt = Mathf.Lerp(1.0f, 2.0f, t);
            return Mathf.Sqrt(energy) * tilt;
        }
    }
}
