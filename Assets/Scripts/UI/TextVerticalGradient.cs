using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace MusicGame.UI
{
    /// <summary>文字竖向渐变（顶白 → 底金）：给大字歌词/标题以判定艺术字同款的"金渐变"语言。</summary>
    [RequireComponent(typeof(Graphic))]
    public class TextVerticalGradient : BaseMeshEffect
    {
        [SerializeField] private Color topColor = Color.white;
        [SerializeField] private Color bottomColor = new Color(1f, 0.80f, 0.34f);

        /// <summary>运行时改渐变色（如主菜单切主题后重染标题），标脏立即重建顶点。</summary>
        public void SetColors(Color top, Color bottom)
        {
            topColor = top;
            bottomColor = bottom;
            if (graphic != null) graphic.SetVerticesDirty();
        }

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive() || vh.currentVertCount == 0) return;
            var stream = new List<UIVertex>(vh.currentVertCount);
            vh.GetUIVertexStream(stream);

            float top = float.MinValue, bottom = float.MaxValue;
            for (int i = 0; i < stream.Count; i++)
            {
                float y = stream[i].position.y;
                if (y > top) top = y;
                if (y < bottom) bottom = y;
            }
            float range = Mathf.Max(0.001f, top - bottom);

            for (int i = 0; i < stream.Count; i++)
            {
                var v = stream[i];
                float t = (v.position.y - bottom) / range;
                var grad = Color32.Lerp(bottomColor, topColor, t);
                var c = v.color; // 与文字本色相乘，保留 CanvasGroup/染色能力
                v.color = new Color32(
                    (byte)(c.r * grad.r / 255f), (byte)(c.g * grad.g / 255f),
                    (byte)(c.b * grad.b / 255f), c.a);
                stream[i] = v;
            }
            vh.Clear();
            vh.AddUIVertexTriangleStream(stream);
        }
    }
}
