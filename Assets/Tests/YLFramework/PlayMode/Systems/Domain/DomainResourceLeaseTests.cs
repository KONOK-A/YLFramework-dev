using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Buff;
using YLFramework.Mission;
using YLFramework.Resource;
using YLFramework.Skill;
using Object = UnityEngine.Object;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.Extensions.Domain
{
    public class DomainResourceLeaseTests
    {
        // 仅控制输入来源；使用真实Unity资产、ResourceSystem和AssetLease，不替代Unity原生行为。
        private sealed class Source : IResourceSource
        {
            public ResourceSourceId Id => new("domain-fixture");
            public bool SupportsSynchronousLoad => true;
            internal readonly Dictionary<string, Object> Assets = new();
            internal int Releases;
            public void Validate(ResourceCatalogEntry entry)
            {
            }
            public ResourceSourceResult Load(ResourceCatalogEntry entry, Type type) => new(Assets[entry.Location.Locator]);
            public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type) => throw new NotSupportedException("本用例只验证同步领域登记，不伪装异步来源。");
            public void Release(Object asset) => Releases++;
        }

        [Test]
        public void ManagersOwnOnlyTheirRootLeasesAndReturnThemOnShutdown()
        {
            Assert.That(ResourceSystem.Current, Is.Null, "不能接管其他测试或应用的Resource权威。");
            Assert.That(SkillManager.ConfigCount + BuffManager.ConfigCount + MissionManager.ConfigCount, Is.Zero);
            var skill = ScriptableObject.CreateInstance<SkillsConfig>();
            var buff = ScriptableObject.CreateInstance<BuffsConfig>();
            var mission = ScriptableObject.CreateInstance<MissionsConfig>();
            ResourceSystem system = null;
            try
            {
                skill.name = "Domain.Lease.Skill";
                buff.name = "Domain.Lease.Buff";
                mission.name = "Domain.Lease.Mission";
                var source = new Source();
                source.Assets.Add("skill", skill);
                source.Assets.Add("buff", buff);
                source.Assets.Add("mission", mission);
                var entries = new List<ResourceCatalogEntry>();
                foreach (var pair in source.Assets)
                {
                    entries.Add(new ResourceCatalogEntry(pair.Key, new ResourceAddress("domain-fixture", pair.Key), new ResourceLocation(source.Id, pair.Key, pair.Value.GetType())));
                }

                using var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { new ResourceCatalog("domain-fixture", entries) }, new IResourceSource[] { source }));
                system = initialized.TakeResult();
                var skillAddress = new ResourceAddress("domain-fixture", "skill");
                using var independent = system.Acquire<SkillsConfig>(skillAddress);
                SkillManager.LoadConfig(system, skillAddress);
                SkillManager.LoadConfig(system, skillAddress);
                BuffManager.LoadConfig(system, new ResourceAddress("domain-fixture", "buff"));
                MissionManager.LoadConfig(system, new ResourceAddress("domain-fixture", "mission"));
                SkillManager.Shutdown();
                Assert.That(source.Releases, Is.Zero, "外部独立Lease仍持有Skill根。");
                independent.Dispose();
                Assert.That(source.Releases, Is.EqualTo(1));
                BuffManager.Shutdown();
                MissionManager.Shutdown();
                Assert.That(source.Releases, Is.EqualTo(3));
                Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
                Assert.That(ResourceSystem.Current, Is.SameAs(system), "领域Shutdown不得关闭Resource权威。");
            }
            finally
            {
                SkillManager.Shutdown();
                BuffManager.Shutdown();
                MissionManager.Shutdown();
                if (system != null && ReferenceEquals(ResourceSystem.Current, system))
                {
                    using var shutdown = system.ShutdownAsync();
                    Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
                }
                Object.DestroyImmediate(skill);
                Object.DestroyImmediate(buff);
                Object.DestroyImmediate(mission);
            }
        }
    }
}
