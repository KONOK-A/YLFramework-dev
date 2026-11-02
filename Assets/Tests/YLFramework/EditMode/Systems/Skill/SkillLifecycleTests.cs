using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YLFramework.Events;
using YLFramework.Skill;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Module.Domain")]
    public class SkillLifecycleTests
    {
        private sealed class Host : IEquippableSkill
        {
            public Dictionary<int, SkillBase> Skills { get; } = new();
            public bool CanRelease() => true;
        }
        public sealed class Probe : SkillBase
        {
            public int Updates;
            public int Completes;
            public int Interrupts;
            protected override void OnUpdate() => Updates++;
            protected override void OnReleaseComplete() => Completes++;
            protected override void OnInterrupt() => Interrupts++;
        }
        private static void Advance(SkillBase skill, float delta) => typeof(SkillBase).GetMethod("Advance", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(skill, new object[] { delta });

        [Test]
        public void ReleaseCompletionThenCooldownStaysAtOneAndCompletesOnce()
        {
            var skill = new Probe { Releaser = new Host(), Duration = 1, CoolingTime = 2 };
            var completed = 0;
            var callbacks = new List<string>();
            skill.ReleaseStarted = current =>
            {
                Assert.That(current, Is.SameAs(skill));
                callbacks.Add("release");
            };
            skill.Releasing = (current, progress) =>
            {
                Assert.That(current, Is.SameAs(skill));
                Assert.That(progress, Is.EqualTo(1));
                callbacks.Add("releasing");
            };
            skill.ReleaseCompleted = current =>
            {
                Assert.That(current, Is.SameAs(skill));
                callbacks.Add("released");
            };
            skill.CooldownStarted = current =>
            {
                Assert.That(current, Is.SameAs(skill));
                callbacks.Add("cooldown");
            };
            skill.Cooling = (current, progress) =>
            {
                Assert.That(current, Is.SameAs(skill));
                Assert.That(progress, Is.InRange(0f, 1f));
                callbacks.Add("cooling");
            };
            skill.CooldownCompleted = current =>
            {
                Assert.That(current, Is.SameAs(skill));
                completed++;
                callbacks.Add("ready");
            };
            Assert.That(skill.Release(), Is.EqualTo(ReleaseSkillStatus.Success));
            Advance(skill, 1);
            Assert.That(skill.Completes, Is.EqualTo(1));
            Advance(skill, 1);
            Assert.That(skill.CooldownProgress, Is.EqualTo(0.5f));
            Advance(skill, 1);
            Advance(skill, 10);
            Assert.That(skill.CooldownProgress, Is.EqualTo(1));
            Assert.That(completed, Is.EqualTo(1));
            Assert.That(callbacks, Is.EqualTo(new[] { "release", "releasing", "released", "cooldown", "cooling", "cooling", "ready" }));
        }

        [Test]
        public void CancellationKeepsCooldownAndInterruptCompletesExactlyOnce()
        {
            var skill = new Probe { Releaser = new Host(), Duration = 5, CoolingTime = 2, ActiveCancellation = true };
            var interrupted = 0;
            skill.Interrupted = current =>
            {
                Assert.That(current, Is.SameAs(skill));
                interrupted++;
            };
            skill.Release();
            skill.Cancel();
            skill.Interrupt();
            Assert.That(skill.IsReleasing, Is.False);
            Assert.That(skill.IsCooldown, Is.False);
            Assert.That(skill.Interrupts, Is.EqualTo(1));
            Assert.That(skill.Completes, Is.EqualTo(1));
            Assert.That(interrupted, Is.EqualTo(1));
        }

        [Test]
        public void RemovingDuringProgressDoesNotContinueOldLeaseCallbacks()
        {
            var skill = new Probe { Releaser = new Host(), Duration = 1 };
            skill.Releasing = (_, __) => typeof(SkillBase).GetMethod("ClearRuntime", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(skill, null);
            skill.Release();
            Advance(skill, 2);
            Assert.That(skill.Updates, Is.Zero);
            Assert.That(skill.Completes, Is.Zero);
            Assert.That(skill.Releaser, Is.Null);
            Assert.That(skill.Releasing, Is.Null);
        }
        [Test]
        public void SkillMustOfferRealReleaseAndShutdownBoundaries()
        {
            var assembly = typeof(EventChannel).Assembly;
            var skill = assembly.GetType("YLFramework.Skill.SkillBase");
            Assert.That(skill, Is.Not.Null, "领域释放执行器尚未承接。");
            Assert.That(skill.GetMethod("Release"), Is.Not.Null);
            var manager = assembly.GetType("YLFramework.Skill.SkillManager");
            Assert.That(manager, Is.Not.Null, "宿主/池/三相位责任尚未接入。");
            Assert.That(manager.GetMethod("Shutdown"), Is.Not.Null);
        }
    }
}
