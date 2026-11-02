using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Buff;
using YLFramework.Logging;
using YLFramework.Mission;
using YLFramework.RedDots;
using YLFramework.Resource;
using YLFramework.Skill;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Special.Session")]
    public class DomainTwoPlayTests
    {
        public sealed class SessionSkill : SkillBase
        {
        }
        private string _directory;
        private System.IDisposable _oldPath;
        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            LoggerManager.Disable();
            SkillManager.Shutdown();
            BuffManager.Shutdown();
            MissionManager.Shutdown();
            _oldPath?.Dispose();
            _oldPath = null;
            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }

            if (_directory != null && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_directory)))
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_directory), true);
            }
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
            var oldPath = ReddotManager.RegisterPath("Domain/FirstPlay");
            _oldPath = oldPath;
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
            Assert.That(ReddotManager.ShouldBeDisplayed("Domain"), Is.False);
            Assert.That(LoggerManager.IsEnabled, Is.False);
            using (ReddotManager.RegisterPath("Domain/FirstPlay"))
            {
                oldPath.Dispose();
                Assert.That(ReddotManager.ShouldBeDisplayed("Domain"), Is.True);
            }
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
        public IEnumerator EachPlayReturnsAllThreeDomainHostsConfigurationsAndRealRootLeases()
        {
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled && (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0, Is.True);
            for (int round = 1; round <= 2; round++)
            {
                yield return new EnterPlayMode(false);
                Assert.That(SkillManager.ConfigCount + BuffManager.ConfigCount + MissionManager.ConfigCount, Is.Zero, "Each entry starts a new domain session.");
                Assert.That(SkillManager.HostCount + BuffManager.HostCount + BuffManager.EffectOwnerCount, Is.Zero);
                Assert.That(ResourceSystem.Current, Is.Null);
                var skills = ScriptableObject.CreateInstance<SkillsConfig>();
                skills.name = "TwoPlay.Skills";
                var skillData = ScriptableObject.CreateInstance<SkillConfigData>();
                skillData.id = 100;
                skillData.parent = skills;
                skillData.skillClassFullName = typeof(SessionSkill).FullName;
                skills.skills.Add(skillData);
                var buffs = ScriptableObject.CreateInstance<BuffsConfig>();
                buffs.name = "TwoPlay.Buffs";
                var buffData = ScriptableObject.CreateInstance<BuffConfigData>();
                buffData.id = 100;
                buffData.parent = buffs;
                buffData.buffClassFullName = typeof(BuffBase).FullName;
                buffs.buffs.Add(buffData);
                var missions = ScriptableObject.CreateInstance<MissionsConfig>();
                missions.name = "TwoPlay.Missions";
                var missionData = ScriptableObject.CreateInstance<MissionConfigData>();
                missionData.id = 100;
                missionData.parent = missions;
                missionData.missionTypeClassFullName = typeof(MissionBase).FullName;
                missions.missions.Add(missionData);
                var source = new SessionSource();
                source.Assets.Add("skills", skills);
                source.Assets.Add("buffs", buffs);
                source.Assets.Add("missions", missions);
                var entries = new List<ResourceCatalogEntry>();
                foreach (var pair in source.Assets)
                {
                    entries.Add(new ResourceCatalogEntry(pair.Key, new ResourceAddress("domain-two-play", pair.Key), new ResourceLocation(source.Id, pair.Key, pair.Value.GetType())));
                }

                using var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { new ResourceCatalog("domain-two-play", entries) }, new IResourceSource[] { source }));
                var resources = initialized.TakeResult();
                SkillManager.LoadConfig(resources, new ResourceAddress("domain-two-play", "skills"));
                BuffManager.LoadConfig(resources, new ResourceAddress("domain-two-play", "buffs"));
                MissionManager.LoadConfig(resources, new ResourceAddress("domain-two-play", "missions"));
                var host = new SessionHost();
                var skill = SkillManager.AddSkills(host, 100);
                var buff = BuffManager.AddBuff(host, 100);
                var mission = MissionManager.GetMission(missions.name, 100);
                Assert.That(resources.GetDiagnosticsSnapshot().RecordCount, Is.EqualTo(3));
                Assert.That(skill.SkillData, Is.Not.SameAs(skillData));
                Assert.That(buff.BuffData, Is.Not.SameAs(buffData));
                Assert.That(mission.MissionConfig, Is.Not.SameAs(missionData));
                // 故意不手动Shutdown/Clear static；以下断言只接受真实退出钩子的清理结果。
                yield return new ExitPlayMode();
                Assert.That(SkillManager.ConfigCount + BuffManager.ConfigCount + MissionManager.ConfigCount, Is.Zero, "Every exit returns all configurations.");
                Assert.That(SkillManager.HostCount + BuffManager.HostCount + BuffManager.EffectOwnerCount, Is.Zero);
                Assert.That(host.Skills, Is.Empty);
                Assert.That(host.Buffs, Is.Empty);
                Assert.That(skill.Releaser, Is.Null);
                Assert.That(buff.Owner, Is.Null);
                Assert.That(source.Releases, Is.EqualTo(3), "Each round must return its own three real root leases.");
                Assert.That(ResourceSystem.Current, Is.Null);
                Debug.Log("MC26_DOMAIN_TWO_PLAY_ROUND_PASS round=" + round + "; releases=" + source.Releases);
                foreach (var asset in new UnityEngine.Object[] { skills, skillData, buffs, buffData, missions, missionData })
                {
                    if (asset)
                    {
                        UnityEngine.Object.DestroyImmediate(asset);
                    }
                }
            }
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

        private sealed class SessionSource : IResourceSource
        {
            public ResourceSourceId Id => new("domain-two-play");
            public bool SupportsSynchronousLoad => true;
            internal readonly Dictionary<string, UnityEngine.Object> Assets = new();
            internal int Releases;
            public void Validate(ResourceCatalogEntry entry)
            {
            }
            public ResourceSourceResult Load(ResourceCatalogEntry entry, System.Type type) => new(Assets[entry.Location.Locator]);
            public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, System.Type type) => throw new System.NotSupportedException();
            public void Release(UnityEngine.Object asset) => Releases++;
        }
    }
}
