using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Tests
{
    public class DevelopmentHostBoundaryTests
    {
        private const string ScenePath = "Assets/Scenes/YLFrameworkDev.unity";
        private const string FrameworkPrefabPath = "Assets/YLFramework/Prefabs/YLFramework.prefab";
        private const string GameRoot = "Assets/Games/MainModule";
        private static readonly string[] _allowedPackageNames =
        {
            "com.code-philosophy.hybridclr", "com.ivanmurzak.unity.mcp", "com.ivanmurzak.unity.mcp.animation",
            "com.ivanmurzak.unity.mcp.cinemachine", "com.ivanmurzak.unity.mcp.inputsystem", "com.ivanmurzak.unity.mcp.probuilder",
            "com.ivanmurzak.unity.mcp.tilemap", "com.ivanmurzak.unity.mcp.timeline", "extensions.unity.playerprefsex",
            "com.unity.2d.animation", "com.unity.2d.aseprite", "com.unity.2d.common", "com.unity.2d.pixel-perfect",
            "com.unity.2d.psdimporter", "com.unity.2d.sprite", "com.unity.2d.spriteshape", "com.unity.2d.tilemap",
            "com.unity.2d.tilemap.extras", "com.unity.burst", "com.unity.cinemachine", "com.unity.collections",
            "com.unity.ext.nunit", "com.unity.feature.2d", "com.unity.ide.rider", "com.unity.ide.visualstudio",
            "com.unity.inputsystem", "com.unity.mathematics", "com.unity.nuget.newtonsoft-json", "com.unity.probuilder",
            "com.unity.render-pipelines.core", "com.unity.render-pipelines.universal", "com.unity.render-pipelines.universal-config",
            "com.unity.searcher", "com.unity.settings-manager", "com.unity.shadergraph", "com.unity.splines",
            "com.unity.test-framework", "com.unity.timeline", "com.unity.ugui", "com.unity.visualscripting",
            "com.unity.modules.ai", "com.unity.modules.androidjni", "com.unity.modules.animation", "com.unity.modules.assetbundle",
            "com.unity.modules.audio", "com.unity.modules.cloth", "com.unity.modules.director", "com.unity.modules.imageconversion",
            "com.unity.modules.imgui", "com.unity.modules.jsonserialize", "com.unity.modules.particlesystem", "com.unity.modules.physics",
            "com.unity.modules.physics2d", "com.unity.modules.screencapture", "com.unity.modules.subsystems", "com.unity.modules.terrain",
            "com.unity.modules.terrainphysics", "com.unity.modules.tilemap", "com.unity.modules.ui", "com.unity.modules.uielements",
            "com.unity.modules.umbra", "com.unity.modules.unityanalytics", "com.unity.modules.unitywebrequest",
            "com.unity.modules.unitywebrequestassetbundle", "com.unity.modules.unitywebrequestaudio",
            "com.unity.modules.unitywebrequesttexture", "com.unity.modules.unitywebrequestwww", "com.unity.modules.vehicles",
            "com.unity.modules.video", "com.unity.modules.vr", "com.unity.modules.wind", "com.unity.modules.xr"
        };

        [Test]
        public void DevelopmentHostDeclaresOnlyApprovedPackageDependencies()
        {
            var dependencies = (JObject)JObject.Parse(File.ReadAllText("Packages/manifest.json"))["dependencies"];
            Assert.That(dependencies, Is.Not.Null);
            Assert.That(dependencies.Properties().Select(property => property.Name), Is.SubsetOf(_allowedPackageNames));
            Assert.That((string)dependencies["com.unity.nuget.newtonsoft-json"], Is.EqualTo("3.2.1"));

            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            Assert.That(packages.Select(package => package.name), Is.SubsetOf(_allowedPackageNames));
            var jsonPackage = packages.Single(package => package.name == "com.unity.nuget.newtonsoft-json");
            Assert.That(jsonPackage.version, Is.EqualTo("3.2.1"));
            Assert.That(jsonPackage.isDirectDependency, Is.True);
            Assert.That(Directory.GetDirectories("Packages"), Is.Empty, "The development host has no embedded package overrides.");
        }

        [Test]
        public void DevelopmentHostResourcesAndSettingsHaveExplicitOwners()
        {
            Assert.That(Directory.GetFiles("Assets/Resources", "*", SearchOption.AllDirectories)
                .Select(path => path.Replace('\\', '/')), Is.SubsetOf(new[]
                {
                    "Assets/Resources/DOTweenSettings.asset", "Assets/Resources/DOTweenSettings.asset.meta"
                }));
            var allowedSettings = new[]
            {
                "AudioManager.asset", "BurstAotSettings_StandaloneOSX.json", "BurstAotSettings_StandaloneWindows.json",
                "ClusterInputManager.asset", "CommonBurstAotSettings.json", "DynamicsManager.asset", "EditorBuildSettings.asset",
                "EditorSettings.asset", "GraphicsSettings.asset", "HybridCLRSettings.asset", "InputManager.asset", "MemorySettings.asset",
                "MultiplayerManager.asset", "NavMeshAreas.asset", "NetworkManager.asset", "PackageManagerSettings.asset", "Packages",
                "Physics2DSettings.asset", "PresetManager.asset", "ProjectSettings.asset", "ProjectVersion.txt", "QualitySettings.asset",
                "SceneTemplateSettings.json", "ShaderGraphSettings.asset", "TagManager.asset", "TimelineSettings.asset", "TimeManager.asset",
                "UnityConnectSettings.asset", "URPProjectSettings.asset", "VersionControlSettings.asset", "VFXManager.asset", "XRSettings.asset",
                "YLFramework"
            };
            Assert.That(Directory.GetFileSystemEntries("ProjectSettings").Select(Path.GetFileName), Is.SubsetOf(allowedSettings));

            foreach (var path in AssetDatabase.GetDependencies(ScenePath, true))
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
                Assert.That(path == ScenePath || path.StartsWith("Assets/YLFramework/", StringComparison.Ordinal) ||
                    path == "Resources/unity_builtin_extra" || path == "Library/unity default resources" ||
                    (package != null && new[] { "com.unity.ugui", "com.unity.render-pipelines.core",
                        "com.unity.render-pipelines.universal" }.Contains(package.name)), Is.True, path);
            }
        }

        [Test]
        public void DefaultBuildEntryAndIdentityBelongToFrameworkDevelopmentHost()
        {
            Assert.That(PlayerSettings.companyName, Is.EqualTo("TANGENT LINE"));
            Assert.That(PlayerSettings.productName, Is.EqualTo("YLFrameworkDev"));
            Assert.That(PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Standalone),
                Is.EqualTo("com.tangentline.ylframeworkdev"));
            CollectionAssert.AreEqual(new[] { ScenePath },
                EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path));
        }

        [Test]
        public void DevelopmentHostHasNoMainGameDependencies()
        {
            var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            Assert.That(scene, Is.Not.Null, "The real development scene must exist before inspecting its closure.");
            var dependencies = AssetDatabase.GetDependencies(ScenePath, true);
            CollectionAssert.Contains(dependencies, ScenePath);
            CollectionAssert.Contains(dependencies, FrameworkPrefabPath);
            Assert.That(dependencies.Any(path => path.StartsWith(GameRoot, StringComparison.Ordinal)), Is.False);
            Assert.That(AssetDatabase.IsValidFolder(GameRoot), Is.False);
            Assert.That(Directory.Exists(GameRoot), Is.False);
            Assert.That(File.Exists(GameRoot + ".meta"), Is.False);
            Assert.That(File.Exists("Assets/Scenes/Main.unity"), Is.False);
            Assert.That(File.Exists("Assets/Scenes/Main.unity.meta"), Is.False);
            Assert.That(Directory.Exists("Assets/StreamingAssets/MainModule"), Is.False);
            Assert.That(Directory.Exists("Assets/Atlas(AutoGenerate)/MainModule"), Is.False);
            Assert.That(Directory.Exists("AssetBundles/MainModule"), Is.False);

            foreach (var profile in new[] { AssembliesType.Editor, AssembliesType.Player })
            {
                Assert.That(CompilationPipeline.GetAssemblies(profile)
                    .Any(assembly => assembly.name.StartsWith("Project.Game.MainModule", StringComparison.Ordinal)),
                    Is.False, profile.ToString());
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
                .Where(value => value.GetName().Name.StartsWith("YLFramework", StringComparison.Ordinal)))
            {
                Assert.That(assembly.GetReferencedAssemblies()
                    .Any(reference => reference.Name.StartsWith("Project.Game.", StringComparison.Ordinal)),
                    Is.False, assembly.FullName);
            }
        }

        [Test]
        public void DevelopmentSceneUsesOneBoundFrameworkPrefabAndHasNoMissingScripts()
        {
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath), Is.Not.Null);
            var previous = SceneManager.GetActiveScene();
            var scene = SceneManager.GetSceneByPath(ScenePath);
            var opened = !scene.IsValid() || !scene.isLoaded;
            if (opened)
            {
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            }

            try
            {
                var objects = scene.GetRootGameObjects().SelectMany(root =>
                    root.GetComponentsInChildren<Transform>(true)).Select(transform => transform.gameObject).ToArray();
                var roots = objects.SelectMany(value => value.GetComponents<YLFrameworkRoot>()).ToArray();
                Assert.That(roots, Has.Length.EqualTo(1));
                Assert.That(AssetDatabase.GetAssetPath(
                    PrefabUtility.GetCorrespondingObjectFromSource(roots[0].gameObject)),
                    Is.EqualTo(FrameworkPrefabPath));
                using var rootData = new SerializedObject(roots[0]);
                Assert.That(rootData.FindProperty("panelRoot").objectReferenceValue, Is.Not.Null);
                Assert.That(objects.SelectMany(value => value.GetComponents<Camera>())
                    .Any(camera => camera.enabled && camera.gameObject.activeInHierarchy &&
                        camera.CompareTag("MainCamera")), Is.True);
                foreach (var value in objects)
                {
                    Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(value), Is.Zero, value.name);
                }
            }
            finally
            {
                if (opened)
                {
                    EditorSceneManager.CloseScene(scene, true);
                    if (previous.IsValid() && previous.isLoaded)
                    {
                        SceneManager.SetActiveScene(previous);
                    }
                }
            }
        }

        [Test]
        public void DevelopmentPlayerSettingsHaveNoGameBrandingReferences()
        {
            using var settings = new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            var property = settings.GetIterator();
            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference || !property.objectReferenceValue)
                {
                    continue;
                }

                Assert.That(AssetDatabase.GetAssetPath(property.objectReferenceValue)
                    .StartsWith(GameRoot, StringComparison.Ordinal), Is.False, property.propertyPath);
            }
        }
    }
}
