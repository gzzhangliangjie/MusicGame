using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MusicGame.EditorTools
{
    /// <summary>
    /// Windows 独立版一键构建（batchmode）：输出 Build/Win64/MusicGame.exe。
    /// 用法：Unity.exe -batchmode -projectPath &lt;工程&gt; -executeMethod MusicGame.EditorTools.BuildGame.Build -logFile &lt;日志&gt;
    /// 构建后需把工程根 Songs/ 与 Skins/ 拷到 exe 旁（运行时按 dataPath 上级目录扫描）。
    /// </summary>
    public static class BuildGame
    {
        public static void Build()
        {
            const string dir = "Build/Win64";
            Directory.CreateDirectory(dir);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Main.unity" },
                locationPathName = dir + "/MusicGame.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError("[BuildGame] FAIL result=" + report.summary.result
                               + " errors=" + report.summary.totalErrors);
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log("[BuildGame] PASS size=" + report.summary.totalSize
                      + " output=" + Path.GetFullPath(dir));
            EditorApplication.Exit(0);
        }
    }
}
