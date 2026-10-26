using UnityEngine;
using UnityEngine.UI;

namespace MusicGame.UI
{
    /// <summary>
    /// 动态背景（uGUI 最底层，SetAsFirstSibling）：两块巨幅柔光对转 + 漂浮微光粒子。
    /// 纯装饰，零交互；让菜单/等待界面"活着"而不是死色块。
    /// </summary>
    public class UiBackdrop : MonoBehaviour
    {
        private RectTransform washA;
        private RectTransform washB;
        private RectTransform[] dots;
        private float[] dotSpeed;

        private const int DotCount = 14;

        /// <summary>挂在宿主 Canvas 下并置于最底层。</summary>
        public void Setup(RectTransform host)
        {
            transform.SetSiblingIndex(0);

            washA = MakeWash("washA", UiKit.Theme.Primary, 0.10f, 2600f, new Vector3(-520f, -380f, 0f));
            washB = MakeWash("washB", UiKit.Theme.Secondary, 0.08f, 2200f, new Vector3(560f, 420f, 0f));

            dots = new RectTransform[DotCount];
            dotSpeed = new float[DotCount];
            for (int i = 0; i < DotCount; i++)
            {
                var dot = UiKit.Node(host, "dot" + i);
                var img = dot.gameObject.AddComponent<Image>();
                img.sprite = UiKit.SoftDotSprite;
                img.raycastTarget = false;
                float size = Random.Range(10f, 30f);
                dot.sizeDelta = new Vector2(size, size);
                dot.anchoredPosition = new Vector2(Random.Range(-940f, 940f), Random.Range(-540f, 540f));
                img.color = new Color(1f, 1f, 1f, Random.Range(0.04f, 0.12f));
                dots[i] = dot;
                dotSpeed[i] = Random.Range(14f, 42f);
            }
        }

        private RectTransform MakeWash(string name, Color color, float alpha, float size, Vector3 pos)
        {
            var wash = UiKit.Node(transform, name);
            var img = wash.gameObject.AddComponent<Image>();
            img.sprite = UiKit.SoftDotSprite;
            img.raycastTarget = false;
            img.color = new Color(color.r, color.g, color.b, alpha);
            wash.sizeDelta = new Vector2(size, size);
            wash.localPosition = pos;
            return wash;
        }

        private void Update()
        {
            if (washA != null) washA.Rotate(0f, 0f, 3.5f * Time.unscaledDeltaTime);
            if (washB != null) washB.Rotate(0f, 0f, -2.4f * Time.unscaledDeltaTime);
            if (dots == null) return;
            for (int i = 0; i < dots.Length; i++)
            {
                var pos = dots[i].anchoredPosition;
                pos.y += dotSpeed[i] * Time.unscaledDeltaTime;
                pos.x += Mathf.Sin((Time.unscaledTime + i * 1.7f) * 0.7f) * 6f * Time.unscaledDeltaTime;
                if (pos.y > 560f) pos.y = -560f;
                dots[i].anchoredPosition = pos;
            }
        }
    }
}
