using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YLFramework.Pooling;
using YLFramework.Singletons;
using YLFramework.Tables;

namespace YLFramework.Tests
{
    public class CollectionPoolTests
    {
        private sealed class Item
        {
            internal string Key;
            internal ThrowingKey LookupKey;
        }

        private sealed class ThrowingKey
        {
            internal int HashCalls;
            internal int FailOnCall;
            internal readonly InvalidOperationException Error = new("hash failed");

            public override int GetHashCode()
            {
                if (++HashCalls == FailOnCall)
                {
                    throw Error;
                }

                return 1;
            }
        }

        [SetUp]
        public void SetUp()
        {
            ClearPools();
            ListPool<Item>.MaxPoolSize = 20;
            DictionaryPool<string, Item>.MaxPoolSize = 20;
            DictionaryPool<string, List<Item>>.MaxPoolSize = 20;
            DictionaryPool<ThrowingKey, List<Item>>.MaxPoolSize = 20;
        }

        [TearDown]
        public void TearDown()
        {
            ClearPools();
            ListPool<Item>.MaxPoolSize = 20;
            DictionaryPool<string, Item>.MaxPoolSize = 20;
            DictionaryPool<string, List<Item>>.MaxPoolSize = 20;
            DictionaryPool<ThrowingKey, List<Item>>.MaxPoolSize = 20;
        }

        private static void ClearPools()
        {
            ListPool<Item>.Clear();
            DictionaryPool<string, Item>.Clear();
            DictionaryPool<string, List<Item>>.Clear();
            DictionaryPool<ThrowingKey, List<Item>>.Clear();
        }

        [Test]
        public void ListsClearElementsPreserveCapacityAndReuseLifo()
        {
            var first = ListPool<Item>.Allocate(16);
            var second = ListPool<Item>.Allocate(32);
            Assert.That(first.Capacity, Is.GreaterThanOrEqualTo(16));
            first.Add(new Item());
            second.Add(new Item());
            first.RecycleToPool();
            second.RecycleToPool();
            Assert.That(first, Is.Empty);
            Assert.That(second, Is.Empty);
            Assert.That(second.Capacity, Is.GreaterThanOrEqualTo(32));

            var expanded = ListPool<Item>.Allocate(64);
            var reused = ListPool<Item>.Allocate();
            Assert.That(expanded, Is.SameAs(second));
            Assert.That(expanded.Capacity, Is.GreaterThanOrEqualTo(64));
            Assert.That(reused, Is.SameAs(first));
            expanded.RecycleToPool();
            reused.RecycleToPool();
        }

        [Test]
        public void InvalidCapacityDoesNotConsumeACachedCollection()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ListPool<Item>.Allocate(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => DictionaryPool<string, Item>.Allocate(-1));
            var list = ListPool<Item>.Allocate();
            var dictionary = DictionaryPool<string, Item>.Allocate();
            list.RecycleToPool();
            dictionary.RecycleToPool();
            Assert.Throws<ArgumentOutOfRangeException>(() => ListPool<Item>.Allocate(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => DictionaryPool<string, Item>.Allocate(-1));
            Assert.That(ListPool<Item>.Allocate(), Is.SameAs(list));
            Assert.That(DictionaryPool<string, Item>.Allocate(), Is.SameAs(dictionary));
            list.RecycleToPool();
            dictionary.RecycleToPool();
        }

        [Test]
        public void DictionariesOnlyApplyCapacityOnColdCreationAndReuseLifo()
        {
            var first = DictionaryPool<string, Item>.Allocate(64);
            var second = DictionaryPool<string, Item>.Allocate(128);
            var capacity = second.EnsureCapacity(0);
            Assert.That(capacity, Is.GreaterThanOrEqualTo(128));
            first.Add("first", new Item());
            second.Add("second", new Item());
            first.RecycleToPool();
            second.RecycleToPool();
            Assert.That(first, Is.Empty);
            Assert.That(second, Is.Empty);
            var hot = DictionaryPool<string, Item>.Allocate(256);
            Assert.That(hot, Is.SameAs(second));
            Assert.That(hot.EnsureCapacity(0), Is.EqualTo(capacity));
            Assert.That(DictionaryPool<string, Item>.Allocate(), Is.SameAs(first));
            hot.RecycleToPool();
            first.RecycleToPool();
        }

        [Test]
        public void NullAndDuplicateReturnsAreNoOpsAndDoNotPublishTwoLoans()
        {
            // 保留 void 入口；旧重复归还异常改为内核拒绝且不重复缓存。
            Assert.DoesNotThrow(() => ListPool<Item>.Recycle(null));
            Assert.DoesNotThrow(() => DictionaryPool<string, Item>.Recycle(null));
            var list = ListPool<Item>.Allocate();
            var dictionary = DictionaryPool<string, Item>.Allocate();
            list.RecycleToPool();
            dictionary.RecycleToPool();
            Assert.DoesNotThrow(() => list.RecycleToPool());
            Assert.DoesNotThrow(() => dictionary.RecycleToPool());
            var firstList = ListPool<Item>.Allocate();
            var secondList = ListPool<Item>.Allocate();
            var firstDictionary = DictionaryPool<string, Item>.Allocate();
            var secondDictionary = DictionaryPool<string, Item>.Allocate();
            Assert.That(firstList, Is.SameAs(list));
            Assert.That(secondList, Is.Not.SameAs(list));
            Assert.That(firstDictionary, Is.SameAs(dictionary));
            Assert.That(secondDictionary, Is.Not.SameAs(dictionary));
            firstList.RecycleToPool();
            secondList.RecycleToPool();
            firstDictionary.RecycleToPool();
            secondDictionary.RecycleToPool();
        }

        [Test]
        public void ForeignCollectionsAreRejectedWithoutClearingOrComparerPollution()
        {
            var externalList = new List<Item> { new() };
            var externalDictionary = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase)
            {
                ["key"] = new Item()
            };
            Assert.Throws<InvalidOperationException>(() => externalList.RecycleToPool());
            Assert.Throws<InvalidOperationException>(() => externalDictionary.RecycleToPool());
            Assert.That(externalList.Count, Is.EqualTo(1));
            Assert.That(externalDictionary.Count, Is.EqualTo(1));
            var dictionary = DictionaryPool<string, Item>.Allocate();
            dictionary.Add("key", new Item());
            dictionary.Add("KEY", new Item());
            Assert.That(dictionary.Comparer, Is.SameAs(EqualityComparer<string>.Default));
            Assert.That(dictionary.Count, Is.EqualTo(2));
            dictionary.RecycleToPool();
        }

        [Test]
        public void DefaultCacheLimitIsTwentyAndOverflowStillClearsElements()
        {
            Assert.That(ListPool<Item>.MaxPoolSize, Is.EqualTo(20));
            Assert.That(DictionaryPool<string, Item>.MaxPoolSize, Is.EqualTo(20));
            var lists = new List<Item>[21];
            var dictionaries = new Dictionary<string, Item>[21];
            for (var i = 0; i < 21; i++)
            {
                lists[i] = ListPool<Item>.Allocate();
                dictionaries[i] = DictionaryPool<string, Item>.Allocate();
                lists[i].Add(new Item());
                dictionaries[i].Add("item", new Item());
            }
            for (var i = 0; i < 21; i++)
            {
                lists[i].RecycleToPool();
                dictionaries[i].RecycleToPool();
                Assert.That(lists[i], Is.Empty);
                Assert.That(dictionaries[i], Is.Empty);
            }
            for (var i = 0; i < 20; i++)
            {
                Assert.That(ListPool<Item>.Allocate(), Is.SameAs(lists[19 - i]));
                Assert.That(DictionaryPool<string, Item>.Allocate(), Is.SameAs(dictionaries[19 - i]));
            }
            var coldList = ListPool<Item>.Allocate();
            var coldDictionary = DictionaryPool<string, Item>.Allocate();
            Assert.That(coldList, Is.Not.SameAs(lists[20]));
            Assert.That(coldDictionary, Is.Not.SameAs(dictionaries[20]));
            for (var i = 0; i < 20; i++)
            {
                lists[i].RecycleToPool();
                dictionaries[i].RecycleToPool();
            }
            coldList.RecycleToPool();
            coldDictionary.RecycleToPool();
        }

        [Test]
        public void ShrinkAndClearRetireIdleCollectionsButLeaveBorrowedCollectionsUsable()
        {
            var first = ListPool<Item>.Allocate();
            var second = ListPool<Item>.Allocate();
            var borrowed = ListPool<Item>.Allocate();
            first.RecycleToPool();
            second.RecycleToPool();
            ListPool<Item>.MaxPoolSize = 1;
            Assert.That(ListPool<Item>.Allocate(), Is.SameAs(first));
            ListPool<Item>.Clear();
            first.RecycleToPool();
            ListPool<Item>.Clear();
            var cold = ListPool<Item>.Allocate();
            Assert.That(cold, Is.Not.SameAs(first));
            borrowed.Add(new Item());
            borrowed.RecycleToPool();
            Assert.That(borrowed, Is.Empty);
            Assert.That(ListPool<Item>.Allocate(), Is.SameAs(borrowed));
            borrowed.RecycleToPool();
            cold.RecycleToPool();

            var dictionary = DictionaryPool<string, Item>.Allocate();
            dictionary.RecycleToPool();
            DictionaryPool<string, Item>.MaxPoolSize = 1;
            DictionaryPool<string, Item>.Clear();
            var replacement = DictionaryPool<string, Item>.Allocate();
            Assert.That(replacement, Is.Not.SameAs(dictionary));
            replacement.RecycleToPool();
        }

        [Test]
        public void SessionResetClearsCollectionCachesAndExistingRuntimeSingletonState()
        {
            var singleton = PlainSingletonProbe.Instance;
            var list = ListPool<Item>.Allocate();
            var dictionary = DictionaryPool<string, Item>.Allocate();
            list.RecycleToPool();
            dictionary.RecycleToPool();
            try
            {
                var state = typeof(ListPool<Item>).Assembly.GetType("YLFramework.Singletons.SingletonSessionState", true);
                var reset = state.GetMethod("ResetForNewSession", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(reset, Is.Not.Null);
                reset.Invoke(null, null);
                Assert.That(PlainSingletonProbe.HasInstance, Is.False);
                Assert.That(PlainSingletonProbe.Instance, Is.Not.SameAs(singleton));
                var newList = ListPool<Item>.Allocate();
                var newDictionary = DictionaryPool<string, Item>.Allocate();
                Assert.That(newList, Is.Not.SameAs(list));
                Assert.That(newDictionary, Is.Not.SameAs(dictionary));
                newList.RecycleToPool();
                newDictionary.RecycleToPool();
            }
            finally
            {
                SingletonProperty<PlainSingletonProbe>.Dispose();
            }
        }

        [Test]
        public void TableConstructorValidatesSelectorBeforeBorrowingDictionary()
        {
            var cached = DictionaryPool<string, List<Item>>.Allocate();
            cached.RecycleToPool();
            Assert.Throws<ArgumentNullException>(() => new TableIndex<string, Item>(null));
            var next = DictionaryPool<string, List<Item>>.Allocate();
            Assert.That(next, Is.SameAs(cached));
            next.RecycleToPool();
        }

        [Test]
        public void TableClearAndDisposeReturnOwnedCollectionsOnlyOnce()
        {
            using var table = new TableIndex<string, Item>(item => item.Key);
            var first = new Item { Key = "a" };
            table.Add(first);
            var dictionary = (Dictionary<string, List<Item>>)table.Dictionary;
            var list = dictionary["a"];
            table.Clear();
            table.Clear();
            Assert.That(table.Dictionary, Is.SameAs(dictionary));
            var borrowed = ListPool<Item>.Allocate();
            Assert.That(borrowed, Is.SameAs(list));
            Assert.That(borrowed, Is.Empty);
            borrowed.RecycleToPool();
            table.Add(first);
            table.Dispose();
            table.Dispose();
            Assert.Throws<ObjectDisposedException>(() => table.Add(first));
            var reusedDictionary = DictionaryPool<string, List<Item>>.Allocate();
            Assert.That(reusedDictionary, Is.SameAs(dictionary));
            Assert.That(reusedDictionary, Is.Empty);
            reusedDictionary.RecycleToPool();
        }

        [Test]
        public void TableRemoveReturnsEmptyBucketAndKeepsOtherKeys()
        {
            using var table = new TableIndex<string, Item>(item => item.Key);
            var first = new Item { Key = "a" };
            var second = new Item { Key = "b" };
            table.Add(first);
            table.Add(second);
            var bucket = table.Dictionary["a"];
            Assert.That(table.Remove(first), Is.True);
            Assert.That(table.Dictionary.ContainsKey("a"), Is.False);
            Assert.That(table.Get("b"), Is.EquivalentTo(new[] { second }));
            var reused = ListPool<Item>.Allocate();
            Assert.That(reused, Is.SameAs(bucket));
            Assert.That(reused, Is.Empty);
            reused.RecycleToPool();
        }

        [Test]
        public void TableInsertionFailureReturnsNewBucketAndPreservesOriginalError()
        {
            using var table = new TableIndex<ThrowingKey, Item>(item => item.LookupKey);
            table.Add(new Item { LookupKey = new ThrowingKey() });
            var cached = ListPool<Item>.Allocate();
            cached.RecycleToPool();
            // 已有桶确保 TryGetValue 计算一次 hash，Add 的第二次 hash 在租用列表后失败。
            var key = new ThrowingKey { FailOnCall = 2 };
            Assert.That(Assert.Throws<InvalidOperationException>(() => table.Add(new Item { LookupKey = key })),
                Is.SameAs(key.Error));
            Assert.That(table.Dictionary.Count, Is.EqualTo(1));
            var returned = ListPool<Item>.Allocate();
            Assert.That(returned, Is.SameAs(cached));
            Assert.That(returned, Is.Empty);
            returned.RecycleToPool();
        }
    }
}
