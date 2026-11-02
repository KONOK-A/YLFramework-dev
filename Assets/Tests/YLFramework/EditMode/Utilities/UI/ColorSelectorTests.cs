using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YLFramework.UI;

namespace YLFramework.Core.Tests
{
    public class ColorSelectorTests
    {
        [Test]
        public void ConvenienceTypesUseUiNamespaceWithoutLegacyAliases()
        {
            foreach (var type in new[] { typeof(ColorSelector), typeof(ColorSelectorWindow), typeof(ColorSelectorCursor), typeof(ColorWheelGraphic) })
            {
                Assert.That(type.Namespace, Is.EqualTo("YLFramework.UI"));
                Assert.That(type.Assembly.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
                Assert.That(type.Assembly.GetType("YLFramework.Core." + type.Name), Is.Null);
            }
        }

        [UnityTest]
        public IEnumerator ToolConsumerCompilesWithCoreForEditor() => CompileToolConsumer(AssemblyBuilderFlags.EditorAssembly);

        [UnityTest]
        public IEnumerator ToolConsumerCompilesWithCoreForDevelopmentPlayer() => CompileToolConsumer(AssemblyBuilderFlags.DevelopmentBuild);

        private static IEnumerator CompileToolConsumer(AssemblyBuilderFlags flags)
        {
            var folder = Path.Combine(Path.GetTempPath(), "kilo", "YLColor.ToolConsumer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(folder));
            try
            {
                var source = Path.Combine(folder, "ColorToolConsumer.cs");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(source), @"using System;
using UnityEngine;
using YLFramework.Core; using YLFramework.UI;
public static class ColorToolConsumer
{
    public static LoadViewArgs Use(ColorSelector selector, Color initial, Action<Color> changed)
    {
        selector.BeginSession(initial, changed);
        var current = selector.CurrentColor;
        selector.EndSession();
        return ColorSelectorWindow.Arguments(current, changed);
    }
}");
                var output = Path.Combine(folder, "ColorToolConsumer.dll");
                var ui = typeof(Graphic).Assembly.Location;
                var runtime = typeof(Panel).Assembly.Location;
                var editor = (flags & AssemblyBuilderFlags.EditorAssembly) != 0;
                var trusted = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(EditorApplication.applicationContentsPath)) + Path.DirectorySeparatorChar;
                var builder = new AssemblyBuilder(output, new[] { source })
                {
                    flags = flags,
                    buildTarget = BuildTarget.StandaloneWindows64,
                    buildTargetGroup = BuildTargetGroup.Standalone,
                    referencesOptions = ReferencesOptions.UseEngineModules,
                    additionalReferences = new[] { ui, runtime }
                };
                builder.excludeReferences = builder.defaultReferences.Where(path =>
                    !Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(path)).StartsWith(trusted, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).Contains("TestRunner") ||
                    (!editor && Path.GetFileName(path).StartsWith("UnityEditor", StringComparison.Ordinal))).ToArray();
                Assert.That(builder.defaultDefines.Contains("UNITY_EDITOR"), Is.EqualTo(editor));
                var references = builder.defaultReferences.Except(builder.excludeReferences).Concat(new[] { ui, runtime }).ToArray();
                Assert.That(references.Contains(runtime), Is.True, "The convenience tool is consumed through the Runtime/Core API.");
                Assert.That(references.Any(path => Path.GetFileName(path).Contains("TestRunner") ||
                    Path.GetFileName(path).StartsWith("YLFramework.Core.Tests", StringComparison.Ordinal)), Is.False);
                CompilerMessage[] messages = null;
                builder.buildFinished += (_, result) => messages = result;
                Assert.That(builder.Build(), Is.True);
                while (builder.status != AssemblyBuilderStatus.Finished)
                {
                    yield return null;
                }

                Assert.That(messages, Is.Not.Null);
                Assert.That(messages.Where(message => message.type == CompilerMessageType.Error).Select(message => message.message), Is.Empty);
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output)), Is.True);
            }
            finally
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(folder), true);
            }
        }

        [Test]
        public void ConvenienceWidgetCannotBypassAnUnloadedPanelWithACustomGate()
        {
            var root = new GameObject("YLColor.UnloadedPanel", typeof(ColorSelectorWindow));
            var go = new GameObject("YLColor.NearerGate", typeof(RectTransform));
            try
            {
                go.transform.SetParent(root.transform);
                var gate = go.AddComponent<ColorInputGateProbe>();
                gate.isAllowed = true;
                var selector = go.AddComponent<ColorSelector>();
                var rect = go.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(300, 300);
                int changes = 0;
                selector.BeginSession(Color.red, _ => changes++);
                selector.OnPointerDown(Pointer(rect, new Vector2(-0.44f, 0.7621024f), 1));
                Assert.That(root.GetComponent<Panel>().IsLoaded, Is.False);
                AssertColor(selector.CurrentColor, Color.red);
                Assert.That(changes, Is.Zero);
                Assert.That(gate.reads, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void StandaloneWidgetWithoutPanelIgnoresCustomInputProperties()
        {
            var go = new GameObject("YLColor.Standalone", typeof(RectTransform));
            try
            {
                var gate = go.AddComponent<ColorInputGateProbe>();
                gate.isAllowed = false;
                var selector = go.AddComponent<ColorSelector>();
                var rect = go.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(300, 300);
                selector.BeginSession(Color.red, null);
                selector.OnPointerDown(Pointer(rect, new Vector2(-0.44f, 0.7621024f), 1));
                AssertColor(selector.CurrentColor, Color.green);
                Assert.That(gate.reads, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ConvenienceToolKeepsItsScriptIdentityWithoutASharedInputGate()
        {
            Assert.That(typeof(ColorSelector).Assembly.GetType("YLFramework.UI.IUIInputGate"), Is.Null);
            const string WindowPath = "Packages/com.kano.ylframework/Runtime/Utilities/UI/ColorSelector/ColorSelectorWindow.cs";
            Assert.That(AssetDatabase.AssetPathToGUID(WindowPath), Is.EqualTo("4878c19ecf0794f45baa1869cc9513cb"));
            Assert.That(AssetDatabase.LoadAssetAtPath<MonoScript>(WindowPath).GetClass(), Is.EqualTo(typeof(ColorSelectorWindow)));
        }

        [Test]
        public void WidgetCachesNearestPanelAndDoesNotFallBackAfterItsDestruction()
        {
            var root = new GameObject("YLColor.OuterPanel", typeof(ColorSelectorWindow));
            var host = new GameObject("YLColor.NearestPanel", typeof(ColorSelectorWindow));
            var go = new GameObject("YLColor.GatedWidget", typeof(RectTransform));
            try
            {
                host.transform.SetParent(root.transform);
                go.transform.SetParent(host.transform);
                var nearest = host.GetComponent<Panel>();
                var selector = go.AddComponent<ColorSelector>();
                var rect = go.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(300, 300);
                selector.BeginSession(Color.red, null);
                var data = Pointer(rect, new Vector2(-0.44f, 0.7621024f), 6);
                selector.OnPointerDown(data);
                AssertColor(selector.CurrentColor, Color.red);
                UnityEngine.Object.DestroyImmediate(nearest);
                selector.OnPointerDown(data);
                AssertColor(selector.CurrentColor, Color.green, "A destroyed cached Panel follows the no-host default without resolving another ancestor.");
                selector.BeginSession(Color.red, null);
                selector.OnPointerDown(data);
                AssertColor(selector.CurrentColor, Color.red);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void InactiveAncestorsAndDisabledPanelsKeepUnityHostSelectionRules()
        {
            var root = new GameObject("YLColor.ActivePanel", typeof(ColorSelectorWindow));
            var host = new GameObject("YLColor.InactivePanel", typeof(ColorSelectorWindow));
            var go = new GameObject("YLColor.ChildWidget", typeof(RectTransform));
            try
            {
                host.transform.SetParent(root.transform);
                go.transform.SetParent(host.transform);
                var selector = go.AddComponent<ColorSelector>();
                var rect = go.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(300, 300);
                host.SetActive(false);
                selector.BeginSession(Color.red, null);
                host.SetActive(true);
                var data = Pointer(rect, new Vector2(-0.44f, 0.7621024f), 1);
                selector.OnPointerDown(data);
                AssertColor(selector.CurrentColor, Color.red);
                UnityEngine.Object.DestroyImmediate(root.GetComponent<Panel>());
                selector.OnPointerDown(data);
                AssertColor(selector.CurrentColor, Color.green);
                host.GetComponent<Panel>().enabled = false;
                selector.BeginSession(Color.red, null);
                selector.OnPointerDown(data);
                AssertColor(selector.CurrentColor, Color.red, "Disabled Panel components remain eligible hosts under Unity's ancestor lookup.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void InitialColorIsCurrentWithoutSendingUserNotification()
        {
            var go = new GameObject("YLColor.Initial", typeof(RectTransform));
            try
            {
                var selector = go.AddComponent<ColorSelector>();
                int changes = 0;
                var initial = new Color(0.25f, 0.5f, 0.75f, 0.3f);
                selector.BeginSession(initial, _ => changes++);
                Assert.That(selector.CurrentColor, Is.EqualTo(initial));
                Assert.That(changes, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void TriangleVerticesAndEdgesAgreeWithHSVAndPreserveAlpha()
        {
            var go = new GameObject("YLColor.Triangle", typeof(RectTransform));
            try
            {
                var rect = go.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(320, 240);
                rect.pivot = new Vector2(0.2f, 0.7f);
                rect.position = new Vector3(410, 270);
                rect.localScale = new Vector3(0.75f, 0.75f, 1);
                rect.localRotation = Quaternion.Euler(0, 0, 27);
                var selector = go.AddComponent<ColorSelector>();
                selector.BeginSession(new Color(1, 0, 0, 0.4f), null);
                var data = Pointer(rect, new Vector2(0, 0), 4);
                selector.OnPointerDown(data);
                data.position = Screen(rect, new Vector2(0, 0.65f));
                selector.OnDrag(data);
                AssertColor(selector.CurrentColor, new Color(1, 0, 0, 0.4f));
                data.position = Screen(rect, new Vector2(-0.5629165f, -0.325f));
                selector.OnDrag(data);
                AssertColor(selector.CurrentColor, new Color(0, 0, 0, 0.4f));
                data.position = Screen(rect, new Vector2(0.5629165f, -0.325f));
                selector.OnDrag(data);
                AssertColor(selector.CurrentColor, new Color(1, 1, 1, 0.4f));
                data.position = Screen(rect, new Vector2(0, -0.9f));
                selector.OnDrag(data);
                AssertColor(selector.CurrentColor, new Color(0.5f, 0.5f, 0.5f, 0.4f));
                selector.OnPointerUp(data);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void HueDragKeepsRegionAndPointerAndCallbackCanReplaceSession()
        {
            var go = new GameObject("YLColor.Reentrant", typeof(RectTransform));
            try
            {
                var selector = go.AddComponent<ColorSelector>();
                var rect = go.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(300, 300);
                int old = 0, next = 0;
                selector.BeginSession(Color.red, color =>
                {
                    old++;
                    AssertColor(color, Color.green);
                    selector.BeginSession(Color.blue, _ => next++);
                });
                var data = Pointer(rect, new Vector2(-0.44f, 0.7621024f), 6);
                selector.OnPointerDown(data);
                Assert.That(old, Is.EqualTo(1));
                AssertColor(selector.CurrentColor, Color.blue);
                data.position = Screen(rect, new Vector2(0.3f, 0));
                selector.OnDrag(data);
                AssertColor(selector.CurrentColor, Color.blue);
                Assert.That(next, Is.Zero);
                selector.BeginSession(Color.red, _ => next++);
                selector.OnPointerDown(Pointer(rect, new Vector2(0.88f, 0), 7));
                data.pointerId = 8;
                selector.OnDrag(data);
                AssertColor(selector.CurrentColor, Color.red);
                data.pointerId = 7;
                data.position = Screen(rect, new Vector2(-0.1f, 0.1732051f));
                selector.OnDrag(data);
                AssertColor(selector.CurrentColor, Color.green);
                Assert.That(next, Is.EqualTo(1), "Hue drag remains Hue even after crossing into the triangle.");
                selector.EndSession();
                selector.OnPointerDown(Pointer(rect, new Vector2(0.88f, 0), 9));
                AssertColor(selector.CurrentColor, Color.green);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void WheelMeshUsesSameTriangleAndIndependentVertexColorsNotSharedMaterialMutation()
        {
            var first = new GameObject("YLColor.MeshA", typeof(RectTransform), typeof(ColorWheelGraphic));
            var second = new GameObject("YLColor.MeshB", typeof(RectTransform), typeof(ColorWheelGraphic));
            var mesh = new Mesh();
            try
            {
                var a = first.GetComponent<ColorWheelGraphic>();
                var b = second.GetComponent<ColorWheelGraphic>();
                a.rectTransform.sizeDelta = new Vector2(200, 200);
                var material = a.material;
                a.Hue = 1f / 3;
                using (var helper = new VertexHelper())
                {
                    typeof(ColorWheelGraphic).GetMethod("OnPopulateMesh", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly)
                        .Invoke(a, new object[] { helper });
                    helper.FillMesh(mesh);
                }
                var colors = mesh.colors32;
                var vertices = mesh.vertices;
                Assert.That(vertices[vertices.Length - 3], Is.EqualTo(new Vector3(0, 65, 0)));
                Assert.That(colors[colors.Length - 3], Is.EqualTo((Color32)Color.green));
                Assert.That(colors[colors.Length - 2], Is.EqualTo((Color32)Color.black));
                Assert.That(colors[colors.Length - 1], Is.EqualTo((Color32)Color.white));
                Assert.That(b.Hue, Is.Zero);
                Assert.That(a.material, Is.SameAs(material));
                Assert.That(a.material, Is.SameAs(b.material));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        [TestCase(1f, 0f, 0f, 0f)]
        [TestCase(0f, 1f, 0f, 0.3333333f)]
        [TestCase(0f, 0f, 1f, 0.6666667f)]
        [TestCase(0f, 0f, 0f, 0f)]
        [TestCase(1f, 1f, 1f, 0f)]
        [TestCase(0.5f, 0.5f, 0.5f, 0f)]
        public void InitialPrimaryAndAchromaticColorsReplacePriorHueAndKeepAlpha(float r, float g, float b, float hue)
        {
            var go = new GameObject("YLColor.InitialEndpoints", typeof(RectTransform));
            try
            {
                var selector = go.AddComponent<ColorSelector>();
                selector.BeginSession(Color.magenta, null);
                var initial = new Color(r, g, b, 0.4f);
                int changes = 0;
                selector.BeginSession(initial, _ => changes++);
                Assert.That(selector.CurrentColor, Is.EqualTo(initial));
                Assert.That(go.GetComponent<ColorWheelGraphic>().Hue, Is.EqualTo(hue).Within(0.001f));
                Assert.That(changes, Is.Zero);
                selector.OnPointerDown(Pointer(go.GetComponent<RectTransform>(), new Vector2(-0.44f, 0.7621024f), 2));
                AssertColor(selector.CurrentColor, r == g && g == b ? initial : new Color(0, 1, 0, 0.4f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void InvalidInitialRGBDoesNotReplaceTheHealthySession()
        {
            var go = new GameObject("YLColor.Invalid", typeof(RectTransform));
            try
            {
                var selector = go.AddComponent<ColorSelector>();
                selector.BeginSession(Color.blue, null);
                Assert.Throws<ArgumentException>(() => selector.BeginSession(new Color(float.NaN, 0, 0), null));
                Assert.Throws<ArgumentException>(() => selector.BeginSession(new Color(1.5f, 0, 0), null));
                Assert.That(selector.CurrentColor, Is.EqualTo(Color.blue));
                go.GetComponent<RectTransform>().sizeDelta = Vector2.zero;
                Assert.Throws<InvalidOperationException>(() => selector.OnPointerDown(new PointerEventData(null)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static PointerEventData Pointer(RectTransform rect, Vector2 normalized, int id)
            => new PointerEventData(null) { position = Screen(rect, normalized), pointerId = id, button = PointerEventData.InputButton.Left };
        private static Vector2 Screen(RectTransform rect, Vector2 normalized)
            => RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center + normalized * (Mathf.Min(rect.rect.width, rect.rect.height) * 0.5f)));
        private static void AssertColor(Color actual, Color expected, string message = null)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.001f), message);
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.001f), message);
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.001f), message);
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(0.001f), message);
        }
    }

    public sealed class ColorInputGateProbe : MonoBehaviour
    {
        [global::UnityEngine.Serialization.FormerlySerializedAs("Allowed")]
        public bool isAllowed;
        [global::UnityEngine.Serialization.FormerlySerializedAs("Reads")]
        public int reads;
        public bool IsInputAllowed
        {
            get
            {
                reads++;
                return isAllowed;
            }
        }
    }
}
