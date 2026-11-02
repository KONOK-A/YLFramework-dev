using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YLFramework.Pooling;
using YLFramework.Singletons;

namespace YLFramework.Tests
{
    public class PoolContractTests
    {
        public sealed class SessionItem : IOfPool
        {
            public bool IsRecycled
            {
                get; set;
            }
            public int Recycles;
            public void OnAllocate()
            {
            }
            public void OnRecycle() => Recycles++;
        }

        private sealed class PrivateItem
        {
            internal int Value = 7;
            private PrivateItem()
            {
            }
        }

        private sealed class ThrowingPrivateItem
        {
            internal static readonly Exception Error = new InvalidOperationException("private constructor");
            private ThrowingPrivateItem() => throw Error;
        }

        [Test]
        public void LegacyCacheTypesExitAndSafeDirectlyAdaptsTheFoundationKernel()
        {
            var runtime = typeof(SafeObjectPool<>).Assembly;
            foreach (var name in new[] { "ClassObjectPool", "IndependentObjectPool`1", "Pool`1", "SimpleObjectPool`1" })
            {
                Assert.That(runtime.GetType("YLFramework.Pooling." + name), Is.Null, name);
            }

            Assert.That(typeof(SafeObjectPool<SessionItem>).BaseType, Is.EqualTo(typeof(object)));
            Assert.That(typeof(SafeObjectPool<SessionItem>).GetField("_pool", BindingFlags.Instance | BindingFlags.NonPublic)
                .FieldType, Is.EqualTo(typeof(ObjectPool<SessionItem>)));
            Assert.That(typeof(IPool<SessionItem>).IsAssignableFrom(typeof(SafeObjectPool<SessionItem>)), Is.True);
        }

        [Test]
        public void ExplicitFactoriesPreservePrivateConstructionAndDefaultsWithoutReflectiveReset()
        {
            var factory = new NonPublicObjectFactory<PrivateItem>();
            using var pool = new ObjectPool<PrivateItem>(factory.Create, reset: item => item.Value = 7);
            pool.SetObjectFactory(factory);
            var item = pool.Allocate();
            Assert.That(item.Value, Is.EqualTo(7));
            item.Value = 99;
            pool.Recycle(item);
            Assert.That(pool.Allocate(), Is.SameAs(item));
            Assert.That(item.Value, Is.EqualTo(7));
            pool.Discard(item);
            Assert.That(MultiObjectFactory.CreateNonPublicCtorObject(typeof(PrivateItem)), Is.TypeOf<PrivateItem>());
            Assert.That(Assert.Throws<InvalidOperationException>(() => new NonPublicObjectFactory<ThrowingPrivateItem>().Create()),
                Is.SameAs(ThrowingPrivateItem.Error));
            Assert.That(Assert.Throws<InvalidOperationException>(() => MultiObjectFactory.CreateNonPublicCtorObject<ThrowingPrivateItem>()),
                Is.SameAs(ThrowingPrivateItem.Error));
        }

        [Test]
        public void SafeSessionClosesOriginalPoolEvenWhenSingletonPropertyRegistersFirst()
        {
            Assert.That(SingletonProperty<SafeObjectPool<SessionItem>>.HasInstance, Is.False);
            var original = SafeObjectPool<SessionItem>.Instance;
            var item = original.Allocate();
            try
            {
                var stateType = typeof(ObjectPool<>).Assembly.GetType("YLFramework.Singletons.SingletonSessionState");
                var resets = (List<Action>)stateType.GetField("_sessionResets", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                // 按真实注册顺序只执行本测试类型的两项会话回调，不重置其他模块。
                foreach (var reset in new List<Action>(resets))
                {
                    var type = reset.Method.DeclaringType;
                    if (type == typeof(SingletonProperty<SafeObjectPool<SessionItem>>) || type == typeof(SafeObjectPool<SessionItem>))
                    {
                        reset();
                    }
                }
                Assert.Throws<ObjectDisposedException>(() => original.Allocate());
                Assert.That(original.Recycle(item), Is.False);
                Assert.That(item.Recycles, Is.EqualTo(1));
                Assert.That(SafeObjectPool<SessionItem>.Instance, Is.Not.SameAs(original));
            }
            finally
            {
                original.Dispose();
                SafeObjectPool<SessionItem>.Instance.Dispose();
            }
        }

        private enum FsmStateId
        {
            Active
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FsmRetainsCallbackReturnGateAndFailedExitIsIsolatedRatherThanRetried(bool failExit)
        {
            var fsm = YLFramework.FSM.Fsm<FsmStateId>.Allocate();
            var pool = SafeObjectPool<YLFramework.FSM.Fsm<FsmStateId>>.Instance;
            var exits = 0;
            var state = new YLFramework.FSM.CustomState()
                .OnUpdate(() => Assert.Throws<InvalidOperationException>(fsm.RecycleToCache))
                .OnExit(() =>
                {
                    exits++;
                    if (failExit)
                    {
                        throw new InvalidOperationException("exit");
                    }
                });
            try
            {
                fsm.AddState(FsmStateId.Active, state);
                Assert.That(fsm.SetUp(FsmStateId.Active), Is.True);
                fsm.Update();
                Assert.That(fsm.IsRecycled, Is.False);
                if (failExit)
                {
                    Assert.Throws<InvalidOperationException>(fsm.RecycleToCache);
                    Assert.That(fsm.StateCount, Is.EqualTo(1), "隔离不等于已完成领域 Exit/DeInit。");
                }
                else
                {
                    fsm.RecycleToCache();
                    Assert.That(fsm.CurrentState, Is.Null);
                    Assert.That(fsm.StateCount, Is.Zero);
                }
                Assert.That(fsm.IsRecycled, Is.True);
                fsm.RecycleToCache();
                Assert.That(exits, Is.EqualTo(1));
                var next = YLFramework.FSM.Fsm<FsmStateId>.Allocate();
                Assert.That(ReferenceEquals(next, fsm), Is.EqualTo(!failExit));
                next.RecycleToCache();
            }
            finally
            {
                state.DeInit();
                pool.Dispose();
            }
        }

        public sealed class SafeItem : IOfPool
        {
            public bool IsRecycled
            {
                get; set;
            }
            internal Action Recycled;
            internal Action Allocated;
            internal int Hooks;
            public void OnRecycle()
            {
                Hooks++;
                Recycled?.Invoke();
            }
            public void OnAllocate() => Allocated?.Invoke();
        }

        private SafeObjectPool<SafeItem> _safe;

        [SetUp]
        public void SetUp()
        {
            _safe = SafeObjectPool<SafeItem>.Instance;
            _safe.Clear();
            _safe.MaxPoolSize = 20;
            _safe.SetFactoryMethod(() => new SafeItem());
        }

        [TearDown]
        public void TearDown() => _safe.Dispose();

        [Test]
        public void ExplicitUnboundedFifoResetsDuringPrewarmOnlyOnce()
        {
            var resets = 0;
            using var pool = new ObjectPool<object>(() => new object(), reset: item => resets++, maxPoolSize: 0);
            pool.Prewarm(25);
            Assert.That(pool.CurStoreCount, Is.EqualTo(25));
            Assert.That(resets, Is.EqualTo(25));
            pool.Clear();
            var first = pool.Allocate();
            var second = pool.Allocate();
            pool.Recycle(first);
            pool.Recycle(second);
            Assert.That(pool.Allocate(), Is.SameAs(first));
            Assert.That(pool.Allocate(), Is.SameAs(second));
            Assert.That(resets, Is.EqualTo(27));
            Assert.That(pool.Recycle(null), Is.False);
            Assert.That(resets, Is.EqualTo(27));
        }

        [Test]
        public void ObjectPoolRejectsDuplicatesAndIsolatesFailedReset()
        {
            var fail = false;
            using var pool = new ObjectPool<object>(() => new object(), reset: item =>
            {
                if (fail)
                {
                    throw new InvalidOperationException();
                }
            });
            var value = pool.Allocate();
            Assert.That(pool.Recycle(value), Is.True);
            Assert.That(pool.Recycle(value), Is.False);
            Assert.That(pool.Allocate(), Is.SameAs(value));
            fail = true;
            Assert.Throws<InvalidOperationException>(() => pool.Recycle(value));
            Assert.That(pool.Recycle(value), Is.False);
            Assert.That(pool.CurStoreCount, Is.Zero);
            Assert.That(pool.Allocate(), Is.Not.SameAs(value));
            Assert.Throws<InvalidOperationException>(() => pool.Recycle(new object()));
        }

        [Test]
        public void ObjectPoolSupportsBoundedLifoAndResetsDiscardedItems()
        {
            var resets = 0;
            using var pool = new ObjectPool<object>(() => new object(), reset: _ => resets++,
                maxPoolSize: 2, cacheOrder: PoolCacheOrder.Lifo);
            var first = pool.Allocate();
            var second = pool.Allocate();
            var discarded = pool.Allocate();
            Assert.That(pool.Recycle(first), Is.True);
            Assert.That(pool.Recycle(second), Is.True);
            Assert.That(pool.Recycle(discarded), Is.False);
            Assert.That(resets, Is.EqualTo(3));
            Assert.That(pool.CurStoreCount, Is.EqualTo(2));
            Assert.That(pool.Allocate(), Is.SameAs(second));
            Assert.That(pool.Allocate(), Is.SameAs(first));
            Assert.That(pool.Allocate(), Is.Not.SameAs(discarded));
        }

        [TestCase(PoolCacheOrder.Fifo)]
        [TestCase(PoolCacheOrder.Lifo)]
        public void AllocationAndShrinkUseTheSelectedOrderWithoutCleaning(PoolCacheOrder order)
        {
            var hooks = 0;
            using var pool = new ObjectPool<object>(() => new object(), reset: item => hooks++, cacheOrder: order);
            var first = pool.Allocate();
            var second = pool.Allocate();
            pool.Recycle(first);
            pool.Recycle(second);
            pool.MaxPoolSize = 1;
            Assert.That(pool.Allocate(), Is.SameAs(order == PoolCacheOrder.Fifo ? second : first));
            Assert.That(hooks, Is.EqualTo(2));
        }

        [Test]
        public void RawPrewarmSkipsNullWithoutResetAndRespectsTargetAndCapacity()
        {
            var calls = 0;
            using var pool = new ObjectPool<object>(() =>
            {
                calls++;
                return null;
            },
                reset: item => throw new InvalidOperationException(), maxPoolSize: 2, cacheOrder: PoolCacheOrder.Lifo);
            Assert.That(pool.Prewarm(-1, PoolPrewarmMode.Raw), Is.Zero);
            Assert.That(pool.Prewarm(int.MinValue, PoolPrewarmMode.Raw), Is.Zero);
            Assert.That(pool.Prewarm(5, PoolPrewarmMode.Raw), Is.Zero);
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(pool.Allocate(), Is.Null);
            Assert.That(calls, Is.EqualTo(3));
            pool.SetFactoryMethod(() => new object());
            Assert.That(pool.Prewarm(5, PoolPrewarmMode.Raw), Is.EqualTo(2));
            Assert.That(pool.Prewarm(2, PoolPrewarmMode.Raw), Is.Zero);
        }

        [Test]
        public void PrewarmUsesFiniteNullFactoryCallsAndFailuresPropagate()
        {
            var calls = 0;
            using var pool = new ObjectPool<object>(() =>
            {
                calls++;
                return null;
            });
            Assert.That(pool.Prewarm(3), Is.Zero);
            Assert.That(calls, Is.EqualTo(3));
            pool.SetFactoryMethod(() => throw new InvalidOperationException());
            Assert.Throws<InvalidOperationException>(() => pool.Allocate());
            Assert.Throws<InvalidOperationException>(() => pool.Prewarm(1));
            pool.SetObjectFactory(null);
            Assert.That(pool.Prewarm(0), Is.Zero);
            Assert.Throws<InvalidOperationException>(() => pool.Allocate());
        }

        [TestCase(PoolCacheOrder.Fifo)]
        [TestCase(PoolCacheOrder.Lifo)]
        public void ClearSnapshotsBeforeCallbacksAndLeavesReentrantCacheIntact(PoolCacheOrder order)
        {
            using var pool = new ObjectPool<object>(() => new object(), cacheOrder: order);
            var first = pool.Allocate();
            var second = pool.Allocate();
            var replacement = pool.Allocate();
            pool.Recycle(first);
            pool.Recycle(second);
            var callbacks = 0;
            pool.Clear(item =>
            {
                if (callbacks++ == 0)
                {
                    Assert.That(pool.CurStoreCount, Is.Zero);
                    pool.Recycle(replacement);
                }
            });
            Assert.That(callbacks, Is.EqualTo(2));
            Assert.That(pool.Allocate(), Is.SameAs(replacement));
        }

        [Test]
        public void CapacityDecisionPrecedesCleanupEvenWhenCleanupReenters()
        {
            Action<object> prepare = null;
            using var pool = new ObjectPool<object>(() => new object(), reset: item => prepare?.Invoke(item), maxPoolSize: 1);
            var first = pool.Allocate();
            var second = pool.Allocate();
            pool.Recycle(first);
            prepare = item =>
            {
                pool.Allocate();
            };
            Assert.That(pool.Recycle(second), Is.False);
            Assert.That(pool.CurStoreCount, Is.Zero);
        }

        [Test]
        public void SafeMarksBeforeHooksRejectsReentrancyAndCleansFullCacheOnlyOnce()
        {
            _safe.MaxPoolSize = 1;
            var cached = _safe.Allocate();
            var discarded = _safe.Allocate();
            cached.Recycled = () =>
            {
                Assert.That(cached.IsRecycled, Is.True);
                Assert.That(_safe.Recycle(cached), Is.False);
            };
            Assert.That(_safe.Recycle(cached), Is.True);
            Assert.That(_safe.Recycle(discarded), Is.False);
            Assert.That(discarded.IsRecycled, Is.True);
            Assert.That(_safe.Recycle(discarded), Is.False);
            Assert.That(discarded.Hooks, Is.EqualTo(1));
            cached.Allocated = () => Assert.That(cached.IsRecycled, Is.False);
            Assert.That(_safe.Allocate(), Is.SameAs(cached));
            Assert.That(_safe.Recycle(null), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SafeIsolatesFailedHooksWithOrWithoutCapacity(bool full)
        {
            _safe.MaxPoolSize = 1;
            var item = _safe.Allocate();
            if (full)
            {
                _safe.Recycle(_safe.Allocate());
            }

            item.Recycled = () => throw new InvalidOperationException();
            Assert.Throws<InvalidOperationException>(() => _safe.Recycle(item));
            Assert.That(item.IsRecycled, Is.True);
            item.IsRecycled = false;
            Assert.That(_safe.Recycle(item), Is.False);
            Assert.That(_safe.CurStoreCount, Is.EqualTo(full ? 1 : 0));
        }

        [Test]
        public void SafePrewarmUnmarksFactoryObjectsAndIgnoresNullWithFiniteAttempts()
        {
            var calls = 0;
            _safe.SetFactoryMethod(() => ++calls == 1 ? null : new SafeItem { IsRecycled = true });
            Assert.That(_safe.Prewarm(3), Is.EqualTo(2));
            Assert.That(calls, Is.EqualTo(3));
            var item = _safe.Allocate();
            Assert.That(item.Hooks, Is.EqualTo(1));
            Assert.That(item.IsRecycled, Is.False);
            _safe.Clear();
            _safe.SetFactoryMethod(() => null);
            Assert.That(_safe.TryAllocate(out item), Is.False);
        }

        [Test]
        public void SafeClearAndDisposeDoNotTouchBorrowedObjectsOrRepeatHooks()
        {
            var borrowed = _safe.Allocate();
            var cached = _safe.Allocate();
            _safe.Recycle(cached);
            _safe.Clear();
            Assert.That(cached.IsRecycled, Is.True);
            Assert.That(cached.Hooks, Is.EqualTo(1));
            Assert.That(borrowed.IsRecycled, Is.False);
            _safe.Dispose();
            Assert.That(SafeObjectPool<SafeItem>.Instance, Is.Not.SameAs(_safe));
            Assert.That(SafeObjectPool<SafeItem>.Instance.CurStoreCount, Is.Zero);
            var current = SafeObjectPool<SafeItem>.Instance;
            _safe.Dispose();
            Assert.That(SafeObjectPool<SafeItem>.Instance, Is.SameAs(current));
            Assert.Throws<InvalidOperationException>(() => current.Recycle(borrowed));
            Assert.That(borrowed.IsRecycled, Is.False);
            Assert.That(_safe.Recycle(borrowed), Is.False);
            Assert.That(borrowed.IsRecycled, Is.True);
            Assert.That(borrowed.Hooks, Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(() => _safe.Allocate());
            current.Dispose();
        }

        [Test]
        public void SafeRawPrewarmMarksIdleWithoutLifecycleAndColdHotHooksRejectReturnDuringAllocation()
        {
            SafeItem created = null;
            _safe.SetFactoryMethod(() => created = new SafeItem());
            Assert.That(_safe.Prewarm(1, PoolPrewarmMode.Raw), Is.EqualTo(1));
            Assert.That(created.IsRecycled, Is.True);
            Assert.That(created.Hooks, Is.Zero);
            created.Allocated = () =>
            {
                Assert.That(created.IsRecycled, Is.False);
                Assert.That(_safe.Recycle(created), Is.False);
            };
            Assert.That(_safe.Allocate(), Is.SameAs(created));
            _safe.Recycle(created);
            Assert.That(_safe.Allocate(), Is.SameAs(created));
            Assert.That(created.Hooks, Is.EqualTo(1));
        }

        [Test]
        public void SafeFailedAllocationAndDisposeInsideHookNeverDeliverOrCacheTheObject()
        {
            var item = new SafeItem { Allocated = () => throw new InvalidOperationException("allocate") };
            _safe.SetFactoryMethod(() => item);
            Assert.Throws<InvalidOperationException>(() => _safe.Allocate());
            Assert.That(item.IsRecycled, Is.True);
            Assert.That(_safe.Recycle(item), Is.False);
            Assert.That(_safe.CurStoreCount, Is.Zero);
            item = new SafeItem { Allocated = () => _safe.Dispose() };
            _safe.SetFactoryMethod(() => item);
            Assert.Throws<ObjectDisposedException>(() => _safe.Allocate());
            Assert.That(item.IsRecycled, Is.True);
            Assert.That(_safe.Recycle(item), Is.False);
        }

        [Test]
        public void ClearAttemptsEveryCallbackAndLeavesAllSnapshotObjectsRetired()
        {
            using var pool = new ObjectPool<object>(() => new object());
            pool.Prewarm(3);
            var cleared = 0;
            Assert.Throws<AggregateException>(() => pool.Clear(item =>
            {
                cleared++;
                Assert.That(pool.Recycle(item), Is.False);
                throw new InvalidOperationException();
            }));
            Assert.That(cleared, Is.EqualTo(3));
            Assert.That(pool.CurStoreCount, Is.Zero);
            pool.Dispose();
        }
    }
}
