using System;
using System.Reflection;
using NUnit.Framework;
using YLFramework.Timing;
using Timer = YLFramework.Timing.Timer;

namespace YLFramework.Foundation.Tests
{
    public class ReusableTimerTests
    {
        [Test]
        public void EachStartChangesIdentityAndReusesPrivateStorage()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var reusable = new ReusableTimer(scheduler);
            var old = reusable.Start(0);
            scheduler.Advance(0, 0, 1);
            Assert.That(old.Status, Is.EqualTo(TimerState.Completed));
            var next = reusable.Start(5);
            var runField = typeof(TimerHandle).GetField("_run", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(runField.GetValue(next), Is.SameAs(runField.GetValue(old)));
            Assert.That(old.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(old.Cancel() || old.Pause() || old.Resume(), Is.False);
            Assert.That(next.Status, Is.EqualTo(TimerState.Scheduled));
            Assert.That(next.RemainingSeconds, Is.EqualTo(5));
        }

        [Test]
        public void ActiveStartRejectsWithoutChangingCurrentRun()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var reusable = new ReusableTimer(scheduler);
            var current = reusable.Start(5);
            current.Pause();
            Assert.Throws<InvalidOperationException>(() => reusable.StartFrames(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => reusable.Start(double.NaN));
            Assert.That(current.Status, Is.EqualTo(TimerState.Paused));
            current.Cancel();
            var next = reusable.StartFrames(1);
            Assert.That(current.Status, Is.EqualTo(TimerState.Invalidated));
            scheduler.Advance(0, 0, 1);
            Assert.That(next.Status, Is.EqualTo(TimerState.Completed));
        }

        [Test]
        public void CompletionCallbackCanStartNewRunAndOldFinallyCannotFinishIt()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var reusable = new ReusableTimer(scheduler);
            TimerHandle next = default;
            var calls = 0;
            var old = reusable.Start(0, () => next = reusable.Start(0, () => calls++));
            scheduler.Advance(0, 0, 1);
            Assert.That(old.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(next.Status, Is.EqualTo(TimerState.Scheduled));
            Assert.That(next.ExecutionCount, Is.Zero);
            Assert.That(calls, Is.Zero);
            scheduler.Advance(0, 0, 2);
            Assert.That(next.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void LastRepeatCallbackCanRestartAndThrowWithoutFaultingNewRun()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var reusable = new ReusableTimer(scheduler);
            TimerHandle next = default;
            var old = reusable.StartRepeatFrames(0, 1, _ =>
            {
                next = reusable.StartRepeat(2, 3, count: 2);
                throw new InvalidOperationException("old callback failed");
            }, count: 1);
            Assert.Throws<InvalidOperationException>(() => scheduler.Advance(0, 0, 1));
            Assert.That(old.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(next.Status, Is.EqualTo(TimerState.Scheduled));
            Assert.That(next.RemainingSeconds, Is.EqualTo(2));
            scheduler.Advance(0, 2, 2);
            Assert.That(next.ExecutionCount, Is.EqualTo(1));
            Assert.That(next.RemainingSeconds, Is.EqualTo(3));
        }

        [Test]
        public void NonLastCallbackMustCancelBeforeRestartAndOldContinuationIsRevoked()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var reusable = new ReusableTimer(scheduler);
            TimerHandle old = default;
            TimerHandle next = default;
            old = reusable.StartRepeat(0, 1, _ =>
            {
                Assert.Throws<InvalidOperationException>(() => reusable.Start(0));
                old.Cancel();
                next = reusable.StartFrames(1);
            });
            scheduler.Advance(0, 0, 1);
            Assert.That(old.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(next.Status, Is.EqualTo(TimerState.Scheduled));
            scheduler.Advance(0, 0, 2);
            Assert.That(next.Status, Is.EqualTo(TimerState.Completed));
        }

        [Test]
        public void CancelNotificationRestartDoesNotRegainOldIdentity()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var reusable = new ReusableTimer(scheduler);
            TimerHandle next = default;
            var old = reusable.Start(10, onCancelled: _ => next = reusable.Start(3));
            old.Cancel();
            Assert.That(old.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(old.Cancel(), Is.False);
            Assert.That(next.RemainingSeconds, Is.EqualTo(3));
        }

        [Test]
        public void DisposeClosesBeforeNotificationAndNeverRevives()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var reusable = new ReusableTimer(scheduler);
            var notified = 0;
            var handle = reusable.Start(10, onCancelled: _ =>
            {
                notified++;
                Assert.Throws<ObjectDisposedException>(() => reusable.Start(0));
            });
            reusable.Dispose();
            reusable.Dispose();
            Assert.That(handle.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(notified, Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(() => reusable.StartFrames(0));
        }

        [Test]
        public void DisposedManualSchedulerCannotBeReplacedByDefaultService()
        {
            var scheduler = new TimerScheduler(0, 0, 0);
            using var reusable = new ReusableTimer(scheduler);
            var handle = reusable.Start(1);
            scheduler.Dispose();
            Assert.That(handle.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.Throws<ObjectDisposedException>(() => reusable.Start(1));
        }

        [Test]
        public void DateTimeConversionNormalizesOffsetsAndClampsPastTargetsOnce()
        {
            var method = typeof(Timer).GetMethod("DelayUntil", BindingFlags.Static | BindingFlags.NonPublic);
            var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
            var target = now.AddSeconds(30).ToOffset(TimeSpan.FromHours(8));
            var delay = (double)method.Invoke(null, new object[] { target, now });
            Assert.That(delay, Is.EqualTo(30));
            Assert.That(method.Invoke(null, new object[] { now.AddSeconds(-1), now }), Is.EqualTo(0d));
            using var scheduler = new TimerScheduler(0, 0, 0);
            var run = scheduler.Delay(delay);
            now = now.AddHours(1);
            Assert.That(run.RemainingSeconds, Is.EqualTo(30), "接受后只由 Unscaled 样本推进。");
            scheduler.Advance(0, 30, 1);
            Assert.That(run.Status, Is.EqualTo(TimerState.Completed));
        }
    }
}
