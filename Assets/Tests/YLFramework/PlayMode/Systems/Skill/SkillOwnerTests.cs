using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Skill;

namespace YLFramework.Tests.Extensions.Domain
{
    public class SkillOwnerTests
    {
        [SetUp] public void Isolated() => Assert.That(SkillManager.ConfigCount, Is.Zero, "只在无既有领域配置的验收场景运行。");
        private sealed class Host : IEquippableSkill
        {
            public Dictionary<int, SkillBase> Skills { get; } = new();
            public bool CanRelease() => true;
        }
        [UnityEngine.Scripting.Preserve]
        public sealed class ReentrantSkill : SkillBase
        {
            protected internal override void OnRemove() => SkillManager.RemoveSkills(Releaser, SkillData.id);
        }

        [Test]
        public void ConfigIsCopiedAndClrHostCloseReturnsSkillAndCallbacks()
        {
            var config = ScriptableObject.CreateInstance<SkillsConfig>();
            var data = ScriptableObject.CreateInstance<SkillConfigData>();
            try
            {
                config.name = "Domain.Skill.Config";
                data.id = 100;
                data.cooldownSeconds = 2;
                data.duration = 1;
                data.skillClassFullName = typeof(ReentrantSkill).FullName;
                config.skills.Add(data);
                SkillManager.AddConfig(config);
                var firstHost = new Host();
                var skill = SkillManager.AddSkills(firstHost, 100);
                Assert.That(skill.SkillData, Is.Not.SameAs(data));
                skill.SkillData.cooldownSeconds = 99;
                Assert.That(data.cooldownSeconds, Is.EqualTo(2));
                skill.ReleaseStarted = _ => SkillManager.CloseHost(firstHost);
                skill.Release();
                Assert.That(firstHost.Skills, Is.Empty);
                Assert.That(skill.Releaser, Is.Null);
                Assert.That(skill.SkillData, Is.Null);
                var secondHost = new Host();
                var next = SkillManager.AddSkills(secondHost, 100);
                Assert.That(next, Is.SameAs(skill));
                Assert.That(next.ReleaseStarted, Is.Null);
                Assert.That(next.CoolingTime, Is.EqualTo(2));
                SkillManager.Shutdown();
                Assert.That(secondHost.Skills, Is.Empty);
            }
            finally
            {
                SkillManager.Shutdown();
                Object.DestroyImmediate(config);
                Object.DestroyImmediate(data);
            }
        }
    }
}
