using System;
using NUnit.Framework;
using YLFramework.Timing;

namespace YLFramework.Foundation.Tests
{
    public class TimerWaitTests
    {
        [Test]
        public void LastCallbackMustReturnBeforeWaitCanReportCompleted()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            TimerHandle handle = default;
            TimerWait wait = null;
            handle = scheduler.Delay(0, () =>
            {
                Assert.That(handle.IsActive, Is.False);
                Assert.That(wait.keepWaiting, Is.True);
                Assert.That(wait.Status, Is.EqualTo(TimerState.Executing));
                Assert.That(wait.IsCompleted, Is.False);
            });
            wait = handle.WaitForTermination();
            Assert.That(wait.keepWaiting, Is.True);
            scheduler.Advance(0, 0, 1);
            Assert.That(wait.keepWaiting, Is.False);
            Assert.That(wait.IsFinished, Is.True);
            Assert.That(wait.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(wait.IsCompleted, Is.True);
        }

        [TestCase(TimerState.Cancelled)]
        [TestCase(TimerState.Faulted)]
        [TestCase(TimerState.Invalidated)]
        public void NonSuccessfulTerminationEndsWaitWithoutReportingCompleted(TimerState terminal)
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var handle = scheduler.Delay(0, () => throw new InvalidOperationException("wait fault"));
            var wait = handle.WaitForTermination();
            if (terminal == TimerState.Cancelled)
            {
                handle.Cancel();
            }
            else if (terminal == TimerState.Invalidated)
            {
                scheduler.Dispose();
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => scheduler.Advance(0, 0, 1));
            }

            Assert.That(wait.keepWaiting, Is.False);
            Assert.That(wait.Status, Is.EqualTo(terminal));
            Assert.That(wait.IsCompleted, Is.False);
        }

        [Test]
        public void DefaultHandleTerminatesAsInvalidatedAndPausedRunKeepsWaiting()
        {
            var empty = default(TimerHandle).WaitForTermination();
            Assert.That(empty.keepWaiting, Is.False);
            Assert.That(empty.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(empty.IsCompleted, Is.False);
            using var scheduler = new TimerScheduler(0, 0, 0);
            var handle = scheduler.Delay(1);
            var wait = handle.WaitForTermination();
            handle.Pause();
            scheduler.Advance(0, 100, 1);
            Assert.That(wait.keepWaiting, Is.True);
            Assert.That(wait.Status, Is.EqualTo(TimerState.Paused));
            handle.Resume();
            scheduler.Advance(0, 101, 2);
            Assert.That(wait.IsCompleted, Is.True);
        }

        [Test]
        public void ReusedStorageInvalidatesUnobservedWaitButPreservesObservedTerminalSnapshot()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var reusable = new ReusableTimer(scheduler);
            var old = reusable.Start(0);
            var unobserved = old.WaitForTermination();
            scheduler.Advance(0, 0, 1);
            var observed = old.WaitForTermination();
            var next = reusable.Start(10);
            Assert.That(unobserved.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(unobserved.keepWaiting, Is.False);
            Assert.That(unobserved.IsCompleted, Is.False);
            Assert.That(observed.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(observed.IsCompleted, Is.True);
            Assert.That(next.Status, Is.EqualTo(TimerState.Scheduled));
        }

        [Test]
        public void CallbackRestartCannotMakeOldWaitFollowNewRun()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var reusable = new ReusableTimer(scheduler);
            TimerHandle next = default;
            var old = reusable.Start(0, () => next = reusable.Start(10));
            var wait = old.WaitForTermination();
            scheduler.Advance(0, 0, 1);
            Assert.That(wait.keepWaiting, Is.False);
            Assert.That(wait.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(wait.IsCompleted, Is.False);
            Assert.That(next.IsActive, Is.True);
        }
    }
}
