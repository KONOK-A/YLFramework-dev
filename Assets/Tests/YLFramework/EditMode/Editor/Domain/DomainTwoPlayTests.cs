using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.TestTools;
using YLFramework.Buff;
using YLFramework.Equipment;
using YLFramework.Logging;
using YLFramework.Mission;
#if YL_REDDOT_PACKAGE
using YLFramework.RedDots;
#endif
using YLFramework.Resource;
using YLFramework.Skill;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Special.Session")]
    [Category("YL.Special.GameplaySession")]
    public class DomainTwoPlayTests
    {
        private static readonly string _domainToken = Guid.NewGuid().ToString("N");
        private static readonly List<string> ShutdownCallbacks = new();
        private static readonly List<bool> LoggerDuringCallbacks = new();
        private static void RecordShutdown(string domain)
        {
            ShutdownCallbacks.Add(domain);
            LoggerDuringCallbacks.Add(LoggerManager.IsEnabled);
            LoggerManager.Current?.LogFormat(LogType.Log, null, "domain-shutdown-callback {0}", domain);
        }
        public sealed class SessionSkill : SkillBase
        {
            protected internal override void OnRemove() => RecordShutdown("Skill");
        }
        public sealed class ThrowingSessionSkill : SkillBase
        {
            protected internal override void OnRemove()
            {
                RecordShutdown("Skill");
                throw new System.InvalidOperationException("domain-shutdown-order-marker");
            }
        }
        public sealed class SessionBuff : BuffBase
        {
            public override void OnRemove() => RecordShutdown("Buff");
        }
        public sealed class SessionEquipment : IEquipment
        {
            public IEquippable Equipper
            {
                get; set;
            }
            public EquipmentConfigData EquipmentData
            {
                get; set;
            }
            public void OnLoaded()
            {
            }
            public void OnUnloaded() => RecordShutdown("Equipment");
            public void OnEquip()
            {
            }
            public void OnUnequip()
            {
            }
        }
        private string _directory;
        private string _equipmentFixturePath;
        private string _equipmentFixtureGuid;
        private System.IDisposable _oldPath;
        private bool _ownsSession;
        private readonly List<UnityEngine.Object> _objects = new();
        private ResourceStartup _startup;
        private ResourceSystem _resources;
        private ResourceLocationMap _locator;
        private SessionProvider _provider;
        private Task[] _loads;

        [UnitySetUp]
        public IEnumerator RequireOwnedSession()
        {
            _ownsSession = false;
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var owner = JObject.Parse(File.ReadAllText(Path.Combine(project, ".yl-owner.json")));
            Assert.That((int)owner["schemaVersion"], Is.EqualTo(1));
            Assert.That((string)owner["purpose"], Is.EqualTo("YLTesting.Directory"));
            Assert.That((string)owner["kind"], Is.EqualTo("Host"));
            Assert.That(Path.GetFullPath((string)owner["path"]), Is.EqualTo(project).IgnoreCase);
            Assert.That(Directory.Exists(global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput()), Is.True);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(LoggerManager.IsEnabled, Is.False);
            Assert.That(SkillManager.ConfigCount + BuffManager.ConfigCount + MissionManager.ConfigCount, Is.Zero);
            var equipmentConfigs = (IDictionary)typeof(EquipmentManager).GetField("_configs", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Assert.That(equipmentConfigs.Count, Is.Zero, "Never reset another configuration owner.");
            _ownsSession = true;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (!_ownsSession)
                yield break;
            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }
            var errors = new List<Exception>();
            foreach (Action close in new Action[] { SkillManager.Shutdown, BuffManager.Shutdown, MissionManager.Shutdown, EquipmentManager.Shutdown, LoggerManager.Disable })
            {
                try
                {
                    close();
                }
                catch (Exception error) { errors.Add(error); }
            }
            _oldPath?.Dispose();
            _oldPath = null;
            if (_loads != null)
            {
                double deadline = Time.realtimeSinceStartupAsDouble + 15;
                while (_loads.Any(task => !task.IsCompleted) && Time.realtimeSinceStartupAsDouble < deadline)
                    yield return null;
                foreach (var task in _loads)
                {
                    Assert.That(task.IsCompleted, Is.True, "An owned config load must settle before its Provider is removed.");
                    if (task.IsFaulted)
                        errors.Add(task.Exception);
                }
                _loads = null;
            }
            if (_startup != null && _resources?.State != ResourceSystemState.Invalidated && _resources?.State != ResourceSystemState.Closed)
            {
                using var stop = _startup.StopAsync();
                double deadline = Time.realtimeSinceStartupAsDouble + 15;
                while (!stop.IsDone && Time.realtimeSinceStartupAsDouble < deadline)
                    yield return null;
                Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stop.Error?.ToString());
            }
            if (_resources != null)
            {
                double deadline = Time.realtimeSinceStartupAsDouble + 15;
                while (!IsQuiescent(_resources) && Time.realtimeSinceStartupAsDouble < deadline)
                    yield return null;
                AssertQuiescent(_resources);
            }
            RemoveProvider();
            _startup = null;
            _resources = null;
            DestroyObjects();
            if (_equipmentFixtureGuid != null)
            {
                Assert.That(AssetDatabase.AssetPathToGUID(_equipmentFixturePath), Is.EqualTo(_equipmentFixtureGuid));
                Assert.That(AssetDatabase.DeleteAsset(_equipmentFixturePath), Is.True);
                _equipmentFixtureGuid = null;
                _equipmentFixturePath = null;
            }
            if (_directory != null && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_directory)))
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_directory), true);
            }
            _directory = null;
            _ownsSession = false;
            if (errors.Count != 0)
                throw new AggregateException(errors);
        }
        [UnityTest]
        public IEnumerator TwoPlaysWithoutDomainReloadDoNotReusePathsWriterOrHandler()
        {
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled && (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0,
                Is.True, "由验收owner预先配置Disable Domain Reload；本用例不改ProjectSettings。");
            yield return new EnterPlayMode(false);
            var original = Debug.unityLogger.logHandler;
            var directory = Path.Combine(Application.temporaryCachePath, "YL.Domain.TwoPlay." + System.Guid.NewGuid().ToString("N"));
            _directory = directory;
#if YL_REDDOT_PACKAGE
            var oldPath = ReddotManager.RegisterPath("Domain/FirstPlay");
            _oldPath = oldPath;
#endif
            var session = LoggerManager.Enable(new LoggerOptions { WriteToFile = true, Directory = directory });
            session.LogFormat(LogType.Log, null, "domain-first-play");
            var file = session.LogFilePath;
            yield return new ExitPlayMode();
            Assert.That(LoggerManager.IsEnabled, Is.False);
            Assert.That(session.IsDisposed, Is.True);
            using (var stream = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(file), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.That(stream.Length, Is.GreaterThan(0));
            }

            yield return new EnterPlayMode(false);
            Assert.That(LoggerManager.IsEnabled, Is.False);
#if YL_REDDOT_PACKAGE
            Assert.That(ReddotManager.ShouldBeDisplayed("Domain"), Is.False);
            using (ReddotManager.RegisterPath("Domain/FirstPlay"))
            {
                oldPath.Dispose();
                Assert.That(ReddotManager.ShouldBeDisplayed("Domain"), Is.True);
            }
#endif
            var secondOriginal = Debug.unityLogger.logHandler;
            var second = LoggerManager.Enable(new LoggerOptions { WriteToFile = true, Directory = directory });
            second.LogFormat(LogType.Log, null, "domain-second-play");
            Assert.That(second.LogFilePath, Is.Not.EqualTo(file));
            LoggerManager.Disable();
            Assert.That(Debug.unityLogger.logHandler, Is.SameAs(secondOriginal));
            yield return new ExitPlayMode();
            Assert.That(session, Is.Not.SameAs(second));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(file)), Does.Not.Contain("domain-second-play"));
            Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(directory), true);
            // original仅记录第一Play前任，不能在第二Play/退出后强行安装旧引用。
            Assert.That(original, Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator DomainShutdownContinuesAndLoggingClosesLast()
        {
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled && (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0, Is.True);
            var equipments = CreateEquipmentFixture();
            var equipmentData = equipments.Equipments.Single();
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(equipmentData, out string equipmentGuid, out long equipmentLocalId), Is.True);
            var equipmentBytes = File.ReadAllBytes(_equipmentFixturePath);
            string token = _domainToken;
            ResourceSystem previous = null;
            for (int round = 1; round <= 2; round++)
            {
                yield return new EnterPlayMode(false);
                Assert.That(_domainToken, Is.EqualTo(token));
                ShutdownCallbacks.Clear();
                LoggerDuringCallbacks.Clear();
                Assert.That(SkillManager.ConfigCount + BuffManager.ConfigCount + MissionManager.ConfigCount, Is.Zero, "Each entry starts a new domain session.");
                Assert.That(EquipmentManager.GetConfig("TwoPlay.Equipments"), Is.Null);
                Assert.That(SkillManager.HostCount + BuffManager.HostCount + BuffManager.EffectOwnerCount, Is.Zero);
                Assert.That(ResourceSystem.Current, Is.Null);
                var skills = Own<SkillsConfig>();
                skills.name = "TwoPlay.Skills";
                var skillData = Own<SkillConfigData>();
                skillData.id = 100;
                skillData.parent = skills;
                skillData.skillClassFullName = round == 1 ? typeof(SessionSkill).FullName : typeof(ThrowingSessionSkill).FullName;
                skills.skills.Add(skillData);
                var buffs = Own<BuffsConfig>();
                buffs.name = "TwoPlay.Buffs";
                var buffData = Own<BuffConfigData>();
                buffData.id = 100;
                buffData.parent = buffs;
                buffData.buffClassFullName = typeof(SessionBuff).FullName;
                buffs.buffs.Add(buffData);
                var missions = Own<MissionsConfig>();
                missions.name = "TwoPlay.Missions";
                var missionData = Own<MissionConfigData>();
                missionData.id = 100;
                missionData.parent = missions;
                missionData.missionTypeClassFullName = typeof(MissionBase).FullName;
                missions.missions.Add(missionData);
                string project = "domain-two-play-" + Guid.NewGuid().ToString("N");
                var assets = new Dictionary<string, UnityEngine.Object>
                {
                    { "skills", skills }, { "buffs", buffs }, { "missions", missions }, { "equipments", equipments }
                };
                _provider = new SessionProvider(project);
                _locator = new ResourceLocationMap(project);
                foreach (var pair in assets)
                {
                    string key = project + "/" + pair.Key;
                    _locator.Add(key, new ResourceLocationBase(key, key, _provider.ProviderId, pair.Value.GetType()) { Data = pair.Value });
                }
                _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
                using (var initialized = _startup.StartAsync())
                {
                    double deadline = Time.realtimeSinceStartupAsDouble + 15;
                    while (!initialized.IsDone && Time.realtimeSinceStartupAsDouble < deadline)
                        yield return null;
                    Assert.That(initialized.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), initialized.Error?.ToString());
                    _resources = initialized.TakeResult().Resources;
                }
                var resources = _resources;
                Assert.That(resources, Is.Not.SameAs(previous));
                Addressables.ResourceManager.ResourceProviders.Add(_provider);
                Addressables.AddResourceLocator(_locator);
                _loads = new Task[]
                {
                    SkillManager.LoadConfigAsync(resources, new ResourceAddress(project, "skills")),
                    BuffManager.LoadConfigAsync(resources, new ResourceAddress(project, "buffs")),
                    MissionManager.LoadConfigAsync(resources, new ResourceAddress(project, "missions")),
                    EquipmentManager.LoadConfigAsync(resources, new ResourceAddress(project, "equipments"))
                };
                double loadDeadline = Time.realtimeSinceStartupAsDouble + 15;
                while (_loads.Any(task => !task.IsCompleted) && Time.realtimeSinceStartupAsDouble < loadDeadline)
                    yield return null;
                foreach (var load in _loads)
                    Assert.That(load.IsCompletedSuccessfully, Is.True, load.Exception?.ToString());
                var host = new SessionHost();
                var skill = SkillManager.AddSkills(host, 100);
                skill.Release();
                var buff = BuffManager.AddBuff(host, 100);
                var equipment = EquipmentManager.CreateEquipment(100);
                var mission = MissionManager.GetMission(missions.name, 100);
                Assert.That(resources.GetDiagnostics().LeaseCount, Is.EqualTo(4));
                Assert.That(resources.GetDiagnostics().Leases.Select(lease => lease.Key).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(4));
                Assert.That(_provider.Provides, Is.EqualTo(4));
                Assert.That(_provider.Releases, Is.Zero);
                Assert.That(skill.SkillData, Is.Not.SameAs(skillData));
                Assert.That(buff.BuffData, Is.Not.SameAs(buffData));
                Assert.That(mission.MissionConfig, Is.Not.SameAs(missionData));
                Assert.That(equipment.EquipmentData, Is.SameAs(equipmentData), "Script equipment borrows its definition from its leased configuration.");
                var instances = new UnityEngine.Object[] { skill.SkillData, buff.BuffData, mission.MissionConfig };
                Assert.That(instances, Is.All.Not.Null);
                _directory = Path.Combine(Application.temporaryCachePath, "YL.Domain.Shutdown." + System.Guid.NewGuid().ToString("N"));
                var logger = LoggerManager.Enable(new LoggerOptions { WriteToFile = true, Directory = _directory });
                logger.LogFormat(LogType.Log, null, "domain-shutdown-start");
                string logPath = logger.LogFilePath;
                if (round == 2)
                {
                    LogAssert.Expect(LogType.Exception, new Regex("domain-shutdown-order-marker"));
                }
                var exitTrace = new List<string>();
                System.Action<PlayModeStateChange> observe = state => exitTrace.Add(state + ":logger=" + LoggerManager.IsEnabled + ",configs=" +
                    (SkillManager.ConfigCount + BuffManager.ConfigCount + MissionManager.ConfigCount) + ",releases=" + _provider.Releases);
                EditorApplication.playModeStateChanged += observe;
                // 故意不手动Shutdown/Clear static；以下断言只接受真实退出钩子的清理结果。
                try
                {
                    yield return new ExitPlayMode();
                }
                finally { EditorApplication.playModeStateChanged -= observe; }
                Assert.That(_domainToken, Is.EqualTo(token));
                double drainDeadline = Time.realtimeSinceStartupAsDouble + 15;
                while ((!IsQuiescent(resources) || _provider.Releases != 4 || instances.Any(instance => instance)) && Time.realtimeSinceStartupAsDouble < drainDeadline)
                    yield return null;
                Assert.That(SkillManager.ConfigCount + BuffManager.ConfigCount + MissionManager.ConfigCount, Is.Zero, "Every exit returns all configurations.");
                Assert.That(EquipmentManager.GetConfig("TwoPlay.Equipments"), Is.Null);
                Assert.That(SkillManager.HostCount + BuffManager.HostCount + BuffManager.EffectOwnerCount, Is.Zero);
                Assert.That(host.Skills, Is.Empty);
                Assert.That(host.Buffs, Is.Empty);
                Assert.That(skill.Releaser, Is.Null);
                Assert.That(buff.Owner, Is.Null);
                Assert.That(_provider.Provides, Is.EqualTo(4));
                Assert.That(_provider.Releases, Is.EqualTo(4), "Each round must return all four official root operations, even after an earlier domain throws.");
                Assert.That(_provider.ReleaseCounts.Values, Is.All.EqualTo(1));
                Assert.That(_provider.ReleaseCounts.Count, Is.EqualTo(4));
                Assert.That(instances.All(instance => instance == null), Is.True, "Owned configuration copies must be destroyed, not only detached from their hosts.");
                Assert.That(skill.SkillData, Is.Null);
                Assert.That(buff.BuffData, Is.Null);
                Assert.That(equipment.EquipmentData, Is.Null);
                Assert.That(equipmentData != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(equipmentData, out string afterGuid, out long afterLocalId) &&
                    afterGuid == equipmentGuid && afterLocalId == equipmentLocalId, Is.True, "Closing a borrower must preserve the provider's persisted definition.");
                CollectionAssert.AreEqual(equipmentBytes, File.ReadAllBytes(_equipmentFixturePath));
                AssertQuiescent(resources);
                Assert.That(resources.State == ResourceSystemState.Closed || resources.State == ResourceSystemState.Invalidated, Is.True);
                Assert.That(ShutdownCallbacks, Is.EquivalentTo(new[] { "Skill", "Buff", "Equipment" }));
                Assert.That(ShutdownCallbacks.IndexOf("Skill"), Is.LessThan(ShutdownCallbacks.IndexOf("Buff")), "Keep the established Skill/Buff ordering.");
                Assert.That(LoggerDuringCallbacks, Is.All.True, "Domain callbacks, not later Resource source eviction, must precede logger shutdown. " + string.Join(";", exitTrace));
                Assert.That(logger.IsDisposed, Is.True);
                Assert.That(LoggerManager.IsEnabled, Is.False);
                using (var stream = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(logPath), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Assert.That(stream.Length, Is.GreaterThan(0));
                Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(logPath)), Does.Contain("domain-shutdown-callback Equipment"));
                Assert.That(ResourceSystem.Current, Is.Null);
                Debug.Log("DOMAIN_SHUTDOWN_ORDER_PASS round=" + round + "; releases=" + _provider.Releases);
                previous = resources;
                RemoveProvider();
                _resources = null;
                _startup = null;
                _loads = null;
                DestroyObjects();
                Directory.Delete(_directory, true);
                _directory = null;
            }
        }

        private EquipmentsConfig CreateEquipmentFixture()
        {
            // Unity drops transient ScriptableObjects at Play exit. Use a persisted definition to distinguish borrower cleanup.
            _equipmentFixturePath = "Assets/__YLDomainEquipment-" + Guid.NewGuid().ToString("N") + ".asset";
            Assert.That(File.Exists(_equipmentFixturePath) || Directory.Exists(_equipmentFixturePath) || File.Exists(_equipmentFixturePath + ".meta"), Is.False);
            var root = ScriptableObject.CreateInstance<EquipmentsConfig>();
            var row = ScriptableObject.CreateInstance<EquipmentConfigData>();
            row.id = 100;
            row.parent = root;
            row.equipmentType = EquipmentType.Script;
            row.equipmentClassFullName = typeof(SessionEquipment).FullName;
            AssetDatabase.CreateAsset(root, _equipmentFixturePath);
            _equipmentFixtureGuid = AssetDatabase.AssetPathToGUID(_equipmentFixturePath);
            Assert.That(_equipmentFixtureGuid, Is.Not.Empty);
            root.name = "TwoPlay.Equipments";
            AssetDatabase.AddObjectToAsset(row, root);
            root.Equipments.Add(row);
            EditorUtility.SetDirty(root);
            AssetDatabase.SaveAssetIfDirty(root);
            return root;
        }

        private sealed class SessionHost : IEquippableSkill, IAddableBuff
        {
            public Dictionary<int, SkillBase> Skills { get; } = new();
            public List<BuffBase> Buffs { get; } = new();
            public bool CanRelease() => true;
            public bool CanAdd(int id) => true;
            public void OnAddBuff(BuffBase buff)
            {
            }
            public void OnRemoveBuff(BuffBase buff)
            {
            }
        }

        private T Own<T>() where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>();
            _objects.Add(asset);
            return asset;
        }

        private void DestroyObjects()
        {
            foreach (var asset in _objects)
                if (asset)
                    UnityEngine.Object.DestroyImmediate(asset);
            _objects.Clear();
        }

        private static bool IsQuiescent(ResourceSystem resources)
        {
            var report = resources.GetDiagnostics();
            return report.LeaseCount == 0 && report.ScopeCount == 0 && report.RequestCount == 0 && report.PendingWorkCount == 0 && report.DrainingHandleCount == 0;
        }

        private static void AssertQuiescent(ResourceSystem resources)
        {
            var report = resources.GetDiagnostics();
            Assert.That(report.LeaseCount, Is.Zero);
            Assert.That(report.ScopeCount, Is.Zero);
            Assert.That(report.RequestCount, Is.Zero);
            Assert.That(report.PendingWorkCount, Is.Zero);
            Assert.That(report.DrainingHandleCount, Is.Zero);
            Assert.That(report.Errors, Is.Empty);
        }

        private void RemoveProvider()
        {
            if (_locator != null)
                Addressables.RemoveResourceLocator(_locator);
            if (_provider != null)
                Addressables.ResourceManager.ResourceProviders.Remove(_provider);
            _locator = null;
            _provider = null;
        }

        private sealed class SessionProvider : ResourceProviderBase
        {
            internal int Provides;
            internal int Releases;
            internal readonly Dictionary<string, int> ReleaseCounts = new(StringComparer.Ordinal);
            internal SessionProvider(string id) => m_ProviderId = id;
            public override void Provide(ProvideHandle handle)
            {
                Provides++;
                handle.SetWaitForCompletionCallback(() => throw new InvalidOperationException("NoDR domain roots must load asynchronously."));
                handle.Complete((UnityEngine.Object)handle.Location.Data, true, null);
            }
            public override void Release(IResourceLocation location, object asset)
            {
                Releases++;
                ReleaseCounts.TryGetValue(location.PrimaryKey, out var count);
                ReleaseCounts[location.PrimaryKey] = count + 1;
            }
        }
    }
}
