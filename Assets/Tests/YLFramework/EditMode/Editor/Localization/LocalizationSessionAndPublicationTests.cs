using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Localization;
using YLFramework.Resource;
using YLFramework.Testing.EditorFixtures;
using Object = UnityEngine.Object;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.Extensions.Data
{
    [Category("YL.Special.Session")]
    public class LocalizationSessionAndPublicationTests
    {
        private static readonly string _domainToken = Guid.NewGuid().ToString("N");
        private LocalizationConfigHandle _owner;
        private ResourceSystem _resources;
        private MultiLanguageConfigs _root;
        private LanguageConfig _english;
        private string _folder;
        private bool _hadPreference;
        private int _preference;

        private sealed class Operation : ResourceSourceOperation
        {
            internal void Finish(Object value) => Complete(value, null);
        }

        private sealed class Source : IResourceSource
        {
            internal MultiLanguageConfigs Root;
            internal int Releases;
            public ResourceSourceId Id => new ResourceSourceId("localization-two-play");
            public bool SupportsSynchronousLoad => true;
            public void Validate(ResourceCatalogEntry entry)
            {
            }
            public ResourceSourceResult Load(ResourceCatalogEntry entry, Type type) => new ResourceSourceResult(Root);
            public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type)
            {
                var result = new Operation();
                result.Finish(Root);
                return result;
            }
            public void Release(Object value) => Releases++;
        }

        [SetUp]
        public void RequireOwnedHost()
        {
            Assert.That(Application.isBatchMode && !EditorApplication.isPlaying, Is.True);
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.True);
            Assert.That(EditorSettings.enterPlayModeOptions, Is.EqualTo(EnterPlayModeOptions.DisableDomainReload));
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(LocalizationManager.Configs, Is.Empty);
            Assert.That(Directory.Exists(global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput()), Is.True);
            _hadPreference = PlayerPrefs.HasKey(LocalizationManager.LanguagePreferenceKey);
            _preference = PlayerPrefs.GetInt(LocalizationManager.LanguagePreferenceKey);
        }

        private void CreateRoot()
        {
            _root = ScriptableObject.CreateInstance<MultiLanguageConfigs>();
            _root.name = "PublishedLocale";
            _english = ScriptableObject.CreateInstance<LanguageConfig>();
            _english.language = SystemLanguage.English;
            _english.languagesInfo.Add(new LanguageInfo { id = 17, str = "hello {0}" });
            _root.Languages.Add(_english);
        }

        [UnityTest]
        public IEnumerator TwoActualPlaySessionsCloseOnlyTheirLocalizationOwners()
        {
            string token = _domainToken;
            for (int round = 0; round < 2; round++)
            {
                yield return new EnterPlayMode(false);
                Assert.That(_domainToken, Is.EqualTo(token));
                Assert.That(LocalizationManager.Configs, Is.Empty);
                CreateRoot();
                var source = new Source { Root = _root };
                var address = new ResourceAddress("localization-two-play", "root");
                using (var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[]
                {
                    new ResourceCatalog("localization-two-play", new[] { new ResourceCatalogEntry("root", address, new ResourceLocation(source.Id, "root", typeof(MultiLanguageConfigs))) })
                }, new IResourceSource[] { source })))
                {
                    Assert.That(initialized.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
                    _resources = initialized.TakeResult();
                }
                _owner = LocalizationManager.LoadConfigAsync(_resources, address);
                while (_owner.IsLoading)
                {
                    yield return null;
                }
                Assert.That(_owner.Error, Is.Null);
                Assert.That(_owner.Config, Is.SameAs(_root));
                Assert.That(LocalizationManager.GetString(17), Is.EqualTo("hello {0}"));
                yield return new ExitPlayMode();
                Assert.That(_owner.IsDisposed, Is.True);
                Assert.That(LocalizationManager.Configs, Is.Empty);
                Assert.That(source.Releases, Is.EqualTo(1));
                Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
                _owner = null;
                _resources = null;
                if (_root)
                {
                    Object.DestroyImmediate(_root);
                }
                if (_english)
                {
                    Object.DestroyImmediate(_english);
                }
                _root = null;
                _english = null;
            }
        }

        [UnityTest]
        public IEnumerator FormalPublicationLoadsTheMovedTypesInEditorAndActualStandalonePlayers()
        {
            _folder = "Assets/__YLLocale_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", _folder.Substring("Assets/".Length));
            AssetDatabase.CreateFolder(_folder, "Content");
            CreateRoot();
            AssetDatabase.CreateAsset(_root, _folder + "/Content/Root.asset");
            AssetDatabase.CreateAsset(_english, _folder + "/Content/English.asset");
            var project = ScriptableObject.CreateInstance<ResourceProject>();
            project.projectId = "loc-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(_folder + "/Content") });
            var group = new ResourceGroup { id = "Languages", packing = ResourcePacking.Together };
            group.selections.Add(new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(_folder + "/Content/Root.asset"), address = "root" });
            group.selections.Add(new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(_folder + "/Content/English.asset"), address = "english" });
            project.groups.Add(group);
            AssetDatabase.CreateAsset(project, _folder + "/Project.asset");
            EditorUtility.SetDirty(_root);
            EditorUtility.SetDirty(_english);
            EditorUtility.SetDirty(project);
            AssetDatabase.SaveAssetIfDirty(_root);
            AssetDatabase.SaveAssetIfDirty(_english);
            AssetDatabase.SaveAssetIfDirty(project);
            var release = ResourcePublisher.Publish(ResourceBundleBuilder.Build(project, BuildTarget.StandaloneWindows64), "localization-assembly-v1");
            var moved = release.Catalog.Assets.Where(asset => asset.AssetType.StartsWith("YLFramework.Localization.", StringComparison.Ordinal)).ToArray();
            Assert.That(moved, Has.Length.EqualTo(2));
            Assert.That(moved.Select(asset => asset.AssetAssembly), Is.All.EqualTo("YLFramework.Localization"));
            string output = global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            StandaloneFixturePlayer.BuildAndRun(output, _folder, "YLFramework.Localization", release, scene =>
            {
                var type = Type.GetType("YLFramework.Tests.Localization.LocalizationPlayerProbe, YLFramework.Localization.Acceptance", true);
                var host = new GameObject("LocalizationStandaloneProbe");
                host.SetActive(false);
                SceneManager.MoveGameObjectToScene(host, scene);
                var probe = host.AddComponent(type);
                foreach (var value in new[] { ("projectId", release.Catalog.ProjectId), ("catalogDigest", release.Catalog.CatalogDigest), ("compatibility", release.Catalog.AppContentCompatibilityId) })
                {
                    type.GetField(value.Item1).SetValue(probe, value.Item2);
                }
                var binding = host.AddComponent<LocalizeString>();
                binding.enabled = false;
                binding.Id = 17;
                using (var serialized = new SerializedObject(binding))
                {
                    serialized.FindProperty("componentType").enumValueIndex = (int)ComponentType.Other;
                    serialized.FindProperty("componentFullName").stringValue = type.FullName;
                    serialized.FindProperty("fieldName").stringValue = "Value";
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                type.GetField("binding").SetValue(probe, binding);
                host.SetActive(true);
            });
            var snapshot = ResourceLocalSnapshot.Open(Path.GetFullPath(release.DirectoryPath), project.projectId, release.Catalog.CatalogDigest,
                release.Catalog.Platform, release.Catalog.AppContentCompatibilityId);
            var catalog = snapshot.CreateCatalog(new[] { typeof(MultiLanguageConfigs), typeof(LanguageConfig) });
            string projectId = project.projectId;
            yield return new EnterPlayMode(false);
            using (var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { catalog })))
            {
                Assert.That(initialized.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
                _resources = initialized.TakeResult();
            }
            _owner = LocalizationManager.LoadConfigAsync(_resources, new ResourceAddress(projectId, "root"));
            while (_owner.IsLoading)
            {
                yield return null;
            }
            Assert.That(_owner.Error, Is.Null);
            Assert.That(_owner.Config, Is.Not.Null);
            LocalizationManager.Language = SystemLanguage.Japanese;
            Assert.That(LocalizationManager.GetString(17), Is.EqualTo("hello {0}"));
            _owner.Dispose();
            _owner = null;
            Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            using (var shutdown = _resources.ShutdownAsync())
            {
                yield return shutdown;
                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            }
            _resources = null;
            yield return new ExitPlayMode();
        }

        [UnityTearDown]
        public IEnumerator CleanupOwnedFixture()
        {
            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }
            _owner?.Dispose();
            _owner = null;
            LocalizationManager.Shutdown();
            if (_resources != null && _resources.State == ResourceSystemState.Ready)
            {
                using var closed = _resources.ShutdownAsync();
                yield return closed;
                Assert.That(closed.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            }
            _resources = null;
            if (_hadPreference)
            {
                PlayerPrefs.SetInt(LocalizationManager.LanguagePreferenceKey, _preference);
            }
            else
            {
                PlayerPrefs.DeleteKey(LocalizationManager.LanguagePreferenceKey);
            }
            PlayerPrefs.Save();
            if (_folder != null && TestContext.CurrentContext.Result.Outcome.Status != NUnit.Framework.Interfaces.TestStatus.Failed)
            {
                Assert.That(AssetDatabase.DeleteAsset(_folder), Is.True);
            }
            else if (_folder == null)
            {
                if (_root)
                {
                    Object.DestroyImmediate(_root);
                }
                if (_english)
                {
                    Object.DestroyImmediate(_english);
                }
            }
            _folder = null;
            _root = null;
            _english = null;
            Assert.That(LocalizationManager.Configs, Is.Empty);
        }
    }
}
