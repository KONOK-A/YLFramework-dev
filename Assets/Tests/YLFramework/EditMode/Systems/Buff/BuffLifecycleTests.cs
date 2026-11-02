using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YLFramework.Buff;
using YLFramework.Events;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Module.Domain")]
    public class BuffLifecycleTests
    {
        private sealed class Host : IAddableBuff
        {
            public List<BuffBase> Buffs { get; } = new();
            public bool CanAdd(int id) => true;
            public void OnAddBuff(BuffBase buff)
            {
            }
            public void OnRemoveBuff(BuffBase buff)
            {
            }
        }
        private static void Advance(BuffBase buff, float delta) => typeof(BuffBase).GetMethod("Advance", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(buff, new object[] { delta });

        [TestCase(0, -1)]
        [TestCase(-1, -1)]
        [TestCase(4, 0.5f)]
        public void DurationKeepsPermanentAndTimedProgress(float duration, float expected)
        {
            var buff = new BuffBase { Duration = duration };
            Advance(buff, 2);
            Assert.That(buff.Progress, Is.EqualTo(expected));
        }

        [Test]
        public void PoolResetReleasesHostUserDataAndElapsedState()
        {
            var buff = new BuffBase { Duration = 5, UserData = new object() };
            typeof(BuffBase).GetProperty("Owner").SetValue(buff, new Host());
            Advance(buff, 3);
            typeof(BuffBase).GetMethod("ClearRuntime", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(buff, null);
            Assert.That(buff.Owner, Is.Null);
            Assert.That(buff.UserData, Is.Null);
            Assert.That(buff.ExistedTime, Is.Zero);
            Assert.That(buff.BuffData, Is.Null);
        }
        [Test]
        public void BuffMustOfferDurationAndExplicitHostShutdown()
        {
            var assembly = typeof(EventChannel).Assembly;
            var buff = assembly.GetType("YLFramework.Buff.BuffBase");
            Assert.That(buff, Is.Not.Null, "Buff持续时间执行器尚未承接。");
            Assert.That(buff.GetProperty("Progress"), Is.Not.Null);
            var manager = assembly.GetType("YLFramework.Buff.BuffManager");
            Assert.That(manager, Is.Not.Null);
            Assert.That(manager.GetMethod("CloseHost"), Is.Not.Null);
        }
    }
}
