using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Binding;
using YLFramework.Pooling;
using YLFramework.Events;

namespace YLFramework.Tests
{
    public class EventsContractTests
    {
        private sealed class PooledValue
        {
        }

        [Test]
        public void PooledEventClearsListenersReusesIdentityAndRejectsOrdinaryInstance()
        {
            var e = EasyEvent<PooledValue>.Allocate();
            var pool = SafeObjectPool<EasyEvent<PooledValue>>.Instance;
            try
            {
                Assert.That(pool.MaxPoolSize, Is.EqualTo(50));
                var calls = 0;
                Action<PooledValue> callback = _ => calls++;
                var oldToken = e.Register(callback);
                e.RecycleToCache();
                Assert.That(e.IsRecycled, Is.True);
                Assert.That(e.HasListener, Is.False);
                Assert.That(pool.CurStoreCount, Is.EqualTo(1));
                e.RecycleToCache();
                Assert.That(pool.CurStoreCount, Is.EqualTo(1));
                var next = EasyEvent<PooledValue>.Allocate();
                Assert.That(next, Is.SameAs(e));
                var freshToken = next.Register(callback);
                oldToken.Unregister();
                next.Trigger(new PooledValue());
                Assert.That(calls, Is.EqualTo(1), "Old token must not remove the same callback in a new lease.");
                freshToken.Unregister();
                var ordinary = new EasyEvent<PooledValue>();
                ordinary.Register(_ => calls++);
                Assert.Throws<InvalidOperationException>(ordinary.RecycleToCache);
                Assert.That(ordinary.HasListener, Is.True);
                Assert.That(ordinary.IsRecycled, Is.False);
                next.RecycleToCache();
            }
            finally
            {
                pool.Dispose();
            }
        }

        [Test]
        public void EasyEventsUseSharedChannelsAndExactRegistrationIdentity()
        {
            Assert.That(new EasyEvent(), Is.InstanceOf<EventChannel>());
            Assert.That(new EasyEvent<int>(), Is.InstanceOf<EventChannel<int>>());
            Assert.That(new EasyEvent<int, string>(), Is.InstanceOf<EventChannel<int, string>>());
            Assert.That(new EasyEvent<int, string, bool>(), Is.InstanceOf<EventChannel<int, string, bool>>());
            var e = new EasyEvent();
            var trace = new List<string>();
            Action a = () => trace.Add("A");
            var first = e.Register(a);
            e.Register(() => trace.Add("B"));
            e.Register(a);
            first.Unregister();
            e.Trigger();
            CollectionAssert.AreEqual(new[] { "B", "A" }, trace);
            e.UnregisterAll();
            e.Register(a);
            first.Unregister();
            e.Trigger();
            CollectionAssert.AreEqual(new[] { "B", "A", "A" }, trace);
        }

        [Test]
        public void EasyEventOnceIsConsumedAcrossNestedSnapshotsAndExceptions()
        {
            var e = new EasyEvent<int>();
            var nested = false;
            e.Register(value =>
            {
                if (nested)
                {
                    return;
                }

                nested = true;
                e.Trigger(value + 1);
            });
            var onceValues = new List<int>();
            e.RegisterOnce(onceValues.Add);
            e.Trigger(1);
            CollectionAssert.AreEqual(new[] { 2 }, onceValues);
            var error = new InvalidOperationException("Once failure");
            e.RegisterOnce(_ => throw error);
            Assert.That(Assert.Throws<InvalidOperationException>(() => e.Trigger(3)), Is.SameAs(error));
            Assert.DoesNotThrow(() => e.Trigger(4));
            Assert.That(e.ListenerCount, Is.EqualTo(1));
        }

        private sealed class PooledLifecycleValue
        {
        }

        [Test]
        public void RecycleAndReallocateInsideNotificationStopsOldSnapshotOnly()
        {
            var e = EasyEvent<PooledLifecycleValue>.Allocate();
            var pool = SafeObjectPool<EasyEvent<PooledLifecycleValue>>.Instance;
            var trace = new List<string>();
            EasyEvent<PooledLifecycleValue> next = null;
            try
            {
                e.Register(_ =>
                {
                    trace.Add("end");
                    e.RecycleToCache();
                    next = EasyEvent<PooledLifecycleValue>.Allocate();
                    Assert.That(next, Is.SameAs(e));
                    next.Register(__ => trace.Add("new"));
                });
                var oldToken = e.Register(_ => trace.Add("old"));
                e.Trigger(new PooledLifecycleValue());
                oldToken.Unregister();
                next.Trigger(new PooledLifecycleValue());
                CollectionAssert.AreEqual(new[] { "end", "new" }, trace);
            }
            finally
            {
                if (next != null && !next.IsRecycled)
                {
                    next.RecycleToCache();
                }
                else if (!e.IsRecycled)
                {
                    e.RecycleToCache();
                }

                pool.Dispose();
            }
        }

        [Test]
        public void DispatchUsesSnapshotWhileListenersUnregisterAndRegister()
        {
            var e = new EasyEvent<int>();
            var trace = new List<int>();
            IUnregister first = null, second = null;
            first = e.Register(_ =>
            {
                trace.Add(1);
                first.Unregister();
                second.Unregister();
                e.Register(value => trace.Add(value));
            });
            second = e.Register(_ => trace.Add(2));
            e.Trigger(3);
            e.Trigger(4);
            CollectionAssert.AreEqual(new[] { 1, 2, 4 }, trace);
        }

        [Test]
        public void DuplicateRegistrationsAndExceptionsKeepDelegateSemantics()
        {
            var e = new EasyEvent();
            var calls = 0;
            Action listener = () => calls++;
            var first = e.Register(listener);
            e.Register(listener);
            e.Trigger();
            first.Unregister();
            first.Unregister();
            e.Trigger();
            Assert.That(calls, Is.EqualTo(3));
            e.UnregisterAll();
            e.Register(() => throw new InvalidOperationException());
            e.Register(listener);
            Assert.Throws<InvalidOperationException>(e.Trigger);
            Assert.That(calls, Is.EqualTo(3));
        }

        [Test]
        public void ImmediateCallPrecedesRegistration()
        {
            var e = new EasyEvent();
            var calls = 0;
            e.RegisterWithACall(() =>
            {
                calls++;
                if (calls == 1)
                {
                    e.Trigger();
                }
            });
            Assert.That(calls, Is.EqualTo(1));
            e.Trigger();
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void SingleUseTokenIsConsumedBeforeReentryAndFailure()
        {
            var calls = 0;
            SingleUseUnregister token = null;
            token = new SingleUseUnregister(() =>
            {
                calls++;
                token.Dispose();
                throw new InvalidOperationException();
            });
            Assert.Throws<InvalidOperationException>(token.Unregister);
            Assert.DoesNotThrow(token.Dispose);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void ExistingTokenRetainsCallbackUntilSuccess()
        {
            var calls = 0;
            var token = new CustomUnregister(() =>
            {
                if (++calls == 1)
                {
                    throw new InvalidOperationException();
                }
            });
            Assert.Throws<InvalidOperationException>(token.Unregister);
            token.Unregister();
            token.Unregister();
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void StrictCallbackCopiesKeepIndependentStateAndRepeatFailure()
        {
            var calls = 0;
            var token = new CallbackUnregister(() => calls++, CallbackReleaseMode.StrictAfterSuccess);
            var copy = token;
            token.Unregister();
            Assert.Throws<NullReferenceException>(() => token.Unregister());
            copy.Unregister();
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void DetachSnapshotAllowsReentryAndPreservesNewSubscriptions()
        {
            var items = new SubscriptionSet<IUnregister>();
            var later = new SingleUseUnregister(() => { });
            var token = new SingleUseUnregister(() =>
            {
                Assert.That(items.Count, Is.Zero);
                Assert.That(items.DetachAll(), Is.Empty);
                items.Add(later);
            });
            items.Add(token);
            items.Add(token);
            Assert.That(items.Count, Is.EqualTo(1));
            foreach (var item in items.DetachAll())
            {
                item.Unregister();
            }

            Assert.That(items.Count, Is.EqualTo(1));
            items.Remove(later);
            Assert.That(items.Count, Is.Zero);
        }

        [Test]
        public void LegacyReleaseRetainsCollectionOnFailure()
        {
            var laterCalls = 0;
            var items = new List<IUnregister>
            {
                new CustomUnregister(() => throw new InvalidOperationException()),
                new CustomUnregister(() => laterCalls++)
            };
            Assert.Throws<InvalidOperationException>(() =>
                SubscriptionCleanup.ReleaseAfterSuccess(items, item => item.Unregister()));
            Assert.That(items.Count, Is.EqualTo(2));
            Assert.That(laterCalls, Is.Zero);
        }

        [Test]
        public void ClearListenersInvalidatesOldEventsButClearOnlyDropsMappings()
        {
            var events = new EasyEvents();
            var old = events.GetOrAddEvent<EasyEvent<int>>();
            var calls = 0;
            var token = old.Register(_ => calls++);
            events.ClearListeners();
            old.Trigger(1);
            token.Unregister();
            Assert.That(calls, Is.Zero);
            Assert.That(events.GetEvent<EasyEvent<int>>(), Is.Null);
            var next = events.GetOrAddEvent<EasyEvent<int>>();
            next.Register(_ => calls++);
            events.Clear();
            next.Trigger(2);
            Assert.That(calls, Is.EqualTo(1));
        }

        private sealed class ThrowingCleanupEvent : EasyEvent, IEasyEvent
        {
            internal Action Cleanup;
            void IEasyEvent.UnregisterAll() => Cleanup();
        }

        [Test]
        public void ContainerCleanupAttemptsEveryEventAndPreservesReentrantMappings()
        {
            var events = new EasyEvents();
            var failing = events.GetOrAddEvent<ThrowingCleanupEvent>();
            var ordinary = events.GetOrAddEvent<EasyEvent>();
            var error = new InvalidOperationException("Cleanup failure");
            failing.Cleanup = () =>
            {
                events.AddEvent<EasyEvent<int>>();
                throw error;
            };
            ordinary.Register(() => { });
            Assert.That(Assert.Throws<InvalidOperationException>(events.ClearListeners), Is.SameAs(error));
            Assert.That(ordinary.HasListener, Is.False);
            Assert.That(events.GetEvent<ThrowingCleanupEvent>(), Is.Null);
            Assert.That(events.GetEvent<EasyEvent<int>>(), Is.Not.Null);
            events.ClearListeners();
        }

        private sealed class GlobalResetEvent : EasyEvent
        {
        }

        [Test]
        public void GlobalContainerResetEndsOldOwnerWithoutRecyclingTheEvent()
        {
            EasyEvents.Register<GlobalResetEvent>();
            var old = EasyEvents.Get<GlobalResetEvent>();
            var reset = typeof(EasyEvents).GetMethod("ClearGlobalEvents", BindingFlags.NonPublic | BindingFlags.Static);
            var trace = new List<string>();
            Action callback = () => trace.Add("fresh");
            old.Register(() =>
            {
                trace.Add("reset");
                reset.Invoke(null, null);
            });
            var oldToken = old.Register(callback);
            old.Trigger();
            Assert.That(EasyEvents.Get<GlobalResetEvent>(), Is.Null);
            Assert.That(old.IsRecycled, Is.False);
            Assert.That(old.HasListener, Is.False);
            old.Register(callback);
            oldToken.Unregister();
            old.Trigger();
            CollectionAssert.AreEqual(new[] { "reset", "fresh" }, trace);
            old.UnregisterAll();
        }

        [Test]
        public void GlobalResetHonorsCustomCleanupAndContinuesAfterItsFailure()
        {
            EasyEvents.Register<ThrowingCleanupEvent>();
            EasyEvents.Register<GlobalResetEvent>();
            var custom = EasyEvents.Get<ThrowingCleanupEvent>();
            var ordinary = EasyEvents.Get<GlobalResetEvent>();
            var cleanupCalls = 0;
            custom.Cleanup = () =>
            {
                cleanupCalls++;
                throw new InvalidOperationException("custom-global-cleanup");
            };
            custom.Register(() => { });
            ordinary.Register(() => { });
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: custom-global-cleanup"));
            typeof(EasyEvents).GetMethod("ClearGlobalEvents", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            Assert.That(cleanupCalls, Is.EqualTo(1));
            Assert.That(custom.HasListener, Is.False);
            Assert.That(ordinary.HasListener, Is.False);
            Assert.That(EasyEvents.Get<ThrowingCleanupEvent>(), Is.Null);
            Assert.That(EasyEvents.Get<GlobalResetEvent>(), Is.Null);
        }

        [Test]
        public void DefaultOrKeepsOtherListenersUntilLastUnregister()
        {
            var source = new EasyEvent();
            var combined = new OrEvent().Or(source);
            var calls = 0;
            var first = combined.Register(() => calls++);
            var second = combined.Register(() => calls++);
            first.Unregister();
            source.Trigger();
            Assert.That(calls, Is.EqualTo(1));
            second.Unregister();
            Assert.That(source.HasListener, Is.False);
            combined.Dispose();
        }

        [Test]
        public void GenericEventNoArgumentInterfaceDropsTheValue()
        {
            var value = new EasyEvent<int>();
            var observed = new List<int>();
            var notifications = 0;
            var typed = value.Register(observed.Add);
            var untyped = ((IEasyEvent)value).Register(() => notifications++);
            value.Trigger(7);
            CollectionAssert.AreEqual(new[] { 7 }, observed);
            Assert.That(notifications, Is.EqualTo(1));
            typed.Unregister();
            untyped.Unregister();
            value.Trigger(9);
            Assert.That(observed.Count, Is.EqualTo(1));
            Assert.That(notifications, Is.EqualTo(1));
        }

        [Test]
        public void TypeErasedNullRegistrationIsIgnoredWithoutCreatingListener()
        {
            var one = new EasyEvent<int>();
            var two = new EasyEvent<int, string>();
            var three = new EasyEvent<int, string, bool>();

            var first = ((IEasyEvent)one).Register(null);
            Assert.That(first, Is.Not.Null);
            Assert.That(one.HasListener, Is.False);
            Assert.That(one.ListenerCount, Is.Zero);
            Assert.DoesNotThrow(() => one.Trigger(1));

            var second = ((IEasyEvent)two).Register(null);
            Assert.That(second, Is.Not.Null);
            Assert.That(two.HasListener, Is.False);
            Assert.DoesNotThrow(() => two.Trigger(1, "a"));

            var third = ((IEasyEvent)three).Register(null);
            Assert.That(third, Is.Not.Null);
            Assert.That(three.HasListener, Is.False);
            Assert.DoesNotThrow(() => three.Trigger(1, "a", true));

            first.Unregister();
            second.Unregister();
            third.Unregister();

            var calls = 0;
            three.Register((_, __, ___) => calls++);
            three.Trigger(1, "a", true);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void TypeErasedNullPropertyRegistrationIsIgnored()
        {
            var property = new BindableProperty<int>(0);
            var token = ((IEasyEvent)property).Register(null);
            Assert.That(token, Is.Not.Null);
            Assert.DoesNotThrow(() => property.Value = 1);
            token.Unregister();

            var notifications = 0;
            property.Register(value => notifications++);
            property.Value = 2;
            Assert.That(notifications, Is.EqualTo(1));
            property.UnregisterAll();
        }

        [Test]
        public void OrUnregisterInsideCallbackKeepsRemainingListenersAndSources()
        {
            var source = new EasyEvent();
            var combined = source.Or(new EasyEvent());
            var trace = new List<int>();
            IUnregister first = null;
            first = combined.Register(() =>
            {
                trace.Add(1);
                first.Unregister();
            });
            var second = combined.Register(() => trace.Add(2));
            source.Trigger();
            source.Trigger();
            CollectionAssert.AreEqual(new[] { 1, 2, 2 }, trace);
            second.Unregister();
            Assert.That(source.HasListener, Is.False);
            Assert.DoesNotThrow(() => combined.Dispose());
        }

        private sealed class SubscriptionList : IUnregisterList
        {
            public List<IUnregister> UnregisterList { get; } = new List<IUnregister>();
        }

        [Test]
        public void RegisterListCollectsAndClearsEveryOwnedSubscription()
        {
            var first = new EasyEvent();
            var second = new EasyEvent();
            var calls = 0;
            var list = new SubscriptionList();
            first.Register(() => calls++).AddToUnregisterList(list);
            second.Register(() => calls++).AddToUnregisterList(list);
            Assert.That(list.UnregisterList.Count, Is.EqualTo(2));
            first.Trigger();
            second.Trigger();
            Assert.That(calls, Is.EqualTo(2));
            list.UnregisterAll();
            Assert.That(list.UnregisterList, Is.Empty);
            list.UnregisterAll();
            first.Trigger();
            second.Trigger();
            Assert.That(calls, Is.EqualTo(2));
        }
    }
}
