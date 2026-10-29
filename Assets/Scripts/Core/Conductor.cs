using UnityEngine;

namespace MusicGame.Core
{
    /// <summary>
    /// 节拍时钟：一切时间以 AudioSettings.dspTime 为唯一基准。
    /// Setup 时刻即乐曲 0 秒；BeatToTime / TimeToBeat 依据 Bpm 与 FirstBeatOffsetSeconds 互逆换算。
    /// source 为 null（或无 clip）时按静音时钟驱动，时间轴逻辑完全一致（无声测试谱模式）。
    /// </summary>
    public class Conductor : MonoBehaviour
    {
        public double Bpm = 120.0;                  // 每分钟节拍数
        public double FirstBeatOffsetSeconds = 0.0; // 第 0 拍对应的乐曲时间（秒）

        private AudioSource _source;         // 关联音源；可为 null（无声模式）
        private double _startDspTime;        // 时钟起点的 dspTime
        private double _frozenSongTime;      // 非播放状态下冻结的乐曲时间
        private bool _playing;               // 时钟是否推进
        private bool _sawSourcePlaying;      // 是否见过音源真正出声（用于识别自然播完）
        private bool _scheduledPending;      // Pause 时计划播放尚未到点（被 Stop 取消），Resume 时需重排
        private int _silentFrames;           // isPlaying 瞬时抖动（编辑器卡顿/切窗）防抖计数
        private double _leadInSeconds;       // 预备倒计时秒数：0 点才出声，之前时钟走负时间（开局 3-2-1 用）
        // 出声调度提前量：Play() 要到下一个音频缓冲才真正出声，会让时钟系统性早于声音（约 20ms+）。
        // 改为按调度起点播放对齐两者；预备期内 SongTimeSeconds 为负值，不影响任何判定逻辑。
        private const double ScheduleAheadSeconds = 0.1;
        // 连续无声帧数达到该值才判"自然播完"——isPlaying 单帧抖动不冻结时钟（否则时钟瞬跳 clip.length，歌词跳尾）
        private const int SilentFramesForNaturalEnd = 20;

        /// <summary>本局预备倒计时秒数（Setup 传入；0=无预备）。</summary>
        public double LeadInSeconds
        {
            get { return _leadInSeconds; }
        }

        /// <summary>当前乐曲时间（秒）；播放中由 dspTime 换算，暂停/停止/播完时冻结。</summary>
        public double SongTimeSeconds
        {
            get { return _playing ? AudioSettings.dspTime - _startDspTime : _frozenSongTime; }
        }

        /// <summary>时钟是否在推进。</summary>
        public bool IsPlaying
        {
            get { return _playing; }
        }

        /// <summary>拍 → 乐曲时间（秒）。beat 0 落在 FirstBeatOffsetSeconds。</summary>
        public double BeatToTime(double beat)
        {
            double bpm = Bpm > 0.0 ? Bpm : 120.0;
            return FirstBeatOffsetSeconds + beat * 60.0 / bpm;
        }

        /// <summary>乐曲时间（秒） → 拍，与 BeatToTime 互逆（往返一致）。</summary>
        public double TimeToBeat(double time)
        {
            double bpm = Bpm > 0.0 ? Bpm : 120.0;
            return (time - FirstBeatOffsetSeconds) * bpm / 60.0;
        }

        /// <summary>
        /// 初始化并启动时钟：记录 dspTime 起点（此刻 = 乐曲 0 秒）。
        /// source 携带 clip 时顺带从头播放，保证时钟与出声对齐；source 可为 null。
        /// leadInSeconds > 0 时出声点后移：时钟从 -leadIn 走到 0 才到乐曲 0 秒（开局倒计时缓冲）。
        /// </summary>
        public void Setup(double bpm, double offsetSeconds, AudioSource source, double leadInSeconds = 0.0)
        {
            Bpm = bpm > 0.0 ? bpm : 120.0;
            FirstBeatOffsetSeconds = offsetSeconds;
            _source = source;
            _leadInSeconds = leadInSeconds > 0.0 ? leadInSeconds : 0.0;

            _startDspTime = AudioSettings.dspTime + ScheduleAheadSeconds + _leadInSeconds; // 时钟 0 点 = 计划出声时刻
            _frozenSongTime = -_leadInSeconds;
            _sawSourcePlaying = false;
            _silentFrames = 0;
            _scheduledPending = false;
            _playing = true;

            if (_source != null && _source.clip != null)
            {
                if (_source.isPlaying) _source.Stop(); // 清掉旧播放状态，确保从 0 开始
                _source.PlayScheduled(_startDspTime);
            }
        }

        /// <summary>暂停时钟与音频（附加接口，非契约成员）。
        /// 计划播放尚未到点（预备倒计时/暂停中重排）时取消调度并记账，Resume 时按新起点重排。</summary>
        public void Pause()
        {
            if (!_playing) return;
            _frozenSongTime = AudioSettings.dspTime - _startDspTime;
            _playing = false;
            if (_source != null && _source.clip != null)
            {
                if (_source.isPlaying) _source.Pause();
                else
                {
                    // PlayScheduled 还没到出声点：Pause 管不住调度，必须 Stop 取消，恢复时重排
                    _source.Stop();
                    _scheduledPending = true;
                }
            }
            Debug.Log("[Clock] Pause @songTime=" + _frozenSongTime.ToString("F2") + (_scheduledPending ? "（取消未到点计划播放）" : ""));
        }

        /// <summary>恢复时钟与音频（附加接口，非契约成员）。</summary>
        public void Resume()
        {
            if (_playing) return;
            _startDspTime = AudioSettings.dspTime - _frozenSongTime;
            _playing = true;
            _sawSourcePlaying = false;
            _silentFrames = 0;
            if (_source != null && _source.clip != null)
            {
                if (_scheduledPending)
                {
                    _scheduledPending = false;
                    _source.PlayScheduled(_startDspTime); // 按恢复后的新起点重排出声
                }
                else if (!_source.isPlaying) _source.UnPause();
            }
            Debug.Log("[Clock] Resume @songTime=" + _frozenSongTime.ToString("F2"));
        }

        /// <summary>停止时钟与音频，SongTimeSeconds 冻结在停止时刻（附加接口，非契约成员）。</summary>
        public void Stop()
        {
            if (_playing)
            {
                _frozenSongTime = AudioSettings.dspTime - _startDspTime;
                _playing = false;
            }
            _scheduledPending = false;
            if (_source != null && _source.isPlaying) _source.Stop();
        }

        private void Update()
        {
            // 音频自然播完 → 冻结时钟并置 IsPlaying=false，外部据此得知曲目结束。
            // isPlaying 有单帧瞬时抖动（编辑器卡顿/切窗/音频设备切换），必须连续多帧无声才判播完，
            // 否则时钟瞬间跳到 clip.length——歌词直接跳尾、结算提前触发。
            if (_playing && _source != null && _source.clip != null)
            {
                if (_source.isPlaying)
                {
                    _sawSourcePlaying = true;
                    _silentFrames = 0;
                }
                else if (_sawSourcePlaying)
                {
                    _silentFrames++;
                    if (_silentFrames >= SilentFramesForNaturalEnd)
                    {
                        _frozenSongTime = _source.clip.length;
                        _playing = false;
                        Debug.Log("[Clock] 音频自然播完 @clip 末尾（连续无声 " + _silentFrames + " 帧）");
                    }
                }
            }
        }
    }
}
