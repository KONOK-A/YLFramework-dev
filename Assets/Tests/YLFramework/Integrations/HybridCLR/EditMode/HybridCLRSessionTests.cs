using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
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

        [UnityTest]
        public IEnumerator PendingModuleBecomesTerminalWhenTheRealPlaySessionEnds()
        {
            PrepareOwnedScene();
            yield return new EnterPlayMode();
            var resources = Initialize();
            _firstLoader = new HybridCLRLoader(resources, "base-test");
            CoreResourceAccess.Bind(new WaitingProvider());
            var address = new ResourceAddress("hybridclr-test", HybridCLRManifest.ManifestPath);
            var request = _firstLoader.StartModuleAsync(address, "YLFramework.HybridCLR.Tests.PlayMode", "YLFramework.Tests.Integrations.HybridCLR.HybridCLRWaitingModule");
            for (var i = 0; i < 240 && request.Module == null && !request.IsDone; i++)
            {
                yield return null;
            }

            Assert.That(request.Module, Is.Not.Null);
            Assert.That(request.IsDone, Is.False);
            yield return new ExitPlayMode();
            Assert.That(request.IsDone, Is.True, "A stopped Core coroutine must not leave its accepted wrapper pending forever.");
            Assert.That(request.Exception, Is.TypeOf<InvalidOperationException>());
            yield return new EnterPlayMode();
            var next = Initialize();
            _firstLoader.Dispose();
            _firstLoader = null;
            Assert.That(next.IsAvailable, Is.True);
            using var stop = next.ShutdownAsync();
            yield return stop;
            stop.TakeResult();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator DisabledDomainReloadStartsANewResourceSessionAndRejectsTheOldPublicLoader()
        {
            PrepareOwnedScene();
            yield return new EnterPlayMode();
            var first = Initialize();
            _firstLoader = new HybridCLRLoader(first, "base-test");
            var address = new ResourceAddress("hybridclr-test", HybridCLRManifest.ManifestPath);
            var firstLoad = _firstLoader.LoadAsync(address);
            yield return firstLoad;
            Assert.That(firstLoad.Exception, Is.Null, firstLoad.Error);
            Assert.That(firstLoad.Result.IsSimulation, Is.True);
            Assert.That(first.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(1));

            // 刻意不正常 Stop，以真实退出 Play 验证会话失效；不宣称托管程序集已经卸载。
            yield return new ExitPlayMode();
            yield return new EnterPlayMode();
            Assert.That(first.IsAvailable, Is.False);
            Assert.Throws<InvalidOperationException>(() => _firstLoader.LoadAsync(address));
            var second = Initialize();
            _secondLoader = new HybridCLRLoader(second, "base-test");
            _firstLoader.Dispose();
            _firstLoader = null;
            Assert.That(second.IsAvailable, Is.True, "Disposing a stale loader must not close the current authority.");
            var secondLoad = _secondLoader.LoadAsync(address);
            yield return secondLoad;
            Assert.That(secondLoad.Exception, Is.Null, secondLoad.Error);
            Assert.That(secondLoad.Result.IsSimulation, Is.True);
            Assert.That(second.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(1));
            _secondLoader.Dispose();
            _secondLoader = null;
            using var shutdown = second.ShutdownAsync();
            yield return shutdown;
            Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
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
            var neutral = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(neutral, _ownedFolder + "/Neutral.unity"), Is.True);
            _wasEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _oldOptions = EditorSettings.enterPlayModeOptions;
            _hasChangedOptions = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorSettings.enterPlayModeOptionsEnabled = true;

        }

        private static ResourceSystem Initialize()
        {
            Assert.That(ResourceSystem.Current, Is.Null);
            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "YLFramework.HybridCLR.Tests.PlayMode");
            var name = assembly.GetName().Name;
            var entries = new[]
            {
                new ResourceCatalogEntry("manifest", new ResourceAddress("hybridclr-test", HybridCLRManifest.ManifestPath),
                    new ResourceLocation(ResourceSourceId.Resources, "YLHybridCLRTests/Good", typeof(TextAsset))),
                new ResourceCatalogEntry("code", new ResourceAddress("hybridclr-test", HybridCLRManifest.PayloadAddress(name, false)),
                    new ResourceLocation(ResourceSourceId.Resources, "YLHybridCLRTests/Entry", typeof(TextAsset)))
            };
            using var request = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { new ResourceCatalog("hybridclr-test", entries) }));
            return request.TakeResult();
        }

        [UnityTearDown]
        public IEnumerator RestoreOnlyOwnedState()
        {
            if (Application.isPlaying)
            {
                yield return new ExitPlayMode();
            }

            try
            {
                _firstLoader?.Dispose();
                _secondLoader?.Dispose();
            }
            finally
            {
                if (_hasChangedOptions)
                {
                    EditorSettings.enterPlayModeOptions = _oldOptions;
                    EditorSettings.enterPlayModeOptionsEnabled = _wasEnabled;
                }
                if (_originalScenes != null && _originalScenes.Length != 0 && _originalScenes.All(s => !string.IsNullOrEmpty(s.path)))
                {
                    EditorSceneManager.RestoreSceneManagerSetup(_originalScenes);
                }
                else if (_originalScenes != null && _originalScenes.Length == 0 && _ownedFolder != null)
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
            }
        }

        private sealed class WaitingProvider : ICoreResourceProvider
        {
            public CoreResourceRequest<string> ReadTextAsync(string projectId, string address) => throw new NotSupportedException();
            public CorePrefabLease AcquirePrefab(string projectId, string address) => throw new NotSupportedException();
            public CoreResourceRequest<CorePrefabLease> AcquirePrefabAsync(string projectId, string address) => throw new NotSupportedException();
            public CoreResourceRequest<CoreSceneResult> LoadSceneAsync(string projectId, string address, LoadSceneMode mode) => new WaitingScene();
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
