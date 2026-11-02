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
    public class TimerScopePlayModeTests
    {
        private TimerScope _scope;
        private TimerScope _otherScope;
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
            _scope = new TimerScope();
            _otherScope = new TimerScope();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_isIsolated)
            {
                yield break;
            }

            _scope.Dispose();
            _otherScope.Dispose();
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

        private static IEnumerator Until(Func<bool> condition)
        {
            var limit = Time.realtimeSinceStartupAsDouble + 3;
            while (!condition() && Time.realtimeSinceStartupAsDouble < limit)
            {
                yield return null;
            }

            Assert.That(condition(), Is.True, "真实 Unity 帧未在期限内完成预期操作。");
        }

        [UnityTest]
        public IEnumerator PauseFreezesAcrossFramesWithoutBlockingOtherDueTasks()
        {
            var calls = 0;
            var paused = Post(() => _scope.PostNamed("cooldown", 0.04, () => calls++));
            Assert.That(_scope.Pause("cooldown"), Is.True);
            var remaining = paused.RemainingSeconds;
            var otherCalls = 0;
            var other = Post(() => Timer.Delay(0.01, () => otherCalls++));
            yield return new WaitForSecondsRealtime(0.08f);
            yield return Until(() => otherCalls == 1);
            Assert.That(paused.RemainingSeconds, Is.EqualTo(remaining));
            Assert.That(paused.Status, Is.EqualTo(TimerState.Paused));
            Assert.That(calls, Is.Zero);
            Assert.That(other.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(_scope.Resume("cooldown"), Is.True);
            yield return Until(() => calls == 1);
            Assert.That(paused.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(_scope.Contains("cooldown"), Is.False);
        }

        [UnityTest]
        public IEnumerator LastCallbackCanRebuildHostAndUseSameScopeNameWithoutOldCleanup()
        {
            TimerHandle replacement = default;
            var created = false;
            var calls = 0;
            var old = Post(() => _scope.PostNamed("rebuild", 0, () =>
            {
                Assert.That(_scope.Contains("rebuild"), Is.False);
                Timer.Shutdown();
                replacement = Post(() => _scope.PostNamed("rebuild", 100, () => calls++));
                created = true;
            }));
            var oldHost = CurrentHost;
            yield return Until(() => created);
            yield return null;
            Assert.That(old.Status, Is.EqualTo(TimerState.Completed), "末轮在 callback 前预留完成，不被 Shutdown 改为取消。");
            Assert.That(old.Cancel(), Is.False);
            Assert.That(_scope.TryGet("rebuild", out var found), Is.True);
            Assert.That(found, Is.EqualTo(replacement));
            Assert.That(replacement.Status, Is.EqualTo(TimerState.Scheduled));
            Assert.That(calls, Is.Zero);
            Assert.That(CurrentHost, Is.Not.SameAs(oldHost));
            Assert.That(FindHosts(), Has.Length.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator OwnerScopeDisposeCancelsOnlyItsTasksAndDoesNotShutdownHost()
        {
            var owned = Post(() => _scope.PostNamed("scope", 100));
            owned.Pause();
            var other = Post(() => _otherScope.PostNamed("scope", 100));
            var global = Post(() => Timer.Delay(100));
            var host = CurrentHost;
            _scope.Dispose();
            yield return null;
            Assert.That(owned.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(other.Status, Is.EqualTo(TimerState.Scheduled));
            Assert.That(global.Status, Is.EqualTo(TimerState.Scheduled));
            Assert.That(CurrentHost, Is.SameAs(host));
            Assert.That(_otherScope.TryGet("scope", out var found), Is.True);
            Assert.That(found, Is.EqualTo(other));
            Assert.That(_scope.Contains("scope"), Is.False);
        }
    }
}
