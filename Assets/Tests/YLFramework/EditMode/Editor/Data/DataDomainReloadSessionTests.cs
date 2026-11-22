using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.DataAssets;
using YLFramework.Localization;
using YLFramework.Resource;
using YLFramework.Save;
using UnityEngine.Localization;
using YLFramework.Tests.Localization;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Extensions.Data
{
    [Category("YL.Special.DataComposition")]
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
        private ResourceStartup _startup;
        private ResourceLocationMap _locator;
        private SessionProvider _provider;
        private ScriptableObjectAsset<ScriptableObjectConfigs> _dataOwner;
        private LocalizationSession _localizationOwner;
        private SaveStore _store;
        private LocalizationFixture _localization;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _ownsSession = false;
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var owner = JObject.Parse(File.ReadAllText(Path.Combine(project, ".yl-owner.json")));
            Assert.That((int)owner["schemaVersion"], Is.EqualTo(1));
            Assert.That((string)owner["purpose"], Is.EqualTo("YLTesting.Directory"));
            Assert.That((string)owner["kind"], Is.EqualTo("Host"));
            Assert.That(Path.GetFullPath((string)owner["path"]), Is.EqualTo(project).IgnoreCase);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput())), Is.True);
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.True);
            Assert.That(EditorSettings.enterPlayModeOptions, Is.EqualTo(EnterPlayModeOptions.DisableDomainReload));
            Assert.That(ResourceSystem.Current, Is.Null);
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
            ResourceSystem previous = null;
            for (int round = 1; round <= 2; round++)
            {
                yield return new EnterPlayMode(expectDomainReload: false);
                Assert.That(Application.isPlaying, Is.True);
                Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.True);
                Assert.That(EditorSettings.enterPlayModeOptions, Is.EqualTo(EnterPlayModeOptions.DisableDomainReload));
                Assert.That(_domainToken, Is.EqualTo(token), "The managed domain must actually survive both entries.");
                Assert.That(ResourceSystem.Current, Is.Null);
                Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero);
                Assert.That(_storeField.GetValue(null), Is.Null);
                _localization = new LocalizationFixture();
                _localization.StringTable(_localization.English).GetEntry("name").Value = "round-" + round;
                var data = ScriptableObject.CreateInstance<ScriptableObjectConfigs>();
                _objects.Add(data);
                data.name = "domain-data";
                var child = ScriptableObject.CreateInstance<ScriptableObjectData>();
                _objects.Add(child);
                child.id = 17;
                child.configName = "round-" + round;
                child.parent = data;
                data.configs.Add(child);
                string project = "data-session-" + Guid.NewGuid().ToString("N");
                var dataAddress = new ResourceAddress(project, "data");
                _provider = new SessionProvider(project);
                _locator = new ResourceLocationMap(project);
                _locator.Add(project + "/data", new ResourceLocationBase(project + "/data", project + "/data", _provider.ProviderId, typeof(ScriptableObjectConfigs)) { Data = data });
                _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
                using (var initialized = _startup.StartAsync())
                {
                    double deadline = Time.realtimeSinceStartupAsDouble + 15;
                    while (!initialized.IsDone && Time.realtimeSinceStartupAsDouble < deadline)
                        yield return null;
                    Assert.That(initialized.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), initialized.Error?.ToString());
                    _resource = initialized.TakeResult().Resources;
                }
                Assert.That(_resource, Is.Not.SameAs(previous));
                Addressables.ResourceManager.ResourceProviders.Add(_provider);
                Addressables.AddResourceLocator(_locator);

                var locOwner = new LocalizationSession();
                _localizationOwner = locOwner;
                string translated = null;
                locOwner.BindString(new LocalizedString(_localization.Strings, "name"), value => translated = value);
                _dataOwner = ScriptableObjectManager.LoadAsync(_resource, dataAddress);
                double loadDeadline = Time.realtimeSinceStartupAsDouble + 15;
                while ((!_dataOwner.IsDone || !locOwner.IsReady || translated != "round-" + round) && Time.realtimeSinceStartupAsDouble < loadDeadline)
                    yield return null;
                Assert.That(_dataOwner.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), _dataOwner.Error?.ToString());
                Assert.That(locOwner.IsReady, Is.True, locOwner.Error?.ToString());
                Assert.That(translated, Is.EqualTo("round-" + round));
                Assert.That(_dataOwner.Asset, Is.SameAs(data));
                Assert.That(ScriptableObjectManager.GetConfig("domain-data").GetName(17), Is.EqualTo("round-" + round));
                Assert.That(_dataOwner.Asset.GetScriptableObjectData(17), Is.SameAs(child), "Rows are borrowed from the root, not separate resource owners.");
                Assert.That(_resource.GetDiagnostics().LeaseCount, Is.EqualTo(1));
                Assert.That(_provider.Provides, Is.EqualTo(1));
                Assert.That(_provider.Releases, Is.Zero);
                string storageRoot = Path.Combine(output, "save-round-" + round);
                Assert.That(Directory.Exists(storageRoot) || File.Exists(storageRoot), Is.False, "Never adopt an existing Save test root.");
                SaveManager.SetStorageRoot(storageRoot);
                _store = SaveManager.Store;
                var model = _store.CreateModel<Model>(3);
                model.Value = round;
                _store.SaveModel(model);
                Assert.That(_store.LoadModel<Model>(3).Value, Is.EqualTo(round));
                Assert.Throws<IOException>(() => { using var competingOwner = new SaveStore(storageRoot); });
                yield return null;
                yield return new ExitPlayMode();
                Assert.That(_domainToken, Is.EqualTo(token));
                double drainDeadline = Time.realtimeSinceStartupAsDouble + 15;
                while ((!IsQuiescent(_resource) || _provider.Releases != 1) && Time.realtimeSinceStartupAsDouble < drainDeadline)
                    yield return null;
                bool saveClosed = false;
                try
                {
                    _store.GetAllSaveItems();
                }
                catch (ObjectDisposedException)
                {
                    saveClosed = true;
                }
                bool saveLockReleased;
                using (var fileLock = new FileStream(Path.Combine(storageRoot, ".yl-save.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    saveLockReleased = fileLock.CanRead && fileLock.CanWrite;
                int savedValue;
                using (var reopened = new SaveStore(storageRoot))
                {
                    var coldModel = reopened.LoadModel<Model>(3);
                    Assert.That(coldModel, Is.Not.SameAs(model), "The persisted value must not come from the old store cache.");
                    savedValue = coldModel.Value;
                }
                Assert.That(_dataOwner.IsDisposed, Is.True, "Observe exit cleanup before a registry query can prune an invalid root.");
                var diagnostics = _resource.GetDiagnostics();
                var record = new
                {
                    round,
                    domainToken = _domainToken,
                    pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                    domainReloadDisabled = EditorSettings.enterPlayModeOptionsEnabled && EditorSettings.enterPlayModeOptions == EnterPlayModeOptions.DisableDomainReload,
                    localizationSessionClosed = locOwner.IsDisposed,
                    dataOwners = ScriptableObjectManager.RegistrationCount,
                    locOwnerClosed = locOwner.IsDisposed,
                    leases = diagnostics.LeaseCount,
                    scopes = diagnostics.ScopeCount,
                    requests = diagnostics.RequestCount,
                    pendingWork = diagnostics.PendingWorkCount,
                    drainingHandles = diagnostics.DrainingHandleCount,
                    providerProvides = _provider.Provides,
                    providerReleases = _provider.Releases,
                    resourceState = _resource.State.ToString(),
                    saveClosed,
                    saveLockReleased,
                    savedValue
                };
                using (var stream = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(output, "domain-round-" + round + ".json")), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream))
                    writer.Write(JsonConvert.SerializeObject(record, Formatting.Indented));
                Assert.That(record.localizationSessionClosed, Is.True);
                Assert.That(record.domainReloadDisabled, Is.True);
                Assert.That(record.dataOwners, Is.Zero);
                Assert.That(record.locOwnerClosed, Is.True);
                Assert.That(record.leases, Is.Zero);
                Assert.That(record.scopes, Is.Zero);
                Assert.That(record.requests, Is.Zero);
                Assert.That(record.pendingWork, Is.Zero);
                Assert.That(record.drainingHandles, Is.Zero);
                Assert.That(diagnostics.Errors, Is.Empty);
                Assert.That(record.providerProvides, Is.EqualTo(1));
                Assert.That(record.providerReleases, Is.EqualTo(1));
                Assert.That(saveClosed, Is.True, "The previous Unity session must not retain the Save file lock/cache owner.");
                Assert.That(saveLockReleased, Is.True);
                Assert.That(savedValue, Is.EqualTo(round));
                Assert.That(_storeField.GetValue(null), Is.Null);
                Assert.That(_resource.State == ResourceSystemState.Closed || _resource.State == ResourceSystemState.Invalidated, Is.True, "Editor exit must leave no live Resource session.");
                Assert.That(ResourceSystem.Current, Is.Null);
                previous = _resource;
                RemoveProvider();
                _dataOwner = null;
                _localizationOwner = null;
                _startup = null;
                _resource = null;
                _store = null;
                _localization.Dispose();
                _localization = null;
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

            _localization?.Dispose();
            _localization = null;
            _localizationOwner?.Dispose();
            _localizationOwner = null;
            _dataOwner?.Dispose();
            _dataOwner = null;
            ScriptableObjectManager.Clear();
            SaveManager.Close();
            if (_startup != null && _resource?.State != ResourceSystemState.Invalidated && _resource?.State != ResourceSystemState.Closed)
            {
                using var shutdown = _startup.StopAsync();
                double deadline = Time.realtimeSinceStartupAsDouble + 15;
                while (!shutdown.IsDone && Time.realtimeSinceStartupAsDouble < deadline)
                    yield return null;
                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
            }
            if (_resource != null)
            {
                double deadline = Time.realtimeSinceStartupAsDouble + 15;
                while (!IsQuiescent(_resource) && Time.realtimeSinceStartupAsDouble < deadline)
                    yield return null;
                Assert.That(IsQuiescent(_resource), Is.True);
                Assert.That(_resource.GetDiagnostics().Errors, Is.Empty);
            }
            RemoveProvider();
            _startup = null;
            _resource = null;
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
            _ownsSession = false;
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
        private static bool IsQuiescent(ResourceSystem resource)
        {
            var report = resource.GetDiagnostics();
            return report.LeaseCount == 0 && report.ScopeCount == 0 && report.RequestCount == 0 && report.PendingWorkCount == 0 && report.DrainingHandleCount == 0;
        }

        private void RemoveProvider()
        {
            if (_locator != null)
                Addressables.RemoveResourceLocator(_locator);
            if (_provider != null)
                Addressables.ResourceManager.ResourceProviders.Remove(_provider);
            _locator = null;
            _provider = null;
        }

        private sealed class SessionProvider : ResourceProviderBase
        {
            internal int Provides;
            internal int Releases;
            internal SessionProvider(string id) => m_ProviderId = id;
            public override void Provide(ProvideHandle handle)
            {
                Provides++;
                handle.SetWaitForCompletionCallback(() => throw new InvalidOperationException("NoDR consumers must load asynchronously."));
                handle.Complete((Object)handle.Location.Data, true, null);
            }
            public override void Release(IResourceLocation location, object asset) => Releases++;
        }
    }
}
