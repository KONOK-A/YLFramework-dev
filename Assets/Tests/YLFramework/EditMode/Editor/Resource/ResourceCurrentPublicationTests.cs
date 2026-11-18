using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using YLFramework.Testing;
using YLFramework.Testing.EditorFixtures;
using YLFramework.Tests.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceCurrentPublicationTests
    {
        private const string Source = "Assets/Tests/YLFramework/Fixtures/";

        [Test]
        [Category("YL.UPM")]
        public void ExplicitAcceptanceProbeIsAbsentFromOrdinaryPlayerCompilation()
        {
            const string probeAssembly = "YLFramework.Tests.ResourceCurrent.Player";
            var editor = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.Editor);
            Assert.That(editor.Count(assembly => assembly.name == probeAssembly), Is.EqualTo(1));
            var player = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.PlayerWithoutTestAssemblies);
            Assert.That(player.Any(assembly => assembly.name == probeAssembly), Is.False,
                "An ordinary Player must not compile an acceptance probe without its explicit build symbol.");
        }

        [Test]
        [Category("YL.UPM")]
        public void FrameworkPrefabsAndScenesDoNotContainResourceBootstrapComponents()
        {
            var paths = new[]
            {
                "Packages/com.kano.ylframework/Runtime/Core/Resources/YLFramework/Core/Canvas.prefab",
                "Packages/com.kano.ylframework/Runtime/Core/Resources/YLFramework/Core/EventSystem.prefab",
                Source + "Core/YLCoreSmokeModule/YLCoreSmokeScene.unity",
                Source + "Core/YLCoreResourceModule/YLCoreSmokeScene.unity"
            };
            foreach (var path in paths)
            {
                Assert.That(File.ReadAllText(PackageTestPaths.Physical(path)), Does.Not.Contain("ResourceStartup").And.Not.Contain("ResourceBootstrap"), path);
                foreach (string guid in AssetDatabase.GetDependencies(path).Select(AssetDatabase.AssetPathToGUID))
                {
                    var dependency = AssetDatabase.GUIDToAssetPath(guid);
                    if (dependency.EndsWith(".cs", StringComparison.Ordinal))
                        Assert.That(Path.GetFileNameWithoutExtension(dependency), Does.Not.Contain("ResourceStartup").And.Not.Contain("ResourceBootstrap"), path);
                }
            }
            var methods = typeof(ResourceSystem).Assembly.GetTypes().Where(type => type.Namespace?.StartsWith("YLFramework.Resource", StringComparison.Ordinal) == true)
                .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
            Assert.That(methods.Where(method => method.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>() != null)
                .All(method => method.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>().loadType == RuntimeInitializeLoadType.SubsystemRegistration), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        [Category("YL.UPM.Generators")]
        public void OfficialSettingsRoundTripPreservesEntryLabelSourceAndMetadataBytes(bool controlledStartup)
        {
            AssertOwnedHost();
            ResourceAddressablesTestContent.BuildForConsumers();
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
            settings.DisableCatalogUpdateOnStartup = controlledStartup;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            var path = AssetDatabase.GetAssetPath(settings);
            var guid = AssetDatabase.AssetPathToGUID(path);
            var bytes = File.ReadAllBytes(path);
            var meta = File.ReadAllBytes(path + ".meta");
            var entries = Entries(settings);
            var labels = settings.GetLabels().OrderBy(value => value, StringComparer.Ordinal).ToArray();
            Assert.That(entries, Is.Not.Empty);
            Resources.UnloadAsset(settings);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            settings = AssetDatabase.LoadAssetAtPath<AddressableAssetSettings>(path);
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            Assert.That(settings.DisableCatalogUpdateOnStartup, Is.EqualTo(controlledStartup));
            Assert.That(Entries(settings), Is.EqualTo(entries));
            Assert.That(settings.GetLabels().OrderBy(value => value, StringComparer.Ordinal), Is.EqualTo(labels));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
            Assert.That(File.ReadAllBytes(path + ".meta"), Is.EqualTo(meta));
        }

        [Test]
        [Category("YL.UPM.Generators")]
        public void ResourceAuthorWindowUsesOfficialSettingsWithoutPrivateBuildRecipeFields()
        {
            AssertOwnedHost();
            var fields = typeof(ResServiceEditorWindow).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(fields.Single(field => field.Name == "settings").FieldType, Is.EqualTo(typeof(AddressableAssetSettings)));
            Assert.That(fields.Any(field => field.FieldType == typeof(ResourceProject) || field.FieldType == typeof(ResourceBuildRecipe)), Is.False);
            ResourceAddressablesTestContent.BuildForConsumers();
            var window = ScriptableObject.CreateInstance<ResServiceEditorWindow>();
            var copy = ScriptableObject.CreateInstance<ResServiceEditorWindow>();
            try
            {
                using var serialized = new SerializedObject(window);
                foreach (var field in fields.Where(field => field.GetCustomAttribute<SerializeField>() != null))
                {
                    var property = serialized.FindProperty(field.Name);
                    Assert.That(property, Is.Not.Null, field.Name);
                    if (field.FieldType == typeof(AddressableAssetSettings))
                        property.objectReferenceValue = AddressableAssetSettingsDefaultObject.GetSettings(false);
                    else if (field.FieldType == typeof(string))
                        property.stringValue = "current-" + field.Name;
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var json = EditorJsonUtility.ToJson(window);
                EditorJsonUtility.FromJsonOverwrite(json, copy);
                Assert.That(EditorJsonUtility.ToJson(copy), Is.EqualTo(json));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy);
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        [Category("YL.UPM.Generators")]
        public void ResourceCurrentPlayerProbeHasNoTestOnlyPlayerDependencies()
        {
            var definition = JObject.Parse(File.ReadAllText(Source + "ResourceCurrent/YLFramework.Tests.ResourceCurrent.Player.asmdef"));
            Assert.That(((JArray)definition["references"]).Values<string>(), Is.EquivalentTo(new[] { "YLFramework.Runtime", "Unity.Addressables" }));
            Assert.That(((JArray)definition["defineConstraints"]).Values<string>(), Is.EqualTo(new[] { "UNITY_EDITOR || YL_RESOURCE_CURRENT_ACCEPTANCE" }));
            Assert.That((bool)definition["autoReferenced"], Is.False);
            Assert.That(((JArray)definition["precompiledReferences"]).Count, Is.Zero);
            Assert.That(((JArray)definition["versionDefines"]).Count, Is.Zero);
        }

        [UnityTest]
        [Category("YL.Special.ResourceCurrent")]
        [Category("YL.NativeGraphics")]
        public IEnumerator OfficialContentLoadsInTwoIndependentOrdinaryPlayersWithIsolatedOwnership()
        {
            AssertOwnedHost();
            ResourceAddressablesTestContent.BuildForConsumers();
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
            settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
            var name = "__YLCurrentOfficial-" + Guid.NewGuid().ToString("N");
            var root = "Assets/" + name;
            var folderGuid = AssetDatabase.CreateFolder("Assets", name);
            Assert.That(folderGuid, Is.Not.Empty);
            AddressableAssetGroup group = null;
            try
            {
                group = settings.CreateGroup(name, false, false, true, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                schema.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
                schema.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
                schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
                foreach (var value in new[] { "first", "second" })
                {
                    var path = root + "/" + value + ".txt";
                    File.WriteAllText(path, "current resource " + value);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), group).SetAddress("resource-current/" + value);
                }
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                var evidence = Environment.GetEnvironmentVariable("YL_TEST_OUTPUT_ROOT");
                Assert.That(Directory.Exists(evidence), Is.True);
                var report = ResourceEditorOperations.BuildContent(settings, Path.Combine(evidence, "current-official-" + Guid.NewGuid().ToString("N") + ".bin"));
                var hash = StandaloneFixturePlayer.ContentHash(report);
                StandaloneFixturePlayer.BuildAndRun(evidence, root, "YLFramework.Runtime", report, _ =>
                {
                    var type = Type.GetType("YLFramework.Tests.ResourceBuild.ResourceCurrentPlayerProbe, YLFramework.Tests.ResourceCurrent.Player", true);
                    var probe = new GameObject("OfficialCurrentPlayer").AddComponent(type);
                    type.GetField("firstAddress").SetValue(probe, "resource-current/first");
                    type.GetField("secondAddress").SetValue(probe, "resource-current/second");
                    type.GetField("contentHash").SetValue(probe, hash);
                }, new[] { "YL_RESOURCE_CURRENT_ACCEPTANCE" });
                for (var run = 1; run <= 2; run++)
                {
                    var result = JObject.Parse(File.ReadAllText(Path.Combine(evidence, "StandalonePlayer-" + run + ".json")));
                    Assert.That((bool)result["resourceLoaded"], Is.True);
                    Assert.That((bool)result["independentHeldAfterScopeClose"], Is.True);
                    Assert.That((string)result["probeAssembly"], Is.EqualTo("YLFramework.Tests.ResourceCurrent.Player"));
                    foreach (var field in new[] { "assemblies", "probeReferences" })
                    {
                        var names = result[field].Values<string>().ToArray();
                        Assert.That(names, Is.Not.Empty, field);
                        Assert.That(names.Any(IsEditorOrTestDependency), Is.False, field);
                    }
                }

                var player = File.ReadAllText(Path.Combine(evidence, "standalone-player-location.txt"));
                var shipped = Directory.GetFiles(Path.Combine(player, "P_Data/Managed"), "*.dll").Select(Path.GetFileNameWithoutExtension).ToArray();
                Assert.That(shipped, Does.Contain("YLFramework.Tests.ResourceCurrent.Player"));
                Assert.That(shipped.Any(IsEditorOrTestDependency), Is.False, "The executable must not ship Editor, NUnit or TestRunner assemblies.");
            }
            finally
            {
                Assert.That(AssetDatabase.AssetPathToGUID(root), Is.EqualTo(folderGuid), "Do not remove a replaced output folder.");
                if (group)
                {
                    Assert.That(group.Settings, Is.SameAs(settings));
                    settings.RemoveGroup(group);
                    AssetDatabase.SaveAssetIfDirty(settings);
                }
                Assert.That(AssetDatabase.DeleteAsset(root), Is.True);
            }
            yield break;
        }

        private static bool IsEditorOrTestDependency(string name) => name.StartsWith("UnityEditor", StringComparison.Ordinal) ||
            name.IndexOf("TestRunner", StringComparison.OrdinalIgnoreCase) >= 0 || name.StartsWith("nunit", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("YLFramework.Tests.Fixtures", StringComparison.Ordinal) || name.StartsWith("YLFramework.Core.Tests", StringComparison.Ordinal);

        private static string[] Entries(AddressableAssetSettings settings) => settings.groups.Where(group => group)
            .SelectMany(group => group.entries).Select(entry => entry.guid + ":" + entry.address + ":" + string.Join(",", entry.labels.OrderBy(value => value, StringComparer.Ordinal)))
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();

        private static void AssertOwnedHost()
        {
            var owner = JObject.Parse(File.ReadAllText(".yl-owner.json"));
            Assert.That((string)owner["kind"], Is.EqualTo("Host"));
            Assert.That(Application.isBatchMode, Is.True, "Never build or change scenes in a user's Editor.");
        }
    }
}
