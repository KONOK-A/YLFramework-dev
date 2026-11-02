using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YLFramework.Singletons;
using YLFramework.Events;

namespace YLFramework.Tests
{
    public class GlobalEventsContractTests
    {
        private enum Key : ulong
        {
            One = 1,
            High = ulong.MaxValue
        }
        private enum OtherKey
        {
            One = 1
        }
        private class Message
        {
        }
        private sealed class DerivedMessage : Message
        {
        }
        private sealed class ConstructedMessage
        {
            internal static int Constructions;
            public ConstructedMessage() => Constructions++;
        }

        [Test]
        public void StringFacadeLifetimeIsIsolatedAcrossDisposeAndReset() => CheckLifetime(
            () => StringEventSystem.Instance, (s, a) => s.Register("lifetime", a),
            s => s.Send("lifetime"), s => s.Dispose());

        [Test]
        public void TypeFacadeLifetimeIsIsolatedAcrossDisposeAndReset() => CheckLifetime(
            () => TypeEventSystem.Instance, (s, a) => s.Register<int>(_ => a()),
            s => s.Send(1), s => s.Dispose());

        [Test]
        public void EnumFacadeLifetimeIsIsolatedAcrossDisposeAndReset() => CheckLifetime(
            () => EnumEventSystem.Instance, (s, a) => s.Register(Key.One, _ => a()),
            s => s.Send(Key.One), s => s.Dispose());

        [Test]
        public void IntFacadeLifetimeIsIsolatedAcrossDisposeAndReset() => CheckLifetime(
            () => IntIdEventSystem.Instance, (s, a) => s.Register(1, (_, __) => a()),
            s => s.Send(1), s => s.Dispose());

        private static void CheckLifetime<T>(Func<T> instance, Func<T, Action, IUnregister> register,
            Action<T> send, Action<T> dispose) where T : class, ISingleton
        {
            Assert.That(SingletonProperty<T>.HasInstance, Is.False, "Test must own this facade context.");
            T old = null, next = null, latest = null;
            try
            {
                old = instance();
                var trace = new List<string>();
                Action callback = () => trace.Add("message");
                register(old, () =>
                {
                    trace.Add("dispose");
                    dispose(old);
                    next = instance();
                    register(next, callback);
                });
                var oldToken = register(old, callback);
                send(old);
                CollectionAssert.AreEqual(new[] { "dispose" }, trace);
                dispose(old);
                oldToken.Unregister();
                Assert.That(instance(), Is.SameAs(next));
                Assert.Throws<ObjectDisposedException>(() => register(old, callback));
                Assert.Throws<ObjectDisposedException>(() => send(old));
                Assert.Throws<ObjectDisposedException>(old.OnSingletonInit);
                send(next);
                CollectionAssert.AreEqual(new[] { "dispose", "message" }, trace);

                var resetToken = register(next, callback);
                // 模拟 Singleton 先摘入口；facade 仍须关闭自己的旧 bus，不重置无关模块。
                SingletonProperty<T>.Dispose();
                typeof(T).GetMethod("ResetSessionState", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
                Assert.Throws<ObjectDisposedException>(() => send(next));
                latest = instance();
                register(latest, callback);
                dispose(next);
                resetToken.Unregister();
                Assert.That(instance(), Is.SameAs(latest));
                send(latest);
                CollectionAssert.AreEqual(new[] { "dispose", "message", "message" }, trace);
            }
            finally
            {
                if (old != null)
                {
                    dispose(old);
                }

                if (next != null)
                {
                    dispose(next);
                }

                if (latest != null)
                {
                    dispose(latest);
                }
            }
        }

        [Test]
        public void FacadeDisposeDoesNotAffectOtherFacadesOrSharedBusContexts()
        {
            var strings = StringEventSystem.Instance;
            var types = TypeEventSystem.Instance;
            var enums = EnumEventSystem.Instance;
            var ints = IntIdEventSystem.Instance;
            var hadDefault = EventBus.TryGetDefault(out _);
            var defaultBus = EventBus.Default;
            using var independent = new EventBus();
            const string Key = "2.2-B.facade-isolation";
            try
            {
                var trace = new List<string>();
                strings.Register(Key, () => trace.Add("string"));
                types.Register<int>(_ => trace.Add("type"));
                enums.Register(GlobalEventsContractTests.Key.One, _ => trace.Add("enum"));
                ints.Register(1, (_, __) => trace.Add("int"));
                defaultBus.Register(Key, () => trace.Add("default"));
                independent.Register(Key, () => trace.Add("independent"));
                strings.Dispose();
                types.Send(1);
                enums.Send(GlobalEventsContractTests.Key.One);
                ints.Send(1);
                defaultBus.Send(Key);
                independent.Send(Key);
                CollectionAssert.AreEqual(new[] { "type", "enum", "int", "default", "independent" }, trace);
            }
            finally
            {
                strings.Dispose();
                types.Dispose();
                enums.Dispose();
                ints.Dispose();
                if (hadDefault)
                {
                    defaultBus.RemoveKey(Key);
                }
                else
                {
                    EventBus.ResetDefault();
                }
            }
        }

        [Test]
        public void StringFacadeKeepsSignaturesAndExactTokensUntilExplicitKeyRemoval()
        {
            var system = new StringEventSystem();
            try
            {
                system.Send("missing", "unbound");
                system.Register<string>("key", null);
                var values = new List<int>();
                Action<int> callback = values.Add;
                var old = system.Register("key", callback);
                old.Unregister();
                Assert.Throws<InvalidOperationException>(() => system.Send("key", "wrong"));
                Assert.Throws<InvalidOperationException>(() => system.Register<string>("key", null));
                system.Unregister("key");
                system.Register("key", callback);
                old.Unregister();
                system.Send("key", 1);
                CollectionAssert.AreEqual(new[] { 1 }, values);
                system.UnregisterAll();
                string result = null;
                system.Register<int, string, bool>("key", (a, b, c) => result = a + b + c);
                system.Send("key", 2, "value", true);
                Assert.That(result, Is.EqualTo("2valueTrue"));
                var error = new InvalidOperationException("broadcast");
                system.Register("failure", () => throw error);
                system.Register("failure", () => values.Add(99));
                Assert.That(Assert.Throws<InvalidOperationException>(() => system.Send("failure")), Is.SameAs(error));
                CollectionAssert.AreEqual(new[] { 1 }, values);
            }
            finally
            {
                system.Dispose();
            }
        }

        [Test]
        public void TypeFacadeRoutesByDeclaredTypeAndDoesNotConstructMissingMessages()
        {
            var system = new TypeEventSystem();
            try
            {
                ConstructedMessage.Constructions = 0;
                system.Send<ConstructedMessage>();
                Assert.That(ConstructedMessage.Constructions, Is.Zero);
                var trace = new List<string>();
                system.Register<Message>(_ => trace.Add("base"));
                system.Register<DerivedMessage>(_ => trace.Add("derived"));
                system.Send<Message>(new DerivedMessage());
                system.Send(new DerivedMessage());
                system.Register<ConstructedMessage>(_ => trace.Add("constructed"));
                system.Send<ConstructedMessage>();
                Assert.That(ConstructedMessage.Constructions, Is.EqualTo(1));
                CollectionAssert.AreEqual(new[] { "base", "derived", "constructed" }, trace);
            }
            finally
            {
                system.Dispose();
            }
        }

        [Test]
        public void DynamicFacadesPreserveEnumIdentityArrayPayloadAndIntConversion()
        {
            var enums = EnumEventSystem.Instance;
            var ints = IntIdEventSystem.Instance;
            try
            {
                var trace = new List<string>();
                var payload = new object[] { "payload", 3 };
                enums.Register(Key.One, args =>
                {
                    Assert.That(args, Is.SameAs(payload));
                    trace.Add("one");
                });
                enums.Register(Key.High, args =>
                {
                    Assert.That(args, Is.Null);
                    trace.Add("high");
                });
                enums.Register(OtherKey.One, _ => trace.Add("other"));
                enums.Send(Key.One, payload);
                enums.Send(Key.High, (object[])null);
                enums.Send(OtherKey.One);
                ints.Register((short)1, (id, args) =>
                {
                    Assert.That(id, Is.EqualTo(1));
                    Assert.That(args, Is.SameAs(payload));
                    trace.Add("int");
                });
                ints.Send(1, payload);
                Assert.Throws<OverflowException>(() => ints.Send(long.MaxValue));
                Assert.Throws<FormatException>(() => ints.Register("invalid", (_, __) => { }));
                CollectionAssert.AreEqual(new[] { "one", "high", "other", "int" }, trace);
            }
            finally
            {
                enums.Dispose();
                ints.Dispose();
            }
        }
    }
}
