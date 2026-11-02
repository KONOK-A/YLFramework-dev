using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace YLFramework.Core.Tests
{
    public class UIRootConfigurationTests
    {
        [Test]
        public void RootPreservesNativeCameraAndScalerConfigurationWithoutAddingAuthorities()
        {
            var go = new GameObject("YLRoot.Configuration", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var cameraObject = new GameObject("YLRoot.Camera", typeof(Camera));
            try
            {
                go.SetActive(false);
                var canvas = go.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                var camera = cameraObject.GetComponent<Camera>();
                canvas.worldCamera = camera;
                canvas.planeDistance = 5;
                var scaler = go.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(800, 600);
                scaler.matchWidthOrHeight = 0.25f;
                var context = ConfigureRoot(go, out var layer);
                go.SetActive(true);
                typeof(UIRootContext).GetMethod("OnEnable", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(context, null);
                Assert.That(context.Canvas, Is.SameAs(canvas));
                Assert.That(context.GetLayer(0), Is.SameAs(layer));
                Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.ScreenSpaceCamera));
                Assert.That(canvas.worldCamera, Is.SameAs(camera));
                Assert.That(canvas.planeDistance, Is.EqualTo(5));
                Assert.That(scaler.uiScaleMode, Is.EqualTo(CanvasScaler.ScaleMode.ScaleWithScreenSize));
                Assert.That(scaler.referenceResolution, Is.EqualTo(new Vector2(800, 600)));
                Assert.That(scaler.matchWidthOrHeight, Is.EqualTo(0.25f));
                Assert.That(go.GetComponents<CanvasScaler>(), Has.Length.EqualTo(1));
                Assert.That(go.GetComponent<AutomaticAdaptationCanvasScaler>(), Is.Null);
                Assert.That(go.GetComponentsInChildren<Camera>(), Is.Empty);
            }
            finally
            {
                var context = go.GetComponent<UIRootContext>();
                if (context)
                {
                    typeof(UIRootContext).GetMethod("OnDestroy", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(context, null);
                }

                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void MissingDestroyedAndForeignLayersFailWithoutCreatingOrReplacingContainers()
        {
            var go = new GameObject("YLRoot.Explicit", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            var other = new GameObject("YLRoot.Foreign", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            try
            {
                go.SetActive(false);
                other.SetActive(false);
                var context = ConfigureRoot(go, out var layer);
                var foreign = ConfigureRoot(other, out var foreignLayer);
                Assert.That(context.GetLayer(0), Is.SameAs(layer));
                Assert.Throws<ArgumentOutOfRangeException>(() => context.GetLayer(-1));
                Assert.Throws<ArgumentOutOfRangeException>(() => context.GetLayer(1));
                Assert.That(go.transform.childCount, Is.EqualTo(1), "Missing layers must not be synthesized at runtime.");

                layer.SetParent(foreignLayer, false);
                Assert.Throws<ArgumentOutOfRangeException>(() => context.GetLayer(0));
                Assert.That(foreign.GetLayer(0), Is.SameAs(foreignLayer));
                Assert.That(layer.parent, Is.SameAs(foreignLayer), "A rejected mapping must not move the external object.");
                UnityEngine.Object.DestroyImmediate(layer.gameObject);
                Assert.Throws<ArgumentOutOfRangeException>(() => context.GetLayer(0));
                Assert.That(go.transform.childCount, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void UnconfiguredRootReportsMigrationInsteadOfAddingLayersOrScaler()
        {
            var go = new GameObject("YLRoot.Unconfigured", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            try
            {
                go.SetActive(false);
                var context = go.AddComponent<UIRootContext>();
                var error = Assert.Throws<InvalidOperationException>(() => context.GetLayer(0));
                StringAssert.Contains("Layer 0", error.Message);
                Assert.That(go.transform.childCount, Is.Zero);
                Assert.That(go.GetComponent<CanvasScaler>(), Is.Null);
                Assert.That(go.GetComponent<AutomaticAdaptationCanvasScaler>(), Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static UIRootContext ConfigureRoot(GameObject host, out Transform layer)
        {
            Assert.That(host.activeSelf, Is.False);
            var context = host.AddComponent<UIRootContext>();
            layer = new GameObject("Layer0", typeof(RectTransform)).transform;
            layer.SetParent(host.transform, false);
            typeof(UIRootContext).GetField("uiLayers", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(context,
                new[] { new UIRootContext.Layer { root = layer, enableMask = false } });
            return context;
        }
    }
}
