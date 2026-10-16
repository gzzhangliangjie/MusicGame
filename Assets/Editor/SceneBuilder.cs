using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using MusicGame.Analysis;
using MusicGame.Gameplay;
using MusicGame.UI;

namespace MusicGame.EditorTools
{
    /// <summary>
    /// 编辑器工具：一键创建 Main.unity 游戏场景（相机 + EventSystem + Canvas + 流程组件）。
    /// 菜单路径：Tools/MusicGame/一键创建游戏场景
    /// </summary>
    public static class SceneBuilder
    {
        private const string SceneDirectory = "Assets/Scenes";
        private const string ScenePath = SceneDirectory + "/Main.unity";

        [MenuItem("Tools/MusicGame/一键创建游戏场景")]
        public static void BuildMainScene()
        {
            // 新建空场景（Single 模式，替换当前场景）
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateMainCamera();
            CreateEventSystem();
            var canvas = CreateCanvas();

            // 流程组件：主菜单→选曲→分析→难度→游戏→结算（全部代码创建 UI，无预制体依赖）
            canvas.gameObject.AddComponent<GameFlow>();
            canvas.gameObject.AddComponent<GameManager>();
            canvas.gameObject.AddComponent<SongAnalyzer>();

            // 保存场景（目录不存在则先创建）
            if (!Directory.Exists(SceneDirectory))
            {
                Directory.CreateDirectory(SceneDirectory);
            }

            bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
            if (!saved)
            {
                Debug.LogError("[SceneBuilder] 场景保存失败：" + ScenePath);
                return;
            }

            // 顺手把场景加入 Build Settings，便于打包
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            Debug.Log("[SceneBuilder] 场景创建完成并已保存到 " + ScenePath +
                      "。直接点击 Play，在选曲界面选择 sample 即可试玩示例谱（无声模式，DFJK 四轨）。");
        }

        /// <summary>主相机：正交 2D 视角 + AudioListener</summary>
        private static Camera CreateMainCamera()
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            var camera = go.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 10f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.05f, 0.05f, 0.08f, 1f);
            camera.nearClipPlane = -10f;
            camera.farClipPlane = 50f;
            go.AddComponent<AudioListener>();
            return camera;
        }

        /// <summary>EventSystem（uGUI 交互与触屏必需）</summary>
        private static void CreateEventSystem()
        {
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            // 与 UiKit.EnsureEventSystem 相同的输入后端适配：仅新 Input System 启用时必须用
            // InputSystemUIInputModule，否则 StandaloneInputModule 每帧读 UnityEngine.Input 会抛异常
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }

        /// <summary>Canvas：ScreenSpaceOverlay + ScaleWithScreenSize 1080x1920（竖屏）</summary>
        private static Canvas CreateCanvas()
        {
            var go = new GameObject("Canvas");
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0.5f; // 宽高各按一半匹配，避免极端宽高比裁切
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            go.AddComponent<GraphicRaycaster>();
            return canvas;
        }
    }
}
