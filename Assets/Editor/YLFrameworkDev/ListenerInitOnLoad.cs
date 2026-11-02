using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YLFramework.Core;

namespace YLFramework.Editor.Core
{
    /// <summary>仅供仓库作者显式补建缺失默认资源，不自动初始化、迁移或覆盖已有资产。</summary>
    public class ListenerInitOnLoad
    {
        /// <summary>Core 默认资源所在目录，也是删除保护检查的路径边界。</summary>
        public const string ResourceRoot = "Packages/com.kano.ylframework/Runtime/Core/Resources/YLFramework/Core";
        /// <summary>框架按需加载的基础 UI 根 Prefab 路径。</summary>
        public const string CanvasPath = ResourceRoot + "/Canvas.prefab";
        /// <summary>框架自建输入系统所用 Prefab 路径，具体输入模块在运行时按后端补充。</summary>
        public const string EventSystemPath = ResourceRoot + "/EventSystem.prefab";

        internal static void RequireLocalSourcePackage(UnityEditor.PackageManager.PackageInfo package)
        {
            if (package == null || package.source != UnityEditor.PackageManager.PackageSource.Local)
            {
                throw new System.InvalidOperationException("Authoring requires a local source package.");
            }
        }

        /// <summary>在非 Play 切换且非 Player 构建阶段补建缺失资源，直接保存 Prefab，不保存用户场景或建立 Undo。</summary>
        [MenuItem("YLFramework/Core/Create Missing Default Resources")]
        public static void GenerateDefaultResources()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(YLFrameworkRoot).Assembly);
            RequireLocalSourcePackage(package);
            if (EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer)
            {
                return;
            }

            GenerateCanvas();
            GenerateEventSystem();
        }

        /// <summary>仅当文件、配套 meta 和已导入资产均不存在时允许创建，保护未导入文件与孤立元数据。</summary>
        private static bool CanCreate(string path)
        {
            var fullPath = global::YLFramework.Editor.Common.FrameworkPackagePaths.FullPath(typeof(YLFrameworkRoot).Assembly,
                path.Substring("Packages/com.kano.ylframework/".Length));
            return !File.Exists(fullPath) && !File.Exists(fullPath + ".meta") &&
                AssetDatabase.LoadMainAssetAtPath(path) == null;
        }

        /// <summary>在独立预览场景中创建缺失基础根，保存并启用资产后关闭临时场景。</summary>
        private static void GenerateCanvas()
        {
            if (!CanCreate(CanvasPath))
            {
                return;
            }

            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var canvas = CreateCanvas(scene);
                Directory.CreateDirectory(global::YLFramework.Editor.Common.FrameworkPackagePaths.FullPath(typeof(YLFrameworkRoot).Assembly,
                    ResourceRoot.Substring("Packages/com.kano.ylframework/".Length)));
                PrefabUtility.SaveAsPrefabAsset(canvas, CanvasPath);
                EnablePrefabAsset(CanvasPath);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        /// <summary>在独立预览场景中补建缺失输入系统 Prefab，不激活用户场景中的输入组件。</summary>
        private static void GenerateEventSystem()
        {
            if (!CanCreate(EventSystemPath))
            {
                return;
            }

            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var eventSystem = CreateEventSystem(scene);
                Directory.CreateDirectory(global::YLFramework.Editor.Common.FrameworkPackagePaths.FullPath(typeof(YLFrameworkRoot).Assembly,
                    ResourceRoot.Substring("Packages/com.kano.ylframework/".Length)));
                PrefabUtility.SaveAsPrefabAsset(eventSystem, EventSystemPath);
                EnablePrefabAsset(EventSystemPath);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        /// <summary>创建暂不激活的 Overlay 根，配置缩放、射线和显式 Layer0，并绑定 UIRootContext。</summary>
        private static GameObject CreateCanvas(Scene scene)
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform));
            canvas.SetActive(false);
            SceneManager.MoveGameObjectToScene(canvas, scene);
            var component = canvas.AddComponent<Canvas>();
            component.renderMode = RenderMode.ScreenSpaceOverlay;
            component.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.TexCoord2 | AdditionalCanvasShaderChannels.TexCoord3;
            var scaler = canvas.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 1;
            canvas.AddComponent<GraphicRaycaster>();
            canvas.AddComponent<AutomaticAdaptationCanvasScaler>();
            var context = canvas.AddComponent<UIRootContext>();
            var layer = new GameObject("Layer0", typeof(RectTransform));
            layer.transform.SetParent(canvas.transform, false);
            var rect = (RectTransform)layer.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var configuration = new SerializedObject(context);
            configuration.FindProperty("canvas").objectReferenceValue = component;
            configuration.FindProperty("graphicRaycaster").objectReferenceValue = canvas.GetComponent<GraphicRaycaster>();
            var layers = configuration.FindProperty("uiLayers");
            layers.arraySize = 1;
            var entry = layers.GetArrayElementAtIndex(0);
            entry.FindPropertyRelative("root").objectReferenceValue = layer.transform;
            entry.FindPropertyRelative("enableMask").boolValue = false;
            entry.FindPropertyRelative("maskImage").objectReferenceValue = null;
            configuration.ApplyModifiedPropertiesWithoutUndo();
            return canvas;
        }

        /// <summary>创建仅含 EventSystem 的 inactive 对象；不在 Editor 生成阶段选定输入模块。</summary>
        private static GameObject CreateEventSystem(Scene scene)
        {
            var eventSystem = new GameObject("EventSystem");
            eventSystem.SetActive(false);
            SceneManager.MoveGameObjectToScene(eventSystem, scene);
            eventSystem.AddComponent<EventSystem>();
            return eventSystem;
        }

        /// <summary>只修改并保存资产的激活标记，不激活预览场景中的原对象或输入组件。</summary>
        private static void EnablePrefabAsset(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                throw new IOException("Could not create YL default resource: " + path);
            }

            using (var serialized = new SerializedObject(prefab))
            {
                serialized.FindProperty("m_IsActive").boolValue = true;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            PrefabUtility.SavePrefabAsset(prefab);
        }
    }
}
