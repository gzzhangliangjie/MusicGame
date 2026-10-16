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
        // 出声调度提前量：Play() 要到下一个音频缓冲才真正出声，会让时钟系统性早于声音（约 20ms+）。
        // 改为按调度起点播放对齐两者；预备期内 SongTimeSeconds 为 -0.1..0 的微小负值，不影响任何判定逻辑。
        private const double ScheduleAheadSeconds = 0.1;

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
        /// </summary>
        public void Setup(double bpm, double offsetSeconds, AudioSource source)
        {
            Bpm = bpm > 0.0 ? bpm : 120.0;
            FirstBeatOffsetSeconds = offsetSeconds;
            _source = source;

            _startDspTime = AudioSettings.dspTime + ScheduleAheadSeconds; // 时钟 0 点 = 计划出声时刻
            _frozenSongTime = 0.0;
            _sawSourcePlaying = false;
            _playing = true;

            if (_source != null && _source.clip != null)
            {
                if (_source.isPlaying) _source.Stop(); // 清掉旧播放状态，确保从 0 开始
                _source.PlayScheduled(_startDspTime);
            }
        }

        /// <summary>暂停时钟与音频（附加接口，非契约成员）。</summary>
        public void Pause()
        {
            if (!_playing) return;
            _frozenSongTime = AudioSettings.dspTime - _startDspTime;
            _playing = false;
            if (_source != null && _source.isPlaying) _source.Pause();
        }

        /// <summary>恢复时钟与音频（附加接口，非契约成员）。</summary>
        public void Resume()
        {
            if (_playing) return;
            _startDspTime = AudioSettings.dspTime - _frozenSongTime;
            _playing = true;
            _sawSourcePlaying = false;
            if (_source != null && !_source.isPlaying) _source.UnPause();
        }

        /// <summary>停止时钟与音频，SongTimeSeconds 冻结在停止时刻（附加接口，非契约成员）。</summary>
        public void Stop()
        {
            if (_playing)
            {
                _frozenSongTime = AudioSettings.dspTime - _startDspTime;
                _playing = false;
            }
            if (_source != null && _source.isPlaying) _source.Stop();
        }

        private void Update()
        {
            // 音频自然播完 → 冻结时钟并置 IsPlaying=false，外部据此得知曲目结束
            if (_playing && _source != null && _source.clip != null)
            {
                if (_source.isPlaying) _sawSourcePlaying = true;
                else if (_sawSourcePlaying)
                {
                    _frozenSongTime = _source.clip.length;
                    _playing = false;
                }
            }
        }
    }
}
