using System;
using System.Collections.Generic;
using NUnit.Framework;
using YLFramework.RedDots;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Module.Reddot")]
    [Category("YL.Module.Domain")]
    public class ReddotTests
    {
        [TearDown]
        public void Clean() => ReddotManager.Reset();

        [TestCase("Mail", true)]
        [TestCase("Mail/Gift", true)]
        [TestCase("Mai", false)]
        [TestCase("Mailbox", false)]
        [TestCase(null, false)]
        public void PathsMatchWholeSegments(string path, bool expected)
        {
            ReddotManager.AddPath(" /Mail//Gift/ ");
            Assert.That(ReddotManager.ShouldBeDisplayed(path), Is.EqualTo(expected));
        }

        [Test]
        public void ResetNotifiesAfterClearingAndDuplicateAddDoesNotNotify()
        {
            var displays = new List<bool>();
            Action changed = () => displays.Add(ReddotManager.ShouldBeDisplayed("Mail"));
            var token = ReddotManager.RegisterChanged(changed);
            try
            {
                ReddotManager.AddPath("Mail");
                ReddotManager.AddPath("/Mail/");
                ReddotManager.Reset();
                Assert.That(displays, Is.EqualTo(new[] { true, false }));
                token.Unregister();
                ReddotManager.AddPath("Mail");
                Assert.That(displays.Count, Is.EqualTo(2));
            }
            finally
            {
                token.Unregister();
            }
        }

        [Test]
        public void SamePathSourcesAreReleasedIndependently()
        {
            using var first = ReddotManager.RegisterPath("Mail/Gift");
            using var second = ReddotManager.RegisterPath("Mail/Gift/");
            first.Dispose();
            first.Dispose();
            Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.True);
            second.Dispose();
            Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.False);
        }

        [Test]
        public void OldRegistrationCannotRemoveNewPathAfterReset()
        {
            using var old = ReddotManager.RegisterPath("Mail");
            ReddotManager.Reset();
            using var current = ReddotManager.RegisterPath("Mail");
            old.Dispose();
            Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.True);
            current.Dispose();
        }

        [Test]
        public void CollectionRemoveDoesNotStealIndependentSource()
        {
            using var source = ReddotManager.RegisterPath("Mail");
            ReddotManager.AddPath("Mail");
            ReddotManager.RemovePath("Mail");
            ReddotManager.RemovePath(null);
            Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.True);
            source.Dispose();
            Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.False);
        }

        [Test]
        public void FailedRegistrationPublishesRollbackAndPreservesBothObserverErrors()
        {
            var visible = false;
            var display = ReddotManager.RegisterChanged(() => visible = ReddotManager.ShouldBeDisplayed("Mail"));
            var throwing = ReddotManager.RegisterChanged(() => throw new InvalidOperationException("observer-marker"));
            try
            {
                var error = Assert.Throws<AggregateException>(() => ReddotManager.RegisterPath("Mail"));
                Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.False);
                Assert.That(visible, Is.False, "回滚后的状态也必须送给已经刷新过的观察者。");
                Assert.That(error.InnerExceptions, Has.Count.EqualTo(2));
                Assert.That(error.InnerExceptions, Has.All.Message.EqualTo("observer-marker"));
            }
            finally
            {
                display.Unregister();
                throwing.Unregister();
            }
        }
    }

    [Category("YL.Module.Reddot")]
    [Category("YL.Module.Domain")]
    public sealed class ReddotValueTests
    {
        [SetUp]
        public void Prepare() => ResetSession();

        [TearDown]
        public void Clean() => ResetSession();

        [Test]
        public void LegacyPathTruthAndLeasesUnchanged()
        {
            Assert.That(ReddotManager.NormalizePath(" /Mail/ / Gift // "), Is.EqualTo("Mail/Gift/"));
            var calls = 0;
            var changed = ReddotManager.RegisterChanged(() => calls++);
            try
            {
                Assert.That(calls, Is.Zero);
                ReddotManager.AddPath(" /Mail//Gift/ ");
                ReddotManager.AddPath("Mail/Gift");
                Assert.That(calls, Is.EqualTo(1));
                using var first = ReddotManager.RegisterPath("Mail/Gift");
                using var second = ReddotManager.RegisterPath(" /Mail/Gift/ ");
                ReddotManager.RemovePath("Mail/Gift");
                first.Dispose();
                Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.True);
                Assert.That(ReddotManager.ShouldBeDisplayed("Mai"), Is.False);
                Assert.That(ReddotManager.ShouldBeDisplayed("mail"), Is.False);
                Assert.That(ReddotManager.ShouldBeDisplayed("Mail/Gift/Child"), Is.False);
                Assert.That(ReddotManager.ShouldBeDisplayed(" / / "), Is.False);
                second.Dispose();
                Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.False);
            }
            finally
            {
                changed.Unregister();
            }
        }

        [Test]
        public void CountSumAnyMaxAndBoundaries()
        {
            Assert.That(typeof(ReddotManager).GetMethod("GetValue", new[] { typeof(string) }), Is.Not.Null,
                "5B must expose cached numeric path values, not only legacy boolean truth.");
            ReddotManager.AddPath("Mail");
            using var lease = ReddotManager.RegisterPath("Mail");
            using var own = ReddotManager.RegisterSource("Mail", "own", initialValue: 3);
            using var gift = ReddotManager.RegisterSource("Mail/Gift", "gift", 7);
            using var nested = ReddotManager.RegisterSource("Mail/Gift/Nested", "nested", 2);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(14));
            ReddotManager.SetAggregation("Mail/Gift", rule: ReddotAggregation.Any);
            Assert.That(ReddotManager.GetValue("Mail/Gift"), Is.EqualTo(1));
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(6));
            ReddotManager.SetAggregation("Mail", ReddotAggregation.Max);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(5), "Max compares the entire own sum to each child.");
            own.SetValue(0);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(2));
            ReddotManager.SetAggregation("Mail/Gift", ReddotAggregation.Max);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(7));
            ReddotManager.SetAggregation("Mail", ReddotAggregation.Any);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(1));
            Assert.That(ReddotManager.GetValue("Mail/Gift"), Is.EqualTo(7), "Parent rules must not change descendants.");
            Assert.That(ReddotManager.GetValue("Mai"), Is.Zero);
            Assert.That(ReddotManager.GetValue("mail"), Is.Zero);
            Assert.That(ReddotManager.GetValue("Mail/Gift/Unknown"), Is.Zero);
            Assert.That(ReddotManager.GetValue(null), Is.Zero);
            Assert.That(ReddotManager.GetSnapshot(" / / ").Path, Is.Empty);
        }

        [Test]
        public void BatchCoalescesAndReentrantListenersAreSafe()
        {
            Assert.That(typeof(ReddotManager).GetMethod("BeginBatch", Type.EmptyTypes), Is.Not.Null,
                "5B must expose an independently disposable nested batch scope.");
            using var first = ReddotManager.RegisterSource("Mail/A", "a");
            using var second = ReddotManager.RegisterSource("Mail/B", "b");
            var parent = new List<long>();
            var child = new List<long>();
            using var parentSubscription = ReddotManager.Subscribe("Mail", snapshot => parent.Add(snapshot.Value), false);
            using var childSubscription = ReddotManager.Subscribe("Mail/A", snapshot => child.Add(snapshot.Value), false);
            var globalCalls = 0;
            var global = ReddotManager.RegisterChanged(() => globalCalls++);
            try
            {
                using (ReddotManager.BeginBatch())
                {
                    first.SetValue(1);
                    using (ReddotManager.BeginBatch())
                    {
                        first.SetValue(3);
                        second.SetValue(4);
                    }
                    Assert.That(parent, Is.Empty);
                    Assert.That(child, Is.Empty);
                    Assert.That(globalCalls, Is.Zero);
                    Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(7), "Reads see completed valid writes in a batch.");
                }
                Assert.That(parent, Is.EqualTo(new long[] { 7 }));
                Assert.That(child, Is.EqualTo(new long[] { 3 }));
                Assert.That(globalCalls, Is.EqualTo(1));
                using (ReddotManager.BeginBatch())
                {
                    first.SetValue(8);
                    first.SetValue(3);
                }
                Assert.That(parent, Is.EqualTo(new long[] { 7 }), "Returning to the original value is not a redraw.");
                Assert.That(child, Is.EqualTo(new long[] { 3 }));
            }
            finally
            {
                global.Unregister();
            }
        }

        [Test]
        public void InvalidAndDuplicateSourcesCannotChangeVisibleState()
        {
            Assert.Throws<ArgumentException>(() => ReddotManager.RegisterSource(" / / ", "id"));
            Assert.Throws<ArgumentException>(() => ReddotManager.RegisterSource("Mail", " \t "));
            Assert.Throws<ArgumentException>(() => ReddotManager.RegisterSource("Mail", null));
            Assert.Throws<ArgumentOutOfRangeException>(() => ReddotManager.RegisterSource("Mail", "bad", -1));
            Assert.Throws<ArgumentNullException>(() => ReddotManager.Subscribe("Mail", null));
            Assert.Throws<ArgumentOutOfRangeException>(() => ReddotManager.SetAggregation("Mail", (ReddotAggregation)123));
            Assert.Throws<ArgumentException>(() => ReddotManager.SetAggregation(null, ReddotAggregation.Sum));
            Assert.That(ReddotManager.ReddotPath, Is.Empty);
            using var first = ReddotManager.RegisterSource(" /Mail//Gift/ ", "id", 2);
            Assert.Throws<ArgumentException>(() => ReddotManager.RegisterSource("Mail/Gift", "id", 9));
            using var second = ReddotManager.RegisterSource("Mail/Gift", "ID", 3);
            using var spaced = ReddotManager.RegisterSource("Mail/Gift", " id ", 4);
            Assert.Throws<ArgumentOutOfRangeException>(() => first.SetValue(-1));
            Assert.That(first.Value, Is.EqualTo(2));
            Assert.That(ReddotManager.GetValue("Mail/Gift"), Is.EqualTo(9));
            Assert.That(ReddotManager.ReddotPath, Is.EqualTo(new[] { "Mail/Gift/" }));
            first.Dispose();
            first.Dispose();
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(7));
            Assert.Throws<ObjectDisposedException>(() => first.SetValue(1));
            using var replacement = ReddotManager.RegisterSource("Mail/Gift", "id", 5);
            first.Dispose();
            ReddotManager.RemovePath("Mail/Gift");
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(12));
        }

        [Test]
        public void OverflowIsRejectedBeforeSourcesRulesCachesOrNotificationsChange()
        {
            using var large = ReddotManager.RegisterSource("Mail/A", "large", long.MaxValue);
            using var small = ReddotManager.RegisterSource("Mail/B", "small");
            var values = new List<long>();
            using var subscription = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value), false);
            Assert.Throws<OverflowException>(() => small.SetValue(1));
            Assert.Throws<OverflowException>(() => ReddotManager.RegisterSource("Mail/C", "overflow", 1));
            Assert.Throws<OverflowException>(() => ReddotManager.RegisterPath("Mail/A"));
            Assert.Throws<OverflowException>(() => ReddotManager.AddPath("Mail/A"));
            Assert.That(small.Value, Is.Zero);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(long.MaxValue));
            Assert.That(ReddotManager.ReddotPath, Is.EqualTo(new[] { "Mail/A/", "Mail/B/" }));
            Assert.That(values, Is.Empty);
            ReddotManager.SetAggregation("Mail/A", ReddotAggregation.Any);
            small.SetValue(1);
            Assert.Throws<OverflowException>(() => ReddotManager.SetAggregation("Mail/A", ReddotAggregation.Sum));
            Assert.That(ReddotManager.GetValue("Mail/A"), Is.EqualTo(1));
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(2));
            Assert.Throws<OverflowException>(() => ReddotManager.RegisterSource("Mail/A", "own-overflow", 1));
            Assert.That(large.Value, Is.EqualTo(long.MaxValue), "Even Any must validate its full own sum.");
            ReddotManager.SetAggregation("Mail", ReddotAggregation.Max);
            ReddotManager.SetAggregation("Mail/A", ReddotAggregation.Sum);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(long.MaxValue));
            Assert.Throws<OverflowException>(() => ReddotManager.SetAggregation("Mail", ReddotAggregation.Sum));
            small.SetValue(2);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(long.MaxValue), "The rejected Sum rule must not stick.");
        }

        [Test]
        public void PathSubscriptionsAreImmutableLocalAndValueSensitive()
        {
            using var source = ReddotManager.RegisterSource("Mail/Gift", "id", 2);
            var snapshots = new List<ReddotSnapshot>();
            var oldCalls = 0;
            var old = ReddotManager.RegisterChanged(() => oldCalls++);
            try
            {
                var subscription = ReddotManager.Subscribe(" /Mail// ", callback: snapshots.Add, emitInitial: true);
                using var empty = ReddotManager.Subscribe(null,
                    snapshot => Assert.That(snapshot.Value, Is.Zero), emitInitial: true);
                Assert.That(oldCalls, Is.Zero, "Subscribing must not replay the legacy global channel.");
                Assert.That(snapshots.Count, Is.EqualTo(1));
                Assert.That(snapshots[0].Path, Is.EqualTo("Mail/"));
                source.SetValue(3);
                source.SetValue(3);
                ReddotManager.AddPath("Mailbox");
                ReddotManager.AddPath("Other");
                Assert.That(snapshots.Count, Is.EqualTo(2));
                Assert.That(snapshots[0].Value, Is.EqualTo(2), "Previously delivered snapshots must not mutate.");
                ReddotManager.SetAggregation("Mail", ReddotAggregation.Any);
                source.SetValue(4);
                Assert.That(snapshots.Count, Is.EqualTo(3));
                Assert.That(snapshots[2].Value, Is.EqualTo(1));
                subscription.Dispose();
                subscription.Dispose();
                source.SetValue(0);
                Assert.That(snapshots.Count, Is.EqualTo(3));
                Assert.That(typeof(ReddotSnapshot).GetProperty("Value").CanWrite, Is.False);
                Assert.That(typeof(ReddotSnapshot).GetProperty("Path").CanWrite, Is.False);
                Assert.That(typeof(ReddotSource).GetProperty("Value").CanWrite, Is.False);
            }
            finally
            {
                old.Unregister();
            }
        }

        [Test]
        public void ReentrantSourcesAndSubscriptionsUseStableWaves()
        {
            using var source = ReddotManager.RegisterSource("Mail", "id");
            var depth = 0;
            var maximumDepth = 0;
            var calls = 0;
            var removedCalls = 0;
            var addedValues = new List<long>();
            IDisposable removed = null;
            IDisposable added = null;
            using var first = ReddotManager.Subscribe("Mail", snapshot =>
            {
                depth++;
                maximumDepth = Math.Max(maximumDepth, depth);
                calls++;
                if (snapshot.Value == 1)
                {
                    removed.Dispose();
                    added = ReddotManager.Subscribe("Mail", next =>
                    {
                        Assert.That(depth, Is.Zero, "Immediate replay requested in a callback must not nest callbacks.");
                        addedValues.Add(next.Value);
                    });
                }
                if (snapshot.Value < 512)
                {
                    source.SetValue(snapshot.Value + 1);
                }
                depth--;
            }, false);
            removed = ReddotManager.Subscribe("Mail", _ => removedCalls++, false);
            try
            {
                source.SetValue(1);
                Assert.That(calls, Is.EqualTo(512));
                Assert.That(maximumDepth, Is.EqualTo(1));
                Assert.That(removedCalls, Is.Zero);
                Assert.That(addedValues[0], Is.EqualTo(2));
                Assert.That(addedValues[addedValues.Count - 1], Is.EqualTo(512));
                Assert.That(addedValues.Count, Is.EqualTo(511));
            }
            finally
            {
                removed.Dispose();
                added?.Dispose();
            }
        }

        [Test]
        public void ReentrantImmediateSubscriptionsDoNotGrowTheCallStack()
        {
            var subscriptions = new List<IDisposable>();
            var depth = 0;
            var maximumDepth = 0;
            var calls = 0;
            Action<ReddotSnapshot> callback = null;
            callback = _ =>
            {
                depth++;
                maximumDepth = Math.Max(maximumDepth, depth);
                if (++calls < 256)
                {
                    subscriptions.Add(ReddotManager.Subscribe("Mail", callback));
                }
                depth--;
            };
            subscriptions.Add(ReddotManager.Subscribe("Mail", callback));
            Assert.That(calls, Is.EqualTo(256));
            Assert.That(maximumDepth, Is.EqualTo(1));
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }

        [Test]
        public void RegistrationFailuresRollbackAndAllObserversStillRun()
        {
            var values = new List<long>();
            var first = ReddotManager.Subscribe("Mail", _ => throw new InvalidOperationException("path-marker"), false);
            using var second = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value), false);
            var error = Assert.Throws<AggregateException>(() => ReddotManager.RegisterSource("Mail", "id", 1));
            Assert.That(error.Flatten().InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(values, Is.EqualTo(new long[] { 1, 0 }));
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero);
            Assert.That(ReddotManager.ReddotPath, Is.Empty);
            first.Dispose();
            using var retry = ReddotManager.RegisterSource("Mail", "id", 2);
            Assert.That(retry.Value, Is.EqualTo(2));
        }

        [Test]
        public void ListenerErrorsAggregateWithoutRevertingDeliveredSourcesOrStallingFlush()
        {
            using var source = ReddotManager.RegisterSource("Mail", "id");
            var values = new List<long>();
            var legacy = ReddotManager.RegisterChanged(() => throw new InvalidOperationException("legacy-marker"));
            var throwing = ReddotManager.Subscribe("Mail", _ => throw new ArgumentException("path-marker"), false);
            using var healthy = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value), false);
            try
            {
                var error = Assert.Throws<AggregateException>(() => source.SetValue(2));
                Assert.That(error.Flatten().InnerExceptions, Has.Count.EqualTo(2));
                Assert.That(values, Is.EqualTo(new long[] { 2 }));
                Assert.That(source.Value, Is.EqualTo(2));
            }
            finally
            {
                legacy.Unregister();
                throwing.Dispose();
            }
            source.SetValue(3);
            Assert.That(values, Is.EqualTo(new long[] { 2, 3 }));
        }

        [Test]
        public void FailedInitialNotificationUnsubscribesWithoutLeakingAnUnobtainableToken()
        {
            var calls = 0;
            Assert.Throws<InvalidOperationException>(() => ReddotManager.Subscribe("Mail", _ =>
            {
                calls++;
                throw new InvalidOperationException("initial-marker");
            }));
            ReddotManager.AddPath("Mail");
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void BatchBodyAndObserverExceptionsStillFinishAndRecover()
        {
            using var source = ReddotManager.RegisterSource("Mail", "id");
            var values = new List<long>();
            using var healthy = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value), false);
            Assert.Throws<InvalidOperationException>(() =>
            {
                using (ReddotManager.BeginBatch())
                {
                    source.SetValue(4);
                    throw new InvalidOperationException("body-marker");
                }
            });
            Assert.That(values, Is.EqualTo(new long[] { 4 }));
            var throwing = ReddotManager.Subscribe("Mail", _ => throw new ArgumentException("batch-marker"), false);
            var batch = ReddotManager.BeginBatch();
            source.SetValue(5);
            Assert.Throws<ArgumentException>(() => batch.Dispose());
            batch.Dispose();
            throwing.Dispose();
            source.SetValue(6);
            Assert.That(values, Is.EqualTo(new long[] { 4, 5, 6 }));
        }

        [Test]
        public void ResetKeepsRulesAndSubscriptionsButInvalidatesOldSourcesAndBatches()
        {
            ReddotManager.SetAggregation("Mail", ReddotAggregation.Any);
            var values = new List<long>();
            using var subscription = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value), false);
            using var old = ReddotManager.RegisterSource("Mail", "id", 2);
            using var oldLease = ReddotManager.RegisterPath("Mail");
            var oldBatch = ReddotManager.BeginBatch();
            old.SetValue(8);
            ReddotManager.Reset();
            Assert.That(values, Is.EqualTo(new long[] { 1, 0 }));
            Assert.Throws<ObjectDisposedException>(() => old.SetValue(9));
            using var current = ReddotManager.RegisterSource("Mail", "id", 7);
            var currentBatch = ReddotManager.BeginBatch();
            old.Dispose();
            oldLease.Dispose();
            oldBatch.Dispose();
            current.SetValue(0);
            Assert.That(values, Is.EqualTo(new long[] { 1, 0, 1 }));
            currentBatch.Dispose();
            Assert.That(values, Is.EqualTo(new long[] { 1, 0, 1, 0 }));
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero);
        }

        [Test]
        public void SessionResetClearsBothSubscriptionKindsRulesAndAllOldCredentials()
        {
            ReddotManager.SetAggregation("Mail", ReddotAggregation.Any);
            using var oldSource = ReddotManager.RegisterSource("Mail", "id", 2);
            var calls = 0;
            Action<ReddotSnapshot> callback = _ => calls++;
            var oldSubscription = ReddotManager.Subscribe("Mail", callback, false);
            var legacyCalls = 0;
            Action legacyCallback = () => legacyCalls++;
            var oldLegacy = ReddotManager.RegisterChanged(legacyCallback);
            var oldBatch = ReddotManager.BeginBatch();
            oldSource.SetValue(3);
            ResetSession();
            Assert.That(ReddotManager.ReddotPath, Is.Empty);
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero);
            Assert.That(calls, Is.Zero);
            using var currentSubscription = ReddotManager.Subscribe("Mail", callback, false);
            var currentLegacy = ReddotManager.RegisterChanged(legacyCallback);
            try
            {
                using var current = ReddotManager.RegisterSource("Mail", "id", 7);
                oldSubscription.Dispose();
                oldLegacy.Unregister();
                oldBatch.Dispose();
                oldSource.Dispose();
                current.SetValue(8);
                Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(8), "A new session must return to Sum.");
                Assert.That(calls, Is.EqualTo(2));
                Assert.That(legacyCalls, Is.EqualTo(2));
            }
            finally
            {
                currentLegacy.Unregister();
            }
        }

        [Test]
        public void ResetDuringDispatchDoesNotPublishStaleValuesToLaterListeners()
        {
            var firstValues = new List<long>();
            var laterValues = new List<long>();
            using var first = ReddotManager.Subscribe("Mail", snapshot =>
            {
                firstValues.Add(snapshot.Value);
                if (snapshot.Value != 0)
                {
                    ReddotManager.Reset();
                }
            }, false);
            using var later = ReddotManager.Subscribe("Mail", snapshot => laterValues.Add(snapshot.Value), false);
            ReddotManager.AddPath("Mail");
            Assert.That(firstValues, Is.EqualTo(new long[] { 1, 0 }));
            Assert.That(laterValues, Is.Empty, "A listener still at zero must not receive an obsolete pre-reset value.");
        }

        [Test]
        public void ResetDuringZeroNotificationStillCatchesUpLaterSubscribers()
        {
            using var source = ReddotManager.RegisterSource("Mail", "id", 3);
            var laterValues = new List<long>();
            using var first = ReddotManager.Subscribe("Mail", snapshot =>
            {
                if (snapshot.Value == 0)
                {
                    ReddotManager.Reset();
                }
            }, false);
            using var later = ReddotManager.Subscribe("Mail", snapshot => laterValues.Add(snapshot.Value), false);
            source.SetValue(0);
            Assert.That(laterValues, Is.EqualTo(new long[] { 0 }), "Reset must not drop an already pending zero transition.");
        }

        [Test]
        public void ResetInsideQueuedInitialReplayDoesNotLoseOtherInitialReplays()
        {
            var subscriptions = new List<IDisposable>();
            var calls = 0;
            subscriptions.Add(ReddotManager.Subscribe("Seed", _ =>
            {
                subscriptions.Add(ReddotManager.Subscribe("Mail", _ => ReddotManager.Reset()));
                subscriptions.Add(ReddotManager.Subscribe("Other", _ => calls++));
            }));
            Assert.That(calls, Is.EqualTo(1), "Reset retains subscriptions, including initial replay not yet delivered.");
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }

        [Test]
        public void SessionResetInsideDispatchTruncatesOldListenersButDeliversTheNewSession()
        {
            var oldCalls = 0;
            var newValues = new List<long>();
            IDisposable newSubscription = null;
            ReddotSource newSource = null;
            var legacy = ReddotManager.RegisterChanged(() => oldCalls++);
            using var first = ReddotManager.Subscribe("Mail", _ =>
            {
                ResetSession();
                newSubscription = ReddotManager.Subscribe("Mail", snapshot => newValues.Add(snapshot.Value));
                newSource = ReddotManager.RegisterSource("Mail", "id", 7);
            }, false);
            using var later = ReddotManager.Subscribe("Mail", _ => oldCalls++, false);
            try
            {
                ReddotManager.AddPath("Mail");
                Assert.That(oldCalls, Is.Zero);
                Assert.That(newValues, Is.EqualTo(new long[] { 7 }));
                Assert.That(newSource.Value, Is.EqualTo(7));
            }
            finally
            {
                legacy.Unregister();
                newSubscription?.Dispose();
                newSource?.Dispose();
            }
        }

        [Test]
        public void BatchInitialReplayAndLateSubscriptionHaveIndependentBaselines()
        {
            using var source = ReddotManager.RegisterSource("Mail", "id");
            var values = new List<long>();
            var silentValues = new List<long>();
            var outer = ReddotManager.BeginBatch();
            var inner = ReddotManager.BeginBatch();
            source.SetValue(2);
            using var subscription = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value));
            using var silent = ReddotManager.Subscribe("Mail", snapshot => silentValues.Add(snapshot.Value), false);
            Assert.That(values, Is.EqualTo(new long[] { 2 }), "An explicitly requested initial replay is immediate in a batch.");
            outer.Dispose();
            source.SetValue(3);
            Assert.That(values, Is.EqualTo(new long[] { 2 }));
            Assert.That(silentValues, Is.Empty);
            inner.Dispose();
            inner.Dispose();
            Assert.That(values, Is.EqualTo(new long[] { 2, 3 }));
            Assert.That(silentValues, Is.EqualTo(new long[] { 3 }));
        }

        [Test]
        public void InternalSourceValiditySupportsSameAssemblyCompositionWithoutASecondPublicContract()
        {
            var validity = typeof(ReddotSource).GetProperty("IsCurrent",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(validity, Is.Not.Null);
            using var source = ReddotManager.RegisterSource("Mail", "id");
            Assert.That(validity.GetValue(source), Is.True);
            ReddotManager.Reset();
            Assert.That(validity.GetValue(source), Is.False);
            using var current = ReddotManager.RegisterSource("Mail", "id");
            source.Dispose();
            Assert.That(validity.GetValue(current), Is.True);
            current.Dispose();
            Assert.That(validity.GetValue(current), Is.False);
        }

        [Test]
        public void SessionResetReleasesObserverTargetsEvenWhileAnOldLeaseStillHoldsTheTree()
        {
            using var oldLease = ReddotManager.RegisterPath("Mail");
            var observer = SubscribeCollectibleObserver();
            ResetSession();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.That(observer.IsAlive, Is.False, "A stale lease must not keep all previous-session observers alive.");
            GC.KeepAlive(oldLease);
        }

        [TestCase(null, false)]
        [TestCase(null, true)]
        [TestCase("", false)]
        [TestCase("", true)]
        [TestCase(" / / \t ", false)]
        [TestCase(" / / \t ", true)]
        public void SessionResetReleasesEmptyPathCallbackTargetsWhileOldTokenIsRetained(string path, bool emitInitial)
        {
            var tokens = new List<IDisposable>();
            var observer = SubscribeCollectibleEmptyObserver(path, emitInitial, tokens);
            try
            {
                Assert.That(tokens, Has.Count.EqualTo(1));
                ResetSession();
                AssertEmptyTargetCollected(observer,
                    "An empty-path token must not retain its terminal callback target across sessions.");
                GC.KeepAlive(tokens);
            }
            finally
            {
                foreach (var token in tokens)
                {
                    token.Dispose();
                    token.Dispose();
                }
            }
        }

        [Test]
        public void EmptyPathInitialReplayRemainsQueuedAcrossReentrantReset()
        {
            var tokens = new List<IDisposable>();
            var isInsideSeed = false;
            var calls = 0;
            try
            {
                tokens.Add(ReddotManager.Subscribe("Seed", _ =>
                {
                    isInsideSeed = true;
                    tokens.Add(ReddotManager.Subscribe("Trigger", _ => ReddotManager.Reset()));
                    tokens.Add(ReddotManager.Subscribe(" / / ", snapshot =>
                    {
                        Assert.That(isInsideSeed, Is.False, "Queued initial callbacks must not execute recursively.");
                        Assert.That(snapshot.Path, Is.Empty);
                        Assert.That(snapshot.Value, Is.Zero);
                        calls++;
                    }));
                    isInsideSeed = false;
                }));
                Assert.That(calls, Is.EqualTo(1), "Reset must reschedule the undelivered empty-path initial callback.");
                ReddotManager.AddPath("Mail");
                Assert.That(calls, Is.EqualTo(1), "An empty path must not receive later path changes.");
            }
            finally
            {
                foreach (var token in tokens)
                {
                    token.Dispose();
                    token.Dispose();
                }
            }
        }

        [Test]
        public void EmptyPathThrowingQueuedReplayReleasesTargetWithoutHidingObserverError()
        {
            WeakReference observer = null;
            var tokens = new List<IDisposable>();
            try
            {
                var error = Assert.Throws<InvalidOperationException>(() => ReddotManager.Subscribe("Seed", _ =>
                {
                    observer = SubscribeCollectibleEmptyObserver(null, true, tokens, throwOnInitial: true);
                }));
                Assert.That(error.Message, Is.EqualTo("empty-initial-marker"));
                Assert.That(observer, Is.Not.Null);
                Assert.That(tokens, Has.Count.EqualTo(1));
                AssertEmptyTargetCollected(observer,
                    "A throwing queued one-shot must release its callback even while its returned token is retained.");
                GC.KeepAlive(tokens);
                ReddotManager.AddPath("Mail");
                Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(1), "Observer errors must not stall later flushes.");
            }
            finally
            {
                foreach (var token in tokens)
                {
                    token.Dispose();
                    token.Dispose();
                }
            }
        }

        [Test]
        public void SessionResetCancelsQueuedEmptyInitialReplayWithoutRetainingItsTarget()
        {
            WeakReference observer = null;
            var tokens = new List<IDisposable>();
            try
            {
                using var seed = ReddotManager.Subscribe("Seed", _ =>
                {
                    tokens.Add(ReddotManager.Subscribe("Trigger", _ => ResetSession()));
                    observer = SubscribeCollectibleEmptyObserver(null, true, tokens, throwOnInitial: true);
                });
                Assert.That(observer, Is.Not.Null);
                Assert.That(tokens, Has.Count.EqualTo(2));
                AssertEmptyTargetCollected(observer,
                    "Cancelling an in-flight empty-path initial replay must release its callback target.");
                GC.KeepAlive(tokens);
            }
            finally
            {
                foreach (var token in tokens)
                {
                    token.Dispose();
                    token.Dispose();
                }
            }
        }

        [Test]
        public void InvalidWriteInsideBatchDoesNotUndoOtherCompletedValidWrites()
        {
            using var large = ReddotManager.RegisterSource("Mail/A", "a", long.MaxValue - 1);
            using var small = ReddotManager.RegisterSource("Mail/B", "b");
            var values = new List<long>();
            using var subscription = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value), false);
            using (ReddotManager.BeginBatch())
            {
                small.SetValue(1);
                Assert.Throws<OverflowException>(() => large.SetValue(long.MaxValue));
                Assert.That(large.Value, Is.EqualTo(long.MaxValue - 1));
                Assert.That(small.Value, Is.EqualTo(1));
                Assert.That(values, Is.Empty);
            }
            Assert.That(values, Is.EqualTo(new[] { long.MaxValue }));
        }

        [Test]
        public void LegacyListAddsAndZeroValuedSourceKeepTheirDistinctOwnership()
        {
            var calls = 0;
            var global = ReddotManager.RegisterChanged(() => calls++);
            try
            {
                using var zero = ReddotManager.RegisterSource("Mail/Gift", "zero");
                Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.False);
                ReddotManager.AddPath(new[] { "Mail/Gift", " /Mail/Gift/ ", " / / ", "Mail/Other" });
                Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(2));
                Assert.That(calls, Is.EqualTo(2), "One zero-source registration and one successful list Add.");
                ReddotManager.AddPath((IList<string>)null);
                ReddotManager.AddPath(new[] { "Mail/Other", " / / " });
                Assert.That(calls, Is.EqualTo(2));
                ReddotManager.RemovePath("Mail/Gift");
                Assert.That(ReddotManager.ReddotPath, Is.EqualTo(new[] { "Mail/Gift/", "Mail/Other/" }));
                zero.SetValue(5);
                Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(6));
            }
            finally
            {
                global.Unregister();
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference SubscribeCollectibleObserver()
        {
            var target = new object();
            ReddotManager.Subscribe("Mail", _ => GC.KeepAlive(target), false);
            return new WeakReference(target);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference SubscribeCollectibleEmptyObserver(string path, bool emitInitial,
            List<IDisposable> tokens, bool throwOnInitial = false)
        {
            var target = new EmptyPathObserver(throwOnInitial);
            tokens.Add(ReddotManager.Subscribe(path, target.OnSnapshot, emitInitial));
            return new WeakReference(target);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void AssertEmptyTargetCollected(WeakReference observer, string message)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.That(observer.IsAlive, Is.False, message);
        }

        private sealed class EmptyPathObserver
        {
            private readonly bool _throwOnInitial;

            internal EmptyPathObserver(bool throwOnInitial) => _throwOnInitial = throwOnInitial;

            internal void OnSnapshot(ReddotSnapshot snapshot)
            {
                if (_throwOnInitial)
                {
                    throw new InvalidOperationException("empty-initial-marker");
                }
            }
        }

        private static void ResetSession() => typeof(ReddotManager)
            .GetMethod("ResetSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Invoke(null, null);

        [Test]
        public void LegacyReentrantNotificationsUseIterativeWaves()
        {
            var depth = 0;
            var maximumDepth = 0;
            var calls = 0;
            var changed = ReddotManager.RegisterChanged(() =>
            {
                depth++;
                maximumDepth = Math.Max(maximumDepth, depth);
                if (++calls < 8)
                {
                    ReddotManager.AddPath("Mail/Reentrant" + calls);
                }
                depth--;
            });
            try
            {
                ReddotManager.AddPath("Mail/Start");
                Assert.That(calls, Is.EqualTo(8));
                Assert.That(maximumDepth, Is.EqualTo(1), "Reentrant updates must run as later notification waves.");
            }
            finally
            {
                changed.Unregister();
            }
        }
    }
}
