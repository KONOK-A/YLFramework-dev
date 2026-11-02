using System;
using System.Collections;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Audio;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed partial class ResourceContentRuntimeTests
    {
        [UnityTest]
        public IEnumerator ProviderReentrantCancelAndExplicitRetryCannotRemoveTheNextBundleGeneration()
        {
            yield return new EnterPlayMode();
            yield return ProviderReentryBody();
        }

        private static IEnumerator ProviderReentryBody()
        {
            using var keys = new TestKeys();
            using var initialize = ResourceSystem.InitializeAsync(Configuration(keys));
            yield return initialize;
            var system = initialize.TakeResult();
            ResourceRequest<AssetLease<GameObject>> original = null;
            ResourceRequest<AssetLease<GameObject>> retry = null;
            var reentered = false;
            keys.OnCopy = () =>
            {
                if (original == null || reentered)
                {
                    return;
                }

                reentered = true;
                original.Cancel();
                retry = system.AcquireAsync<GameObject>(Address("A.prefab"));
            };
            try
            {
                original = system.AcquireAsync<GameObject>(Address("A.prefab"));
                var deadline = Time.realtimeSinceStartup + 8;
                while ((retry == null || !retry.IsDone) && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.That(reentered, Is.True);
                Assert.That(retry?.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), "An old admission callback cannot delete the retry's record/ticket/dependency edges.");
                using var held = retry.TakeResult();
                Assert.That(held.Asset.GetComponent<MeshRenderer>().sharedMaterial.mainTexture, Is.Not.Null);
                Assert.That(Budget(system).ReservedBytes, Is.Zero);
            }
            finally
            {
                keys.OnCopy = null;
                original?.Dispose();
                retry?.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ExplicitRetryDoesNotInheritAnOlderZeroOwnerIoCancellation()
        {
            yield return new EnterPlayMode();
            yield return IoCancellationRetryBody();
        }

        private static IEnumerator IoCancellationRetryBody()
        {
            using var keys = new TestKeys();
            using var initialize = ResourceSystem.InitializeAsync(Configuration(keys));
            yield return initialize;
            var system = initialize.TakeResult();
            ResourceRequest<AssetLease<TextAsset>> old = null;
            ResourceRequest<AssetLease<TextAsset>> retry = null;
            try
            {
                var snapshot = Snapshot();
                var id = snapshot.Document.Assets.Single(x => x.Address == "Cold.bytes").BundleId;
                var records = (IDictionary)typeof(ResourceSystem).GetField("_bundleRecords", Hidden).GetValue(system);
                old = system.AcquireAsync<TextAsset>(Address("Cold.bytes"));
                var record = (ResourceSystem.BundleRecord)records[(snapshot.Document.CatalogDigest, id)];
                var work = record.InputWork;
                Assert.That(work, Is.Not.Null);
                Assert.That(work.Task.IsCompleted, Is.False, "The owned random payload must still be inside pure IO, not native.");
                old.Cancel();
                // 等待已取消的纯IO退出，但不让主线程tick先交割旧结果；不改ThreadPool或暂停native。
                var canceled = Assert.Throws<AggregateException>(() => work.Task.Wait(5000));
                Assert.That(canceled.Flatten().InnerExceptions.Any(x => x is OperationCanceledException), Is.True);
                retry = system.AcquireAsync<TextAsset>(Address("Cold.bytes"));
                var deadline = Time.realtimeSinceStartup + 8;
                while (!retry.IsDone && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.That(retry.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), "A new owner did not request cancellation of the abandoned IO generation.");
                using var held = retry.TakeResult();
                Assert.That(held.Asset.bytes.Length, Is.EqualTo(8 * 1024 * 1024));
                Assert.That(Budget(system).ReservedBytes, Is.Zero);
            }
            finally
            {
                old?.Dispose();
                retry?.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator GroupAssetHandoffRechecksChildrenThatBecameFaultedWhileAnotherChildWasPending()
            => FaultDuringMixedGroup(false);

        [UnityTest]
        public IEnumerator GroupPreloadHandoffRechecksChildrenThatBecameFaultedWhileAnotherChildWasPending()
            => FaultDuringMixedGroup(true);

        private static IEnumerator FaultDuringMixedGroup(bool preload)
        {
            yield return new EnterPlayMode();
            using var keys = new TestKeys();
            using var initialize = ResourceSystem.InitializeAsync(Configuration(keys));
            yield return initialize;
            var system = initialize.TakeResult();
            using var held = system.Acquire<GameObject>(Address("A.prefab"));
            var snapshot = Snapshot();
            var root = snapshot.Document.Assets.Single(x => x.Address == "A.prefab").BundleId;
            var dependency = snapshot.Bundles[root].Dependencies.First();
            using var preloaded = preload ? system.PreloadGroupAsync(snapshot.Document.ProjectId, "Mixed") : null;
            using var acquired = preload ? null : system.AcquireGroupAsync(snapshot.Document.ProjectId, "Mixed");
            Assert.That(preload ? preloaded.IsDone : acquired.IsDone, Is.False, "The independent text input must keep the batch pending after A was ready.");
            LatchOwnedFault(system, snapshot, dependency);
            yield return preload ? (CustomYieldInstruction)preloaded : acquired;
            Assert.That(preload ? preloaded.Status : acquired.Status, Is.EqualTo(ResourceRequestStatus.Failed), "Final batch handoff must recheck its already-completed children.");
            Assert.That(held.Asset, Is.Not.Null, "An existing external lease is not force-destroyed.");
        }

        [UnityTest]
        public IEnumerator AudioProviderRechecksItsLeaseAfterTheTerminalApplicationProgressCallback()
        {
            yield return new EnterPlayMode();
            yield return AudioTerminalProgressBody();
        }

        private static IEnumerator AudioTerminalProgressBody()
        {
            using var keys = new TestKeys();
            using var initialize = ResourceSystem.InitializeAsync(Configuration(keys));
            yield return initialize;
            var system = initialize.TakeResult();
            var snapshot = Snapshot();
            var id = snapshot.Document.Assets.Single(x => x.Address == "Owned.wav").BundleId;
            AudioClipLease received = null;
            Exception failure = null;
            var completed = false;
            var faulted = false;
            try
            {
                system.GetAudioClipProvider(snapshot.Document.ProjectId).LoadAsync("Owned.wav", (lease, error) =>
                {
                    received = lease;
                    failure = error;
                    completed = true;
                }, progress =>
                {
                    if (progress < 1 || faulted)
                    {
                        return;
                    }

                    faulted = true;
                    LatchOwnedFault(system, snapshot, id);
                });
                var deadline = Time.realtimeSinceStartup + 8;
                while (!completed && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.That(completed && faulted, Is.True);
                Assert.That(received, Is.Null);
                Assert.That(failure, Is.TypeOf<ResourceException>());
                Assert.That(((ResourceException)failure).Kind, Is.EqualTo(ResourceError.ContentAuthenticationFailed));
            }
            finally
            {
                received?.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator NoneConfigurationFailuresRetainTheirOriginalDomainClassification()
        {
            yield return new EnterPlayMode();
            var duplicate = new ResourceCatalog("duplicate", Array.Empty<ResourceCatalogEntry>());
            var missingSource = new ResourceCatalog("source", new[] { new ResourceCatalogEntry("item", new ResourceAddress("source", "item"),
                new YLFramework.Resource.ResourceLocation(ResourceSourceId.Resources, "unused", typeof(TextAsset))) });
            foreach (var config in new[] { null, new ResourceConfiguration(new[] { duplicate, duplicate }),
                new ResourceConfiguration(new[] { missingSource }, Array.Empty<IResourceSource>()) })
            {
                using var initialize = ResourceSystem.InitializeAsync(config);
                yield return initialize;
                Assert.That(initialize.Error.Kind, Is.EqualTo(ResourceError.InvalidConfiguration));
            }
        }

        private static void LatchOwnedFault(ResourceSystem system, ResourceLocalSnapshot snapshot, string bundleId)
        {
            using var lease = system.AcquireBundle(snapshot.Document.ProjectId, bundleId);
            var input = (ResourceBundleInput)(object)lease.Record.Input;
            typeof(ResourceDecryptStream).GetMethod("Latch", Hidden).Invoke(input.Stream,
                new object[] { new CryptographicException("owned final-admission fault") });
        }
    }
}
