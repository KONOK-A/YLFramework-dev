using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.DataAssets;
using YLFramework.Localization;
using YLFramework.Resource;
using YLFramework.Save;
using Object = UnityEngine.Object;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.Extensions.Data
{
    [Category("YL.Special.Session")]
    public class DataDomainReloadSessionTests
    {
        private static readonly string _domainToken = Guid.NewGuid().ToString("N");
        private static readonly FieldInfo _storeField = typeof(SaveManager).GetField("_store", BindingFlags.Static | BindingFlags.NonPublic);
        private bool _ownsSession;
        private bool _wasEnabled;
        private EnterPlayModeOptions _previousOptions;
        private SceneSetup[] _previousScenes;
        private string _previousOverride;
        private string _previousDefault;
        private readonly List<Object> _objects = new();
        private ResourceSystem _resource;
        private SaveStore _store;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput())), Is.True);
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.True);
            Assert.That(EditorSettings.enterPlayModeOptions, Is.EqualTo(EnterPlayModeOptions.DisableDomainReload));
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(LocalizationManager.Configs, Is.Empty);
            Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero);
            Assert.That(_storeField.GetValue(null), Is.Null, "Do not close another Save owner.");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Assert.That(SceneManager.GetSceneAt(i).isDirty, Is.False);
            }

            _previousScenes = EditorSceneManager.GetSceneManagerSetup();
            _wasEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _previousOptions = EditorSettings.enterPlayModeOptions;
            _previousOverride = (string)typeof(SaveManager).GetField("_storageRootOverride", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            _previousDefault = (string)typeof(SaveManager).GetField("_defaultStorageRoot", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            _ownsSession = true;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return null;
        }

        [UnityTest]
        public IEnumerator TwoRealPlaySessionsWithoutDomainReloadReleaseOldOwnersAndCaches()
        {
            string token = _domainToken;
            string output = YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            for (int round = 1; round <= 2; round++)
            {
                yield return new EnterPlayMode(expectDomainReload: false);
                Assert.That(Application.isPlaying, Is.True);
                Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.True);
                Assert.That(EditorSettings.enterPlayModeOptions, Is.EqualTo(EnterPlayModeOptions.DisableDomainReload));
                Assert.That(_domainToken, Is.EqualTo(token), "The managed domain must actually survive both entries.");
                Assert.That(ResourceSystem.Current, Is.Null);
                Assert.That(LocalizationManager.Configs, Is.Empty);
                Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero);
                Assert.That(_storeField.GetValue(null), Is.Null);
                var language = ScriptableObject.CreateInstance<LanguageConfig>();
                _objects.Add(language);
                language.language = SystemLanguage.English;
                language.languagesInfo.Add(new LanguageInfo { id = 17, str = "round-" + round });
                var localization = ScriptableObject.CreateInstance<MultiLanguageConfigs>();
                _objects.Add(localization);
                localization.Languages.Add(language);
                var data = ScriptableObject.CreateInstance<ScriptableObjectConfigs>();
                _objects.Add(data);
                data.name = "domain-data";
                var child = ScriptableObject.CreateInstance<ScriptableObjectData>();
                _objects.Add(child);
                child.id = 17;
                child.configName = "round-" + round;
                child.parent = data;
                data.configs.Add(child);
                var source = new Source { Localization = localization, Data = data };
                var locAddress = new ResourceAddress("domain-b", "localization");
                var dataAddress = new ResourceAddress("domain-b", "data");
                using (var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[]
                {
                    new ResourceCatalog("domain-b", new[]
                    {
                        new ResourceCatalogEntry("localization", locAddress, new ResourceLocation(source.Id, "localization", typeof(MultiLanguageConfigs))),
                        new ResourceCatalogEntry("data", dataAddress, new ResourceLocation(source.Id, "data", typeof(ScriptableObjectConfigs)))
                    })
                }, new IResourceSource[] { source })))
                {
                    _resource = initialized.TakeResult();
                }

                var locOwner = LocalizationManager.LoadConfigAsync(_resource, locAddress);
                ScriptableObjectManager.Load(_resource, dataAddress);
                LocalizationManager.Language = SystemLanguage.English;
                Assert.That(LocalizationManager.GetString(17), Is.EqualTo("round-" + round));
                Assert.That(ScriptableObjectManager.GetConfig("domain-data").GetName(17), Is.EqualTo("round-" + round));
                SaveManager.SetStorageRoot(Path.Combine(output, "save-round-" + round));
                var model = SaveManager.CreateModel<Model>(3);
                model.Value = round;
                SaveManager.SaveModel(model);
                Assert.That(SaveManager.LoadModel<Model>(3).Value, Is.EqualTo(round));
                _store = (SaveStore)_storeField.GetValue(null);
                yield return null;
                yield return new ExitPlayMode();
                bool saveClosed = false;
                try
                {
                    _store.GetAllSaveItems();
                }
                catch (ObjectDisposedException)
                {
                    saveClosed = true;
                }
                var record = new
                {
                    round,
                    domainToken = _domainToken,
                    pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                    domainReloadDisabled = EditorSettings.enterPlayModeOptionsEnabled && EditorSettings.enterPlayModeOptions == EnterPlayModeOptions.DisableDomainReload,
                    localizationOwners = LocalizationManager.Configs.Count,
                    dataOwners = ScriptableObjectManager.RegistrationCount,
                    locOwnerClosed = locOwner.IsDisposed,
                    leases = _resource.GetDiagnosticsSnapshot().LeaseCount,
                    sourceReleases = source.Releases,
                    resourceState = _resource.State.ToString(),
                    saveClosed
                };
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(output, "domain-round-" + round + ".json")), JsonConvert.SerializeObject(record, Formatting.Indented));
                Assert.That(record.localizationOwners, Is.Zero);
                Assert.That(record.dataOwners, Is.Zero);
                Assert.That(record.locOwnerClosed, Is.True);
                Assert.That(record.leases, Is.Zero);
                Assert.That(record.sourceReleases, Is.EqualTo(2));
                Assert.That(saveClosed, Is.True, "The previous Unity session must not retain the Save file lock/cache owner.");
                if (_resource.State == ResourceSystemState.Ready)
                {
                    using var shutdown = _resource.ShutdownAsync();
                    yield return shutdown;
                    Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
                }
                Assert.That(_resource.State == ResourceSystemState.Closed || _resource.State == ResourceSystemState.Invalidated, Is.True, "Editor exit must leave no live Resource session.");
                Assert.That(ResourceSystem.Current, Is.Null);
                _resource = null;
                _store = null;
                DestroyOwnedObjects();
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_ownsSession)
            {
                yield break;
            }

            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }

            LocalizationManager.Shutdown();
            ScriptableObjectManager.Clear();
            SaveManager.Close();
            _store?.Dispose();
            if (_resource != null && _resource.State == ResourceSystemState.Ready)
            {
                using var shutdown = _resource.ShutdownAsync();
                yield return shutdown;
            }
            SaveManager.SetStorageRoot(_previousOverride);
            typeof(SaveManager).GetField("_defaultStorageRoot", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, _previousDefault);
            DestroyOwnedObjects();
            EditorSettings.enterPlayModeOptionsEnabled = _wasEnabled;
            EditorSettings.enterPlayModeOptions = _previousOptions;
            if (_previousScenes.Length > 0 && Array.Exists(_previousScenes, s => s.isLoaded) && Array.Exists(_previousScenes, s => s.isActive) && Array.TrueForAll(_previousScenes, s => !string.IsNullOrEmpty(s.path)))
            {
                EditorSceneManager.RestoreSceneManagerSetup(_previousScenes);
            }
            else
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        private void DestroyOwnedObjects()
        {
            foreach (var item in _objects)
            {
                if (item)
                {
                    Object.DestroyImmediate(item);
                }
            }
            _objects.Clear();
        }
        public sealed class Model : ISaveModel
        {
            public int Id
            {
                get; set;
            }
            public int Value;
        }
        private sealed class Immediate : ResourceSourceOperation
        {
            public Immediate(Object value) => Complete(value, null);
        }
        private sealed class Source : IResourceSource
        {
            public ResourceSourceId Id => new("domain-b-source");
            public bool SupportsSynchronousLoad => true;
            public Object Localization;
            public Object Data;
            public int Releases;
            private Object Asset(Type type) => type == typeof(MultiLanguageConfigs) ? Localization : Data;
            public void Validate(ResourceCatalogEntry entry)
            {
            }
            public ResourceSourceResult Load(ResourceCatalogEntry entry, Type type) => new(Asset(type));
            public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type) => new Immediate(Asset(type));
            public void Release(Object asset) => Releases++;
        }
    }
}
