using System;
using UnityEngine;
using UnityEngine.Video;

namespace MusicGame.Gameplay
{
    /// <summary>
    /// MV 背景打底：世界空间远处 Quad + VideoPlayer(RenderTexture)，
    /// 半透明（Alpha）铺满相机视野，压低存在感不抢音符轨道。
    /// 音频轨不输出（曲声来自 audioSource 的 audio.mp3）；视频文件按歌曲目录 mv.mp4 约定，
    /// 由 GameManager 在存在文件时创建——缺文件零开销零影响。
    /// 随 GameManager.SetPaused 同步暂停/恢复（压制位不变量：暂停期内播放器若自行恢复
    /// ——循环边界/seek 竞态——Update 逐帧压回）；宿主销毁时释放 RT 与材质。
    /// </summary>
    public class MvBackdrop : MonoBehaviour
    {
        /// <summary>背景透明度（0..1）——低到不喧宾夺主，高到还认得出是 MV。</summary>
        public const float Alpha = 0.35f;
        /// <summary>背景平面世界 z（相机 z=-10、音符全在 z=0，取其后方即可，正交视野与深度无关）。</summary>
        public const float DepthZ = 6f;
        /// <summary>解码纹理宽上限（源超过则等比降，防极端大源爆显存）。</summary>
        private const int MaxTextureWidth = 1280;
        /// <summary>排序序：垫到场地底板（-4）之下，全场最底。</summary>
        private const int SortingOrder = -10;
        /// <summary>MV 横带高度占视野比例（顶部贴边向下）——避开下方判定/键帽区。</summary>
        private const float BandHeightFraction = 0.40f;
        /// <summary>横带整体放大倍数（用户 2026-10-27 定：长宽均×1.5）。</summary>
        private const float BandScale = 1.5f;
        /// <summary>带底安全线：放大后带底不得低于此 y（判定线 -4 的上方 2 个单位）。</summary>
        private const float BandBottomMinY = -2f;

        private VideoPlayer player;
        private Renderer quadRenderer;
        private Material quadMaterial;
        private RenderTexture texture;
        private Camera cam;
        private float videoAspect = 16f / 9f;
        private int lastScreenWidth = -1;
        private int lastScreenHeight = -1;
        private bool startRequested;
        private double startSeconds;
        private bool pendingStartSeek;
        private float seekStartedRealtime = -1f;
        private bool seekGiveUpLogged;
        private bool firstFrameLogged;
        /// <summary>游戏暂停压制中：置位后播放器不得出帧，Update 逐帧纠偏（现场实测 VideoPlayer.Pause
        /// 会被循环边界重启竞态顶掉，须主动压回）。</summary>
        private bool suppressed;
        /// <summary>压制期基线帧号：tripwire 按"帧号变了"判推进，不信任 isPlaying（实测 Pause 后
        /// isPlaying=false 帧仍推进）。</summary>
        private long suppressedFrame = long.MinValue;
        private int suppressViolations;

        /// <summary>创建 MV 背景；视频不存在或相机缺失返回 null（调用方免判）。
        /// startSeconds = 歌曲起点对应的 MV 内时间（对不齐的 MV 从此处起播，负值按 0 处理）。</summary>
        public static MvBackdrop Create(Transform parent, string videoPath, Camera camera, double startSeconds = 0.0)
        {
            if (string.IsNullOrEmpty(videoPath) || !System.IO.File.Exists(videoPath) || camera == null) return null;
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(go.GetComponent<Collider>()); // 纯视觉，不吃碰撞/射线
            go.name = "MvBackdrop";
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, DepthZ);
            MvBackdrop backdrop = go.AddComponent<MvBackdrop>();
            backdrop.startSeconds = System.Math.Max(0.0, startSeconds);
            backdrop.Setup(videoPath, camera);
            return backdrop;
        }

        private void Setup(string videoPath, Camera camera)
        {
            cam = camera;
            quadRenderer = GetComponent<MeshRenderer>();
            quadRenderer.sortingOrder = SortingOrder;
            // 自带 shader（Resources 内保证打进包，构建不被裁剪）；编辑器回退精灵 shader
            Shader shader = Shader.Find("MusicGame/MvBackdrop");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            quadMaterial = new Material(shader); // 透明队列 + 可调 alpha，与音符精灵同语言
            quadMaterial.color = new Color(1f, 1f, 1f, Alpha);
            quadRenderer.material = quadMaterial;
            quadRenderer.enabled = false; // 首帧解码就绪再亮，避免黑块闪烁

            player = gameObject.AddComponent<VideoPlayer>();
            player.playOnAwake = false;
            player.source = VideoSource.Url;
            player.url = new Uri(videoPath).AbsoluteUri;
            player.renderMode = VideoRenderMode.RenderTexture;
            player.audioOutputMode = VideoAudioOutputMode.None;
            player.isLooping = true; // 短于歌曲的 MV 循环续氛围
            player.errorReceived += OnVideoError;
            player.prepareCompleted += OnPrepared;
            player.Prepare();
        }

        private void OnVideoError(VideoPlayer source, string message)
        {
            Debug.LogWarning("[MvBackdrop] video error: " + message);
            if (quadRenderer != null) quadRenderer.enabled = false;
        }

        private void OnPrepared(VideoPlayer source)
        {
            int w = source.width > 0 ? (int)source.width : 640;
            int h = source.height > 0 ? (int)source.height : 480;
            if (w > MaxTextureWidth)
            {
                h = Mathf.Max(2, h * MaxTextureWidth / w);
                w = MaxTextureWidth;
            }
            videoAspect = (float)w / Mathf.Max(1, h);

            if (texture != null)
            {
                texture.Release();
                Destroy(texture);
            }
            texture = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            texture.Create();
            var prev = RenderTexture.active; // 未初始化显存是彩色噪声（用户看到"全红"），先清成黑
            RenderTexture.active = texture;
            GL.Clear(false, true, Color.black);
            RenderTexture.active = prev;
            quadMaterial.mainTexture = texture; // 绑定显示链：不绑的话 quad 显示的是默认纹理
            player.targetTexture = texture;
            if (quadRenderer != null) quadRenderer.enabled = true;
            RefreshBandFit();
            Debug.Log(string.Format("[MvBackdrop] 解码就绪 {0}x{1}，起播偏移 {2:F2}s", w, h, startSeconds));
            // 对不齐的 MV：记录待 seek（真正赋值放 Update——开播早期赋值会被播放器吞，
            // 逐帧重试直到落点生效）
            if (startSeconds > 0.01 && player.frameRate > 0)
                pendingStartSeek = true;
            if (startRequested && !suppressed && !player.isPlaying) player.Play(); // 开拍先于解码就绪时补播（暂停压制期不出帧）
        }

        /// <summary>与音乐同步开播（GameManager 时钟启动后调用；解码未就绪则记意图，OnPrepared 补播）。</summary>
        public void Play()
        {
            startRequested = true;
            if (player != null && player.isPrepared && !player.isPlaying && !suppressed)
            {
                player.playbackSpeed = 1f;
                player.Play();
            }
        }

        /// <summary>暂停中开局：记开播意图（OnPrepared 补播/Resume 都依赖），但压制起播，Resume() 时补播。</summary>
        public void PlayPaused()
        {
            startRequested = true;
            suppressed = true;
        }

        public void Pause()
        {
            suppressed = true;
            if (player != null && player.isPlaying)
            {
                player.Pause();
                // 双保险：实测 Pause() 后 isPlaying=false 帧仍往 RT 推，playbackSpeed=0 直接掐解码时钟
                player.playbackSpeed = 0f;
                suppressedFrame = player.frame;
                suppressViolations = 0;
                Debug.Log("[MvBackdrop] 暂停 @frame=" + player.frame + "（playbackSpeed=0 压制）");
            }
        }

        public void Resume()
        {
            suppressed = false;
            suppressedFrame = long.MinValue;
            if (player != null)
            {
                player.playbackSpeed = 1f;
                if (startRequested && player.isPrepared && !player.isPlaying) player.Play();
            }
        }

        private void Update()
        {
            // 暂停压制不变量：压制期帧号不得推进（不信任 isPlaying——实测 Pause 后它报 false 帧照走）；
            // 推进了就再刹一次并留全量诊断，供定位 Playback 引擎层根因
            if (suppressed && player != null && suppressedFrame != long.MinValue && player.frame != suppressedFrame)
            {
                suppressedFrame = player.frame;
                suppressViolations++;
                Debug.LogWarning("[MvBackdrop] 压制期帧仍推进 ->frame=" + player.frame
                    + " isPlaying=" + player.isPlaying + " speed=" + player.playbackSpeed
                    + " loop=" + player.isLooping + "，第 " + suppressViolations + " 次压回");
                if (player.isPlaying) player.Pause();
                player.playbackSpeed = 0f;
            }
            if (!firstFrameLogged && player != null && player.frame > 0)
            {
                firstFrameLogged = true;
                Debug.Log("[MvBackdrop] 首帧已送入纹理 frame=" + player.frame);
            }
            // 起播 seek：等真正开播（isPlaying 且已出首帧）后赋 frame；
            // 早期赋值可能被播放器忽略——落点不达标就逐帧重试，6s 上限
            if (pendingStartSeek && player != null && player.isPlaying && player.frame > 0)
            {
                long target = (long)System.Math.Round(startSeconds * player.frameRate);
                float fps = Mathf.Max(1f, (float)player.frameRate);
                if (System.Math.Abs(player.frame - target) <= fps * 0.5f)
                {
                    pendingStartSeek = false; // 已落到目标附近
                }
                else
                {
                    if (seekStartedRealtime < 0f) seekStartedRealtime = Time.unscaledTime;
                    if (Time.unscaledTime - seekStartedRealtime < 6f)
                        player.frame = target;
                    else if (!seekGiveUpLogged)
                    {
                        seekGiveUpLogged = true;
                        pendingStartSeek = false;
                        Debug.LogWarning("[MvBackdrop] 起播 seek 6s 未生效，按原始时间轴继续");
                    }
                }
            }
            if (cam == null || quadRenderer == null) return;
            if (Screen.width == lastScreenWidth && Screen.height == lastScreenHeight) return;
            RefreshBandFit();
        }

        // 顶部横带 contain 适配：贴屏幕顶、宽≤视野宽、高≤视野 40%，再整体×1.5；
        // 放大后带底不越过 BandBottomMinY（保判定区），宽溢出视野即居中裁边
        private void RefreshBandFit()
        {
            lastScreenWidth = Screen.width;
            lastScreenHeight = Screen.height;
            float viewH = cam.orthographic ? cam.orthographicSize * 2f : 12f;
            float viewW = viewH * cam.aspect;
            float top = viewH * 0.5f;
            float bandH = viewH * BandHeightFraction;
            float w = viewW;
            float h = w / Mathf.Max(0.01f, videoAspect);
            if (h > bandH)
            {
                h = bandH;
                w = h * videoAspect;
            }
            w *= BandScale;
            h *= BandScale;
            if (top - h < BandBottomMinY)
            {
                h = top - BandBottomMinY;
                w = h * videoAspect;
            }
            transform.localScale = new Vector3(w, h, 1f);
            transform.localPosition = new Vector3(0f, top - h * 0.5f, DepthZ);
        }

        private void OnDestroy()
        {
            if (player != null)
            {
                player.errorReceived -= OnVideoError;
                player.prepareCompleted -= OnPrepared;
                player.Stop();
                player.targetTexture = null;
            }
            if (texture != null)
            {
                texture.Release();
                Destroy(texture);
                texture = null;
            }
            if (quadMaterial != null) Destroy(quadMaterial);
        }
    }
}
