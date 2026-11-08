using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
        [TestCase(typeof(PanelConfig))]
        [TestCase(typeof(YLFramework.Timing.TimerObject))]
        public void CurrentProductFieldsDoNotKeepRetiredUnityReaderNames(Type type)
        {
            var readers = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .SelectMany(field => field.GetCustomAttributes<UnityEngine.Serialization.FormerlySerializedAsAttribute>());
            Assert.That(readers, Is.Empty, "Current formal inputs must not depend on a second field-name reader.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CurrentPanelConfigPreservesExplicitBooleansZerosAndReferences(bool enabled)
        {
            string folder = CreatePanelFixtureFolder();
            try
            {
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
                string path = folder + "/Current.prefab";
                Assert.That(AssetDatabase.CopyAsset(UIPanelAssetValidator.ColorPath, path), Is.True);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                string guid = AssetDatabase.AssetPathToGUID(path);
                var metadata = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"));
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.That(prefab.GetComponent<Panel>(), Is.Not.Null, "The current script reference must survive.");
                var selector = prefab.GetComponentInChildren<ColorSelector>(true);
                Assert.That(selector, Is.Not.Null);
                var cursor = new SerializedObject(selector).FindProperty("outerCursor").objectReferenceValue as Transform;
                Assert.That(cursor && cursor.IsChildOf(prefab.transform), Is.True,
                    "The local fileID reference must resolve inside the independently copied prefab.");
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(cursor, out string cursorGuid, out long cursorId), Is.True);
                Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(prefab), Is.Zero);
                var serialized = new SerializedObject(prefab.GetComponent<Panel>());
                var settings = serialized.FindProperty("settings");
                foreach (var value in values)
                {
                    var property = settings.FindPropertyRelative(value.Item2);
                    Assert.That(property, Is.Not.Null, "Current field missing: " + value.Item2);
                    if (property.propertyType == SerializedPropertyType.Boolean)
                    {
                        property.boolValue = value.Item3 != 0;
                    }
                    else
                    {
                        property.intValue = value.Item3;
                    }
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(PrefabUtility.SavePrefabAsset(prefab), Is.Not.Null);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
                Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")), Is.EqualTo(metadata));
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                serialized = new SerializedObject(prefab.GetComponent<Panel>());
                cursor = new SerializedObject(prefab.GetComponentInChildren<ColorSelector>(true)).FindProperty("outerCursor").objectReferenceValue as Transform;
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(cursor, out string afterGuid, out long afterId), Is.True);
                Assert.That(afterGuid, Is.EqualTo(cursorGuid));
                Assert.That(afterId, Is.EqualTo(cursorId));
                var saved = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                foreach (var value in values)
                {
                    var property = serialized.FindProperty("settings").FindPropertyRelative(value.Item2);
                    Assert.That(property.propertyType == SerializedPropertyType.Boolean ? (property.boolValue ? 1 : 0) : property.intValue, Is.EqualTo(value.Item3));
                    StringAssert.Contains("    " + value.Item2 + ": " + value.Item3, saved);
                    Assert.That(Regex.IsMatch(saved, @"(?m)^    " + value.Item1 + @": "), Is.False);
                }
            }
            finally
            {
                Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
            }
        }

        private static string CreatePanelFixtureFolder()
        {
            string name = "CurrentPanelFields_" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets/Tests/YLFramework/EditMode/Editor/Core", name), Is.Not.Empty);
            return "Assets/Tests/YLFramework/EditMode/Editor/Core/" + name;
        }

        [Test]
        public void CurrentTimerEventsRetainPersistentTargetsArgumentsAndClockPolicy()
        {
            string folder = CreatePanelFixtureFolder();
            try
            {
                string path = folder + "/Timer.prefab";
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(UIPanelAssetValidator.TipsPath)));
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

                string guid = AssetDatabase.AssetPathToGUID(path);
                var metadata = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"));
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
                StringAssert.Contains("\n  onCompleted:", File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
                StringAssert.Contains("\n  onAborted:", File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
                Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")), Is.EqualTo(metadata));
            }
            finally
            {
                Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
            }
        }

        [Test]
        public void PanelValidationAndIl2CppBuildAreExplicitEntrypointsWithoutMigrationWriters()
        {
            foreach (var type in new[] { typeof(UIPanelAssetValidator), typeof(UIPanelAcceptanceBuild) })
            {
                Assert.That(type.GetCustomAttributes(typeof(InitializeOnLoadAttribute), false), Is.Empty);
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    Assert.That(method.GetCustomAttributes(typeof(InitializeOnLoadMethodAttribute), false), Is.Empty, method.Name);
                }
            }
            Assert.That(typeof(UIPanelAssetValidator).GetMethod("ValidateAssets", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
            Assert.That(typeof(UIPanelAssetValidator).GetMethod("MigrateAssets", BindingFlags.Public | BindingFlags.Static), Is.Null);
            Assert.That(typeof(UIPanelAssetValidator).GetMethod("MigrateTestFixtures", BindingFlags.Public | BindingFlags.Static), Is.Null);
            Assert.That(typeof(UIPanelAssetValidator).Assembly.GetName().Name, Is.EqualTo("YLFramework.Editor"));
            Assert.That(typeof(ListenerInitOnLoad).Assembly.GetType("YLFramework.Editor.Core.UIPanelAssetAuthoring"), Is.Null);
            Assert.That(typeof(UIPanelAcceptanceBuild).GetMethod("BuildWindows64IL2CPP", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
            Assert.That(UIPanelAssetValidator.BaseRootPath, Is.EqualTo("Packages/com.kano.ylframework/Runtime/Core/Resources/YLFramework/Core/Canvas.prefab"));
            Assert.That(UIPanelAssetValidator.TemplatePath, Is.EqualTo("Packages/com.kano.ylframework/Prefabs/YLFramework.prefab"));
            Assert.That(AssetDatabase.AssetPathToGUID(UIPanelAssetValidator.BaseRootPath), Is.Not.Empty);
            Assert.That(AssetDatabase.AssetPathToGUID(UIPanelAssetValidator.TemplatePath), Is.Not.Empty);
        }

        [Test]
        public void PanelTrueDefaultsAndExplicitFalseZeroSurviveVariantSaveReloadAndInstantiation()
        {
            string folder = "Assets/Tests/YLFramework/EditMode/Editor/Core/PanelVariant_" + Guid.NewGuid().ToString("N");
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                Assert.That(AssetDatabase.CreateFolder("Assets/Tests/YLFramework/EditMode/Editor/Core", Path.GetFileName(folder)), Is.Not.Empty);
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(UIPanelAssetValidator.ColorPath);
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

        [TestCase("Canvas", "CreateCanvas")]
        [TestCase("EventSystem", "CreateEventSystem")]
        public void DefaultFactoriesCreateOwnedInactivePreviewObjectsAndSaveIndependentPrefabs(string name, string factoryName)
        {
            string folderName = "YLCoreEditorTest_" + Guid.NewGuid().ToString("N");
            string directory = "Assets/Tests/YLFramework/EditMode/Editor/Core/" + folderName;
            Scene activeScene = SceneManager.GetActiveScene();
            bool activeWasDirty = activeScene.isDirty;
            int[] originalRoots = activeScene.GetRootGameObjects().Select(go => go.GetInstanceID()).ToArray();
            var externalEventSystem = EventSystem.current;
            Scene preview = default(Scene);
            try
            {
                Assert.That(AssetDatabase.CreateFolder("Assets/Tests/YLFramework/EditMode/Editor/Core", folderName), Is.Not.Empty);
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
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(YLFrameworkRoot).Assembly);
            Assert.That(package, Is.Not.Null);
            for (int round = 0; round < 2; round++)
            {
                if (package.source == UnityEditor.PackageManager.PackageSource.Local)
                {
                    ListenerInitOnLoad.GenerateDefaultResources();
                }
                else
                {
                    Assert.Throws<InvalidOperationException>(ListenerInitOnLoad.GenerateDefaultResources);
                }
            }
            AssertDefaultsUnchanged(snapshots);
        }

        [Test]
        public void ShippedDefaultsHaveNoAutomaticWriterAndAuthoringDoesNotScheduleGeneration()
        {
            var snapshots = CaptureExistingDefaults();
            var before = EditorApplication.delayCall?.GetInvocationList() ?? Array.Empty<Delegate>();
            var activeScene = SceneManager.GetActiveScene();
            bool wasDirty = activeScene.isDirty;
            var roots = activeScene.GetRootGameObjects().Select(root => root.GetInstanceID()).ToArray();
            var input = EventSystem.current;
            var authorTypes = new[] { typeof(ListenerInitOnLoad), typeof(ListenerFileChange),
                typeof(YLFramework.Editor.Actions.ScreenTransitionResourceCreator) };
            var product = typeof(UIPanelAssetValidator).Assembly;
            foreach (var type in new[] { typeof(UIPanelAssetValidator) }.Concat(authorTypes))
            {
                Assert.That(type.IsDefined(typeof(InitializeOnLoadAttribute), false), Is.False, type.FullName);
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    Assert.That(method.IsDefined(typeof(InitializeOnLoadMethodAttribute), false), Is.False, type.FullName + "." + method.Name);
                }
            }

            foreach (var type in authorTypes)
            {
                Assert.That(product.GetType(type.FullName), Is.Null, "A package consumer must not import author-only writers: " + type.FullName);
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            }
            StringAssert.Contains("assets validated read-only", UIPanelAssetValidator.ValidateAssets());
            Assert.That(typeof(ListenerInitOnLoad).GetMethod("GenerateDefaultResources").IsDefined(typeof(MenuItem), false), Is.True);
            Assert.That(EditorApplication.delayCall?.GetInvocationList() ?? Array.Empty<Delegate>(), Is.EqualTo(before));
            AssertDefaultsUnchanged(snapshots);
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(activeScene));
            Assert.That(activeScene.isDirty, Is.EqualTo(wasDirty));
            Assert.That(activeScene.GetRootGameObjects().Select(root => root.GetInstanceID()), Is.EqualTo(roots));
            Assert.That(EventSystem.current, Is.SameAs(input));
        }

        [Test]
        public void AuthoringGateRejectsMissingAndNonLocalInstalledPackagesBeforeWriting()
        {
            var snapshots = CaptureExistingDefaults();
            var gate = typeof(ListenerInitOnLoad).GetMethod("RequireLocalSourcePackage", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(gate, Is.Not.Null, "All explicit author writers must share the local-source guard.");
            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            Assert.That(packages.Any(package => package.source != UnityEditor.PackageManager.PackageSource.Local), Is.True);
            foreach (var package in packages.Append(null))
            {
                if (package != null && package.source == UnityEditor.PackageManager.PackageSource.Local)
                {
                    Assert.DoesNotThrow(() => gate.Invoke(null, new object[] { package }));
                }
                else
                {
                    var error = Assert.Throws<TargetInvocationException>(() => gate.Invoke(null, new object[] { package }));
                    Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
                }
            }
            AssertDefaultsUnchanged(snapshots);
        }

        [TestCase(ListenerInitOnLoad.CanvasPath)]
        [TestCase(ListenerInitOnLoad.EventSystemPath)]
        [TestCase(ListenerInitOnLoad.ResourceRoot)]
        [TestCase("Packages/com.kano.ylframework/Runtime/Core/Resources")]
        [TestCase("Packages/com.kano.ylframework/Runtime/Core/Resources/YLFramework")]
        [TestCase("Packages/com.kano.ylframework/Runtime/Core")]
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
        [TestCase("Packages/com.kano.ylframework/Editor/Help")]
        [TestCase("Packages/com.kano.ylframework/Editor/Core")]
        [TestCase("Packages/com.kano.ylframework/Runtime/Unrelated")]
        [TestCase("Packages/com.kano.ylframework/Runtime")]
        [TestCase("Assets/YLFramework")]
        [TestCase("Packages/com.kano.ylframework/Runtime/Core/Resources/YLFramework/CoreExtra")]
        [TestCase("Packages/com.kano.ylframework/Runtime/Core/Resources/YLFramework/Core/Unrelated.asset")]
        [TestCase("Packages/com.kano.ylframework/Runtime/Core/Resources/YLFramework/Core/Canvas.prefab.backup")]
        public void DeletionCallbackDoesNotBlockGameExternalUIHelpOrUnrelatedAssets(string path)
        {
            Assert.That(ListenerFileChange.IsProtectedDefaultResource(path), Is.False);
            Assert.That(ListenerFileChange.OnWillDeleteAsset(path, RemoveAssetOptions.DeleteAssets),
                Is.EqualTo(AssetDeleteResult.DidNotDelete));
        }

        private static DefaultFileSnapshot[] CaptureExistingDefaults()
        {
            Assert.That(ListenerInitOnLoad.ResourceRoot,
                Is.EqualTo("Packages/com.kano.ylframework/Runtime/Core/Resources/YLFramework/Core"));
            var paths = new[]
            {
                ListenerInitOnLoad.CanvasPath, ListenerInitOnLoad.CanvasPath + ".meta",
                ListenerInitOnLoad.EventSystemPath, ListenerInitOnLoad.EventSystemPath + ".meta"
            };
            // Missing-resource generation belongs to coordinator verification, not destructive test setup.
            foreach (string path in paths)
            {
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.True, "Coordinator must create/import default resources first: " + path);
            }

            return paths.Select(path => new DefaultFileSnapshot
            {
                Path = path,
                Bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)),
                LastWriteTime = File.GetLastWriteTimeUtc(global::YLFramework.Testing.PackageTestPaths.Physical(path))
            }).ToArray();
        }

        private static void AssertDefaultsUnchanged(DefaultFileSnapshot[] snapshots)
        {
            foreach (var snapshot in snapshots)
            {
                Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(snapshot.Path)), Is.EqualTo(snapshot.Bytes), snapshot.Path);
                Assert.That(File.GetLastWriteTimeUtc(global::YLFramework.Testing.PackageTestPaths.Physical(snapshot.Path)), Is.EqualTo(snapshot.LastWriteTime), snapshot.Path);
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
