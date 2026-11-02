using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.U2D;
using YLFramework.Audio;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed partial class ResourceContentRuntimeTests
    {
        [UnityTest]
        public IEnumerator StartupScopeCoreAtlasGroupAndSceneUseTheSameFormalInputGraph()
        {
            yield return new EnterPlayMode();
            var snapshot = Snapshot();
            using var keys = new TestKeys();
            var config = ResourceConfiguration.Create(ResourceLoadMode.Bundles, new[] {
                new ResourceProjectMount(snapshot.Document.ProjectId, "StandaloneWindows64", "app-v1", snapshot) },
                Path.Combine(SessionState.GetString(Key + "cache", ""), "s"), decryption: new ResourceDecryptionOptions(keys));
            var startup = new ResourceStartup(config, bindCore: true, audioProjects: new[] { snapshot.Document.ProjectId });
            using var start = startup.StartAsync();
            yield return start;
            Assert.That(start.TakeResult(), Is.SameAs(startup));
            var system = startup.Resources;
            using (var scope = system.CreateScope())
            {
                var request = scope.AcquireAsync<TextAsset>(Address("entry.txt"));
                yield return request;
                Assert.That(scope.TakeResult(request).Asset.text, Does.Contain("first release"));
            }
            using (var text = startup.CoreProvider.ReadTextAsync(snapshot.Document.ProjectId, "entry.txt"))
            {
                yield return text;
                Assert.That(text.TakeResult(), Does.Contain("first release"));
            }
            using (var core = startup.CoreProvider.AcquirePrefabAsync(snapshot.Document.ProjectId, "A.prefab"))
            {
                yield return core;
                using var prefab = core.TakeResult();
                var instance = prefab.Instantiate(null, SceneManager.GetActiveScene());
                Assert.That(instance.Instance.GetComponent<MeshRenderer>().sharedMaterial.mainTexture, Is.Not.Null);
                instance.Discard();
                yield return null;
                Assert.That(instance.IsValid, Is.False);
            }
            using (var atlas = system.Acquire<SpriteAtlas>(Address("Explicit.spriteatlasv2")))
            {
                var clone = atlas.Asset.GetSprite("Left");
                Assert.That(clone, Is.Not.Null);
                Object.Destroy(clone);
                var sprite = snapshot.Document.Assets.First(x => x.Kind == "SubAsset" && x.AssetType == typeof(Sprite).FullName && x.Address == "Sprites.png");
                using var direct = system.Acquire<Sprite>(new ResourceAddress(snapshot.Document.ProjectId, sprite.Address, sprite.SubAssetKey));
                Assert.That(direct.Asset.texture, Is.Not.Null);
            }
            var sceneCount = SceneManager.sceneCount;
            using (var group = system.PreloadGroupAsync(snapshot.Document.ProjectId, "Owned"))
            {
                yield return group;
                using var held = group.TakeResult();
                Assert.That(SceneManager.sceneCount, Is.EqualTo(sceneCount), "Group preloading cannot secretly load its Scene.");
            }
            using var sceneRequest = system.LoadSceneAsync(Address("Owned.unity"), LoadSceneMode.Additive);
            yield return sceneRequest;
            var sceneLease = sceneRequest.TakeResult();
            var record = sceneLease.Record;
            Assert.That(sceneLease.Scene.isLoaded, Is.True);
            Assert.That(sceneLease.Scene.GetRootGameObjects().Single().GetComponent<MeshRenderer>().sharedMaterial.mainTexture, Is.Not.Null);
            var bytes = Budget(system).AllocatedBytes;
            sceneLease.Dispose();
            Assert.That(Budget(system).AllocatedBytes, Is.EqualTo(bytes), "Returning SceneLease does not unload the Scene or release its inputs.");
            using (var busy = startup.StopAsync())
            {
                yield return busy;
                Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }
            using var unload = system.UnloadSceneAsync(record);
            yield return unload;
            Assert.That(unload.TakeResult(), Is.True);
            yield return null;
            using var stop = startup.StopAsync();
            yield return stop;
            Assert.That(stop.TakeResult(), Is.True);
            Assert.That(Budget(system).AllocatedBytes + Budget(system).ReservedBytes, Is.Zero);
            Assert.That(startup.Resources, Is.Null);
        }

        [UnityTest]
        public IEnumerator NamedAudioTailRetainsItsOwnEncryptedInputUntilTheRealTransitionEnds()
        {
            yield return new EnterPlayMode();
            using var keys = new TestKeys();
            using var initialize = ResourceSystem.InitializeAsync(Configuration(keys));
            yield return initialize;
            var system = initialize.TakeResult();
            var provider = system.GetAudioClipProvider(Snapshot().Document.ProjectId);
            using var music = AudioKit.PlayMusicAsync(new AudioClipAddress(provider, "Owned.wav"));
            var deadline = Time.realtimeSinceStartup + 10;
            while (!music.HasStarted && music.State != AudioPlayState.Ended && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(music.Error, Is.Null);
            Assert.That(music.HasStarted, Is.True);
            using var next = AudioKit.CrossFadeMusicAsync(new AudioClipAddress(provider, "Next.wav"), 0.25f);
            while (!next.HasStarted && next.State != AudioPlayState.Ended && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(next.HasStarted, Is.True);
            Assert.That(music.Handle.IsValid, Is.True, "The tail still owns its original source and clip lease.");
            Assert.That(Budget(system).LiveInputs, Is.EqualTo(2));
            while (music.Handle.IsValid && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(music.Handle.IsValid, Is.False);
            yield return null;
            Assert.That(Budget(system).LiveInputs, Is.EqualTo(1));
            AudioKit.Shutdown();
            yield return null;
            Assert.That(Budget(system).AllocatedBytes + Budget(system).ReservedBytes, Is.Zero);
            using var shutdown = system.ShutdownAsync();
            yield return shutdown;
            Assert.That(shutdown.TakeResult(), Is.True);
        }

        [UnityTest]
        public IEnumerator PrepareActivateAndRollbackAuthenticateBeforeTheirOriginalCommitBoundaries()
        {
            yield return new EnterPlayMode();
            yield return TransactionBody();
        }

        private static IEnumerator TransactionBody()
        {
            var current = Snapshot();
            var candidate = Snapshot(true);
            var root = Path.Combine(SessionState.GetString(Key + "cache", ""), "t");
            var store = new ArtifactStore(root, new[] { new ResourceProjectMount(current.Document.ProjectId, "StandaloneWindows64", "app-v1", current) });
            using var keys = new TestKeys();
            using var initialize = ResourceSystem.InitializeAsync(new ResourceConfiguration(store, decryption: new ResourceDecryptionOptions(keys)));
            yield return initialize;
            var system = initialize.TakeResult();
            var copies = keys.Copies;
            using var check = system.CheckUpdatesAsync(new[] { candidate });
            yield return check;
            var plan = check.TakeResult();
            ArtifactStoreReport report = null;
            yield return store.QueryAsync(value => report = value);
            Assert.That(report, Is.Not.Null);
            Assert.That(keys.Copies, Is.EqualTo(copies), "Check/E05 remain key-free, decryption-free queries.");
            keys.Missing = true;
            using (var rejected = system.PrepareUpdateAsync(plan))
            {
                yield return rejected;
                Assert.That(rejected.Error.Kind, Is.EqualTo(ResourceError.ContentKeyUnavailable));
            }
            Assert.That(store.GetPreparedUpdates(), Is.Empty);
            Assert.That(system.GetActiveCatalogDigest(current.Document.ProjectId), Is.EqualTo(current.Document.CatalogDigest));
            keys.Missing = false;
            using var prepare = system.PrepareUpdateAsync(plan);
            yield return prepare;
            var prepared = prepare.TakeResult();
            using var activate = system.ActivatePreparedUpdateAsync(prepared);
            yield return activate;
            Assert.That(activate.TakeResult(), Is.True);
            using (var held = system.Acquire<TextAsset>(Address("entry.txt")))
            {
                Assert.That(held.Asset.text, Is.EqualTo("second formal encrypted version"));
            }

            keys.Missing = true;
            using (var rejected = system.RollbackAsync())
            {
                yield return rejected;
                Assert.That(rejected.Error.Kind, Is.EqualTo(ResourceError.ContentKeyUnavailable));
            }
            Assert.That(system.GetActiveCatalogDigest(current.Document.ProjectId), Is.EqualTo(candidate.Document.CatalogDigest));
            Assert.That(store.Mount().Single().Document.CatalogDigest, Is.EqualTo(candidate.Document.CatalogDigest), "Recovery and Mount remain independent of content keys.");
            keys.Missing = false;
            using var rollback = system.RollbackAsync();
            yield return rollback;
            Assert.That(rollback.TakeResult(), Is.True);
            using (var held = system.Acquire<TextAsset>(Address("entry.txt")))
            {
                Assert.That(held.Asset.text, Does.Contain("first release"));
            }
        }

        [UnityTest]
        public IEnumerator CancelAndShutdownObservePureIoBeforeAllowingTheNextSubject()
        {
            yield return new EnterPlayMode();
            using var keys = new TestKeys();
            var config = Configuration(keys);
            using var initialize = ResourceSystem.InitializeAsync(config);
            yield return initialize;
            var first = initialize.TakeResult();
            using var request = first.AcquireAsync<GameObject>(Address("A.prefab"));
            request.Cancel();
            using var shutdown = first.ShutdownAsync();
            yield return shutdown;
            Assert.That(shutdown.TakeResult(), Is.True);
            Assert.That(Budget(first).AllocatedBytes + Budget(first).ReservedBytes, Is.Zero);
            using var restart = ResourceSystem.InitializeAsync(config);
            yield return restart;
            var second = restart.TakeResult();
            Assert.That(second, Is.Not.SameAs(first));
            using var ready = second.Acquire<TextAsset>(Address("entry.txt"));
            yield return null;
            Assert.That(ResourceSystem.Current, Is.SameAs(second));
            Assert.That(ready.Asset.text, Does.Contain("first release"));
        }
    }
}
