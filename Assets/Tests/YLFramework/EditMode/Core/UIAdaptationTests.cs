using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace YLFramework.Core.Tests
{
    public class UIAdaptationTests
    {
        [Test]
        public void BackgroundCoverRefreshesCanvasAndContentSizesAndNewParent()
        {
            var first = new GameObject("YLAdaptation.CanvasA", typeof(RectTransform), typeof(Canvas));
            var second = new GameObject("YLAdaptation.CanvasB", typeof(RectTransform), typeof(Canvas));
            var background = new GameObject("YLAdaptation.Background", typeof(RectTransform));
            try
            {
                first.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                second.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                var firstRect = first.GetComponent<RectTransform>();
                firstRect.sizeDelta = new Vector2(600, 400);
                second.GetComponent<RectTransform>().sizeDelta = new Vector2(1200, 400);
                var rect = background.GetComponent<RectTransform>();
                rect.SetParent(first.transform, false);
                rect.sizeDelta = new Vector2(300, 200);
                var adaptation = background.AddComponent<BackgroundAdaptation>();
                InvokeLifecycle(adaptation, "OnEnable");
                Assert.That(rect.localScale.x, Is.EqualTo(2).Within(0.001f));

                firstRect.sizeDelta = new Vector2(900, 400);
                InvokeLifecycle(adaptation, "LateUpdate");
                Assert.That(rect.localScale.x, Is.EqualTo(3).Within(0.001f), "Canvas resizing must refresh cover without toggling the component.");

                rect.sizeDelta = new Vector2(450, 200);
                InvokeLifecycle(adaptation, "LateUpdate");
                Assert.That(rect.localScale.x, Is.EqualTo(2).Within(0.001f), "Cover must use the current unscaled content size.");

                rect.SetParent(second.transform, false);
                InvokeLifecycle(adaptation, "LateUpdate");
                Assert.That(rect.localScale.x, Is.EqualTo(1200f / 450).Within(0.001f), "Reparenting must not retain the previous Canvas.");
                adaptation.enabled = false;
                rect.localScale = Vector3.one;
                adaptation.enabled = true;
                InvokeLifecycle(adaptation, "OnEnable");
                Assert.That(rect.localScale.x, Is.EqualTo(1200f / 450).Within(0.001f), "Re-enabling must reapply cover even when sizes are unchanged.");
            }
            finally
            {
                Object.DestroyImmediate(background);
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void BorderHorizontalOffsetPreservesExistingVerticalPosition()
        {
            var go = new GameObject("YLAdaptation.Border", typeof(RectTransform));
            try
            {
                var rect = go.GetComponent<RectTransform>();
                rect.anchoredPosition = new Vector2(15, 37);
                var adaptation = go.AddComponent<BorderAdaptation>();
                InvokeLifecycle(adaptation, "Start");
                Assert.That(rect.anchoredPosition.y, Is.EqualTo(37), "Wide-screen margin must not erase vertical layout.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ScalerAdoptsExistingResolutionAndKeepsConfiguredMatchAsTheOnlyAuthority()
        {
            var go = new GameObject("YLAdaptation.Scaler", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            try
            {
                var scaler = go.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                var adaptation = go.AddComponent<AutomaticAdaptationCanvasScaler>();
                Assert.That(adaptation.ReferenceResolution, Is.EqualTo(new Vector2(1920, 1080)));
                Assert.That(adaptation.AutomaticMatch, Is.True);

                adaptation.Configure(new Vector2(1280, 720), false, 0.35f);
                scaler.referenceResolution = new Vector2(600, 900);
                scaler.matchWidthOrHeight = 1;
                InvokeLifecycle(adaptation, "Update");
                Assert.That(scaler.referenceResolution, Is.EqualTo(new Vector2(1280, 720)));
                Assert.That(scaler.matchWidthOrHeight, Is.EqualTo(0.35f));

                adaptation.Configure(new Vector2(1, 10000), true, 0.35f);
                Assert.That(scaler.matchWidthOrHeight, Is.EqualTo(1));
                adaptation.ReferenceResolution = new Vector2(10000, 1);
                Assert.That(scaler.matchWidthOrHeight, Is.Zero, "Changing configuration on the same screen must recalculate automatic match.");
                adaptation.AutomaticMatch = false;
                adaptation.ManualMatch = 0.6f;
                Assert.That(scaler.matchWidthOrHeight, Is.EqualTo(0.6f));
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                adaptation.Configure(new Vector2(640, 480), false, 0.4f);
                adaptation.Refresh();
                Assert.That(scaler.uiScaleMode, Is.EqualTo(CanvasScaler.ScaleMode.ConstantPixelSize),
                    "Adaptation owns resolution/match, not the native CanvasScaler scale mode.");
                Assert.Throws<ArgumentException>(() => adaptation.Configure(Vector2.zero, true, 0));
                Assert.That(adaptation.ReferenceResolution, Is.EqualTo(new Vector2(640, 480)));
                Assert.That(adaptation.ManualMatch, Is.EqualTo(0.4f));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BorderUsesCurrentCanvasAspectAfterResizeAndReparent()
        {
            var first = new GameObject("YLAdaptation.BorderCanvasA", typeof(RectTransform), typeof(Canvas));
            var second = new GameObject("YLAdaptation.BorderCanvasB", typeof(RectTransform), typeof(Canvas));
            var go = new GameObject("YLAdaptation.DynamicBorder", typeof(RectTransform));
            try
            {
                first.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                second.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                var firstRect = first.GetComponent<RectTransform>();
                firstRect.sizeDelta = new Vector2(1770, 1000);
                second.GetComponent<RectTransform>().sizeDelta = new Vector2(1770, 1000);
                var rect = go.GetComponent<RectTransform>();
                rect.SetParent(first.transform, false);
                rect.anchoredPosition = new Vector2(15, 37);
                var adaptation = go.AddComponent<BorderAdaptation>();
                Assert.That(adaptation.Refresh(), Is.True);
                Assert.That(rect.anchoredPosition.x, Is.Zero.Within(0.001f));
                firstRect.sizeDelta = new Vector2(2220, 1000);
                InvokeLifecycle(adaptation, "LateUpdate");
                Assert.That(rect.anchoredPosition.x, Is.EqualTo(-100).Within(0.001f));
                Assert.That(rect.anchoredPosition.y, Is.EqualTo(37));
                rect.SetParent(second.transform, false);
                InvokeLifecycle(adaptation, "LateUpdate");
                Assert.That(rect.anchoredPosition.x, Is.Zero.Within(0.001f));
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void BackgroundReportsZeroSizeWithoutCorruptingScaleAndRecovers()
        {
            var canvas = new GameObject("YLAdaptation.ZeroCanvas", typeof(RectTransform), typeof(Canvas));
            var go = new GameObject("YLAdaptation.ZeroBackground", typeof(RectTransform));
            try
            {
                canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                canvas.GetComponent<RectTransform>().sizeDelta = new Vector2(600, 400);
                go.SetActive(false);
                var rect = go.GetComponent<RectTransform>();
                rect.SetParent(canvas.transform, false);
                rect.sizeDelta = Vector2.zero;
                rect.localScale = new Vector3(3, 3, 3);
                var adaptation = go.AddComponent<BackgroundAdaptation>();
                LogAssert.Expect(LogType.Error, "BackgroundAdaptation requires positive finite Canvas and content sizes.");
                Assert.That(adaptation.Refresh(), Is.False);
                Assert.That(rect.localScale, Is.EqualTo(new Vector3(3, 3, 3)));
                Assert.That(adaptation.Refresh(), Is.False, "A persistent error must not emit a log every frame.");
                rect.sizeDelta = new Vector2(100, 100);
                Assert.That(adaptation.Refresh(), Is.True);
                Assert.That(rect.localScale.x, Is.EqualTo(6));
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(canvas);
            }
        }

        [TestCase(RenderMode.ScreenSpaceOverlay)]
        [TestCase(RenderMode.ScreenSpaceCamera)]
        public void SafeAreaConvertsScreenRectInsideNestedParentWithoutChangingBackground(RenderMode mode)
        {
            var root = new GameObject("YLAdaptation.SafeCanvas", typeof(RectTransform), typeof(Canvas));
            var parent = new GameObject("YLAdaptation.SafeParent", typeof(RectTransform));
            var content = new GameObject("YLAdaptation.SafeContent", typeof(RectTransform));
            var background = new GameObject("YLAdaptation.FullBackground", typeof(RectTransform));
            var cameraObject = new GameObject("YLAdaptation.SafeCamera", typeof(Camera));
            var texture = new RenderTexture(800, 600, 0);
            try
            {
                var canvas = root.GetComponent<Canvas>();
                canvas.renderMode = mode;
                Camera camera = null;
                Vector2 display = new Vector2(800, 600);
                if (mode == RenderMode.ScreenSpaceCamera)
                {
                    camera = cameraObject.GetComponent<Camera>();
                    camera.targetTexture = texture;
                    camera.orthographic = true;
                    camera.orthographicSize = 300;
                    camera.rect = new Rect(0.05f, 0.05f, 0.9f, 0.9f);
                    canvas.worldCamera = camera;
                    canvas.planeDistance = 1;
                }
                var parentRect = parent.GetComponent<RectTransform>();
                parentRect.SetParent(root.transform, false);
                parentRect.anchorMin = new Vector2(0.05f, 0.05f);
                parentRect.anchorMax = new Vector2(0.95f, 0.95f);
                parentRect.offsetMin = parentRect.offsetMax = Vector2.zero;
                var backgroundRect = background.GetComponent<RectTransform>();
                backgroundRect.SetParent(root.transform, false);
                backgroundRect.anchorMin = Vector2.zero;
                backgroundRect.anchorMax = Vector2.one;
                backgroundRect.offsetMin = backgroundRect.offsetMax = Vector2.zero;
                var rect = content.GetComponent<RectTransform>();
                rect.SetParent(parent.transform, false);
                content.SetActive(false);
                var adaptation = content.AddComponent<SafeAreaAdaptation>();
                Canvas.ForceUpdateCanvases();
                // EditMode 的 Screen 尺寸不一定等于 Overlay Canvas 的实际像素区域。
                if (mode == RenderMode.ScreenSpaceOverlay)
                {
                    display = canvas.pixelRect.size;
                }

                Assert.That(adaptation.Refresh(new Rect(80, 120, 640, 360), new Vector2(800, 600)), Is.True);
                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                var min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
                var max = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
                Assert.That(min.x, Is.EqualTo(display.x * 0.1f).Within(0.1f));
                Assert.That(min.y, Is.EqualTo(display.y * 0.2f).Within(0.1f));
                Assert.That(max.x, Is.EqualTo(display.x * 0.9f).Within(0.1f));
                Assert.That(max.y, Is.EqualTo(display.y * 0.8f).Within(0.1f));
                Assert.That(adaptation.Refresh(new Rect(60, 80, 480, 640), new Vector2(600, 800)), Is.True);
                rect.GetWorldCorners(corners);
                min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
                max = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
                Assert.That(min.y, Is.EqualTo(display.y * 0.1f).Within(0.1f));
                Assert.That(max.y, Is.EqualTo(display.y * 0.9f).Within(0.1f));
                Assert.That(backgroundRect.anchorMin, Is.EqualTo(Vector2.zero));
                Assert.That(backgroundRect.anchorMax, Is.EqualTo(Vector2.one));
                Assert.That(backgroundRect.offsetMin, Is.EqualTo(Vector2.zero));
                Assert.That(backgroundRect.offsetMax, Is.EqualTo(Vector2.zero));
            }
            finally
            {
                cameraObject.GetComponent<Camera>().targetTexture = null;
                Object.DestroyImmediate(content);
                Object.DestroyImmediate(parent);
                Object.DestroyImmediate(background);
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(texture);
            }
        }

        private static void InvokeLifecycle(MonoBehaviour component, string method)
            => component.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(component, null);
    }
}
