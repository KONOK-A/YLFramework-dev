using System;
using NUnit.Framework;
using YLFramework.Timing;

namespace YLFramework.Tests
{
    public class TimingSchedulerTests
    {
        [Test]
        public void DisposeFromRepeatCallbackInvalidatesExecutingPausedAndPendingRuns()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var calls = 0;
            var repeated = scheduler.Repeat(0, 1, _ =>
            {
                calls++;
                scheduler.Advance(100, 100, 100);
                scheduler.Dispose();
            });
            var pending = scheduler.Delay(0, () => Assert.Fail("Disposed callback ran"), TimerClock.Scaled);
            var paused = scheduler.DelayFrames(100);
            paused.Pause();
            scheduler.Advance(0, 0, 1);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(repeated.ExecutionCount, Is.EqualTo(1));
            foreach (var handle in new[] { repeated, pending, paused })
            {
                Assert.That(handle.Status, Is.EqualTo(TimerState.Invalidated));
                Assert.That(handle.IsFinished, Is.True);
                Assert.That(handle.RemainingSeconds, Is.Zero);
                Assert.That(handle.Cancel() || handle.Pause() || handle.Resume(), Is.False);
            }
            Assert.Throws<ObjectDisposedException>(() => scheduler.Delay(0));
        }

        [Test]
        public void CallbackFailurePreservesFutureTaskAndLaterAdvanceStillWorks()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var failure = new InvalidOperationException("timing callback");
            var broken = scheduler.Repeat(0, 1, _ => throw failure);
            var calls = 0;
            var pending = scheduler.Delay(2, () => calls++);
            Assert.That(Assert.Throws<InvalidOperationException>(() => scheduler.Advance(0, 1, 1)), Is.SameAs(failure));
            Assert.That(broken.Status, Is.EqualTo(TimerState.Faulted));
            Assert.That(broken.Cancel(), Is.False);
            Assert.That(pending.Status, Is.EqualTo(TimerState.Scheduled));
            scheduler.Advance(0, 2, 2);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(pending.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(broken.ExecutionCount, Is.EqualTo(1));
            Assert.That(broken.Status, Is.EqualTo(TimerState.Faulted));
        }
    }
}
