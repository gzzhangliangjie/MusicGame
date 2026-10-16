using System;

namespace MusicGame.Pure.Generator
{
    /// <summary>单档难度的谱面生成参数（纯数据，四档共用同一生成算法）。</summary>
    [Serializable]
    public class DifficultyProfile
    {
        /// <summary>显示名（如 Easy）。</summary>
        public string Name;

        /// <summary>密度预算：任意 1 秒窗口内允许的最大音符数（notes per second）。</summary>
        public double MaxNotesPerSecond;

        /// <summary>同一 lane 相邻两音符的最小间隔（秒），难度越高越紧。</summary>
        public double MinSameLaneGapSeconds;

        /// <summary>量化网格：每拍划分格数（4=四分音符网格，8=八分，16=十六分）。</summary>
        public int GridDivision;

        /// <summary>hold 音符占比（0..1）。</summary>
        public double HoldRatio;

        /// <summary>双押概率（0..1），仅中高难度大于 0。</summary>
        public double DoubleTapChance;

        public DifficultyProfile()
        {
        }

        public DifficultyProfile(string name, double maxNotesPerSecond, double minSameLaneGapSeconds,
            int gridDivision, double holdRatio, double doubleTapChance)
        {
            Name = name;
            MaxNotesPerSecond = maxNotesPerSecond;
            MinSameLaneGapSeconds = minSameLaneGapSeconds;
            GridDivision = gridDivision;
            HoldRatio = holdRatio;
            DoubleTapChance = doubleTapChance;
        }
    }
}
