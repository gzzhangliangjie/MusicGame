namespace MusicGame.Gameplay
{
    /// <summary>
    /// 计分系统（纯逻辑）：Perfect=1.0 / Great=0.65 权重，连击、准确率与百万分制得分。
    /// hold 记两次判定（头+尾），总数由 Setup 传入。
    /// </summary>
    public class ScoreSystem
    {
        /// <summary>Perfect 权重。</summary>
        public const double PerfectWeight = 1.0;
        /// <summary>Great 权重。</summary>
        public const double GreatWeight = 0.65;

        private double weightSum;
        private int judgedCount;
        private int totalJudgements = 1;
        private int combo;
        private int maxCombo;
        private int perfectCount;
        private int greatCount;
        private int missCount;

        /// <summary>当前连击（Miss 清零）。</summary>
        public int Combo => combo;
        /// <summary>最大连击。</summary>
        public int MaxCombo => maxCombo;
        /// <summary>得分（0..1000000，按总判定数折算）。</summary>
        public double Score => 1000000.0 * weightSum / totalJudgements;
        /// <summary>准确率（加权，未判定时为 1）。</summary>
        public double Accuracy => judgedCount <= 0 ? 1.0 : weightSum / judgedCount;
        /// <summary>总判定数（hold=2）。</summary>
        public int TotalJudgements => totalJudgements;
        /// <summary>已判定次数。</summary>
        public int JudgedCount => judgedCount;
        /// <summary>Perfect 计数。</summary>
        public int PerfectCount => perfectCount;
        /// <summary>Great 计数。</summary>
        public int GreatCount => greatCount;
        /// <summary>Miss 计数。</summary>
        public int MissCount => missCount;

        /// <summary>开局设置总判定容量（tap=1，hold=2），并清零全部统计。</summary>
        public void Setup(int totalJudgementCount)
        {
            totalJudgements = totalJudgementCount <= 0 ? 1 : totalJudgementCount;
            weightSum = 0.0;
            judgedCount = 0;
            combo = 0;
            maxCombo = 0;
            perfectCount = 0;
            greatCount = 0;
            missCount = 0;
        }

        /// <summary>登记一次判定（头判或尾判；isHoldTail 仅作标记，不影响权重）。</summary>
        public void Register(Judgement j, bool isHoldTail)
        {
            judgedCount++;
            double weight = j == Judgement.Perfect ? PerfectWeight
                : j == Judgement.Great ? GreatWeight
                : 0.0;
            weightSum += weight;
            switch (j)
            {
                case Judgement.Perfect: perfectCount++; break;
                case Judgement.Great: greatCount++; break;
                default: missCount++; break;
            }
            if (j == Judgement.Miss) combo = 0;
            else
            {
                combo++;
                if (combo > maxCombo) maxCombo = combo;
            }
        }
    }
}
