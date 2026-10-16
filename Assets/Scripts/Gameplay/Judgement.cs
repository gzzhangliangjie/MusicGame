namespace MusicGame.Gameplay
{
    /// <summary>判定结果。窗口：Perfect ±0.040s，Great ±0.080s，超窗 Miss。</summary>
    public enum Judgement
    {
        /// <summary>完美（±40ms）。</summary>
        Perfect,
        /// <summary>良好（±80ms）。</summary>
        Great,
        /// <summary>错过（超过判定窗，自动判定）。</summary>
        Miss
    }
}
