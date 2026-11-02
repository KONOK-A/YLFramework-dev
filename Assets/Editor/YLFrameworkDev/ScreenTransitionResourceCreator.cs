// 按本地 ActionKit Prefab 的设置创建模块自有资源，不读取、复制或覆盖旧资源。
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YLFramework.Actions;
using Object = UnityEngine.Object;

namespace YLFramework.Editor.Actions
{
    /// <summary>在固定路径缺少资源及其元数据时，创建 ActionKit 自有的屏幕过渡预制体。</summary>
    internal static class ScreenTransitionResourceCreator
    {
        private const string AssetPath = "Packages/com.kano.ylframework/Runtime/Systems/Action/Resources/ActionKit/ScreenTransitionCanvas.prefab";

        /// <summary>在预览场景中构建并保存新预制体，保留已有资源，最后释放本次临时对象和场景。</summary>
        /// <remarks>资产创建直接写入磁盘，不登记 Undo；失败清理不回滚已创建的目录或已保存的资产。</remarks>
        [MenuItem("YLFramework/Action/创建屏幕过渡资源")]
        private static void CreateResource()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("Exit Play Mode before creating the ActionKit resource.");
            }

            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ScreenTransitionCanvas).Assembly);
            global::YLFramework.Editor.Core.ListenerInitOnLoad.RequireLocalSourcePackage(package);
            var absolutePath = Path.Combine(package.resolvedPath, AssetPath.Substring("Packages/com.kano.ylframework/".Length));
            if (File.Exists(absolutePath) || File.Exists(absolutePath + ".meta"))
            {
                var existing = AssetDatabase.LoadMainAssetAtPath(AssetPath);
                if (existing)
                {
                    EditorGUIUtility.PingObject(existing);
                }

                Debug.Log("Existing ActionKit ScreenTransition resource or metadata was kept unchanged: " + AssetPath);
                return;
            }

            EnsureFolder("Packages/com.kano.ylframework/Runtime/Systems/Action/Resources/ActionKit");
            var temporaryScene = EditorSceneManager.NewPreviewScene();
            GameObject root = null;
            GameObject imageObject = null;
            try
            {
                // 先移入临时预览场景再创建 UI，避免影响用户当前场景。
                root = EditorUtility.CreateGameObjectWithHideFlags("ScreenTransitionCanvas",
                    HideFlags.HideAndDontSave, typeof(RectTransform));
                SceneManager.MoveGameObjectToScene(root, temporaryScene);
                root.hideFlags = HideFlags.None;
                root.AddComponent<Canvas>();
                root.AddComponent<CanvasScaler>();
                root.AddComponent<ScreenTransitionCanvas>();
                root.layer = 5;
                var canvas = root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 10000;
                canvas.sortingLayerID = 0;
                canvas.planeDistance = 100;
                canvas.pixelPerfect = false;
                canvas.overrideSorting = false;
                canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.None;
                canvas.targetDisplay = 0;
                var scaler = root.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = 1;
                scaler.referencePixelsPerUnit = 100;
                scaler.referenceResolution = new Vector2(800, 600);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0;
                scaler.physicalUnit = CanvasScaler.Unit.Points;
                scaler.fallbackScreenDPI = 96;
                scaler.defaultSpriteDPI = 96;
                scaler.dynamicPixelsPerUnit = 1;

                imageObject = EditorUtility.CreateGameObjectWithHideFlags("ColorImage",
                    HideFlags.HideAndDontSave, typeof(RectTransform));
                SceneManager.MoveGameObjectToScene(imageObject, temporaryScene);
                imageObject.hideFlags = HideFlags.None;
                imageObject.AddComponent<CanvasRenderer>();
                imageObject.AddComponent<Image>();
                imageObject.layer = 5;
                var imageRect = imageObject.GetComponent<RectTransform>();
                imageRect.SetParent(root.transform, false);
                imageRect.anchorMin = Vector2.zero;
                imageRect.anchorMax = Vector2.one;
                imageRect.pivot = new Vector2(0.5f, 0.5f);
                imageRect.anchoredPosition = Vector2.zero;
                imageRect.sizeDelta = Vector2.zero;
                var image = imageObject.GetComponent<Image>();
                image.color = Color.black;
                image.raycastTarget = true;
                image.type = Image.Type.Simple;
                image.sprite = null;
                image.useSpriteMesh = false;
                imageObject.GetComponent<CanvasRenderer>().cullTransparentMesh = false;
                root.GetComponent<ScreenTransitionCanvas>().colorImage = image;

                var rootRect = root.GetComponent<RectTransform>();
                rootRect.anchorMin = Vector2.zero;
                rootRect.anchorMax = Vector2.zero;
                rootRect.pivot = Vector2.zero;
                rootRect.anchoredPosition = Vector2.zero;
                rootRect.sizeDelta = Vector2.zero;
                // 保留既有序列化基线；视觉效果由 Unity 验证，不在此猜测调整。
                rootRect.localScale = Vector3.zero;

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, AssetPath, out var success);
                if (!success || !prefab)
                {
                    throw new InvalidOperationException("Could not save the new ActionKit ScreenTransition resource: " + AssetPath);
                }

                Selection.activeObject = prefab;
                EditorGUIUtility.PingObject(prefab);
                Debug.Log("Created ActionKit ScreenTransition resource: " + AssetPath);
            }
            finally
            {
                if (imageObject)
                {
                    Object.DestroyImmediate(imageObject);
                }

                if (root)
                {
                    Object.DestroyImmediate(root);
                }

                if (temporaryScene.IsValid())
                {
                    EditorSceneManager.ClosePreviewScene(temporaryScene);
                }
            }
        }

        /// <summary>通过 AssetDatabase 逐层补建资源目录，创建失败时报告错误。</summary>
        /// <param name="path">以 Assets 为根、使用正斜杠分隔的目录路径。</param>
        private static void EnsureFolder(string path)
        {
            var segments = path.Split('/');
            var parent = segments[0];
            for (var index = 1; index < segments.Length; index++)
            {
                var next = parent + "/" + segments[index];
                if (!AssetDatabase.IsValidFolder(next) && string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, segments[index])))
                {
                    throw new IOException("Could not create the ActionKit resource folder: " + next);
                }

                parent = next;
            }
        }
    }
}
