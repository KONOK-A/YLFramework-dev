using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YLFramework.Events;

namespace YLFramework.Foundation.Tests
{
    public class EventBusContractTests
    {
        private enum WideKey : ulong
        {
            Low = 1,
            High = 0x8000000000000001,
            Max = ulong.MaxValue
        }
        private enum OtherKey : ulong
        {
            Low = 1
        }
        private enum SignedKey : long
        {
            Negative = long.MinValue
        }
        private class Message
        {
        }
        private class DerivedMessage : Message
        {
        }

        [Test]
        public void KeysPreserveOrdinalStringsFullEnumsAndIndependentDomains()
        {
            EventKey text = "1";
            EventKey number = 1;
            EventKey firstEnum = WideKey.Low;
            EventKey secondEnum = OtherKey.Low;
            EventKey type = typeof(Message);
            Assert.That(text, Is.EqualTo(new EventKey(new string(new[] { '1' }))));
            Assert.That(new EventKey("KEY"), Is.Not.EqualTo(new EventKey("key")));
            Assert.That(new HashSet<EventKey> { text, number, firstEnum, secondEnum, type }.Count, Is.EqualTo(5));
            Assert.That(new EventKey(WideKey.High), Is.Not.EqualTo(new EventKey(WideKey.Low)));
            Assert.That(new EventKey(WideKey.Max), Is.EqualTo(new EventKey(WideKey.Max)));
            Assert.That(new EventKey(SignedKey.Negative), Is.EqualTo(new EventKey(SignedKey.Negative)));
            Assert.That(new EventKey(SignedKey.Negative).ToString(), Does.Contain(long.MinValue.ToString()));
            Assert.That(EventKey.ForType<Message>(), Is.EqualTo(type));

            using var bus = new EventBus();
            var calls = new List<int>();
            var keys = new[] { text, number, firstEnum, secondEnum, type };
            for (var i = 0; i < keys.Length; i++)
            {
                var index = i;
                bus.Register(keys[i], () => calls.Add(index));
            }
            bus.Send("1");
            bus.Send(1);
            bus.Send(WideKey.Low);
            bus.Send(OtherKey.Low);
            bus.Send(typeof(Message));
            Assert.That(calls, Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
        }

        [Test]
        public void InvalidKeysAreRejectedAndMissingOperationsDoNotBindSignatures()
        {
            Assert.Throws<ArgumentNullException>(() => new EventKey((string)null));
            Assert.Throws<ArgumentNullException>(() => new EventKey((Enum)null));
            Assert.Throws<ArgumentNullException>(() => new EventKey((Type)null));
            using var bus = new EventBus();
            Assert.Throws<ArgumentException>(() => bus.Send(default(EventKey)));
            bus.Send((EventKey)"missing");
            bus.Send("missing", 1);
            bus.SendAll("missing", 1, "value", true);
            bus.Unregister("missing", (Action<int>)null);
            bus.ClearListeners("missing");
            Assert.That(bus.RemoveKey("missing"), Is.False);
            var empty = bus.Register("missing", (Action)null);
            var emptyOnce = bus.RegisterOnce<int>("missing", null);
            empty.Unregister();
            empty.Unregister();
            emptyOnce.Unregister();
            bus.SendMessage<Message>(null);
            bus.SendMessageAll<Message>(null);
            bus.RegisterMessage<Message>(null).Unregister();
            Assert.That(bus.KeyCount, Is.Zero);
            bus.Register<string>("missing", _ => { });
            Assert.That(bus.KeyCount, Is.EqualTo(1));
        }

        [Test]
        public void ExistingSignatureIsValidatedForNullCallbacksAndRetainedUntilRemoval()
        {
            using var bus = new EventBus();
            var calls = 0;
            var token = bus.Register<int>("bound", _ => calls++);
            var error = Assert.Throws<InvalidOperationException>(() => bus.Register<string>("bound", null));
            Assert.That(error.Message, Does.Contain("bound"));
            Assert.That(error.Message, Does.Contain(typeof(EventChannel<int>).ToString()));
            Assert.That(error.Message, Does.Contain(typeof(EventChannel<string>).ToString()));
            Assert.Throws<InvalidOperationException>(() => bus.RegisterOnce<string>("bound", null));
            Assert.Throws<InvalidOperationException>(() => bus.Unregister<string>("bound", null));
            Assert.Throws<InvalidOperationException>(() => bus.Send("bound", "wrong"));
            bus.Send("bound", 1);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(bus.ListenerCount("bound"), Is.EqualTo(1));
            token.Unregister();
            Assert.That(bus.HasKey("bound"), Is.True);
            Assert.That(bus.HasListener("bound"), Is.False);
            Assert.Throws<InvalidOperationException>(() => bus.Send("bound", "wrong"));
            bus.ClearListeners();
            Assert.Throws<InvalidOperationException>(() => bus.Register<string>("bound", _ => { }));
            Assert.That(bus.RemoveKey("bound"), Is.True);
            bus.Register<string>("bound", _ => calls++);
            bus.Send("bound", "new signature");
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void ContravariantCallbacksKeepTheExplicitDeclaredSignature()
        {
            using var bus = new EventBus();
            var values = new List<object>();
            Action<object> broad = value => values.Add(value);
            Action<string> converted = broad;
            bus.Register<string>("variance", converted);
            bus.RegisterOnce<string>("variance", converted);
            Assert.Throws<InvalidOperationException>(() => bus.Send<object>("variance", "wrong signature"));
            Assert.Throws<InvalidOperationException>(() => bus.Register<object>("variance", broad));
            Assert.Throws<InvalidOperationException>(() => bus.Unregister<object>("variance", broad));
            bus.Send("variance", "right signature");
            Assert.That(values, Is.EqualTo(new object[] { "right signature", "right signature" }));
            Assert.That(bus.ListenerCount("variance"), Is.EqualTo(1));
            bus.Unregister<string>("variance", converted);
            Assert.That(bus.ListenerCount("variance"), Is.Zero);
        }

        [Test]
        public void ZeroOneThreeAndSevenArgumentsUseTheSharedTypedChannels()
        {
            using var bus = new EventBus();
            var calls = new List<string>();
            EventKey zeroKey = "zero";
            EventKey tripleKey = WideKey.Max;
            EventKey sevenKey = typeof(DerivedMessage);
            Action zero = () => calls.Add("zero");
            Action<int> one = value => calls.Add(value.ToString());
            Action<int, string, bool> triple = (number, text, flag) => calls.Add($"{number}:{text}:{flag}");
            Action<int, int, int, int, int, int, int> seven = (a, b, c, d, e, f, g) => calls.Add($"{a}:{b}:{c}:{d}:{e}:{f}:{g}");
            bus.RegisterOnce(zeroKey, zero);
            bus.RegisterOnce(1, one);
            bus.RegisterOnce(tripleKey, triple);
            bus.RegisterOnce(sevenKey, seven);
            bus.Send(zeroKey);
            bus.Send(1, 2);
            bus.Send(tripleKey, 3, "three", true);
            bus.Send(sevenKey, 1, 2, 3, 4, 5, 6, 7);
            Assert.That(calls, Is.EqualTo(new[] { "zero", "2", "3:three:True", "1:2:3:4:5:6:7" }));
            bus.SendAll(zeroKey);
            bus.SendAll(1, 2);
            bus.SendAll(tripleKey, 3, "three", true);
            bus.SendAll(sevenKey, 1, 2, 3, 4, 5, 6, 7);
            Assert.That(calls.Count, Is.EqualTo(4));
            bus.Register(zeroKey, zero);
            bus.Register(1, one);
            bus.Register(tripleKey, triple);
            bus.Register(sevenKey, seven);
            bus.SendAll(zeroKey);
            bus.SendAll(1, 2);
            bus.SendAll(tripleKey, 3, "three", true);
            bus.SendAll(sevenKey, 1, 2, 3, 4, 5, 6, 7);
            Assert.That(calls, Is.EqualTo(new[] { "zero", "2", "3:three:True", "1:2:3:4:5:6:7",
                "zero", "2", "3:three:True", "1:2:3:4:5:6:7" }));
            bus.Unregister(zeroKey, zero);
            bus.Unregister(1, one);
            bus.Unregister(tripleKey, triple);
            bus.Unregister(sevenKey, seven);
            Assert.That(bus.ListenerCount(zeroKey) + bus.ListenerCount(1)
                + bus.ListenerCount(tripleKey) + bus.ListenerCount(sevenKey), Is.Zero);
            Assert.That(bus.KeyCount, Is.EqualTo(4));
        }

        [Test]
        public void PayloadConvenienceRoutesOnlyByItsDeclaredType()
        {
            using var bus = new EventBus();
            var baseCalls = 0;
            var derivedCalls = 0;
            var textCalls = 0;
            var namedCalls = 0;
            var payload = new DerivedMessage();
            Action<Message> callback = message =>
            {
                Assert.That(message, Is.SameAs(payload));
                baseCalls++;
            };
            bus.RegisterMessage(callback);
            bus.RegisterMessageOnce<DerivedMessage>(_ => derivedCalls++);
            bus.RegisterMessage<string>(_ => textCalls++);
            bus.Register("message", () => namedCalls++);
            bus.SendMessage<Message>(payload);
            Assert.That(baseCalls, Is.EqualTo(1));
            Assert.That(derivedCalls, Is.Zero);
            bus.SendMessageAll(payload);
            Assert.That(baseCalls, Is.EqualTo(1));
            Assert.That(derivedCalls, Is.EqualTo(1));
            bus.SendMessage("message");
            bus.Send("message");
            Assert.That(textCalls, Is.EqualTo(1));
            Assert.That(namedCalls, Is.EqualTo(1));
            bus.UnregisterMessage(callback);
            bus.SendMessage<Message>(payload);
            Assert.That(baseCalls, Is.EqualTo(1));
        }

        [Test]
        public void ClearAndRemoveInvalidateOnlyOldTokensAndKeepBusContextsIndependent()
        {
            using var bus = new EventBus();
            using var other = new EventBus();
            var calls = 0;
            var otherCalls = 0;
            Action callback = () => calls++;
            var old = bus.Register("same", callback);
            other.Register<int>("same", _ => otherCalls++);
            bus.ClearListeners("same");
            bus.Register("same", callback);
            old.Unregister();
            bus.Send((EventKey)"same");
            Assert.That(calls, Is.EqualTo(1));
            var removed = bus.Register("same", callback);
            bus.RemoveKey("same");
            bus.Register("same", callback);
            removed.Unregister();
            bus.Send((EventKey)"same");
            other.Send("same", 1);
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(otherCalls, Is.EqualTo(1));
            bus.ClearKeys();
            Assert.That(bus.KeyCount, Is.Zero);
            bus.Register<int>("same", _ => calls++);
            bus.Send("same", 1);
            Assert.That(calls, Is.EqualTo(3));
            Assert.That(other.ListenerCount("same"), Is.EqualTo(1));
        }

        [Test]
        public void SendKeepsTheOriginalExceptionWhileSendAllContinuesAndAggregates()
        {
            using var bus = new EventBus();
            var first = new InvalidOperationException("first");
            var second = new ArgumentException("second");
            var tailCalls = 0;
            bus.Register<int>("error", _ => throw first);
            bus.Register<int>("error", _ => throw second);
            bus.Register<int>("error", _ => tailCalls++);
            Assert.That(Assert.Throws<InvalidOperationException>(() => bus.Send("error", 1)), Is.SameAs(first));
            Assert.That(tailCalls, Is.Zero);
            var aggregate = Assert.Throws<AggregateException>(() => bus.SendAll("error", 1));
            Assert.That(aggregate.InnerExceptions, Is.EqualTo(new Exception[] { first, second }));
            Assert.That(tailCalls, Is.EqualTo(1));
            bus.ClearListeners("error");
            bus.Register<int>("error", _ => tailCalls++);
            bus.Send("error", 1);
            Assert.That(tailCalls, Is.EqualTo(2));
        }

        [Test]
        public void RemovingAKeyDuringSendEndsItsSnapshotWithoutTouchingTheReplacement()
        {
            using var bus = new EventBus();
            var calls = new List<string>();
            bus.Register("replace", () =>
            {
                calls.Add("old-first");
                bus.RemoveKey("replace");
                bus.Register<int>("replace", _ => calls.Add("new"));
                bus.Send("replace", 1);
            });
            var old = bus.Register("replace", () => calls.Add("old-tail"));
            bus.Send((EventKey)"replace");
            old.Unregister();
            bus.Send("replace", 1);
            Assert.That(calls, Is.EqualTo(new[] { "old-first", "new", "new" }));
        }

        [Test]
        public void DisposeIsPermanentIdempotentAndEndsAnInFlightSnapshot()
        {
            var bus = new EventBus();
            var tailCalls = 0;
            bus.Register("close", bus.Dispose);
            var token = bus.Register("close", () => tailCalls++);
            bus.Send((EventKey)"close");
            Assert.That(tailCalls, Is.Zero);
            Assert.That(bus.IsDisposed, Is.True);
            bus.Dispose();
            token.Unregister();
            token.Unregister();
            bus.Unregister("close", bus.Dispose);
            bus.Unregister<int>("missing", _ => tailCalls++);
            bus.UnregisterMessage<Message>(_ => tailCalls++);
            Assert.That(bus.RemoveKey("close"), Is.False);
            Assert.That(bus.RemoveKey("missing"), Is.False);
            Assert.Throws<ObjectDisposedException>(() => bus.Send((EventKey)"missing"));
            Assert.Throws<ObjectDisposedException>(() => bus.SendAll("missing", 1));
            Assert.Throws<ObjectDisposedException>(() => bus.Register("missing", (Action)null));
            Assert.Throws<ObjectDisposedException>(() => bus.RegisterOnce<int>("missing", null));
        }

        [Test]
        public void DefaultIsLazyAndSessionResetClosesOnlyItsOldContext()
        {
            EventBus.ResetDefault();
            using var independent = new EventBus();
            try
            {
                if (EventBus.TryGetDefault(out var missing))
                {
                    missing.Send((EventKey)"missing");
                }

                if (EventBus.TryGetDefault(out missing))
                {
                    missing.RemoveKey("missing");
                }

                EventBus.ResetDefault();
                Assert.That(EventBus.TryGetDefault(out _), Is.False);
                var old = EventBus.Default;
                Assert.That(EventBus.TryGetDefault(out var current), Is.True);
                Assert.That(current, Is.SameAs(old));
                var oldCalls = 0;
                var newCalls = 0;
                var independentCalls = 0;
                var oldToken = old.Register("session", () => oldCalls++);
                independent.Register("session", () => independentCalls++);
                // 仅验证已接入 SessionState 的 reset 回调，不将此断言视为真实无 Domain Reload Play 验证。
                var session = typeof(EventBus).Assembly.GetType("YLFramework.Singletons.SingletonSessionState", true);
                var reset = session.GetMethod("ResetForNewSession", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(reset, Is.Not.Null);
                reset.Invoke(null, null);
                Assert.That(old.IsDisposed, Is.True);
                Assert.That(EventBus.TryGetDefault(out _), Is.False);
                Assert.Throws<ObjectDisposedException>(() => old.Send((EventKey)"session"));
                var next = EventBus.Default;
                Assert.That(next, Is.Not.SameAs(old));
                next.Register("session", () => newCalls++);
                oldToken.Unregister();
                old.Dispose();
                Assert.That(EventBus.TryGetDefault(out current), Is.True);
                Assert.That(current, Is.SameAs(next));
                next.Send((EventKey)"session");
                independent.Send((EventKey)"session");
                Assert.That(oldCalls, Is.Zero);
                Assert.That(newCalls, Is.EqualTo(1));
                Assert.That(independentCalls, Is.EqualTo(1));
            }
            finally
            {
                EventBus.ResetDefault();
            }
        }
    }
}
