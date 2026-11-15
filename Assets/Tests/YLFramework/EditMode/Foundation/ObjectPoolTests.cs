using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using YLFramework.Pooling;

namespace YLFramework.Foundation.Tests
{
    public class ObjectPoolTests
    {
        private sealed class EqualItem
        {
            internal int Hash;
            internal int EqualsCalls;
            internal int HashCalls;

            public override bool Equals(object obj)
            {
                EqualsCalls++;
                return obj is EqualItem;
            }

            public override int GetHashCode()
            {
                HashCalls++;
                return Hash;
            }
        }

        [Test]
        public void ColdAndHotAllocateInvokeTheSameHookWithoutResetting()
        {
            var created = 0;
            var allocated = new List<object>();
            var reset = new List<object>();
            var discarded = new List<object>();
            using var pool = new ObjectPool<object>(() =>
            {
                created++;
                return new object();
            },
                onAllocate: allocated.Add, reset: reset.Add, onDiscard: discarded.Add);

            var cold = pool.Allocate();
            Assert.That(created, Is.EqualTo(1));
            Assert.That(allocated.Count, Is.EqualTo(1));
            Assert.That(allocated[0], Is.SameAs(cold));
            Assert.That(reset, Is.Empty);
            Assert.That(pool.Recycle(cold), Is.True);
            Assert.That(pool.TryAllocate(out var hot), Is.True);

            Assert.That(hot, Is.SameAs(cold));
            Assert.That(created, Is.EqualTo(1));
            Assert.That(allocated.Count, Is.EqualTo(2));
            Assert.That(allocated[1], Is.SameAs(cold));
            Assert.That(reset, Is.EqualTo(new[] { cold }));
            Assert.That(discarded, Is.Empty);
            Assert.That(pool.CurStoreCount, Is.Zero);
        }

        [Test]
        public void RecycleOverflowDiscardAndClearKeepResetAndPermanentCleanupDistinct()
        {
            var calls = new List<string>();
            using var pool = new ObjectPool<object>(() => new object(),
                reset: _ => calls.Add("reset"), onDiscard: _ => calls.Add("discard"), maxPoolSize: 1);
            var cached = pool.Allocate();
            var overflow = pool.Allocate();
            var explicitDiscard = pool.Allocate();

            Assert.That(pool.Recycle(cached), Is.True);
            Assert.That(calls, Is.EqualTo(new[] { "reset" }));
            Assert.That(pool.Recycle(overflow), Is.False);
            pool.Discard(explicitDiscard);
            Assert.That(calls, Is.EqualTo(new[] { "reset", "reset", "discard", "reset", "discard" }));
            Assert.That(pool.CurStoreCount, Is.EqualTo(1));

            pool.Clear();
            Assert.That(calls, Is.EqualTo(new[] { "reset", "reset", "discard", "reset", "discard", "discard" }));
            Assert.That(pool.Recycle(cached), Is.False);
            Assert.That(pool.Recycle(overflow), Is.False);
            pool.Discard(explicitDiscard);
            Assert.That(calls.Count, Is.EqualTo(6));
            Assert.That(pool.CurStoreCount, Is.Zero);
        }

        [TestCase(PoolCacheOrder.Fifo)]
        [TestCase(PoolCacheOrder.Lifo)]
        public void IdentityDoesNotUseValueEqualityOrMutableHash(PoolCacheOrder order)
        {
            using var pool = new ObjectPool<EqualItem>(() => new EqualItem(), cacheOrder: order);
            var first = pool.Allocate();
            var second = pool.Allocate();
            Assert.That(first, Is.Not.SameAs(second));
            first.Hash = 17;
            second.Hash = 17;
            Assert.That(pool.Recycle(first), Is.True);
            first.Hash = 99;
            Assert.That(pool.Recycle(second), Is.True);
            second.Hash = -1;
            Assert.That(pool.Recycle(first), Is.False);
            Assert.That(pool.CurStoreCount, Is.EqualTo(2));
            Assert.That(pool.Allocate(), Is.SameAs(order == PoolCacheOrder.Fifo ? first : second));
            Assert.That(pool.Allocate(), Is.SameAs(order == PoolCacheOrder.Fifo ? second : first));
            Assert.That(first.EqualsCalls + second.EqualsCalls, Is.Zero);
            Assert.That(first.HashCalls + second.HashCalls, Is.Zero);
        }

        [Test]
        public void NullDuplicateForeignAndRetiredReturnsDoNotRepeatCallbacks()
        {
            var resets = 0;
            var discards = 0;
            using var pool = new ObjectPool<object>(() => new object(),
                reset: _ => resets++, onDiscard: _ => discards++);
            using var otherPool = new ObjectPool<object>(() => new object());
            var owned = pool.Allocate();
            var foreign = otherPool.Allocate();

            Assert.That(pool.Recycle(null), Is.False);
            pool.Discard(null);
            Assert.Throws<InvalidOperationException>(() => pool.Recycle(new object()));
            Assert.Throws<InvalidOperationException>(() => pool.Recycle(foreign));
            Assert.Throws<InvalidOperationException>(() => pool.Discard(foreign));
            Assert.That(resets, Is.Zero);
            Assert.That(discards, Is.Zero);
            Assert.That(otherPool.Recycle(foreign), Is.True);
            Assert.That(otherPool.Allocate(), Is.SameAs(foreign));

            Assert.That(pool.Recycle(owned), Is.True);
            Assert.That(pool.Recycle(owned), Is.False);
            pool.Discard(owned);
            Assert.That(resets, Is.EqualTo(1));
            Assert.That(discards, Is.Zero);
            Assert.That(pool.Allocate(), Is.SameAs(owned));
            pool.Discard(owned);
            Assert.That(pool.Recycle(owned), Is.False);
            pool.Discard(owned);
            Assert.That(resets, Is.EqualTo(2));
            Assert.That(discards, Is.EqualTo(1));
            Assert.That(pool.CurStoreCount, Is.Zero);
        }

        [TestCase("leased")]
        [TestCase("idle")]
        [TestCase("retired")]
        public void FactoryCannotReturnAnAlreadyRegisteredReference(string state)
        {
            var item = new object();
            var allocations = 0;
            var resets = 0;
            var discards = 0;
            using var pool = new ObjectPool<object>(() => item,
                onAllocate: _ => allocations++, reset: _ => resets++, onDiscard: _ => discards++);
            Assert.That(pool.Allocate(), Is.SameAs(item));
            if (state == "idle")
            {
                Assert.That(pool.Recycle(item), Is.True);
            }

            if (state == "retired")
            {
                pool.Discard(item);
            }

            var previousResets = resets;
            var previousDiscards = discards;

            if (state == "idle")
            {
                Assert.Throws<InvalidOperationException>(() => pool.Prewarm(2, PoolPrewarmMode.Raw));
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => pool.Allocate());
            }

            Assert.That(allocations, Is.EqualTo(1));
            Assert.That(resets, Is.EqualTo(previousResets));
            Assert.That(discards, Is.EqualTo(previousDiscards));
            Assert.That(pool.CurStoreCount, Is.EqualTo(state == "idle" ? 1 : 0));
            if (state == "idle")
            {
                Assert.That(pool.Allocate(), Is.SameAs(item));
            }

            Assert.That(pool.Recycle(item), Is.EqualTo(state != "retired"));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void AllocateFailurePreservesRootCauseAndRetiresColdOrHotObject(bool hot, bool cleanupFails)
        {
            var root = new InvalidOperationException("allocate root");
            var cleanup = new ApplicationException("discard failure");
            var fail = false;
            var resets = 0;
            var discards = 0;
            object failed = null;
            using var pool = new ObjectPool<object>(() => failed = new object(),
                onAllocate: _ =>
                {
                    if (fail)
                    {
                        throw root;
                    }
                }, reset: _ => resets++,
                onDiscard: _ =>
                {
                    discards++;
                    if (cleanupFails)
                    {
                        throw cleanup;
                    }
                });
            if (hot)
            {
                Assert.That(pool.Recycle(pool.Allocate()), Is.True);
            }

            fail = true;

            if (cleanupFails)
            {
                var error = Assert.Throws<AggregateException>(() => pool.Allocate());
                Assert.That(error.InnerExceptions.Count, Is.EqualTo(2));
                Assert.That(error.InnerExceptions[0], Is.SameAs(root));
                Assert.That(error.InnerExceptions[1], Is.SameAs(cleanup));
            }
            else
            {
                Assert.That(Assert.Throws<InvalidOperationException>(() => pool.Allocate()), Is.SameAs(root));
            }

            Assert.That(pool.CurStoreCount, Is.Zero);
            Assert.That(resets, Is.EqualTo(hot ? 1 : 0));
            Assert.That(discards, Is.EqualTo(1));
            Assert.That(pool.Recycle(failed), Is.False);
            pool.Discard(failed);
            Assert.That(discards, Is.EqualTo(1));
            fail = false;
            var recovered = pool.Allocate();
            Assert.That(pool.Recycle(recovered), Is.True);
            Assert.That(pool.Allocate(), Is.SameAs(recovered));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ResetFailurePreservesRootCauseAndRetiresObject(bool discard, bool cleanupFails)
        {
            var root = new InvalidOperationException("reset root");
            var cleanup = new ApplicationException("discard failure");
            var fail = true;
            var resets = 0;
            var discards = 0;
            using var pool = new ObjectPool<object>(() => new object(),
                reset: _ =>
                {
                    resets++;
                    if (fail)
                    {
                        throw root;
                    }
                },
                onDiscard: _ =>
                {
                    discards++;
                    if (cleanupFails)
                    {
                        throw cleanup;
                    }
                });
            var failed = pool.Allocate();
            TestDelegate returnFailed = () =>
            {
                if (discard)
                {
                    pool.Discard(failed);
                }
                else
                {
                    pool.Recycle(failed);
                }
            };

            if (cleanupFails)
            {
                var error = Assert.Throws<AggregateException>(returnFailed);
                Assert.That(error.InnerExceptions.Count, Is.EqualTo(2));
                Assert.That(error.InnerExceptions[0], Is.SameAs(root));
                Assert.That(error.InnerExceptions[1], Is.SameAs(cleanup));
            }
            else
            {
                Assert.That(Assert.Throws<InvalidOperationException>(returnFailed), Is.SameAs(root));
            }

            Assert.That(pool.CurStoreCount, Is.Zero);
            Assert.That(pool.Recycle(failed), Is.False);
            pool.Discard(failed);
            Assert.That(resets, Is.EqualTo(1));
            Assert.That(discards, Is.EqualTo(1));
            fail = false;
            var recovered = pool.Allocate();
            Assert.That(recovered, Is.Not.SameAs(failed));
            Assert.That(pool.Recycle(recovered), Is.True);
            Assert.That(pool.Allocate(), Is.SameAs(recovered));
        }

        [Test]
        public void FactoryFailurePropagatesThroughAllocateAndTryAllocateWithoutCallbacks()
        {
            var root = new ApplicationException("factory root");
            var callbacks = 0;
            using var pool = new ObjectPool<object>(() => throw root,
                onAllocate: _ => callbacks++, reset: _ => callbacks++, onDiscard: _ => callbacks++);
            Assert.That(Assert.Throws<ApplicationException>(() => pool.Allocate()), Is.SameAs(root));
            Assert.That(Assert.Throws<ApplicationException>(() => pool.TryAllocate(out _)), Is.SameAs(root));
            Assert.That(callbacks, Is.Zero);
            Assert.That(pool.CurStoreCount, Is.Zero);
        }

        [Test]
        public void SameObjectRecycleIsRejectedInsideAllocateAndResetCallbacks()
        {
            ObjectPool<object> pool = null;
            var nestedResults = new List<bool>();
            var resets = 0;
            var discards = 0;
            pool = new ObjectPool<object>(() => new object(),
                onAllocate: item => nestedResults.Add(pool.Recycle(item)),
                reset: item =>
                {
                    resets++;
                    nestedResults.Add(pool.Recycle(item));
                },
                onDiscard: _ => discards++);
            using (pool)
            {
                var item = pool.Allocate();
                Assert.That(pool.Recycle(item), Is.True);
                Assert.That(pool.Allocate(), Is.SameAs(item));
                pool.Discard(item);
                Assert.That(nestedResults, Is.EqualTo(new[] { false, false, false, false }));
                Assert.That(resets, Is.EqualTo(2));
                Assert.That(discards, Is.EqualTo(1));
                Assert.That(pool.CurStoreCount, Is.Zero);
            }
        }

        [Test]
        public void ResetReturningAnotherObjectRechecksCapacityBeforeCachingOuterObject()
        {
            ObjectPool<object> pool = null;
            object outer = null;
            object other = null;
            var resets = new List<object>();
            var discarded = new List<object>();
            var nestedCached = false;
            pool = new ObjectPool<object>(() => new object(), reset: item =>
            {
                resets.Add(item);
                if (ReferenceEquals(item, outer))
                {
                    nestedCached = pool.Recycle(other);
                }
            }, onDiscard: discarded.Add, maxPoolSize: 1);
            using (pool)
            {
                outer = pool.Allocate();
                other = pool.Allocate();
                Assert.That(pool.Recycle(outer), Is.False);
                Assert.That(nestedCached, Is.True);
                Assert.That(pool.CurStoreCount, Is.EqualTo(1));
                Assert.That(resets, Is.EqualTo(new[] { outer, other }));
                Assert.That(discarded, Is.EqualTo(new[] { outer }));
                Assert.That(pool.Allocate(), Is.SameAs(other));
                Assert.That(pool.Recycle(outer), Is.False);
            }
        }

        [Test]
        public void InitiallyFullReturnStillDiscardsWhenResetAllocatesTheCachedObject()
        {
            ObjectPool<object> pool = null;
            object outer = null;
            object nestedLease = null;
            var discarded = new List<object>();
            pool = new ObjectPool<object>(() => new object(), reset: item =>
            {
                if (ReferenceEquals(item, outer))
                {
                    nestedLease = pool.Allocate();
                }
            }, onDiscard: discarded.Add, maxPoolSize: 1);
            using (pool)
            {
                var cached = pool.Allocate();
                outer = pool.Allocate();
                Assert.That(pool.Recycle(cached), Is.True);
                Assert.That(pool.Recycle(outer), Is.False);
                Assert.That(nestedLease, Is.SameAs(cached));
                Assert.That(pool.CurStoreCount, Is.Zero);
                Assert.That(discarded, Is.EqualTo(new[] { outer }));
                Assert.That(pool.Recycle(nestedLease), Is.True);
            }
        }

        [Test]
        public void DisposeInsideResetRetiresTheReturningObjectWithoutRevivingCache()
        {
            ObjectPool<object> pool = null;
            var resets = 0;
            var discards = 0;
            pool = new ObjectPool<object>(() => new object(),
                reset: _ =>
                {
                    resets++;
                    pool.Dispose();
                }, onDiscard: _ => discards++);
            using (pool)
            {
                var item = pool.Allocate();
                Assert.That(pool.Recycle(item), Is.False);
                Assert.That(resets, Is.EqualTo(1));
                Assert.That(discards, Is.EqualTo(1));
                Assert.That(pool.CurStoreCount, Is.Zero);
                Assert.That(pool.Recycle(item), Is.False);
                Assert.Throws<ObjectDisposedException>(() => pool.Allocate());
            }
        }

        [TestCase(PoolCacheOrder.Fifo)]
        [TestCase(PoolCacheOrder.Lifo)]
        public void ClearRetiresTheWholeSnapshotBeforeCallbacksAndPreservesNewCache(PoolCacheOrder order)
        {
            ObjectPool<object> pool = null;
            object first = null;
            object second = null;
            object replacement = null;
            var callbacks = 0;
            var resets = 0;
            var root = new ApplicationException("first cleanup fails");
            var discarded = new List<object>();
            pool = new ObjectPool<object>(() => new object(), reset: _ => resets++,
                onDiscard: item =>
                {
                    discarded.Add(item);
                    if (++callbacks != 1)
                    {
                        return;
                    }

                    Assert.That(pool.CurStoreCount, Is.Zero);
                    Assert.That(pool.Recycle(first), Is.False);
                    Assert.That(pool.Recycle(second), Is.False);
                    pool.Discard(first);
                    pool.Discard(second);
                    Assert.That(pool.Recycle(replacement), Is.True);
                    throw root;
                }, cacheOrder: order);
            using (pool)
            {
                first = pool.Allocate();
                second = pool.Allocate();
                replacement = pool.Allocate();
                Assert.That(pool.Recycle(first), Is.True);
                Assert.That(pool.Recycle(second), Is.True);

                var error = Assert.Throws<AggregateException>(() => pool.Clear());
                Assert.That(error.InnerExceptions.Count, Is.EqualTo(1));
                Assert.That(error.InnerExceptions[0], Is.SameAs(root));
                Assert.That(discarded, Is.EqualTo(order == PoolCacheOrder.Fifo
                    ? new[] { first, second } : new[] { second, first }));
                Assert.That(resets, Is.EqualTo(3));
                Assert.That(pool.CurStoreCount, Is.EqualTo(1));
                Assert.That(pool.Allocate(), Is.SameAs(replacement));
                Assert.That(pool.Recycle(first), Is.False);
                Assert.That(pool.Recycle(second), Is.False);
            }
        }

        [TestCase(PoolCacheOrder.Fifo)]
        [TestCase(PoolCacheOrder.Lifo)]
        public void ShrinkAttemptsEverySelectedCleanupAndUsesAllocationOrder(PoolCacheOrder order)
        {
            var resets = 0;
            var discarded = new List<object>();
            var firstError = new ApplicationException("first cleanup");
            var secondError = new InvalidOperationException("second cleanup");
            using var pool = new ObjectPool<object>(() => new object(), reset: _ => resets++,
                onDiscard: item =>
                {
                    discarded.Add(item);
                    if (discarded.Count == 1)
                    {
                        throw firstError;
                    }

                    if (discarded.Count == 2)
                    {
                        throw secondError;
                    }
                }, cacheOrder: order);
            var first = pool.Allocate();
            var second = pool.Allocate();
            var third = pool.Allocate();
            Assert.That(pool.Recycle(first), Is.True);
            Assert.That(pool.Recycle(second), Is.True);
            Assert.That(pool.Recycle(third), Is.True);

            var error = Assert.Throws<AggregateException>(() => pool.MaxPoolSize = 1);
            Assert.That(error.InnerExceptions.Count, Is.EqualTo(2));
            Assert.That(error.InnerExceptions[0], Is.SameAs(firstError));
            Assert.That(error.InnerExceptions[1], Is.SameAs(secondError));
            Assert.That(discarded, Is.EqualTo(order == PoolCacheOrder.Fifo
                ? new[] { first, second } : new[] { third, second }));
            Assert.That(resets, Is.EqualTo(3));
            Assert.That(pool.MaxPoolSize, Is.EqualTo(1));
            Assert.That(pool.CurStoreCount, Is.EqualTo(1));
            Assert.That(pool.Recycle(discarded[0]), Is.False);
            Assert.That(pool.Recycle(discarded[1]), Is.False);
            Assert.That(pool.Allocate(), Is.SameAs(order == PoolCacheOrder.Fifo ? third : first));
        }

        [Test]
        public void ShrinkCleanupReentrantReturnObservesTheNewCapacity()
        {
            ObjectPool<object> pool = null;
            object late = null;
            var discarded = new List<object>();
            var resets = 0;
            var nestedResult = true;
            pool = new ObjectPool<object>(() => new object(), reset: _ => resets++, onDiscard: item =>
            {
                discarded.Add(item);
                if (!ReferenceEquals(item, late))
                {
                    nestedResult = pool.Recycle(late);
                }
            });
            using (pool)
            {
                var first = pool.Allocate();
                var second = pool.Allocate();
                late = pool.Allocate();
                Assert.That(pool.Recycle(first), Is.True);
                Assert.That(pool.Recycle(second), Is.True);
                pool.MaxPoolSize = 1;
                Assert.That(nestedResult, Is.False);
                Assert.That(discarded, Is.EqualTo(new[] { first, late }));
                Assert.That(resets, Is.EqualTo(3));
                Assert.That(pool.CurStoreCount, Is.EqualTo(1));
                Assert.That(pool.Allocate(), Is.SameAs(second));
            }
        }

        [Test]
        public void DisposeCleansEveryIdleObjectDespiteErrorsAndLateReturnsCannotReviveCache()
        {
            ObjectPool<object> pool = null;
            var resets = new List<object>();
            var discarded = new List<object>();
            var errors = new[] { new ApplicationException("one"), new ApplicationException("two") };
            var fail = true;
            pool = new ObjectPool<object>(() => new object(), reset: resets.Add, onDiscard: item =>
            {
                discarded.Add(item);
                Assert.Throws<ObjectDisposedException>(() => pool.Allocate());
                pool.Dispose();
                if (fail)
                {
                    throw errors[discarded.Count - 1];
                }
            });
            using (pool)
            {
                var first = pool.Allocate();
                var second = pool.Allocate();
                var late = pool.Allocate();
                var lateDiscard = pool.Allocate();
                Assert.That(pool.Recycle(first), Is.True);
                Assert.That(pool.Recycle(second), Is.True);

                var error = Assert.Throws<AggregateException>(() => pool.Dispose());
                Assert.That(error.InnerExceptions.Count, Is.EqualTo(2));
                Assert.That(error.InnerExceptions[0], Is.SameAs(errors[0]));
                Assert.That(error.InnerExceptions[1], Is.SameAs(errors[1]));
                Assert.That(discarded, Is.EqualTo(new[] { first, second }));
                Assert.That(resets, Is.EqualTo(new[] { first, second }));
                Assert.That(pool.CurStoreCount, Is.Zero);
                Assert.DoesNotThrow(() => pool.Dispose());
                Assert.Throws<ObjectDisposedException>(() => pool.TryAllocate(out _));
                Assert.Throws<ObjectDisposedException>(() => pool.Prewarm(0));
                Assert.Throws<ObjectDisposedException>(() => pool.SetFactoryMethod(() => new object()));
                Assert.Throws<ObjectDisposedException>(() => pool.SetObjectFactory(null));

                fail = false;
                Assert.That(pool.Recycle(late), Is.False);
                pool.Discard(lateDiscard);
                Assert.That(pool.Recycle(late), Is.False);
                Assert.That(pool.Recycle(first), Is.False);
                Assert.That(discarded, Is.EqualTo(new[] { first, second, late, lateDiscard }));
                Assert.That(resets, Is.EqualTo(new[] { first, second, late, lateDiscard }));
                Assert.That(pool.CurStoreCount, Is.Zero);
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        public void AllocateCannotDeliverAnObjectAfterFactoryOrAllocateHookDisposesPool(bool factoryDisposes, bool hot)
        {
            ObjectPool<object> pool = null;
            var item = new object();
            var allocations = 0;
            var resets = 0;
            var discarded = new List<object>();
            var close = false;
            pool = new ObjectPool<object>(() =>
            {
                if (factoryDisposes && close)
                {
                    pool.Dispose();
                }

                return item;
            }, onAllocate: _ =>
            {
                allocations++;
                if (close)
                {
                    pool.Dispose();
                }
            },
                reset: _ => resets++, onDiscard: discarded.Add);
            using (pool)
            {
                if (hot)
                {
                    Assert.That(pool.Recycle(pool.Allocate()), Is.True);
                }

                close = true;
                Assert.Throws<ObjectDisposedException>(() => pool.Allocate());
                Assert.That(allocations, Is.EqualTo(hot ? 2 : factoryDisposes ? 0 : 1));
                Assert.That(resets, Is.EqualTo(hot ? 1 : 0));
                Assert.That(discarded, Is.EqualTo(new[] { item }));
                Assert.That(pool.CurStoreCount, Is.Zero);
                Assert.That(pool.Recycle(item), Is.False);
                pool.Discard(item);
                Assert.That(discarded.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void NullFactoryResultNeverCachesOrInvokesCallbacks()
        {
            var created = 0;
            var callbacks = 0;
            using var pool = new ObjectPool<object>(() =>
            {
                created++;
                return null;
            },
                onAllocate: _ => callbacks++, reset: _ => callbacks++, onDiscard: _ => callbacks++);
            Assert.That(pool.Allocate(), Is.Null);
            Assert.That(pool.TryAllocate(out var item), Is.False);
            Assert.That(item, Is.Null);
            Assert.That(created, Is.EqualTo(2));
            Assert.That(callbacks, Is.Zero);
            Assert.That(pool.CurStoreCount, Is.Zero);
        }

        [TestCase(PoolPrewarmMode.Raw)]
        [TestCase(PoolPrewarmMode.Recycle)]
        public void PrewarmNullResultsUseFiniteAttemptsWithoutConsumingCacheCapacity(PoolPrewarmMode mode)
        {
            var attempts = 0;
            var allocations = 0;
            var resets = 0;
            using var pool = new ObjectPool<object>(() => ++attempts == 2 ? new object() : null,
                onAllocate: _ => allocations++, reset: _ => resets++, maxPoolSize: 3);
            Assert.That(pool.Prewarm(10, mode), Is.EqualTo(1));
            Assert.That(attempts, Is.EqualTo(3));
            Assert.That(pool.CurStoreCount, Is.EqualTo(1));
            Assert.That(pool.Prewarm(3, mode), Is.Zero);
            Assert.That(attempts, Is.EqualTo(5));
            Assert.That(pool.CurStoreCount, Is.EqualTo(1));
            Assert.That(allocations, Is.Zero);
            Assert.That(resets, Is.EqualTo(mode == PoolPrewarmMode.Raw ? 0 : 1));
        }

        [TestCase(PoolPrewarmMode.Raw)]
        [TestCase(PoolPrewarmMode.Recycle)]
        public void PrewarmUsesTargetCountAndCapacityAndDoesNotAllocateHooks(PoolPrewarmMode mode)
        {
            var created = 0;
            var allocations = 0;
            var resets = 0;
            using var pool = new ObjectPool<object>(() =>
            {
                created++;
                return new object();
            },
                onAllocate: _ => allocations++, reset: _ => resets++, maxPoolSize: 3);
            Assert.That(pool.Prewarm(-1, mode), Is.Zero);
            Assert.That(pool.Prewarm(int.MinValue, mode), Is.Zero);
            Assert.That(pool.Prewarm(0, mode), Is.Zero);
            Assert.That(created, Is.Zero);
            Assert.That(pool.Prewarm(2, mode), Is.EqualTo(2));
            Assert.That(pool.Prewarm(1, mode), Is.Zero);
            Assert.That(pool.Prewarm(2, mode), Is.Zero);
            Assert.That(pool.Prewarm(int.MaxValue, mode), Is.EqualTo(1));
            Assert.That(pool.CurStoreCount, Is.EqualTo(3));
            Assert.That(created, Is.EqualTo(3));
            Assert.That(allocations, Is.Zero);
            Assert.That(resets, Is.EqualTo(mode == PoolPrewarmMode.Raw ? 0 : 3));
            Assert.That(pool.Allocate(), Is.Not.Null);
            Assert.That(allocations, Is.EqualTo(1));
            Assert.That(created, Is.EqualTo(3));
        }

        [TestCase(-1)]
        public void MinusOneCapacityIsUnboundedAndDoesNotLimitLeasedObjects(int capacity)
        {
            using var pool = new ObjectPool<object>(() => new object(), maxPoolSize: capacity);
            Assert.That(pool.Prewarm(25, PoolPrewarmMode.Raw), Is.EqualTo(25));
            var leases = new List<object>();
            for (var i = 0; i < 30; i++)
            {
                leases.Add(pool.Allocate());
            }

            Assert.That(pool.CurStoreCount, Is.Zero);
            foreach (var item in leases)
            {
                Assert.That(pool.Recycle(item), Is.True);
            }

            Assert.That(pool.CurStoreCount, Is.EqualTo(30));
        }

        [TestCase(PoolCacheOrder.Fifo)]
        [TestCase(PoolCacheOrder.Lifo)]
        public void ZeroCapacityCreatesWithoutQuotaButNeverCachesOrPrewarms(PoolCacheOrder order)
        {
            int created = 0;
            int resets = 0;
            int discarded = 0;
            using var pool = new ObjectPool<object>(() => { created++; return new object(); },
                reset: _ => resets++, onDiscard: _ => discarded++, maxPoolSize: 0, cacheOrder: order);
            Assert.That(pool.Prewarm(25, PoolPrewarmMode.Raw), Is.Zero);
            Assert.That(pool.Prewarm(25, PoolPrewarmMode.Recycle), Is.Zero);
            Assert.That(created, Is.Zero, "A target-idle prewarm must not create merely to discard when caching is disabled.");
            var raw = pool.Allocate();
            var lease = pool.Rent(out var scoped);
            Assert.That(pool.TryAllocate(out var third), Is.True);
            Assert.That(created, Is.EqualTo(3));
            Assert.That(raw, Is.Not.SameAs(scoped));
            Assert.That(pool.Recycle(raw), Is.False);
            lease.Dispose();
            pool.Discard(third);
            lease.Discard();
            Assert.That(pool.Recycle(raw), Is.False);
            Assert.That(resets, Is.EqualTo(3));
            Assert.That(discarded, Is.EqualTo(3));
            Assert.That(pool.CurStoreCount, Is.Zero);
            pool.MaxPoolSize = -1;
            Assert.That(pool.Prewarm(25, PoolPrewarmMode.Raw), Is.EqualTo(25));
            Assert.That(pool.CurStoreCount, Is.EqualTo(25));
        }

        [TestCase(-2)]
        [TestCase(int.MinValue)]
        public void InvalidCapacityIsRejectedBeforeConstructorOrSetterSideEffects(int capacity)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectPool<object>(() => new object(), maxPoolSize: capacity));
            int discarded = 0;
            using var pool = new ObjectPool<object>(() => new object(), onDiscard: _ => discarded++);
            pool.Prewarm(2, PoolPrewarmMode.Raw);
            Assert.Throws<ArgumentOutOfRangeException>(() => pool.MaxPoolSize = capacity);
            Assert.That(pool.MaxPoolSize, Is.EqualTo(20));
            Assert.That(pool.CurStoreCount, Is.EqualTo(2));
            Assert.That(discarded, Is.Zero);
        }

        [TestCase(PoolCacheOrder.Fifo)]
        [TestCase(PoolCacheOrder.Lifo)]
        public void ChangingCapacityToZeroRetiresIdleOnlyAndLateBorrowStillCleans(PoolCacheOrder order)
        {
            int resets = 0;
            int discarded = 0;
            using var pool = new ObjectPool<object>(() => new object(), reset: _ => resets++, onDiscard: _ => discarded++, cacheOrder: order);
            pool.Prewarm(4);
            var raw = pool.Allocate();
            var scoped = pool.Rent(out _);
            pool.MaxPoolSize = 0;
            Assert.That(pool.CurStoreCount, Is.Zero);
            Assert.That(resets, Is.EqualTo(4), "Shrinking only retires idle entries; borrowed and already-reset objects are not reset again.");
            Assert.That(discarded, Is.EqualTo(2));
            Assert.That(pool.Recycle(raw), Is.False);
            scoped.Dispose();
            Assert.That(resets, Is.EqualTo(6));
            Assert.That(discarded, Is.EqualTo(4));
            Assert.That(pool.CurStoreCount, Is.Zero);
        }

        [Test]
        public void ZeroCapacityShrinkAttemptsAllCleanupAndReentryCannotRefillIt()
        {
            ObjectPool<object> pool = null;
            object late = null;
            var failures = new List<Exception>();
            int resets = 0;
            int discarded = 0;
            pool = new ObjectPool<object>(() => new object(), reset: _ => resets++, onDiscard: value =>
            {
                discarded++;
                if (ReferenceEquals(value, late))
                {
                    return;
                }
                Assert.That(pool.CurStoreCount, Is.Zero);
                Assert.That(pool.Recycle(late), Is.False);
                var error = new InvalidOperationException("zero-shrink-" + failures.Count);
                failures.Add(error);
                throw error;
            });
            using (pool)
            {
                pool.Prewarm(2, PoolPrewarmMode.Raw);
                late = pool.Allocate();
                var idle = pool.Allocate();
                pool.Recycle(idle);
                // Keep two actual idle identities while late remains borrowed.
                pool.Prewarm(2, PoolPrewarmMode.Raw);
                var error = Assert.Throws<AggregateException>(() => pool.MaxPoolSize = 0);
                Assert.That(error.InnerExceptions, Is.EqualTo(failures));
                Assert.That(failures.Count, Is.EqualTo(2));
                Assert.That(discarded, Is.EqualTo(3));
                Assert.That(resets, Is.EqualTo(2));
                Assert.That(pool.MaxPoolSize, Is.Zero);
                Assert.That(pool.CurStoreCount, Is.Zero);
                Assert.That(pool.Recycle(late), Is.False);
            }
        }

        [TestCase(PoolPrewarmMode.Raw)]
        [TestCase(PoolPrewarmMode.Recycle)]
        public void PrewarmRechecksCachingDisabledDuringCreation(PoolPrewarmMode mode)
        {
            ObjectPool<object> pool = null;
            int created = 0;
            int resets = 0;
            int discarded = 0;
            pool = new ObjectPool<object>(() => { created++; pool.MaxPoolSize = 0; return new object(); },
                reset: _ => resets++, onDiscard: _ => discarded++);
            using (pool)
            {
                Assert.That(pool.Prewarm(1, mode), Is.Zero);
                Assert.That(created, Is.EqualTo(1));
                Assert.That(resets, Is.EqualTo(mode == PoolPrewarmMode.Raw ? 0 : 1));
                Assert.That(discarded, Is.EqualTo(1));
                Assert.That(pool.CurStoreCount, Is.Zero);
                Assert.That(pool.Prewarm(1, mode), Is.Zero);
                Assert.That(created, Is.EqualTo(1));
            }
        }

        [Test]
        public void ResetMayDisableCachingBeforeTheReturningObjectIsPublished()
        {
            ObjectPool<object> pool = null;
            int resets = 0;
            int discarded = 0;
            pool = new ObjectPool<object>(() => new object(), reset: _ => { resets++; pool.MaxPoolSize = 0; }, onDiscard: _ => discarded++);
            using (pool)
            {
                var rental = pool.Rent(out _);
                rental.Dispose();
                Assert.That(resets, Is.EqualTo(1));
                Assert.That(discarded, Is.EqualTo(1));
                Assert.That(pool.CurStoreCount, Is.Zero);
            }
        }

        [TestCase(PoolPrewarmMode.Raw)]
        [TestCase(PoolPrewarmMode.Recycle)]
        public void PrewarmFactoryFailureKeepsObjectsAlreadyCached(PoolPrewarmMode mode)
        {
            var root = new ApplicationException("prewarm factory root");
            var attempts = 0;
            var resets = 0;
            var discards = 0;
            object first = null;
            using var pool = new ObjectPool<object>(() =>
            {
                if (++attempts == 2)
                {
                    throw root;
                }

                return first = new object();
            }, reset: _ => resets++, onDiscard: _ => discards++);
            Assert.That(Assert.Throws<ApplicationException>(() => pool.Prewarm(3, mode)), Is.SameAs(root));
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(pool.CurStoreCount, Is.EqualTo(1));
            Assert.That(resets, Is.EqualTo(mode == PoolPrewarmMode.Raw ? 0 : 1));
            Assert.That(discards, Is.Zero);
            Assert.That(pool.Allocate(), Is.SameAs(first));
        }

        [Test]
        public void PrewarmResetFailureKeepsSuccessfulItemsAndIsolatesFailedItem()
        {
            var root = new InvalidOperationException("prewarm reset root");
            var cleanup = new ApplicationException("prewarm discard failure");
            var created = new List<object>();
            var resets = 0;
            var discarded = new List<object>();
            using var pool = new ObjectPool<object>(() =>
            {
                var item = new object();
                created.Add(item);
                return item;
            }, reset: _ =>
            {
                if (++resets == 2)
                {
                    throw root;
                }
            },
                onDiscard: item =>
                {
                    discarded.Add(item);
                    throw cleanup;
                });
            var error = Assert.Throws<AggregateException>(() => pool.Prewarm(3));
            Assert.That(error.InnerExceptions.Count, Is.EqualTo(2));
            Assert.That(error.InnerExceptions[0], Is.SameAs(root));
            Assert.That(error.InnerExceptions[1], Is.SameAs(cleanup));
            Assert.That(created.Count, Is.EqualTo(2));
            Assert.That(resets, Is.EqualTo(2));
            Assert.That(pool.CurStoreCount, Is.EqualTo(1));
            Assert.That(discarded, Is.EqualTo(new[] { created[1] }));
            Assert.That(pool.Recycle(created[1]), Is.False);
            pool.Discard(created[1]);
            Assert.That(discarded.Count, Is.EqualTo(1));
            Assert.That(pool.Allocate(), Is.SameAs(created[0]));
        }

        [TestCase(PoolPrewarmMode.Raw)]
        [TestCase(PoolPrewarmMode.Recycle)]
        public void PrewarmFactoryReturningAnotherLeaseDoesNotCachePastTheTarget(PoolPrewarmMode mode)
        {
            ObjectPool<object> pool = null;
            var factoryCalls = 0;
            var allocated = new List<object>();
            var reset = new List<object>();
            var discarded = new List<object>();
            pool = new ObjectPool<object>(() =>
            {
                factoryCalls++;
                return new object();
            },
                onAllocate: allocated.Add, reset: reset.Add, onDiscard: discarded.Add, maxPoolSize: 5);
            using (pool)
            {
                var otherLease = pool.Allocate();
                var created = new object();
                pool.SetFactoryMethod(() =>
                {
                    factoryCalls++;
                    Assert.That(pool.Recycle(otherLease), Is.True);
                    return created;
                });

                Assert.That(pool.Prewarm(1, mode), Is.Zero);
                Assert.That(factoryCalls, Is.EqualTo(2));
                Assert.That(pool.CurStoreCount, Is.EqualTo(1));
                Assert.That(allocated, Is.EqualTo(new[] { otherLease }));
                Assert.That(reset, Is.EqualTo(mode == PoolPrewarmMode.Raw
                    ? new[] { otherLease } : new[] { otherLease, created }));
                Assert.That(discarded, Is.EqualTo(new[] { created }));
                Assert.That(pool.Recycle(created), Is.False);
                Assert.That(pool.Allocate(), Is.SameAs(otherLease));
            }
        }

        [TestCase(PoolPrewarmMode.Raw)]
        [TestCase(PoolPrewarmMode.Recycle)]
        public void PrewarmCannotCacheAnObjectAfterFactoryDisposesPool(PoolPrewarmMode mode)
        {
            ObjectPool<object> pool = null;
            var item = new object();
            var resets = 0;
            var discarded = new List<object>();
            pool = new ObjectPool<object>(() =>
            {
                pool.Dispose();
                return item;
            },
                reset: _ => resets++, onDiscard: discarded.Add);
            using (pool)
            {
                Assert.Throws<ObjectDisposedException>(() => pool.Prewarm(1, mode));
                Assert.That(resets, Is.Zero);
                Assert.That(discarded, Is.EqualTo(new[] { item }));
                Assert.That(pool.CurStoreCount, Is.Zero);
                Assert.That(pool.Recycle(item), Is.False);
            }
        }

        [Test]
        public void PrewarmResetReturningAnotherLeaseRechecksTargetBeforeCaching()
        {
            ObjectPool<object> pool = null;
            object created = null;
            object otherLease = null;
            var reset = new List<object>();
            var discarded = new List<object>();
            pool = new ObjectPool<object>(() => new object(), reset: item =>
            {
                reset.Add(item);
                if (ReferenceEquals(item, created))
                {
                    Assert.That(pool.Recycle(otherLease), Is.True);
                }
            }, onDiscard: discarded.Add, maxPoolSize: 5);
            using (pool)
            {
                otherLease = pool.Allocate();
                created = new object();
                pool.SetFactoryMethod(() => created);
                Assert.That(pool.Prewarm(1), Is.Zero);
                Assert.That(reset, Is.EqualTo(new[] { created, otherLease }));
                Assert.That(discarded, Is.EqualTo(new[] { created }));
                Assert.That(pool.CurStoreCount, Is.EqualTo(1));
                Assert.That(pool.Recycle(created), Is.False);
                Assert.That(pool.Allocate(), Is.SameAs(otherLease));
            }
        }

        [Test]
        public void FactoryReplacementOnlyChangesColdCreationAndKeepsExistingCleanupResponsibility()
        {
            var first = new object();
            var second = new object();
            var third = new object();
            var replacementCalls = 0;
            var reset = new List<object>();
            var discarded = new List<object>();
            using var pool = new ObjectPool<object>(() => first, reset: reset.Add, onDiscard: discarded.Add);
            Assert.That(pool.Allocate(), Is.SameAs(first));
            Assert.That(pool.Recycle(first), Is.True);
            pool.SetFactoryMethod(() =>
            {
                replacementCalls++;
                return second;
            });
            Assert.That(pool.Allocate(), Is.SameAs(first));
            Assert.That(replacementCalls, Is.Zero);
            Assert.That(pool.Allocate(), Is.SameAs(second));
            Assert.That(replacementCalls, Is.EqualTo(1));
            pool.SetObjectFactory(new CustomObjectFactory<object>(() => third));
            Assert.That(pool.Allocate(), Is.SameAs(third));
            pool.SetObjectFactory(null);
            Assert.Throws<InvalidOperationException>(() => pool.Allocate());
            Assert.That(pool.Recycle(first), Is.True);
            Assert.That(pool.Allocate(), Is.SameAs(first));
            pool.Discard(first);
            pool.Discard(second);
            pool.Discard(third);
            Assert.That(reset, Is.EqualTo(new[] { first, first, first, second, third }));
            Assert.That(discarded, Is.EqualTo(new[] { first, second, third }));
            Assert.That(pool.CurStoreCount, Is.Zero);
        }

        private static ObjectPool<object> RetainingPool(Action<object> reset, Action<object> discard, Func<object, bool> canRetain)
        {
            var constructor = typeof(ObjectPool<object>).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(Func<object>), typeof(Action<object>), typeof(Action<object>), typeof(Action<object>),
                    typeof(int), typeof(PoolCacheOrder), typeof(Func<object, bool>) }, null);
            Assert.That(constructor, Is.Not.Null, "Collections need a narrow internal retention decision, not a public strategy API.");
            return (ObjectPool<object>)constructor.Invoke(new object[] { (Func<object>)(() => new object()), null, reset,
                discard, 20, PoolCacheOrder.Fifo, canRetain });
        }

        [TestCase(PoolPrewarmMode.Raw)]
        [TestCase(PoolPrewarmMode.Recycle)]
        public void RetentionVetoCannotBeBypassedByEitherPrewarmMode(PoolPrewarmMode mode)
        {
            int resets = 0;
            int discarded = 0;
            int checkedItems = 0;
            using var pool = RetainingPool(_ => resets++, _ => discarded++, _ => { checkedItems++; return false; });
            Assert.That(pool.Prewarm(2, mode), Is.Zero);
            Assert.That(checkedItems, Is.EqualTo(2));
            Assert.That(discarded, Is.EqualTo(2));
            Assert.That(resets, Is.EqualTo(mode == PoolPrewarmMode.Raw ? 0 : 2));
            Assert.That(pool.CurStoreCount, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RetentionVetoOrFailureConsumesTheBorrowAfterReset(bool fails)
        {
            int resets = 0;
            int discarded = 0;
            var root = new InvalidOperationException("retention");
            using var pool = RetainingPool(_ => resets++, _ => discarded++, _ =>
            {
                Assert.That(resets, Is.EqualTo(1));
                if (fails)
                {
                    throw root;
                }
                return false;
            });
            var item = pool.Allocate();
            if (fails)
            {
                Assert.That(Assert.Throws<InvalidOperationException>(() => pool.Recycle(item)), Is.SameAs(root));
            }
            else
            {
                Assert.That(pool.Recycle(item), Is.False);
            }
            Assert.That(pool.Recycle(item), Is.False);
            pool.Discard(item);
            Assert.That(resets, Is.EqualTo(1));
            Assert.That(discarded, Is.EqualTo(1));
            Assert.That(pool.CurStoreCount, Is.Zero);
        }

        [TestCase("dispose")]
        [TestCase("capacity")]
        [TestCase("session")]
        public void RetentionReentryRechecksTheActualCacheBoundary(string boundary)
        {
            ObjectPool<object> pool = null;
            object peer = null;
            bool entered = false;
            pool = RetainingPool(null, null, _ =>
            {
                if (!entered)
                {
                    entered = true;
                    if (boundary == "dispose")
                    {
                        pool.Dispose();
                    }
                    else if (boundary == "capacity")
                    {
                        pool.MaxPoolSize = 1;
                        pool.Recycle(peer);
                    }
                    else
                    {
                        pool.GetType().GetMethod("ResetSession", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(pool, null);
                    }
                }
                return true;
            });
            using (pool)
            {
                var first = pool.Allocate();
                peer = pool.Allocate();
                Assert.That(pool.Recycle(first), Is.False);
                Assert.That(pool.CurStoreCount, Is.EqualTo(boundary == "capacity" ? 1 : 0));
                pool.Discard(peer);
            }
        }

        [Test]
        public void TrackingDoesNotStronglyKeepAnUnreachableLeasedObjectAlive()
        {
            using var pool = new ObjectPool<object>(() => new object());
            var weak = AllocateWithoutKeepingLease(pool);
            // 辅助方法已退出，池保持存活；不以任意等待时间推测 GC 生命周期。
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.That(weak.IsAlive, Is.False);
            Assert.That(pool.CurStoreCount, Is.Zero);
            GC.KeepAlive(pool);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference AllocateWithoutKeepingLease(ObjectPool<object> pool)
        {
            return new WeakReference(pool.Allocate());
        }
    }
}
