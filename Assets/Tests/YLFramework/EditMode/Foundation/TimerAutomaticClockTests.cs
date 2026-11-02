using System;
using System.Reflection;
using NUnit.Framework;
using YLFramework.Timing;

namespace YLFramework.Foundation.Tests
{
    public class TimerAutomaticClockTests
    {
        private static TimerScheduler Create() => (TimerScheduler)Activator.CreateInstance(typeof(TimerScheduler),
            BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { (Action)(() => { }) }, null);
        private static void Sample(TimerScheduler scheduler, double scaled, double unscaled, long frame, bool seconds = true) =>
            typeof(TimerScheduler).GetMethod("Sample", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(scheduler, new object[] { scaled, unscaled, frame, seconds });
        private static void Tick(TimerScheduler scheduler) => typeof(TimerScheduler)
            .GetMethod("AdvanceAutomatic", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(scheduler, null);

        [Test]
        public void ColdRequestsAnchorOnFirstUpdateAndPauseFreezesFullDelay()
        {
            using var scheduler = Create();
            var frames = scheduler.DelayFrames(1);
            var zero = scheduler.Delay(0);
            var paused = scheduler.Delay(5);
            paused.Pause();
            Assert.That(paused.RemainingSeconds, Is.EqualTo(5));
            Sample(scheduler, 10, 100, 1);
            Tick(scheduler);
            Assert.That(frames.Status, Is.EqualTo(TimerState.Scheduled));
            Assert.That(zero.Status, Is.EqualTo(TimerState.Scheduled));
            paused.Resume();
            Sample(scheduler, 11, 101, 2);
            Tick(scheduler);
            Assert.That(frames.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(zero.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(paused.RemainingSeconds, Is.EqualTo(4));
        }

        [Test]
        public void RegistrationSampleBeforeDriverUpdateStillExcludesThatRenderedFrame()
        {
            using var scheduler = Create();
            Sample(scheduler, 0, 0, 1);
            Tick(scheduler);
            Sample(scheduler, 1, 10, 2);
            var run = scheduler.Delay(0);
            Tick(scheduler);
            Assert.That(run.Status, Is.EqualTo(TimerState.Scheduled));
            Sample(scheduler, 2, 11, 3);
            Tick(scheduler);
            Assert.That(run.Status, Is.EqualTo(TimerState.Completed));
        }

        [Test]
        public void FixedSecondAnchorDoesNotPostponeAlreadyKnownFrameIdentity()
        {
            using var scheduler = Create();
            Sample(scheduler, 1, 1, 1);
            Tick(scheduler);
            Sample(scheduler, 1, 1, 2, false);
            var seconds = scheduler.Delay(3);
            var frames = scheduler.DelayFrames(1);
            Assert.That(seconds.RemainingSeconds, Is.EqualTo(3));
            Sample(scheduler, 2, 20, 2);
            Tick(scheduler);
            Assert.That(seconds.RemainingSeconds, Is.EqualTo(3));
            Sample(scheduler, 3, 21, 3);
            Tick(scheduler);
            Assert.That(frames.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(seconds.RemainingSeconds, Is.EqualTo(2));
            Assert.Throws<InvalidOperationException>(() => scheduler.Advance(3, 21, 3));
        }
    }
}
