using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YLFramework.Events;

namespace YLFramework.Core.Tests
{
    public class CoreEventsContractTests
    {
        private sealed class ProbeIntIdEventSystem : IntIdEventSystem
        {
        }

        private EventBus _previousDefault;
        private static readonly FieldInfo _defaultField = typeof(EventBus)
            .GetField("_default", BindingFlags.Static | BindingFlags.NonPublic);

        [SetUp]
        public void SetUp()
        {
            // 隔离本测试的 Default，不关闭其他测试或编辑器已持有的上下文。
            _previousDefault = (EventBus)_defaultField.GetValue(null);
            _defaultField.SetValue(null, null);
        }

        [TearDown]
        public void TearDown()
        {
            EventBus.ResetDefault();
            _defaultField.SetValue(null, _previousDefault);
            _previousDefault = null;
        }

        [Test]
        public void CoreFacadeHasOnlyTypedActionEntrypointsAndSharedSubscriptionTokens()
        {
            Assert.That(CoreEvents.ViewRemoved, Is.EqualTo("ON_REMOVE_VIEW"),
                "The persisted event key must not change when the CSharp identifier is renamed.");
            var core = typeof(EventManager).Assembly;
            Assert.That(core.GetTypes().Where(type => type.Namespace == "YLFramework.Core" &&
                typeof(Delegate).IsAssignableFrom(type)), Is.Empty, "Core must reuse typed BCL Action callbacks.");

            Assert.That(typeof(EventManager).GetFields(BindingFlags.Static | BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic), Is.Empty);
            Assert.That(typeof(EventManager).GetMethod("StringToHash"), Is.Null);
            Assert.That(typeof(EventManager).GetMethod("HashToString", BindingFlags.Static | BindingFlags.NonPublic), Is.Null);
            var registrations = typeof(EventManager).GetMethods().Where(method => method.Name == "AddEvent").ToArray();
            Assert.That(registrations.Length, Is.EqualTo(16));
            foreach (var method in registrations)
            {
                Assert.That(method.ReturnType, Is.EqualTo(typeof(IUnregister)));
                var callback = method.GetParameters()[1].ParameterType;
                Assert.That(callback == typeof(Action) || callback.Name.StartsWith("Action`"), Is.True);
            }
            Assert.That(typeof(IUnregister).Assembly.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
        }

        [Test]
        public void MissingOperationsAndNullRegistrationsValidateKeysWithoutCreatingDefault()
        {
            EventManager.TriggerEvent("missing");
            EventManager.TriggerEvent(17, 1);
            EventManager.TriggerEventAll("missing", 1, 2, 3, 4, 5, 6, 7);
            EventManager.RemoveEvent("missing", (Action)null);
            EventManager.RemoveEvent<int>(17, null);
            EventManager.RemoveAllEvent("missing");
            EventManager.RemoveAllEvent(17);
            EventManager.RemoveAllEvent();
            var empty = EventManager.AddEvent("missing", (Action)null);
            Assert.That(empty, Is.Not.Null);
            empty.Unregister();
            empty.Unregister();
            EventManager.AddEvent<int>(17, null).Unregister();
            EventManager.AddEvent<int, int, int, int, int, int, int>("missing", null).Unregister();
            Assert.Throws<ArgumentNullException>(() => EventManager.TriggerEvent((string)null));
            Assert.Throws<ArgumentNullException>(() => EventManager.TriggerEventAll((string)null, 1));
            Assert.Throws<ArgumentNullException>(() => EventManager.RemoveEvent((string)null, (Action)null));
            Assert.Throws<ArgumentNullException>(() => EventManager.RemoveAllEvent((string)null));
            Assert.Throws<ArgumentNullException>(() => EventManager.AddEvent((string)null, (Action)null));
            Assert.That(EventBus.TryGetDefault(out _), Is.False);

            var bus = EventBus.Default;
            EventManager.TriggerEventAll("missing");
            EventManager.TriggerEvent("missing", 1);
            EventManager.RemoveEvent(17, (Action)null);
            EventManager.AddEvent<string>("missing", null).Unregister();
            Assert.That(bus.KeyCount, Is.Zero);
            EventManager.AddEvent("", () => { });
            Assert.That(bus.HasKey(""), Is.True, "Empty strings remain valid shared keys.");
        }

        [Test]
        public void CoreAndDefaultShareZeroOneAndSevenArgumentRoutesButNeverHashAliases()
        {
            const string Key = "CoreEventsContract.zero";
            var hash = UnityEngine.Animator.StringToHash(Key);
            var calls = new List<string>();
            Action zero = () => calls.Add("zero");
            Action<int> one = value => calls.Add("one:" + value);
            Action<int, string, bool, long, float, double, char> seven =
                (a, b, c, d, e, f, g) => calls.Add($"seven:{a}:{b}:{c}:{d}:{e}:{f}:{g}");
            EventManager.AddEvent(Key, zero);
            var bus = EventBus.Default;
            bus.Register(hash, one);
            EventManager.AddEvent("seven", seven);

            bus.Send(Key);
            EventManager.TriggerEvent(hash, 2);
            bus.Send("seven", 1, "two", true, 4L, 5f, 6d, 'g');
            EventManager.TriggerEvent("seven", 1, "two", true, 4L, 5f, 6d, 'g');
            EventManager.TriggerEventAll("seven", 1, "two", true, 4L, 5f, 6d, 'g');
            Assert.That(calls, Is.EqualTo(new[] { "zero", "one:2", "seven:1:two:True:4:5:6:g",
                "seven:1:two:True:4:5:6:g", "seven:1:two:True:4:5:6:g" }));
            Assert.That(bus.KeyCount, Is.EqualTo(3));

            EventManager.RemoveEvent(Key, zero);
            EventManager.RemoveEvent(hash, one);
            EventManager.RemoveEvent("seven", seven);
            bus.Send(Key);
            EventManager.TriggerEventAll(hash, 2);
            EventManager.TriggerEventAll("seven", 1, "two", true, 4L, 5f, 6d, 'g');
            Assert.That(calls.Count, Is.EqualTo(5));
            Assert.That(bus.KeyCount, Is.EqualTo(3), "Delegate removal preserves the bound signatures.");
        }

        [Test]
        public void ExactTokensRetainSignaturesUntilWholeKeyRemovalAndCannotCancelReplacements()
        {
            var calls = new List<string>();
            Action<int> a = _ => calls.Add("A");
            var first = EventManager.AddEvent(17, a);
            var middle = EventBus.Default.Register<int>(17, _ => calls.Add("B"));
            var last = EventManager.AddEvent(17, a);
            first.Unregister();
            first.Unregister();
            EventBus.Default.Send(17, 1);
            Assert.That(calls, Is.EqualTo(new[] { "B", "A" }));
            Assert.Throws<InvalidOperationException>(() => EventManager.AddEvent<string>(17, null));
            Assert.Throws<InvalidOperationException>(() => EventManager.RemoveEvent<string>(17, null));
            Assert.Throws<InvalidOperationException>(() => EventManager.TriggerEvent(17, "wrong"));
            Assert.That(EventBus.Default.ListenerCount(17), Is.EqualTo(2));
            middle.Unregister();
            last.Unregister();
            Assert.That(EventBus.Default.HasKey(17), Is.True);
            Assert.Throws<InvalidOperationException>(() => EventManager.TriggerEventAll(17));

            EventManager.RemoveAllEvent(17);
            Assert.That(EventBus.Default.HasKey(17), Is.False);
            var old = EventManager.AddEvent(17, a);
            EventManager.RemoveAllEvent(17);
            EventManager.AddEvent(17, a);
            old.Unregister();
            EventManager.TriggerEvent(17, 2);
            Assert.That(calls, Is.EqualTo(new[] { "B", "A", "A" }));
            EventManager.RemoveAllEvent(17);
            EventManager.AddEvent<string>(17, value => calls.Add(value));
            last.Unregister();
            EventManager.TriggerEvent(17, "rebound");
            Assert.That(calls, Is.EqualTo(new[] { "B", "A", "A", "rebound" }));
        }

        [Test]
        public void WholeKeyAndWholeDefaultRemovalTerminateSnapshotsAndAllDefaultKeyDomains()
        {
            var calls = new List<string>();
            EventManager.AddEvent("replace", () =>
            {
                calls.Add("old-first");
                EventManager.RemoveAllEvent("replace");
                EventManager.AddEvent<int>("replace", _ => calls.Add("new"));
                EventManager.TriggerEvent("replace", 1);
            });
            var old = EventManager.AddEvent("replace", () => calls.Add("old-tail"));
            EventManager.TriggerEvent("replace");
            old.Unregister();
            EventManager.TriggerEvent("replace", 2);
            Assert.That(calls, Is.EqualTo(new[] { "old-first", "new", "new" }));

            var bus = EventBus.Default;
            bus.RegisterMessage<int>(_ => calls.Add("message"));
            bus.Register(DayOfWeek.Monday, () => calls.Add("enum"));
            EventManager.AddEvent(17, () => calls.Add("int"));
            using var independent = new EventBus();
            independent.Register("replace", () => calls.Add("independent"));
            EventManager.RemoveAllEvent();
            Assert.That(bus.KeyCount, Is.Zero);
            Assert.That(EventBus.Default, Is.SameAs(bus));
            Assert.That(bus.IsDisposed, Is.False);
            bus.SendMessage(1);
            bus.Send(DayOfWeek.Monday);
            EventManager.TriggerEvent(17);
            independent.Send("replace");
            Assert.That(calls, Is.EqualTo(new[] { "old-first", "new", "new", "independent" }));
        }

        [Test]
        public void OrdinaryTriggerFailsFastWhileExplicitViewNotificationContinuesAndAggregates()
        {
            var first = new InvalidOperationException("first observer");
            var second = new ArgumentException("second observer");
            var calls = new List<string>();
            EventManager.AddEvent<View>("view", _ =>
            {
                calls.Add("first");
                throw first;
            });
            EventManager.AddEvent<View>("view", _ =>
            {
                calls.Add("second");
                throw second;
            });
            EventManager.AddEvent<View>("view", view =>
            {
                Assert.That(view, Is.Null);
                calls.Add("tail");
            });
            Assert.That(Assert.Throws<InvalidOperationException>(() => EventManager.TriggerEvent<View>("view", null)), Is.SameAs(first));
            Assert.That(calls, Is.EqualTo(new[] { "first" }));
            calls.Clear();
            var aggregate = Assert.Throws<AggregateException>(() => EventManager.TriggerEventAll<View>("view", null));
            Assert.That(aggregate.InnerExceptions, Is.EqualTo(new Exception[] { first, second }));
            Assert.That(calls, Is.EqualTo(new[] { "first", "second", "tail" }));
            EventManager.RemoveAllEvent("view");
            EventManager.AddEvent<View>("view", _ => calls.Add("healthy"));
            EventManager.TriggerEvent<View>("view", null);
            Assert.That(calls[calls.Count - 1], Is.EqualTo("healthy"));
        }

        [Test]
        public void YLFacadesStayIsolatedFromCorePublishingRemovalAndDisposal()
        {
            var strings = new StringEventSystem();
            // 通过 protected 构造建立独立 facade，不关闭已有全局入口。
            var integers = new ProbeIntIdEventSystem();
            try
            {
                var coreCalls = 0;
                var stringCalls = 0;
                var integerCalls = 0;
                EventManager.AddEvent<int>("same", _ => coreCalls++);
                EventManager.AddEvent(17, () => coreCalls++);
                strings.Register<string>("same", _ => stringCalls++);
                integers.Register(17, (id, args) =>
                {
                    Assert.That(id, Is.EqualTo(17));
                    Assert.That(args, Is.EqualTo(new object[] { "payload" }));
                    integerCalls++;
                });
                EventManager.TriggerEvent("same", 1);
                EventManager.TriggerEvent(17);
                Assert.That(coreCalls, Is.EqualTo(2));
                Assert.That(stringCalls + integerCalls, Is.Zero);
                strings.Send("same", "payload");
                integers.Send(17, "payload");
                Assert.That(coreCalls, Is.EqualTo(2));
                Assert.That(stringCalls, Is.EqualTo(1));
                Assert.That(integerCalls, Is.EqualTo(1));

                EventManager.RemoveAllEvent();
                strings.Send("same", "payload");
                integers.Send(17, "payload");
                Assert.That(stringCalls, Is.EqualTo(2));
                Assert.That(integerCalls, Is.EqualTo(2));
                EventManager.AddEvent<int>("same", _ => coreCalls++);
                EventManager.AddEvent(17, () => coreCalls++);
                strings.Dispose();
                integers.Dispose();
                EventManager.TriggerEvent("same", 2);
                EventManager.TriggerEvent(17);
                Assert.That(coreCalls, Is.EqualTo(4));
            }
            finally
            {
                strings.Dispose();
                integers.Dispose();
            }
        }
    }
}
