using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YLFramework.UI;

namespace YLFramework.Core.Tests
{
    public class CoreDefaultResourceTests
    {
        private const string ResourceRoot = "Assets/YLFramework/Runtime/Core/Resources/";
        private const string OriginalAdaptationGuid = "abf116dc13c27ef4583812631aacc1c3";
        private const string AdaptationPath = "Assets/YLFramework/Runtime/Core/UI/Adaptation/AutomaticAdaptationCanvasScaler.cs";

        [TestCase("Canvas", "0df4ee5a6aa484a40b5d537a1c23a2c0")]
        [TestCase("EventSystem", "e5f419ca4a6e564439be23e0257ffb3c")]
        public void DefaultResourcesHaveUniqueKeysAndOwnedIdentities(string name, string expectedGuid)
        {
            string key = "YLFramework/Core/" + name;
            string path = ResourceRoot + key + ".prefab";
            var prefab = Resources.Load<GameObject>(key);
            Assert.That(prefab, Is.Not.Null);
            Assert.That(AssetDatabase.GetAssetPath(prefab), Is.EqualTo(path));
            string guid = AssetDatabase.AssetPathToGUID(path);
            Assert.That(guid, Has.Length.EqualTo(32));
            Assert.That(guid, Is.EqualTo(expectedGuid));
            Assert.That(AssetDatabase.GUIDToAssetPath(guid), Is.EqualTo(path));

            var paths = AssetDatabase.GetAllAssetPaths().Where(p =>
            {
                int index = p.LastIndexOf("/Resources/", StringComparison.OrdinalIgnoreCase);
                return index >= 0 && !AssetDatabase.IsValidFolder(p) &&
                    string.Equals(Path.ChangeExtension(p.Substring(index + 11), null), key, StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            Assert.That(paths, Is.EqualTo(new[] { path }), key + ": " + string.Join(", ", paths));

            foreach (var transform in prefab.GetComponentsInChildren<Transform>(true))
            {
                Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject), Is.Zero);
                foreach (var component in transform.GetComponents<Component>())
                {
                    Assert.That(component, Is.Not.Null);
                    CollectionAssert.Contains(new[] { typeof(UIRootContext).Assembly,
                        typeof(EventSystem).Assembly, typeof(Transform).Assembly, typeof(Canvas).Assembly }, component.GetType().Assembly);
                    using (var serialized = new SerializedObject(component))
                    {
                        var property = serialized.GetIterator();
                        while (property.Next(true))
                        {
                            if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue == null)
                            {
                                Assert.That(property.objectReferenceInstanceIDValue, Is.Zero, component.GetType() + ": " + property.propertyPath);
                            }
                        }
                    }
                }
            }
#if UNITY_6000_0_OR_NEWER
            const string UguiRuntimeRoot = "Packages/com.unity.ugui/Runtime/UGUI/";
#else
            const string UguiRuntimeRoot = "Packages/com.unity.ugui/Runtime/";
#endif
            var allowedDependencies = new[]
            {
                path, AdaptationPath,
                "Assets/YLFramework/Runtime/Core/UI/Roots/UIRootContext.cs",
                UguiRuntimeRoot + "UI/Core/Layout/CanvasScaler.cs",
                UguiRuntimeRoot + "UI/Core/GraphicRaycaster.cs",
                UguiRuntimeRoot + "EventSystem/EventSystem.cs"
            };
            Assert.That(AssetDatabase.GetDependencies(path), Is.SubsetOf(allowedDependencies));
        }

        [Test]
        public void LightweightCanvasHasAnExplicitRootAndFormalScaleConfiguration()
        {
            string path = ResourceRoot + "YLFramework/Core/Canvas.prefab";
            string adaptationGuid = AssetDatabase.AssetPathToGUID(AdaptationPath);
            Assert.That(adaptationGuid, Is.Not.Empty.And.Not.EqualTo(OriginalAdaptationGuid));
            Assert.That(adaptationGuid, Is.EqualTo("36162b36843cfe2498fbdd4ff95ac2b4"));
            Assert.That(AssetDatabase.GUIDToAssetPath(adaptationGuid), Is.EqualTo(AdaptationPath));
            var prefab = Resources.Load<GameObject>("YLFramework/Core/Canvas");
            Assert.That(AssetDatabase.GetAssetPath(prefab), Is.EqualTo(path));
            Assert.That(prefab.GetComponents<Component>().Select(c => c.GetType()), Is.SupersetOf(new[]
            {
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(AutomaticAdaptationCanvasScaler), typeof(UIRootContext)
            }));
            var context = prefab.GetComponent<UIRootContext>();
            Assert.That(prefab.GetComponents<UIRootContext>().Length, Is.EqualTo(1));
            Assert.That(context.Canvas, Is.SameAs(prefab.GetComponent<Canvas>()));
            var layers = new SerializedObject(context).FindProperty("uiLayers");
            Assert.That(layers.arraySize, Is.EqualTo(1));
            var layer = layers.GetArrayElementAtIndex(0);
            var container = layer.FindPropertyRelative("root").objectReferenceValue as RectTransform;
            Assert.That(container && container.parent == prefab.transform, Is.True);
            Assert.That(container.anchorMin, Is.EqualTo(Vector2.zero));
            Assert.That(container.anchorMax, Is.EqualTo(Vector2.one));
            Assert.That(layer.FindPropertyRelative("enableMask").boolValue, Is.False);
            var adaptation = prefab.GetComponent<AutomaticAdaptationCanvasScaler>();
            Assert.That(adaptation.GetType().Assembly.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
            Assert.That(AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(adaptation)), Is.EqualTo(AdaptationPath));
            Assert.That(prefab.GetComponent<Canvas>().renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
            Assert.That(prefab.GetComponent<CanvasScaler>().referenceResolution, Is.EqualTo(new Vector2(1920, 1080)));
            var scaler = prefab.GetComponent<CanvasScaler>();
            Assert.That(scaler.uiScaleMode, Is.EqualTo(CanvasScaler.ScaleMode.ScaleWithScreenSize));
            Assert.That(scaler.scaleFactor, Is.EqualTo(1));
            Assert.That(scaler.screenMatchMode, Is.EqualTo(CanvasScaler.ScreenMatchMode.MatchWidthOrHeight));
            Assert.That(scaler.matchWidthOrHeight, Is.EqualTo(1));
            Assert.That(scaler.referencePixelsPerUnit, Is.EqualTo(100));
        }

        [Test]
        public void MigratedTemplateReferencesBaseAndTipsVariantOnlyOverridesLayerFour()
        {
            const string Prefix = "Assets/YLFramework/Prefabs/";
            var template = AssetDatabase.LoadAssetAtPath<GameObject>(Prefix + "YLFramework.prefab");
            var root = template.GetComponentsInChildren<UIRootContext>(true).Single();
            var basic = Resources.Load<GameObject>("YLFramework/Core/Canvas");
            Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(root.gameObject), Is.SameAs(basic));
            Assert.That(new SerializedObject(template.GetComponent<YLFrameworkRoot>()).FindProperty("panelRoot").objectReferenceValue, Is.SameAs(root));
            var tips = AssetDatabase.LoadAssetAtPath<GameObject>(Prefix + "UITips.prefab");
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(Prefix + "UITipsOverlay.prefab");
            Assert.That(variant, Is.Not.Null, "须先执行显式资产迁移并导入新 meta。");
            Assert.That(PrefabUtility.GetPrefabAssetType(variant), Is.EqualTo(PrefabAssetType.Variant));
            Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(variant), Is.SameAs(tips));
            var defaults = new SerializedObject(tips.GetComponent<UITipsItem>()).FindProperty("settings");
            Assert.That(defaults.FindPropertyRelative("layer").intValue, Is.Zero);
            Assert.That(defaults.FindPropertyRelative("instanceMode").enumValueIndex, Is.EqualTo((int)PanelInstanceMode.Multiple));
            Assert.That(defaults.FindPropertyRelative("modal").boolValue || defaults.FindPropertyRelative("coversRoot").boolValue || defaults.FindPropertyRelative("participatesInTop").boolValue, Is.False);
            Assert.That(defaults.FindPropertyRelative("cacheOnClose").boolValue, Is.True);
            var inherited = new SerializedObject(variant.GetComponent<UITipsItem>()).FindProperty("settings");
            Assert.That(inherited.FindPropertyRelative("layer").intValue, Is.EqualTo(4));
            foreach (var field in new[] { "priority", "modal", "coversRoot", "participatesInTop", "instanceMode", "cacheOnClose" })
            {
                Assert.That(inherited.FindPropertyRelative(field).prefabOverride, Is.False, field);
            }

            Assert.That(new SerializedObject(root.GetComponentInChildren<UITipsPresenter>(true)).FindProperty("uiTipItemPrefab").objectReferenceValue, Is.SameAs(variant));
            Assert.That(variant.GetComponentsInChildren<Graphic>(true).All(graphic => !graphic.raycastTarget), Is.True);
            var color = new SerializedObject(AssetDatabase.LoadAssetAtPath<GameObject>(Prefix + "ColorSelector.prefab").GetComponent<ColorSelectorWindow>()).FindProperty("settings");
            Assert.That(color.FindPropertyRelative("participatesInTop").boolValue && color.FindPropertyRelative("cacheOnClose").boolValue, Is.True);
            Assert.That(color.FindPropertyRelative("instanceMode").enumValueIndex, Is.EqualTo((int)PanelInstanceMode.Single));
        }

        [Test]
        public void EventSystemPreservesNavigationSettingsAndDefersBackendSelectionToRuntime()
        {
            var prefab = Resources.Load<GameObject>("YLFramework/Core/EventSystem");
            Assert.That(prefab.GetComponents<Component>().Select(c => c.GetType()), Is.EqualTo(new[]
            {
                typeof(Transform), typeof(EventSystem)
            }));
            Assert.That(prefab.GetComponent<EventSystem>().sendNavigationEvents, Is.True);
            Assert.That(prefab.GetComponent<EventSystem>().pixelDragThreshold, Is.EqualTo(10));
            Assert.That(prefab.GetComponents<BaseInputModule>(), Is.Empty,
                "An active Legacy module must not run before the framework chooses the supported backend.");
        }
    }
}
