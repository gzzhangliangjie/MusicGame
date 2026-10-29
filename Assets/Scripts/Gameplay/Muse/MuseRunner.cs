using System.Collections;
using MusicGame.Core;
using UnityEngine;

namespace MusicGame.Gameplay.Muse
{
    /// <summary>
    /// 跑酷模式角色：在地面线上自动奔跑（摆腿/上下起伏），地面攻击前倾挥斩，
    /// 空中攻击跳向空中线挥斩后落回，敌人撞过（Miss）时抖动闪红。程序化贴图，皮肤包可替换整体。
    /// </summary>
    public class MuseRunner : MonoBehaviour
    {
        private MuseSpawner spawner;
        private Transform bodyRoot;          // 身体（不含脚）：起伏/前倾作用于此
        private SpriteRenderer bodyRenderer;
        private Transform footL, footR;
        private Coroutine groundSlashHandle, airSlashHandle, flinchHandle;
        private Vector3 bodyRootBase;
        private bool romMode;                // 原版图块模式：姿态帧切换（站/走1-3/跳/急停）
        private Sprite[] romFrames;
        private int frameOverride = -1;      // 攻击姿态覆盖：5=急停(地面斩) 4=跳(空中斩)
        private float runPhase;

        /// <summary>装配角色（位置由 Spawner 定在角色判定点/地面线上）。两级回退：
        /// ① 原版图块（本地 Rom/CHR 的马里奥姿态帧，不入仓库）② 程序化自绘小人（保底）。</summary>
        public void Setup(MuseSpawner owner)
        {
            spawner = owner;
            transform.localPosition = new Vector3(MuseSpawner.CharacterX, MuseSpawner.GroundY, 0f);
            romMode = MuseSmbSkin.Available;

            bodyRoot = new GameObject("Body").transform;
            bodyRoot.SetParent(transform, false);
            bodyRootBase = new Vector3(0f, 0.56f, 0f);
            bodyRoot.localPosition = bodyRootBase;

            if (romMode)
            {
                romFrames = new[]
                {
                    MuseSmbSkin.Mario(0), MuseSmbSkin.Mario(1), MuseSmbSkin.Mario(2),
                    MuseSmbSkin.Mario(3), MuseSmbSkin.Mario(4), MuseSmbSkin.Mario(5),
                };
                // 贴图 pivot=中心（16x16 原生 1 单位 × 1.1 缩放）：抬高半身高让脚踩在地面线上
                bodyRootBase = new Vector3(0f, 0.56f, 0f);
                bodyRoot.localPosition = bodyRootBase;
                bodyRenderer = bodyRoot.gameObject.AddComponent<SpriteRenderer>();
                bodyRenderer.sprite = romFrames[0];
                bodyRenderer.sortingOrder = 20; // 角色最前：跑酷主角不能被敌人（10）和长条（8/9）盖住
                bodyRoot.localScale = Vector3.one * 1.1f;
                return; // 贴图自带脚，不建独立动画脚
            }

            var bodyGo = new GameObject("BodySprite");
            bodyGo.transform.SetParent(bodyRoot, false);
            bodyRenderer = bodyGo.AddComponent<SpriteRenderer>();
            bodyRenderer.sprite = GetCharSprite();
            bodyRenderer.sortingOrder = 20;
            Fit(bodyRenderer.transform, GetCharSprite(), 1.05f);

            footL = CreateFoot("FootL", new Vector3(-0.16f, 0.10f, 0f));
            footR = CreateFoot("FootR", new Vector3(0.16f, 0.06f, 0f));
        }

        private Transform CreateFoot(string name, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPos;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Taiko.TaikoSpawner.GetDiscSprite();
            sr.color = new Color(0.42f, 0.24f, 0.12f, 1f); // 棕色小鞋：与贴图内鞋子同色系
            sr.sortingOrder = 19;
            go.transform.localScale = Vector3.one * 0.22f;
            return go.transform;
        }

        private void Update()
        {
            if (spawner == null) return;
            // 跑动：速度感步频
            float speed = (MuseSpawner.SpawnX - MuseSpawner.CharacterX) / Mathf.Max(0.05f, spawner.TravelTime);
            runPhase += Time.deltaTime * speed * 2.2f;

            if (romMode)
            {
                // 姿态帧：攻击协程期间用覆盖帧（急停/跳），平时走路循环 1→2→3
                if (bodyRenderer != null)
                {
                    int frame = frameOverride >= 0 ? frameOverride : 1 + (int)(runPhase * 1.5f) % 3;
                    var s = romFrames[frame];
                    if (s != null && bodyRenderer.sprite != s) bodyRenderer.sprite = s;
                }
                return;
            }

            // 自绘小人：身体上下起伏 + 双脚交替迈步（攻击协程期间脚收回）
            float bob = Mathf.Abs(Mathf.Sin(runPhase)) * 0.045f;
            bodyRoot.localPosition = bodyRootBase + new Vector3(0f, bob, 0f);
            if (footL != null && footR != null && groundSlashHandle == null && airSlashHandle == null)
            {
                footL.localPosition = new Vector3(-0.16f, 0.10f + Mathf.Max(0f, Mathf.Sin(runPhase)) * 0.14f, 0f);
                footR.localPosition = new Vector3(0.16f, 0.10f + Mathf.Max(0f, Mathf.Sin(runPhase + Mathf.PI)) * 0.14f, 0f);
            }
        }

        /// <summary>攻击反馈（头判命中非 Miss 时由 Spawner 调用）：lane 0 原地挥斩，lane 1 跳斩。</summary>
        public void Attack(int lane)
        {
            if (lane == 1)
            {
                if (airSlashHandle != null) StopCoroutine(airSlashHandle);
                airSlashHandle = StartCoroutine(AirAttackRoutine());
            }
            else
            {
                if (groundSlashHandle != null) StopCoroutine(groundSlashHandle);
                groundSlashHandle = StartCoroutine(GroundAttackRoutine());
            }
        }

        /// <summary>敌人撞过（Miss）反馈：左右抖动 + 身体闪红。</summary>
        public void Flinch()
        {
            if (flinchHandle != null) StopCoroutine(flinchHandle);
            flinchHandle = StartCoroutine(FlinchRoutine());
        }

        // 地面攻击：前倾压身 0.14s 回弹 + 地面线斩击弧（ROM 模式配急停姿态）
        private IEnumerator GroundAttackRoutine()
        {
            const float Dur = 0.16f;
            SpawnSlash(MuseSpawner.GroundY + 0.25f);
            frameOverride = 5;
            float t = 0f;
            while (t < Dur)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / Dur);
                float e = Mathf.Sin(k * Mathf.PI);
                bodyRoot.localRotation = Quaternion.Euler(0f, 0f, -14f * e);
                bodyRoot.localScale = new Vector3(1f + 0.08f * e, 1f - 0.10f * e, 1f);
                yield return null;
            }
            bodyRoot.localRotation = Quaternion.identity;
            bodyRoot.localScale = Vector3.one;
            frameOverride = -1;
            groundSlashHandle = null;
        }

        // 空中攻击：0.12s 跳向空中线（留半个身位高度差），斩击后 0.22s 落回（ROM 模式配跳跃姿态）
        private IEnumerator AirAttackRoutine()
        {
            SpawnSlash(MuseSpawner.AirY + 0.1f);
            frameOverride = 4;
            Vector3 up = new Vector3(0f, MuseSpawner.AirY - MuseSpawner.GroundY - 0.5f, 0f);
            float t = 0f;
            while (t < 0.12f)
            {
                t += Time.deltaTime;
                float k = t / 0.12f;
                transform.localPosition = new Vector3(MuseSpawner.CharacterX, MuseSpawner.GroundY, 0f) + up * (k * k * (3f - 2f * k));
                yield return null;
            }
            transform.localPosition = new Vector3(MuseSpawner.CharacterX, MuseSpawner.GroundY, 0f) + up;
            yield return new WaitForSecondsRealtime(0.06f);
            t = 0f;
            while (t < 0.22f)
            {
                t += Time.deltaTime;
                float k = t / 0.22f;
                transform.localPosition = new Vector3(MuseSpawner.CharacterX, MuseSpawner.GroundY, 0f) + up * (1f - k * k);
                yield return null;
            }
            transform.localPosition = new Vector3(MuseSpawner.CharacterX, MuseSpawner.GroundY, 0f);
            frameOverride = -1;
            airSlashHandle = null;
        }

        private IEnumerator FlinchRoutine()
        {
            const float Dur = 0.22f;
            float t = 0f;
            Color baseColor = bodyRenderer != null ? bodyRenderer.color : Color.white;
            while (t < Dur)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / Dur);
                float e = 1f - k;
                transform.localPosition = new Vector3(
                    MuseSpawner.CharacterX + Mathf.Sin(k * 40f) * 0.09f * e, MuseSpawner.GroundY, 0f);
                if (bodyRenderer != null)
                    bodyRenderer.color = Color.Lerp(baseColor, new Color(1f, 0.45f, 0.45f), e * 0.8f);
                yield return null;
            }
            transform.localPosition = new Vector3(MuseSpawner.CharacterX, MuseSpawner.GroundY, 0f);
            if (bodyRenderer != null) bodyRenderer.color = baseColor;
            flinchHandle = null;
        }

        /// <summary>在指定高度生成一道斩击弧（白色扇光，向右前方展开后快速淡出）。</summary>
        private void SpawnSlash(float y)
        {
            var go = new GameObject("Slash");
            go.transform.SetParent(spawner.transform, false);
            go.transform.localPosition = new Vector3(MuseSpawner.CharacterX + 0.95f, y, 0f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = MuseSpawner.GetSlashSprite();
            sr.sortingOrder = 22; // 斩击弧压在角色之上
            sr.color = new Color(1f, 0.97f, 0.82f, 0.95f);
            StartCoroutine(SlashRoutine(sr));
        }

        private static IEnumerator SlashRoutine(SpriteRenderer sr)
        {
            const float Dur = 0.17f;
            float t = 0f;
            while (t < Dur && sr != null)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / Dur);
                sr.transform.localScale = Vector3.one * (0.6f + 0.8f * k);
                sr.transform.localRotation = Quaternion.Euler(0f, 0f, -18f * k);
                var c = sr.color;
                sr.color = new Color(c.r, c.g, c.b, 0.95f * (1f - k));
                yield return null;
            }
            if (sr != null) Destroy(sr.gameObject);
        }

        /// <summary>角色贴图（程序化自绘致敬版）：红帽+檐、大鼻子、胡子、蓝背带裤的管道工风小人——原创绘制，非任天堂素材。</summary>
        private static Sprite GetCharSprite()
        {
            const int w = 96, h = 112;
            var texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[w * h];
            var capCol = new Color32(210, 38, 40, 255);
            var capDark = new Color32(156, 26, 30, 255);
            var skin = new Color32(250, 214, 176, 255);
            var skinDark = new Color32(228, 184, 144, 255);
            var hair = new Color32(74, 44, 26, 255);
            var blue = new Color32(56, 88, 192, 255);
            var blueDark = new Color32(38, 60, 144, 255);
            var shoe = new Color32(104, 60, 30, 255);
            var eye = new Color32(40, 32, 30, 255);
            var white = new Color32(252, 250, 246, 255);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    Color32 col;
                    // 自上而下绘制（链首在最上层）：帽徽→帽檐→帽顶→眼→鼻→胡→鬓→头→手→袖→扣→裤→鞋
                    if (In(px, py, 44, 97, 6f, 5.5f)) col = white;                       // 帽徽白底
                    else if (In(px, py, 44, 97, 3f, 3f)) col = capCol;                   // 帽徽红芯
                    else if (py > 86f && py < 93f && px >= 46f && px < 80f) col = capDark; // 帽檐（朝右）
                    else if (In(px, py, 47, 96, 21f, 13f)) col = capCol;                 // 帽顶
                    else if (In(px, py, 57, 84, 3.5f, 5f)) col = eye;                    // 眼
                    else if (In(px, py, 66, 74, 8f, 6f)) col = skinDark;                 // 大鼻子
                    else if (py > 69f && py < 76f && px > 33f && px < 63f) col = hair;   // 胡子
                    else if (In(px, py, 31, 80, 5f, 6f)) col = hair;                     // 鬓角
                    else if (In(px, py, 48, 80, 20f, 18f)) col = skin;                   // 头
                    else if (In(px, py, 26, 36, 7f, 7f) || In(px, py, 70, 36, 7f, 7f)) col = skin; // 手
                    else if (In(px, py, 24, 52, 9f, 16f) || In(px, py, 72, 52, 9f, 16f)) col = capCol; // 红袖
                    else if (In(px, py, 40, 58, 3f, 3f) || In(px, py, 56, 58, 3f, 3f)) col = white;    // 背带扣
                    else if (In(px, py, 48, 42, 22f, 26f)) col = py < 40f ? blueDark : blue;           // 背带裤
                    else if (In(px, py, 34, 10, 15f, 8f) || In(px, py, 62, 10, 15f, 8f)) col = shoe;   // 鞋
                    else col = new Color32(0, 0, 0, 0);
                    pixels[y * w + x] = col;
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 96f);
            sprite.name = "MuseChar";
            return sprite;
        }

        private static bool In(float px, float py, float cx, float cy, float rx, float ry)
        {
            float dx = (px - cx) / rx, dy = (py - cy) / ry;
            return dx * dx + dy * dy <= 1f;
        }

        private static void Fit(Transform t, Sprite sprite, float targetHeight)
        {
            var size = sprite.bounds.size;
            float k = targetHeight / Mathf.Max(0.01f, size.y);
            t.localScale = new Vector3(size.x * k, size.y * k, 1f);
        }
    }
}
