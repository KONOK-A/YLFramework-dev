using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
            public Action<Probe> Late;
            protected internal override void OnLateUpdate() => Late?.Invoke(this);
            protected internal override void OnRemove()
            {
                var callback = Removed;
                Removed = null;
                callback?.Invoke(this);
            }
        }
        private ObjectPool<SkillBase> _pool;
        private bool _isOwned;
        private static readonly BindingFlags _privateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        private static void Reset(SkillBase skill)
        {
            typeof(SkillBase).GetMethod("ClearRuntime", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(skill, null);
            ((Probe)skill).Late = null;
        }

        private static void Dispatch()
        {
            try
            {
                typeof(SkillManager).GetMethod("Dispatch", _privateStatic).Invoke(null, new object[] { 2 });
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
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

        [Test]
        public void NestedDispatchUsesIndependentSnapshotsAndPreservesBothIterations()
        {
            var host = new Host();
            var first = Attach(host, 1);
            var second = Attach(host, 2);
            int firstCalls = 0;
            int secondCalls = 0;
            bool nested = false;
            first.Late = _ =>
            {
                firstCalls++;
                if (!nested)
                {
                    nested = true;
                    Dispatch();
                }
            };
            second.Late = _ => secondCalls++;
            Dispatch();
            Assert.That(firstCalls, Is.EqualTo(2));
            Assert.That(secondCalls, Is.EqualTo(2));
            Assert.That(host.Skills.Count, Is.EqualTo(2));
        }

        [Test]
        public void DispatchReplacementWithSameIdIsNotConsumedByTheOldSnapshot()
        {
            var host = new Host();
            var first = Attach(host, 1);
            var old = Attach(host, 2);
            int staleCalls = 0;
            int currentCalls = 0;
            Probe replacement = null;
            old.Late = _ => staleCalls++;
            first.Late = _ =>
            {
                first.Late = null;
                SkillManager.RemoveSkills(host, 2);
                Assert.That(_pool.CurStoreCount, Is.Zero, "Return is deferred until the outer dispatcher exits.");
                replacement = Attach(host, 2);
                replacement.Late = __ => currentCalls++;
            };
            Dispatch();
            Assert.That(replacement, Is.Not.SameAs(old));
            Assert.That(staleCalls + currentCalls, Is.Zero);
            Assert.That(_pool.CurStoreCount, Is.EqualTo(1));
            Dispatch();
            Assert.That(currentCalls, Is.EqualTo(1));
        }

        [Test]
        public void ClosingAnotherHostDoesNotDispatchANewHostFromTheSameSnapshot()
        {
            var firstHost = new Host();
            var oldHost = new Host();
            var newHost = new Host();
            var first = Attach(firstHost, 1);
            var old = Attach(oldHost, 1);
            Probe next = null;
            int oldCalls = 0;
            int newCalls = 0;
            old.Late = _ => oldCalls++;
            first.Late = _ =>
            {
                first.Late = null;
                SkillManager.CloseHost(oldHost);
                next = Attach(newHost, 1);
                next.Late = __ => newCalls++;
            };
            Dispatch();
            Assert.That(oldHost.Skills, Is.Empty);
            Assert.That(next, Is.Not.SameAs(old));
            Assert.That(oldCalls + newCalls, Is.Zero);
            Dispatch();
            Assert.That(newCalls, Is.EqualTo(1));
        }

        [Test]
        public void DispatchExceptionReturnsSnapshotsWithoutChangingBusinessFailure()
        {
            var host = new Host();
            var first = Attach(host, 1);
            var second = Attach(host, 2);
            var hosts = ListPool<IEquippableSkill>.Allocate();
            var skills = ListPool<(SkillBase skill, int version)>.Allocate();
            hosts.RecycleToPool();
            skills.RecycleToPool();
            var error = new InvalidOperationException("dispatch-marker");
            first.Late = _ => throw error;
            Assert.That(Assert.Throws<InvalidOperationException>(Dispatch), Is.SameAs(error));
            Assert.That(typeof(SkillManager).GetField("_depth", _privateStatic).GetValue(null), Is.EqualTo(0));
            var returnedHosts = ListPool<IEquippableSkill>.Allocate();
            var returnedSkills = ListPool<(SkillBase skill, int version)>.Allocate();
            Assert.That(returnedHosts, Is.SameAs(hosts));
            Assert.That(returnedSkills, Is.SameAs(skills));
            returnedHosts.RecycleToPool();
            returnedSkills.RecycleToPool();
            first.Late = null;
            int calls = 0;
            second.Late = _ => calls++;
            Dispatch();
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void ShutdownInsideDispatchSkipsPeersAndFlushesOnlyAfterTheCallback()
        {
            var host = new Host();
            var first = Attach(host, 1);
            var second = Attach(host, 2);
            int peers = 0;
            second.Late = _ => peers++;
            first.Late = _ =>
            {
                SkillManager.Shutdown();
                Assert.That(_pool.CurStoreCount, Is.Zero);
            };
            Dispatch();
            Assert.That(peers, Is.Zero);
            Assert.That(host.Skills, Is.Empty);
            Assert.That(SkillManager.HostCount, Is.Zero);
            Assert.That(first.Releaser, Is.Null);
            Assert.Throws<ObjectDisposedException>(() => _pool.Allocate());
        }
    }
}
