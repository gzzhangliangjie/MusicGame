using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MusicGame.Core
{
    [Serializable]
    public class PlayRecord
    {
        public string key;        // 歌曲目录名|模式(4K/TAIKO)|难度
        public double bestScore;
        public int bestCombo;
        public double bestAcc;    // 百分制准确率
        public int plays;
        public string lastPlayed;
    }

    [Serializable]
    internal class PlayRecordStore
    {
        public List<PlayRecord> records = new List<PlayRecord>();
    }

    /// <summary>
    /// 本地最佳成绩：按 歌曲x模式x难度 存 persistentDataPath/records.json。
    /// Submit 提交一局成绩，任一维度（分数/连击/准确率）超历史最佳即视为破纪录（首玩也算）。
    /// </summary>
    public static class PlayRecords
    {
        private static PlayRecordStore cache;

        private static PlayRecordStore Store
        {
            get
            {
                if (cache != null) return cache;
                try
                {
                    string path = FilePath();
                    if (File.Exists(path))
                        cache = JsonUtility.FromJson<PlayRecordStore>(File.ReadAllText(path));
                }
                catch (Exception) { }
                if (cache == null) cache = new PlayRecordStore();
                return cache;
            }
        }

        private static string FilePath()
        {
            return System.IO.Path.Combine(Application.persistentDataPath, "records.json");
        }

        public static PlayRecord Get(string key)
        {
            return Store.records.Find(r => r.key == key);
        }

        /// <summary>提交成绩并落盘；返回是否破纪录（previous 为破纪录前的旧纪录拷贝，首玩为 null）。</summary>
        public static bool Submit(string key, double score, int maxCombo, double accPercent, out PlayRecord previous)
        {
            var rec = Get(key);
            previous = rec == null ? null : new PlayRecord
            {
                key = rec.key, bestScore = rec.bestScore, bestCombo = rec.bestCombo,
                bestAcc = rec.bestAcc, plays = rec.plays, lastPlayed = rec.lastPlayed
            };
            bool isNew = rec == null
                || score > rec.bestScore
                || maxCombo > rec.bestCombo
                || (accPercent >= 0 && accPercent > rec.bestAcc);
            if (rec == null)
            {
                rec = new PlayRecord { key = key };
                Store.records.Add(rec);
            }
            rec.bestScore = Math.Max(rec.bestScore, score);
            rec.bestCombo = Math.Max(rec.bestCombo, maxCombo);
            if (accPercent >= 0) rec.bestAcc = Math.Max(rec.bestAcc, accPercent);
            rec.plays++;
            rec.lastPlayed = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            try { File.WriteAllText(FilePath(), JsonUtility.ToJson(Store, true)); } catch (Exception) { }
            return isNew;
        }

        public static string Key(string songDir, bool taiko, string difficulty)
        {
            return songDir + "|" + (taiko ? "TAIKO" : "4K") + "|" + difficulty;
        }
    }
}
