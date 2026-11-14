using System.Reflection;
using NUnit.Framework;
using YLFramework.Mission;
using YLFramework.Pooling;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Module.Domain")]
    public class MissionPoolReuseTests
    {
        [Test]
        public void ManagerResetContractReusesIdentityWithoutPreviousCallbacksOrState()
        {
            var reset = typeof(MissionBase).GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic);
            using var pool = new ObjectPool<MissionBase>(() => new MissionBase(), reset: value => reset.Invoke(value, null));
            var mission = pool.Allocate();
            int previousCallbacks = 0;
            mission.UserData = new object();
            mission.StatusChanged = _ => previousCallbacks++;
            mission.ProgressValueChanged = (_, __) => previousCallbacks++;
            // 安排真实CLR回收前状态，不为离线逻辑伪造ScriptableObject配置。
            typeof(MissionBase).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(mission, MissionState.InProgress);
            var version = typeof(MissionBase).GetField("_version", BindingFlags.Instance | BindingFlags.NonPublic);
            int previousVersion = (int)version.GetValue(mission);
            Assert.That(pool.Recycle(mission), Is.True);
            var next = pool.Allocate();
            Assert.That(next, Is.SameAs(mission));
            Assert.That(next.State, Is.EqualTo(MissionState.NotStarted));
            Assert.That(next.UserData, Is.Null);
            Assert.That(next.StatusChanged, Is.Null);
            Assert.That(next.ProgressValueChanged, Is.Null);
            Assert.That((int)version.GetValue(next), Is.GreaterThan(previousVersion));
            Assert.That(previousCallbacks, Is.Zero);
            pool.Discard(next);
        }
    }
}
