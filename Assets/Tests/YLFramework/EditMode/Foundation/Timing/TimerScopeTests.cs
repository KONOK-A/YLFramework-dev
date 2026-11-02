using NUnit.Framework;
using YLFramework.Timing;

namespace YLFramework.Tests
{
    public class TimerScopeTests
    {
        [Test]
        public void CallbackCanUndoNextPauseAndReplaceItsLoopWithoutOldRescheduling()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var scope = new TimerScope(scheduler);
            TimerHandle loop = default;
            TimerHandle replacement = default;
            var calls = 0;
            loop = scope.PostNamed("loop", 1, 2, _ =>
            {
                calls++;
                Assert.That(scope.Pause("loop"), Is.True);
                Assert.That(loop.Status, Is.EqualTo(TimerState.Executing));
                Assert.That(scope.Resume("loop"), Is.True);
                replacement = scope.ReplaceNamed("loop", 100);
            });
            scheduler.Advance(0, 1, 1);
            Assert.That(loop.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(scope.TryGet("loop", out var found), Is.True);
            Assert.That(found, Is.EqualTo(replacement));
            Assert.That(replacement.RemainingSeconds, Is.EqualTo(100));
            Assert.That(loop.Cancel(), Is.False);
            scheduler.Advance(0, 3, 2);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(loop.ExecutionCount, Is.EqualTo(1));
            Assert.That(replacement.IsActive, Is.True);
            Assert.That(scope.Cancel("loop"), Is.True);
            Assert.That(replacement.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(scope.Cancel("loop"), Is.False);
        }
    }
}
