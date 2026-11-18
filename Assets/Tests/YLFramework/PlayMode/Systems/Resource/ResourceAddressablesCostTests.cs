using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.TestTools;
using YLFramework.Resource;
using Debug = UnityEngine.Debug;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Resource.Addressables.Runtime")]
    public sealed class ResourceAddressablesCostTests
    {
        [Serializable]
        private sealed class WarmAssetEvidence
        {
            public string unity;
            public int iterations;
            public long stopwatchFrequency;
            public long bareAddressablesTicks;
            public long bareAddressablesAllocated;
            public long scopeTicks;
            public long scopeAllocated;
            public long retainedTicks;
            public long retainedAllocated;
        }

        [UnityTest]
        public IEnumerator WarmOwnershipCostsAreMeasuredWithoutClaimingIOOrNativeMemoryWins()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var start = startup.StartAsync();
            yield return start;
            start.TakeResult();
            var resources = startup.Resources;
            var scope = resources.CreateScope("warm-cost-evidence");
            using var loaded = scope.LoadAsync<TextAsset>("ownership/message");
            yield return loaded;
            var text = loaded.TakeResult();
            var evidence = new WarmAssetEvidence { unity = Application.unityVersion, iterations = 200, stopwatchFrequency = Stopwatch.Frequency };
            const int warmup = 10;
            for (int i = 0; i < warmup; i++)
            {
                var bare = Addressables.LoadAssetAsync<TextAsset>("ownership/message");
                Assert.That(bare.IsDone, Is.True);
                Addressables.Release(bare);
                using var local = scope.LoadAsync<TextAsset>("ownership/message");
                local.TakeResult();
                Assert.That(resources.TryAcquireLoaded<TextAsset>("ownership/message", out var extra), Is.True);
                extra.Dispose();
            }
            long allocation = GC.GetAllocatedBytesForCurrentThread();
            long ticks = Stopwatch.GetTimestamp();
            for (int i = 0; i < evidence.iterations; i++)
            {
                var handle = Addressables.LoadAssetAsync<TextAsset>("ownership/message");
                if (!handle.IsDone || handle.Result != text)
                    throw new InvalidOperationException("The measured bare path was not the same ready asset.");
                Addressables.Release(handle);
            }
            evidence.bareAddressablesTicks = Stopwatch.GetTimestamp() - ticks;
            evidence.bareAddressablesAllocated = GC.GetAllocatedBytesForCurrentThread() - allocation;
            allocation = GC.GetAllocatedBytesForCurrentThread();
            ticks = Stopwatch.GetTimestamp();
            for (int i = 0; i < evidence.iterations; i++)
            {
                using var request = scope.LoadAsync<TextAsset>("ownership/message");
                if (request.TakeResult() != text)
                    throw new InvalidOperationException("Scope returned a different object.");
            }
            evidence.scopeTicks = Stopwatch.GetTimestamp() - ticks;
            evidence.scopeAllocated = GC.GetAllocatedBytesForCurrentThread() - allocation;
            allocation = GC.GetAllocatedBytesForCurrentThread();
            ticks = Stopwatch.GetTimestamp();
            for (int i = 0; i < evidence.iterations; i++)
            {
                if (!resources.TryAcquireLoaded<TextAsset>("ownership/message", out var lease))
                    throw new InvalidOperationException("The ready index lost its owner.");
                lease.Dispose();
            }
            evidence.retainedTicks = Stopwatch.GetTimestamp() - ticks;
            evidence.retainedAllocated = GC.GetAllocatedBytesForCurrentThread() - allocation;
            Assert.That(resources.GetDiagnostics().LeaseCount, Is.EqualTo(1));
            Debug.Log("YL.Resource.Cost.WarmAsset " + JsonUtility.ToJson(evidence));
            using var closed = scope.CloseAsync();
            yield return closed;
            closed.TakeResult();
            Assert.That(resources.TryAcquireLoaded<TextAsset>("ownership/message", out _), Is.False);
            using var stopped = startup.StopAsync();
            yield return stopped;
            stopped.TakeResult();
        }

        [UnityTest]
        public IEnumerator DiagnosticsAreDetachedBoundedAndDoNotAddAssetReferences()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(diagnosticHistoryCapacity: 2), bindCoreResourceProvider: false);
            using var start = startup.StartAsync();
            yield return start;
            start.TakeResult();
            var resources = startup.Resources;
            Assert.That(resources.GetDiagnosticsHistory(), Is.Empty);
            var scope = resources.CreateScope("diagnostic-owner");
            using var loaded = scope.LoadAsync<TextAsset>("ownership/message");
            yield return loaded;
            loaded.TakeResult();
            var earlier = resources.GetDiagnostics();
            resources.GetDiagnostics();
            var latest = resources.GetDiagnostics();
            Assert.That(latest.LeaseCount, Is.EqualTo(1));
            Assert.That(latest.Leases[0].Owner, Is.EqualTo("diagnostic-owner"));
            Assert.That(resources.GetDiagnosticsHistory().Count, Is.EqualTo(2));
            using var close = scope.CloseAsync();
            yield return close;
            close.TakeResult();
            Assert.That(resources.GetDiagnostics().LeaseCount, Is.Zero);
            Assert.That(earlier.LeaseCount, Is.EqualTo(1));
            resources.ClearDiagnosticsHistory();
            Assert.That(resources.GetDiagnosticsHistory(), Is.Empty);
            using var stop = startup.StopAsync();
            yield return stop;
            stop.TakeResult();
        }

        [UnityTest]
        public IEnumerator ExplicitDiagnosticsExposeRequestScopePoolSceneAndCleanupWithoutTakingOwnership()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var start = startup.StartAsync();
            yield return start;
            var system = start.TakeResult().Resources;
            using var scope = system.CreateScope("diagnostic-demo");
            using var message = scope.LoadAsync<TextAsset>("ownership/message");
            var pending = system.GetDiagnostics();
            var reader = pending.Requests.Single(row => row.Owner == scope.OwnerLabel);
            Assert.That(reader.Id, Is.Positive);
            Assert.That(reader.Key, Is.EqualTo("ownership/message"));
            Assert.That(reader.Operation, Is.EqualTo(nameof(ResourceScope.LoadAsync)));
            Assert.That(reader.Status, Is.EqualTo(ResourceRequestStatus.Pending));
            yield return message;
            Assert.That(message.TakeResult().text, Is.EqualTo("official addressables content"));
            using var acquiring = system.AcquireAsync<GameObject>("ownership/prefab");
            yield return acquiring;
            using var prefab = acquiring.TakeResult();
            using var pool = system.GetInstanceCache(prefab);
            Assert.That(pool.Prewarm(2), Is.EqualTo(2));
            using var instance = pool.Rent();
            using var loading = scope.LoadSceneAsync("ownership/scene");
            yield return loading;
            var scene = loading.TakeResult();
            var snapshot = system.GetDiagnostics();
            Assert.That(pool.CachedCount, Is.EqualTo(1));
            Assert.That(snapshot.Requests.Any(row => row.Id == reader.Id), Is.False);
            var poolRow = snapshot.Pools.Single(row => row.Key == "ownership/prefab");
            Assert.That(poolRow.IdleCount, Is.EqualTo(1));
            Assert.That(poolRow.ActiveCount, Is.EqualTo(1));
            Assert.That(poolRow.Retired, Is.False);
            Assert.That(snapshot.DrainingHandles, Is.Empty);
            Assert.That(snapshot.Scopes.Single().Owner, Is.EqualTo("diagnostic-demo"));
            Assert.That(scene.isLoaded, Is.True);
            var sceneRow = snapshot.Scenes.Single();
            Assert.That(sceneRow.Key, Is.EqualTo("ownership/scene"));
            Assert.That(sceneRow.Owner, Is.EqualTo(scope.OwnerLabel));
            Assert.That(sceneRow.Loaded, Is.True);
            Assert.That(sceneRow.State, Is.EqualTo("Loaded"));
            Assert.That(sceneRow.Error, Is.Null);
            using var closing = scope.CloseAsync();
            yield return closing;
            Assert.That(closing.TakeResult(), Is.True);
            Assert.That(scene.isLoaded, Is.False);
            instance.Return();
            pool.Dispose();
            prefab.Dispose();
            using var stop = startup.StopAsync();
            yield return stop;
            Assert.That(stop.TakeResult(), Is.True);
            var closed = system.GetDiagnostics();
            Assert.That(closed.LeaseCount, Is.Zero);
            Assert.That(closed.Pools, Is.Empty);
            Assert.That(closed.Scenes, Is.Empty);
            Assert.That(snapshot.Scenes.Single().Loaded, Is.True, "A past snapshot must not follow the current scene lifecycle.");
            Assert.That(system.GetDiagnosticsHistory(), Is.Empty, "Capture without history must not enable background sampling.");
        }
    }
}
