using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.DataAssets;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using Object = UnityEngine.Object;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.DataAssets
{
    [Category("YL.Special.Session")]
    public class DataAssetSessionAndPublicationTests
    {
        private static readonly string _domainToken = Guid.NewGuid().ToString("N");
        private ScriptableObjectRegistration _owner;
        private ResourceSystem _resource;
        private string _folder;
        private ScriptableObjectConfigs _root;

        private sealed class Operation : ResourceSourceOperation
        {
            internal void Finish(Object asset) => Complete(asset, null);
        }

        private sealed class Source : IResourceSource
        {
            internal ScriptableObjectConfigs Root;
            internal int Releases;
            public ResourceSourceId Id => new ResourceSourceId("data-assets-two-play");
            public bool SupportsSynchronousLoad => true;
            public void Validate(ResourceCatalogEntry entry)
            {
            }
            public ResourceSourceResult Load(ResourceCatalogEntry entry, Type type) => new ResourceSourceResult(Root);
            public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type)
            {
                var operation = new Operation();
                operation.Finish(Root);
                return operation;
            }
            public void Release(Object asset) => Releases++;
        }

        [UnitySetUp]
        public IEnumerator RequireOwnedSession()
        {
            Assert.That(Application.isBatchMode, Is.True);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput())), Is.True);
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.True);
            Assert.That(EditorSettings.enterPlayModeOptions, Is.EqualTo(EnterPlayModeOptions.DisableDomainReload));
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero);
            yield return null;
        }

        [UnityTest]
        public IEnumerator TwoActualPlaySessionsReleaseDataAssetsWithoutOtherFeaturePackages()
        {
            string token = _domainToken;
            for (int round = 1; round <= 2; round++)
            {
                yield return new EnterPlayMode(false);
                Assert.That(_domainToken, Is.EqualTo(token), "The managed domain must actually survive both entries.");
                Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero);
                _root = ScriptableObject.CreateInstance<ScriptableObjectConfigs>();
                _root.name = "DataAssets-TwoPlay-" + round;
                var source = new Source { Root = _root };
                var address = new ResourceAddress("data-assets-two-play", "root");
                using (var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[]
                {
                    new ResourceCatalog("data-assets-two-play", new[] { new ResourceCatalogEntry("root", address, new ResourceLocation(source.Id, "root", typeof(ScriptableObjectConfigs))) })
                }, new IResourceSource[] { source })))
                {
                    Assert.That(initialized.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), initialized.Error?.ToString());
                    _resource = initialized.TakeResult();
                }
                _owner = ScriptableObjectManager.Load(_resource, address);
                Assert.That(ScriptableObjectManager.RegistrationCount, Is.EqualTo(1));
                Assert.That(_owner.Config, Is.SameAs(_root));
                yield return new ExitPlayMode();
                Assert.That(_owner.IsDisposed, Is.True);
                Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero);
                Assert.That(source.Releases, Is.EqualTo(1));
                Assert.That(_resource.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
                Assert.That(ResourceSystem.Current, Is.Null);
                _owner = null;
                _resource = null;
                if (_root)
                {
                    Object.DestroyImmediate(_root);
                }
                _root = null;
            }
        }

        [UnityTest]
        public IEnumerator FormalRebuiltPublicationLoadsTheMovedRootAndRowThroughResourceLeases()
        {
            _folder = "Assets/__YLDataAssembly_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", _folder.Substring("Assets/".Length));
            AssetDatabase.CreateFolder(_folder, "Content");
            const string Before = "Assets/Tests/YLFramework/Fixtures/NamespaceMigration/AssemblySplitV1/Content/Domain/";
            Assert.That(AssetDatabase.CopyAsset(Before + "ScriptableObjectConfigs.asset", _folder + "/Content/Root.asset"), Is.True);
            Assert.That(AssetDatabase.CopyAsset(Before + "ScriptableObjectData.asset", _folder + "/Content/Row.asset"), Is.True);
            _root = AssetDatabase.LoadAssetAtPath<ScriptableObjectConfigs>(_folder + "/Content/Root.asset");
            _root.name = "MigratedDataRoot";
            var row = AssetDatabase.LoadAssetAtPath<ScriptableObjectData>(_folder + "/Content/Row.asset");
            int rowId = row.id;
            string rowName = row.configName, rowDescription = row.description;
            Assert.That(rowName, Is.EqualTo("before-assembly-dataassets"));
            row.parent = _root;
            _root.configs.Clear();
            _root.configs.Add(row);
            var project = ScriptableObject.CreateInstance<ResourceProject>();
            project.projectId = "data-move-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(_folder + "/Content") });
            var group = new ResourceGroup { id = "Data", packing = ResourcePacking.Together };
            group.selections.Add(new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(_folder + "/Content/Root.asset"), address = "root" });
            group.selections.Add(new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(_folder + "/Content/Row.asset"), address = "row" });
            project.groups.Add(group);
            AssetDatabase.CreateAsset(project, _folder + "/Project.asset");
            EditorUtility.SetDirty(_root);
            EditorUtility.SetDirty(row);
            EditorUtility.SetDirty(project);
            AssetDatabase.SaveAssetIfDirty(_root);
            AssetDatabase.SaveAssetIfDirty(row);
            AssetDatabase.SaveAssetIfDirty(project);
            var built = ResourceBundleBuilder.Build(project, BuildTarget.StandaloneWindows64);
            var release = ResourcePublisher.Publish(built, "data-assets-assembly-v1");
            var moved = release.Catalog.Assets.Where(asset => asset.AssetType.StartsWith("YLFramework.DataAssets.", StringComparison.Ordinal)).ToArray();
            Assert.That(moved, Has.Length.EqualTo(2));
            Assert.That(moved.Select(asset => asset.AssetAssembly), Is.All.EqualTo("YLFramework.DataAssets"));
            BuildAndRunPlayer(release, _folder, rowId, rowName, rowDescription);
            var snapshot = ResourceLocalSnapshot.Open(Path.GetFullPath(release.DirectoryPath), project.projectId, release.Catalog.CatalogDigest,
                release.Catalog.Platform, release.Catalog.AppContentCompatibilityId);
            var catalog = snapshot.CreateCatalog(new[] { typeof(ScriptableObjectConfigs), typeof(ScriptableObjectData) });
            string projectId = project.projectId;
            yield return new EnterPlayMode(false);
            using (var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { catalog })))
            {
                Assert.That(initialized.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), initialized.Error?.ToString());
                _resource = initialized.TakeResult();
            }
            _owner = ScriptableObjectManager.Load(_resource, new ResourceAddress(projectId, "root"));
            Assert.That(_owner.Config.GetName(rowId), Is.EqualTo(rowName));
            Assert.That(_owner.Config.GetScriptableObjectData(rowId).parent, Is.SameAs(_owner.Config));
            using (var child = _resource.Acquire<ScriptableObjectData>(new ResourceAddress(projectId, "row")))
            {
                _owner.Dispose();
                _owner = null;
                Assert.That(_resource.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(1));
                Assert.That(child.Asset, Is.Not.Null);
                Assert.That(child.Asset.description, Is.EqualTo(rowDescription), "The actual bundle must remain alive after its root registration returns.");
            }
            Assert.That(_resource.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            using (var shutdown = _resource.ShutdownAsync())
            {
                yield return shutdown;
                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
            }
            _resource = null;
            yield return new ExitPlayMode();
        }

        private static void BuildAndRunPlayer(ResourceRelease release, string assetRoot, int rowId, string rowName, string rowDescription)
        {
            string output = global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            string projectPath = Directory.GetParent(Application.dataPath).FullName;
            string playerRoot = Path.Combine(projectPath, "P", Guid.NewGuid().ToString("N").Substring(0, 8));
            Assert.That(Directory.Exists(playerRoot), Is.False, "Never adopt a previous Player output.");
            for (var directory = Directory.GetParent(playerRoot); directory != null; directory = directory.Parent)
            {
                if (directory.Exists)
                {
                    Assert.That((directory.Attributes & FileAttributes.ReparsePoint) == 0, Is.True);
                }
            }
            Directory.CreateDirectory(playerRoot);
            string locator = Path.Combine(output, "data-assets-player-location.txt");
            Assert.That(File.Exists(locator), Is.False);
            File.WriteAllText(locator, playerRoot);
            var deployed = ResourceBuiltInDeployer.Deploy(ResourceBuiltInDeployer.Preflight(release));
            Assert.That(deployed.Snapshot.Document.CatalogDigest, Is.EqualTo(release.Catalog.CatalogDigest));
            const string PreparedScene = "Assets/YLTestingProfile/Empty.unity";
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(PreparedScene)), Is.True);
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                var empty = SceneManager.GetSceneAt(index);
                Assert.That(empty.isDirty, Is.False, "Do not replace a dirty scene, even in an owned host.");
                Assert.That(empty.rootCount, Is.Zero, "Only the verified empty UTF/preparation scenes may be replaced.");
                Assert.That(string.IsNullOrEmpty(empty.path) || empty.path == PreparedScene, Is.True);
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            string executable = Path.Combine(playerRoot, "DataAssetsPlayer.exe");
            try
            {
                var type = Type.GetType("YLFramework.Tests.DataAssets.DataAssetPlayerProbe, YLFramework.DataAssets.Acceptance", true);
                var host = new GameObject("DataAssetsStandaloneProbe");
                SceneManager.MoveGameObjectToScene(host, scene);
                var probe = host.AddComponent(type);
                foreach (var value in new[] { ("projectId", release.Catalog.ProjectId),
                    ("catalogDigest", release.Catalog.CatalogDigest), ("compatibility", release.Catalog.AppContentCompatibilityId),
                    ("rowName", rowName), ("rowDescription", rowDescription) })
                {
                    type.GetField(value.Item1).SetValue(probe, value.Item2);
                }
                type.GetField("rowId").SetValue(probe, rowId);
                string path = assetRoot + "/Player.unity";
                Assert.That(EditorSceneManager.SaveScene(scene, path), Is.True);
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
                var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { path },
                    locationPathName = executable,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development
                });
                Assert.That(build.summary.result, Is.EqualTo(UnityEditor.Build.Reporting.BuildResult.Succeeded));
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, backend);
                Assert.That(EditorSceneManager.OpenScene(PreparedScene, OpenSceneMode.Single).path, Is.EqualTo(PreparedScene));
            }
            string managed = Path.Combine(playerRoot, "DataAssetsPlayer_Data/Managed");
            Assert.That(File.Exists(Path.Combine(managed, "YLFramework.DataAssets.dll")), Is.True);
            Assert.That(File.Exists(Path.Combine(managed, "YLFramework.DataAssets.Editor.dll")), Is.False);
            for (int run = 1; run <= 2; run++)
            {
                string resultPath = Path.Combine(playerRoot, "result-" + run + ".json");
                Assert.That(File.Exists(resultPath), Is.False);
                var start = new System.Diagnostics.ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = playerRoot,
                    Arguments = "-batchmode -nographics -ylFeatureResult \"" + resultPath + "\" -logFile \"" + Path.Combine(playerRoot, "player-" + run + ".log") + "\""
                };
                using var process = System.Diagnostics.Process.Start(start);
                if (!process.WaitForExit(90000))
                {
                    process.Kill();
                    process.WaitForExit(10000);
                    Assert.Fail("The actual DataAssets Player timed out; no successful result is inferred.");
                }
                Assert.That(process.ExitCode, Is.Zero);
                var result = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(resultPath));
                Assert.That((string)result["status"], Is.EqualTo("Passed"), result.ToString());
                Assert.That((int)result["pid"], Is.EqualTo(process.Id));
                Assert.That((bool)result["isEditor"], Is.False);
                Assert.That((string)result["platform"], Is.EqualTo("WindowsPlayer"));
                Assert.That((string)result["unityVersion"], Is.EqualTo(Application.unityVersion));
                Assert.That((string)result["runtimeAssembly"], Is.EqualTo("YLFramework.DataAssets"));
                Assert.That((string)result["catalogDigest"], Is.EqualTo(release.Catalog.CatalogDigest));
                Assert.That((bool)result["childSurvivedRootRelease"], Is.True);
                Assert.That((int)result["leaseCount"] + (int)result["recordCount"], Is.Zero);
                File.Copy(resultPath, Path.Combine(output, "DataAssetsPlayer-" + run + ".json"));
            }
        }

        [UnityTearDown]
        public IEnumerator CleanupOwnedInputs()
        {
            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }
            _owner?.Dispose();
            _owner = null;
            if (_resource != null && _resource.State == ResourceSystemState.Ready)
            {
                using var shutdown = _resource.ShutdownAsync();
                yield return shutdown;
                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            }
            _resource = null;
            Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero);
            if (_folder != null && TestContext.CurrentContext.Result.Outcome.Status != NUnit.Framework.Interfaces.TestStatus.Failed)
            {
                Assert.That(AssetDatabase.DeleteAsset(_folder), Is.True);
            }
            else if (_folder == null && _root)
            {
                Object.DestroyImmediate(_root);
            }
            _root = null;
            _folder = null;
        }
    }
}
