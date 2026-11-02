using System;
using System.Collections.Generic;
using NUnit.Framework;
using YLFramework.Events;

namespace YLFramework.Foundation.Tests
{
    public class EventChannelContractTests
    {
        [Test]
        public void ExactRegistrationIdentityPreservesOrderAndOldTokenCannotCancelReplacement()
        {
            var channel = new EventChannel();
            var trace = new List<string>();
            Action a = () => trace.Add("A");
            var first = channel.Register(a);
            channel.Register(() => trace.Add("B"));
            channel.Register(a);
            first.Unregister();
            first.Unregister();
            channel.Trigger();
            CollectionAssert.AreEqual(new[] { "B", "A" }, trace);
            var old = channel.Register(a);
            channel.ClearListeners();
            channel.Register(a);
            old.Unregister();
            channel.Trigger();
            Assert.That(trace.Count, Is.EqualTo(3));
            Assert.That(channel.ListenerCount, Is.EqualTo(1));
        }

        // 复用既有 EventsContract 的快照断言，直接验证中立机制而非依赖旧适配。
        [Test]
        public void DispatchUsesSnapshotWhileListenersUnregisterAndRegister()
        {
            var channel = new EventChannel<int>();
            var trace = new List<int>();
            IUnregister first = null, second = null;
            first = channel.Register(_ =>
            {
                trace.Add(1);
                first.Unregister();
                second.Unregister();
                channel.Register(trace.Add);
            });
            second = channel.Register(_ => trace.Add(2));
            channel.Trigger(3);
            channel.Trigger(4);
            CollectionAssert.AreEqual(new[] { 1, 2, 4 }, trace);
        }

        [Test]
        public void OrdinaryClearKeepsOuterSnapshotButNestedPublishSeesNewListeners()
        {
            var channel = new EventChannel();
            var trace = new List<int>();
            channel.Register(() =>
            {
                trace.Add(1);
                channel.ClearListeners();
                channel.Register(() => trace.Add(3));
                channel.Trigger();
            });
            channel.Register(() => trace.Add(2));
            channel.Trigger();
            CollectionAssert.AreEqual(new[] { 1, 3, 2 }, trace);
        }

        [Test]
        public void OwnerResetStopsOldSnapshotWhileSameInstanceCanPublishNewLife()
        {
            var channel = new EventChannel();
            var trace = new List<int>();
            Action last = () => trace.Add(2);
            channel.Register(() =>
            {
                trace.Add(1);
                channel.ResetOwner();
                channel.Register(last);
                channel.Trigger();
            });
            var old = channel.Register(last);
            channel.Trigger();
            old.Unregister();
            channel.Trigger();
            CollectionAssert.AreEqual(new[] { 1, 2, 2 }, trace);
            Assert.That(channel.IsDisposed, Is.False);
        }

        [Test]
        public void DisposeIsPermanentAndStopsInFlightSnapshot()
        {
            var channel = new EventChannel();
            var laterCalls = 0;
            var old = channel.Register(channel.Dispose);
            channel.Register(() => laterCalls++);
            channel.Trigger();
            Assert.That(laterCalls, Is.Zero);
            Assert.That(channel.ListenerCount, Is.Zero);
            Assert.DoesNotThrow(old.Unregister);
            Assert.DoesNotThrow(channel.Dispose);
            Assert.Throws<ObjectDisposedException>(() => channel.Register(null));
            Assert.Throws<ObjectDisposedException>(channel.Trigger);
            Assert.Throws<ObjectDisposedException>(channel.ResetOwner);
        }

        [Test]
        public void OnceIsConsumedInNestedPublishBeforeOuterSnapshotReachesIt()
        {
            var channel = new EventChannel();
            var nested = false;
            var onceCalls = 0;
            channel.Register(() =>
            {
                if (nested)
                {
                    return;
                }

                nested = true;
                channel.Trigger();
            });
            channel.RegisterOnce(() =>
            {
                onceCalls++;
                channel.Trigger();
            });
            channel.Trigger();
            channel.Trigger();
            Assert.That(onceCalls, Is.EqualTo(1));
            Assert.That(channel.ListenerCount, Is.EqualTo(1));
        }

        [Test]
        public void OnceFailureConsumesButEarlierFailureDoesNotConsumeUnreachedOnce()
        {
            var channel = new EventChannel();
            var error = new InvalidOperationException("ordinary");
            var first = channel.Register(() => throw error);
            var calls = 0;
            channel.RegisterOnce(() =>
            {
                calls++;
                throw error;
            });
            Assert.That(Assert.Throws<InvalidOperationException>(channel.Trigger), Is.SameAs(error));
            Assert.That(calls, Is.Zero);
            first.Unregister();
            Assert.That(Assert.Throws<InvalidOperationException>(channel.Trigger), Is.SameAs(error));
            Assert.DoesNotThrow(channel.Trigger);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void MulticastGroupsAndLegacyLastSubsequenceHaveIndependentIdentity()
        {
            var channel = new EventChannel();
            var trace = new List<int>();
            Action a = () => trace.Add(1), b = () => trace.Add(2);
            var first = channel.Register(a + b);
            var second = channel.Register(a + b);
            Assert.That(channel.ListenerCount, Is.EqualTo(4));
            channel.Unregister(a);
            second.Unregister();
            channel.Trigger();
            CollectionAssert.AreEqual(new[] { 1, 2 }, trace);
            channel.Register(a);
            channel.Register(b);
            channel.Unregister(a + b);
            first.Unregister();
            Assert.That(channel.ListenerCount, Is.Zero);
            var once = channel.RegisterOnce(a + b);
            Assert.That(channel.ListenerCount, Is.EqualTo(1));
            channel.Trigger();
            channel.Trigger();
            once.Unregister();
            CollectionAssert.AreEqual(new[] { 1, 2, 1, 2 }, trace);
        }

        [Test]
        public void ImmediateCallPrecedesRegistrationAndFailureLeavesNoListener()
        {
            var channel = new EventChannel();
            var calls = 0;
            channel.RegisterWithACall(() =>
            {
                calls++;
                if (calls == 1)
                {
                    channel.Trigger();
                }
            });
            Assert.That(calls, Is.EqualTo(1));
            channel.Trigger();
            Assert.That(calls, Is.EqualTo(2));
            Assert.Throws<InvalidOperationException>(() => channel.RegisterWithACall(() => throw new InvalidOperationException()));
            Assert.That(channel.ListenerCount, Is.EqualTo(1));
        }

        [Test]
        public void NoArgumentObservationUsesSameTypedOrderAndNullDoesNotRegister()
        {
            var channel = new EventChannel<int>();
            var trace = new List<int>();
            channel.Register(trace.Add);
            ((IEventSource)channel).Register(() => trace.Add(2));
            channel.Register(value => trace.Add(value + 2));
            ((IEventSource)channel).Register(null).Unregister();
            channel.Register(null).Unregister();
            channel.RegisterOnce(null).Unregister();
            channel.Trigger(1);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, trace);
            Assert.That(channel.ListenerCount, Is.EqualTo(3));
        }

        [Test]
        public void ThreeAndSevenParametersInvokeStrongTypesWithoutPayloadConversion()
        {
            var three = new EventChannel<int, string, object>();
            var seven = new EventChannel<int, string, object, bool, long, decimal, char>();
            var payload = new object();
            var calls = 0;
            three.Register((i, s, o) =>
            {
                Assert.That(i, Is.EqualTo(1));
                Assert.That(s, Is.EqualTo("three"));
                Assert.That(o, Is.SameAs(payload));
                calls++;
            });
            seven.Register((i, s, o, b, l, d, c) =>
            {
                Assert.That(i, Is.EqualTo(7));
                Assert.That(s, Is.EqualTo("seven"));
                Assert.That(o, Is.SameAs(payload));
                Assert.That(b, Is.True);
                Assert.That(l, Is.EqualTo(long.MaxValue));
                Assert.That(d, Is.EqualTo(1.5m));
                Assert.That(c, Is.EqualTo('x'));
                calls++;
            });
            three.Trigger(1, "three", payload);
            seven.Trigger(7, "seven", payload, true, long.MaxValue, 1.5m, 'x');
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void FailFastAndExplicitContinueHaveDifferentExceptionContracts()
        {
            var channel = new EventChannel();
            var first = new InvalidOperationException("first");
            var second = new ArgumentException("second");
            var trace = new List<int>();
            channel.Register(() =>
            {
                trace.Add(1);
                throw first;
            });
            channel.Register(() => trace.Add(2));
            channel.RegisterOnce(() =>
            {
                trace.Add(3);
                throw second;
            });
            Assert.That(Assert.Throws<InvalidOperationException>(channel.Trigger), Is.SameAs(first));
            CollectionAssert.AreEqual(new[] { 1 }, trace);
            var errors = Assert.Throws<AggregateException>(channel.TriggerAll);
            CollectionAssert.AreEqual(new Exception[] { first, second }, errors.InnerExceptions);
            CollectionAssert.AreEqual(new[] { 1, 1, 2, 3 }, trace);
            Assert.That(Assert.Throws<InvalidOperationException>(channel.TriggerAll), Is.SameAs(first));
            channel.ClearListeners();
            channel.Register(() => trace.Add(4));
            Assert.DoesNotThrow(channel.Trigger);
            Assert.That(trace[trace.Count - 1], Is.EqualTo(4));
        }
    }
}
