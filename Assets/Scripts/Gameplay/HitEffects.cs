using System.Collections.Generic;
using UnityEngine;

namespace MusicGame.Gameplay
{
    /// <summary>
    /// 打击反馈（世界空间）：判定点爆炸（大光晕横扫 + 核心光斑 + 双斜向光条，加法混合）与轨道光柱。
    /// 分层结构与缓动参数参考 osu!lazer mania 的 DefaultHitExplosion / PoolableHitExplosion（MIT，github.com/ppy/osu）。
    /// 全部实例对象池复用，运行期零实例化；贴图与材质代码生成，无资源依赖。
    /// </summary>
    public class HitEffects : MonoBehaviour
    {
        private const float ExplosionDuration = 0.20f; // 核心爆炸时长（osu 同款）
        private const float FaintDuration = 0.40f;     // 大光晕横扫时长
        private const float BeamDuration = 0.22f;      // 轨道光柱衰减（低 alpha 快衰减，只做提示不抢判定区）
        private const int ExplosionPoolSize = 24;
        private const int BeamPoolSize = 8;

        private static readonly Color PerfectColor = new Color(0.98f, 0.80f, 0.25f);
        private static readonly Color GreatColor = new Color(0.35f, 0.85f, 0.45f);
        private static readonly Color MissBeamColor = new Color(0.55f, 0.55f, 0.55f);

        private Material additive;
        private Sprite circleSprite;
        private Sprite beamSprite;

        private readonly List<EffectFx> explosions = new List<EffectFx>();
        private readonly List<EffectFx> beams = new List<EffectFx>();
        private readonly List<EffectFx> explosionFree = new List<EffectFx>();
        private readonly List<EffectFx> beamFree = new List<EffectFx>();

        private class EffectFx
        {
            public GameObject Go;
            public SpriteRenderer[] Renderers; // 爆炸=4 层；光柱=1 层
            public Vector3[] BaseScales;       // 各层基础缩放（动画在其上乘系数，避免覆写丢失尺寸）
            public float Age;
            public float Duration;
            public float BaseAlpha;
            public Vector3 BaseScale;
        }

        private void EnsureAssets()
        {
            if (additive != null) return;
            Shader shader = Shader.Find("Particles/Additive");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            additive = new Material(shader);
            circleSprite = Sprite.Create(MakeCircleTexture(), new Rect(0f, 0f, 64f, 64f), new Vector2(0.5f, 0.5f), 64f);
            circleSprite.name = "HitFxCircle";
            beamSprite = Sprite.Create(MakeBeamTexture(), new Rect(0f, 0f, 16f, 64f), new Vector2(0.5f, 0f), 16f);
            beamSprite.name = "HitFxBeam";
        }

        /// <summary>在判定线轨道处触发一次打击反馈。headMiss 为 true 时只亮灰色光柱（Miss 无爆炸）。</summary>
        public void Spawn(int lane, Judgement judgement, bool isTail, bool headMiss)
        {
            EnsureAssets();
            if (headMiss)
            {
                SpawnBeam(lane, MissBeamColor, 0.10f);
                return;
            }
            Color color = judgement == Judgement.Perfect ? PerfectColor
                : judgement == Judgement.Great ? GreatColor
                : GreatColor;
            SpawnExplosion(lane, color, isTail ? 0.7f : 1f);
            SpawnBeam(lane, color, 0.14f);
        }

        private void SpawnExplosion(int lane, Color color, float intensity)
        {
            EffectFx fx = Rent(explosionFree, explosions, ExplosionPoolSize, BuildExplosion);
            fx.Age = 0f;
            fx.Duration = FaintDuration;
            fx.BaseAlpha = intensity;
            fx.BaseScale = Vector3.one;
            fx.Go.transform.position = new Vector3(NoteSpawner.LaneX(lane), NoteSpawner.JudgeLineY, 0f);
            for (int i = 0; i < fx.Renderers.Length; i++)
            {
                fx.Renderers[i].color = new Color(color.r, color.g, color.b, 1f);
                fx.Renderers[i].transform.localScale = fx.BaseScales[i];
            }
            fx.Go.SetActive(true);
        }

        private void SpawnBeam(int lane, Color color, float alpha)
        {
            EffectFx fx = Rent(beamFree, beams, BeamPoolSize, BuildBeam);
            fx.Age = 0f;
            fx.Duration = BeamDuration;
            fx.BaseAlpha = alpha;
            fx.Go.transform.position = new Vector3(NoteSpawner.LaneX(lane), NoteSpawner.JudgeLineY, 0f);
            fx.Renderers[0].color = new Color(color.r, color.g, color.b, 1f);
            fx.Go.SetActive(true);
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            AnimateAll(explosions, explosionFree, dt);
            AnimateAll(beams, beamFree, dt);
        }

        private static void AnimateAll(List<EffectFx> active, List<EffectFx> free, float dt)
        {
            for (int i = active.Count - 1; i >= 0; i--)
            {
                EffectFx fx = active[i];
                fx.Age += dt;
                float t = fx.Age / fx.Duration;
                if (t >= 1f)
                {
                    fx.Go.SetActive(false);
                    active.RemoveAt(i);
                    free.Add(fx);
                    continue;
                }
                if (fx.Renderers.Length == 4) AnimateExplosion(fx, t);
                else AnimateBeam(fx, t);
            }
        }

        // 爆炸动画：0=大光晕 1=核心 2/3=斜光条（osu 分层同构）；全部在基础缩放上乘系数
        private static void AnimateExplosion(EffectFx fx, float t)
        {
            SpriteRenderer faint = fx.Renderers[0];
            float faintT = Mathf.Min(1f, fx.Age / FaintDuration);
            float sweep = OutQuint(faintT);
            faint.transform.localScale = Vector3.Scale(fx.BaseScales[0], new Vector3(1f + sweep * 4f, 1f + sweep * 0.2f, 1f));
            faint.color = MulAlpha(faint.color, (1f - faintT) * 0.35f * fx.BaseAlpha);

            SpriteRenderer core = fx.Renderers[1];
            float coreT = Mathf.Min(1f, fx.Age / ExplosionDuration);
            core.transform.localScale = Vector3.Scale(fx.BaseScales[1], Vector3.one * (1f + 0.4f * OutQuint(coreT)));
            core.color = MulAlpha(core.color, (1f - OutCubic(coreT)) * 0.75f * fx.BaseAlpha);

            for (int i = 2; i < 4; i++)
            {
                SpriteRenderer streak = fx.Renderers[i];
                float streakT = Mathf.Min(1f, fx.Age / ExplosionDuration);
                streak.transform.localScale = Vector3.Scale(fx.BaseScales[i], new Vector3(1f, 1f + OutQuint(streakT) * 1.2f, 1f));
                streak.color = MulAlpha(streak.color, (1f - OutCubic(streakT)) * 0.6f * fx.BaseAlpha);
            }
        }

        private static void AnimateBeam(EffectFx fx, float t)
        {
            SpriteRenderer beam = fx.Renderers[0];
            beam.color = MulAlpha(beam.color, (1f - OutCubic(t)) * fx.BaseAlpha);
            beam.transform.localScale = Vector3.Scale(fx.BaseScales[0], new Vector3(1f + t * 0.15f, 1f, 1f));
        }

        private EffectFx Rent(List<EffectFx> free, List<EffectFx> active, int cap, System.Func<EffectFx> build)
        {
            if (free.Count > 0)
            {
                EffectFx fx = free[free.Count - 1];
                free.RemoveAt(free.Count - 1);
                active.Add(fx);
                return fx;
            }
            if (active.Count >= cap) return active[0]; // 池满：复用最老实例
            EffectFx created = build();
            created.Go.transform.SetParent(transform, false);
            active.Add(created);
            return created;
        }

        private EffectFx BuildExplosion()
        {
            var go = new GameObject("HitExplosion");
            var renderers = new SpriteRenderer[4];
            renderers[0] = MakeChild(go.transform, "faint", circleSprite, NoteSpawner.LaneWidth * 0.8f, 0);
            renderers[1] = MakeChild(go.transform, "core", circleSprite, NoteSpawner.LaneWidth * 0.5f, 1);
            renderers[2] = MakeChild(go.transform, "streakA", circleSprite, 1f, 2, new Vector2(0.28f, 2.6f), UnityEngine.Random.Range(-15f, 15f));
            renderers[3] = MakeChild(go.transform, "streakB", circleSprite, 1f, 2, new Vector2(0.28f, 2.6f), UnityEngine.Random.Range(-15f, 15f));
            var baseScales = new Vector3[renderers.Length];
            for (int i = 0; i < renderers.Length; i++) baseScales[i] = renderers[i].transform.localScale;
            go.SetActive(false);
            return new EffectFx { Go = go, Renderers = renderers, BaseScales = baseScales };
        }

        private EffectFx BuildBeam()
        {
            var go = new GameObject("LaneBeam");
            var renderer = MakeChild(go.transform, "beam", beamSprite, 1f, 7, new Vector2(NoteSpawner.LaneWidth * 0.96f, NoteSpawner.FallHeight * 0.7f), 0f);
            go.SetActive(false);
            return new EffectFx { Go = go, Renderers = new[] { renderer }, BaseScales = new[] { renderer.transform.localScale } };
        }

        private SpriteRenderer MakeChild(Transform parent, string name, Sprite sprite, float size, int order)
        {
            return MakeChild(parent, name, sprite, size, order, new Vector2(size, size), 0f);
        }

        private SpriteRenderer MakeChild(Transform parent, string name, Sprite sprite, float size, int order, Vector2 scale, float rotate)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent, false);
            child.transform.localScale = new Vector3(scale.x, scale.y, 1f);
            child.transform.localRotation = Quaternion.Euler(0f, 0f, rotate);
            var renderer = child.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.material = additive;
            renderer.sortingOrder = 11 + order;
            return renderer;
        }

        private static Color MulAlpha(Color c, float alpha)
        {
            return new Color(c.r, c.g, c.b, Mathf.Clamp01(alpha));
        }

        private static float OutQuint(float t) { t = Mathf.Clamp01(t); float u = 1f - t; return 1f - u * u * u * u * u; }

        private static float OutCubic(float t) { t = Mathf.Clamp01(t); float u = 1f - t; return 1f - u * u * u; }

        // 64x64 径向渐变圆（中心实、边缘透明），供加法混合出光斑；SpectrumVisualizer 复用
        internal static Texture2D MakeCircleTexture()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - r);
                    byte v = (byte)(a * a * 255f);
                    pixels[y * size + x] = new Color32(255, 255, 255, v);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        // 16x64 纵向渐变光柱（底部最亮，平方衰减让上段近乎透明——只照亮判定区附近）
        private static Texture2D MakeBeamTexture()
        {
            const int w = 16, h = 64;
            var texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                float t = (float)y / h;
                byte a = (byte)((1f - t) * (1f - t) * 255f);
                for (int x = 0; x < w; x++) pixels[y * w + x] = new Color32(255, 255, 255, a);
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }
    }
}
