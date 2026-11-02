using System.Collections.Generic;
using NUnit.Framework;
using YLFramework.Timing;

namespace YLFramework.Tests
{
    public class TimingContractTests
    {
        [Test]
        public void DeadlinesStayOrderedAfterIndexedCancellationAndResume()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var calls = new List<int>();
            var handles = new TimerHandle[16];
            for (var i = 0; i < handles.Length; i++)
            {
                var delay = 16 - i;
                handles[i] = scheduler.Delay(delay, () => calls.Add(delay));
            }
            scheduler.Advance(0, 0, 1);
            Assert.That(handles[15].Cancel(), Is.True);
            Assert.That(handles[5].Cancel(), Is.True);
            Assert.That(handles[10].Pause(), Is.True);
            scheduler.Advance(0, 4, 2);
            CollectionAssert.AreEqual(new[] { 2, 3, 4 }, calls);
            Assert.That(handles[10].RemainingSeconds, Is.EqualTo(6));
            Assert.That(handles[10].Resume(), Is.True);
            scheduler.Advance(0, 16, 3);
            // 原 delay=6 的任务恢复后在 10 到期，同到期的原 delay=10 先登记。
            CollectionAssert.AreEqual(new[] { 2, 3, 4, 5, 7, 8, 9, 10, 6, 12, 13, 14, 15, 16 }, calls);
            for (var i = 0; i < handles.Length; i++)
            {
                Assert.That(handles[i].Status, Is.EqualTo(i == 15 || i == 5 ? TimerState.Cancelled : TimerState.Completed));
            }
        }

        [Test]
        public void InfiniteRepeatCountsTicksUntilCancellation()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var ticks = new List<long>();
            var repeated = scheduler.Repeat(0, 1, ticks.Add, count: null);
            scheduler.Advance(0, 0, 1);
            scheduler.Advance(0, 1, 2);
            CollectionAssert.AreEqual(new long[] { 1, 2 }, ticks);
            Assert.That(repeated.Status, Is.EqualTo(TimerState.Scheduled));
            Assert.That(repeated.ExecutionCount, Is.EqualTo(2));
            Assert.That(repeated.Cancel(), Is.True);
            scheduler.Advance(0, 100, 3);
            CollectionAssert.AreEqual(new long[] { 1, 2 }, ticks);
            Assert.That(repeated.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(repeated.ExecutionCount, Is.EqualTo(2));
        }

        [Test]
        public void ClearedGroupAndOldOrdinaryHandleCannotCancelNextRun()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var group = new TimerHandleGroup();
            var previous = scheduler.Delay(100);
            group.Add(previous);
            group.Clear();
            Assert.That(previous.Status, Is.EqualTo(TimerState.Cancelled));
            var current = scheduler.Delay(1);
            Assert.That(current, Is.Not.EqualTo(previous));
            Assert.That(previous.Cancel() || previous.Pause() || previous.Resume(), Is.False);
            group.Clear();
            Assert.That(current.IsActive, Is.True);
            scheduler.Advance(0, 1, 1);
            Assert.That(current.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(previous.Status, Is.EqualTo(TimerState.Cancelled));
        }
    }
}
