using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Timing;
using Object = UnityEngine.Object;
using Timer = YLFramework.Timing.Timer;

namespace YLFramework.Tests
{
    public class TimingPlayModeTests
    {
        private readonly List<Component> _hosts = new();
        private bool _isIsolated;
        private static Type DriverType => typeof(Timer).Assembly.GetType("YLFramework.Timing.Internal.TimerDriver", true);
        private static Component CurrentHost => DriverType.GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)
            .GetValue(null) as Component;
        private static Component[] FindHosts() => Object.FindObjectsByType(DriverType,
            FindObjectsInactive.Include, FindObjectsSortMode.None).Cast<Component>().ToArray();

        [SetUp]
        public void SetUp()
        {
            _isIsolated = false;
            Assert.That(FindHosts(), Is.Empty, "不接管用户已有或停用的 TimerDriver。");
            Assert.That(CurrentHost, Is.Null, "不接管尚未释放的静态 owner。");
            _isIsolated = true;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_isIsolated)
            {
                yield break;
            }

            if (!ReferenceEquals(CurrentHost, null) && _hosts.Contains(CurrentHost))
            {
                Timer.Shutdown();
            }

            foreach (var host in _hosts)
            {
                if (host)
                {
                    Object.Destroy(host.gameObject);
                }
            }

            _hosts.Clear();
            yield return null;
        }

        private TimerHandle Post(Func<TimerHandle> post)
        {
            var before = FindHosts();
            Assert.That(before.All(_hosts.Contains), Is.True, "不接管并行创建的非自有宿主。");
            try
            {
                return post();
            }
            finally
            {
                foreach (var host in FindHosts())
                {
                    if (!before.Contains(host) && !_hosts.Contains(host))
                    {
                        _hosts.Add(host);
                    }
                }
            }
        }

        [UnityTest]
        public IEnumerator RepeatedSameFrameShutdownCannotReadoptOldHostsOrClearReplacementTasks()
        {
            var old = Post(() => Timer.Delay(100));
            var first = CurrentHost;
            Timer.Shutdown();
            var middle = Post(() => Timer.Delay(100));
            var second = CurrentHost;
            Timer.Shutdown();
            var pending = Post(() => Timer.Delay(100));
            var current = CurrentHost;
            Assert.That(current, Is.Not.SameAs(first));
            Assert.That(current, Is.Not.SameAs(second));
            Assert.That(old.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(middle.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(old.Cancel() || middle.Cancel(), Is.False);
            yield return null;
            Assert.That(pending.Status, Is.EqualTo(TimerState.Scheduled));
            Assert.That(CurrentHost, Is.SameAs(current));
            Assert.That(FindHosts(), Has.Length.EqualTo(1));
        }
    }
}
