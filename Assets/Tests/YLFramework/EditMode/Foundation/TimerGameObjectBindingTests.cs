using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Timing;
using Object = UnityEngine.Object;

namespace YLFramework.Foundation.Tests
{
    public class TimerGameObjectBindingTests
    {
        private TimerScheduler _scheduler;
        private GameObject _a;
        private GameObject _b;

        [SetUp]
        public void SetUp()
        {
            _scheduler = new TimerScheduler(0, 0, 0);
            _a = new GameObject("YLTimer.BindingA");
            _b = new GameObject("YLTimer.BindingB");
        }

        [TearDown]
        public void TearDown()
        {
            _scheduler.Dispose();
            if (_a)
            {
                Object.DestroyImmediate(_a);
            }

            if (_b)
            {
                Object.DestroyImmediate(_b);
            }
        }

        private static int ListenerCount(GameObject obj)
        {
            var type = typeof(TimerScheduler).Assembly.GetType("YLFramework.Timing.Internal.TimerLifetimeListener", true);
            var listener = obj.GetComponent(type);
            if (!listener)
            {
                return 0;
            }

            var bindings = type.GetField("_bindings", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(listener);
            return (int)bindings.GetType().GetProperty("Count").GetValue(bindings);
        }

        [Test]
        public void RebindingRemovesOldListenerAndPausedRunFollowsNewOwner()
        {
            var cancels = 0;
            var handle = _scheduler.Delay(10, onCancelled: _ => cancels++);
            Assert.That(handle.BindTo(_a), Is.True);
            Assert.That(handle.Pause(), Is.True);
            Assert.That(handle.BindTo(_b), Is.True);
            Assert.That(ListenerCount(_a), Is.Zero);
            _a.SetActive(false);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Paused));
            _b.SetActive(false);
            _scheduler.Advance(0, 0, 1);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(cancels, Is.EqualTo(1));
            Assert.That(ListenerCount(_b), Is.Zero);
        }

        [Test]
        public void NeverActiveDestroyedOwnerIsFoundEvenForPausedRun()
        {
            _a.SetActive(false);
            var handle = _scheduler.Delay(10);
            Assert.That(handle.BindTo(_a, TimerGameObjectLifetime.CancelOnDestroy), Is.True);
            handle.Pause();
            Object.DestroyImmediate(_a);
            _scheduler.Advance(0, 0, 1);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Cancelled));
        }

        [Test]
        public void InvalidAndInactiveOwnersRejectActiveBindingWithoutSuccessfulCallback()
        {
            _a.SetActive(false);
            var calls = 0;
            var handle = _scheduler.Delay(0, () => calls++);
            Assert.That(handle.BindTo(_a), Is.False);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Cancelled));
            var missing = _scheduler.Delay(0, () => calls++);
            Assert.That(missing.BindTo(null), Is.False);
            _scheduler.Advance(1, 1, 1);
            Assert.That(calls, Is.Zero);
            Assert.That(ListenerCount(_a), Is.Zero);
            Assert.Throws<ArgumentOutOfRangeException>(() => missing.BindTo(_a, (TimerGameObjectLifetime)99));
        }

        [Test]
        public void DestroyOnlyBindingSurvivesDisableAndListenerComponentDisable()
        {
            var handle = _scheduler.Delay(10);
            handle.BindTo(_a, TimerGameObjectLifetime.CancelOnDestroy);
            var type = typeof(TimerScheduler).Assembly.GetType("YLFramework.Timing.Internal.TimerLifetimeListener", true);
            ((Behaviour)_a.GetComponent(type)).enabled = false;
            _a.SetActive(false);
            _scheduler.Advance(0, 0, 1);
            Assert.That(handle.IsActive, Is.True);
            Object.DestroyImmediate(_a);
            _scheduler.Advance(0, 0, 2);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Cancelled));
        }

        [Test]
        public void TerminalPathsReleaseBindingBeforeCallbacksAndClearRunReferences()
        {
            var handle = _scheduler.Delay(0, () =>
            {
                Assert.That(ListenerCount(_a), Is.Zero);
                _a.SetActive(false);
            }, onCancelled: _ => Assert.Fail("成功末轮不能 Abort"));
            handle.BindTo(_a);
            _scheduler.Advance(0, 0, 1);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Completed));
            var run = typeof(TimerHandle).GetField("_run", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(handle);
            Assert.That(run.GetType().GetField("Binding", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(run), Is.Null);
            _a.SetActive(true);
            var fault = _scheduler.Delay(0, () => throw new InvalidOperationException("binding fault"));
            fault.BindTo(_a);
            Assert.Throws<InvalidOperationException>(() => _scheduler.Advance(0, 0, 2));
            Assert.That(fault.Status, Is.EqualTo(TimerState.Faulted));
            Assert.That(ListenerCount(_a), Is.Zero);
            var invalid = _scheduler.Delay(10);
            invalid.BindTo(_a);
            _scheduler.Dispose();
            Assert.That(invalid.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(ListenerCount(_a), Is.Zero);
        }

        [Test]
        public void OwnerFallbackAttemptsAllCancellationsAndReadyTasksBeforeReportingFailure()
        {
            var failure = new InvalidOperationException("binding cancellation failure");
            var first = _scheduler.Delay(100, onCancelled: _ => throw failure);
            var notified = 0;
            var second = _scheduler.Delay(100, onCancelled: _ => notified++);
            first.BindTo(_a);
            second.BindTo(_a);
            second.Pause();
            var calls = 0;
            var unrelated = _scheduler.Delay(0, () => calls++);
            _a.SetActive(false);
            Assert.That(Assert.Throws<InvalidOperationException>(() => _scheduler.Advance(0, 0, 1)), Is.SameAs(failure));
            Assert.That(first.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(second.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(notified, Is.EqualTo(1));
            Assert.That(ListenerCount(_a), Is.Zero);
            Assert.That(unrelated.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void DisableSnapshotCannotCancelReenabledNewGeneration()
        {
            using var slot = new ReusableTimer(_scheduler);
            TimerHandle next = default;
            var cancels = 0;
            var old = slot.Start(10, onCancelled: _ =>
            {
                cancels++;
                _a.SetActive(true);
                next = slot.Start(10);
                next.BindTo(_a);
            });
            old.BindTo(_a);
            _a.SetActive(false);
            _scheduler.Advance(0, 0, 1);
            Assert.That(old.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(next.IsActive, Is.True);
            Assert.That(cancels, Is.EqualTo(1));
            Assert.That(ListenerCount(_a), Is.EqualTo(1));
        }
    }
}
