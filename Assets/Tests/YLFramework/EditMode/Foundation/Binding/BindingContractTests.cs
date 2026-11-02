using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Binding;
using YLFramework.Pooling;
using YLFramework.Events;

namespace YLFramework.Tests
{
    public class BindingContractTests
    {
        private sealed class PooledValue
        {
        }

        [Test]
        public void PooledPropertyClearsValueComparerAndListenersButRetainsItsOwnedEvent()
        {
            var property = BindableProperty<PooledValue>.Allocate();
            var pool = SafeObjectPool<BindableProperty<PooledValue>>.Instance;
            var events = SafeObjectPool<EasyEvent<PooledValue>>.Instance;
            var owned = (EasyEvent<PooledValue>)typeof(BindableProperty<PooledValue>)
                .GetField("_onValueChanged", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(property);
            try
            {
                var oldCalls = 0;
                Action<PooledValue> listener = _ => oldCalls++;
                var oldToken = property.Register(listener);
                property.SetValueWithoutCallback(new PooledValue());
                property.WithComparer((a, b) => true);
                var eventCount = events.CurStoreCount;
                BindableProperty<PooledValue>.Recycle(property);
                Assert.That(oldCalls, Is.Zero);
                Assert.That(property.Value, Is.Null);
                Assert.That(owned.IsRecycled, Is.False);
                Assert.That(owned.HasListener, Is.False);
                Assert.That(events.CurStoreCount, Is.EqualTo(eventCount));
                var next = BindableProperty<PooledValue>.Allocate();
                Assert.That(next, Is.SameAs(property));
                Assert.That(next.TrySetValue(new PooledValue()), Is.True);
                Assert.That(oldCalls, Is.Zero);
                var newCalls = 0;
                next.Register(listener);
                next.Register(_ => newCalls++);
                oldToken.Unregister();
                oldToken.Unregister();
                next.Value = new PooledValue();
                Assert.That(oldCalls, Is.EqualTo(1));
                Assert.That(newCalls, Is.EqualTo(1));
                var separate = events.Allocate();
                Assert.That(separate, Is.Not.SameAs(owned));
                events.Recycle(separate);
                BindableProperty<PooledValue>.Recycle(next);
                Assert.Throws<InvalidOperationException>(() => BindableProperty<PooledValue>.Recycle(new BindableProperty<PooledValue>()));
            }
            finally
            {
                pool.Dispose();
                events.Dispose();
            }
        }

        private sealed class FirstSetProperty : BindableProperty<string>
        {
            private bool _hasSet;
            protected override bool IsSameValue(string value) => _hasSet && base.IsSameValue(value);
            protected override void SetValue(string value)
            {
                base.SetValue(value);
                _hasSet = true;
            }
        }

        [Test]
        public void OnceConsumedByEarlierListenersNestedNotificationDoesNotRunAgain()
        {
            var property = new BindableProperty<int>();
            var observed = new List<int>();
            property.Register(value =>
            {
                if (value == 1)
                {
                    property.Value = 2;
                }
            });
            var once = property.RegisterOnce(observed.Add);

            property.Value = 1;
            once.Unregister();
            property.Value = 3;

            CollectionAssert.AreEqual(new[] { 2 }, observed);
            Assert.That(property.RegisterOnce(null), Is.SameAs(new EventChannel<int>().RegisterOnce(null)));
        }

        [Test]
        public void RecycleDuringNotificationStopsOldSnapshotButKeepsNewOwnerUsable()
        {
            var property = new BindableProperty<int>();
            var observed = new List<string>();
            property.Register(value =>
            {
                observed.Add("old:" + value);
                property.OnRecycle();
                property.RegisterOnce(next => observed.Add("new:" + next));
                property.ForceNotify();
            });
            var oldToken = property.Register(_ => observed.Add("stale"));

            property.Value = 1;
            oldToken.Unregister();
            property.ForceNotify();

            Assert.That(property.Value, Is.Zero);
            CollectionAssert.AreEqual(new[] { "old:1", "new:0" }, observed);
        }

        [Test]
        public void ListNotificationsKeepDataFirstOperationCountChangedOrderAndBatchReset()
        {
            var list = new BindableList<int>();
            var observed = new List<string>();
            list.OnAdd.Register((index, value) =>
            {
                Assert.That(list[index], Is.EqualTo(value));
                observed.Add("add:" + index + ":" + value);
            });
            list.OnReplace.Register((index, oldValue, newValue) =>
            {
                Assert.That(list[index], Is.EqualTo(newValue));
                observed.Add("replace:" + index + ":" + oldValue + ":" + newValue);
            });
            list.OnClear.Register(() =>
            {
                Assert.That(list, Is.Empty);
                observed.Add("clear");
            });
            list.OnCountChanged.Register(count =>
            {
                Assert.That(list.Count, Is.EqualTo(count));
                observed.Add("count:" + count);
            });
            list.OnChanged.Register(change => observed.Add("changed:" + change.ChangeType));

            list.Add(1);
            list[0] = 2;
            using (list.BeginBatchScope())
            {
                list.Add(3);
                using (list.BeginBatchScope())
                {
                    list.Add(4);
                }

                Assert.That(observed.Count, Is.EqualTo(5));
            }
            list.Clear();
            list.Clear();

            CollectionAssert.AreEqual(new[]
            {
                "add:0:1", "count:1", "changed:Add",
                "replace:0:1:2", "changed:Replace",
                "count:3", "changed:Reset",
                "clear", "count:0", "changed:Clear",
                "clear", "changed:Clear"
            }, observed);
        }

        [Test]
        public void DisposedMappingIgnoresCapturedSourceCallbackButRetainsTargetListeners()
        {
            var source = new BindableProperty<int>(1);
            MappedProperty<int, int> mapped = null;
            source.Register(_ => mapped.Dispose());
            mapped = source.Map(value => value * 2);
            var observed = new List<int>();
            var token = mapped.RegisterWithInitValue(observed.Add);

            source.Value = 2;
            Assert.That(mapped.IsDisposed, Is.True);
            Assert.That(mapped.Value, Is.EqualTo(2));
            mapped.ForceNotify();
            mapped.Dispose();
            token.Unregister();
            source.Value = 3;
            mapped.ForceNotify();

            CollectionAssert.AreEqual(new[] { 2, 2 }, observed);
        }

        [Test]
        public void InitialCallbackPrecedesSubscriptionAndSameValuesDoNotNotify()
        {
            var property = new BindableProperty<string>("first");
            var observed = new List<string>();
            var token = property.RegisterWithInitValue(value =>
            {
                observed.Add(value);
                if (value == "first")
                {
                    property.Value = "second";
                }
            });
            property.Value = "second";
            property.Value = "third";
            property.Value = null;
            property.Value = null;
            CollectionAssert.AreEqual(new[] { "first", "third", null }, observed);
            token.Unregister();
            token.Unregister();
            property.Value = "fourth";
            Assert.That(observed.Count, Is.EqualTo(3));
        }

        [Test]
        public void InstanceComparerDoesNotChangeOtherPropertiesOrStaticComparer()
        {
            var previous = BindableProperty<string>.Comparer;
            var custom = new BindableProperty<string>("audio").WithComparer(
                (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase));
            var ordinary = new BindableProperty<string>("audio");
            Assert.That(custom.TrySetValue("AUDIO"), Is.False);
            Assert.That(ordinary.TrySetValue("AUDIO"), Is.True);
            Assert.That(BindableProperty<string>.Comparer, Is.SameAs(previous));
        }

        [Test]
        public void SilentSetForceNotifyAndDuplicateUnregisterKeepTheirContracts()
        {
            var property = new BindableProperty<int>(1);
            var observed = new List<int>();
            Action<int> listener = observed.Add;
            var first = property.Register(listener);
            var second = property.Register(listener);
            property.SetValueWithoutCallback(2);
            Assert.That(property.Value, Is.EqualTo(2));
            Assert.That(observed, Is.Empty);
            Assert.That(property.TrySetValue(2), Is.False);
            property.ForceNotify();
            first.Unregister();
            property.Value = 3;
            property.UnregisterAll();
            second.Unregister();
            property.Value = 4;
            CollectionAssert.AreEqual(new[] { 2, 2, 3 }, observed);
        }

        [Test]
        public void ComparisonHookCanNotifyFirstEqualValueWithoutChangingDefaultPolicy()
        {
            var property = new FirstSetProperty();
            var calls = 0;
            property.Register(_ => calls++);
            Assert.That(property.TrySetValue(null), Is.True);
            Assert.That(property.TrySetValue(null), Is.False);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(new BindableProperty<string>().TrySetValue(null), Is.False);
        }

        [TestCase(-1, false)]
        [TestCase(0, false)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        public void PlayerPrefsBoolRetainsZeroOneEncoding(int stored, bool expected)
        {
            var key = "YL.Binding.Tests." + Guid.NewGuid().ToString("N");
            try
            {
                var defaults = new PlayerPrefsBoolProperty(key, true);
                Assert.That(defaults.Value, Is.True);
                Assert.That(defaults.HasKey, Is.False);
                PlayerPrefs.SetInt(key, stored);
                var property = new PlayerPrefsBoolProperty(key, true);
                Assert.That(property.Value, Is.EqualTo(expected));
                property.Value = !expected;
                Assert.That(PlayerPrefs.GetInt(key), Is.EqualTo(expected ? 0 : 1));
                Assert.That(new PlayerPrefsBoolProperty(key).Value, Is.EqualTo(!expected));
            }
            finally
            {
                PlayerPrefs.DeleteKey(key);
            }
        }

        [Test]
        public void PlayerPrefsFloatWritesBeforeNotificationAndSilentSetDoesNotPersist()
        {
            var key = "YL.Binding.Tests." + Guid.NewGuid().ToString("N");
            try
            {
                var property = new PlayerPrefsFloatProperty(key, .75f);
                property.WithComparer((a, b) => a.Equals(b));
                Assert.That(property.Value, Is.EqualTo(.75f));
                Assert.That(property.HasKey, Is.False);
                var calls = 0;
                property.Register(value =>
                {
                    calls++;
                    Assert.That(property.Value, Is.EqualTo(value));
                    Assert.That(PlayerPrefs.GetFloat(key), Is.EqualTo(value));
                });
                var changed = .75f + .0000001f;
                property.Value = changed;
                property.Value = changed;
                property.SetValueWithoutCallback(.5f);
                Assert.That(property.Value, Is.EqualTo(.5f));
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(new PlayerPrefsFloatProperty(key).Value, Is.EqualTo(changed));
            }
            finally
            {
                PlayerPrefs.DeleteKey(key);
            }
        }

        [Test]
        public void PlayerPrefsReloadAndDeleteDoNotWriteBackButReentrantChangesDo()
        {
            var key = "YL.Binding.Tests." + Guid.NewGuid().ToString("N");
            try
            {
                var property = new PlayerPrefsIntProperty(key, 3);
                property.Value = 4;
                PlayerPrefs.SetInt(key, 5);
                property.Reload();
                Assert.That(property.Value, Is.EqualTo(5));
                property.Delete();
                Assert.That(property.Value, Is.EqualTo(3));
                Assert.That(property.HasKey, Is.False);
                property.Register(value =>
                {
                    if (value == 6)
                    {
                        property.Value = 7;
                    }
                });
                PlayerPrefs.SetInt(key, 6);
                property.Reload();
                Assert.That(PlayerPrefs.GetInt(key), Is.EqualTo(7));
            }
            finally
            {
                PlayerPrefs.DeleteKey(key);
            }
        }
    }
}
