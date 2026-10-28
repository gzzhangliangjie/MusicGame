using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace MusicGame.UI
{
    /// <summary>
    /// 歌词消散效果：Progress 0→1 时逐字沿确定性随机方向飘散 + 绕字符中心微旋转 + 淡出。
    /// 顶点级实现，按 6 顶点/字符的三角流分组——必须挂在 Outline 之后、Shadow 之前
    /// （这样投影会跟随消散后的字形一起走）。由 GameFlow.LyricSwap 在旧行退场时驱动；
    /// Progress<=0 直通不干预。
    /// </summary>
    public class LyricScatterEffect : BaseMeshEffect
    {
        [SerializeField] private float spread = 90f;    // 水平飘散幅度
        [SerializeField] private float rise = 60f;      // 整体上飘
        [SerializeField] private float stagger = 0.35f; // 字符间错峰占比（后面的字更晚散）
        [SerializeField] private float spin = 50f;      // 字符旋转幅度（度）

        private float progress;

        /// <summary>消散进度 0=完整 1=完全消散；变化时标脏重建顶点。</summary>
        public float Progress
        {
            get { return progress; }
            set
            {
                if (Mathf.Approximately(progress, value)) return;
                progress = value;
                if (graphic != null) graphic.SetVerticesDirty();
            }
        }

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive() || progress <= 0f || vh.currentVertCount == 0) return;
            var stream = new List<UIVertex>(vh.currentVertCount);
            vh.GetUIVertexStream(stream);

            int total = stream.Count / 6; // 三角流：6 顶点一个字符
            if (total == 0) return;
            float p = Mathf.Clamp01(progress);
            float staggerSpan = Mathf.Max(0.001f, 1f - stagger);

            for (int c = 0; c < total; c++)
            {
                // 每字符错峰：左右交替+越靠后越慢
                float delay = stagger * ((c % 2 == 0 ? c : total - c) / (float)total);
                float cp = Mathf.Clamp01((p - delay) / staggerSpan);
                if (cp <= 0f) continue;
                float e = cp * cp * (3f - 2f * cp); // smoothstep

                // 确定性伪随机方向（按字符序号，稳定不闪）
                float r1 = Mathf.Abs(Mathf.Sin(c * 127.1f + 311.7f) * 43758.5453f) % 1f;
                float r2 = Mathf.Abs(Mathf.Sin(c * 269.5f + 183.3f) * 24634.6345f) % 1f;
                Vector2 drift = new Vector2((r1 * 2f - 1f) * spread, r2 * 40f + rise * e) * e;
                float angle = ((r2 * 2f - 1f) * spin) * e * Mathf.Deg2Rad;
                float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);

                int b0 = c * 6;
                // 字符中心（6 顶点平均）作为旋转中心
                Vector2 center = Vector2.zero;
                for (int k = 0; k < 6; k++) center += (Vector2)stream[b0 + k].position;
                center /= 6f;

                for (int k = 0; k < 6; k++)
                {
                    int idx = b0 + k;
                    var v = stream[idx];
                    Vector2 pos = (Vector2)v.position + drift;
                    Vector2 rel = pos - center;
                    pos = center + new Vector2(rel.x * cos - rel.y * sin, rel.x * sin + rel.y * cos);
                    v.position = new Vector3(pos.x, pos.y, v.position.z);
                    var col = v.color;
                    col.a = (byte)(col.a * (1f - e));
                    v.color = col;
                    stream[idx] = v;
                }
            }
            vh.Clear();
            vh.AddUIVertexTriangleStream(stream);
        }
    }
}
