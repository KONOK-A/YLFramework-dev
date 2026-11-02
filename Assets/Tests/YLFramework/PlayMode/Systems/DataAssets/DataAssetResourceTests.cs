using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.DataAssets;
using YLFramework.Resource;
using Object = UnityEngine.Object;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.DataAssets
{
    public sealed class DataAssetSingletonFixture : SingletonScriptableObjectConfigs<DataAssetSingletonFixture>
    {
    }

    public class DataAssetResourceTests
    {
        private sealed class Operation : ResourceSourceOperation
        {
            public void Finish(Object asset, Exception error = null) => Complete(asset, error);
        }

        private sealed class Source : IResourceSource
        {
            public ResourceSourceId Id => new ResourceSourceId("data-assets-test");
            public bool SupportsSynchronousLoad => true;
            internal Object Asset;
            internal bool Immediate;
            internal Action OnLoad;
            internal int Releases;
            internal readonly List<Operation> Operations = new List<Operation>();
            public void Validate(ResourceCatalogEntry entry)
            {
            }
            public ResourceSourceResult Load(ResourceCatalogEntry entry, Type type)
            {
                OnLoad?.Invoke();
                return new ResourceSourceResult(Asset);
            }
            public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type)
            {
                OnLoad?.Invoke();
                var operation = new Operation();
                Operations.Add(operation);
                if (Immediate)
                {
                    operation.Finish(Asset);
                }

                return operation;
            }
            public void Release(Object asset) => Releases++;
        }

        private Source _source;
        private ScriptableObjectConfigs _root;
        private ScriptableObjectData _child;
        private ResourceSystem _resource;
        private readonly List<IDisposable> _owned = new List<IDisposable>();
        private readonly ResourceAddress _address = new ResourceAddress("data-assets", "items");

        [SetUp]
        public void SetUp()
        {
            Assert.That(ResourceSystem.Current, Is.Null, "Do not take over another Resource authority.");
            Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero, "Do not clear another owner's registrations.");
            _root = ScriptableObject.CreateInstance<ScriptableObjectConfigs>();
            _root.name = "DataAssets-Owned-Items";
            _child = ScriptableObject.CreateInstance<ScriptableObjectData>();
            _child.id = 17;
            _child.configName = "Potion";
            _child.parent = _root;
            _root.configs.Add(_child);
            _source = new Source { Asset = _root };
        }

        private ResourceSystem Initialize(Type type = null)
        {
            var config = new ResourceConfiguration(new[] { new ResourceCatalog("data-assets", new[]
            {
                new ResourceCatalogEntry("items", _address, new ResourceLocation(_source.Id, "controlled", type ?? typeof(ScriptableObjectConfigs)))
            }) }, new IResourceSource[] { _source });
            using var initialized = ResourceSystem.InitializeAsync(config);
            Assert.That(initialized.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), initialized.Error?.ToString());
            return _resource = initialized.TakeResult();
        }

        private T Own<T>(T value) where T : IDisposable
        {
            _owned.Add(value);
            return value;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var owner in _owned)
            {
                owner.Dispose();
            }

            _owned.Clear();
            DataAssetSingletonFixture.Release();
            foreach (var operation in _source.Operations)
            {
                if (!operation.IsDone)
                {
                    operation.Finish(_source.Asset);
                }
            }

            if (_resource != null && _resource.State == ResourceSystemState.Ready)
            {
                using var shutdown = _resource.ShutdownAsync();
                yield return shutdown;
                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
            }
            if (_source.Asset && !ReferenceEquals(_source.Asset, _root))
            {
                Object.DestroyImmediate(_source.Asset);
            }

            Object.DestroyImmediate(_child);
            Object.DestroyImmediate(_root);
            Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero);
        }

        [Test]
        public void TwoOwnersKeepRootAndChildAliveUntilLastLeaseReturns()
        {
            _source.Immediate = true;
            var system = Initialize();
            var first = Own(ScriptableObjectManager.LoadAsync(system, _address));
            var second = Own(ScriptableObjectManager.LoadAsync(system, _address));
            Assert.That(first.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            Assert.That(ScriptableObjectManager.GetConfig(_root.name).GetScriptableObjectData(17), Is.SameAs(_child));
            Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(2));
            first.Dispose();
            Assert.That(second.Asset, Is.SameAs(_root));
            Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.SameAs(_root));
            Assert.That(_source.Releases, Is.Zero);
            second.Dispose();
            Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.Null);
            Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            Assert.That(_source.Releases, Is.EqualTo(1));
        }

        [Test]
        public void ClosingBeforeCompletionCannotPublishLateRoot()
        {
            var system = Initialize();
            var load = Own(ScriptableObjectManager.LoadAsync(system, _address));
            load.Dispose();
            Assert.That(load.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            _source.Operations[0].Finish(_root);
            Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.Null);
            Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            Assert.That(_source.Releases, Is.EqualTo(1));
        }

        [Test]
        public void ReentrantCloseDuringAsyncStartReleasesUnclaimedImmediateResult()
        {
            _source.Immediate = true;
            _source.OnLoad = ScriptableObjectManager.Clear;
            var system = Initialize();
            var load = Own(ScriptableObjectManager.LoadAsync(system, _address));
            Assert.That(load.IsDisposed, Is.True);
            Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.Null);
            Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            Assert.That(_source.Releases, Is.EqualTo(1));
        }

        [Test]
        public void ReentrantCloseDuringSynchronousLoadCannotRegisterAfterClear()
        {
            _source.OnLoad = ScriptableObjectManager.Clear;
            var system = Initialize();
            Assert.Throws<OperationCanceledException>(() => ScriptableObjectManager.Load(system, _address));
            Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.Null);
            Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            Assert.That(_source.Releases, Is.EqualTo(1));
        }

        [Test]
        public void DuplicateRootNameFailureReleasesOnlyRejectedLoad()
        {
            var original = ScriptableObject.CreateInstance<ScriptableObjectConfigs>();
            original.name = _root.name;
            try
            {
                using var registered = ScriptableObjectManager.AddConfig(original);
                _source.Immediate = true;
                var system = Initialize();
                var rejected = Own(ScriptableObjectManager.LoadAsync(system, _address));
                Assert.That(rejected.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(rejected.Error, Is.InstanceOf<InvalidOperationException>());
                Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.SameAs(original));
                Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
                Assert.That(_source.Releases, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(original);
            }
        }

        [Test]
        public void RemovingRenamedRootByReferenceReleasesOriginalRegistrationAndLease()
        {
            var system = Initialize();
            var owner = Own(ScriptableObjectManager.Load(system, _address));
            _root.name = "Renamed";
            ScriptableObjectManager.RemoveConfig(_root);
            Assert.That(owner.IsDisposed, Is.True);
            Assert.That(ScriptableObjectManager.GetConfig("DataAssets-Owned-Items"), Is.Null);
            Assert.That(_source.Releases, Is.EqualTo(1));
        }

        [Test]
        public void HashRemovalClosesBothOwnersOfResolvedRootButNotOtherRoots()
        {
            var otherRoot = ScriptableObject.CreateInstance<ScriptableObjectConfigs>();
            otherRoot.name = "DataAssets-Owned-Other";
            try
            {
                var first = Own(ScriptableObjectManager.AddConfig(_root));
                var second = Own(ScriptableObjectManager.AddConfig(_root));
                using var other = ScriptableObjectManager.AddConfig(otherRoot);
                var hash = ScriptableObjectManager.StringToHash(_root.name);
                Assert.That(hash, Is.Not.EqualTo(ScriptableObjectManager.StringToHash(otherRoot.name)), "Fixture roots must have distinct hashes.");
                ScriptableObjectManager.RemoveConfig(hash);
                Assert.That(first.IsDisposed && second.IsDisposed, Is.True);
                Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.Null);
                Assert.That(other.Config, Is.SameAs(otherRoot));
                Assert.That(ScriptableObjectManager.RegistrationCount, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(otherRoot);
            }
        }

        [Test]
        public void ClearCancelsPendingSingletonAndAllowsExplicitReload()
        {
            _source.Asset = ScriptableObject.CreateInstance<DataAssetSingletonFixture>();
            var system = Initialize(typeof(DataAssetSingletonFixture));
            var pending = Own(DataAssetSingletonFixture.LoadAsync(system, _address));
            Assert.Throws<InvalidOperationException>(() => DataAssetSingletonFixture.LoadAsync(system, _address));
            ScriptableObjectManager.Clear();
            _source.Operations[0].Finish(_source.Asset);
            Assert.That(pending.IsDisposed, Is.True);
            Assert.That(DataAssetSingletonFixture.Instance, Is.Null);
            _source.Immediate = true;
            var replacement = Own(DataAssetSingletonFixture.LoadAsync(system, _address));
            Assert.That(DataAssetSingletonFixture.Instance, Is.SameAs(_source.Asset));
            DataAssetSingletonFixture.Release();
            Assert.That(replacement.IsDisposed, Is.True);
            Assert.That(DataAssetSingletonFixture.Instance, Is.Null);
            Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
        }

        [Test]
        public void ExplicitConsecutiveResourceSessionsDoNotReuseDisposedRegistrations()
        {
            var first = Initialize();
            var firstOwner = Own(ScriptableObjectManager.Load(first, _address));
            firstOwner.Dispose();
            using (var shutdown = first.ShutdownAsync())
            {
                Assert.That(shutdown.TakeResult(), Is.True);
            }

            var second = Initialize();
            using var secondOwner = ScriptableObjectManager.Load(second, _address);
            Assert.That(secondOwner.Config, Is.SameAs(_root));
            firstOwner.Dispose();
            Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.SameAs(_root));
        }
    }
}
