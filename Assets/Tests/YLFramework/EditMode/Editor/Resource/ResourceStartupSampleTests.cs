using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AddressableAssets;
using YLFramework.Editor.Common;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Resource.Addressables.Samples")]
    public sealed class ResourceStartupSampleTests
    {
        private static string SampleRoot => FrameworkPackagePaths.FullPath(typeof(ResourceEditorOperations).Assembly, "Samples~/ResourceStartup");

        [Test]
        public void SamplesAndCoreGuidanceHaveNoRetiredResourceExecutionContracts()
        {
            var paths = Directory.GetFiles(SampleRoot, "*.cs", SearchOption.AllDirectories)
                .Append(FrameworkPackagePaths.FullPath(typeof(ResourceEditorOperations).Assembly, "Editor/Core/ModuleGeneration/CreateModuleCommand.cs"))
                .Append(FrameworkPackagePaths.FullPath(typeof(ResourceEditorOperations).Assembly, "Editor/Core/CoreHelpProvider.cs"));
            const string Retired = @"\b(ResourceLoadMode|ResourceProjectMount|ResourceLocalSnapshot|ResourceEditorSimulation|ResourceBuildRecipe|ResourceProject|ResourcePublisher|ResourceBuiltInDeployer|ResourceRelease|SceneRecord|IResourceContentKeyProvider)\b|ResourceConfiguration\.Create|ExecuteRecipe\s*\(|BuildAndPublish\s*\(|bindCore\s*:";
            foreach (var path in paths)
            {
                Assert.That(File.Exists(path), Is.True, path);
                Assert.That(Regex.IsMatch(File.ReadAllText(path), Retired), Is.False, path);
            }
        }

        [Test]
        public void EditorSampleCommandsNeverInitializeNativeResources()
        {
            foreach (var path in Directory.GetFiles(Path.Combine(SampleRoot, "Editor"), "*.cs", SearchOption.AllDirectories))
            {
                Assert.That(Regex.IsMatch(File.ReadAllText(path), @"\b(InitializeAsync|StartAsync|StartExampleAsync|BeginBundles|CreateConfiguration)\s*\("), Is.False, path);
            }
        }

        [Test]
        [Category("YL.Resource.Addressables.Samples.Imported")]
        public void ImportedSampleDeclaresOfficialSerializedReferencesAndAttachingDoesNotStartAService()
        {
            var type = ImportedType("YLFramework.Samples.Resource.ResourceStartupExample");
            var current = ResourceSystem.Current;
            var owner = new GameObject("ResourceStartupSampleContract");
            try
            {
                var sample = owner.AddComponent(type);
                foreach (var name in new[] { "message", "data", "prefab", "scene", "preview", "audioCue" })
                {
                    var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                    Assert.That(field, Is.Not.Null, name);
                    Assert.That(typeof(AssetReference).IsAssignableFrom(field.FieldType), Is.True, name);
                }
                Assert.That(type.GetProperty("Startup").GetValue(sample), Is.Null);
                Assert.That(ResourceSystem.Current, Is.SameAs(current));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        [Category("YL.Resource.Addressables.Samples.Imported")]
        public void ImportedSampleExplicitStartInEditModeFailsWithoutCreatingNativeResponsibilities()
        {
            var type = ImportedType("YLFramework.Samples.Resource.ResourceStartupExample");
            var current = ResourceSystem.Current;
            var owner = new GameObject("ResourceStartupSampleEditModeGuard");
            try
            {
                var sample = owner.AddComponent(type);
                var method = type.GetMethod("StartExampleAsync", new[] { typeof(CancellationToken) });
                Assert.That(method, Is.Not.Null);
                var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(sample, new object[] { CancellationToken.None }));
                Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(type.GetProperty("Startup").GetValue(sample), Is.Null);
                Assert.That(ResourceSystem.Current, Is.SameAs(current));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        private static Type ImportedType(string name)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name, false)).SingleOrDefault(value => value != null);
            Assert.That(type, Is.Not.Null, "Import the complete ResourceStartup sample into the isolated test host before running the Imported category.");
            return type;
        }
    }
}
