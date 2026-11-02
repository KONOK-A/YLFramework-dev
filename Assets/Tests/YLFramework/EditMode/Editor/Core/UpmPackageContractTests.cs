using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Common;
using YLFramework.Editor.Core;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace YLFramework.Core.Tests
{
    [Category("YL.UPM")]
    public sealed class UpmPackageContractTests
    {
        [Test]
        public void MainAssemblyHasTheInstalledPreviewIdentityAndNoDevelopmentProject()
        {
            var package = PackageInfo.FindForAssembly(typeof(YLFrameworkRoot).Assembly);
            Assert.That(package, Is.Not.Null);
            Assert.That(package.name, Is.EqualTo("com.kano.ylframework"));
            Assert.That(package.version, Is.EqualTo("0.1.0-preview.1"));
            Assert.That(package.assetPath, Is.EqualTo("Packages/com.kano.ylframework"));
            Assert.That(FrameworkPackagePaths.FullPath(typeof(YLFrameworkRoot).Assembly, "package.json"), Is.EqualTo(Path.GetFullPath(Path.Combine(package.resolvedPath, "package.json"))));
            foreach (var name in new[] { "Assets", "Packages", "ProjectSettings", "Tools", "Tests", "Integrations", ".kilo", ".kilocode" })
            {
                Assert.That(Directory.Exists(Path.Combine(package.resolvedPath, name)), Is.False, name);
            }
        }

        [TestCase("Runtime/Core/Resources/YLFramework/Core/Canvas.prefab", "YLFramework/Core/Canvas")]
        [TestCase("Runtime/Core/Resources/YLFramework/Core/EventSystem.prefab", "YLFramework/Core/EventSystem")]
        [TestCase("Runtime/Systems/Action/Resources/ActionKit/ScreenTransitionCanvas.prefab", "ActionKit/ScreenTransitionCanvas")]
        public void RequiredResourcesKeepTheirAddressesOutsideOptInSamples(string path, string key)
        {
            var assetPath = FrameworkPackagePaths.AssetPath(typeof(YLFrameworkRoot).Assembly, path);
            Assert.That(File.Exists(FrameworkPackagePaths.FullPath(typeof(YLFrameworkRoot).Assembly, path)), Is.True);
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(assetPath), Is.Not.Null);
            Assert.That(Resources.Load<GameObject>(key), Is.SameAs(AssetDatabase.LoadAssetAtPath<GameObject>(assetPath)));
        }

        [TestCase("../outside")]
        [TestCase("Editor/../../outside")]
        [TestCase("/outside")]
        [TestCase("C:\\outside")]
        public void PackageRelativePathsRejectEscapes(string path)
        {
            Assert.Throws<ArgumentException>(() => FrameworkPackagePaths.FullPath(typeof(YLFrameworkRoot).Assembly, path));
            Assert.Throws<ArgumentException>(() => FrameworkPackagePaths.AssetPath(typeof(YLFrameworkRoot).Assembly, path));
        }

        [Test]
        public void UserOutputsAndAuthorOnlyWritersAreNotInstalledPackageContent()
        {
            Assert.That(CreateModuleCommand.OutputRoot, Is.EqualTo("Assets/YLFrameworkData/Generated/Modules"));
            Assert.That(ScriptableObjectExtensions.GeneratedAssetRoot, Is.EqualTo("Assets/YLFrameworkData/ScriptableObjects"));
            var editor = typeof(CreateModuleCommand).Assembly;
            Assert.That(editor.GetType("YLFramework.Editor.Core.ListenerInitOnLoad"), Is.Null);
            Assert.That(editor.GetType("YLFramework.Editor.Core.ListenerFileChange"), Is.Null);
            Assert.That(typeof(UIPanelAssetMigration).GetMethod("MigrateAssets"), Is.Null);
            Assert.That(File.Exists(FrameworkPackagePaths.FullPath(editor, "Documentation~/README.md")), Is.True);
        }

        [Test]
        public void ScriptableObjectGenerationRejectsAnInstalledPackageEvenWhenItIsLocallyWritable()
        {
            var folder = FrameworkPackagePaths.AssetPath(typeof(YLFrameworkRoot).Assembly, "Prefabs");
            var instance = ScriptableObject.CreateInstance<global::YLFramework.Editor.Resource.ResourceProject>();
            try
            {
                Assert.Throws<ArgumentException>(() => instance.CreateAsset(folder));
                Assert.That(AssetDatabase.Contains(instance), Is.False);
            }
            finally
            {
                if (instance && !AssetDatabase.Contains(instance))
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AnalyzerConnectionResolvesVirtualPackageFilesToTheirActualInstallation(bool independentBusinessRoot)
        {
            var owner = typeof(CreateModuleCommand).Assembly;
            var provider = owner.GetType("YLFramework.Editor.Core.Analyzers.AnalyzerRuleSettingsProvider", true);
            var resolver = provider.GetMethod("FullPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.That(resolver, Is.Not.Null);
            var path = FrameworkPackagePaths.AssetPath(owner, "Runtime/Core/Analyzers/YLFramework.Analyzers.dll");
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var actual = (string)resolver.Invoke(null, new object[] { independentBusinessRoot ? Path.Combine(project, "BusinessScope") : project, path });
            Assert.That(actual, Is.EqualTo(FrameworkPackagePaths.FullPath(owner, "Runtime/Core/Analyzers/YLFramework.Analyzers.dll")));
            Assert.That(File.Exists(actual), Is.True);
        }

        [TestCase("Packages/manifest.json")]
        [TestCase("Packages/packages-lock.json")]
        public void ProjectPackageDeclarationsAreNotMistakenForInstalledPackageAssets(string path)
        {
            Assert.That(global::YLFramework.Testing.PackageTestPaths.Physical(path), Is.EqualTo(path));
            Assert.That(File.Exists(path), Is.True);
        }

        [Test]
        public void ResourceStartupOnlyCompilesWhenTheSampleWasExplicitlyPrepared()
        {
#if YL_TEST_RESOURCE_STARTUP_SAMPLE
            Assert.That(Type.GetType("YLFramework.Samples.Resource.ResourceStartupExample, YLFramework.ResourceStartup.Sample"), Is.Not.Null);
#else
            Assert.That(Type.GetType("YLFramework.Samples.Resource.ResourceStartupExample, YLFramework.ResourceStartup.Sample"), Is.Null);
#endif
        }
    }
}
