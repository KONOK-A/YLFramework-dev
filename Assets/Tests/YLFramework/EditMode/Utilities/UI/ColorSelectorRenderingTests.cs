using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YLFramework.UI;

namespace YLFramework.Core.Tests
{
    public class ColorSelectorRenderingTests
    {
        [Test]
        public void FormalGraphicsRenderThroughStencilRectClipAndCanvasAlpha()
        {
            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewPreviewScene();
            var output = new RenderTexture(960, 600, 24);
            var pixels = new Texture2D(960, 600, TextureFormat.RGBA32, false);
            var oldTarget = RenderTexture.active;
            try
            {
                var camera = new GameObject("YLColor.RenderCamera", typeof(Camera)).GetComponent<Camera>();
                SceneManager.MoveGameObjectToScene(camera.gameObject, scene);
                camera.scene = scene;
                camera.transform.position = new Vector3(0, 0, -10);
                camera.orthographic = true;
                camera.orthographicSize = 3;
                camera.cullingMask = 1 << 31;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.035f, 0.04f, 0.055f);
                camera.targetTexture = output;
                var root = new GameObject("YLColor.RenderCanvas", typeof(RectTransform), typeof(Canvas));
                SceneManager.MoveGameObjectToScene(root, scene);
                var canvas = root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = camera;
                var rootRect = (RectTransform)root.transform;
                rootRect.sizeDelta = new Vector2(960, 600);
                rootRect.localScale = Vector3.one * 0.01f;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.kano.ylframework/Prefabs/ColorSelector.prefab");
                Assert.That(prefab, Is.Not.Null);
                var left = Container(root.transform, "Stencil", new Vector2(360, 410), new Vector2(-200, 0));
                left.gameObject.AddComponent<Image>();
                left.gameObject.AddComponent<Mask>().showMaskGraphic = false;
                var right = Container(root.transform, "RectClip", new Vector2(360, 320), new Vector2(200, 0));
                right.gameObject.AddComponent<RectMask2D>();
                right.gameObject.AddComponent<CanvasGroup>().alpha = 0.5f;
                foreach (var parent in new[] { left, right })
                {
                    var item = Object.Instantiate(prefab, parent, false);
                    Object.DestroyImmediate(item.GetComponent<ColorSelectorWindow>());
                    item.GetComponentInChildren<ColorSelector>().BeginSession(parent == left ? Color.green : Color.blue, null);
                }
                SetLayer(root.transform);
                Canvas.ForceUpdateCanvases();
                camera.Render();
                RenderTexture.active = output;
                pixels.ReadPixels(new Rect(0, 0, 960, 600), 0, 0);
                pixels.Apply();
                var redRing = pixels.GetPixel(408, 293);
                Assert.That(redRing.r, Is.GreaterThan(0.9f));
                Assert.That(redRing.g, Is.LessThan(0.08f));
                var white = pixels.GetPixel(356, 250);
                Assert.That(white.r, Is.GreaterThan(0.9f), "The white triangle corner must remain visually white, not inherit the selected Hue or background tint.");
                Assert.That(white.b, Is.GreaterThan(0.9f));
                var blue = pixels.GetPixel(680, 375);
                Assert.That(blue.b, Is.GreaterThan(blue.r + 0.2f), "Second instance must render its own blue triangle through CanvasGroup alpha.");
                var outside = pixels.GetPixel(680, 485);
                Assert.That(outside.r, Is.EqualTo(camera.backgroundColor.r).Within(0.01f), "RectMask2D must clip the window above its actual rect.");
            }
            finally
            {
                RenderTexture.active = oldTarget;
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(pixels);
                Object.DestroyImmediate(output);
                if (previous.IsValid() && previous.isLoaded)
                {
                    SceneManager.SetActiveScene(previous);
                }
            }
        }

        private static RectTransform Container(Transform parent, string name, Vector2 size, Vector2 position)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            return rect;
        }

        private static void SetLayer(Transform root)
        {
            root.gameObject.layer = 31;
            foreach (Transform child in root)
            {
                SetLayer(child);
            }
        }
    }
}
