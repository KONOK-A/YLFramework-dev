using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YLFramework.Pooling;
using YLFramework.Skill;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Module.Domain")]
    public class SkillBatchReentryTests
    {
        private sealed class Host : IEquippableSkill
        {
            public Dictionary<int, SkillBase> Skills { get; } = new();
            public bool CanRelease() => true;
        }
        public sealed class Probe : SkillBase
        {
            public Action<Probe> Removed;
#if UNITY_EDITOR
            protected internal override void OnRemove()
#else
            protected override void OnRemove()
#endif
            {
                var callback = Removed;
                Removed = null;
                callback?.Invoke(this);
            }
        }
        private ObjectPool<SkillBase> _pool;
        private bool _isOwned;
        private static readonly BindingFlags _privateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        private static void Reset(SkillBase skill) => typeof(SkillBase).GetMethod("ClearRuntime", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(skill, null);
        [SetUp]
        public void Setup()
        {
            Assert.That(SkillManager.ConfigCount + SkillManager.HostCount, Is.Zero);
            _pool = new ObjectPool<SkillBase>(() => new Probe(), reset: Reset);
            ((Dictionary<Type, ObjectPool<SkillBase>>)typeof(SkillManager).GetField("_pools", _privateStatic).GetValue(null)).Add(typeof(Probe), _pool);
            _isOwned = true;
        }
        [TearDown]
        public void Cleanup()
        {
            if (_isOwned)
            {
                SkillManager.Shutdown();
                _isOwned = false;
            }
        }

        // 仅安排真实CLR技能和池的状态，不构造或替代任何Unity对象。
        private Probe Attach(Host host, int id)
        {
            var skill = (Probe)_pool.Allocate();
            skill.Releaser = host;
            typeof(SkillBase).GetField("_isRemoving", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(skill, false);
            typeof(SkillBase).GetProperty("IsReleasing").SetValue(skill, true);
            host.Skills.Add(id, skill);
            var hosts = (List<IEquippableSkill>)typeof(SkillManager).GetField("_hosts", _privateStatic).GetValue(null);
            if (!hosts.Contains(host))
            {
                hosts.Add(host);
            }

            return skill;
        }

        [Test]
        public void InterruptSnapshotCannotInterruptAnotherOwnersReplacementLease()
        {
            var first = new Host();
            var other = new Host();
            var a = Attach(first, 1);
            Attach(first, 2);
            Probe replacement = null;
            var interrupts = 0;
            a.Interrupted = _ =>
            {
                SkillManager.RemoveSkills(first, 2);
                replacement = Attach(other, 2);
                replacement.Interrupted = __ => interrupts++;
            };
            SkillManager.InterruptAllSkill(first);
            Assert.That(replacement.IsReleasing, Is.True);
            Assert.That(interrupts, Is.Zero);
        }

        [Test]
        public void BatchRemovalDoesNotConsumeNewInstanceWithSameId()
        {
            var host = new Host();
            var a = Attach(host, 1);
            Attach(host, 2);
            Probe replacement = null;
            a.Removed = _ =>
            {
                SkillManager.RemoveSkills(host, 2);
                replacement = Attach(host, 2);
            };
            SkillManager.RemoveSkills(host, new[] { 1, 2 });
            Assert.That(host.Skills.TryGetValue(2, out var current), Is.True);
            Assert.That(current, Is.SameAs(replacement));
        }

        [Test]
        public void BatchRemovalContinuesCleanupAfterBusinessException()
        {
            var host = new Host();
            var a = Attach(host, 1);
            Attach(host, 2);
            a.Removed = _ => throw new InvalidOperationException("remove-marker");
            Assert.Throws<AggregateException>(() => SkillManager.RemoveSkills(host, new[] { 1, 2 }));
            Assert.That(host.Skills, Is.Empty);
            Assert.That(SkillManager.HostCount, Is.Zero);
        }
    }
}
