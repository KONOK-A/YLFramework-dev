using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using YLFramework.Timing;

namespace YLFramework.Foundation.Tests
{
    public class TimerSchedulerContractTests
    {
        [Test]
        public void SchedulerBelongsToFoundationWithoutRuntimeDependency()
        {
            Assert.That(typeof(ApplicationTool).Assembly.GetType("YLFramework.Timing.TimerScheduler"), Is.Not.Null);
        }

        [Test]
        public void SeparateSecondClocksIncludeExactDeadline()
        {
            using var scheduler = new TimerScheduler(10, 100, 20);
            var calls = new List<string>();
            var scaled = scheduler.Delay(2, () => calls.Add("scaled"), TimerClock.Scaled);
            var unscaled = scheduler.Delay(2, () => calls.Add("unscaled"));
            scheduler.Advance(10, 102, 21);
            CollectionAssert.AreEqual(new[] { "unscaled" }, calls);
            Assert.That(unscaled.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(scaled.RemainingSeconds, Is.EqualTo(2));
            scheduler.Advance(12, 102, 22);
            CollectionAssert.AreEqual(new[] { "unscaled", "scaled" }, calls);
        }

        [Test]
        public void ZeroDelayAndSameFrameAdvanceNeverInvokeInline()
        {
            using var scheduler = new TimerScheduler(0, 0, 10);
            var calls = 0;
            var handle = scheduler.Delay(0, () => calls++);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Scheduled));
            scheduler.Advance(0, 0, 10);
            Assert.That(calls, Is.Zero);
            scheduler.Advance(0, 0, 11);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void RepeatUsesFirstDelayThenFixedDelayWithoutCatchUp()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var ticks = new List<long>();
            var handle = scheduler.Repeat(2, 5, ticks.Add, count: 3);
            scheduler.Advance(0, 2, 1);
            CollectionAssert.AreEqual(new long[] { 1 }, ticks);
            scheduler.Advance(0, 100, 2);
            CollectionAssert.AreEqual(new long[] { 1, 2 }, ticks);
            Assert.That(handle.RemainingSeconds, Is.EqualTo(5));
            scheduler.Advance(0, 104, 3);
            Assert.That(ticks.Count, Is.EqualTo(2));
            scheduler.Advance(0, 105, 4);
            CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, ticks);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(handle.ExecutionCount, Is.EqualTo(3));
        }

        [Test]
        public void FrameDelayCountsFutureRenderedFramesAndUsesLongPrecision()
        {
            const long Start = 9007199254740992;
            using var scheduler = new TimerScheduler(0, 0, Start);
            var calls = new List<int>();
            scheduler.DelayFrames(0, () => calls.Add(0));
            scheduler.DelayFrames(1, () => calls.Add(1));
            var two = scheduler.DelayFrames(2, () => calls.Add(2));
            scheduler.Advance(100, 100, Start);
            Assert.That(calls, Is.Empty);
            scheduler.Advance(100, 100, Start + 1);
            CollectionAssert.AreEqual(new[] { 0, 1 }, calls);
            Assert.That(two.RemainingFrames, Is.EqualTo(1));
            scheduler.Advance(100, 100, Start + 2);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, calls);
        }

        [Test]
        public void FrameRepeatDoesNotRepaySkippedFrames()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var ticks = new List<long>();
            var handle = scheduler.RepeatFrames(1, 2, ticks.Add, count: 3);
            scheduler.Advance(0, 0, 1);
            scheduler.Advance(0, 0, 100);
            CollectionAssert.AreEqual(new long[] { 1, 2 }, ticks);
            Assert.That(handle.RemainingFrames, Is.EqualTo(2));
            scheduler.Advance(0, 0, 102);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Completed));
        }

        [Test]
        public void CancellationCommitsBeforeNotificationAndCannotControlNewRun()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            TimerHandle old = default;
            TimerHandle current = default;
            var notifications = 0;
            old = scheduler.Delay(1, () => Assert.Fail("Cancelled callback ran"), onCancelled: reason =>
            {
                Assert.That(old.Status, Is.EqualTo(TimerState.Cancelled));
                Assert.That(old.IsActive, Is.False);
                Assert.That(old.Cancel(), Is.False);
                Assert.That(reason, Is.EqualTo(TimerCancellationReason.Cancelled));
                notifications++;
                current = scheduler.Delay(0);
            });
            Assert.That(old.Cancel(), Is.True);
            Assert.That(old.Cancel(), Is.False);
            Assert.That(old.Pause(), Is.False);
            Assert.That(old.Resume(), Is.False);
            scheduler.Advance(1, 1, 1);
            Assert.That(current.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(old.Status, Is.EqualTo(TimerState.Cancelled));
        }

        [Test]
        public void PauseFreezesSecondsAndResumeUsesCurrentSnapshot()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var calls = 0;
            var handle = scheduler.Delay(10, () => calls++);
            scheduler.Advance(0, 3, 1);
            Assert.That(handle.Pause(), Is.True);
            Assert.That(handle.Pause(), Is.True);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Paused));
            scheduler.Advance(0, 100, 2);
            Assert.That(handle.RemainingSeconds, Is.EqualTo(7));
            Assert.That(handle.Resume(), Is.True);
            handle.Resume();
            scheduler.Advance(0, 106, 3);
            Assert.That(calls, Is.Zero);
            scheduler.Advance(0, 107, 4);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void PauseFreezesRemainingFrames()
        {
            using var scheduler = new TimerScheduler(0, 0, 10);
            var handle = scheduler.DelayFrames(4);
            scheduler.Advance(0, 0, 11);
            handle.Pause();
            scheduler.Advance(0, 0, 100);
            Assert.That(handle.RemainingFrames, Is.EqualTo(3));
            handle.Resume();
            scheduler.Advance(0, 0, 102);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Scheduled));
            scheduler.Advance(0, 0, 103);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Completed));
        }

        [Test]
        public void CallbackCanCancelOwnRepeatAndAnotherDueTask()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            TimerHandle repeat = default;
            TimerHandle other = default;
            repeat = scheduler.Repeat(0, 1, _ =>
            {
                repeat.Cancel();
                other.Cancel();
            });
            other = scheduler.Delay(0, () => Assert.Fail("Cancelled due task ran"));
            scheduler.Advance(0, 1, 1);
            scheduler.Advance(0, 100, 2);
            Assert.That(repeat.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(repeat.ExecutionCount, Is.EqualTo(1));
            Assert.That(other.Status, Is.EqualTo(TimerState.Cancelled));
        }

        [Test]
        public void RepeatCallbackPauseSavesFullNextInterval()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            TimerHandle handle = default;
            handle = scheduler.Repeat(1, 5, tick =>
            {
                if (tick != 1)
                {
                    return;
                }

                Assert.That(handle.Pause(), Is.True);
                Assert.That(handle.Pause(), Is.True);
                Assert.That(handle.Status, Is.EqualTo(TimerState.Executing));
                Assert.That(handle.IsActive, Is.True);
                Assert.That(handle.RemainingSeconds, Is.Zero);
            }, count: 2);
            scheduler.Advance(0, 20, 1);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Paused));
            Assert.That(handle.RemainingSeconds, Is.EqualTo(5));
            scheduler.Advance(0, 100, 2);
            handle.Resume();
            scheduler.Advance(0, 105, 3);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Completed));
        }

        [Test]
        public void CallbackPauseThenResumeCannotScheduleTwice()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            TimerHandle handle = default;
            handle = scheduler.Repeat(0, 1, _ =>
            {
                handle.Pause();
                handle.Resume();
                handle.Resume();
            }, count: 2);
            scheduler.Advance(0, 0, 1);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Scheduled));
            scheduler.Advance(0, 1, 2);
            Assert.That(handle.ExecutionCount, Is.EqualTo(2));
            Assert.That(handle.Status, Is.EqualTo(TimerState.Completed));
        }

        [Test]
        public void LastCallbackReservesCompletionAndNewRunSurvivesOldFinally()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            TimerHandle old = default;
            TimerHandle current = default;
            old = scheduler.Repeat(0, 1, _ =>
            {
                Assert.That(old.Status, Is.EqualTo(TimerState.Executing));
                Assert.That(old.IsActive, Is.False);
                Assert.That(old.IsFinished, Is.False);
                Assert.That(old.Cancel(), Is.False);
                Assert.That(old.Pause(), Is.False);
                current = scheduler.Delay(0);
            }, count: 1, onCancelled: _ => Assert.Fail("Last round cancellation was notified"));
            scheduler.Advance(0, 0, 1);
            Assert.That(old.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(current.Status, Is.EqualTo(TimerState.Scheduled));
            old.Cancel();
            scheduler.Advance(0, 0, 2);
            Assert.That(current.Status, Is.EqualTo(TimerState.Completed));
        }

        [Test]
        public void CallbackPostedTasksInOtherClockWaitForNextAdvance()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var calls = new List<string>();
            scheduler.Delay(0, () =>
            {
                calls.Add("parent");
                scheduler.Delay(0, () => calls.Add("scaled"), TimerClock.Scaled);
                scheduler.DelayFrames(0, () => calls.Add("frame"));
                scheduler.Advance(100, 100, 100);
            });
            scheduler.Advance(0, 0, 1);
            CollectionAssert.AreEqual(new[] { "parent" }, calls);
            scheduler.Advance(0, 0, 2);
            CollectionAssert.AreEqual(new[] { "parent", "scaled", "frame" }, calls);
        }

        [Test]
        public void CallbackResumeOfDueTaskDefersItWithoutBlockingOtherDueTasks()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            TimerHandle resumed = default;
            var calls = new List<int>();
            scheduler.Delay(0, () =>
            {
                calls.Add(1);
                resumed.Resume();
            });
            resumed = scheduler.Delay(0, () => calls.Add(2));
            resumed.Pause();
            scheduler.Delay(0, () => calls.Add(3));
            scheduler.Advance(0, 0, 1);
            CollectionAssert.AreEqual(new[] { 1, 3 }, calls);
            scheduler.Advance(0, 0, 2);
            CollectionAssert.AreEqual(new[] { 1, 3, 2 }, calls);
        }

        [Test]
        public void FailureEndsOnlyBrokenRunAndReportsAfterAllReadyDomains()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var failure = new InvalidOperationException("timer callback");
            var broken = scheduler.Repeat(0, 1, _ => throw failure);
            var calls = new List<string>();
            scheduler.Delay(0, () => calls.Add("unscaled"));
            scheduler.Delay(0, () => calls.Add("scaled"), TimerClock.Scaled);
            scheduler.DelayFrames(1, () => calls.Add("frame"));
            Assert.That(Assert.Throws<InvalidOperationException>(() => scheduler.Advance(0, 0, 1)), Is.SameAs(failure));
            CollectionAssert.AreEqual(new[] { "unscaled", "scaled", "frame" }, calls);
            Assert.That(broken.Status, Is.EqualTo(TimerState.Faulted));
            scheduler.Advance(100, 100, 2);
            Assert.That(broken.ExecutionCount, Is.EqualTo(1));
        }

        [Test]
        public void MultipleFailuresAggregateAndCancellationWinsOverLaterThrow()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var first = new InvalidOperationException("first");
            var second = new ArgumentException("second");
            TimerHandle cancelled = default;
            cancelled = scheduler.Repeat(0, 1, _ =>
            {
                cancelled.Cancel();
                throw first;
            });
            var faulted = scheduler.Delay(0, () => throw second);
            var thrown = Assert.Throws<AggregateException>(() => scheduler.Advance(0, 0, 1));
            CollectionAssert.AreEqual(new Exception[] { first, second }, thrown.InnerExceptions);
            Assert.That(cancelled.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(faulted.Status, Is.EqualTo(TimerState.Faulted));
        }

        [Test]
        public void CancellationNotificationFailureDoesNotRestoreTask()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var failure = new InvalidOperationException("cancel notification");
            var handle = scheduler.Delay(0, () => Assert.Fail(), onCancelled: _ => throw failure);
            Assert.That(Assert.Throws<InvalidOperationException>(() => handle.Cancel()), Is.SameAs(failure));
            Assert.That(handle.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(handle.Cancel(), Is.False);
            scheduler.Advance(0, 0, 1);
        }

        [Test]
        public void DisposeInvalidatesPausedAndPendingRunsDespiteNotificationFailure()
        {
            var scheduler = new TimerScheduler(0, 0, 0);
            var failure = new InvalidOperationException("dispose notification");
            var a = scheduler.Delay(10, onCancelled: _ => throw failure);
            var b = scheduler.DelayFrames(10, onCancelled: reason =>
            {
                Assert.That(reason, Is.EqualTo(TimerCancellationReason.SchedulerDisposed));
                Assert.Throws<ObjectDisposedException>(() => scheduler.Delay(0));
            });
            b.Pause();
            Assert.That(Assert.Throws<InvalidOperationException>(() => scheduler.Dispose()), Is.SameAs(failure));
            Assert.That(a.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(b.Status, Is.EqualTo(TimerState.Invalidated));
            scheduler.Dispose();
            Assert.Throws<ObjectDisposedException>(() => scheduler.Advance(0, 0, 1));
        }

        [Test]
        public void DisposeDuringCallbackCutsOffOtherTasksAndPreservesReservedCompletion()
        {
            var scheduler = new TimerScheduler(0, 0, 0);
            var last = scheduler.Delay(0, scheduler.Dispose);
            var pending = scheduler.Delay(0, () => Assert.Fail("Disposed run executed"));
            scheduler.Advance(0, 0, 1);
            Assert.That(last.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(pending.Status, Is.EqualTo(TimerState.Invalidated));
        }

        [Test]
        public void EqualDeadlinesKeepRegistrationOrderAfterIndexedRemovals()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var calls = new List<int>();
            var handles = new TimerHandle[12];
            for (var i = 0; i < handles.Length; i++)
            {
                var captured = i;
                handles[i] = scheduler.Delay(2, () => calls.Add(captured));
            }
            scheduler.Advance(0, 1, 1);
            handles[4].Cancel();
            handles[8].Pause();
            handles[0].Cancel();
            scheduler.Advance(0, 2, 2);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 5, 6, 7, 9, 10, 11 }, calls);
            handles[8].Resume();
            scheduler.Advance(0, 3, 3);
            Assert.That(calls[calls.Count - 1], Is.EqualTo(8));
        }

        [Test]
        public void InvalidInputsDoNotChangeExistingRunOrClockSnapshot()
        {
            using var scheduler = new TimerScheduler(10, 100, 20);
            var existing = scheduler.Delay(2);
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Delay(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Delay(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Repeat(0, double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Repeat(0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Repeat(0, 1, count: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.DelayFrames(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.RepeatFrames(0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Delay(0, clock: TimerClock.RenderedFrame));
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Delay(0, clock: (TimerClock)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Advance(11, double.NaN, 21));
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Advance(9, 101, 21));
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Advance(11, 101, 19));
            Assert.That(existing.RemainingSeconds, Is.EqualTo(2));
            scheduler.Advance(10, 102, 21);
            Assert.That(existing.Status, Is.EqualTo(TimerState.Completed));
        }

        [Test]
        public void NullCallbacksStillCountAndDefaultHandleHasNoControl()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var handle = scheduler.Repeat(0, 1, count: 2);
            scheduler.Advance(0, 0, 1);
            scheduler.Advance(0, 1, 2);
            Assert.That(handle.ExecutionCount, Is.EqualTo(2));
            Assert.That(handle.IsFinished, Is.True);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Completed));
            var empty = default(TimerHandle);
            Assert.That(empty.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(empty.Cancel() || empty.Pause() || empty.Resume(), Is.False);
        }

        [Test]
        public void DisposeCommitsAllInvalidationsBeforeAnyNotification()
        {
            var scheduler = new TimerScheduler(0, 0, 0);
            var handles = new TimerHandle[3];
            var notifications = 0;
            Action<TimerCancellationReason> notify = reason =>
            {
                foreach (var handle in handles)
                {
                    Assert.That(handle.Status, Is.EqualTo(TimerState.Invalidated));
                    Assert.That(handle.Cancel(), Is.False);
                }
                Assert.That(reason, Is.EqualTo(TimerCancellationReason.SchedulerDisposed));
                notifications++;
            };
            for (var i = 0; i < handles.Length; i++)
            {
                handles[i] = scheduler.Delay(0, onCancelled: notify);
            }

            scheduler.Dispose();
            Assert.That(notifications, Is.EqualTo(3));
        }

        [UnityTest]
        public IEnumerator RetainedTerminalHandleDoesNotKeepUserCallbackTargetAlive(
            [Values(TimerState.Completed, TimerState.Cancelled, TimerState.Faulted, TimerState.Invalidated)] TimerState terminal)
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var handle = ScheduleCallbackTarget(scheduler, terminal == TimerState.Faulted, out var target);
            if (terminal == TimerState.Cancelled)
            {
                handle.Cancel();
            }
            else if (terminal == TimerState.Invalidated)
            {
                scheduler.Dispose();
            }
            else if (terminal == TimerState.Faulted)
            {
                Assert.Throws<InvalidOperationException>(() => scheduler.Advance(0, 0, 1));
            }
            else
            {
                scheduler.Advance(0, 0, 1);
            }

            // 在同一主线程的独立 Editor 回调收集，避开 Runner 深层调用栈的临时引用。
            var collected = false;
            void Collect()
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                collected = true;
            }

            EditorApplication.delayCall += Collect;
            try
            {
                var deadline = EditorApplication.timeSinceStartup + 10;
                while (!collected && EditorApplication.timeSinceStartup < deadline)
                {
                    yield return null;
                }
                Assert.That(collected, Is.True, "The main-thread collection callback must complete.");
                Assert.That(target.IsAlive, Is.False);
                Assert.That(handle.Status, Is.EqualTo(terminal));
                GC.KeepAlive(handle);
                GC.KeepAlive(scheduler);
            }
            finally
            {
                EditorApplication.delayCall -= Collect;
            }
        }

        [Test]
        public void RetainedTerminalHandleAfterInvalidationHasNoOwnedReferences()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var handle = ScheduleCallbackTarget(scheduler, false, out _);
            scheduler.Dispose();
            const System.Reflection.BindingFlags Fields = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var run = typeof(TimerHandle).GetField("_run", Fields).GetValue(handle);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(run.GetType().GetField("Generation", Fields).GetValue(run),
                Is.EqualTo(typeof(TimerHandle).GetField("_generation", Fields).GetValue(handle)));
            foreach (var field in new[] { "Owner", "Callback", "TickCallback", "OnCancelled" })
            {
                Assert.That(run.GetType().GetField(field, Fields).GetValue(run), Is.Null, field);
            }
            foreach (var field in new[] { "_owned", "_pending" })
            {
                var collection = typeof(TimerScheduler).GetField(field, Fields).GetValue(scheduler);
                Assert.That(collection.GetType().GetProperty("Count").GetValue(collection), Is.EqualTo(0), field);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static TimerHandle ScheduleCallbackTarget(TimerScheduler scheduler, bool fail, out WeakReference target)
        {
            var callback = new CallbackTarget { Fail = fail };
            target = new WeakReference(callback);
            return scheduler.Repeat(0, 1, callback.Tick, count: 1, onCancelled: callback.Cancelled);
        }

        private sealed class CallbackTarget
        {
            internal bool Fail;
            internal void Tick(long tick)
            {
                if (Fail)
                {
                    throw new InvalidOperationException("callback target failure");
                }
            }
            internal void Cancelled(TimerCancellationReason reason)
            {
            }
        }
    }
}
