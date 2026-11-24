#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.SceneManagement;
using YLFramework.Resource;

namespace YLFramework.Tests.Resource
{
    public sealed partial class ResourceAddressablesOwnershipTests
    {
        [UnityTest]
        public IEnumerator EarlyScopeAndImageRejectionsKeepOnlyTheirTerminalEvent()
        {
            using var startup = new ResourceStartup(bindCoreResourceProvider: false);
            using var starting = startup.StartAsync();
            yield return Wait(starting);
            var system = starting.TakeResult().Resources;
            using var scope = system.CreateScope("rejected-owner");
            ResourceEventHistory.SetRecording(true);
            var before = system.GetDiagnostics();
            using var empty = scope.LoadAsync<TextAsset>(string.Empty);
            using var image = system.AcquireImageAsync("invalid-relative-image");
            Assert.That(empty.Error.Kind, Is.EqualTo(ResourceError.LoadFailed));
            Assert.That(image.Error.Kind, Is.EqualTo(ResourceError.InvalidAddress));
            var destroyedParent = new GameObject("rejected-parent").transform;
            UnityEngine.Object.DestroyImmediate(destroyedParent.gameObject);
            using var instance = scope.InstantiateAsync("ownership/prefab", destroyedParent);
            using var rent = scope.RentAsync("ownership/prefab", destroyedParent);
            using var scene = scope.LoadSceneAsync("ownership/scene", (LoadSceneMode)(-1));
            Assert.That(instance.Error.Kind, Is.EqualTo(ResourceError.LoadFailed));
            Assert.That(rent.Error.Kind, Is.EqualTo(ResourceError.LoadFailed));
            Assert.That(scene.Error.Kind, Is.EqualTo(ResourceError.InvalidConfiguration));
            var after = system.GetDiagnostics();
            Assert.That(after.RequestCount, Is.EqualTo(before.RequestCount));
            Assert.That(after.PendingWorkCount, Is.EqualTo(before.PendingWorkCount));
            empty.Dispose();
            image.Dispose();
            instance.Dispose();
            rent.Dispose();
            scene.Dispose();
            var rows = ResourceEventHistory.Read();
            Assert.That(rows.All(row => row.Kind == ResourceEventKind.Terminal && row.Status == ResourceRequestStatus.Failed &&
                row.DiagnosticId == 0 && row.WorkId == 0), Is.True);
            Assert.That(rows.Select(row => row.Source), Is.EqualTo(new[] { ResourceEventSource.ScopeLoad, ResourceEventSource.Image,
                ResourceEventSource.Instantiate, ResourceEventSource.ScopeInstantiate, ResourceEventSource.Rent, ResourceEventSource.ScopeRent,
                ResourceEventSource.Scene, ResourceEventSource.ScopeScene }));
            Assert.That(rows.Select(row => row.Error), Is.EqualTo(new ResourceError?[] { empty.Error.Kind, image.Error.Kind,
                instance.Error.Kind, instance.Error.Kind, rent.Error.Kind, rent.Error.Kind, scene.Error.Kind, scene.Error.Kind }));
            foreach (var inner in new[] { 2, 4, 6 })
                Assert.That(rows[inner + 1].LoadId, Is.EqualTo(rows[inner].RequestId));
            Assert.That(rows[0].KeyFingerprint, Is.Zero);
            Assert.That(rows[1].KeyFingerprint, Is.Not.Zero);
            using var stop = startup.StopAsync();
            yield return Wait(stop);
            Assert.That(stop.TakeResult(), Is.True);
        }

        [UnityTest]
        public IEnumerator TwoScopeWaitersKeepIndependentIdsAndShareOneLocateAndProvide()
        {
            using var startup = new ResourceStartup(bindCoreResourceProvider: false);
            using var starting = startup.StartAsync();
            yield return Wait(starting);
            var system = starting.TakeResult().Resources;
            using var scope = system.CreateScope("owner-of-two");
            using var provider = new DeferredBatchProvider();
            ResourceEventHistory.SetRecording(true);
            using (var canceled = scope.LoadAsync<TextAsset>(provider.Key, new System.Threading.CancellationToken(true)))
            {
                Assert.That(canceled.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
                var only = ResourceEventHistory.Read().Single();
                Assert.That(only.Source, Is.EqualTo(ResourceEventSource.ScopeLoad));
                Assert.That(only.LoadId, Is.Zero);
                Assert.That(provider.Provides, Is.Zero);
            }
            ResourceEventHistory.Clear();
            using var first = scope.LoadAsync<TextAsset>(provider.Key);
            using var second = scope.LoadAsync<TextAsset>(provider.Key);
            yield return Wait(() => provider.Provides == 1);
            first.Cancel();
            provider.Complete();
            yield return Wait(second);
            Assert.That(second.TakeResult(), Is.SameAs(provider.Asset));
            using var reused = scope.LoadAsync<TextAsset>(provider.Key);
            Assert.That(reused.TakeResult(), Is.SameAs(provider.Asset));
            using var close = scope.CloseAsync();
            yield return Wait(close);
            Assert.That(close.TakeResult(), Is.True);
            Assert.That(provider.Releases, Is.EqualTo(1));
            var rows = ResourceEventHistory.Read();
            var waits = rows.Where(row => row.Source == ResourceEventSource.ScopeLoad && row.Kind == ResourceEventKind.Terminal).ToArray();
            Assert.That(waits.Select(row => row.Status), Is.EqualTo(new[] { ResourceRequestStatus.Canceled, ResourceRequestStatus.Succeeded, ResourceRequestStatus.Succeeded }));
            Assert.That(waits.Select(row => row.RequestId).Distinct().Count(), Is.EqualTo(3));
            var load = rows.Single(row => row.Source == ResourceEventSource.ScopeAcquire && row.Kind == ResourceEventKind.Terminal);
            Assert.That(waits.Select(row => row.LoadId), Is.All.EqualTo(load.RequestId));
            var work = rows.Where(row => row.Kind == ResourceEventKind.Work).ToArray();
            Assert.That(work.Select(row => row.Phase), Is.EquivalentTo(new[] { ResourceEventPhase.Locate, ResourceEventPhase.Provide }));
            Assert.That(work.Select(row => row.WorkId).Distinct().Count(), Is.EqualTo(2));
            Assert.That(work.Select(row => row.RequestId), Is.All.EqualTo(load.RequestId));
            using var stop = startup.StopAsync();
            yield return Wait(stop);
            Assert.That(stop.TakeResult(), Is.True);
        }

        [UnityTest]
        public IEnumerator AbandonedAssetRecordsBothCompletionOrdersAndEachOfficialResult()
        {
            using var startup = new ResourceStartup(bindCoreResourceProvider: false);
            using var starting = startup.StartAsync();
            yield return Wait(starting);
            var system = starting.TakeResult().Resources;
            ResourceEventHistory.SetRecording(true);
            foreach (bool drainFirst in new[] { false, true })
            {
                foreach (bool fail in new[] { false, true })
                {
                    yield return VerifyAbandoned(system, drainFirst, fail, releaseFault: false);
                }
            }
            yield return VerifyAbandoned(system, drainFirst: false, fail: false, releaseFault: true);
            using var stop = startup.StopAsync();
            yield return Wait(stop);
            Assert.That(stop.Error?.ToString(), Does.Contain("history-last-release"));
        }

        private static IEnumerator VerifyAbandoned(ResourceSystem system, bool drainFirst, bool fail, bool releaseFault)
        {
            ResourceEventHistory.Clear();
            using var provider = new DeferredBatchProvider();
            var scope = system.CreateScope("last scope waiter");
            using var waiting = scope.LoadAsync<TextAsset>(provider.Key);
            yield return Wait(() => provider.Provides == 1);
            waiting.Cancel();
            if (drainFirst)
            {
                yield return Wait(() => system.GetDiagnostics().DrainingHandles.Any(row => row.Key == provider.Key));
            }
            if (releaseFault)
            {
                provider.ReleaseFault = new InvalidOperationException("history-last-release");
                LogAssert.Expect(LogType.Exception, new Regex("history-last-release"));
            }
            if (fail)
            {
                LogAssert.Expect(LogType.Error, new Regex("history-content-failure"));
            }
            provider.Complete(fail ? new InvalidOperationException("history-content-failure") : null);
            using var close = scope.CloseAsync();
            yield return Wait(close);
            if (releaseFault)
            {
                Assert.That(close.Error?.ToString(), Does.Contain("history-last-release"));
            }
            else
            {
                Assert.That(close.TakeResult(), Is.True);
            }
            var rows = ResourceEventHistory.Read();
            var consumer = rows.Single(row => row.Source == ResourceEventSource.ScopeLoad && row.Kind == ResourceEventKind.Terminal);
            Assert.That(consumer.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(consumer.Role, Is.EqualTo(ResourceEventRole.Caller));
            var provide = rows.Where(row => row.Phase == ResourceEventPhase.Provide).ToArray();
            Assert.That(provide.Select(row => row.Kind), Is.EqualTo(new[] { ResourceEventKind.Work, ResourceEventKind.Release }));
            Assert.That(provide.Select(row => row.RequestId), Is.All.EqualTo(consumer.LoadId));
            Assert.That(provide[0].WorkId, Is.Not.Zero.And.EqualTo(provide[1].WorkId));
            Assert.That(provide.Select(row => row.Late), Is.All.True);
            Assert.That(provide[0].Status, Is.EqualTo(fail ? ResourceRequestStatus.Failed : ResourceRequestStatus.Succeeded));
            Assert.That(provide[1].Status, Is.EqualTo(releaseFault ? ResourceRequestStatus.Failed : ResourceRequestStatus.Succeeded));
            Assert.That(provider.Releases, Is.EqualTo(fail ? 0 : 1));
        }
    }
}
#endif
