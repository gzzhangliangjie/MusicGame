using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace MusicGame.UI
{
    /// <summary>
    /// 判定弹窗层（uGUI）：判定线上方按轨道位置弹出 PERFECT / GOOD / BAD / MISS 艺术字。
    /// 贴图在 Assets/Resources/Judgement（程序生成：斜体渐变字+描边+外圈辉光），缺图回退普通字体+描边。
    /// 动效参数参考 osu!lazer mania DefaultManiaJudgementPiece（MIT，github.com/ppy/osu）：
    /// 命中 = OutBack 弹性放大 + 上浮淡出（Perfect 额外回正摆头）；Miss = 下坠 + 旋转（InQuint）。
    /// </summary>
    public class JudgementPopupLayer : MonoBehaviour
    {
        private const int PoolSize = 10;
        private const float ArtHeight = 96f; // 艺术字显示高（UiKit 参考分辨率 1920x1080）
        private const float ArtAspect = 800f / 256f; // 贴图画布宽高比（四词统一画布）

        // 回退配色（无艺术字贴图时的字体方案用）
        private static readonly Color PerfectColor = new Color(0.98f, 0.80f, 0.25f);
        private static readonly Color GoodColor = new Color(0.35f, 0.85f, 0.45f);
        private static readonly Color BadColor = new Color(0.95f, 0.55f, 0.20f);
        private static readonly Color MissColor = new Color(0.90f, 0.26f, 0.36f);

        private readonly Dictionary<Gameplay.Judgement, Sprite> art = new Dictionary<Gameplay.Judgement, Sprite>();
        private readonly List<Popup> pool = new List<Popup>();
        private int nextIndex;

        /// <summary>太鼓模式下弹窗锚定鼓面上方（lane 0/1/2 是咚/咔/连击，不是四轨坐标）。</summary>
        public bool TaikoMode { get; set; }

        /// <summary>缩圈模式下弹窗锚定命中圆圈处（OsuSpawner.AnchorWorld 在判定前置位）。</summary>
        public bool OsuMode { get; set; }
        /// <summary>缩圈模式弹窗锚点（世界坐标，命中/Miss 前由 OsuSpawner 写入）。</summary>
        public static Vector3 OsuAnchorWorld { get; set; }

        private class Popup
        {
            public RectTransform Rt;
            public Image Art;      // 艺术字（缺图时禁用）
            public Text Text;      // 回退字体（有艺术字时禁用）
            public Coroutine Running;
        }

        /// <summary>在宿主（HUD 全屏面板）下构建弹窗对象池。</summary>
        public void Setup(RectTransform host)
        {
            art[Gameplay.Judgement.Perfect] = LoadArt("perfect");
            art[Gameplay.Judgement.Good] = LoadArt("good");
            art[Gameplay.Judgement.Bad] = LoadArt("bad");
            art[Gameplay.Judgement.Miss] = LoadArt("miss");

            for (int i = 0; i < PoolSize; i++)
            {
                var rt = UiKit.Node(host, "JudgementPopup" + i);
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(320f, ArtHeight);

                // 艺术字挂根节点；回退 Text 必须放子节点——uGUI 一个 GameObject 只能挂一个 Graphic，
                // Image+Text 同节点会在 text.font 赋值时 NRE（BuildScreens 曾被此处打断引发连锁雪崩）
                var img = rt.gameObject.AddComponent<Image>();
                img.raycastTarget = false;
                img.enabled = false;

                var textRt = UiKit.Node(rt, "FallbackText");
                textRt.anchorMin = Vector2.zero;
                textRt.anchorMax = Vector2.one;
                textRt.offsetMin = Vector2.zero;
                textRt.offsetMax = Vector2.zero;
                var text = textRt.gameObject.AddComponent<Text>();
                text.font = UiKit.FontOrDefault;
                text.fontSize = 40;
                text.fontStyle = FontStyle.Bold;
                text.alignment = TextAnchor.MiddleCenter;
                text.raycastTarget = false;
                text.text = "";
                var outline = text.gameObject.AddComponent<Outline>();
                outline.effectColor = new Color(0f, 0f, 0f, 0.7f);
                outline.effectDistance = new Vector2(2f, -2f);

                rt.gameObject.SetActive(false);
                pool.Add(new Popup { Rt = rt, Art = img, Text = text });
            }
        }

        // Resources/Judgement/<key>.png → 精灵；缺图返回 null（调用方回退字体方案）
        private static Sprite LoadArt(string key)
        {
            try
            {
                var tex = Resources.Load<Texture2D>("Judgement/" + key);
                if (tex == null) return null;
                var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                sprite.name = "Judgement_" + key;
                return sprite;
            }
            catch
            {
                return null; // Unity 假 null / 加载异常一律回退
            }
        }

        /// <summary>弹出一次判定。</summary>
        public void Show(Gameplay.Judgement judgement, int lane, bool isTail)
        {
            Popup popup = pool[nextIndex];
            nextIndex = (nextIndex + 1) % pool.Count;
            if (popup.Running != null) StopCoroutine(popup.Running);

            bool hasArt = art.TryGetValue(judgement, out Sprite sprite) && sprite != null;
            popup.Art.enabled = hasArt;
            popup.Text.enabled = !hasArt;
            if (hasArt)
            {
                popup.Art.sprite = sprite;
                popup.Art.rectTransform.sizeDelta = new Vector2(ArtHeight * ArtAspect, ArtHeight);
                popup.Art.color = new Color(1f, 1f, 1f, 0f);
            }
            else
            {
                popup.Text.text = Word(judgement);
                popup.Text.color = ColorOf(judgement);
            }

            popup.Rt.anchoredPosition = LaneToCanvasPosition(TaikoMode ? -1 : lane);
            popup.Rt.localRotation = Quaternion.identity;
            popup.Rt.localScale = Vector3.one;
            popup.Rt.gameObject.SetActive(true);
            popup.Running = StartCoroutine(Animate(popup, judgement));
        }

        private static string Word(Gameplay.Judgement j)
        {
            return j == Gameplay.Judgement.Perfect ? "PERFECT"
                : j == Gameplay.Judgement.Good ? "GOOD"
                : j == Gameplay.Judgement.Bad ? "BAD" : "MISS";
        }

        private static Color ColorOf(Gameplay.Judgement j)
        {
            return j == Gameplay.Judgement.Perfect ? PerfectColor
                : j == Gameplay.Judgement.Good ? GoodColor
                : j == Gameplay.Judgement.Bad ? BadColor : MissColor;
        }

        private IEnumerator Animate(Popup popup, Gameplay.Judgement judgement)
        {
            var rt = popup.Rt;
            Vector2 basePos = rt.anchoredPosition;

            if (judgement == Gameplay.Judgement.Miss)
            {
                // Miss：微放大回落 + 下坠 + 旋转（InQuint 0.75s，参考 osu!lazer）
                float Dur = OsuMode ? 0.45f : 0.75f; // 缩圈弹窗压在圆圈上方，短命避免叠到下一个圈
                float t = 0f;
                while (t < Dur)
                {
                    t += Time.unscaledDeltaTime;
                    float k = Mathf.Clamp01(t / Dur);
                    float e = InQuint(k);
                    rt.localScale = Vector3.one * (1.25f - 0.25f * Mathf.Clamp01(k * 6f));
                    rt.anchoredPosition = basePos + new Vector2(0f, -e * 130f);
                    rt.localRotation = Quaternion.Euler(0f, 0f, -e * 30f);
                    SetAlpha(popup, 1f - k * k);
                    yield return null;
                }
            }
            else
            {
                // 命中：OutBack 弹出 + 上浮，后段淡出；Perfect 多一记回正摆头，其余随机小倾角
                const float Fade = 0.44f;
                bool perfect = judgement == Gameplay.Judgement.Perfect;
                float tilt = perfect ? -8f : Random.Range(-2.5f, 2.5f);
                float t = 0f;
                while (t < Fade)
                {
                    t += Time.unscaledDeltaTime;
                    float pop = Mathf.Lerp(0.55f, perfect ? 1.14f : 1.06f, OutBack(Mathf.Clamp01(t / 0.18f)));
                    float settle = OutCubic(Mathf.Clamp01((t - 0.18f) / (Fade - 0.18f)));
                    rt.localScale = Vector3.one * (pop * (1f - 0.07f * settle));
                    rt.anchoredPosition = basePos + new Vector2(0f, 34f * OutCubic(Mathf.Clamp01(t / 0.34f)));
                    rt.localRotation = Quaternion.Euler(0f, 0f, tilt * (1f - OutCubic(Mathf.Clamp01((t - 0.08f) / 0.24f))));
                    SetAlpha(popup, t > 0.22f ? 1f - Mathf.Clamp01((t - 0.22f) / (Fade - 0.22f)) : 1f);
                    yield return null;
                }
            }
            rt.gameObject.SetActive(false);
            popup.Running = null;
        }

        private static void SetAlpha(Popup popup, float alpha)
        {
            float a = Mathf.Clamp01(alpha);
            if (popup.Art != null && popup.Art.enabled) popup.Art.color = new Color(1f, 1f, 1f, a);
            if (popup.Text != null && popup.Text.enabled)
            {
                var c = popup.Text.color;
                popup.Text.color = new Color(c.r, c.g, c.b, a);
            }
        }

        // 轨道世界坐标 → HUD 画布局部坐标；太鼓模式锚定鼓面上方，lane=-1 时回中（Miss 下坠基准）
        private Vector2 LaneToCanvasPosition(int lane)
        {
            Camera cam = Camera.main;
            if (cam == null) return new Vector2(0f, 0f);

            Vector3 world;
            if (OsuMode)
                world = OsuAnchorWorld + Vector3.up * 1.35f; // 抬到圈顶上方：弹窗不再盖住圈内数字
            else if (TaikoMode)
                world = new Vector3(Gameplay.Taiko.TaikoSpawner.HitX, Gameplay.Taiko.TaikoSpawner.NoteY + 1.7f, 0f);
            else if (lane >= 0)
                world = new Vector3(Gameplay.NoteSpawner.LaneX(lane), Gameplay.NoteSpawner.JudgeLineY + 0.9f, 0f);
            else
                world = new Vector3(0f, Gameplay.NoteSpawner.JudgeLineY + 0.9f, 0f);

            Vector3 screen = cam.WorldToScreenPoint(world);
            Canvas canvas = GetComponentInParent<Canvas>();
            float factor = canvas != null ? canvas.scaleFactor : 1f;
            // 画布半宽/半高从实际屏幕推导（scaleFactor 等比），不假设 16:9
            return new Vector2(screen.x / factor - Screen.width / factor * 0.5f,
                screen.y / factor - Screen.height / factor * 0.5f);
        }

        private static float InQuint(float t) { return t * t * t * t * t; }

        private static float OutCubic(float t) { float k = 1f - t; return 1f - k * k * k; }

        private static float OutBack(float t)
        {
            const float c1 = 1.70158f;
            float k = t - 1f;
            return 1f + (c1 + 1f) * k * k * k + c1 * k * k;
        }
    }
}
