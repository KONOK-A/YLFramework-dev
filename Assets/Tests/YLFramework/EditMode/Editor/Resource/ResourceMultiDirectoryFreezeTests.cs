using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed class ResourceMultiDirectoryFreezeTests
    {
        [Test]
        public void SettingsAndInputFingerprintIgnoreSourceOrderButDetectUnusedSourceAndPrefixChanges()
        {
            using var fixture = new ResourceReleaseFixture();
            ResourceMultiDirectoryAtlasTests.AddDirectory(fixture, "Other");
            ResourceMultiDirectoryAtlasTests.AddDirectory(fixture, "Replacement");
            var project = fixture.Project;
            project.groups.Clear();
            project.groups.Add(new ResourceGroup { id = "text", selections = { new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(fixture.TextPath) } } });
            var firstPath = fixture.Folder + "/Content";
            var otherPath = fixture.Folder + "/Other";
            ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, (firstPath, ""), (otherPath, "audio"));
            ResourceMultiDirectoryAtlasTests.Save(project);
            var first = Fingerprints(project);
            ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, (otherPath, "audio"), (firstPath, ""));
            ResourceMultiDirectoryAtlasTests.Save(project);
            Assert.That(Fingerprints(project), Is.EqualTo(first), "Display order is not a resource-input change, including the serialized Project asset.");
            ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, (firstPath, ""), (otherPath, "sound"));
            ResourceMultiDirectoryAtlasTests.Save(project);
            Assert.That(Fingerprints(project).Settings, Is.Not.EqualTo(first.Settings), "An unused source prefix is still part of the frozen configuration.");
            Assert.That(Fingerprints(project).Input, Is.Not.EqualTo(first.Input));
            ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, (firstPath, ""));
            ResourceMultiDirectoryAtlasTests.Save(project);
            Assert.That(Fingerprints(project).Settings, Is.Not.EqualTo(first.Settings), "Removing an unused source must invalidate compatibility.");
            ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, (firstPath, ""), (fixture.Folder + "/Replacement", "audio"));
            ResourceMultiDirectoryAtlasTests.Save(project);
            Assert.That(Fingerprints(project).Settings, Is.Not.EqualTo(first.Settings), "Replacing a source GUID must invalidate compatibility.");
            ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project);
            ResourceMultiDirectoryAtlasTests.Save(project);
            Assert.Throws<InvalidOperationException>(() => Fingerprints(project));
        }

        [Test]
        public void ExplicitSaveOfCurrentDirectoriesKeepsFingerprintsWithoutMaskingOtherProjectBytes()
        {
            using var fixture = new ResourceReleaseFixture();
            var project = fixture.Project;
            ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, (fixture.Folder + "/Content", "resources"));
            ResourceMultiDirectoryAtlasTests.Save(project);
            var current = Fingerprints(project);
            var definition = (string)Invoke("CanonicalDefinition", project);
            var directories = JsonUtility.ToJson(project);
            EditorUtility.SetDirty(project);
            ResourceEditorOperations.SaveProject(project);
            Assert.That(JsonUtility.ToJson(project), Is.EqualTo(directories), "Saving is not a directory-model conversion or address rewrite.");
            Assert.That((string)Invoke("CanonicalDefinition", project), Is.EqualTo(definition));
            Assert.That(Fingerprints(project), Is.EqualTo(current));
            project.displayName = "changed non-directory input";
            ResourceMultiDirectoryAtlasTests.Save(project);
            var renamed = Fingerprints(project);
            Assert.That(renamed.Settings, Is.EqualTo(current.Settings));
            Assert.That(renamed.Input, Is.Not.EqualTo(current.Input));
            File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(AssetDatabase.GetAssetPath(project)), "\n# external bytes outside the directory fields\n");
            Assert.That(Fingerprints(project).Input, Is.Not.EqualTo(renamed.Input), "Do not hash only the resident Unity object and miss a disk-only definition edit.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AKeyProviderCannotAddOrRemoveSourceDirectoriesBeforeTheNativeBuild(bool remove)
        {
            using var fixture = new ResourceReleaseFixture();
            ResourceMultiDirectoryAtlasTests.AddDirectory(fixture, "Other");
            var project = fixture.Project;
            var content = fixture.Folder + "/Content";
            var other = fixture.Folder + "/Other";
            ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, remove ? new[] { (content, ""), (other, "audio") } : new[] { (content, "") });
            project.contentEncryption = ResourceContentEncryption.Aes256GcmHkdfSegmentedV1;
            project.contentKeyId = "multi-freeze-test";
            ResourceMultiDirectoryAtlasTests.Save(project);
            var invoked = false;
            var provider = new MutatingKeys(() =>
            {
                ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, remove ? new[] { (content, "") } : new[] { (content, ""), (other, "audio") });
                ResourceMultiDirectoryAtlasTests.Save(project);
            });
            var error = Assert.Catch(() => Execute(project, provider, (path, builds, options, target) =>
            {
                invoked = true;
                fixture.ReleaseRoot = Path.GetDirectoryName(path);
                return null;
            }));
            Assert.That(error, Is.InstanceOf<InvalidOperationException>());
            Assert.That(provider.Copies, Is.EqualTo(1));
            Assert.That(invoked, Is.False, "Do not combine a key acquired for one source set with a later saved source set.");
        }

        [Test]
        public void AKeyProviderMayReorderTheSameSourceSetWithoutChangingBuildSemantics()
        {
            using var fixture = new ResourceReleaseFixture();
            ResourceMultiDirectoryAtlasTests.AddDirectory(fixture, "Other");
            var project = fixture.Project;
            var content = fixture.Folder + "/Content";
            var other = fixture.Folder + "/Other";
            ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, (content, ""), (other, "audio"));
            project.contentEncryption = ResourceContentEncryption.Aes256GcmHkdfSegmentedV1;
            project.contentKeyId = "multi-freeze-test";
            ResourceMultiDirectoryAtlasTests.Save(project);
            var sentinel = new InvalidOperationException("owned pipeline checkpoint");
            var invoked = false;
            var provider = new MutatingKeys(() =>
            {
                ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, (other, "audio"), (content, ""));
                ResourceMultiDirectoryAtlasTests.Save(project);
            });
            var error = Assert.Catch(() => Execute(project, provider, (path, builds, options, target) =>
            {
                invoked = true;
                fixture.ReleaseRoot = Path.GetDirectoryName(path);
                throw sentinel;
            }));
            Assert.That(invoked, Is.True);
            Assert.That(error, Is.SameAs(sentinel));
        }

        [TestCase("build")]
        [TestCase("publish")]
        [TestCase("recipe")]
        public void FormalOperationsRejectAProviderChangingDirectoriesDuringTheFirstPreflightKeyCopy(string entry)
        {
            using var fixture = new ResourceReleaseFixture();
            ResourceMultiDirectoryAtlasTests.AddDirectory(fixture, "Other");
            var project = fixture.Project;
            var content = fixture.Folder + "/Content";
            var other = fixture.Folder + "/Other";
            ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, (content, ""));
            project.contentEncryption = ResourceContentEncryption.Aes256GcmHkdfSegmentedV1;
            project.contentKeyId = "owned-preflight-review";
            ResourceMultiDirectoryAtlasTests.Save(project);
            var calls = 0;
            var provider = new MutatingKeys(() =>
            {
                if (++calls != 1)
                {
                    throw new InvalidOperationException("Owned second-key checkpoint; native build must not start.");
                }

                ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, (content, ""), (other, "extra"));
                ResourceMultiDirectoryAtlasTests.Save(project);
            });
            ResourceEditorBuildReport report;
            if (entry == "recipe")
            {
                var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
                recipe.project = project;
                recipe.target = EditorUserBuildSettings.activeBuildTarget;
                recipe.appContentCompatibilityId = "fixture-v1";
                recipe.action = ResourceBuildAction.BuildOnly;
                AssetDatabase.CreateAsset(recipe, fixture.Folder + "/OwnedRecipe.asset");
                report = ResourceEditorOperations.ExecuteRecipe(recipe, provider: provider);
            }
            else
            {
                report = entry == "publish" ? ResourceEditorOperations.BuildAndPublish(project, "fixture-v1", provider: provider) :
                ResourceEditorOperations.BuildOnly(project, "fixture-v1", provider: provider);
            }

            Assert.That(report.Build, Is.Null);
            Assert.That(report.BuildError, Is.InstanceOf<InvalidOperationException>());
            Assert.That(calls, Is.EqualTo(1), "The first preflight must reject its changed source set before a second key copy or native build.");
            Assert.That(project.sourceDirectories.Count, Is.EqualTo(2), "Do not undo the callback's explicitly saved configuration.");
        }

        [Test]
        public void FormalPreflightAcceptsAProviderOnlyReorderingTheSameDirectorySet()
        {
            using var fixture = new ResourceReleaseFixture();
            ResourceMultiDirectoryAtlasTests.AddDirectory(fixture, "Other");
            var project = fixture.Project;
            var content = fixture.Folder + "/Content";
            var other = fixture.Folder + "/Other";
            ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, (content, ""), (other, "extra"));
            project.contentEncryption = ResourceContentEncryption.Aes256GcmHkdfSegmentedV1;
            project.contentKeyId = "owned-preflight-review";
            ResourceMultiDirectoryAtlasTests.Save(project);
            var keys = new MutatingKeys(() =>
            {
                ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, (other, "extra"), (content, ""));
                ResourceMultiDirectoryAtlasTests.Save(project);
            });
            var result = ResourceEditorOperations.InspectPreflight(project, "fixture-v1", keys);
            Assert.That(result.State, Is.EqualTo(ResourcePreflightState.Complete), result.Error?.ToString());
            Assert.That(keys.Copies, Is.EqualTo(1));
        }

        [TestCase("add")]
        [TestCase("remove")]
        [TestCase("replace")]
        [TestCase("reorder")]
        public void PostBuildValidationDetectsSourceSetChangesButAcceptsTheSameSetInAnotherOrder(string change)
        {
            using var fixture = new ResourceReleaseFixture();
            ResourceMultiDirectoryAtlasTests.AddDirectory(fixture, "Other");
            ResourceMultiDirectoryAtlasTests.AddDirectory(fixture, "Replacement");
            var project = fixture.Project;
            var content = fixture.Folder + "/Content";
            var other = fixture.Folder + "/Other";
            project.groups.Clear();
            project.groups.Add(new ResourceGroup { id = "text", selections = { new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(fixture.TextPath) } } });
            ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, change == "add" ? new[] { (content, "") } : new[] { (content, ""), (other, "audio") });
            ResourceMultiDirectoryAtlasTests.Save(project);
            var invoked = false;
            ResourceBuildResult result = null;
            Exception error = null;
            try
            {
                result = (ResourceBuildResult)Execute(project, null, (path, builds, options, target) =>
                {
                    invoked = true;
                    fixture.ReleaseRoot = Path.GetDirectoryName(path);
                    var manifest = BuildPipeline.BuildAssetBundles(path, builds, options, target);
                    var current = fixture.Project;
                    var directories = change switch
                    {
                        "remove" => new[] { (content, "") },
                        "replace" => new[] { (content, ""), (fixture.Folder + "/Replacement", "audio") },
                        "reorder" => new[] { (other, "audio"), (content, "") },
                        _ => new[] { (content, ""), (other, "audio") }
                    };
                    ResourceMultiDirectoryAtlasTests.ConfigureDirectories(current, directories);
                    ResourceMultiDirectoryAtlasTests.Save(current);
                    return manifest;
                });
            }
            catch (Exception failure)
            {
                error = failure;
            }
            Assert.That(invoked, Is.True, "The source edit must occur after a real native build, not fail at an earlier gate.");
            if (change == "reorder")
            {
                Assert.That(error, Is.Null, error?.ToString());
                Assert.That(result, Is.Not.Null);
            }
            else
            {
                Assert.That(error, Is.TypeOf<InvalidOperationException>());
                Assert.That((bool)JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(fixture.ReleaseRoot, ".ylr-workspace.json"))))["Complete"], Is.False);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SavedSourceSetChangesDuringAtlasImportAreRejectedAndOwnedWritesAreRolledBack(bool duringOutput)
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = ResourceAutomaticAtlasTests.Configure(fixture);
            ResourceMultiDirectoryAtlasTests.AddDirectory(fixture, "Other");
            var project = fixture.Project;
            ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, (fixture.Folder + "/Content", ""));
            ResourceMultiDirectoryAtlasTests.Save(project);
            if (duringOutput)
            {
                ResourceAtlasGenerator.Generate(project);
            }

            var preview = ResourceAtlasGenerator.Preview(project);
            var output = preview.Changes.Single();
            configuration.rules[0].padding = 8;
            ResourceMultiDirectoryAtlasTests.Save(configuration);
            var before = ResourceReleaseFixture.Snapshot(preview.DirectoryPath);
            var triggered = false;
            MultiDirectoryAtlasImportProbe.Imported = paths =>
            {
                if (triggered || !paths.Contains(duringOutput ? output.Path : fixture.Folder + "/Content/Sprites.png"))
                {
                    return;
                }

                if (duringOutput && ((SpriteAtlasImporter)AssetImporter.GetAtPath(output.Path)).packingSettings.padding != 8)
                {
                    return;
                }

                triggered = true;
                ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, (fixture.Folder + "/Content", ""), (fixture.Folder + "/Other", "extra"));
                ResourceMultiDirectoryAtlasTests.Save(project);
            };
            try
            {
                Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.Generate(project));
            }
            finally
            {
                MultiDirectoryAtlasImportProbe.Imported = null;
            }
            Assert.That(triggered, Is.True);
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(preview.DirectoryPath));
            Assert.That(EditorJsonUtility.ToJson(project), Does.Contain(AssetDatabase.AssetPathToGUID(fixture.Folder + "/Other")), "Rollback must preserve the callback's saved Project edit.");
        }

        [Test]
        public void SavedDirectoryReorderingDuringAtlasImportDoesNotChangeGenerationInputs()
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = ResourceAutomaticAtlasTests.Configure(fixture);
            ResourceMultiDirectoryAtlasTests.AddDirectory(fixture, "Other");
            var project = fixture.Project;
            var content = fixture.Folder + "/Content";
            var other = fixture.Folder + "/Other";
            ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, (content, ""), (other, "extra"));
            ResourceMultiDirectoryAtlasTests.Save(project);
            var triggered = false;
            MultiDirectoryAtlasImportProbe.Imported = paths =>
            {
                if (triggered || !paths.Contains(content + "/Sprites.png"))
                {
                    return;
                }

                triggered = true;
                ResourceMultiDirectoryAtlasTests.ConfigureDirectories(project, (other, "extra"), (content, ""));
                ResourceMultiDirectoryAtlasTests.Save(project);
            };
            try
            {
                ResourceAtlasGenerator.Generate(project);
                Assert.That(ResourceAtlasGenerator.Preview(project).NeedsGeneration, Is.False);
            }
            finally
            {
                MultiDirectoryAtlasImportProbe.Imported = null;
            }
            Assert.That(triggered, Is.True);
            Assert.That(configuration.outputs.Count, Is.EqualTo(1));
        }

        [Test]
        public void FailedRegistrationDoesNotRestoreProjectBytesOverASavedExternalEdit()
        {
            using var fixture = new ResourceReleaseFixture();
            var project = fixture.Project;
            var projectPath = AssetDatabase.GetAssetPath(project);
            var path = fixture.Folder + "/Content/GeneratedAtlases/AutomaticAtlases.asset";
            byte[] externalProject = null;
            MultiDirectoryAtlasImportProbe.Imported = paths =>
            {
                if (externalProject != null || !paths.Contains(path) || !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")))
                {
                    return;
                }

                project.displayName = "external edit must survive";
                ResourceMultiDirectoryAtlasTests.Save(project);
                externalProject = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(projectPath));
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"), Regex.Replace(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")), "guid: [0-9a-fA-F]{32}", "guid: " + Guid.NewGuid().ToString("N")));
            };
            try
            {
                Assert.Catch(() => ResourceAtlasGenerator.CreateConfiguration(project, fixture.Folder + "/Content", "GeneratedAtlases"));
            }
            finally
            {
                MultiDirectoryAtlasImportProbe.Imported = null;
            }
            Assert.That(externalProject, Is.Not.Null);
            CollectionAssert.AreEqual(externalProject, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(projectPath)));
            Assert.That(fixture.Project.displayName, Is.EqualTo("external edit must survive"));
        }

        private static (string Settings, string Input) Fingerprints(ResourceProject project)
        {
            var map = ResourceBuildMap.Create(project);
            var settings = (string)Invoke("Settings", map, project, EditorUserBuildSettings.activeBuildTarget, 0L);
            return (settings, (string)Invoke("Fingerprint", map, settings, 0L));
        }

        private static object Invoke(string name, params object[] arguments)
        {
            var method = typeof(ResourceBundleBuilder).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            try
            {
                return method.Invoke(null, arguments);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static object Execute(ResourceProject project, IResourceContentKeyProvider provider,
            Func<string, AssetBundleBuild[], BuildAssetBundleOptions, BuildTarget, AssetBundleManifest> pipeline) =>
            Invoke("ExecuteWithContentKey", project, EditorUserBuildSettings.activeBuildTarget, false, CancellationToken.None, pipeline, provider);

        private sealed class MutatingKeys : IResourceContentKeyProvider
        {
            private readonly Action _mutate;
            internal int Copies;
            internal MutatingKeys(Action mutate) => this._mutate = mutate;
            public bool TryCopyKey(string projectId, string keyId, byte[] destination)
            {
                Copies++;
                _mutate();
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = (byte)(i + 1);
                }

                return true;
            }
        }
    }

    internal sealed class MultiDirectoryAtlasImportProbe : AssetPostprocessor
    {
        internal static Action<string[]> Imported;
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] oldPaths) => Imported?.Invoke(imported);
    }
}
