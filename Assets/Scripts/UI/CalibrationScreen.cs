using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace MusicGame.UI
{
    /// <summary>
    /// 延迟校准界面：代码生成 120BPM 节拍音（AudioClip 精确 0.5s 循环，dspTime 调度），
    /// 玩家跟随节拍点击 8 次，取 (点击时刻 − 最近节拍时刻) 的平均值存入
    /// PlayerPrefs 键 MusicGame.AudioOffset（单位：秒，正值=玩家平均偏晚），
    /// 由 GameManager 判定时应用。Esc 退出。
    /// </summary>
    public class CalibrationScreen : MonoBehaviour
    {
        public const string PrefsKey = "MusicGame.AudioOffset";
        const double Bpm = 120.0;
        const double BeatSeconds = 60.0 / Bpm; // 0.5s
        const int RequiredTaps = 8;
        const double MaxTapDistance = 0.2; // 偏离节拍超过 0.2s 的点击忽略

        readonly List<double> offsets = new List<double>();

        AudioSource tickSource;
        double startDspTime;
        bool finished;
        RectTransform panel;
        Text countText, avgText, resultText, savedText;
        Action onExit;

        /// <summary>本物体需已挂在 Canvas 下；构建面板并开始播放节拍。</summary>
        public void Setup(Action exitCallback)
        {
            onExit = exitCallback;
            tickSource = gameObject.AddComponent<AudioSource>();
            tickSource.clip = CreateTickClip();
            tickSource.loop = true; // clip 精确 0.5s，循环即 120BPM 稳定节拍
            tickSource.playOnAwake = false;
            tickSource.volume = 0.85f;
            BuildUi();
            Restart();
        }

        void BuildUi()
        {
            panel = UiKit.Panel(transform, "CalibrationPanel", UiKit.Theme.Bg).rectTransform;

            var title = UiKit.Label(panel, "title", "延 迟 校 准", 48, TextAnchor.MiddleCenter, UiKit.Theme.Accent);
            UiKit.Stretch(title.rectTransform, new Vector2(0f, 0.86f), new Vector2(1f, 0.97f));

            savedText = UiKit.Label(panel, "saved", SavedLine(), 24, TextAnchor.MiddleCenter, UiKit.Theme.Dim);
            UiKit.Stretch(savedText.rectTransform, new Vector2(0f, 0.78f), new Vector2(1f, 0.86f));

            var tips = UiKit.Label(panel, "tips", "听到“嘀”声的瞬间点击鼠标或按任意键，尽量与节拍对齐\n共需 8 次 · Esc 直接退出", 28, TextAnchor.MiddleCenter, Color.white);
            UiKit.Stretch(tips.rectTransform, new Vector2(0f, 0.58f), new Vector2(1f, 0.76f));

            countText = UiKit.Label(panel, "count", "0 / " + RequiredTaps, 84, TextAnchor.MiddleCenter, UiKit.Theme.Accent);
            UiKit.Stretch(countText.rectTransform, new Vector2(0f, 0.40f), new Vector2(1f, 0.58f));

            avgText = UiKit.Label(panel, "avg", "当前平均 -- ms", 30, TextAnchor.MiddleCenter, Color.white);
            UiKit.Stretch(avgText.rectTransform, new Vector2(0f, 0.32f), new Vector2(1f, 0.40f));

            resultText = UiKit.Label(panel, "result", "", 32, TextAnchor.MiddleCenter, new Color(0.98f, 0.80f, 0.25f), true);
            UiKit.Stretch(resultText.rectTransform, new Vector2(0f, 0.22f), new Vector2(1f, 0.32f));

            UiKit.ButtonRect(panel, "btnRestart", "重 来", new Vector2(-180f, -420f), new Vector2(300f, 80f), Restart, UiKit.Theme.Secondary, 30);
            UiKit.ButtonRect(panel, "btnDone", "完 成", new Vector2(180f, -420f), new Vector2(300f, 80f), RequestExit, UiKit.Theme.Primary, 30);
        }

        static string SavedLine()
        {
            if (PlayerPrefs.HasKey(PrefsKey))
                return "已保存偏移 " + (PlayerPrefs.GetFloat(PrefsKey) * 1000.0).ToString("+0.0;-0.0") + " ms";
            return "尚未校准";
        }

        /// <summary>重新开始一轮：清空计数并重新调度节拍起点。</summary>
        void Restart()
        {
            offsets.Clear();
            finished = false;
            resultText.text = "";
            startDspTime = AudioSettings.dspTime + 1.0; // 1 秒预备
            tickSource.Stop();
            tickSource.PlayScheduled(startDspTime);
            UpdateLive();
        }

        void UpdateLive()
        {
            countText.text = offsets.Count + " / " + RequiredTaps;
            avgText.text = offsets.Count == 0
                ? "当前平均 -- ms"
                : "当前平均 " + (Mean() * 1000.0).ToString("+0.0;-0.0") + " ms";
        }

        double Mean()
        {
            double sum = 0.0;
            foreach (var o in offsets) sum += o;
            return sum / offsets.Count;
        }

        void Update()
        {
            if (tickSource == null || panel == null) return;
            if (UiKit.EscDown()) { RequestExit(); return; }
            if (finished) return;

            bool keyDown = UiKit.AnyKeyPressed();
            bool mouseDown = UiKit.MouseLeftDown();
            if (mouseDown && UiKit.PointerOverUi()) return; // 点在按钮上不算击打
            if (!keyDown && !mouseDown) return;
            RegisterTap();
        }

        void RegisterTap()
        {
            double local = AudioSettings.dspTime - startDspTime;
            if (local < 0.0) return; // 节拍尚未开始
            int beat = (int)Math.Round(local / BeatSeconds);
            double diff = local - beat * BeatSeconds;
            if (Math.Abs(diff) > MaxTapDistance) return;
            offsets.Add(diff);
            UpdateLive();
            if (offsets.Count >= RequiredTaps) Finish();
        }

        void Finish()
        {
            finished = true;
            double avg = Mean();
            PlayerPrefs.SetFloat(PrefsKey, (float)avg);
            PlayerPrefs.Save();
            resultText.text = "已保存：平均偏移 " + (avg * 1000.0).ToString("+0.0;-0.0") + " ms";
            savedText.text = SavedLine();
        }

        /// <summary>退出校准（回调由 GameFlow 提供并销毁本物体）。</summary>
        public void RequestExit()
        {
            finished = true;
            if (tickSource != null) tickSource.Stop();
            var cb = onExit;
            onExit = null;
            if (cb != null) cb();
        }

        /// <summary>生成 0.5s 节拍音：1760Hz+880Hz 混合、指数衰减的短促“嘀”声。</summary>
        static AudioClip CreateTickClip()
        {
            const int rate = 44100;
            int samples = (int)Math.Round(rate * BeatSeconds); // 精确 0.5s，保证循环对拍
            var data = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                double t = i / (double)rate;
                double env = t < 0.004 ? t / 0.004 : Math.Exp(-(t - 0.004) * 45.0);
                double v = 0.6 * Math.Sin(2.0 * Math.PI * 1760.0 * t) + 0.4 * Math.Sin(2.0 * Math.PI * 880.0 * t);
                data[i] = (float)(v * env * 0.85);
            }
            var clip = AudioClip.Create("calib_tick_120bpm", samples, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
