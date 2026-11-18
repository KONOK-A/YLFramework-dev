using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.TestTools;
using YLFramework.Buff;
using YLFramework.DataAssets;
using YLFramework.Equipment;
using YLFramework.Mission;
using YLFramework.Resource;
using YLFramework.Skill;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Extensions.Domain
{
    public sealed class AddressablesReentrantMissionProbe : MissionBase
    {
        public static Action Construct;
        public AddressablesReentrantMissionProbe() => Construct?.Invoke();
    }

    [Category("YL.Resource.Addressables.Consumers")]
    public sealed class AddressablesConsumerMigrationTests
    {
        private readonly List<Object> _assets = new();
        private readonly List<IDisposable> _held = new();
        private readonly List<Task> _tasks = new();
        private ResourceStartup _startup;
        private ResourceSystem _resources;
        private ResourceLocationMap _locator;
        private ConsumerProvider _provider;
        private string _project;
        private bool _ownsManagers;
        private AsyncOperationHandle<GameObject> _builtPrefab;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _ownsManagers = false;
            _startup = null;
            _provider = null;
            _locator = null;
            Assert.That(ResourceSystem.Current, Is.Null, "Never close an external resource authority.");
            Assert.That(ScriptableObjectManager.RegistrationCount, Is.Zero);
            Assert.That(BuffManager.ConfigCount + SkillManager.ConfigCount + MissionManager.ConfigCount, Is.Zero);
            var equipmentConfigs = (IDictionary)typeof(EquipmentManager).GetField("_configs", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Assert.That(equipmentConfigs.Count, Is.Zero);
            _ownsManagers = true;
            _project = "consumers-" + Guid.NewGuid().ToString("N");
            _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var start = _startup.StartAsync();
            yield return Wait(() => start.IsDone, "consumer resource startup");
            Assert.That(start.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), start.Error?.ToString());
            _resources = start.TakeResult().Resources;
            _builtPrefab = Addressables.LoadAssetAsync<GameObject>("ownership/prefab");
            yield return _builtPrefab;
            Assert.That(_builtPrefab.Status, Is.EqualTo(AsyncOperationStatus.Succeeded), _builtPrefab.OperationException?.ToString());
            Assert.That(_builtPrefab.Result.GetComponent<AddressablesEquipmentConsumerProbe>(), Is.Not.Null);
            _provider = new ConsumerProvider(_project);
            _locator = new ResourceLocationMap(_project);
            Addressables.ResourceManager.ResourceProviders.Add(_provider);
            Addressables.AddResourceLocator(_locator);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            AddressablesReentrantMissionProbe.Construct = null;
            if (_ownsManagers)
            {
                BuffManager.Shutdown();
                SkillManager.Shutdown();
                MissionManager.Shutdown();
                EquipmentManager.Shutdown();
                ScriptableObjectManager.Clear();
                _ownsManagers = false;
            }
            _provider?.FinishPending();
            foreach (var held in _held)
            {
                held.Dispose();
            }
            _held.Clear();
            foreach (var task in _tasks)
            {
                yield return Wait(() => task.IsCompleted, "consumer task cleanup");
                _ = task.Exception;
            }
            _tasks.Clear();
            if (_startup?.Resources != null)
            {
                using var stop = _startup.StopAsync();
                yield return Wait(() => stop.IsDone, "consumer resource cleanup");
                Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stop.Error?.ToString());
            }
            if (_locator != null)
                Addressables.RemoveResourceLocator(_locator);
            if (_builtPrefab.IsValid()) Addressables.Release(_builtPrefab);
            _builtPrefab = default;
            if (_provider != null)
                Addressables.ResourceManager.ResourceProviders.Remove(_provider);
            foreach (var asset in _assets)
            {
                if (asset)
                    Object.Destroy(asset);
            }
            _assets.Clear();
            yield return null;
        }

        [UnityTest]
        public IEnumerator DataAssetsColdLoadIsExplicitAndAsyncOwnersKeepIndependentRegistrations()
        {
            var root = AddRoot<ScriptableObjectConfigs>("items");
            var address = Address("items");
            Assert.That(Assert.Throws<ResourceException>(() => ScriptableObjectManager.Load(_resources, address)).Kind,
                Is.EqualTo(ResourceError.InProgress));
            Assert.That(_provider.Provides, Is.Zero);
            var first = Hold(ScriptableObjectManager.LoadAsync(_resources, address));
            var second = Hold(ScriptableObjectManager.LoadAsync(_resources, address));
            yield return Wait(() => first.IsDone && second.IsDone, "independent DataAssets registrations");
            Assert.That(first.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), first.Error?.ToString());
            Assert.That(second.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), second.Error?.ToString());
            first.Dispose();
            Assert.That(ScriptableObjectManager.GetConfig(root.name), Is.SameAs(root));
            using var hot = ScriptableObjectManager.Load(_resources, address);
            second.Dispose();
            Assert.That(hot.Config, Is.SameAs(root));
            hot.Dispose();
            Assert.That(ScriptableObjectManager.GetConfig(root.name), Is.Null);
            yield return Wait(() => _provider.Releases == 1, "last DataAssets owner release");
        }

        [UnityTest]
        public IEnumerator AllGameplayConfigsHaveHotOnlySyncAndManagerOwnedAsyncRoots()
        {
            var buff = AddRoot<BuffsConfig>("buff");
            var skill = AddRoot<SkillsConfig>("skill");
            var mission = AddRoot<MissionsConfig>("mission");
            var equipment = AddRoot<EquipmentsConfig>("equipment");
            foreach (Action load in new Action[]
            {
                () => BuffManager.LoadConfig(_resources, Address("buff")),
                () => SkillManager.LoadConfig(_resources, Address("skill")),
                () => MissionManager.LoadConfig(_resources, Address("mission")),
                () => EquipmentManager.LoadConfig(_resources, Address("equipment"))
            })
            {
                Assert.That(Assert.Throws<ResourceException>(() => load()).Kind, Is.EqualTo(ResourceError.InProgress));
            }
            Assert.That(_provider.Provides, Is.Zero);
            var loaded = Track(Task.WhenAll(
                BuffManager.LoadConfigAsync(_resources, Address("buff")),
                SkillManager.LoadConfigAsync(_resources, Address("skill")),
                MissionManager.LoadConfigAsync(_resources, Address("mission")),
                EquipmentManager.LoadConfigAsync(_resources, Address("equipment"))));
            yield return Wait(() => loaded.IsCompleted, "async domain configs");
            Assert.That(loaded.Status, Is.EqualTo(TaskStatus.RanToCompletion), loaded.Exception?.ToString());
            Assert.That(BuffManager.GetConfig(buff.name), Is.SameAs(buff));
            Assert.That(SkillManager.GetConfig(skill.name), Is.SameAs(skill));
            Assert.That(MissionManager.GetConfig(mission.name), Is.SameAs(mission));
            Assert.That(EquipmentManager.GetConfig(equipment.name), Is.SameAs(equipment));
            Assert.That(_resources.TryAcquireLoaded<SkillsConfig>(_project + "/skill", out var independent), Is.True);
            Hold(independent);
            BuffManager.Shutdown();
            SkillManager.Shutdown();
            MissionManager.Shutdown();
            EquipmentManager.Shutdown();
            Assert.That(independent.Asset, Is.SameAs(skill));
            Assert.That(_resources.IsAvailable, Is.True, "Managers close only their own resource responsibilities.");
            yield return Wait(() => _provider.Releases == 3, "domain roots except independent Skill owner");
            independent.Dispose();
            yield return Wait(() => _provider.Releases == 4, "independent Skill root owner");
        }

        [UnityTest]
        public IEnumerator ClosingDataAssetOwnerDoesNotCancelAnIndependentWaiterOrRegisterLate()
        {
            var root = AddRoot<ScriptableObjectConfigs>("items");
            _provider.Delay = true;
            var owner = Hold(ScriptableObjectManager.LoadAsync(_resources, Address("items")));
            using var independent = _resources.AcquireAsync<ScriptableObjectConfigs>(Address("items"));
            yield return Wait(() => _provider.Provides == 1, "shared DataAssets producer");
            owner.Dispose();
            _provider.FinishPending();
            yield return Wait(() => independent.IsDone, "independent DataAssets waiter");
            Assert.That(independent.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), independent.Error?.ToString());
            var lease = Hold(independent.TakeResult());
            Assert.That(lease.Asset, Is.SameAs(root));
            Assert.That(ScriptableObjectManager.GetConfig(root.name), Is.Null);
            Assert.That(owner.IsDisposed, Is.True);
            Assert.That(_provider.Releases, Is.Zero);
            lease.Dispose();
            yield return Wait(() => _provider.Releases == 1, "independent DataAssets owner release");
        }

        [UnityTest]
        public IEnumerator ClosingOneManagerCancelsItsPendingConfigWithoutCancellingAnotherManager()
        {
            AddRoot<BuffsConfig>("buff");
            var skill = AddRoot<SkillsConfig>("skill");
            _provider.Delay = true;
            var buffLoad = Track(BuffManager.LoadConfigAsync(_resources, Address("buff")));
            var skillLoad = Track(SkillManager.LoadConfigAsync(_resources, Address("skill")));
            yield return Wait(() => _provider.Provides == 2, "two independent pending config roots");
            BuffManager.Shutdown();
            _provider.FinishPending();
            yield return Wait(() => buffLoad.IsCompleted && skillLoad.IsCompleted, "pending config close");
            Assert.That(buffLoad.IsCanceled, Is.True, buffLoad.Exception?.ToString());
            Assert.That(BuffManager.ConfigCount, Is.Zero);
            Assert.That(skillLoad.Status, Is.EqualTo(TaskStatus.RanToCompletion), skillLoad.Exception?.ToString());
            Assert.That(SkillManager.GetConfig(skill.name), Is.SameAs(skill));
        }

        [UnityTest]
        public IEnumerator FailedConfigRegistrationReturnsOnlyTheRejectedRootLease()
        {
            var accepted = AddRoot<SkillsConfig>("accepted");
            var rejected = AddRoot<SkillsConfig>("rejected");
            rejected.name = accepted.name;
            var first = Track(SkillManager.LoadConfigAsync(_resources, Address("accepted")));
            yield return Wait(() => first.IsCompleted, "accepted config root");
            Assert.That(first.Status, Is.EqualTo(TaskStatus.RanToCompletion), first.Exception?.ToString());
            var second = Track(SkillManager.LoadConfigAsync(_resources, Address("rejected")));
            yield return Wait(() => second.IsCompleted, "rejected config root");
            Assert.That(second.IsFaulted, Is.True);
            Assert.That(second.Exception.InnerException, Is.InstanceOf<InvalidOperationException>());
            Assert.That(SkillManager.GetConfig(accepted.name), Is.SameAs(accepted));
            yield return Wait(() => _provider.Releases == 1, "rejected root lease release");
            SkillManager.Shutdown();
            yield return Wait(() => _provider.Releases == 2, "accepted root lease release");
        }

        [UnityTest]
        public IEnumerator ReentrantMissionReplacementCannotReceiveTheRejectedOriginalRootLease()
        {
            var original = AddRoot<MissionsConfig>("mission");
            var replacement = AddRoot<MissionsConfig>("replacement");
            replacement.name = original.name;
            var row = ScriptableObject.CreateInstance<MissionConfigData>();
            _assets.Add(row);
            row.id = 1;
            row.parent = original;
            row.missionTypeClassFullName = typeof(AddressablesReentrantMissionProbe).FullName;
            original.missions.Add(row);
            AddressablesReentrantMissionProbe.Construct = () =>
            {
                AddressablesReentrantMissionProbe.Construct = null;
                MissionManager.RemoveConfig(original.name);
                MissionManager.AddConfig(replacement);
            };
            var load = Track(MissionManager.LoadConfigAsync(_resources, Address("mission")));
            yield return Wait(() => load.IsCompleted, "reentrant mission replacement");
            Assert.That(load.IsCanceled, Is.True, load.Exception?.ToString());
            Assert.That(MissionManager.GetConfig(original.name), Is.SameAs(replacement));
            yield return Wait(() => _provider.Releases == 1, "rejected original mission root lease");
        }

        [UnityTest]
        public IEnumerator AssetEquipmentColdCreationIsAsyncAndSubsequentSyncCheckoutReusesTheInstance()
        {
            var root = EquipmentRoot("equipment", "prefab");
            AddPrefab("prefab");
            var load = Track(EquipmentManager.LoadConfigAsync(_resources, Address("equipment")));
            yield return Wait(() => load.IsCompleted, "equipment root");
            Assert.That(load.Status, Is.EqualTo(TaskStatus.RanToCompletion), load.Exception?.ToString());
            Assert.That(Assert.Throws<ResourceException>(() => EquipmentManager.CreateEquipment(1001)).Kind,
                Is.EqualTo(ResourceError.InProgress));
            Assert.That(Assert.Throws<ResourceException>(() => EquipmentManager.CreateAndEquip(new Host(), 1001)).Kind,
                Is.EqualTo(ResourceError.InProgress));
            var create = Track(EquipmentManager.CreateEquipmentAsync(1001));
            yield return Wait(() => create.IsCompleted, "cold equipment creation");
            Assert.That(create.Status, Is.EqualTo(TaskStatus.RanToCompletion), create.Exception?.ToString());
            var first = (AddressablesEquipmentConsumerProbe)create.Result;
            Assert.That(first.EquipmentData, Is.SameAs(root.Equipments[0]), "Entries are borrowed, not independently loaded.");
            EquipmentManager.DestroyEquipment(first);
            var second = (AddressablesEquipmentConsumerProbe)EquipmentManager.CreateEquipment(1001);
            Assert.That(second, Is.SameAs(first));
            Assert.That(second.Loads, Is.EqualTo(2));
            EquipmentManager.Shutdown();
            Assert.That(second.Unloads, Is.EqualTo(2));
            Assert.That(EquipmentManager.GetConfig(root.name), Is.Null);
            Assert.That(_resources.IsAvailable, Is.True);
        }

        [UnityTest]
        public IEnumerator ClosingAHostCancelsOnlyItsPendingEquipmentCheckout()
        {
            var root = EquipmentRoot("equipment", "prefab");
            AddPrefab("prefab");
            EquipmentManager.AddConfig(root);
            _provider.Delay = true;
            var firstHost = new Host();
            var secondHost = new Host();
            var first = Track(EquipmentManager.CreateAndEquipAsync(firstHost, 1001));
            var second = Track(EquipmentManager.CreateAndEquipAsync(secondHost, 1001));
            yield return Wait(() => _provider.Provides == 1, "shared pending equipment prefab");
            EquipmentManager.CloseHost(firstHost);
            _provider.FinishPending();
            yield return Wait(() => first.IsCompleted && second.IsCompleted, "independent equipment waiters");
            Assert.That(first.IsCanceled, Is.True, first.Exception?.ToString());
            Assert.That(firstHost.Equipments, Is.Empty);
            Assert.That(second.Status, Is.EqualTo(TaskStatus.RanToCompletion), second.Exception?.ToString());
            Assert.That(secondHost.Equipments, Has.Count.EqualTo(1));
            Assert.That(second.Result.Equipper, Is.SameAs(secondHost));
        }

        [UnityTest]
        public IEnumerator RemovingConfigCancelsPendingEquipmentWithoutPublishingALateInstance()
        {
            var root = EquipmentRoot("equipment", "prefab");
            AddPrefab("prefab");
            EquipmentManager.AddConfig(root);
            _provider.Delay = true;
            var create = Track(EquipmentManager.CreateEquipmentAsync(1001));
            yield return Wait(() => _provider.Provides == 1, "pending equipment config removal");
            EquipmentManager.RemoveConfig(root);
            _provider.FinishPending();
            yield return Wait(() => create.IsCompleted, "removed equipment creation");
            Assert.That(create.IsCanceled, Is.True, create.Exception?.ToString());
            Assert.That(EquipmentManager.GetConfig(root.name), Is.Null);
            Assert.That(_resources.IsAvailable, Is.True);
        }

        private T AddRoot<T>(string path) where T : ScriptableObject
        {
            var root = ScriptableObject.CreateInstance<T>();
            root.name = _project + "-" + path;
            Add(path, root);
            return root;
        }

        private EquipmentsConfig EquipmentRoot(string path, string prefab)
        {
            var root = AddRoot<EquipmentsConfig>(path);
            var row = ScriptableObject.CreateInstance<EquipmentConfigData>();
            _assets.Add(row);
            row.id = 1001;
            row.parent = root;
            row.equipmentType = EquipmentType.Asset;
            row.projectName = _project;
            row.assetName = prefab;
            root.Equipments.Add(row);
            return root;
        }

        private void AddPrefab(string path)
        {
            string key = _project + "/" + path;
            _locator.Add(key, new ResourceLocationBase(key, key, _provider.ProviderId, typeof(GameObject)) { Data = _builtPrefab.Result });
        }

        private void Add(string path, Object asset)
        {
            _assets.Add(asset);
            string key = _project + "/" + path;
            _locator.Add(key, new ResourceLocationBase(key, key, _provider.ProviderId, asset.GetType()) { Data = asset });
        }

        private ResourceAddress Address(string path) => new(_project, path);
        private T Hold<T>(T value) where T : IDisposable
        {
            _held.Add(value);
            return value;
        }
        private T Track<T>(T task) where T : Task
        {
            _tasks.Add(task);
            return task;
        }

        private static IEnumerator Wait(Func<bool> completed, string operation)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            while (!completed() && Time.realtimeSinceStartupAsDouble < deadline)
                yield return null;
            Assert.That(completed(), Is.True, operation + " timed out.");
        }

        private sealed class Host : IEquippable
        {
            public List<IEquipment> Equipments { get; } = new();
            public bool CanEquip(IEquipment equipment) => true;
        }

        private sealed class ConsumerProvider : ResourceProviderBase
        {
            private readonly List<ProvideHandle> _pending = new();
            internal bool Delay;
            internal int Provides;
            internal int Releases;
            internal ConsumerProvider(string id) => m_ProviderId = id;
            public override void Provide(ProvideHandle handle)
            {
                Provides++;
                handle.SetWaitForCompletionCallback(() => throw new InvalidOperationException("Consumer sync waits are forbidden."));
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
            public override void Release(IResourceLocation location, object asset) => Releases++;
        }
    }
}
