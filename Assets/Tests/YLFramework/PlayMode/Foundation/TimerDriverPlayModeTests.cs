using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Timing;
using Object = UnityEngine.Object;
using Timer = YLFramework.Timing.Timer;
using YLFramework.Async;
using YLFramework.Update;

namespace YLFramework.Foundation.Tests
{
    public class TimerDriverPlayModeTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly List<Component> _hosts = new();
        private readonly List<GameObject> _probes = new();
        private bool _isIsolated;
        private float _previousTimeScale;
        private bool _wasStopping;
        private bool _wasQuitting;
        private object _previousSession;
        private static Type DriverType => typeof(Timer).Assembly.GetType("YLFramework.Timing.Internal.TimerDriver", true);
        private static Type SessionType => typeof(Timer).Assembly.GetType("YLFramework.Singletons.SingletonSessionState", true);
        private static Component[] FindHosts() => Object.FindObjectsByType(DriverType,
            FindObjectsInactive.Include, FindObjectsSortMode.None).Cast<Component>().ToArray();

        [SetUp]
        public void SetUp()
        {
            _isIsolated = false;
            Assert.That(FindHosts(), Is.Empty, "不接管用户已有 TimerDriver，包括停用宿主。");
            Assert.That(DriverType.GetField("_instance", PrivateStatic).GetValue(null), Is.Null,
                "不接管尚未释放的静态 owner。");
            _previousTimeScale = Time.timeScale;
            _wasStopping = (bool)SessionType.GetField("_isExecutionStopping", PrivateStatic).GetValue(null);
            _wasQuitting = (bool)SessionType.GetField("_isApplicationQuitting", PrivateStatic).GetValue(null);
            _previousSession = SessionType.GetProperty("ExecutionSession", PrivateStatic).GetValue(null);
            Assert.That(SessionType.GetProperty("CanRunExecution", PrivateStatic).GetValue(null), Is.True,
                "测试不擅自打开用户已关闭的执行门禁。");
            _isIsolated = true;
            Time.timeScale = 1;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_isIsolated)
            {
                yield break;
            }

            try
            {
                foreach (var probe in _probes)
                {
                    if (probe)
                    {
                        Object.Destroy(probe);
                    }
                }

                _probes.Clear();
                var owner = DriverType.GetField("_instance", PrivateStatic).GetValue(null) as Component;
                if (!ReferenceEquals(owner, null) && _hosts.Contains(owner))
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
            }
            finally
            {
                Time.timeScale = _previousTimeScale;
                SessionType.GetField("<ExecutionSession>k__BackingField", PrivateStatic).SetValue(null, _previousSession);
                SessionType.GetField("_isExecutionStopping", PrivateStatic).SetValue(null, _wasStopping);
                SessionType.GetField("_isApplicationQuitting", PrivateStatic).SetValue(null, _wasQuitting);
            }
            yield return null;
        }

        private TimerHandle Post(Func<TimerHandle> post)
        {
            var before = FindHosts();
            Assert.That(before.All(_hosts.Contains), Is.True, "并行出现的非自有宿主不能被测试接管。");
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

        private TimerDriverPhaseProbe CreateProbe()
        {
            var obj = new GameObject("YLTimer.TestPhaseProbe");
            _probes.Add(obj);
            return obj.AddComponent<TimerDriverPhaseProbe>();
        }

        private static IEnumerator Until(Func<bool> condition)
        {
            var deadline = Time.realtimeSinceStartupAsDouble + 3;
            while (!condition() && Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
            }

            Assert.That(condition(), Is.True, "真实 Unity 帧未在期限内完成预期操作。");
        }

        private void AssertNoForeignExecutionHosts()
        {
            Assert.That(FindHosts().All(_hosts.Contains), Is.True, "不能 reset 非自有 TimerDriver。");
            foreach (var type in new[] { typeof(UpdateManager), typeof(CoroutineStarter) })
            {
                Assert.That(Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None),
                    Is.Empty, "会话 reset/quit 不得影响其他共享执行宿主：" + type.Name);
            }
        }

        private void ResetTimerSessionOnly()
        {
            AssertNoForeignExecutionHosts();
            // 定向身份模拟，不是多轮真实 Play；不调用公共 session reset，以免清理用户事件域和对象池。
            SessionType.GetField("<ExecutionSession>k__BackingField", PrivateStatic).SetValue(null, new object());
            DriverType.GetMethod("ResetSession", PrivateStatic).Invoke(null, null);
        }

        private static void Lifecycle(Component host, string method) =>
            DriverType.GetMethod(method, PrivateInstance).Invoke(host, null);

        [UnityTest]
        public IEnumerator InvalidPostsQueriesAndCleanupNeverCreateHost()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Timer.Delay(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => Timer.Delay(0, clock: TimerClock.RenderedFrame));
            Assert.Throws<ArgumentOutOfRangeException>(() => Timer.Repeat(0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Timer.DelayFrames(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Timer.RepeatFrames(0, 1, count: 0));
            var empty = default(TimerHandle);
            Assert.That(empty.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(empty.IsActive, Is.False);
            Assert.That(empty.IsFinished, Is.True);
            Assert.That(empty.RemainingSeconds, Is.Zero);
            Assert.That(empty.RemainingFrames, Is.Zero);
            Assert.That(empty.Cancel() || empty.Pause() || empty.Resume(), Is.False);
            using (var scope = new TimerScope())
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => scope.Delay(-1));
                Assert.That(scope.TryGet("missing", out var missing), Is.False);
                Assert.That(missing.IsActive, Is.False);
                Assert.That(scope.Contains("missing"), Is.False);
                Assert.That(scope.Cancel("missing"), Is.False);
                Assert.That(scope.Pause("missing"), Is.False);
                Assert.That(scope.Resume("missing"), Is.False);
                scope.CancelAll();
            }
            using (var slot = new ReusableTimer())
            {
                Assert.That(slot.Current.IsActive, Is.False);
            }

            var group = new TimerHandleGroup();
            group.Add(empty);
            group.CancelAll();
            group.Clear();
            Timer.Shutdown();
            Assert.That(FindHosts(), Is.Empty);
            yield break;
        }

        [UnityTest]
        public IEnumerator FirstHostInLateUpdateAnchorsFrameOneAtFirstRealUpdate()
        {
            var probe = CreateProbe();
            var registeredFrame = -1;
            var firstUpdate = -1;
            var frameOne = -1;
            var zeroSeconds = -1;
            var zeroFrames = -1;
            var inline = false;
            var firedAtAnchor = false;
            probe.OnLate = () =>
            {
                if (registeredFrame >= 0)
                {
                    return;
                }

                registeredFrame = Time.frameCount;
                Post(() => Timer.DelayFrames(1, () => frameOne = Time.frameCount));
                Post(() => Timer.Delay(0, () => zeroSeconds = Time.frameCount));
                Post(() => Timer.DelayFrames(0, () => zeroFrames = Time.frameCount));
                inline = frameOne >= 0 || zeroSeconds >= 0 || zeroFrames >= 0;
            };
            probe.OnUpdate = () =>
            {
                if (registeredFrame < 0 || firstUpdate >= 0)
                {
                    return;
                }

                firstUpdate = Time.frameCount;
                firedAtAnchor = frameOne >= 0 || zeroSeconds >= 0 || zeroFrames >= 0;
            };
            yield return Until(() => frameOne >= 0 && zeroSeconds >= 0 && zeroFrames >= 0);
            Assert.That(inline, Is.False);
            Assert.That(firstUpdate, Is.EqualTo(registeredFrame + 1));
            Assert.That(firedAtAnchor, Is.False, "冷启动首 Update 仅锚定，n=1 还须再等一个 Update。");
            Assert.That(frameOne, Is.EqualTo(firstUpdate + 1));
            Assert.That(zeroSeconds, Is.EqualTo(frameOne));
            Assert.That(zeroFrames, Is.EqualTo(frameOne));
        }

        [UnityTest]
        public IEnumerator WarmZeroPostsExcludeRegistrationFrameAndFrameRepeatUsesRealFrames()
        {
            var parentFrame = -1;
            var secondsFrame = -1;
            var framesFrame = -1;
            var inline = false;
            var ticks = new List<long>();
            var tickFrames = new List<int>();
            Post(() => Timer.DelayFrames(0, () =>
            {
                parentFrame = Time.frameCount;
                Post(() => Timer.Delay(0, () => secondsFrame = Time.frameCount));
                Post(() => Timer.DelayFrames(0, () => framesFrame = Time.frameCount));
                Post(() => Timer.RepeatFrames(0, 1, tick =>
                {
                    ticks.Add(tick);
                    tickFrames.Add(Time.frameCount);
                }, count: 2));
                inline = secondsFrame >= 0 || framesFrame >= 0 || ticks.Count != 0;
            }));
            yield return Until(() => secondsFrame >= 0 && framesFrame >= 0 && ticks.Count == 2);
            Assert.That(inline, Is.False);
            Assert.That(secondsFrame, Is.EqualTo(parentFrame + 1));
            Assert.That(framesFrame, Is.EqualTo(secondsFrame));
            CollectionAssert.AreEqual(new long[] { 1, 2 }, ticks);
            CollectionAssert.AreEqual(new[] { parentFrame + 1, parentFrame + 2 }, tickFrames);
        }

        [UnityTest]
        public IEnumerator FixedUpdateSecondsPostWaitsFromNextLegalUpdateSample()
        {
            var warm = false;
            Post(() => Timer.DelayFrames(0, () => warm = true));
            yield return Until(() => warm);
            var probe = CreateProbe();
            var registered = false;
            var inFixed = false;
            var anchor = -1d;
            var completed = -1d;
            var remainingAtAnchor = -1d;
            var firedAtAnchor = false;
            TimerHandle handle = default;
            const double Delay = 0.05;
            probe.OnFixed = () =>
            {
                if (registered)
                {
                    return;
                }

                registered = true;
                inFixed = Time.inFixedTimeStep;
                handle = Post(() => Timer.Delay(Delay, () => completed = Time.unscaledTimeAsDouble));
            };
            probe.OnUpdate = () =>
            {
                if (!registered || anchor >= 0)
                {
                    return;
                }

                anchor = Time.unscaledTimeAsDouble;
                remainingAtAnchor = handle.RemainingSeconds;
                firedAtAnchor = completed >= 0;
            };
            yield return Until(() => completed >= 0);
            Assert.That(inFixed, Is.True);
            Assert.That(firedAtAnchor, Is.False);
            Assert.That(remainingAtAnchor, Is.EqualTo(Delay).Within(0.000001));
            Assert.That(completed, Is.GreaterThanOrEqualTo(anchor + Delay - 0.000001));
        }

        [UnityTest]
        public IEnumerator ZeroTimeScaleStopsScaledButUnscaledRepeatContinues()
        {
            Time.timeScale = 0;
            var scaledCalls = 0;
            var rounds = new List<long>();
            var scaled = Post(() => Timer.Delay(0.02, () => scaledCalls++, TimerClock.Scaled));
            var unscaled = Post(() => Timer.Repeat(0, 0.02, round => rounds.Add(round), count: 2));
            yield return Until(() => rounds.Count == 2);
            Assert.That(scaledCalls, Is.Zero);
            Assert.That(scaled.IsActive, Is.True);
            Assert.That(unscaled.Status, Is.EqualTo(TimerState.Completed));
            CollectionAssert.AreEqual(new long[] { 1, 2 }, rounds);
            Time.timeScale = 1;
            yield return Until(() => scaledCalls == 1);
            Assert.That(scaled.Status, Is.EqualTo(TimerState.Completed));
        }

        [UnityTest]
        public IEnumerator AtUsesPastZeroAndOneFutureUnscaledConversion()
        {
            var warm = false;
            Post(() => Timer.DelayFrames(0, () => warm = true));
            yield return Until(() => warm);
            Time.timeScale = 0;
            var pastCalls = 0;
            var futureCalls = 0;
            var past = Post(() => Timer.At(DateTimeOffset.UtcNow.AddDays(-1), () => pastCalls++));
            var target = DateTimeOffset.UtcNow.AddSeconds(0.2).ToOffset(TimeSpan.FromHours(5.5));
            var future = Post(() => Timer.At(target, () => futureCalls++));
            Assert.That(pastCalls + futureCalls, Is.Zero, "At 不同步执行，包括过去时间。");
            Assert.That(future.RemainingSeconds, Is.GreaterThan(0), "未来时间转换为正的 Unscaled 等待。");
            var run = typeof(TimerHandle).GetField("_run", PrivateInstance).GetValue(future);
            var deadlineField = run.GetType().GetField("DueSeconds", BindingFlags.NonPublic | BindingFlags.Instance);
            var deadline = (double)deadlineField.GetValue(run);
            yield return null;
            Assert.That(deadlineField.GetValue(run), Is.EqualTo(deadline), "后续样本不重新按墙钟转换 deadline。");
            yield return Until(() => pastCalls == 1 && futureCalls == 1);
            Assert.That(past.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(future.Status, Is.EqualTo(TimerState.Completed));
            yield return null;
            Assert.That(pastCalls + futureCalls, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator ShutdownInvalidatesPendingAndPausedButSameSessionObjectsCanRestart()
        {
            using var scope = new TimerScope();
            using var slot = new ReusableTimer();
            var notifications = new List<TimerCancellationReason>();
            var pending = Post(() => Timer.Delay(60, onCancelled: notifications.Add));
            var paused = Post(() => Timer.RepeatFrames(60, 1, onCancelled: notifications.Add));
            Assert.That(paused.Pause(), Is.True);
            var scoped = Post(() => scope.Delay(60, onCancelled: notifications.Add));
            var stored = Post(() => slot.Start(60, onCancelled: notifications.Add));
            var oldHost = FindHosts().Single();
            Timer.Shutdown();
            foreach (var handle in new[] { pending, paused, scoped, stored })
            {
                Assert.That(handle.Status, Is.EqualTo(TimerState.Invalidated));
                Assert.That(handle.IsActive, Is.False);
                Assert.That(handle.Cancel(), Is.False);
                Assert.That(handle.Pause(), Is.False);
                Assert.That(handle.Resume(), Is.False);
            }
            Assert.That(notifications.Count, Is.EqualTo(4));
            Assert.That(notifications.All(reason => reason == TimerCancellationReason.SchedulerDisposed), Is.True);
            var calls = 0;
            var newScoped = Post(() => scope.Delay(0, () => calls++));
            var newStored = Post(() => slot.Start(0, () => calls++));
            Assert.That(FindHosts().Single(host => ((Behaviour)host).enabled), Is.Not.SameAs(oldHost));
            Assert.That(stored.Cancel() || stored.Pause() || stored.Resume(), Is.False, "旧 slot 代次不能控制新 run。");
            Assert.That(newStored.IsActive && newScoped.IsActive, Is.True);
            yield return Until(() => calls == 2);
        }

        [UnityTest]
        public IEnumerator DisabledDriverRejectsUntilShutdownAndDestroyedDriverCanRebuild()
        {
            var old = Post(() => Timer.Delay(60));
            var disabled = (Behaviour)FindHosts().Single();
            disabled.enabled = false;
            Assert.That(old.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.Throws<InvalidOperationException>(() => Post(() => Timer.Delay(0)));
            disabled.enabled = true;
            Assert.Throws<InvalidOperationException>(() => Post(() => Timer.DelayFrames(0)), "重新启用不复活已释放 owner。");
            Timer.Shutdown();
            var doomed = Post(() => Timer.Delay(60));
            var destroyed = FindHosts().Single(host => ((Behaviour)host).enabled);
            Object.DestroyImmediate(destroyed.gameObject);
            Assert.That(doomed.Status, Is.EqualTo(TimerState.Invalidated));
            var calls = 0;
            var current = Post(() => Timer.Delay(0, () => calls++));
            Assert.That(FindHosts().Single(host => ((Behaviour)host).enabled), Is.Not.SameAs(destroyed));
            Assert.That(old.Cancel() || doomed.Cancel(), Is.False);
            yield return Until(() => calls == 1);
            Assert.That(current.Status, Is.EqualTo(TimerState.Completed));
        }

        [UnityTest]
        public IEnumerator SimulatedSessionRejectsOldScopeSlotAndLateLifecycleThenQuitClosesGate()
        {
            AssertNoForeignExecutionHosts();
            using var scope = new TimerScope();
            using var slot = new ReusableTimer();
            var oldScoped = Post(() => scope.Delay(60));
            var oldStored = Post(() => slot.Start(60));
            var oldHost = FindHosts().Single();
            ResetTimerSessionOnly();
            Assert.That(((Behaviour)oldHost).enabled, Is.False);
            Assert.That(oldScoped.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(oldStored.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.Throws<InvalidOperationException>(() => Post(() => scope.Delay(0)));
            Assert.Throws<InvalidOperationException>(() => Post(() => slot.Start(0)));
            var calls = 0;
            var current = Post(() => Timer.DelayFrames(0, () => calls++));
            var newHost = FindHosts().Single(host => ((Behaviour)host).enabled);
            Lifecycle(oldHost, "OnApplicationQuit");
            Lifecycle(oldHost, "OnDestroy");
            Assert.That(current.IsActive, Is.True, "旧宿主迟到生命周期不能释放新 owner 或关闭其 gate。");
            Assert.That(oldScoped.Cancel() || oldStored.Cancel(), Is.False);
            yield return Until(() => calls == 1);
            var pending = Post(() => Timer.Delay(60));
            AssertNoForeignExecutionHosts();
            Lifecycle(newHost, "OnApplicationQuit");
            Assert.That(pending.Status, Is.EqualTo(TimerState.Invalidated));
            var count = FindHosts().Length;
            Assert.Throws<InvalidOperationException>(() => Post(() => Timer.Delay(0)));
            Timer.Shutdown();
            Assert.Throws<InvalidOperationException>(() => Post(() => Timer.DelayFrames(0)), "Shutdown 不绕过退出 gate。");
            Assert.That(FindHosts().Length, Is.EqualTo(count));
            yield return null;
            Assert.That(calls, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CallbackExceptionFaultsOnlyItsRunAndOtherTasksKeepGoing()
        {
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: YLTimer.ExpectedFailure"));
            var failed = Post(() => Timer.Repeat(0, 0.02,
                _ => throw new InvalidOperationException("YLTimer.ExpectedFailure")));
            var continued = 0;
            var survivor = Post(() => Timer.RepeatFrames(0, 1, _ => continued++, count: 2));
            yield return Until(() => continued == 2);
            Assert.That(failed.Status, Is.EqualTo(TimerState.Faulted));
            Assert.That(failed.ExecutionCount, Is.EqualTo(1));
            Assert.That(failed.IsActive, Is.False);
            Assert.That(survivor.Status, Is.EqualTo(TimerState.Completed));
            yield return null;
            Assert.That(failed.ExecutionCount, Is.EqualTo(1));
            Assert.That(continued, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator SceneUnloadKeepsDefaultDdolOwnerAndIdleHostAlive()
        {
            var scene = SceneManager.CreateScene("YLTimer.DriverScene");
            var previous = SceneManager.GetActiveScene();
            try
            {
                SceneManager.SetActiveScene(scene);
                var calls = 0;
                var handle = Post(() => Timer.DelayFrames(0, () => calls++));
                var host = FindHosts().Single();
                Assert.That(host.name, Is.EqualTo("YLFramework.Timing.TimerDriver"));
                Assert.That(host.gameObject.scene, Is.Not.EqualTo(scene));
                SceneManager.SetActiveScene(previous);
                yield return SceneManager.UnloadSceneAsync(scene);
                yield return Until(() => calls == 1);
                Assert.That(handle.Status, Is.EqualTo(TimerState.Completed));
                yield return null;
                Assert.That(FindHosts().Single(), Is.SameAs(host), "空闲默认 owner 不依赖原场景生命周期。");
                Assert.That(((Behaviour)host).isActiveAndEnabled, Is.True);
            }
            finally
            {
                SceneManager.SetActiveScene(previous);
                if (scene.IsValid() && scene.isLoaded)
                {
                    SceneManager.UnloadSceneAsync(scene);
                }
            }
        }
    }

    // 后置观察 TimerDriver 的真实 Update；不用 SendMessage 或手动 Advance 冒充引擎帧。
    [DefaultExecutionOrder(32000)]
    public sealed class TimerDriverPhaseProbe : MonoBehaviour
    {
        internal Action OnUpdate;
        internal Action OnLate;
        internal Action OnFixed;
        private void Update() => OnUpdate?.Invoke();
        private void LateUpdate() => OnLate?.Invoke();
        private void FixedUpdate() => OnFixed?.Invoke();
    }
}
