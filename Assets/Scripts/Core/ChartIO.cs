using System;
using System.IO;
using MusicGame.Pure.Model;
using UnityEngine;

namespace MusicGame.Core
{
    /// <summary>
    /// 谱面/元数据/分析结果的 JSON 读写，统一走 JsonUtility。
    /// 模型类自身即 JSON 根对象（列表字段内嵌），无需额外包装数组。
    /// </summary>
    public static class ChartIO
    {
        // ---- 契约签名（逐字一致） ----

        /// <summary>从 JSON 文件载入谱面；文件缺失或解析失败返回 null。</summary>
        public static ChartData Load(string path)
        {
            return LoadFile<ChartData>(path);
        }

        /// <summary>将谱面写入 JSON 文件（prettyPrint，便于手改调试），自动创建目录。</summary>
        public static void Save(ChartData chart, string path)
        {
            SaveFile(chart, path);
        }

        // ---- 附加重载：SongMeta / AnalysisResult 走同一套读写 ----

        /// <summary>载入歌曲元信息（meta.json）。</summary>
        public static SongMeta LoadSongMeta(string path)
        {
            return LoadFile<SongMeta>(path);
        }

        /// <summary>保存歌曲元信息。</summary>
        public static void Save(SongMeta meta, string path)
        {
            SaveFile(meta, path);
        }

        /// <summary>载入分析中间结果（analysis.json）。</summary>
        public static AnalysisResult LoadAnalysis(string path)
        {
            return LoadFile<AnalysisResult>(path);
        }

        /// <summary>保存分析中间结果。</summary>
        public static void Save(AnalysisResult analysis, string path)
        {
            SaveFile(analysis, path);
        }

        // ---- 内部实现 ----

        private static T LoadFile<T>(string path) where T : class
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Debug.LogError($"[ChartIO] 文件不存在: {path}");
                return null;
            }
            try
            {
                string json = File.ReadAllText(path);
                T obj = JsonUtility.FromJson<T>(json);
                if (obj == null) Debug.LogError($"[ChartIO] JSON 解析失败: {path}");
                return obj;
            }
            catch (Exception e)
            {
                Debug.LogError($"[ChartIO] 读取异常 {path}: {e.Message}");
                return null;
            }
        }

        private static void SaveFile(object obj, string path)
        {
            if (obj == null || string.IsNullOrEmpty(path))
            {
                Debug.LogError("[ChartIO] 保存参数无效（对象为空或路径为空）");
                return;
            }
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonUtility.ToJson(obj, true));
            }
            catch (Exception e)
            {
                Debug.LogError($"[ChartIO] 写入异常 {path}: {e.Message}");
            }
        }
    }
}
