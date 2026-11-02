using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Timing;

namespace YLFramework.Foundation.Tests
{
    public class TimerOwnershipTests
    {
        [Test]
        public void OwnershipTypesBelongToFoundationAndCleanupNeverCreatesHost()
        {
            var assembly = typeof(ApplicationTool).Assembly;
            Assert.That(typeof(TimerScope).Assembly, Is.SameAs(assembly));
            Assert.That(typeof(TimerHandleGroup).Assembly, Is.SameAs(assembly));
            Assert.That(Application.isPlaying, Is.False);
            var driver = assembly.GetType("YLFramework.Timing.Internal.TimerDriver");
            Assert.That(driver, Is.Not.Null);
            var before = UnityEngine.Object.FindObjectsByType(driver, FindObjectsInactive.Include, FindObjectsSortMode.None);
            using var scope = new TimerScope();
            Assert.That(scope.Contains("missing"), Is.False);
            Assert.That(scope.TryGet("missing", out var missing), Is.False);
            Assert.That(missing.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(scope.Cancel("missing") || scope.Pause("missing") || scope.Resume("missing"), Is.False);
            scope.CancelAll();
            var group = new TimerHandleGroup();
            group.Add(default);
            group.CancelAll();
            group.Clear();
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.Delay(double.NaN));
            Assert.Throws<ArgumentException>(() => scope.PostNamed(" ", 0));
            Assert.Throws<InvalidOperationException>(() => scope.Delay(0));
            Assert.Throws<InvalidOperationException>(() => scope.Repeat(0, 1));
            Assert.Throws<InvalidOperationException>(() => scope.DelayFrames(0));
            Assert.Throws<InvalidOperationException>(() => scope.RepeatFrames(0, 1));
            CollectionAssert.AreEquivalent(before,
                UnityEngine.Object.FindObjectsByType(driver, FindObjectsInactive.Include, FindObjectsSortMode.None));
        }

        [Test]
        public void NamesAreOrdinalAndIndependentWhileAnonymousRunsRemainOwned()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var first = new TimerScope(scheduler);
            using var second = new TimerScope(scheduler);
            var lower = first.PostNamed("cooldown", 10);
            var upper = first.PostNamedFrames("Cooldown", 10);
            var other = second.PostNamed("cooldown", 10);
            var anonymous = first.Delay(10, clock: TimerClock.Scaled);
            Assert.That(first.TryGet("cooldown", out var found), Is.True);
            Assert.That(found, Is.EqualTo(lower));
            Assert.That(first.TryGet("Cooldown", out found), Is.True);
            Assert.That(found, Is.EqualTo(upper));
            Assert.Throws<InvalidOperationException>(() => first.PostNamed("cooldown", 0));
            first.CancelAll();
            Assert.That(lower.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(upper.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(anonymous.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(other.IsActive, Is.True);
            Assert.That(first.Contains("cooldown") || first.Contains("Cooldown"), Is.False);
            Assert.That(first.PostNamed("cooldown", 10).IsActive, Is.True);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t")]
        public void NamedEntryAndControlRejectBlankNames(string name)
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var scope = new TimerScope(scheduler);
            Assert.Throws<ArgumentException>(() => scope.PostNamed(name, 0));
            Assert.Throws<ArgumentException>(() => scope.ReplaceNamed(name, 0));
            Assert.Throws<ArgumentException>(() => scope.PostNamedFrames(name, 0));
            Assert.Throws<ArgumentException>(() => scope.ReplaceNamedFrames(name, 0));
            Assert.Throws<ArgumentException>(() => scope.Contains(name));
            Assert.Throws<ArgumentException>(() => scope.TryGet(name, out _));
            Assert.Throws<ArgumentException>(() => scope.Cancel(name));
            Assert.Throws<ArgumentException>(() => scope.Pause(name));
            Assert.Throws<ArgumentException>(() => scope.Resume(name));
        }

        [Test]
        public void SecondsAndFramesOfferNamedAndAnonymousOneShotAndRepeat()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var scope = new TimerScope(scheduler);
            var calls = new List<string>();
            var scaled = scope.PostNamed("scaled", 2, () => calls.Add("scaled"), TimerClock.Scaled);
            var seconds = scope.PostNamed("seconds", 1, 3, tick => calls.Add("seconds" + tick), count: 2);
            var frames = scope.PostNamedFrames("frames", 1, 2, tick => calls.Add("frames" + tick), count: 2);
            var anonymousSeconds = scope.Repeat(1, 3, count: 2);
            var anonymousFrames = scope.RepeatFrames(1, 2, count: 2);
            var single = scope.DelayFrames(2);
            scheduler.Advance(0, 1, 1);
            CollectionAssert.AreEqual(new[] { "seconds1", "frames1" }, calls);
            scheduler.Advance(2, 4, 3);
            CollectionAssert.AreEqual(new[] { "seconds1", "frames1", "seconds2", "scaled", "frames2" }, calls);
            foreach (var handle in new[] { scaled, seconds, frames, anonymousSeconds, anonymousFrames, single })
            {
                Assert.That(handle.Status, Is.EqualTo(TimerState.Completed));
            }

            Assert.That(scope.Contains("seconds") || scope.Contains("frames") || scope.Contains("scaled"), Is.False);
        }

        [Test]
        public void NamedPauseAndResumePreserveRemainingSecondsAndFrames()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var scope = new TimerScope(scheduler);
            var seconds = scope.PostNamed("seconds", 5);
            var frames = scope.PostNamedFrames("frames", 5);
            scheduler.Advance(0, 2, 2);
            Assert.That(scope.Pause("seconds"), Is.True);
            Assert.That(scope.Pause("frames"), Is.True);
            scheduler.Advance(0, 100, 100);
            Assert.That(seconds.RemainingSeconds, Is.EqualTo(3));
            Assert.That(frames.RemainingFrames, Is.EqualTo(3));
            Assert.That(scope.Contains("seconds") && scope.Contains("frames"), Is.True);
            Assert.That(scope.Resume("seconds") && scope.Resume("frames"), Is.True);
            scheduler.Advance(0, 103, 103);
            Assert.That(seconds.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(frames.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(scope.Cancel("seconds") || scope.Resume("frames"), Is.False);
        }

        [Test]
        public void ReplacementValidatesAllParametersBeforeCancellingOldResponsibility()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var scope = new TimerScope(scheduler);
            var notifications = 0;
            var old = scope.PostNamed("name", 100, onCancelled: _ => notifications++);
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.ReplaceNamed("name", double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.ReplaceNamed("name", double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.ReplaceNamed("name", -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.ReplaceNamed("name", 0, clock: TimerClock.RenderedFrame));
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.ReplaceNamed("name", 0, clock: (TimerClock)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.ReplaceNamed("name", 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.ReplaceNamed("name", 0, double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.ReplaceNamed("name", 0, 1, count: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.ReplaceNamedFrames("name", -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.ReplaceNamedFrames("name", 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.ReplaceNamedFrames("name", 0, 1, count: -1));
            Assert.That(old.IsActive, Is.True);
            Assert.That(notifications, Is.Zero);
            var current = scope.ReplaceNamedFrames("name", 10);
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(old.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(old.Cancel(), Is.False);
            Assert.That(scope.TryGet("name", out var found), Is.True);
            Assert.That(found, Is.EqualTo(current));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReplacementDeadlineOverflowDoesNotCancelOldRun(bool frames)
        {
            using var scheduler = new TimerScheduler(0, frames ? 0 : double.MaxValue, 1);
            using var scope = new TimerScope(scheduler);
            var notifications = 0;
            var old = frames ? scope.PostNamedFrames("name", 1, onCancelled: _ => notifications++) :
                scope.PostNamed("name", 0, onCancelled: _ => notifications++);
            Assert.Throws<OverflowException>(() =>
            {
                if (frames)
                {
                    scope.ReplaceNamedFrames("name", long.MaxValue);
                }
                else
                {
                    scope.ReplaceNamed("name", double.MaxValue);
                }
            });
            Assert.That(old.IsActive, Is.True);
            Assert.That(scope.TryGet("name", out var found), Is.True);
            Assert.That(found, Is.EqualTo(old));
            Assert.That(notifications, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReplacementNotificationReentrantSameNameWins(bool nestedReplacement)
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var scope = new TimerScope(scheduler);
            TimerHandle inner = default;
            var old = scope.PostNamed("name", 100, onCancelled: _ =>
            {
                Assert.That(scope.Contains("name"), Is.False);
                inner = nestedReplacement ? scope.ReplaceNamed("name", 0) : scope.PostNamed("name", 0);
            });
            var outer = scope.ReplaceNamed("name", 100);
            Assert.That(outer.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(old.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(scope.TryGet("name", out var found), Is.True);
            Assert.That(found, Is.EqualTo(inner));
            scheduler.Advance(0, 0, 1);
            Assert.That(inner.Status, Is.EqualTo(TimerState.Completed));
        }

        [Test]
        public void ReplacementVersionIsNotJustPresenceOfNameAfterReentry()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var scope = new TimerScope(scheduler);
            scope.PostNamed("name", 100, onCancelled: _ => scope.ReplaceNamed("name", 100).Cancel());
            var outer = scope.ReplaceNamed("name", 0);
            Assert.That(outer.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(scope.Contains("name"), Is.False);
        }

        [Test]
        public void UnrelatedReentrantNameDoesNotSuppressReplacement()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var scope = new TimerScope(scheduler);
            scope.PostNamed("name", 100, onCancelled: _ => scope.ReplaceNamedFrames("other", 100));
            var current = scope.ReplaceNamed("name", 0, 1, count: 2);
            Assert.That(current.IsActive, Is.True);
            Assert.That(scope.Contains("other"), Is.True);
            scheduler.Advance(0, 0, 1);
            scheduler.Advance(0, 1, 2);
            Assert.That(current.Status, Is.EqualTo(TimerState.Completed));
        }

        [Test]
        public void ReplacementNotificationFailureCannotRestoreOldNameOrDropReentrantRun()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var scope = new TimerScope(scheduler);
            var failure = new InvalidOperationException("replacement notification");
            TimerHandle inner = default;
            var old = scope.PostNamed("name", 100, onCancelled: _ =>
            {
                inner = scope.PostNamed("name", 100);
                throw failure;
            });
            Assert.That(Assert.Throws<InvalidOperationException>(() => scope.ReplaceNamed("name", 0)), Is.SameAs(failure));
            Assert.That(old.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(scope.TryGet("name", out var found), Is.True);
            Assert.That(found, Is.EqualTo(inner));
        }

        [Test]
        public void LastRoundReleasesNameBeforeCallbackAndOldFinallyCannotDetachNewRun()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var scope = new TimerScope(scheduler);
            TimerHandle old = default;
            TimerHandle current = default;
            old = scope.PostNamed("name", 0, 1, _ =>
            {
                Assert.That(old.Status, Is.EqualTo(TimerState.Executing));
                Assert.That(old.IsActive || old.IsFinished, Is.False);
                Assert.That(scope.Contains("name"), Is.False);
                Assert.That(scope.Cancel("name"), Is.False);
                scope.CancelAll();
                current = scope.PostNamed("name", 0);
            }, count: 1, onCancelled: _ => Assert.Fail("Reserved completion was cancelled"));
            scheduler.Advance(0, 0, 1);
            Assert.That(old.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(scope.TryGet("name", out var found), Is.True);
            Assert.That(found, Is.EqualTo(current));
            Assert.That(current.Status, Is.EqualTo(TimerState.Scheduled));
            scheduler.Advance(0, 0, 2);
            Assert.That(current.Status, Is.EqualTo(TimerState.Completed));
        }

        [Test]
        public void NonLastCallbackCanReplaceItselfWithoutOldRescheduling()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var scope = new TimerScope(scheduler);
            TimerHandle current = default;
            var old = scope.PostNamedFrames("loop", 1, 1, _ => current = scope.ReplaceNamedFrames("loop", 10));
            scheduler.Advance(0, 0, 1);
            Assert.That(old.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(current.IsActive, Is.True);
            scheduler.Advance(0, 0, 2);
            Assert.That(old.ExecutionCount, Is.EqualTo(1));
            Assert.That(scope.TryGet("loop", out var found), Is.True);
            Assert.That(found, Is.EqualTo(current));
        }

        [Test]
        public void CancelAllAttemptsOldSnapshotAndRetainsNewResponsibilityDespiteFailures()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var scope = new TimerScope(scheduler);
            var firstFailure = new InvalidOperationException("first");
            var secondFailure = new ArgumentException("second");
            TimerHandle current = default;
            var named = scope.PostNamed("name", 100, onCancelled: _ =>
            {
                current = scope.PostNamed("name", 100);
                throw firstFailure;
            });
            var paused = scope.DelayFrames(100, onCancelled: _ => throw secondFailure);
            paused.Pause();
            var anonymous = scope.Repeat(100, 1);
            var failure = Assert.Throws<AggregateException>(scope.CancelAll);
            CollectionAssert.AreEquivalent(new Exception[] { firstFailure, secondFailure }, failure.InnerExceptions);
            foreach (var handle in new[] { named, paused, anonymous })
            {
                Assert.That(handle.Status, Is.EqualTo(TimerState.Cancelled));
            }

            Assert.That(current.IsActive, Is.True);
            Assert.That(scope.TryGet("name", out var found), Is.True);
            Assert.That(found, Is.EqualTo(current));
            scope.CancelAll();
            Assert.That(current.Status, Is.EqualTo(TimerState.Cancelled));
        }

        [Test]
        public void DisposeClosesBeforeNotificationAndAttemptsAllResponsibility()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var scope = new TimerScope(scheduler);
            var failure = new InvalidOperationException("dispose notification");
            var first = scope.PostNamed("name", 100, onCancelled: _ =>
            {
                Assert.That(scope.IsDisposed, Is.True);
                Assert.Throws<ObjectDisposedException>(() => scope.Delay(0));
                Assert.Throws<ObjectDisposedException>(() => scope.ReplaceNamed("name", 0));
                throw failure;
            });
            var second = scope.DelayFrames(100);
            second.Pause();
            Assert.That(Assert.Throws<InvalidOperationException>(scope.Dispose), Is.SameAs(failure));
            Assert.That(first.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(second.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(scope.Contains("name"), Is.False);
            scope.Dispose();
            Assert.Throws<ObjectDisposedException>(() => scope.RepeatFrames(0, 1));
            Assert.That(scheduler.Delay(0).IsActive, Is.True);
        }

        [Test]
        public void DisposeDuringRepeatCancelsExecutingAndOtherOwnedRuns()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var scope = new TimerScope(scheduler);
            var repeat = scope.PostNamed("dispose", 0, 1, _ => scope.Dispose());
            var other = scope.Delay(0, () => Assert.Fail("Disposed scope callback ran"));
            scheduler.Advance(0, 0, 1);
            Assert.That(repeat.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(other.Status, Is.EqualTo(TimerState.Cancelled));
        }

        [TestCase(TimerState.Completed)]
        [TestCase(TimerState.Cancelled)]
        [TestCase(TimerState.Faulted)]
        [TestCase(TimerState.Invalidated)]
        public void TerminalRunReleasesScopeAndName(TimerState terminal)
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var scope = new TimerScope(scheduler);
            var handle = scope.PostNamed("name", 0, terminal == TimerState.Faulted ? (Action)(() => throw new InvalidOperationException()) : null);
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

            Assert.That(handle.Status, Is.EqualTo(terminal));
            Assert.That(scope.Contains("name"), Is.False);
            var run = typeof(TimerHandle).GetField("_run", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(handle);
            Assert.That(run.GetType().GetField("Scope", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(run), Is.Null);
            Assert.That(run.GetType().GetField("Name", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(run), Is.Null);
        }

        [Test]
        public void DisposedManualSchedulerCannotBeReplacedByDefaultContext()
        {
            Assert.Throws<ArgumentNullException>(() => new TimerScope(null));
            var scheduler = new TimerScheduler(0, 0, 0);
            using var scope = new TimerScope(scheduler);
            var handle = scope.PostNamed("name", 100);
            scheduler.Dispose();
            Assert.That(handle.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(scope.Contains("name"), Is.False);
            Assert.Throws<ObjectDisposedException>(() => scope.Delay(0));
            Assert.Throws<ObjectDisposedException>(() => scope.PostNamedFrames("name", 0));
        }

        [Test]
        public void EarlierSessionScopeRejectsPostingWithoutChangingGlobalSession()
        {
            using var scope = new TimerScope();
            typeof(TimerScope).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(scope, new object());
            Assert.Throws<InvalidOperationException>(() => scope.Delay(0));
            Assert.Throws<InvalidOperationException>(() => scope.PostNamed("name", 0));
            Assert.That(scope.Contains("name"), Is.False);
            scope.CancelAll();
        }

        [Test]
        public void GroupRemovalDoesNotCancelOrTransferOriginalScope()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            using var first = new TimerScope(scheduler);
            using var second = new TimerScope(scheduler);
            var kept = first.PostNamed("name", 100);
            var cancelled = second.PostNamed("name", 100);
            var group = new TimerHandleGroup();
            group.Add(kept);
            group.Add(cancelled);
            Assert.That(group.Remove(kept), Is.True);
            Assert.That(group.Remove(kept), Is.False);
            Assert.That(first.TryGet("name", out var found), Is.True);
            Assert.That(found, Is.EqualTo(kept));
            group.CancelAll();
            Assert.That(cancelled.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(kept.IsActive, Is.True);
            Assert.That(second.Contains("name"), Is.False);
            first.CancelAll();
            Assert.That(kept.Status, Is.EqualTo(TimerState.Cancelled));
        }

        [Test]
        public void GroupClearAttemptsAllOldHandlesAndKeepsReentrantAddForNextRound()
        {
            using var firstScheduler = new TimerScheduler(0, 0, 0);
            using var secondScheduler = new TimerScheduler(0, 0, 0);
            var group = new TimerHandleGroup();
            var firstFailure = new InvalidOperationException("first");
            var secondFailure = new ArgumentException("second");
            var notifications = 0;
            TimerHandle current = default;
            var old = firstScheduler.Delay(100, onCancelled: _ =>
            {
                notifications++;
                current = firstScheduler.Delay(100);
                group.Add(current);
                throw firstFailure;
            });
            var other = secondScheduler.DelayFrames(100, onCancelled: _ => throw secondFailure);
            group.Add(default);
            group.Add(old);
            group.Add(old);
            group.Add(other);
            var failure = Assert.Throws<AggregateException>(group.Clear);
            CollectionAssert.AreEqual(new Exception[] { firstFailure, secondFailure }, failure.InnerExceptions);
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(old.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(other.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(current.IsActive, Is.True);
            group.CancelAll();
            Assert.That(current.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(group.Remove(old) || group.Remove(current), Is.False);
            group.Clear();
        }

        [Test]
        public void GroupClearPropagatesSingleOriginalFailureAfterReleasingCollection()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var group = new TimerHandleGroup();
            var failure = new InvalidOperationException("notification");
            var broken = scheduler.Delay(100, onCancelled: _ => throw failure);
            var other = scheduler.Delay(100);
            group.Add(broken);
            group.Add(other);
            Assert.That(Assert.Throws<InvalidOperationException>(group.Clear), Is.SameAs(failure));
            Assert.That(other.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(group.Remove(broken), Is.False);
            group.Clear();
        }
    }
}
