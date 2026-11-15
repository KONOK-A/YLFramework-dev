using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using YLFramework.Pooling;

namespace YLFramework.Tests
{
    public class PoolLeaseTests
    {
        private sealed class Item
        {
            internal int Value;
        }

        public sealed class DomainItem : IOfPool
        {
            public bool IsRecycled
            {
                get; set;
            }
            public int Allocations;
            public int Resets;
            public void OnAllocate() => Allocations++;
            public void OnRecycle() => Resets++;
        }

        // 先用真实公开方法反射运行，缺API时得到可读RED，不以占位实现解锁编译。
        private static IDisposable Rent<T>(ObjectPool<T> pool, out T item) where T : class => RentFrom(pool, out item);

        private static IDisposable RentFrom<T>(object pool, out T item) where T : class
        {
            var method = pool.GetType().GetMethod("Rent", new[] { typeof(T).MakeByRefType() });
            Assert.That(method, Is.Not.Null, "A generation-protected Rent(out T) entry is required.");
            Assert.That(method.ReturnType.IsValueType && typeof(IDisposable).IsAssignableFrom(method.ReturnType), Is.True);
            Assert.That(method.ReturnType.Name, Is.EqualTo("PoolLease`1"));
            Assert.That(method.ReturnType.GetConstructors(), Is.Empty);
            var arguments = new object[] { null };
            var lease = (IDisposable)Invoke(method, pool, arguments);
            item = (T)arguments[0];
            return lease;
        }

        private static object Invoke(MethodInfo method, object target, object[] arguments)
        {
            try
            {
                return method.Invoke(target, arguments);
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static void Discard(IDisposable lease)
        {
            var method = lease.GetType().GetMethod("Discard", Type.EmptyTypes);
            Assert.That(method, Is.Not.Null, "A lease needs an explicit permanent-discard exit.");
            Invoke(method, lease, null);
        }

        private static IDisposable Copy(IDisposable lease)
        {
            var copy = (IDisposable)RuntimeHelpers.GetObjectValue(lease);
            Assert.That(copy, Is.Not.SameAs(lease), "Exercise two actual boxed struct copies, not one shared box.");
            return copy;
        }

        private static void ResetSession<T>(ObjectPool<T> pool) where T : class
        {
            var method = pool.GetType().GetMethod("ResetSession", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            Invoke(method, pool, null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CopiedLeaseEndsOnceAndDiscardNeverCaches(bool discard)
        {
            int resets = 0;
            int discards = 0;
            using var pool = new ObjectPool<Item>(() => new Item(), reset: value => { value.Value = 0; resets++; }, onDiscard: _ => discards++);
            var lease = Rent(pool, out var item);
            var copy = Copy(lease);
            item.Value = 17;
            if (discard)
            {
                Discard(lease);
            }
            else
            {
                lease.Dispose();
            }
            copy.Dispose();
            Discard(copy);
            lease.Dispose();
            Assert.That(item.Value, Is.Zero);
            Assert.That(resets, Is.EqualTo(1));
            Assert.That(discards, Is.EqualTo(discard ? 1 : 0));
            Assert.That(pool.CurStoreCount, Is.EqualTo(discard ? 0 : 1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StaleDisposeOrDiscardCannotConsumeNextRental(bool rawNext)
        {
            int resets = 0;
            using var pool = new ObjectPool<Item>(() => new Item(), reset: value => { value.Value = 0; resets++; });
            var previous = Rent(pool, out var first);
            var stale = Copy(previous);
            previous.Dispose();
            Item current;
            IDisposable next = null;
            if (rawNext)
            {
                current = pool.Allocate();
            }
            else
            {
                next = Rent(pool, out current);
            }
            Assert.That(current, Is.SameAs(first));
            current.Value = 29;
            stale.Dispose();
            Discard(stale);
            Assert.That(current.Value, Is.EqualTo(29));
            Assert.That(resets, Is.EqualTo(1));
            Assert.That(pool.CurStoreCount, Is.Zero);
            if (rawNext)
            {
                pool.Discard(current);
            }
            else
            {
                Discard(next);
            }
            Assert.That(resets, Is.EqualTo(2));
        }

        [Test]
        public void RawReturnCannotConsumeLeaseRental()
        {
            int resets = 0;
            using var pool = new ObjectPool<Item>(() => new Item(), reset: _ => resets++);
            var lease = Rent(pool, out var item);
            Assert.Throws<InvalidOperationException>(() => pool.Recycle(item));
            Assert.Throws<InvalidOperationException>(() => pool.Discard(item));
            Assert.That(resets, Is.Zero);
            lease.Dispose();
            Assert.That(resets, Is.EqualTo(1));
        }

        [Test]
        public void DefaultAndNullFactoryLeaseAreNoOps()
        {
            using var pool = new ObjectPool<Item>(() => null);
            var empty = Rent(pool, out var item);
            Assert.That(item, Is.Null);
            empty.Dispose();
            Discard(empty);
            var defaultLease = (IDisposable)Activator.CreateInstance(empty.GetType());
            defaultLease.Dispose();
            Discard(defaultLease);
            Assert.That(pool.CurStoreCount, Is.Zero);
        }

        [Test]
        public void ClearKeepsOutstandingLeaseValidAndDisposeConsumesLateLeaseWithoutCaching()
        {
            int resets = 0;
            int discards = 0;
            var pool = new ObjectPool<Item>(() => new Item(), reset: _ => resets++, onDiscard: _ => discards++);
            var lease = Rent(pool, out _);
            pool.Clear();
            lease.Dispose();
            Assert.That(pool.CurStoreCount, Is.EqualTo(1));
            var late = Rent(pool, out _);
            pool.Dispose();
            late.Dispose();
            Discard(late);
            Assert.That(resets, Is.EqualTo(2));
            Assert.That(discards, Is.EqualTo(1));
            Assert.That(pool.CurStoreCount, Is.Zero);
            Assert.Throws<ObjectDisposedException>(() => Rent(pool, out _));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LeaseResetFailureRetiresOnceAndPreservesCleanupFailure(bool cleanupFails)
        {
            int resets = 0;
            int discards = 0;
            var root = new InvalidOperationException("lease reset");
            var cleanup = new ApplicationException("lease cleanup");
            using var pool = new ObjectPool<Item>(() => new Item(),
                reset: _ => { resets++; throw root; },
                onDiscard: _ => { discards++; if (cleanupFails) { throw cleanup; } });
            var lease = Rent(pool, out _);
            var copy = Copy(lease);
            if (cleanupFails)
            {
                var error = Assert.Throws<AggregateException>(() => lease.Dispose());
                Assert.That(error.InnerExceptions, Is.EqualTo(new Exception[] { root, cleanup }));
            }
            else
            {
                Assert.That(Assert.Throws<InvalidOperationException>(() => lease.Dispose()), Is.SameAs(root));
            }
            copy.Dispose();
            Discard(copy);
            Assert.That(resets, Is.EqualTo(1));
            Assert.That(discards, Is.EqualTo(1));
            Assert.That(pool.CurStoreCount, Is.Zero);
        }

        [Test]
        public void DiscardFailureCannotBeReplayed()
        {
            int resets = 0;
            int discards = 0;
            using var pool = new ObjectPool<Item>(() => new Item(), reset: _ => resets++,
                onDiscard: _ => { discards++; throw new InvalidOperationException("discard"); });
            var lease = Rent(pool, out _);
            Assert.Throws<InvalidOperationException>(() => Discard(lease));
            lease.Dispose();
            Discard(Copy(lease));
            Assert.That(resets, Is.EqualTo(1));
            Assert.That(discards, Is.EqualTo(1));
        }

        [Test]
        public void LeaseCannotReenterItsOwnTransitionButCanReturnAnotherRental()
        {
            IDisposable first = null;
            IDisposable peer = null;
            int resets = 0;
            using var pool = new ObjectPool<Item>(() => new Item(), reset: _ =>
            {
                resets++;
                first.Dispose();
                Discard(first);
                peer.Dispose();
            });
            first = Rent(pool, out _);
            peer = Rent(pool, out _);
            first.Dispose();
            Assert.That(resets, Is.EqualTo(2));
            Assert.That(pool.CurStoreCount, Is.EqualTo(2));
        }

        [Test]
        public void SessionBoundaryRetiresOldRawAndLeaseReturnsWithoutClosingPool()
        {
            int resets = 0;
            int discards = 0;
            using var pool = new ObjectPool<Item>(() => new Item(), reset: _ => resets++, onDiscard: _ => discards++);
            var raw = pool.Allocate();
            var lease = Rent(pool, out _);
            ResetSession(pool);
            var current = Rent(pool, out _);
            pool.Recycle(raw);
            lease.Dispose();
            Assert.That(resets, Is.EqualTo(2));
            Assert.That(discards, Is.EqualTo(2));
            Assert.That(pool.CurStoreCount, Is.Zero);
            current.Dispose();
            Assert.That(pool.CurStoreCount, Is.EqualTo(1));
        }

        [Test]
        public void SessionChangeInsideResetCannotRevivePreviousBorrow()
        {
            ObjectPool<Item> pool = null;
            int discards = 0;
            pool = new ObjectPool<Item>(() => new Item(), reset: _ => ResetSession(pool), onDiscard: _ => discards++);
            using (pool)
            {
                var lease = Rent(pool, out _);
                lease.Dispose();
                Assert.That(discards, Is.EqualTo(1));
                Assert.That(pool.CurStoreCount, Is.Zero);
            }
        }

        [Test]
        public void GenerationCannotWrapAndExhaustedObjectIsRetired()
        {
            int discards = 0;
            using var pool = new ObjectPool<Item>(() => new Item(), onDiscard: _ => discards++);
            var lease = Rent(pool, out var item);
            lease.Dispose();
            var entries = pool.GetType().GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pool);
            var args = new object[] { item, null };
            entries.GetType().GetMethod("TryGetValue").Invoke(entries, args);
            var generation = args[1].GetType().GetField("Generation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(generation, Is.Not.Null);
            generation.SetValue(args[1], ulong.MaxValue);
            Assert.Throws<OverflowException>(() => Rent(pool, out _));
            Assert.That(discards, Is.EqualTo(1));
            Assert.That(pool.CurStoreCount, Is.Zero);
            var replacement = Rent(pool, out var next);
            Assert.That(next, Is.Not.SameAs(item));
            lease.Dispose();
            Assert.That(pool.CurStoreCount, Is.Zero);
            replacement.Dispose();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference LoseRental(ObjectPool<Item> pool)
        {
            // 不经反射/NUnit断言路径借用，避免框架保留最后一次调用的参数而污染弱引用验证。
#pragma warning disable YL0011 // 故意丢失租约及对象，验证弱键跟踪不保活；不是业务用法。
            pool.Rent(out var item);
#pragma warning restore YL0011
            return new WeakReference(item);
        }

        [Test]
        public void IdentityTrackingDoesNotKeepLostLeaseAndObjectAlive()
        {
            using var pool = new ObjectPool<Item>(() => new Item());
            var weak = LoseRental(pool);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.That(weak.IsAlive, Is.False);
            GC.KeepAlive(pool);
        }

        [Test]
        public void SafeLeasePreservesDomainCallbacksAndOriginalOwnerAfterDispose()
        {
            var pool = SafeObjectPool<DomainItem>.Instance;
            try
            {
                var lease = RentFrom<DomainItem>(pool, out var item);
                Assert.That(item.Allocations, Is.EqualTo(1));
                Assert.That(item.IsRecycled, Is.False);
                pool.Dispose();
                lease.Dispose();
                Assert.That(item.Resets, Is.EqualTo(1));
                Assert.That(item.IsRecycled, Is.True);
                Assert.That(pool.CurStoreCount, Is.Zero);
                var replacement = SafeObjectPool<DomainItem>.Instance;
                Assert.That(replacement, Is.Not.SameAs(pool));
                pool.Dispose();
                Assert.That(SafeObjectPool<DomainItem>.Instance, Is.SameAs(replacement));
                replacement.Dispose();
            }
            finally { pool.Dispose(); }
        }
    }
}
