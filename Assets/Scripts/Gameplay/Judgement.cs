namespace MusicGame.Gameplay
{
    /// <summary>判定结果。窗口：Perfect ±0.040s，Good ±0.080s，Bad ±0.140s，超窗 Miss。</summary>
    public enum Judgement
    {
        /// <summary>完美（±40ms，权重 1.0）。</summary>
        Perfect,
        /// <summary>好（±80ms，权重 0.65）。</summary>
        Good,
        /// <summary>差（±140ms，权重 0.30，不断连击）。</summary>
        Bad,
        /// <summary>错过（超过判定窗，自动判定，断连击）。</summary>
        Miss
    }
}
