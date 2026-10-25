using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace MusicGame.UI
{
    /// <summary>
    /// Hit-error 误差条（uGUI）：实时展示最近若干次头判的早/晚偏移（早负晚正，±80ms 满量程），
    /// 中央高亮区为 Perfect 窗（±40ms）。音游标准自评工具，用于校准手感与延迟。
    /// </summary>
    public class HitErrorBar : MonoBehaviour
    {
        private const float FullScaleSeconds = 0.08f;  // ±80ms 映射满宽
        private const float BarWidth = 480f;
        private const int TickPoolSize = 12;
        private const float TickLifeSeconds = 1.2f;

        private static readonly Color PerfectColor = new Color(0.98f, 0.80f, 0.25f);
        private static readonly Color GreatColor = new Color(0.35f, 0.85f, 0.45f);

        private readonly List<Tick> pool = new List<Tick>();
        private int nextIndex;
        private Image perfectZone;

        private class Tick
        {
            public Image Image;
            public float Age;
            public bool Active;
        }

        /// <summary>在宿主下构建误差条（背景条 + Perfect 区 + 刻度池 + 早/晚标注）。</summary>
        public void Setup(RectTransform host)
        {
            var root = UiKit.Node(host, "HitErrorBar");
            root.anchorMin = new Vector2(0.5f, 0.5f);
            root.anchorMax = new Vector2(0.5f, 0.5f);
            root.anchoredPosition = new Vector2(0f, -540f * 0.62f - 56f); // 判定线弹窗下方一行
            root.sizeDelta = new Vector2(BarWidth, 14f);

            var bg = root.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.35f);
            bg.raycastTarget = false;

            // Perfect 窗高亮区（±40ms 占满宽一半）
            var zoneRt = UiKit.Node(root, "perfectZone");
            UiKit.Stretch(zoneRt, new Vector2(0.25f, 0.1f), new Vector2(0.75f, 0.9f));
            perfectZone = zoneRt.gameObject.AddComponent<Image>();
            perfectZone.color = new Color(PerfectColor.r, PerfectColor.g, PerfectColor.b, 0.22f);
            perfectZone.raycastTarget = false;

            // 中线
            var centerRt = UiKit.Node(root, "center");
            UiKit.Stretch(centerRt, new Vector2(0.5f - 1f / BarWidth, 0f), new Vector2(0.5f + 1f / BarWidth, 1f));
            var center = centerRt.gameObject.AddComponent<Image>();
            center.color = new Color(1f, 1f, 1f, 0.75f);
            center.raycastTarget = false;

            // 早/晚标注
            var early = UiKit.Label(root, "early", "早", 20, TextAnchor.MiddleRight, UiKit.Theme.Dim);
            UiKit.Stretch(early.rectTransform, new Vector2(-0.18f, 0f), new Vector2(-0.02f, 1f));
            var late = UiKit.Label(root, "late", "晚", 20, TextAnchor.MiddleLeft, UiKit.Theme.Dim);
            UiKit.Stretch(late.rectTransform, new Vector2(0.02f, 0f), new Vector2(0.18f, 1f));

            // 刻度池
            for (int i = 0; i < TickPoolSize; i++)
            {
                var tickRt = UiKit.Node(root, "tick" + i);
                tickRt.anchorMin = new Vector2(0.5f, 0.5f);
                tickRt.anchorMax = new Vector2(0.5f, 0.5f);
                tickRt.sizeDelta = new Vector2(3f, 20f);
                tickRt.gameObject.AddComponent<Image>().raycastTarget = false;
                tickRt.gameObject.SetActive(false);
                pool.Add(new Tick { Image = tickRt.gameObject.GetComponent<Image>(), Age = 0f, Active = false });
            }
        }

        /// <summary>记录一次头判偏移（秒，早负晚正）。Miss 无刻度。</summary>
        public void AddTick(Gameplay.Judgement judgement, double deltaSeconds)
        {
            if (judgement == Gameplay.Judgement.Miss) return;
            Tick tick = pool[nextIndex];
            nextIndex = (nextIndex + 1) % pool.Count;

            float normalized = Mathf.Clamp((float)deltaSeconds / FullScaleSeconds, -1f, 1f);
            var rt = tick.Image.rectTransform;
            rt.anchoredPosition = new Vector2(normalized * BarWidth * 0.5f, 0f);
            Color baseColor = judgement == Gameplay.Judgement.Perfect ? PerfectColor : GreatColor;
            tick.Image.color = baseColor;
            tick.Image.gameObject.SetActive(true);
            tick.Age = 0f;
            tick.Active = true;
        }

        private void Update()
        {
            for (int i = 0; i < pool.Count; i++)
            {
                Tick tick = pool[i];
                if (!tick.Active) continue;
                tick.Age += Time.deltaTime;
                if (tick.Age >= TickLifeSeconds)
                {
                    tick.Active = false;
                    tick.Image.gameObject.SetActive(false);
                    continue;
                }
                float alpha = 1f - tick.Age / TickLifeSeconds;
                var c = tick.Image.color;
                tick.Image.color = new Color(c.r, c.g, c.b, alpha);
            }
        }
    }
}
