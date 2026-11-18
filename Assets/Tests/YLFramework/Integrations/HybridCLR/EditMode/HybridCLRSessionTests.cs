using System;
using System.Collections;
using System.IO;
using System.Linq;
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
using YLFramework.Core;
using YLFramework.Integrations.HybridCLR;
using YLFramework.Resource;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    public class HybridCLRSessionTests
    {
        private SceneSetup[] _originalScenes;
        private string _ownedFolder;
        private bool _hasChangedOptions;
        private bool _wasEnabled;
        private EnterPlayModeOptions _oldOptions;
        private HybridCLRLoader _firstLoader;
        private HybridCLRLoader _secondLoader;
        private ResourceStartup _firstStartup;
        private ResourceStartup _secondStartup;
        private SessionContent _firstContent;
        private SessionContent _secondContent;
        private WaitingProvider _replacementProvider;
        private bool _ownsSceneSetup;
        private const string EntryAssembly = "YLFramework.HybridCLR.Tests.PlayMode";
        private const string ManifestKey = "hybridclr-test/hybridclr/manifest.json";
        private static readonly object DomainLifetime = new();
        private static ResourceAddress Address => new("hybridclr-test", HybridCLRManifest.ManifestPath);

        [UnityTest]
        public IEnumerator PendingModuleBecomesTerminalWhenTheRealPlaySessionEnds()
        {
            PrepareOwnedScene();
            yield return new EnterPlayMode(false);
            yield return Initialize();
            var resources = _firstStartup.Resources;
            _firstLoader = new HybridCLRLoader(resources, "base-test");
            _replacementProvider = new WaitingProvider();
            CoreResourceAccess.Bind(_replacementProvider);
            var request = _firstLoader.StartModuleAsync(Address, EntryAssembly, "YLFramework.Tests.Integrations.HybridCLR.HybridCLRWaitingModule");
            yield return Wait(() => _replacementProvider.SceneCalls == 1 || request.IsDone, "real pending Module startup");

            Assert.That(request.Module, Is.Not.Null, request.Error);
            Assert.That(request.IsDone, Is.False);
            Assert.That(_replacementProvider.SceneCalls, Is.EqualTo(1));
            AssertManifestLease(resources);
            yield return new ExitPlayMode();
            Assert.That(request.IsDone, Is.True, "A stopped Core coroutine must not leave its accepted wrapper pending forever.");
            Assert.That(request.Exception, Is.TypeOf<InvalidOperationException>());
            var error = request.Exception;
            Assert.That(resources.IsAvailable || resources.IsSessionActive, Is.False);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            yield return WaitForDrain(resources);
            _firstContent.Dispose();
            yield return new EnterPlayMode(false);
            yield return Initialize(second: true);
            var next = _secondStartup.Resources;
            _firstLoader.Dispose();
            _firstLoader = null;
            yield return Stop(_firstStartup);
            Assert.That(next.IsAvailable, Is.True);
            Assert.That(ResourceSystem.Current, Is.SameAs(next));
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(_secondStartup.CoreProvider));
            Assert.That(request.Exception, Is.SameAs(error), "A later Play session must not rewrite the old terminal result.");
            yield return Stop(_secondStartup);
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator DisabledDomainReloadStartsANewResourceSessionAndRejectsTheOldPublicLoader()
        {
            PrepareOwnedScene();
            var domainLifetime = DomainLifetime;
            yield return new EnterPlayMode(false);
            Assert.That(DomainLifetime, Is.SameAs(domainLifetime));
            yield return Initialize();
            var first = _firstStartup.Resources;
            _firstLoader = new HybridCLRLoader(first, "base-test");
            var firstLoad = _firstLoader.LoadAsync(Address);
            yield return Wait(() => firstLoad.IsDone, "first real Play code load");
            Assert.That(firstLoad.Exception, Is.Null, firstLoad.Error);
            Assert.That(firstLoad.Result.IsSimulation, Is.True);
            AssertManifestLease(first);

            // 刻意不正常 Stop，以真实退出 Play 验证会话失效；不宣称托管程序集已经卸载。
            yield return new ExitPlayMode();
            Assert.That(first.IsAvailable || first.IsSessionActive, Is.False);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            yield return WaitForDrain(first);
            _firstContent.Dispose();
            yield return new EnterPlayMode(false);
            Assert.That(DomainLifetime, Is.SameAs(domainLifetime), "The regression must retain the actual managed domain across both Play sessions.");
            Assert.That(first.IsAvailable, Is.False);
            Assert.Throws<InvalidOperationException>(() => _firstLoader.LoadAsync(Address));
            yield return Initialize(second: true);
            var second = _secondStartup.Resources;
            Assert.That(second, Is.Not.SameAs(first));
            _secondLoader = new HybridCLRLoader(second, "base-test");
            _firstLoader.Dispose();
            _firstLoader = null;
            yield return Stop(_firstStartup);
            Assert.That(second.IsAvailable, Is.True, "Disposing a stale loader must not close the current authority.");
            Assert.That(ResourceSystem.Current, Is.SameAs(second));
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(_secondStartup.CoreProvider));
            var secondLoad = _secondLoader.LoadAsync(Address);
            yield return Wait(() => secondLoad.IsDone, "second real Play code load");
            Assert.That(secondLoad.Exception, Is.Null, secondLoad.Error);
            Assert.That(secondLoad.Result.IsSimulation, Is.True);
            Assert.That(secondLoad.Result.CodePackageId, Is.EqualTo(firstLoad.Result.CodePackageId));
            Assert.That(secondLoad.Result.Assemblies[EntryAssembly], Is.SameAs(firstLoad.Result.Assemblies[EntryAssembly]),
                "A new Resource session does not unload process code.");
            AssertManifestLease(second);
            _secondLoader.Dispose();
            _secondLoader = null;
            yield return Stop(_secondStartup);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator StoppingTheStartupAndDisposingItsLoaderCannotUnbindAReplacementProvider()
        {
            PrepareOwnedScene();
            yield return new EnterPlayMode(false);
            yield return Initialize();
            var resources = _firstStartup.Resources;
            var originalProvider = _firstStartup.CoreProvider;
            _firstLoader = new HybridCLRLoader(resources, "base-test");
            var load = _firstLoader.LoadAsync(Address);
            yield return Wait(() => load.IsDone, "replacement isolation code load");
            Assert.That(load.Exception, Is.Null, load.Error);
            AssertManifestLease(resources);
            _replacementProvider = new WaitingProvider();
            CoreResourceAccess.Bind(_replacementProvider);
            using (var busy = _firstStartup.StopAsync())
            {
                Assert.That(busy.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }
            Assert.That(resources.IsAvailable, Is.False);
            Assert.That(resources.IsSessionActive, Is.True);
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(_replacementProvider));
            _firstLoader.Dispose();
            _firstLoader = null;
            yield return Stop(_firstStartup);
            _firstStartup.Dispose();
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(_replacementProvider));
            Assert.That(CoreResourceAccess.Unbind(originalProvider), Is.False);
            Assert.That(_replacementProvider.SceneCalls, Is.Zero);
            Assert.That(CoreResourceAccess.Unbind(_replacementProvider), Is.True);
            _replacementProvider = null;
            yield return new ExitPlayMode();
        }

        private void PrepareOwnedScene()
        {
            Assert.That(EditorApplication.isPlaying, Is.False);
            _originalScenes = EditorSceneManager.GetSceneManagerSetup();
            if (_originalScenes.Any(s => string.IsNullOrEmpty(s.path)) || Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount)
                .Any(i => UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty))
            {
                throw new InvalidOperationException("Save your own scenes explicitly before this real cross-Play test.");
            }

            HybridCLRTestFixtures.Prepare();
            _ownedFolder = "Assets/Tests/YLFramework/Fixtures/HybridCLRSession-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/Tests/YLFramework/Fixtures", System.IO.Path.GetFileName(_ownedFolder));
            _ownsSceneSetup = true;
            var neutral = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(neutral, _ownedFolder + "/Neutral.unity"), Is.True);
            _wasEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _oldOptions = EditorSettings.enterPlayModeOptions;
            _hasChangedOptions = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorSettings.enterPlayModeOptionsEnabled = true;

        }

        private IEnumerator Initialize(bool second = false)
        {
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(CoreResourceAccess.Provider, Is.Null, "Never replace another composition root's Core binding.");
            var startup = new ResourceStartup(new ResourceConfiguration());
            if (second)
            {
                _secondStartup = startup;
            }
            else
            {
                _firstStartup = startup;
            }
            using var request = startup.StartAsync();
            yield return Wait(() => request.IsDone, "official Addressables session startup");
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            var resources = request.TakeResult().Resources;
            Assert.That(resources.IsAvailable && resources.IsSessionActive, Is.True);
            Assert.That(ResourceSystem.Current, Is.SameAs(resources));
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(startup.CoreProvider));
            var content = new SessionContent();
            if (second)
            {
                _secondContent = content;
            }
            else
            {
                _firstContent = content;
            }
        }

        private static IEnumerator Stop(ResourceStartup startup)
        {
            if (startup == null)
            {
                yield break;
            }
            var resources = startup.Resources;
            using var stop = startup.StopAsync();
            yield return Wait(() => stop.IsDone, "owned session stop");
            Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stop.Error?.ToString());
            Assert.That(stop.TakeResult(), Is.True);
            if (resources == null)
            {
                yield break;
            }
            Assert.That(resources.GetDiagnostics().State, Is.EqualTo(ResourceSystemState.Closed));
            yield return WaitForDrain(resources);
        }

        private static IEnumerator Wait(Func<bool> completed, string operation)
        {
            var deadline = EditorApplication.timeSinceStartup + 15;
            while (!completed() && EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(completed(), Is.True, operation + " timed out.");
        }

        private static IEnumerator WaitForDrain(ResourceSystem resources)
        {
            yield return Wait(() =>
            {
                var ledger = resources.GetDiagnostics();
                return ledger.LeaseCount == 0 && ledger.ScopeCount == 0 && ledger.RequestCount == 0 &&
                    ledger.PendingWorkCount == 0 && ledger.DrainingHandleCount == 0;
            }, "old Resource session drain");
            Assert.That(resources.GetDiagnostics().Errors, Is.Empty);
        }

        private static void AssertManifestLease(ResourceSystem resources)
        {
            var ledger = resources.GetDiagnostics();
            Assert.That(ledger.LeaseCount, Is.EqualTo(1));
            var lease = ledger.Leases.Single();
            Assert.That(lease.Key, Is.EqualTo(ManifestKey));
            Assert.That(lease.Internal || lease.Disposed || lease.Settled, Is.False);
        }

        [UnityTearDown]
        public IEnumerator RestoreOnlyOwnedState()
        {
            if (_ownsSceneSetup && Application.isPlaying)
            {
                yield return new ExitPlayMode();
            }

            try
            {
                _firstLoader?.Dispose();
                _secondLoader?.Dispose();
                _firstLoader = null;
                _secondLoader = null;
                yield return Stop(_firstStartup);
                yield return Stop(_secondStartup);
                _firstStartup = null;
                _secondStartup = null;
                CoreResourceAccess.Unbind(_replacementProvider);
                _replacementProvider = null;
                _firstContent?.Dispose();
                _secondContent?.Dispose();
                _firstContent = null;
                _secondContent = null;
            }
            finally
            {
                if (_hasChangedOptions)
                {
                    EditorSettings.enterPlayModeOptions = _oldOptions;
                    EditorSettings.enterPlayModeOptionsEnabled = _wasEnabled;
                }
                if (_ownsSceneSetup && _originalScenes != null && _originalScenes.Length != 0 && _originalScenes.All(s => !string.IsNullOrEmpty(s.path)))
                {
                    EditorSceneManager.RestoreSceneManagerSetup(_originalScenes);
                }
                else if (_ownsSceneSetup && _originalScenes != null && _originalScenes.Length == 0 && _ownedFolder != null)
                {
                    for (var i = SceneManager.sceneCount - 1; i >= 0; i--)
                    {
                        var scene = SceneManager.GetSceneAt(i);
                        if (!scene.path.StartsWith(_ownedFolder + "/", StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("An unknown scene cannot be closed to restore an originally empty setup.");
                        }
                        Assert.That(EditorSceneManager.CloseScene(scene, true), Is.True);
                    }
                    Assert.That(EditorSceneManager.GetSceneManagerSetup(), Is.Empty);
                }

                if (_ownedFolder != null && AssetDatabase.IsValidFolder(_ownedFolder))
                {
                    AssetDatabase.DeleteAsset(_ownedFolder);
                }
                _ownsSceneSetup = false;
                _hasChangedOptions = false;
                _ownedFolder = null;
                _originalScenes = null;
            }
        }

        private sealed class SessionContent : ResourceProviderBase, IDisposable
        {
            private readonly ResourceLocationMap _locator;
            private int _provides;
            private int _releases;
            private bool _disposed;

            internal SessionContent()
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == EntryAssembly);
                var manifestAsset = UnityEngine.Resources.Load<TextAsset>("YLHybridCLRTests/Good");
                var entry = UnityEngine.Resources.Load<TextAsset>("YLHybridCLRTests/Entry");
                Assert.That(manifestAsset && entry, Is.True, "Run HybridCLRTestFixtures.Prepare before the real Play sessions.");
                var manifest = HybridCLRManifest.Parse(manifestAsset.text);
                Assert.That(manifest.SchemaVersion, Is.EqualTo(2));
                var payload = manifest.HotUpdateAssemblies.Single();
                Assert.That(payload.Identity, Is.EqualTo(assembly.GetName().FullName));
                Assert.That(payload.Mvid, Is.EqualTo(assembly.ManifestModule.ModuleVersionId.ToString("D")));
                Assert.That(HybridCLRMetadata.ReadMvid(entry.bytes), Is.EqualTo(assembly.ManifestModule.ModuleVersionId));
                Assert.That(payload.Length, Is.EqualTo(entry.bytes.LongLength));
                Assert.That(payload.Sha256, Is.EqualTo(HybridCLRManifest.Hash(entry.bytes)));
                m_ProviderId = "hybridclr-session-" + Guid.NewGuid().ToString("N");
                _locator = new ResourceLocationMap(ProviderId);
                Add(ManifestKey, manifestAsset);
                Add("hybridclr-test/" + payload.Address, entry);
                Addressables.ResourceManager.ResourceProviders.Add(this);
                Addressables.AddResourceLocator(_locator);
            }

            private void Add(string key, TextAsset asset) => _locator.Add(key,
                new ResourceLocationBase(key, key, ProviderId, typeof(TextAsset)) { Data = asset });

            public override Type GetDefaultType(IResourceLocation location) => typeof(TextAsset);
            public override void Provide(ProvideHandle handle)
            {
                _provides++;
                handle.SetWaitForCompletionCallback(() => throw new InvalidOperationException("Session tests must not block official operations."));
                var asset = handle.Location.Data as TextAsset;
                handle.Complete(asset, asset != null, asset ? null : new FileNotFoundException(handle.Location.PrimaryKey));
            }

            // Prepared raw Resources assets are borrowed; only official handle ownership is returned here.
            public override void Release(IResourceLocation location, object asset) => _releases++;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                Assert.That(_releases, Is.EqualTo(_provides), "Remove only after the old session has returned every official handle.");
                Addressables.RemoveResourceLocator(_locator);
                Addressables.ResourceManager.ResourceProviders.Remove(this);
                _disposed = true;
            }
        }

        private sealed class WaitingProvider : ICoreResourceProvider
        {
            internal int SceneCalls;
            public CoreResourceRequest<string> ReadTextAsync(string projectId, string address) => throw new NotSupportedException();
            public CorePrefabLease AcquirePrefab(string projectId, string address) => throw new NotSupportedException();
            public CoreResourceRequest<CorePrefabLease> AcquirePrefabAsync(string projectId, string address) => throw new NotSupportedException();
            public CoreResourceRequest<CoreSceneResult> LoadSceneAsync(string projectId, string address, LoadSceneMode mode)
            {
                Assert.That(projectId, Is.EqualTo("hybridclr-test"));
                Assert.That(address, Is.EqualTo("owned-pending-scene"));
                Assert.That(mode, Is.EqualTo(LoadSceneMode.Single));
                SceneCalls++;
                return new WaitingScene();
            }
        }
        private sealed class WaitingScene : CoreResourceRequest<CoreSceneResult>
        {
            private bool _isDisposed;
            public override CoreResourceStatus Status => IsDisposed ? CoreResourceStatus.Canceled : CoreResourceStatus.Pending;
            public override float Progress => 0;
            public override CoreResourceException Error => null;
            public override bool IsDisposed => _isDisposed;
            public override event Action<CoreResourceRequest<CoreSceneResult>> Completed { add { } remove { } }
            public override CoreSceneResult TakeResult() => throw new InvalidOperationException("Deliberately pending test scene.");
            public override void Cancel() => Dispose();
            public override void Dispose() => _isDisposed = true;
        }
    }
}
