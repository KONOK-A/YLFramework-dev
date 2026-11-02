using System;
using NUnit.Framework;
using YLFramework.Actions;

namespace YLFramework.Tests.Actions
{
    public class ActionInternalSupportTests
    {
        [SetUp]
        public void SetUp() => ActionQueue.Drain();

        [TearDown]
        public void TearDown() => ActionQueue.Drain();

        private sealed class PrivateConstruction
        {
            private PrivateConstruction()
            {
            }
            internal static PrivateConstruction Create() => new PrivateConstruction();
        }

        private sealed class Callback : IActionQueueCallback
        {
            private readonly Action _callback;
            internal Callback(Action callback) => _callback = callback;
            public void Call() => _callback();
        }

        [Test]
        public void PoolSupportsPrivateFactoriesWarmCountAndResetOnRecycleOnly()
        {
            var created = 0;
            var resets = 0;
            var pool = new ActionPool<PrivateConstruction>(() =>
            {
                created++;
                return PrivateConstruction.Create();
            }, item => resets++, 2);
            Assert.That(created, Is.EqualTo(2));
            Assert.That(resets, Is.Zero);
            var first = pool.Allocate();
            pool.Allocate();
            Assert.That(created, Is.EqualTo(2));
            pool.Allocate();
            Assert.That(created, Is.EqualTo(3));
            Assert.That(pool.Recycle(first), Is.True);
            Assert.That(resets, Is.EqualTo(1));
            Assert.That(pool.Allocate(), Is.SameAs(first));
            Assert.That(resets, Is.EqualTo(1));
        }

        [Test]
        public void PoolUsesLifoForWarmObjectsAndInterfaceFactories()
        {
            var first = new Callback(() => { });
            var second = new Callback(() => { });
            var calls = 0;
            var pool = new ActionPool<IActionQueueCallback>(() => ++calls == 1 ? first : second, initCount: 2);
            Assert.That(pool.Allocate(), Is.SameAs(second));
            Assert.That(pool.Allocate(), Is.SameAs(first));
            pool.Recycle(first);
            pool.Recycle(second);
            Assert.That(pool.Allocate(), Is.SameAs(second));
            Assert.That(pool.Allocate(), Is.SameAs(first));
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void PoolDoesNotCacheNullAndRejectsDuplicateAndForeignRecycle()
        {
            var created = 0;
            var resets = 0;
            var pool = new ActionPool<object>(() =>
            {
                created++;
                return null;
            }, item => resets++, 2);
            Assert.That(resets, Is.Zero);
            Assert.That(pool.Allocate(), Is.Null);
            Assert.That(pool.Allocate(), Is.Null);
            Assert.That(created, Is.EqualTo(4), "Raw 预热跳过 null；取得仍需调用工厂。");
            Assert.That(pool.Recycle(null), Is.False);
            Assert.That(pool.Allocate(), Is.Null);
            Assert.That(created, Is.EqualTo(5));
            Assert.That(resets, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => pool.Recycle(new object()));
            var owned = new ActionPool<object>(() => new object(), item => resets++);
            var value = owned.Allocate();
            Assert.That(owned.Recycle(value), Is.True);
            Assert.That(owned.Recycle(value), Is.False);
            Assert.That(resets, Is.EqualTo(1));
            Assert.That(owned.Allocate(), Is.SameAs(value));
            Assert.That(owned.Allocate(), Is.Not.SameAs(value));
        }

        [Test]
        public void PoolPropagatesFactoryAndResetExceptionsWithoutCachingTheFailedRecycle()
        {
            Assert.Throws<ArgumentNullException>(() => new ActionPool<object>(null));
            Func<object> failure = () => throw new InvalidOperationException("expected");
            Assert.Throws<InvalidOperationException>(() => new ActionPool<object>(failure, initCount: 1));
            var empty = new ActionPool<object>(failure, initCount: -1);
            Assert.Throws<InvalidOperationException>(() => empty.Allocate());
            var calls = 0;
            var resets = 0;
            var pool = new ActionPool<object>(() =>
            {
                calls++;
                return new object();
            },
                item =>
                {
                    resets++;
                    throw new InvalidOperationException("expected");
                });
            var value = pool.Allocate();
            Assert.Throws<InvalidOperationException>(() => pool.Recycle(value));
            Assert.That(pool.Recycle(value), Is.False);
            pool.Discard(value);
            Assert.That(resets, Is.EqualTo(1));
            Assert.That(pool.Allocate(), Is.Not.SameAs(value));
            Assert.That(calls, Is.EqualTo(2));
        }

        public sealed class FailureAction : AbstractAction<FailureAction>
        {
            internal static Exception ResetError;
            internal static Exception CleanupError;
            internal static FailureAction Last;
            internal int Cleanups;

            protected override void OnReset()
            {
                Last = this;
                Cleanups = 0;
                if (ResetError != null)
                {
                    throw ResetError;
                }
            }

            protected override void OnDeinit()
            {
                Cleanups++;
                if (CleanupError != null)
                {
                    throw CleanupError;
                }
            }
        }

        [Test]
        public void FailedDomainCleanupIsDiscardedOnlyWhenQueueDrainsAndNeverRetried()
        {
            var failed = FailureAction.Allocate();
            FailureAction replacement = null;
            try
            {
                var failure = new InvalidOperationException("domain cleanup");
                FailureAction.CleanupError = failure;
                Assert.That(Assert.Throws<InvalidOperationException>(failed.Deinit), Is.SameAs(failure));
                Assert.That(failed.IsDeinitialized, Is.True);
                Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
                Assert.That(failed.Cleanups, Is.EqualTo(1));
                FailureAction.CleanupError = null;
                replacement = FailureAction.Allocate();
                Assert.That(replacement, Is.Not.SameAs(failed));
                ActionQueue.Drain();
                failed.Deinit();
                Assert.That(failed.Cleanups, Is.EqualTo(1));
                var next = FailureAction.Allocate();
                Assert.That(next, Is.Not.SameAs(failed));
                next.Deinit();
            }
            finally
            {
                FailureAction.CleanupError = null;
                replacement?.Deinit();
                FailureAction.Last = null;
            }
        }

        [Test]
        public void FailedInitializationEndsTheBorrowAndPreservesOriginalAndCleanupErrors()
        {
            var original = new InvalidOperationException("initialization");
            var cleanup = new InvalidOperationException("initialization cleanup");
            FailureAction.ResetError = original;
            FailureAction.CleanupError = cleanup;
            FailureAction failed = null;
            FailureAction replacement = null;
            try
            {
                Assert.That(Assert.Throws<InvalidOperationException>(() => FailureAction.Allocate()), Is.SameAs(original));
                Assert.That(original.Data["ActionKit.CleanupException"], Is.SameAs(cleanup));
                failed = FailureAction.Last;
                Assert.That(failed.IsDeinitialized, Is.True);
                Assert.That(failed.Cleanups, Is.EqualTo(1));
                FailureAction.ResetError = null;
                FailureAction.CleanupError = null;
                failed.Deinit();
                ActionQueue.Drain();
                replacement = FailureAction.Allocate();
                Assert.That(replacement, Is.Not.SameAs(failed));
                Assert.That(failed.Cleanups, Is.EqualTo(1));
            }
            finally
            {
                FailureAction.ResetError = null;
                FailureAction.CleanupError = null;
                replacement?.Deinit();
                FailureAction.Last = null;
            }
        }

        [Test]
        public void QueueDefersReentrantEnqueuesAndIgnoresNestedDrain()
        {
            var calls = 0;
            ActionQueue.AddCallback(new Callback(() =>
            {
                calls++;
                ActionQueue.AddCallback(new Callback(() => calls++));
                ActionQueue.Drain();
            }));
            Assert.That(calls, Is.Zero);
            ActionQueue.Drain();
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
            ActionQueue.Drain();
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(ActionQueue.PendingCount, Is.Zero);
        }

        [Test]
        public void QueuePropagatesFailureWithoutReplayingTheConsumedCallback()
        {
            var calls = 0;
            ActionQueue.AddCallback(new Callback(() => throw new InvalidOperationException("expected")));
            ActionQueue.AddCallback(new Callback(() => calls++));
            Assert.Throws<InvalidOperationException>(() => ActionQueue.Drain());
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
            ActionQueue.Drain();
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(ActionQueue.PendingCount, Is.Zero);
        }
    }
}
