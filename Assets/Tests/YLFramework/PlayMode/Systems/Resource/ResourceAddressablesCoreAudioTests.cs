using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Audio;
using YLFramework.Core;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Resource.Addressables")]
    [Category("YL.Resource.Addressables.Runtime")]
    public sealed class ResourceAddressablesCoreAudioTests
    {
        private readonly List<IDisposable> _held = new();
        private readonly List<Object> _assets = new();
        private ResourceStartup _startup;
        private ResourceSystem _resources;
        private CoreResourceProvider _core;
        private ResourceLocationMap _locator;
        private AssetProvider _provider;
        private string _project;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Assert.That(ResourceSystem.Current, Is.Null, "Never close an external resource authority for this fixture.");
            Assert.That(CoreResourceAccess.Provider, Is.Null, "Never replace an external Core binding.");
            _project = "core-audio-" + Guid.NewGuid().ToString("N");
            _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var start = _startup.StartAsync();
            yield return Wait(() => start.IsDone, "official Addressables startup");
            Assert.That(start.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), start.Error?.ToString());
            _resources = start.TakeResult().Resources;
            _core = _resources.BindCoreResourceProvider();
            _locator = new ResourceLocationMap(_project);
            _provider = new AssetProvider(_project);
            Addressables.ResourceManager.ResourceProviders.Add(_provider);
            Addressables.AddResourceLocator(_locator);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _provider?.FinishPending();
            for (int i = _held.Count - 1; i >= 0; i--)
            {
                _held[i].Dispose();
            }
            _held.Clear();
            if (_startup?.Resources != null)
            {
                using var stop = _startup.StopAsync();
                yield return Wait(() => stop.IsDone, "resource integration cleanup");
                Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stop.Error?.ToString());
            }
            if (_locator != null)
            {
                Addressables.RemoveResourceLocator(_locator);
            }
            if (_provider != null)
            {
                Addressables.ResourceManager.ResourceProviders.Remove(_provider);
            }
            foreach (var asset in _assets)
            {
                if (asset)
                {
                    Object.Destroy(asset);
                }
            }
            _assets.Clear();
            yield return null;
        }

        [UnityTest]
        public IEnumerator SynchronousPrefabAcquisitionOnlyBorrowsLoadedAssetsAndOwnsItsOwnLease()
        {
            var prefab = AddPrefab("view");
            var cold = Assert.Throws<CoreResourceException>(() => _core.AcquirePrefab(_project, "view"));
            Assert.That(cold.Kind, Is.EqualTo(CoreResourceError.InProgress));
            Assert.That(_provider.Provides, Is.Zero, "A synchronous miss must not start or block a producer.");

            using var request = _core.AcquirePrefabAsync(_project, "view");
            yield return Wait(() => request.Status != CoreResourceStatus.Pending, "cold async prefab");
            Assert.That(request.Status, Is.EqualTo(CoreResourceStatus.Succeeded), request.Error?.ToString());
            var first = Hold(request.TakeResult());
            request.Dispose();
            var second = Hold(_core.AcquirePrefab(_project, "view"));
            Assert.That(first.Prefab, Is.SameAs(prefab));
            Assert.That(second.Prefab, Is.SameAs(prefab));

            first.Dispose();
            Assert.That(second.IsValid, Is.True, "Disposing one Core consumer cannot consume another lease.");
            second.Dispose();
            AssertNotLoaded<GameObject>(_project + "/view");
            yield return Wait(() => _provider.Releases == 1, "last prefab reference release");
        }

        [UnityTest]
        public IEnumerator CorePrefabCheckoutKeepsTheExistingRentReuseContract()
        {
            using var request = _core.AcquirePrefabAsync("ownership", "prefab");
            yield return Wait(() => request.Status != CoreResourceStatus.Pending, "reusable prefab");
            Assert.That(request.Status, Is.EqualTo(CoreResourceStatus.Succeeded), request.Error?.ToString());
            var prefab = Hold(request.TakeResult());
            var first = Hold(prefab.Rent(null, SceneManager.GetActiveScene()));
            var instance = first.Instance;
            first.Return();
            prefab.Dispose();
            Assert.That(first.IsValid, Is.False);

            var retained = Hold(_core.AcquirePrefab("ownership", "prefab"));
            var second = Hold(retained.Rent(null, SceneManager.GetActiveScene()));
            Assert.That(second.Instance, Is.SameAs(instance), "Core checkout must use ResourceSystem.Rent, not fresh Instantiate.");
            retained.Dispose();
            Assert.That(second.IsValid, Is.True, "The instance owns its pin independently of the Core prefab lease.");
            second.Discard();
        }

        [UnityTest]
        public IEnumerator DisposingAnUnclaimedCorePrefabRequestReturnsItsResult()
        {
            AddPrefab("unclaimed");
            using var request = _core.AcquirePrefabAsync(_project, "unclaimed");
            yield return Wait(() => request.Status != CoreResourceStatus.Pending, "unclaimed Core prefab");
            Assert.That(request.Status, Is.EqualTo(CoreResourceStatus.Succeeded), request.Error?.ToString());
            request.Dispose();

            AssertNotLoaded<GameObject>(_project + "/unclaimed");
            yield return Wait(() => _provider.Releases == 1, "unclaimed Core result release");
        }

        [UnityTest]
        public IEnumerator CoreSceneRequestDisposalDoesNotUnloadTheApplicationOwnedScene()
        {
            // The active scene can be UTF's injected controller scene; never reload that runner.
            // Exercise the actual built scene and its real Addressables bundle dependencies instead.
            using var request = _core.LoadSceneAsync("ownership", "scene", LoadSceneMode.Additive);
            yield return Wait(() => request.Status != CoreResourceStatus.Pending, "Core application scene");
            Assert.That(request.Status, Is.EqualTo(CoreResourceStatus.Succeeded), request.Error?.ToString());
            var result = request.TakeResult();
            Assert.That(result.HasSceneSideEffects, Is.True);
            var scene = result.Scene;
            request.Dispose();
            CoreResourceAccess.Unbind(_core);
            yield return null;
            yield return null;
            Assert.That(scene.IsValid() && scene.isLoaded, Is.True,
                "The Core result and binding do not own unload; the application scope must retain the SceneLease.");

            using var stop = _startup.StopAsync();
            yield return Wait(() => stop.IsDone, "application scene scope cleanup");
            Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stop.Error?.ToString());
            Assert.That(scene.isLoaded, Is.False, "Stopping Resource closes its application scene scope.");
        }

        [UnityTest]
        public IEnumerator TextCopyAndResourceStopNeverConsumeTheExternalAddressablesReference()
        {
            var text = Add("text", new TextAsset("T6 copied text"));
            var external = Addressables.LoadAssetAsync<TextAsset>(_project + "/text");
            try
            {
                yield return Wait(() => external.IsDone, "external Addressables text");
                Assert.That(external.Status, Is.EqualTo(AsyncOperationStatus.Succeeded));
                using var request = _core.ReadTextAsync(_project, "text");
                yield return Wait(() => request.Status != CoreResourceStatus.Pending, "Core text copy");
                Assert.That(request.Status, Is.EqualTo(CoreResourceStatus.Succeeded), request.Error?.ToString());
                Assert.That(request.TakeResult(), Is.EqualTo("T6 copied text"));
                request.Dispose();
                AssertNotLoaded<TextAsset>(_project + "/text");

                using var stop = _startup.StopAsync();
                yield return Wait(() => stop.IsDone, "stop with an external Addressables owner");
                Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stop.Error?.ToString());
                Assert.That(external.IsValid(), Is.True);
                Assert.That(external.Result, Is.SameAs(text));
                Assert.That(_provider.Releases, Is.Zero);
            }
            finally
            {
                if (external.IsValid())
                {
                    Addressables.Release(external);
                }
            }
            yield return Wait(() => _provider.Releases == 1, "external reference release");
        }

        [UnityTest]
        public IEnumerator AudioProvidersUseProjectNamespacesWithoutRequiringLegacyCatalogs()
        {
            var firstClip = Add("signal", AudioClip.Create("first", 4410, 1, 44100, false));
            var secondClip = AudioClip.Create("second", 4410, 1, 44100, false);
            _assets.Add(secondClip);
            string otherProject = _project + "-other";
            AddLocation(otherProject + "/signal", secondClip);
            var first = _resources.GetAudioClipProvider(_project);
            var second = _resources.GetAudioClipProvider(otherProject);
            Assert.That(_resources.GetAudioClipProvider(_project), Is.SameAs(first));
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(first.ProjectId, Is.EqualTo(_project));
            Assert.That(Assert.Throws<ResourceException>(() => first.Load("signal")).Kind, Is.EqualTo(ResourceError.InProgress));
            Assert.That(_provider.Provides, Is.Zero);

            AudioClipLease firstLease = null;
            AudioClipLease secondLease = null;
            Exception firstError = null;
            Exception secondError = null;
            int deliveries = 0;
            first.LoadAsync("signal", (lease, error) =>
            {
                firstLease = lease == null ? null : Hold(lease);
                firstError = error;
                deliveries++;
            }, null);
            second.LoadAsync("signal", (lease, error) =>
            {
                secondLease = lease == null ? null : Hold(lease);
                secondError = error;
                deliveries++;
            }, null);
            yield return Wait(() => deliveries == 2, "namespaced Audio providers");
            Assert.That(firstError, Is.Null);
            Assert.That(secondError, Is.Null);
            Assert.That(firstLease.Clip, Is.SameAs(firstClip));
            Assert.That(secondLease.Clip, Is.SameAs(secondClip));
            using var hot = first.Load("signal");
            firstLease.Dispose();
            Assert.That(hot.Clip, Is.SameAs(firstClip));
            Assert.That(secondLease.Clip, Is.SameAs(secondClip));
        }

        [UnityTest]
        public IEnumerator AudioWaitersShareTheOfficialProducerButNotTheirReleaseResponsibility()
        {
            var clip = Add("shared", AudioClip.Create("shared", 4410, 1, 44100, false));
            _provider.Delay = true;
            var provider = _resources.GetAudioClipProvider(_project);
            AudioClipLease first = null;
            AudioClipLease second = null;
            Exception failure = null;
            int deliveries = 0;
            provider.LoadAsync("shared", (lease, error) =>
            {
                first = lease == null ? null : Hold(lease);
                failure = error;
                deliveries++;
            }, null);
            provider.LoadAsync("shared", (lease, error) =>
            {
                second = lease == null ? null : Hold(lease);
                failure ??= error;
                deliveries++;
            }, null);
            yield return Wait(() => _provider.Provides == 1, "shared official Audio producer");
            Assert.That(deliveries, Is.Zero);
            Assert.That(Assert.Throws<ResourceException>(() => provider.Load("shared")).Kind, Is.EqualTo(ResourceError.InProgress));
            _provider.FinishPending();
            yield return Wait(() => deliveries == 2, "independent Audio result leases");
            Assert.That(failure, Is.Null);
            first.Dispose();
            Assert.That(second.Clip, Is.SameAs(clip));
            Assert.That(_provider.Releases, Is.Zero);
            second.Dispose();
            yield return Wait(() => _provider.Releases == 1, "last Audio consumer release");
        }

        private GameObject AddPrefab(string path)
        {
            var prefab = new GameObject(path);
            prefab.SetActive(false);
            return Add(path, prefab);
        }

        private T Add<T>(string path, T asset) where T : Object
        {
            _assets.Add(asset);
            AddLocation(_project + "/" + path, asset);
            return asset;
        }

        private void AddLocation(string key, Object asset) => _locator.Add(key,
            new ResourceLocationBase(key, key, _provider.ProviderId, asset.GetType()) { Data = asset });

        private T Hold<T>(T value) where T : IDisposable
        {
            _held.Add(value);
            return value;
        }

        private void AssertNotLoaded<T>(string key) where T : Object
        {
            bool loaded = _resources.TryAcquireLoaded<T>(key, out var unexpected);
            using (unexpected)
            {
                Assert.That(loaded, Is.False, "The adapter must return its temporary or unclaimed lease: " + key);
            }
        }

        private static IEnumerator Wait(Func<bool> completed, string operation)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            while (!completed() && Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
            }
            Assert.That(completed(), Is.True, operation + " timed out.");
        }

        // 仅替换测试资产来源；查址、共享生产、句柄 retain/release 与适配器都使用正式实现。
        private sealed class AssetProvider : ResourceProviderBase
        {
            private readonly List<ProvideHandle> _pending = new();
            internal bool Delay;
            internal int Provides;
            internal int Releases;

            internal AssetProvider(string id) => m_ProviderId = id;

            public override void Provide(ProvideHandle handle)
            {
                Provides++;
                handle.SetWaitForCompletionCallback(() => throw new InvalidOperationException("Synchronous waits are forbidden."));
                if (Delay)
                {
                    _pending.Add(handle);
                }
                else
                {
                    handle.Complete((Object)handle.Location.Data, true, null);
                }
            }

            internal void FinishPending()
            {
                var pending = _pending.ToArray();
                _pending.Clear();
                foreach (var handle in pending)
                {
                    handle.Complete((Object)handle.Location.Data, true, null);
                }
            }

            public override void Release(IResourceLocation location, object asset) => Releases++;
        }
    }
}
