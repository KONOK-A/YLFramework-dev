using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Extensions.Domain
{
    internal sealed class AddressablesDomainResourceFixture
    {
        private readonly List<Object> _assets = new();
        private readonly List<IDisposable> _owners = new();
        private readonly List<Task> _tasks = new();
        private ResourceLocationMap _locator;
        internal readonly string Project = "domain-" + Guid.NewGuid().ToString("N");
        internal ResourceStartup Startup;
        internal ResourceSystem Resources;
        internal ControlledProvider Provider;

        internal IEnumerator Start()
        {
            Assert.That(ResourceSystem.Current, Is.Null, "Never take over another resource authority.");
            Startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var start = Startup.StartAsync();
            yield return Wait(() => start.IsDone, "domain resource startup");
            Assert.That(start.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), start.Error?.ToString());
            Resources = start.TakeResult().Resources;
            Provider = new ControlledProvider(Project);
            _locator = new ResourceLocationMap(Project);
            Addressables.ResourceManager.ResourceProviders.Add(Provider);
            Addressables.AddResourceLocator(_locator);
        }

        internal ResourceAddress Address(string path) => new(Project, path);
        internal string Key(string path) => Project + "/" + path;

        internal T New<T>(string path = null) where T : ScriptableObject
        {
            var asset = OwnAsset(ScriptableObject.CreateInstance<T>());
            if (path != null)
            {
                asset.name = Project + "-" + path;
                Add(path, asset);
            }
            return asset;
        }

        internal T OwnAsset<T>(T asset) where T : Object
        {
            _assets.Add(asset);
            return asset;
        }

        internal void Add(string path, Object asset)
        {
            string key = Key(path);
            _locator.Add(key, new ResourceLocationBase(key, key, Provider.ProviderId, asset.GetType()) { Data = asset });
        }

        internal T Hold<T>(T owner) where T : IDisposable
        {
            _owners.Add(owner);
            return owner;
        }

        internal T Track<T>(T task) where T : Task
        {
            _tasks.Add(task);
            return task;
        }

        internal IEnumerator Close()
        {
            if (Provider != null)
            {
                Provider.Providing = null;
                Provider.Releasing = null;
            }
            foreach (var owner in _owners)
                owner.Dispose();
            _owners.Clear();
            Provider?.FinishPending();
            foreach (var task in _tasks)
            {
                yield return Wait(() => task.IsCompleted, "domain task cleanup");
                _ = task.Exception;
            }
            _tasks.Clear();
            if (Startup != null)
            {
                using var stop = Startup.StopAsync();
                yield return Wait(() => stop.IsDone, "domain resource cleanup");
                Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stop.Error?.ToString());
            }
            if (Resources != null)
            {
                AssertQuiescent(Resources);
                Assert.That(Resources.State, Is.EqualTo(ResourceSystemState.Closed));
            }
            if (_locator != null)
                Addressables.RemoveResourceLocator(_locator);
            if (Provider != null)
                Addressables.ResourceManager.ResourceProviders.Remove(Provider);
            foreach (var asset in _assets)
            {
                if (asset)
                    Object.DestroyImmediate(asset);
            }
            _assets.Clear();
        }

        internal static bool IsQuiescent(ResourceSystem resources)
        {
            var report = resources.GetDiagnostics();
            return report.LeaseCount == 0 && report.ScopeCount == 0 && report.RequestCount == 0 &&
                report.PendingWorkCount == 0 && report.DrainingHandleCount == 0;
        }

        internal static void AssertQuiescent(ResourceSystem resources)
        {
            var report = resources.GetDiagnostics();
            Assert.That(report.LeaseCount, Is.Zero, "A root, instance or independent lease survived.");
            Assert.That(report.ScopeCount, Is.Zero, "A business scope survived.");
            Assert.That(report.RequestCount, Is.Zero, "A request still owns an unclaimed result.");
            Assert.That(report.PendingWorkCount, Is.Zero, "Owned work has not drained.");
            Assert.That(report.DrainingHandleCount, Is.Zero, "An official handle has not drained.");
            Assert.That(report.Errors, Is.Empty);
        }

        internal static IEnumerator Wait(Func<bool> completed, string operation)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            while (!completed() && Time.realtimeSinceStartupAsDouble < deadline)
                yield return null;
            Assert.That(completed(), Is.True, operation + " timed out.");
        }

        internal sealed class ControlledProvider : ResourceProviderBase
        {
            private readonly List<ProvideHandle> _pending = new();
            internal bool Delay;
            internal int Provides;
            internal int Releases;
            internal Action Providing;
            internal Action<IResourceLocation, Object> Releasing;
            internal ControlledProvider(string id) => m_ProviderId = id;

            public override void Provide(ProvideHandle handle)
            {
                Provides++;
                handle.SetWaitForCompletionCallback(() => throw new InvalidOperationException("Consumer tests must not block on cold resources."));
                Providing?.Invoke();
                if (Delay)
                    _pending.Add(handle);
                else
                    handle.Complete((Object)handle.Location.Data, true, null);
            }

            internal void FinishPending()
            {
                var pending = _pending.ToArray();
                _pending.Clear();
                foreach (var handle in pending)
                    handle.Complete((Object)handle.Location.Data, true, null);
            }

            public override void Release(IResourceLocation location, object asset)
            {
                Releases++;
                Releasing?.Invoke(location, (Object)asset);
            }
        }
    }
}
