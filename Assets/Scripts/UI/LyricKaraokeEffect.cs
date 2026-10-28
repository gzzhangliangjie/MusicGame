using UnityEngine;
using UnityEngine.UI;

namespace MusicGame.UI
{
    /// <summary>
    /// 卡拉OK逐字渐亮：按 Progress(0..1) 自左向右擦亮 UGUI Text 的字符。
    /// 未唱字符降为半透明残影，已唱字符恢复本色，前沿一个字宽内做平滑过渡（字内左右顶点插值）。
    /// 前提：必须先于其他 IMeshModifier（渐变/描边/阴影会展开三角流或复制顶点）执行，
    /// 因此 AddComponent 顺序要最早，拿到的才是每字符四顶点的四边形流；顶点序 0=左上 1=右上 2=右下 3=左下。
    /// </summary>
    public class LyricKaraokeEffect : BaseMeshEffect
    {
        [Range(0f, 1f)][SerializeField] private float progress = 1f;
        [Range(0f, 1f)][SerializeField] private float dimAlpha = 0.32f; // 未唱字符的透明度系数

        /// <summary>已唱比例（按可见字符数均分，LRC 无逐字时间戳）。变更即触发网格重刷。</summary>
        public float Progress
        {
            get => progress;
            set
            {
                float v = Mathf.Clamp01(value);
                if (Mathf.Approximately(v, progress)) return;
                progress = v;
                if (graphic != null) graphic.SetVerticesDirty();
            }
        }

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive() || dimAlpha >= 1f || progress >= 1f) return;
            int count = vh.currentVertCount;
            if (count == 0 || count % 4 != 0) return; // 非四顶点流：布局被其他修饰改过，放弃而不损坏网格

            int chars = count / 4;
            float frontier = progress * chars; // 擦亮前沿（字符域）
            for (int c = 0; c < chars; c++)
            {
                float litLeft = LitFactor(c, frontier);
                float litRight = LitFactor(c + 1, frontier);
                if (litLeft >= 1f && litRight >= 1f) continue; // 整字已亮：保持本色免改写

                for (int v = 0; v < 4; v++)
                {
                    var vert = new UIVertex();
                    vh.PopulateUIVertex(ref vert, c * 4 + v);
                    float lit = (v == 1 || v == 2) ? litRight : litLeft;
                    vert.color = new Color32(vert.color.r, vert.color.g, vert.color.b,
                        (byte)(vert.color.a * Mathf.Lerp(dimAlpha, 1f, lit)));
                    vh.SetUIVertex(vert, c * 4 + v);
                }
            }
        }

        // 字符域位置 x 相对前沿的擦亮系数：前沿处 0.5，前半个字宽内平滑升至 1，后半个字宽内降至 0
        private static float LitFactor(float x, float frontier)
        {
            float t = Mathf.Clamp01(frontier - x + 0.5f);
            return t * t * (3f - 2f * t);
        }
    }
}
