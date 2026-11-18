using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.DataAssets;
using YLFramework.Resource;
using YLFramework.Tests.Extensions.Domain;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.DataAssets
{
    public sealed class DataAssetSingletonFixture : SingletonScriptableObjectConfigs<DataAssetSingletonFixture>
    {
    }

    [Category("YL.Resource.Addressables.Regression")]
    public class DataAssetResourceTests
    {
        private AddressablesDomainResourceFixture _fixture;
        private bool _ownsDataAssets;
        private ScriptableObjectConfigs _root;
        private ScriptableObjectData _child;
        private ResourceAddress Address => _fixture.Address("items");
        private ResourceSystem Resources => _fixture.Resources;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _ownsDataAssets = false;
            _fixture = null;
            Assert.That(ResourceSystem.Current, Is.Null, "Do not take over another resource authority.");
            Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero, "Do not clear another owner's registrations.");
            Assert.That(DataAssetSingletonFixture.Instance, Is.Null);
            _ownsDataAssets = true;
            _fixture = new AddressablesDomainResourceFixture();
            yield return _fixture.Start();
            _root = _fixture.New<ScriptableObjectConfigs>("items");
            _root.name = "DataAssets-Owned-Items";
            _child = _fixture.New<ScriptableObjectData>();
            _child.id = 17;
            _child.configName = "Potion";
            _child.parent = _root;
            _root.configs.Add(_child);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_ownsDataAssets)
                yield break;
            _ownsDataAssets = false;
            DataAssetSingletonFixture.Release();
            if (_fixture != null)
                yield return _fixture.Close();
            Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero);
        }

        [Test]
        public void DestroyedBorrowedRowInvalidatesTheCurrentIndexWithoutClosingItsRoot()
        {
            using var owner = ScriptableObjectManager.AddConfig(_root);
            Assert.That(owner.Config.GetScriptableObjectData(17), Is.SameAs(_child));
            Object.DestroyImmediate(_child);
            Assert.That(ReferenceEquals(_child, null), Is.False, "The test must retain a Unity fake-null wrapper.");
            Assert.That(_child == null, Is.True);
            Assert.That(owner.Config.GetScriptableObjectData(17), Is.Null);
            Assert.That(owner.Config.GetName(17), Is.Null);
            Assert.That(owner.Config.TryGetIdRange(out _, out _), Is.False);
            Assert.That(ScriptableObjectManager.RegistrationCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator DestroyedLoadedRootIsPrunedAndOnlyItsOwnedLeaseIsReturned()
        {
            var preload = _fixture.Hold(ScriptableObjectManager.LoadAsync(Resources, Address));
            yield return AddressablesDomainResourceFixture.Wait(() => preload.IsDone, "root preload");
            Assert.That(preload.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), preload.Error?.ToString());
            var name = _root.name;
            var owner = _fixture.Hold(ScriptableObjectManager.Load(Resources, Address));
            preload.Dispose();
            Assert.That(Resources.GetDiagnostics().LeaseCount, Is.EqualTo(1));
            Object.DestroyImmediate(_root);
            Assert.That(ReferenceEquals(_root, null), Is.False);
            Assert.That(owner.IsValid, Is.False);
            Assert.Throws<InvalidOperationException>(() => _ = owner.Config);
            Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero);
            Assert.That(ScriptableObjectManager.GetConfig(name), Is.Null);
            Assert.That(owner.IsDisposed, Is.True);
            Assert.That(Resources.GetDiagnostics().LeaseCount, Is.Zero);
            yield return AddressablesDomainResourceFixture.Wait(() => _fixture.Provider.Releases == 1, "destroyed root lease release");
        }

        [UnityTest]
        public IEnumerator TwoOwnersKeepRootAndChildAliveUntilLastLeaseReturns()
        {
            var first = _fixture.Hold(ScriptableObjectManager.LoadAsync(Resources, Address));
            var second = _fixture.Hold(ScriptableObjectManager.LoadAsync(Resources, Address));
            yield return AddressablesDomainResourceFixture.Wait(() => first.IsDone && second.IsDone, "independent root owners");
            Assert.That(first.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), first.Error?.ToString());
            Assert.That(second.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), second.Error?.ToString());
            Assert.That(ScriptableObjectManager.GetConfig(_root.name).GetScriptableObjectData(17), Is.SameAs(_child));
            Assert.That(Resources.GetDiagnostics().LeaseCount, Is.EqualTo(2), "Rows are borrowed through their root, not acquired separately.");
            first.Dispose();
            Assert.That(second.Asset, Is.SameAs(_root));
            Assert.That(second.Asset.GetScriptableObjectData(17), Is.SameAs(_child));
            Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.SameAs(_root));
            Assert.That(_fixture.Provider.Releases, Is.Zero);
            second.Dispose();
            Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.Null);
            Assert.That(Resources.GetDiagnostics().LeaseCount, Is.Zero);
            yield return AddressablesDomainResourceFixture.Wait(() => _fixture.Provider.Releases == 1, "last root owner release");
        }

        [UnityTest]
        public IEnumerator ClosingBeforeCompletionCannotPublishLateRoot()
        {
            _fixture.Provider.Delay = true;
            var load = _fixture.Hold(ScriptableObjectManager.LoadAsync(Resources, Address));
            yield return AddressablesDomainResourceFixture.Wait(() => _fixture.Provider.Provides == 1, "pending root load");
            load.Dispose();
            Assert.That(load.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            _fixture.Provider.FinishPending();
            yield return AddressablesDomainResourceFixture.Wait(() => _fixture.Provider.Releases == 1 && AddressablesDomainResourceFixture.IsQuiescent(Resources), "late root cleanup");
            Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.Null);
            Assert.That(Resources.GetDiagnostics().LeaseCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ReentrantCloseDuringAsyncStartReleasesUnclaimedImmediateResult()
        {
            _fixture.Provider.Providing = ScriptableObjectManager.Clear;
            var load = _fixture.Hold(ScriptableObjectManager.LoadAsync(Resources, Address));
            yield return AddressablesDomainResourceFixture.Wait(() => load.IsDone && _fixture.Provider.Releases == 1, "reentrant root close");
            Assert.That(load.IsDisposed, Is.True);
            Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.Null);
            Assert.That(Resources.GetDiagnostics().LeaseCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ColdSynchronousLoadDoesNotEnterProviderAndHotLoadOwnsOnlyItsRegistration()
        {
            Assert.That(Assert.Throws<ResourceException>(() => ScriptableObjectManager.Load(Resources, Address)).Kind,
                Is.EqualTo(ResourceError.InProgress));
            Assert.That(_fixture.Provider.Provides, Is.Zero, "Cold synchronous provider callbacks have been retired.");
            ScriptableObjectManager.Clear();
            Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.Null);
            var preload = _fixture.Hold(ScriptableObjectManager.LoadAsync(Resources, Address));
            yield return AddressablesDomainResourceFixture.Wait(() => preload.IsDone, "explicit async root preload");
            Assert.That(preload.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), preload.Error?.ToString());
            var hot = _fixture.Hold(ScriptableObjectManager.Load(Resources, Address));
            ScriptableObjectManager.Clear();
            Assert.That(preload.IsDisposed && hot.IsDisposed, Is.True);
            Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.Null);
            Assert.That(Resources.GetDiagnostics().LeaseCount, Is.Zero);
            yield return AddressablesDomainResourceFixture.Wait(() => _fixture.Provider.Releases == 1, "hot registration clear");
        }

        [UnityTest]
        public IEnumerator DuplicateRootNameFailureReleasesOnlyRejectedLoad()
        {
            var original = _fixture.New<ScriptableObjectConfigs>();
            original.name = _root.name;
            using var registered = ScriptableObjectManager.AddConfig(original);
            var rejected = _fixture.Hold(ScriptableObjectManager.LoadAsync(Resources, Address));
            yield return AddressablesDomainResourceFixture.Wait(() => rejected.IsDone, "duplicate root rejection");
            Assert.That(rejected.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(rejected.Error, Is.InstanceOf<InvalidOperationException>());
            Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.SameAs(original));
            Assert.That(Resources.GetDiagnostics().LeaseCount, Is.Zero);
            yield return AddressablesDomainResourceFixture.Wait(() => _fixture.Provider.Releases == 1, "rejected root lease release");
        }

        [UnityTest]
        public IEnumerator RemovingRenamedRootByReferenceReleasesOriginalRegistrationAndLease()
        {
            var owner = _fixture.Hold(ScriptableObjectManager.LoadAsync(Resources, Address));
            yield return AddressablesDomainResourceFixture.Wait(() => owner.IsDone, "renamable root load");
            Assert.That(owner.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), owner.Error?.ToString());
            _root.name = "Renamed";
            ScriptableObjectManager.RemoveConfig(_root);
            Assert.That(owner.IsDisposed, Is.True);
            Assert.That(ScriptableObjectManager.GetConfig("DataAssets-Owned-Items"), Is.Null);
            yield return AddressablesDomainResourceFixture.Wait(() => _fixture.Provider.Releases == 1, "renamed root release");
        }

        [Test]
        public void HashRemovalClosesBothOwnersOfResolvedRootButNotOtherRoots()
        {
            var otherRoot = _fixture.New<ScriptableObjectConfigs>();
            otherRoot.name = "DataAssets-Owned-Other";
            var first = _fixture.Hold(ScriptableObjectManager.AddConfig(_root));
            var second = _fixture.Hold(ScriptableObjectManager.AddConfig(_root));
            using var other = ScriptableObjectManager.AddConfig(otherRoot);
            var hash = ScriptableObjectManager.StringToHash(_root.name);
            Assert.That(hash, Is.Not.EqualTo(ScriptableObjectManager.StringToHash(otherRoot.name)), "Fixture roots must have distinct hashes.");
            ScriptableObjectManager.RemoveConfig(hash);
            Assert.That(first.IsDisposed && second.IsDisposed, Is.True);
            Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.Null);
            Assert.That(other.Config, Is.SameAs(otherRoot));
            Assert.That(ScriptableObjectManager.RegistrationCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ClearCancelsPendingSingletonAndAllowsExplicitReload()
        {
            var singleton = _fixture.New<DataAssetSingletonFixture>("singleton");
            var address = _fixture.Address("singleton");
            _fixture.Provider.Delay = true;
            var pending = _fixture.Hold(DataAssetSingletonFixture.LoadAsync(Resources, address));
            Assert.Throws<InvalidOperationException>(() => DataAssetSingletonFixture.LoadAsync(Resources, address));
            yield return AddressablesDomainResourceFixture.Wait(() => _fixture.Provider.Provides == 1, "pending singleton");
            ScriptableObjectManager.Clear();
            _fixture.Provider.FinishPending();
            yield return AddressablesDomainResourceFixture.Wait(() => _fixture.Provider.Releases == 1 && AddressablesDomainResourceFixture.IsQuiescent(Resources), "canceled singleton drain");
            Assert.That(pending.IsDisposed, Is.True);
            Assert.That(DataAssetSingletonFixture.Instance, Is.Null);
            _fixture.Provider.Delay = false;
            var replacement = _fixture.Hold(DataAssetSingletonFixture.LoadAsync(Resources, address));
            yield return AddressablesDomainResourceFixture.Wait(() => replacement.IsDone, "explicit singleton reload");
            Assert.That(replacement.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), replacement.Error?.ToString());
            Assert.That(DataAssetSingletonFixture.Instance, Is.SameAs(singleton));
            DataAssetSingletonFixture.Release();
            Assert.That(replacement.IsDisposed, Is.True);
            Assert.That(DataAssetSingletonFixture.Instance, Is.Null);
            Assert.That(Resources.GetDiagnostics().LeaseCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ExplicitConsecutiveResourceSessionsDoNotReuseDisposedRegistrations()
        {
            var firstSystem = Resources;
            var firstOwner = _fixture.Hold(ScriptableObjectManager.LoadAsync(firstSystem, Address));
            yield return AddressablesDomainResourceFixture.Wait(() => firstOwner.IsDone, "first resource session root");
            Assert.That(firstOwner.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), firstOwner.Error?.ToString());
            firstOwner.Dispose();
            using (var stop = _fixture.Startup.StopAsync())
            {
                yield return AddressablesDomainResourceFixture.Wait(() => stop.IsDone, "first resource session stop");
                Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stop.Error?.ToString());
            }
            using (var start = _fixture.Startup.StartAsync())
            {
                yield return AddressablesDomainResourceFixture.Wait(() => start.IsDone, "second resource session start");
                Assert.That(start.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), start.Error?.ToString());
                _fixture.Resources = start.TakeResult().Resources;
            }
            Assert.That(Resources, Is.Not.SameAs(firstSystem));
            var secondOwner = _fixture.Hold(ScriptableObjectManager.LoadAsync(Resources, Address));
            yield return AddressablesDomainResourceFixture.Wait(() => secondOwner.IsDone, "second resource session root");
            Assert.That(secondOwner.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), secondOwner.Error?.ToString());
            Assert.That(secondOwner.Asset, Is.SameAs(_root));
            firstOwner.Dispose();
            Assert.That(ScriptableObjectManager.GetConfig(_root.name), Is.SameAs(_root));
            AddressablesDomainResourceFixture.AssertQuiescent(firstSystem);
        }
    }
}
