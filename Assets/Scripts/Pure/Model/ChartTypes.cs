using System;
using System.Collections.Generic;

namespace MusicGame.Pure.Model
{
    /// <summary>单个音符。Type 取 "tap" 或 "hold"；Duration 仅 hold 有效（秒），tap 恒为 0。</summary>
    [Serializable]
    public class NoteData
    {
        public double Time;     // 判定时间（秒，乐曲时间轴）
        public int Lane;        // 轨道 0-3（预留扩展）
        public string Type;     // tap | hold
        public double Duration; // hold 时长（秒）
    }

    /// <summary>谱面：一首歌某个难度的完整音符列表（Songs/&lt;songId&gt;/chart_*.json）。</summary>
    [Serializable]
    public class ChartData
    {
        public string SongId;         // 所属歌曲 Id
        public string Difficulty;     // easy | normal | hard | expert
        public double Bpm;            // 每分钟节拍数
        public double OffsetSeconds;  // 第 0 拍在乐曲时间轴上的位置（秒）
        public List<NoteData> Notes = new List<NoteData>();
    }

    /// <summary>歌曲元信息（Songs/&lt;songId&gt;/meta.json）。AudioPath 为空表示无声测试谱模式。</summary>
    [Serializable]
    public class SongMeta
    {
        public string Id;             // 歌曲唯一标识（同时是缓存目录名）
        public string Title;          // 显示标题
        public string AudioPath;      // 原始音频路径；可为空
        public double DurationSeconds; // 音频时长（秒）
        public List<string> Difficulties = new List<string>();
    }

    /// <summary>onset 检测出的一个拍点。Band 取 low/mid/high 三频带之一。</summary>
    [Serializable]
    public class Onset
    {
        public double TimeSeconds; // 拍点时间（秒）
        public double Strength;    // 归一化强度 0..1
        public string Band;        // low | mid | high
    }

    /// <summary>音频分析中间结果（Songs/&lt;songId&gt;/analysis.json），各难度谱面由它派生。</summary>
    [Serializable]
    public class AnalysisResult
    {
        public double Bpm;           // 估计 BPM
        public double OffsetSeconds; // 估计的第 0 拍偏移（秒）
        public List<Onset> Onsets = new List<Onset>();
    }
}
