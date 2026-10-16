using System.Collections.Generic;

namespace MusicGame.Pure.Generator
{
    /// <summary>
    /// 四档难度参数表：easy≈2nps/四分网格 → expert≈10nps/十六分网格+双押+更密 hold。
    /// 键与谱面文件名后缀一致（chart_easy.json 等）。
    /// </summary>
    public class DifficultyProfiles
    {
        /// <summary>难度键：easy / normal / hard / expert。</summary>
        public static readonly Dictionary<string, DifficultyProfile> All = new Dictionary<string, DifficultyProfile>
        {
            // easy：约 2 nps，1/4 网格，同键间隔宽松，几乎无 hold，无双押
            { "easy", new DifficultyProfile("Easy", 2.0, 0.75, 4, 0.05, 0.00) },

            // normal：约 4 nps，1/8 网格，少量 hold，无双押
            { "normal", new DifficultyProfile("Normal", 4.0, 0.35, 8, 0.10, 0.00) },

            // hard：约 7 nps，1/8 网格，引入双押
            { "hard", new DifficultyProfile("Hard", 7.0, 0.18, 8, 0.15, 0.25) },

            // expert：约 10 nps，1/16 网格，双押更多、hold 更密，同键间隔最紧
            { "expert", new DifficultyProfile("Expert", 10.0, 0.12, 16, 0.25, 0.35) },
        };
    }
}
