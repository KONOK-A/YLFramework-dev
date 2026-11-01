using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YLFramework.Editor;
using YLFramework.Timing;
using YLFramework.UI;
using YLFramework.Editor.Core;

namespace YLFramework.Core.Editor.Tests
{
    public class CoreDefaultResourceEditorTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void LegacyPanelConfigNamesPreserveExplicitBooleansZerosAndReferences(bool enabled)
        {
            string folder = CreateLegacyPanelFolder();
            try
            {
                var yaml = LegacyPanelYaml(File.ReadAllText(UIPanelAssetMigration.ColorPath));
                var values = new[]
                {
                    ("Layer", "layer", enabled ? 3 : 0),
                    ("Priority", "priority", enabled ? 9 : 0),
                    ("Modal", "modal", enabled ? 1 : 0),
                    ("CoversRoot", "coversRoot", enabled ? 0 : 1),
                    ("ParticipatesInTop", "participatesInTop", enabled ? 1 : 0),
                    ("InstanceMode", "instanceMode", enabled ? 1 : 0),
                    ("CacheOnClose", "cacheOnClose", enabled ? 0 : 1)
                };
                foreach (var value in values)
                {
                    yaml = Regex.Replace(yaml, @"(?m)^    " + value.Item1 + @": [^\r\n]*$",
                        "    " + value.Item1 + ": " + value.Item3);
                }

                string path = folder + "/Legacy.prefab";
                File.WriteAllText(path, yaml, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                string guid = AssetDatabase.AssetPathToGUID(path);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.That(prefab.GetComponent<Panel>(), Is.Not.Null, "The real legacy script reference must survive.");
                var selector = prefab.GetComponentInChildren<ColorSelector>(true);
                Assert.That(selector, Is.Not.Null);
                var cursor = new SerializedObject(selector).FindProperty("outerCursor").objectReferenceValue as Transform;
                Assert.That(cursor && cursor.IsChildOf(prefab.transform), Is.True,
                    "The legacy local fileID reference must resolve inside the copied prefab.");
                Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(prefab), Is.Zero);
                var settings = new SerializedObject(prefab.GetComponent<Panel>()).FindProperty("settings");
                foreach (var value in values)
                {
                    var property = settings.FindPropertyRelative(value.Item2);
                    Assert.That(property, Is.Not.Null, "Migration target field missing: " + value.Item2);
                    if (property.propertyType == SerializedPropertyType.Boolean)
                    {
                        Assert.That(property.boolValue, Is.EqualTo(value.Item3 != 0), value.Item2);
                    }
                    else
                    {
                        Assert.That(property.intValue, Is.EqualTo(value.Item3), value.Item2);
                    }
                }

                Assert.That(PrefabUtility.SavePrefabAsset(prefab), Is.Not.Null);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
                var saved = File.ReadAllText(path);
                foreach (var value in values)
                {
                    StringAssert.Contains("    " + value.Item2 + ": " + value.Item3, saved);
                    Assert.That(Regex.IsMatch(saved, @"(?m)^    " + value.Item1 + @": "), Is.False);
                }
            }
            finally
            {
                Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
            }
        }

        [Test]
        public void LegacyPanelVariantLayerOverrideSurvivesReimport()
        {
            string folder = CreateLegacyPanelFolder();
            try
            {
                string basePath = folder + "/Base.prefab";
                File.WriteAllText(basePath, LegacyPanelYaml(File.ReadAllText(UIPanelAssetMigration.TipsPath)),
                    new UTF8Encoding(false));
                AssetDatabase.ImportAsset(basePath, ImportAssetOptions.ForceSynchronousImport);
                string baseGuid = AssetDatabase.AssetPathToGUID(basePath);
                var variantYaml = File.ReadAllText(UIPanelAssetMigration.TipsVariantPath)
                    .Replace(AssetDatabase.AssetPathToGUID(UIPanelAssetMigration.TipsPath), baseGuid)
                    .Replace("propertyPath: settings.layer", "propertyPath: settings.Layer");
                StringAssert.Contains("propertyPath: settings.Layer", variantYaml);
                string variantPath = folder + "/Variant.prefab";
                File.WriteAllText(variantPath, variantYaml, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(variantPath, ImportAssetOptions.ForceSynchronousImport);
                var variant = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath);
                Assert.That(PrefabUtility.GetPrefabAssetType(variant), Is.EqualTo(PrefabAssetType.Variant));
                var migrate = typeof(UIPanelAssetMigration).GetMethod("MigratePanelConfigOverridePaths",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(migrate, Is.Not.Null, "Legacy property paths require an explicit migration, not a weaker override assertion.");
                var original = PrefabUtility.GetPropertyModifications(variant);
                var legacy = original.Single(change => change.propertyPath == "settings.Layer");
                var conflicting = original.Concat(new[] { new PropertyModification
                {
                    target = legacy.target,
                    propertyPath = "settings.layer",
                    value = "7"
                } }).ToArray();
                PrefabUtility.SetPropertyModifications(variant, conflicting);
                var beforeConflict = PrefabUtility.GetPropertyModifications(variant)
                    .Select(change => (change.target, change.propertyPath, change.value, change.objectReference)).ToArray();
                var conflict = Assert.Throws<TargetInvocationException>(() => migrate.Invoke(null, new object[] { variant }));
                Assert.That(conflict.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(PrefabUtility.GetPropertyModifications(variant)
                    .Select(change => (change.target, change.propertyPath, change.value, change.objectReference)),
                    Is.EqualTo(beforeConflict),
                    "A conflicting legacy/canonical pair must be rejected before any modification.");
                PrefabUtility.SetPropertyModifications(variant, original);
                Assert.That(migrate.Invoke(null, new object[] { variant }), Is.EqualTo(true));
                Assert.That(migrate.Invoke(null, new object[] { variant }), Is.EqualTo(false), "Migration must be idempotent.");
                var layer = new SerializedObject(variant.GetComponent<Panel>()).FindProperty("settings.layer");
                Assert.That(layer, Is.Not.Null, "Migration target field missing: settings.layer");
                Assert.That(layer.intValue, Is.EqualTo(4), "The old override must not silently fall back to the base layer.");
                Assert.That(layer.prefabOverride, Is.True);
                Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(variant),
                    Is.SameAs(AssetDatabase.LoadAssetAtPath<GameObject>(basePath)));
                Assert.That(PrefabUtility.SavePrefabAsset(variant), Is.Not.Null);
                AssetDatabase.ImportAsset(variantPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                var reloaded = new SerializedObject(AssetDatabase.LoadAssetAtPath<GameObject>(variantPath).GetComponent<Panel>());
                Assert.That(reloaded.FindProperty("settings.layer").intValue, Is.EqualTo(4));
                Assert.That(reloaded.FindProperty("settings.layer").prefabOverride, Is.True);
            }
            finally
            {
                Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
            }
        }

        private static string CreateLegacyPanelFolder()
        {
            string name = "LegacyPanelFields_" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets/YLFramework/Tests/EditMode/Editor/Core", name), Is.Not.Empty);
            return "Assets/YLFramework/Tests/EditMode/Editor/Core/" + name;
        }

        [Test]
        public void LegacyTimerEventsRetainPersistentTargetsArgumentsAndClockPolicy()
        {
            string folder = CreateLegacyPanelFolder();
            try
            {
                string path = folder + "/Timer.prefab";
                File.WriteAllBytes(path, File.ReadAllBytes(UIPanelAssetMigration.TipsPath));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    contents.SetActive(false);
                    var timer = contents.AddComponent<TimerObject>();
                    Assert.That(timer.delay, Is.EqualTo(2));
                    Assert.That(timer.ignoreTimeScale, Is.True);
                    timer.delay = 3.25f;
                    timer.ignoreTimeScale = false;
                    timer.onCompleted = new UnityEngine.Events.UnityEvent();
                    timer.onAborted = new UnityEngine.Events.UnityEvent();
                    UnityEditor.Events.UnityEventTools.AddBoolPersistentListener(timer.onCompleted, contents.SetActive, true);
                    UnityEditor.Events.UnityEventTools.AddBoolPersistentListener(timer.onAborted, contents.SetActive, false);
                    Assert.That(PrefabUtility.SaveAsPrefabAsset(contents, path), Is.Not.Null);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }

                string yaml = File.ReadAllText(path).Replace("\n  onCompleted:", "\n  Action:")
                    .Replace("\n  onAborted:", "\n  Abort:");
                StringAssert.Contains("\n  Action:", yaml);
                StringAssert.Contains("\n  Abort:", yaml);
                File.WriteAllText(path, yaml, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var restored = prefab.GetComponent<TimerObject>();
                Assert.That(restored.delay, Is.EqualTo(3.25f));
                Assert.That(restored.ignoreTimeScale, Is.False);
                var serialized = new SerializedObject(restored);
                foreach (var entry in new[] { ("onCompleted", true), ("onAborted", false) })
                {
                    var property = serialized.FindProperty(entry.Item1);
                    Assert.That(property, Is.Not.Null, "Migration target field missing: " + entry.Item1);
                    var calls = property.FindPropertyRelative("m_PersistentCalls.m_Calls");
                    Assert.That(calls.arraySize, Is.EqualTo(1));
                    var call = calls.GetArrayElementAtIndex(0);
                    Assert.That(call.FindPropertyRelative("m_Target").objectReferenceValue, Is.SameAs(prefab));
                    Assert.That(call.FindPropertyRelative("m_MethodName").stringValue, Is.EqualTo("SetActive"));
                    Assert.That(call.FindPropertyRelative("m_Arguments.m_BoolArgument").boolValue, Is.EqualTo(entry.Item2));
                }

                Assert.That(PrefabUtility.SavePrefabAsset(prefab), Is.Not.Null);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                StringAssert.Contains("\n  onCompleted:", File.ReadAllText(path));
                StringAssert.Contains("\n  onAborted:", File.ReadAllText(path));
            }
            finally
            {
                Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
            }
        }

        private static string LegacyPanelYaml(string yaml)
        {
            foreach (var names in new[]
            {
                ("Layer", "layer"), ("Priority", "priority"), ("Modal", "modal"),
                ("CoversRoot", "coversRoot"), ("ParticipatesInTop", "participatesInTop"),
                ("InstanceMode", "instanceMode"), ("CacheOnClose", "cacheOnClose")
            })
            {
                yaml = Regex.Replace(yaml, @"(?m)^    " + names.Item2 + @": ", "    " + names.Item1 + ": ");
                Assert.That(Regex.Matches(yaml, @"(?m)^    " + names.Item1 + @": ").Count, Is.EqualTo(1));
            }

            return yaml;
        }

        [Test]
        public void PanelMigrationAndIl2CppBuildAreExplicitEntrypointsWithoutAutomaticMigrationHooks()
        {
            foreach (var type in new[] { typeof(UIPanelAssetMigration), typeof(UIPanelAcceptanceBuild) })
            {
                Assert.That(type.GetCustomAttributes(typeof(InitializeOnLoadAttribute), false), Is.Empty);
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    Assert.That(method.GetCustomAttributes(typeof(InitializeOnLoadMethodAttribute), false), Is.Empty, method.Name);
                }
            }
            Assert.That(typeof(UIPanelAssetMigration).GetMethod("ValidateAssets", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
            Assert.That(typeof(UIPanelAssetMigration).GetMethod("MigrateAssets", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
            Assert.That(typeof(UIPanelAcceptanceBuild).GetMethod("BuildWindows64IL2CPP", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
            Assert.That(UIPanelAssetMigration.BaseRootPath, Is.EqualTo("Assets/YLFramework/Runtime/Core/Resources/YLFramework/Core/Canvas.prefab"));
            Assert.That(UIPanelAssetMigration.TemplatePath, Is.EqualTo("Assets/YLFramework/Prefabs/YLFramework.prefab"));
            Assert.That(AssetDatabase.AssetPathToGUID(UIPanelAssetMigration.BaseRootPath), Is.Not.Empty);
            Assert.That(AssetDatabase.AssetPathToGUID(UIPanelAssetMigration.TemplatePath), Is.Not.Empty);
        }

        [Test]
        public void PanelTrueDefaultsAndExplicitFalseZeroSurviveVariantSaveReloadAndInstantiation()
        {
            string folder = "Assets/YLFramework/Tests/EditMode/Editor/Core/PanelVariant_" + Guid.NewGuid().ToString("N");
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                Assert.That(AssetDatabase.CreateFolder("Assets/YLFramework/Tests/EditMode/Editor/Core", Path.GetFileName(folder)), Is.Not.Empty);
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(UIPanelAssetMigration.ColorPath);
                Assert.That(source, Is.Not.Null);
                var original = (GameObject)PrefabUtility.InstantiatePrefab(source, preview);
                original.SetActive(false);
                var configured = new SerializedObject(original.GetComponent<Panel>());
                var defaults = configured.FindProperty("settings");
                defaults.FindPropertyRelative("layer").intValue = 0;
                defaults.FindPropertyRelative("priority").intValue = 11;
                defaults.FindPropertyRelative("modal").boolValue = true;
                defaults.FindPropertyRelative("participatesInTop").boolValue = true;
                defaults.FindPropertyRelative("cacheOnClose").boolValue = true;
                configured.ApplyModifiedPropertiesWithoutUndo();
                string basePath = folder + "/Base.prefab", variantPath = folder + "/Variant.prefab";
                Assert.That(PrefabUtility.SaveAsPrefabAsset(original, basePath), Is.Not.Null);
                var baseAsset = AssetDatabase.LoadAssetAtPath<GameObject>(basePath);
                var reloaded = new SerializedObject(baseAsset.GetComponent<Panel>()).FindProperty("settings");
                Assert.That(reloaded.FindPropertyRelative("participatesInTop").boolValue && reloaded.FindPropertyRelative("cacheOnClose").boolValue, Is.True);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(baseAsset, preview);
                var changed = new SerializedObject(instance.GetComponent<Panel>());
                var overrides = changed.FindProperty("settings");
                overrides.FindPropertyRelative("priority").intValue = 0;
                foreach (var field in new[] { "modal", "participatesInTop", "cacheOnClose" })
                {
                    overrides.FindPropertyRelative(field).boolValue = false;
                }

                changed.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(PrefabUtility.SaveAsPrefabAsset(instance, variantPath), Is.Not.Null);
                var variant = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath);
                Assert.That(PrefabUtility.GetPrefabAssetType(variant), Is.EqualTo(PrefabAssetType.Variant));
                Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(variant), Is.SameAs(baseAsset));
                var clone = (GameObject)PrefabUtility.InstantiatePrefab(variant, preview);
                var values = new SerializedObject(clone.GetComponent<Panel>()).FindProperty("settings");
                Assert.That(values.FindPropertyRelative("priority").intValue, Is.Zero);
                foreach (var field in new[] { "modal", "participatesInTop", "cacheOnClose" })
                {
                    Assert.That(values.FindPropertyRelative(field).boolValue, Is.False, field);
                }

                var saved = new SerializedObject(variant.GetComponent<Panel>()).FindProperty("settings");
                foreach (var field in new[] { "priority", "modal", "participatesInTop", "cacheOnClose" })
                {
                    Assert.That(saved.FindPropertyRelative(field).prefabOverride, Is.True, field);
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
                if (AssetDatabase.IsValidFolder(folder))
                {
                    AssetDatabase.DeleteAsset(folder);
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MigrationSavePreservesLocalIdsAndRawAdaptationAcrossPrefabConversion(bool convert)
        {
            string folder = "Assets/YLFramework/Tests/EditMode/Editor/Core/MigrationIdentity_" + Guid.NewGuid().ToString("N");
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                AssetDatabase.CreateFolder("Assets/YLFramework/Tests/EditMode/Editor/Core", Path.GetFileName(folder));
                var host = new GameObject("MigrationHost");
                SceneManager.MoveGameObjectToScene(host, preview);
                var canvas = new GameObject("UISystem", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvas.transform.SetParent(host.transform, false);
                var scaler = canvas.GetComponent<CanvasScaler>();
                scaler.referenceResolution = new Vector2(1280, 720);
                scaler.matchWidthOrHeight = 0.25f;
                canvas.AddComponent<UIRootContext>();
                var adaptation = new SerializedObject(canvas.AddComponent<AutomaticAdaptationCanvasScaler>());
                adaptation.FindProperty("referenceResolution").vector2Value = Vector2.zero;
                adaptation.FindProperty("automaticMatch").boolValue = false;
                adaptation.FindProperty("manualMatch").floatValue = 0.37f;
                adaptation.ApplyModifiedPropertiesWithoutUndo();
                string path = folder + "/Host.prefab";
                PrefabUtility.SaveAsPrefabAsset(host, path);
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var identities = asset.GetComponentsInChildren<Transform>(true)
                    .SelectMany(t => new UnityEngine.Object[] { t.gameObject }.Concat(t.GetComponents<Component>()))
                    .ToDictionary(obj =>
                    {
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long id);
                        return id;
                    }, obj => obj.GetType());
                var capture = typeof(UIPanelAssetMigration).GetMethod("CaptureIdentity", BindingFlags.NonPublic | BindingFlags.Static);
                var save = typeof(UIPanelAssetMigration).GetMethod("SaveChecked", BindingFlags.NonPublic | BindingFlags.Static);
                for (int round = 0; round < 2; round++)
                {
                    var before = capture.Invoke(null, new object[] { path });
                    var contents = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        if (convert && round == 0)
                        {
                            PrefabUtility.ConvertToPrefabInstance(contents.transform.Find("UISystem").gameObject,
                                AssetDatabase.LoadAssetAtPath<GameObject>(UIPanelAssetMigration.BaseRootPath), new ConvertToPrefabInstanceSettings
                                {
                                    objectMatchMode = ObjectMatchMode.ByHierarchy,
                                    recordPropertyOverridesOfMatches = true,
                                    componentsNotMatchedBecomesOverride = true,
                                    gameObjectsNotMatchedBecomesOverride = true,
                                    changeRootNameToAssetName = false
                                }, InteractionMode.AutomatedAction);
                        }

                        save.Invoke(null, new[] { contents, path, before });
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(contents);
                    }
                    asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    var actual = asset.GetComponentsInChildren<Transform>(true)
                        .SelectMany(t => new UnityEngine.Object[] { t.gameObject }.Concat(t.GetComponents<Component>()))
                        .ToDictionary(obj =>
                        {
                            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long id);
                            return id;
                        }, obj => obj.GetType());
                    foreach (var pair in identities)
                    {
                        Assert.That(actual.TryGetValue(pair.Key, out var type) && type == pair.Value, Is.True, pair.Key.ToString());
                    }

                    var saved = new SerializedObject(asset.GetComponentInChildren<AutomaticAdaptationCanvasScaler>(true));
                    Assert.That(saved.FindProperty("referenceResolution").vector2Value, Is.EqualTo(Vector2.zero));
                    Assert.That(saved.FindProperty("automaticMatch").boolValue, Is.False);
                    Assert.That(saved.FindProperty("manualMatch").floatValue, Is.EqualTo(0.37f));
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
                if (AssetDatabase.IsValidFolder(folder))
                {
                    AssetDatabase.DeleteAsset(folder);
                }
            }
        }

        [TestCase("Canvas", "CreateCanvas")]
        [TestCase("EventSystem", "CreateEventSystem")]
        public void DefaultFactoriesCreateOwnedInactivePreviewObjectsAndSaveIndependentPrefabs(string name, string factoryName)
        {
            string folderName = "YLCoreEditorTest_" + Guid.NewGuid().ToString("N");
            string directory = "Assets/YLFramework/Tests/EditMode/Editor/Core/" + folderName;
            Scene activeScene = SceneManager.GetActiveScene();
            bool activeWasDirty = activeScene.isDirty;
            int[] originalRoots = activeScene.GetRootGameObjects().Select(go => go.GetInstanceID()).ToArray();
            var externalEventSystem = EventSystem.current;
            Scene preview = default(Scene);
            try
            {
                Assert.That(AssetDatabase.CreateFolder("Assets/YLFramework/Tests/EditMode/Editor/Core", folderName), Is.Not.Empty);
                preview = EditorSceneManager.NewPreviewScene();
                var factory = typeof(ListenerInitOnLoad).GetMethod(factoryName, BindingFlags.NonPublic | BindingFlags.Static);
                var enable = typeof(ListenerInitOnLoad).GetMethod("EnablePrefabAsset", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(factory, Is.Not.Null);
                Assert.That(enable, Is.Not.Null);
                var created = (GameObject)factory.Invoke(null, new object[] { preview });
                Assert.That(created.scene, Is.EqualTo(preview));
                Assert.That(created.activeSelf, Is.False);
                Assert.That(EventSystem.current, Is.SameAs(externalEventSystem));
                string path = directory + "/" + name + ".prefab";
                Assert.That(PrefabUtility.SaveAsPrefabAsset(created, path), Is.Not.Null);
                enable.Invoke(null, new object[] { path });
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.That(prefab, Is.Not.Null);
                Assert.That(prefab.activeSelf, Is.True);
                Assert.That(created.activeSelf, Is.False, "Only the serialized asset may be enabled.");
                Assert.That(AssetDatabase.GetAssetPath(prefab), Is.EqualTo(path));
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.Not.EqualTo(AssetDatabase.AssetPathToGUID(
                    name == "Canvas" ? ListenerInitOnLoad.CanvasPath : ListenerInitOnLoad.EventSystemPath)));
                foreach (var transform in prefab.GetComponentsInChildren<Transform>(true))
                {
                    Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject), Is.Zero);
                    foreach (var component in transform.GetComponents<Component>())
                    {
                        CollectionAssert.Contains(new[] { typeof(UIRootContext).Assembly,
                            typeof(EventSystem).Assembly, typeof(Transform).Assembly, typeof(Canvas).Assembly }, component.GetType().Assembly);
                    }
                }
                if (name == "Canvas")
                {
                    Assert.That(prefab.GetComponents<Component>().Select(c => c.GetType()), Is.EqualTo(new[]
                    {
                        typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster),
                         typeof(AutomaticAdaptationCanvasScaler), typeof(UIRootContext)
                    }));
                    Assert.That(prefab.GetComponent<Canvas>().renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
                    Assert.That(prefab.GetComponent<Canvas>().additionalShaderChannels, Is.EqualTo(
                        AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.TexCoord2 | AdditionalCanvasShaderChannels.TexCoord3));
                    var scaler = prefab.GetComponent<CanvasScaler>();
                    Assert.That(scaler.uiScaleMode, Is.EqualTo(CanvasScaler.ScaleMode.ScaleWithScreenSize));
                    Assert.That(scaler.referenceResolution, Is.EqualTo(new Vector2(1920, 1080)));
                    Assert.That(scaler.matchWidthOrHeight, Is.EqualTo(1));
                    Assert.That(prefab.GetComponent<AutomaticAdaptationCanvasScaler>().GetType().Assembly.GetName().Name,
                         Is.EqualTo("YLFramework.Runtime"));
                    var layers = new SerializedObject(prefab.GetComponent<UIRootContext>()).FindProperty("uiLayers");
                    Assert.That(layers.arraySize, Is.EqualTo(1));
                    Assert.That(layers.GetArrayElementAtIndex(0).FindPropertyRelative("root").objectReferenceValue, Is.SameAs(prefab.transform.Find("Layer0")));
                }
                else
                {
                    Assert.That(prefab.GetComponents<Component>().Select(c => c.GetType()), Is.EqualTo(new[]
                    {
                        typeof(Transform), typeof(EventSystem)
                    }));
                    var eventSystem = prefab.GetComponent<EventSystem>();
                    Assert.That(eventSystem.sendNavigationEvents, Is.True);
                    Assert.That(eventSystem.pixelDragThreshold, Is.EqualTo(10));
                    Assert.That(prefab.GetComponents<BaseInputModule>(), Is.Empty);
                    var bridge = typeof(PanelManager).Assembly.GetType("YLFramework.Core.InputSystemBridge")
                        .GetMethod("EnsureEventSystemInputModule", BindingFlags.Public | BindingFlags.Static);
                    var instanceSystem = created.GetComponent<EventSystem>();
                    bridge.Invoke(null, new object[] { instanceSystem });
                    var input = created.GetComponent<BaseInputModule>();
#if ENABLE_INPUT_SYSTEM
                    Assert.That(input.GetType().FullName, Is.EqualTo("UnityEngine.InputSystem.UI.InputSystemUIInputModule"));
#else
                    Assert.That(input, Is.TypeOf<StandaloneInputModule>());
#endif
                    bridge.Invoke(null, new object[] { instanceSystem });
                    Assert.That(created.GetComponents<BaseInputModule>(), Is.EqualTo(new[] { input }));
                    Assert.That(EventSystem.current, Is.SameAs(externalEventSystem));
                    Assert.That(prefab.GetComponents<BaseInputModule>(), Is.Empty, "Runtime setup must not rewrite the asset.");
                }
            }
            finally
            {
                if (preview.IsValid())
                {
                    EditorSceneManager.ClosePreviewScene(preview);
                }

                if (AssetDatabase.IsValidFolder(directory))
                {
                    AssetDatabase.DeleteAsset(directory);
                }
            }
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(activeScene));
            Assert.That(activeScene.isDirty, Is.EqualTo(activeWasDirty));
            Assert.That(activeScene.GetRootGameObjects().Select(go => go.GetInstanceID()), Is.EqualTo(originalRoots));
            Assert.That(EventSystem.current, Is.SameAs(externalEventSystem));
        }

        [Test]
        public void RepeatedDefaultGenerationDoesNotChangeExistingOwnedResources()
        {
            var snapshots = CaptureExistingDefaults();
            ListenerInitOnLoad.GenerateDefaultResources();
            ListenerInitOnLoad.GenerateDefaultResources();
            AssertDefaultsUnchanged(snapshots);
        }

        [Test]
        public void ActualInitializeOnLoadMethodSchedulesOnlyItsOwnDeferredGenerator()
        {
            var snapshots = CaptureExistingDefaults();
            MethodInfo initializer = typeof(ListenerInitOnLoad).GetMethod("InitOnLoad", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(initializer, Is.Not.Null);
            Assert.That(initializer.IsDefined(typeof(InitializeOnLoadMethodAttribute), false), Is.True);
            EditorApplication.CallbackFunction callback = ListenerInitOnLoad.GenerateDefaultResources;
            Delegate[] before = EditorApplication.delayCall == null ? new Delegate[0] : EditorApplication.delayCall.GetInvocationList();
            bool scheduled = false;
            try
            {
                initializer.Invoke(null, null);
                scheduled = true;
                var after = EditorApplication.delayCall.GetInvocationList();
                Assert.That(after.Count(d => d.Equals(callback)), Is.EqualTo(before.Count(d => d.Equals(callback)) + 1));
                Assert.That(after.Where(d => !d.Equals(callback)), Is.EqualTo(before.Where(d => !d.Equals(callback))));
                // Remove and execute only this newly scheduled method, never the global delayCall delegate.
                EditorApplication.delayCall -= callback;
                scheduled = false;
                callback();
                AssertDefaultsUnchanged(snapshots);
                Assert.That(EditorApplication.delayCall == null ? new Delegate[0] : EditorApplication.delayCall.GetInvocationList(),
                    Is.EqualTo(before));
            }
            finally
            {
                if (scheduled)
                {
                    EditorApplication.delayCall -= callback;
                }
            }
        }

        [TestCase(ListenerInitOnLoad.CanvasPath)]
        [TestCase(ListenerInitOnLoad.EventSystemPath)]
        [TestCase(ListenerInitOnLoad.ResourceRoot)]
        [TestCase("Assets/YLFramework/Runtime/Core/Resources")]
        [TestCase("Assets/YLFramework/Runtime/Core/Resources/YLFramework")]
        [TestCase("Assets/YLFramework/Runtime/Core")]
        public void DeletionProtectionRecognizesOnlyOwnedDefaultsAndNarrowAncestors(string path)
        {
            Assert.That(ListenerFileChange.IsProtectedDefaultResource(path), Is.True);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Assets/Games")]
        [TestCase("Assets/Games/MainModule/Resources/Canvas.prefab")]
        [TestCase("Assets/Resources/Canvas.prefab")]
        [TestCase("Assets/Resources/EventSystem.prefab")]
        [TestCase("Assets/Resources/ExternalUI/Canvas.prefab")]
        [TestCase("Assets/Resources/ExternalUI/EventSystem.prefab")]
        [TestCase("Packages/com.example.ui/Runtime/Resources/Canvas.prefab")]
        [TestCase("Assets/YLFramework/Editor/Help")]
        [TestCase("Assets/YLFramework/Editor/Core")]
        [TestCase("Assets/YLFramework/Runtime/Unrelated")]
        [TestCase("Assets/YLFramework/Runtime")]
        [TestCase("Assets/YLFramework")]
        [TestCase("Assets/YLFramework/Runtime/Core/Resources/YLFramework/CoreExtra")]
        [TestCase("Assets/YLFramework/Runtime/Core/Resources/YLFramework/Core/Unrelated.asset")]
        [TestCase("Assets/YLFramework/Runtime/Core/Resources/YLFramework/Core/Canvas.prefab.backup")]
        public void DeletionCallbackDoesNotBlockGameExternalUIHelpOrUnrelatedAssets(string path)
        {
            Assert.That(ListenerFileChange.IsProtectedDefaultResource(path), Is.False);
            Assert.That(ListenerFileChange.OnWillDeleteAsset(path, RemoveAssetOptions.DeleteAssets),
                Is.EqualTo(AssetDeleteResult.DidNotDelete));
        }

        private static DefaultFileSnapshot[] CaptureExistingDefaults()
        {
            Assert.That(ListenerInitOnLoad.ResourceRoot,
                Is.EqualTo("Assets/YLFramework/Runtime/Core/Resources/YLFramework/Core"));
            var paths = new[]
            {
                ListenerInitOnLoad.CanvasPath, ListenerInitOnLoad.CanvasPath + ".meta",
                ListenerInitOnLoad.EventSystemPath, ListenerInitOnLoad.EventSystemPath + ".meta"
            };
            // Missing-resource generation belongs to coordinator verification, not destructive test setup.
            foreach (string path in paths)
            {
                Assert.That(File.Exists(path), Is.True, "Coordinator must create/import default resources first: " + path);
            }

            return paths.Select(path => new DefaultFileSnapshot
            {
                Path = path,
                Bytes = File.ReadAllBytes(path),
                LastWriteTime = File.GetLastWriteTimeUtc(path)
            }).ToArray();
        }

        private static void AssertDefaultsUnchanged(DefaultFileSnapshot[] snapshots)
        {
            foreach (var snapshot in snapshots)
            {
                Assert.That(File.ReadAllBytes(snapshot.Path), Is.EqualTo(snapshot.Bytes), snapshot.Path);
                Assert.That(File.GetLastWriteTimeUtc(snapshot.Path), Is.EqualTo(snapshot.LastWriteTime), snapshot.Path);
            }
        }

        private sealed class DefaultFileSnapshot
        {
            public string Path;
            public byte[] Bytes;
            public DateTime LastWriteTime;
        }
    }
}
