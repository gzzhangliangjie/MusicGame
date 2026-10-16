using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace MusicGame.Analysis
{
    /// <summary>
    /// 运行时导入本地音频文件为 AudioClip（支持 mp3 / ogg / wav）。
    /// 通过 UnityWebRequestMultimedia 在主线程异步加载，须从主线程调用。
    /// </summary>
    public class AudioImportService
    {
        /// <summary>加载本地音频；失败时抛异常（文件不存在 / 格式不支持 / 网络错误）。</summary>
        public static async Task<AudioClip> LoadClip(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            string fullPath = Path.GetFullPath(path); // 兼容相对路径
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Audio file not found.", fullPath);

            AudioType audioType;
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            switch (ext)
            {
                case ".mp3": audioType = AudioType.MPEG; break;
                case ".ogg": audioType = AudioType.OGGVORBIS; break;
                case ".wav": audioType = AudioType.WAV; break;
                default:
                    throw new ArgumentException("Unsupported audio extension '" + ext + "' (mp3/ogg/wav only).");
            }

            // file:// URI（处理反斜杠、中文与空格路径）
            string url = new Uri(fullPath).AbsoluteUri;
            using (var request = UnityWebRequestMultimedia.GetAudioClip(url, audioType))
            {
                var operation = request.SendWebRequest();
                while (!operation.isDone) await Task.Yield(); // 回到主线程等待
                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new InvalidOperationException(
                        "Load audio failed (" + request.result + "): " + request.error);
                }
                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null) throw new InvalidOperationException("AudioClip decode returned null.");
                return clip;
            }
        }
    }
}
