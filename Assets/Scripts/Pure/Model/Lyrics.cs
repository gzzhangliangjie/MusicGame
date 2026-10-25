using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MusicGame.Pure.Model
{
    /// <summary>
    /// LRC 歌词（纯 C#，无 UnityEngine 依赖）：解析行级时间戳 [mm:ss.xx] 文本。
    /// 支持一行多个时间标签与空文本行（间奏，显示层据此清空上一句）。
    /// TryGetLine 返回该时刻应显示的行：返回 false 表示第一句尚未开始。
    /// </summary>
    public class Lyrics
    {
        private readonly List<double> times = new List<double>();
        private readonly List<string> texts = new List<string>();

        /// <summary>解析出的歌词行数（含空行）。</summary>
        public int Count => times.Count;

        public static Lyrics Parse(string content)
        {
            var lyrics = new Lyrics();
            if (string.IsNullOrEmpty(content)) return lyrics;

            foreach (string rawLine in content.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r').Trim();
                if (line.Length < 2 || line[0] != '[') continue;

                var stamps = new List<double>();
                int pos = 1;
                while (pos < line.Length)
                {
                    int close = line.IndexOf(']', pos);
                    if (close < 0) break;
                    string tag = line.Substring(pos, close - pos);
                    double seconds;
                    if (!TryParseTimestamp(tag, out seconds)) break; // 元数据标签（ti:/ar: 等）→ 停止收集
                    stamps.Add(seconds);
                    pos = close + 1;
                }
                if (stamps.Count == 0) continue;
                string text = line.Substring(pos).Trim();
                foreach (double t in stamps)
                {
                    lyrics.times.Add(t);
                    lyrics.texts.Add(text);
                }
            }

            // 按时间稳定排序（插入序保持同刻多行顺序）
            var order = new int[lyrics.times.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                int c = lyrics.times[a].CompareTo(lyrics.times[b]);
                return c != 0 ? c : a.CompareTo(b);
            });
            var sortedTimes = new List<double>(lyrics.times.Count);
            var sortedTexts = new List<string>(lyrics.times.Count);
            foreach (int i in order)
            {
                sortedTimes.Add(lyrics.times[i]);
                sortedTexts.Add(lyrics.texts[i]);
            }
            lyrics.times.Clear(); lyrics.times.AddRange(sortedTimes);
            lyrics.texts.Clear(); lyrics.texts.AddRange(sortedTexts);
            return lyrics;
        }

        public static Lyrics LoadFile(string path)
        {
            return Parse(File.ReadAllText(path));
        }

        /// <summary>当前时刻应显示的行。false = 第一句未开始；true 且 text 为空 = 间奏。</summary>
        public bool TryGetLine(double timeSeconds, out string text)
        {
            text = null;
            int lo = 0, hi = times.Count - 1, idx = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (times[mid] <= timeSeconds) { idx = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (idx < 0) return false;
            text = texts[idx];
            return true;
        }

        // "mm:ss" / "mm:ss.xx"（分数位 1~3 位均可）；不合法返回 false（元数据标签）
        private static bool TryParseTimestamp(string tag, out double seconds)
        {
            seconds = 0.0;
            int colon = tag.IndexOf(':');
            if (colon <= 0 || colon != tag.LastIndexOf(':')) return false;
            string mm = tag.Substring(0, colon);
            string rest = tag.Substring(colon + 1);
            int dot = rest.IndexOf('.');
            string ss = dot >= 0 ? rest.Substring(0, dot) : rest;
            string frac = dot >= 0 ? rest.Substring(dot + 1) : "";
            int minutes, whole;
            if (!int.TryParse(mm, NumberStyles.None, CultureInfo.InvariantCulture, out minutes)) return false;
            if (!int.TryParse(ss, NumberStyles.None, CultureInfo.InvariantCulture, out whole)) return false;
            if (ss.Length != 2 || frac.Length > 3) return false;
            double fracValue = 0.0;
            if (frac.Length > 0)
            {
                if (!int.TryParse(frac, NumberStyles.None, CultureInfo.InvariantCulture, out int f)) return false;
                fracValue = f / Math.Pow(10.0, frac.Length);
            }
            if (minutes < 0 || whole < 0 || whole >= 60) return false;
            seconds = minutes * 60.0 + whole + fracValue;
            return true;
        }
    }
}
