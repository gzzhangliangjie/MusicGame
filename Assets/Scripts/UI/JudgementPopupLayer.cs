using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace MusicGame.UI
{
    /// <summary>
    /// 判定弹窗层（uGUI）：在判定线上方按轨道位置弹出 PERFECT / GREAT / MISS。
    /// 动效参数参考 osu!lazer mania DefaultManiaJudgementPiece（MIT，github.com/ppy/osu）：
    /// 命中 = 0.8→1 弹性放大（OutElastic 250ms）+ 50ms 后缩退淡出；Miss = 下坠 + 旋转 40°（InQuint 800ms）。
    /// </summary>
    public class JudgementPopupLayer : MonoBehaviour
    {
        private const int PoolSize = 10;

        private static readonly Color PerfectColor = new Color(0.98f, 0.80f, 0.25f);
        private static readonly Color GoodColor = new Color(0.35f, 0.85f, 0.45f);
        private static readonly Color BadColor = new Color(0.95f, 0.55f, 0.20f);
        private static readonly Color MissColor = new Color(0.90f, 0.26f, 0.36f);

        private readonly List<Popup> pool = new List<Popup>();
        private int nextIndex;

        private class Popup
        {
            public RectTransform Rt;
            public Text Text;
            public Coroutine Running;
        }

        /// <summary>在宿主（HUD 全屏面板）下构建弹窗对象池。</summary>
        public void Setup(RectTransform host)
        {
            for (int i = 0; i < PoolSize; i++)
            {
                var rt = UiKit.Node(host, "JudgementPopup" + i);
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(320f, 64f);
                var text = rt.gameObject.AddComponent<Text>();
                text.font = UiKit.FontOrDefault;
                text.fontSize = 34;
                text.fontStyle = FontStyle.Bold;
                text.alignment = TextAnchor.MiddleCenter;
                text.raycastTarget = false;
                text.text = "";
                var outline = text.gameObject.AddComponent<Outline>();
                outline.effectColor = new Color(0f, 0f, 0f, 0.7f);
                outline.effectDistance = new Vector2(2f, -2f);
                rt.gameObject.SetActive(false);
                pool.Add(new Popup { Rt = rt, Text = text });
            }
        }

        /// <summary>弹出一次判定。deltaSeconds 为早负晚正的误差，用于 Miss 前的位置（当前仅按轨道展示）。</summary>
        public void Show(Gameplay.Judgement judgement, int lane, bool isTail)
        {
            Popup popup = pool[nextIndex];
            nextIndex = (nextIndex + 1) % pool.Count;
            if (popup.Running != null) StopCoroutine(popup.Running);

            popup.Text.text = judgement == Gameplay.Judgement.Perfect ? "PERFECT"
                : judgement == Gameplay.Judgement.Good ? "GOOD"
                : judgement == Gameplay.Judgement.Bad ? "BAD"
                : "MISS";
            popup.Text.color = judgement == Gameplay.Judgement.Perfect ? PerfectColor
                : judgement == Gameplay.Judgement.Good ? GoodColor
                : judgement == Gameplay.Judgement.Bad ? BadColor
                : MissColor;

            popup.Rt.anchoredPosition = LaneToCanvasPosition(lane);
            popup.Rt.localRotation = Quaternion.identity;
            popup.Rt.localScale = Vector3.one;
            popup.Rt.gameObject.SetActive(true);
            popup.Running = StartCoroutine(Animate(popup, judgement));
        }

        private IEnumerator Animate(Popup popup, Gameplay.Judgement judgement)
        {
            var rt = popup.Rt;
            if (judgement == Gameplay.Judgement.Miss)
            {
                // osu Miss 动效：微放大回落 + 下坠 + 旋转（InQuint）
                rt.localScale = new Vector3(1.3f, 1.3f, 1f);
                float t = 0f;
                while (t < 0.8f)
                {
                    t += Time.unscaledDeltaTime;
                    float k = Mathf.Clamp01(t / 0.8f);
                    float e = InQuint(k);
                    rt.localScale = Vector3.one * (1.3f - 0.3f * Mathf.Clamp01(k * 8f));
                    rt.anchoredPosition = LaneToCanvasPosition(-1) + new Vector2(0f, -e * 100f);
                    rt.localRotation = Quaternion.Euler(0f, 0f, -e * 40f);
                    SetAlpha(popup, 1f - k);
                    yield return null;
                }
            }
            else
            {
                // 命中：0.8→1 OutElastic 250ms；50ms 后 250ms 缩至 0.75 并在最后 200ms 淡出
                float t = 0f;
                while (t < 0.3f)
                {
                    t += Time.unscaledDeltaTime;
                    float k = Mathf.Clamp01(t / 0.25f);
                    float elastic = OutElastic(k);
                    float scale = Mathf.Lerp(0.8f, 1f, elastic);
                    if (t > 0.05f)
                    {
                        float k2 = Mathf.Clamp01((t - 0.05f) / 0.25f);
                        scale *= Mathf.Lerp(1f, 0.75f, k2);
                        if (k2 > 0.2f) SetAlpha(popup, 1f - (k2 - 0.2f) / 0.8f);
                    }
                    rt.localScale = new Vector3(scale, scale, 1f);
                    yield return null;
                }
            }
            rt.gameObject.SetActive(false);
            popup.Running = null;
        }

        private void SetAlpha(Popup popup, float alpha)
        {
            var c = popup.Text.color;
            popup.Text.color = new Color(c.r, c.g, c.b, Mathf.Clamp01(alpha));
        }

        // 轨道世界坐标 → HUD 画布局部坐标；lane=-1 时返回画布中下方（Miss 下坠基准）
        private Vector2 LaneToCanvasPosition(int lane)
        {
            float x = 0f;
            if (lane >= 0)
            {
                Camera cam = Camera.main;
                if (cam != null)
                {
                    Vector3 world = new Vector3(Gameplay.NoteSpawner.LaneX(lane), Gameplay.NoteSpawner.JudgeLineY + 0.9f, 0f);
                    Vector3 screen = cam.WorldToScreenPoint(world);
                    Canvas canvas = GetComponentInParent<Canvas>();
                    float factor = canvas != null ? canvas.scaleFactor : 1f;
                    // ScreenSpaceOverlay：屏幕坐标除以 scaleFactor 后以画布中心为原点
                    x = screen.x / factor - 960f;
                }
            }
            float y = -540f * 0.62f; // 判定线大约在屏幕下 1/3 处（UiKit 参考分辨率 1920x1080）
            return new Vector2(x, y);
        }

        private static float InQuint(float t) { return t * t * t * t * t; }

        private static float OutElastic(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            float p = 0.3f;
            return Mathf.Pow(2f, -10f * t) * Mathf.Sin((t - p / 4f) * (2f * Mathf.PI / p)) + 1f;
        }
    }
}
