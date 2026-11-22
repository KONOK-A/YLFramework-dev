using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.DataAssets;
using YLFramework.Editor.DataAssets;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using YLFramework.Testing.EditorFixtures;

namespace YLFramework.Tests.DataAssets
{
    [Category("YL.Special.Session")]
    public class DataAssetSessionAndPublicationTests
    {
        private static readonly string _domainToken = Guid.NewGuid().ToString("N");
        private ScriptableObjectAsset<ScriptableObjectConfigs> _owner;
        private ResourceStartup _startup;
        private ResourceSystem _resource;
        private bool _ownsHost;
        private string _output;
        private string _settingsGuid;
        private string _folder;
        private string _folderGuid;
        private string _rowFolder;
        private string _rowFolderGuid;
        private string _groupPath;
        private string _groupGuid;
        private string[] _schemaGuids;
        private Dictionary<string, string> _entries;
        private ScriptableObjectConfigs _root;

        [Serializable]
        private sealed class HostOwner
        {
            public int schemaVersion;
            public string purpose;
            public string kind;
            public string path;
        }

        [UnitySetUp]
        public IEnumerator RequireOwnedSession()
        {
            _ownsHost = false;
            Assert.That(Application.isBatchMode && !EditorApplication.isPlayingOrWillChangePlaymode, Is.True);
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var host = JsonUtility.FromJson<HostOwner>(File.ReadAllText(Path.Combine(project, ".yl-owner.json")));
            Assert.That(host, Is.Not.Null);
            Assert.That(host.schemaVersion, Is.EqualTo(1));
            Assert.That(host.purpose, Is.EqualTo("YLTesting.Directory"));
            Assert.That(host.kind, Is.EqualTo("Host"));
            Assert.That(Path.GetFullPath(host.path), Is.EqualTo(project).IgnoreCase);
            for (var directory = new DirectoryInfo(project); directory != null; directory = directory.Parent)
                Assert.That((directory.Attributes & FileAttributes.ReparsePoint) == 0, Is.True);
            _output = global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            Assert.That(Directory.Exists(_output), Is.True);
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.True);
            Assert.That(EditorSettings.enterPlayModeOptions, Is.EqualTo(EnterPlayModeOptions.DisableDomainReload));
            Assert.That(EditorUserBuildSettings.activeBuildTarget, Is.EqualTo(BuildTarget.StandaloneWindows64));
            Assert.That(BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64), Is.True);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero);
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
            Assert.That(settings, Is.Not.Null, "Prepare official default Addressables settings; never adopt or replace unknown settings here.");
            Assert.That(settings.ActivePlayModeDataBuilder, Is.TypeOf<BuildScriptPackedPlayMode>(), "The actual Play sessions must use the existing formal build, not AssetDatabase simulation.");
            Assert.That(settings.BuildAddressablesWithPlayerBuild, Is.EqualTo(AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer));
            ResourceEditorOperations.ValidateBuild(settings);
            _settingsGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(settings));
            Assert.That(_settingsGuid, Is.Not.Empty);
            _ownsHost = true;
            yield return null;
        }

        [UnityTest]
        public IEnumerator TwoActualPlaySessionsReleaseDataAssetsWithoutOtherFeaturePackages()
        {
            CreateOwnedFolder();
            string projectId = "data-two-play-" + Guid.NewGuid().ToString("N");
            var paths = new string[2];
            for (int round = 1; round <= 2; round++)
            {
                var root = ScriptableObject.CreateInstance<ScriptableObjectConfigs>();
                root.name = "DataAssets-TwoPlay-" + round;
                paths[round - 1] = _folder + "/Content/" + root.name + ".asset";
                AssetDatabase.CreateAsset(root, paths[round - 1]);
                AssetDatabase.SaveAssetIfDirty(root);
            }
            RegisterEntries(projectId, paths.Select((path, index) => (path, projectId + "/round" + (index + 1))).ToArray());
            var built = ResourceEditorOperations.BuildContent(Settings(), Path.Combine(_output, "data-assets-two-play-content-state.bin"));
            string contentHash = StandaloneFixturePlayer.ContentHash(built);
            string token = _domainToken;
            ResourceSystem previous = null;
            for (int round = 1; round <= 2; round++)
            {
                yield return new EnterPlayMode(false);
                Assert.That(_domainToken, Is.EqualTo(token), "The managed domain must actually survive both entries.");
                Assert.That(EditorSettings.enterPlayModeOptionsEnabled && EditorSettings.enterPlayModeOptions == EnterPlayModeOptions.DisableDomainReload, Is.True);
                Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero);
                yield return StartResources();
                Assert.That(_resource, Is.Not.SameAs(previous));
                _owner = ScriptableObjectManager.LoadAsync(_resource, new ResourceAddress(projectId, "round" + round));
                yield return Wait(() => _owner.IsDone, "session root loading");
                Assert.That(_owner.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), _owner.Error?.ToString());
                Assert.That(ScriptableObjectManager.RegistrationCount, Is.EqualTo(1));
                Assert.That(_owner.Asset.name, Is.EqualTo("DataAssets-TwoPlay-" + round));
                Assert.That(_owner.Asset.GetType(), Is.EqualTo(typeof(ScriptableObjectConfigs)));
                Assert.That(ScriptableObjectManager.GetConfig(_owner.Asset.name), Is.SameAs(_owner.Asset));
                Assert.That(_resource.GetDiagnostics().LeaseCount, Is.EqualTo(1));
                yield return new ExitPlayMode();
                Assert.That(_domainToken, Is.EqualTo(token));
                Assert.That(_owner.IsDisposed, Is.True);
                Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero);
                yield return Wait(() => IsQuiescent(_resource), "exited session resource drain");
                AssertQuiescent(_resource);
                Assert.That(_resource.State, Is.EqualTo(ResourceSystemState.Invalidated));
                Assert.That(ResourceSystem.Current, Is.Null);
                previous = _resource;
                _owner = null;
                _resource = null;
                _startup = null;
            }
            Assert.That(StandaloneFixturePlayer.ContentHash(built), Is.EqualTo(contentHash), "Actual Play sessions must not rewrite the formal content.");
        }

        [UnityTest]
        public IEnumerator CurrentAuthorGeneratedRootAndRowPublishWithIndependentRuntimeLeases()
        {
            CreateOwnedFolder();
            var rootPath = _folder + "/Content/Root.asset";
            _root = ScriptableObject.CreateInstance<ScriptableObjectConfigs>();
            AssetDatabase.CreateAsset(_root, rootPath);
            _root.name = "CurrentDataRoot";
            ScriptableObjectConfigsController.SetDataType(_root,
                AssetDatabase.LoadAssetAtPath<MonoScript>("Packages/com.kano.ylframework.data-assets/Runtime/Systems/DataAssets/ScriptableObjectData.cs"));
            _rowFolder = ScriptableObjectConfigsController.GetDataPath(_root);
            RequireLocalAssetPath(_rowFolder);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_rowFolder)) ||
                File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_rowFolder)) ||
                File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_rowFolder + ".meta")), Is.False, "Do not adopt another author's row directory.");
            var row = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            _rowFolderGuid = AssetDatabase.AssetPathToGUID(_rowFolder);
            Assert.That(_rowFolderGuid, Is.Not.Empty);
            row.configName = "current-data-assets";
            row.description = "Current author-generated publication input";
            ScriptableObjectConfigsController.Save(_root);
            string rowPath = AssetDatabase.GetAssetPath(row);
            string rootGuid = AssetDatabase.AssetPathToGUID(rootPath), rowGuid = AssetDatabase.AssetPathToGUID(rowPath);
            Assert.That(rootGuid, Is.Not.EqualTo("bc5f270fc89b4f64e96f0792352cb45e"));
            Assert.That(rowGuid, Is.Not.EqualTo("ec36d8de00c0bb849a6aaf0c124d0230"));
            Resources.UnloadAsset(row);
            Resources.UnloadAsset(_root);
            _root = AssetDatabase.LoadAssetAtPath<ScriptableObjectConfigs>(rootPath);
            row = AssetDatabase.LoadAssetAtPath<ScriptableObjectData>(rowPath);
            Assert.That(_root.typeScriptGuid, Is.EqualTo("a1743beab98e80e47b2c8b4917852add"));
            Assert.That(_root.configs.Single(), Is.SameAs(row));
            Assert.That(row.parent, Is.SameAs(_root));
            Assert.That(MonoScript.FromScriptableObject(_root).GetClass(), Is.EqualTo(typeof(ScriptableObjectConfigs)));
            Assert.That(MonoScript.FromScriptableObject(row).GetClass(), Is.EqualTo(typeof(ScriptableObjectData)));
            Assert.That(new[] { _root.GetType().Assembly.GetName().Name, row.GetType().Assembly.GetName().Name }, Is.All.EqualTo("YLFramework.DataAssets"));
            StringAssert.Contains("configName: current-data-assets", File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(rowPath)));
            StringAssert.DoesNotContain("config_name:", File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(rowPath)));
            StringAssert.DoesNotContain("type_script_guid:", File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(rootPath)));
            int rowId = row.id;
            string rowName = row.configName, rowDescription = row.description;
            Assert.That(rowName, Is.EqualTo("current-data-assets"));
            string evidence = Path.Combine(_output, "current-data-assets-input.json");
            Assert.That(File.Exists(evidence), Is.False);
            File.WriteAllText(evidence, Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                rootGuid,
                rowGuid,
                rowId,
                rowName,
                rowDescription,
                runtimeAssembly = typeof(ScriptableObjectConfigs).Assembly.GetName().Name
            }, Newtonsoft.Json.Formatting.Indented));
            var definitions = new[] { rootPath, rowPath, rootPath + ".meta", rowPath + ".meta" }
                .ToDictionary(path => path, path => File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            string projectId = "data-move-" + Guid.NewGuid().ToString("N");
            RegisterEntries(projectId, (rootPath, projectId + "/root"), (rowPath, projectId + "/row"));
            var built = ResourceEditorOperations.BuildContent(Settings(), Path.Combine(_output, "data-assets-content-state.bin"));
            BuildAndRunPlayer(built, projectId, _folder, rowId, rowName, rowDescription);
            yield return new EnterPlayMode(false);
            yield return StartResources();
            _owner = ScriptableObjectManager.LoadAsync(_resource, new ResourceAddress(projectId, "root"));
            yield return Wait(() => _owner.IsDone, "published root loading");
            Assert.That(_owner.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), _owner.Error?.ToString());
            Assert.That(_owner.Asset.GetName(rowId), Is.EqualTo(rowName));
            Assert.That(_owner.Asset.GetScriptableObjectData(rowId).parent, Is.SameAs(_owner.Asset));
            using (var request = _resource.AcquireAsync<ScriptableObjectData>(projectId + "/row"))
            {
                yield return Wait(() => request.IsDone, "independent published row loading");
                Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
                using var child = request.TakeResult();
                Assert.That(_owner.Asset.GetScriptableObjectData(rowId), Is.SameAs(child.Asset));
                _owner.Dispose();
                _owner = null;
                Assert.That(_resource.GetDiagnostics().LeaseCount, Is.EqualTo(1));
                Assert.That(child.Asset, Is.Not.Null);
                Assert.That(child.Asset.description, Is.EqualTo(rowDescription), "The actual official content must remain alive after its root registration returns.");
            }
            Assert.That(_resource.GetDiagnostics().LeaseCount, Is.Zero);
            using (var shutdown = _startup.StopAsync())
            {
                yield return Wait(() => shutdown.IsDone, "publication resource stop");
                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
            }
            AssertQuiescent(_resource);
            Assert.That(_resource.State, Is.EqualTo(ResourceSystemState.Closed));
            _resource = null;
            _startup = null;
            yield return new ExitPlayMode();
            foreach (var definition in definitions)
                CollectionAssert.AreEqual(definition.Value, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(definition.Key)), "Runtime consumption must not change author assets or their identities.");
        }

        private static void BuildAndRunPlayer(ResourceEditorBuildReport content, string projectId, string assetRoot, int rowId, string rowName, string rowDescription)
        {
            string output = global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            string contentHash = StandaloneFixturePlayer.ContentHash(content);
            StandaloneFixturePlayer.BuildAndRun(output, assetRoot, "YLFramework.DataAssets", content, scene =>
            {
                var type = Type.GetType("YLFramework.Tests.DataAssets.DataAssetPlayerProbe, YLFramework.DataAssets.Acceptance", true);
                var host = new GameObject("DataAssetsStandaloneProbe");
                host.SetActive(false);
                SceneManager.MoveGameObjectToScene(host, scene);
                using (var serialized = new SerializedObject(host.AddComponent(type)))
                {
                    serialized.FindProperty("projectId").stringValue = projectId;
                    serialized.FindProperty("contentHash").stringValue = contentHash;
                    serialized.FindProperty("rowName").stringValue = rowName;
                    serialized.FindProperty("rowDescription").stringValue = rowDescription;
                    serialized.FindProperty("rowId").intValue = rowId;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                host.SetActive(true);
            }, new[] { "YL_DATAASSETS_ACCEPTANCE" });
            string player = File.ReadAllText(Path.Combine(output, "standalone-player-location.txt"));
            CollectionAssert.AreEquivalent(new[] { "YLFramework.Runtime.dll", "YLFramework.DataAssets.dll", "YLFramework.DataAssets.Acceptance.dll" },
                Directory.GetFiles(Path.Combine(player, "P_Data/Managed"), "YLFramework*.dll").Select(Path.GetFileName), "No other feature or Editor assembly may enter the real DataAssets Player.");
            for (int run = 1; run <= 2; run++)
            {
                var report = JObject.Parse(File.ReadAllText(Path.Combine(output, "StandalonePlayer-" + run + ".json")));
                Assert.That((int)report["schemaVersion"], Is.EqualTo(2));
                Assert.That((string)report["contentHash"], Is.EqualTo(contentHash));
                Assert.That((bool)report["childSurvivedRootRelease"], Is.True);
                Assert.That((bool)report["ledgerMeasured"], Is.True);
                Assert.That((string)report["resourceState"], Is.EqualTo("Closed"));
                foreach (string count in new[] { "leaseCount", "scopeCount", "requestCount", "pendingWorkCount", "drainingHandleCount" })
                    Assert.That((int)report[count], Is.Zero, count + ": " + report);
            }
        }

        private void CreateOwnedFolder()
        {
            _folder = "Assets/__YLDataAssembly_" + Guid.NewGuid().ToString("N");
            Assert.That(Directory.Exists(_folder) || File.Exists(_folder) || File.Exists(_folder + ".meta") || AssetDatabase.IsValidFolder(_folder), Is.False);
            _folderGuid = AssetDatabase.CreateFolder("Assets", _folder.Substring("Assets/".Length));
            Assert.That(_folderGuid, Is.Not.Empty);
            Assert.That(AssetDatabase.CreateFolder(_folder, "Content"), Is.Not.Empty);
        }

        private AddressableAssetSettings Settings()
        {
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
            Assert.That(settings, Is.Not.Null);
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(settings)), Is.EqualTo(_settingsGuid), "Do not switch or replace the prepared official settings.");
            return settings;
        }

        private void RegisterEntries(string name, params (string Path, string Key)[] assets)
        {
            var settings = Settings();
            var existing = new List<AddressableAssetEntry>();
            settings.GetAllAssets(existing, false);
            _entries = assets.ToDictionary(asset => AssetDatabase.AssetPathToGUID(asset.Path), asset => asset.Key, StringComparer.Ordinal);
            Assert.That(_entries.Keys, Is.All.Not.Empty);
            foreach (var entry in _entries)
                Assert.That(existing.Any(value => value.guid == entry.Key || value.address == entry.Value || value.labels.Contains(entry.Value)), Is.False, "Never move or overwrite another owner's entry.");
            Assert.That(settings.groups.Any(group => group && group.Name == name), Is.False);
            _groupPath = settings.GroupFolder.Replace('\\', '/') + "/" + name + ".asset";
            var schemas = settings.GroupSchemaFolder.Replace('\\', '/');
            foreach (string path in new[] { _groupPath, schemas + "/" + name + "_BundledAssetGroupSchema.asset", schemas + "/" + name + "_ContentUpdateGroupSchema.asset" })
            {
                RequireLocalAssetPath(path);
                Assert.That(File.Exists(path) || File.Exists(path + ".meta") || Directory.Exists(path), Is.False, "Never overwrite an occupied official group/schema output.");
            }
            var group = settings.CreateGroup(name, false, false, false, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            Assert.That(AssetDatabase.GetAssetPath(group), Is.EqualTo(_groupPath));
            _groupGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(group));
            var bundled = group.GetSchema<BundledAssetGroupSchema>();
            bundled.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
            bundled.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            bundled.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            bundled.IncludeInBuild = bundled.IncludeAddressInCatalog = bundled.IncludeGUIDInCatalog = true;
            group.GetSchema<ContentUpdateGroupSchema>().StaticContent = false;
            foreach (var entry in _entries)
                settings.CreateOrMoveEntry(entry.Key, group).SetAddress(entry.Value);
            foreach (var schema in group.Schemas)
            {
                EditorUtility.SetDirty(schema);
                AssetDatabase.SaveAssetIfDirty(schema);
            }
            _schemaGuids = group.Schemas.Select(schema => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(schema))).ToArray();
            AssetDatabase.SaveAssetIfDirty(group);
            AssetDatabase.SaveAssetIfDirty(settings);
        }

        private IEnumerator StartResources()
        {
            Assert.That(ResourceSystem.Current, Is.Null);
            _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var initialized = _startup.StartAsync();
            yield return Wait(() => initialized.IsDone, "resource startup");
            Assert.That(initialized.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), initialized.Error?.ToString());
            _resource = initialized.TakeResult().Resources;
        }

        private static void RequireLocalAssetPath(string path)
        {
            Assert.That(path.StartsWith("Assets/", StringComparison.Ordinal), Is.True, "Only local host Assets may be authored or removed.");
            for (string cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
                if (File.Exists(cursor) || Directory.Exists(cursor))
                    Assert.That((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, Is.True, "Do not author or delete through linked assets.");
        }

        private static IEnumerator Wait(Func<bool> completed, string operation)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            while (!completed() && Time.realtimeSinceStartupAsDouble < deadline)
                yield return null;
            Assert.That(completed(), Is.True, operation + " timed out.");
        }

        private static bool IsQuiescent(ResourceSystem resource)
        {
            var report = resource.GetDiagnostics();
            return report.LeaseCount + report.ScopeCount + report.RequestCount + report.PendingWorkCount + report.DrainingHandleCount == 0;
        }

        private static void AssertQuiescent(ResourceSystem resource)
        {
            var report = resource.GetDiagnostics();
            Assert.That(report.LeaseCount, Is.Zero);
            Assert.That(report.ScopeCount, Is.Zero);
            Assert.That(report.RequestCount, Is.Zero);
            Assert.That(report.PendingWorkCount, Is.Zero);
            Assert.That(report.DrainingHandleCount, Is.Zero);
            Assert.That(report.Errors, Is.Empty);
        }

        [UnityTearDown]
        public IEnumerator CleanupOwnedInputs()
        {
            if (!_ownsHost)
                yield break;
            _owner?.Dispose();
            _owner = null;
            if (_startup != null && _resource?.State != ResourceSystemState.Invalidated && _resource?.State != ResourceSystemState.Closed)
            {
                using var shutdown = _startup.StopAsync();
                yield return Wait(() => shutdown.IsDone, "owned cleanup stop");
                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
            }
            if (EditorApplication.isPlaying)
                yield return new ExitPlayMode();
            if (_resource != null)
            {
                yield return Wait(() => IsQuiescent(_resource), "owned cleanup drain");
                AssertQuiescent(_resource);
            }
            _resource = null;
            _startup = null;
            Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero);
            if (_folder != null && TestContext.CurrentContext.Result.Outcome.Status != NUnit.Framework.Interfaces.TestStatus.Failed)
            {
                if (_groupGuid != null)
                {
                    Assert.That(AssetDatabase.AssetPathToGUID(_groupPath), Is.EqualTo(_groupGuid));
                    var group = AssetDatabase.LoadAssetAtPath<AddressableAssetGroup>(_groupPath);
                    Assert.That(group.entries.Count, Is.EqualTo(_entries.Count));
                    Assert.That(group.entries.All(entry => _entries.TryGetValue(entry.guid, out var key) && key == entry.address), Is.True);
                    CollectionAssert.AreEquivalent(_schemaGuids, group.Schemas.Select(schema => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(schema))));
                    foreach (string path in new[] { _groupPath }.Concat(group.Schemas.Select(AssetDatabase.GetAssetPath)))
                        RequireLocalAssetPath(path);
                    var settings = Settings();
                    settings.RemoveGroup(group);
                    AssetDatabase.SaveAssetIfDirty(settings);
                }
                Assert.That(AssetDatabase.AssetPathToGUID(_folder), Is.EqualTo(_folderGuid));
                RequireLocalAssetPath(_folder);
                Assert.That(AssetDatabase.DeleteAsset(_folder), Is.True);
                if (_rowFolderGuid != null)
                {
                    Assert.That(AssetDatabase.AssetPathToGUID(_rowFolder), Is.EqualTo(_rowFolderGuid));
                    RequireLocalAssetPath(_rowFolder);
                    Assert.That(AssetDatabase.DeleteAsset(_rowFolder), Is.True);
                }
            }
            _root = null;
            _folder = _folderGuid = _rowFolder = _rowFolderGuid = _groupPath = _groupGuid = null;
            _schemaGuids = null;
            _entries = null;
            _ownsHost = false;
        }
    }
}
