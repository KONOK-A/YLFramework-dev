using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using YLFramework.Pooling;
using YLFramework.Events;
using EventType = YLFramework.Events.EventType;
using Object = UnityEngine.Object;

namespace YLFramework.Tests
{
    public class UnityEventListenerContractTests
    {
        [Test]
        public void NativePayloadSchemaMatchesAllTwentyUnityMessages()
        {
            var root = new GameObject("Events.Listener.SchemaTest");
            try
            {
                var listener = root.AddComponent<EventListener>();
                CheckPayload<PointerEventData>(listener, EventType.OnMouseEnter, EventType.OnMouseExit,
                    EventType.OnClick, EventType.OnClickDown, EventType.OnClickUp,
                    EventType.OnBeginDrag, EventType.OnDrag, EventType.OnEndDrag);
                CheckPayload<Collision>(listener, EventType.OnCollisionEnter, EventType.OnCollisionStay, EventType.OnCollisionExit);
                CheckPayload<Collision2D>(listener, EventType.OnCollisionEnter2D, EventType.OnCollisionStay2D, EventType.OnCollisionExit2D);
                CheckPayload<Collider>(listener, EventType.OnTriggerEnter, EventType.OnTriggerStay, EventType.OnTriggerExit);
                CheckPayload<Collider2D>(listener, EventType.OnTriggerEnter2D, EventType.OnTriggerStay2D, EventType.OnTriggerExit2D);
                Assert.Throws<ArgumentOutOfRangeException>(() => listener.Register<int>((EventType)999, _ => { }));
                Assert.Throws<ArgumentException>(() => listener.Register<BaseEventData>(EventType.OnClick, null));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void CheckPayload<T>(EventListener listener, params EventType[] eventTypes)
        {
            foreach (var eventType in eventTypes)
            {
                Assert.Throws<ArgumentException>(() => listener.Register<int>(eventType, _ => { }), eventType.ToString());
                var calls = 0;
                var token = listener.Register<T>(eventType, value =>
                {
                    Assert.That(value, Is.Null);
                    calls++;
                });
                var methodName = eventType switch
                {
                    EventType.OnMouseEnter => "OnPointerEnter",
                    EventType.OnMouseExit => "OnPointerExit",
                    EventType.OnClick => "OnPointerClick",
                    EventType.OnClickDown => "OnPointerDown",
                    EventType.OnClickUp => "OnPointerUp",
                    _ => eventType.ToString()
                };
                var method = typeof(EventListener).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                method.Invoke(listener, new object[] { null });
                Assert.That(calls, Is.EqualTo(1), eventType.ToString());
                token.Unregister();
            }
        }

        [Test]
        public void LayerFilteringControlsBothTriggerChannelsButNotPointerMessages()
        {
            var root = new GameObject("Events.Listener.FilterTest");
            var other = new GameObject("Events.Listener.FilterCollider");
            var other2D = new GameObject("Events.Listener.FilterCollider2D");
            var flag = new GameObject("Events.Listener.PersistentFlag");
            try
            {
                var listener = root.AddComponent<EventListener>();
                var property = typeof(EventListener).GetProperty("PhysicsLayerMask");
                Assert.That(property, Is.Not.Null);
                Assert.That(((LayerMask)property.GetValue(listener)).value, Is.EqualTo(Physics.AllLayers));
                Collider collider = other.AddComponent<BoxCollider>();
                Collider2D collider2D = other2D.AddComponent<BoxCollider2D>();
                Assert.That(collider && collider2D, Is.True);
                CheckFilter(listener, property, EventType.OnTriggerEnter, "triggerEnter", collider, other, flag);
                CheckFilter(listener, property, EventType.OnTriggerStay, "triggerStay", collider, other, flag);
                CheckFilter(listener, property, EventType.OnTriggerExit, "triggerExit", collider, other, flag);
                CheckFilter(listener, property, EventType.OnTriggerEnter2D, "triggerEnter2D", collider2D, other2D, flag);
                CheckFilter(listener, property, EventType.OnTriggerStay2D, "triggerStay2D", collider2D, other2D, flag);
                CheckFilter(listener, property, EventType.OnTriggerExit2D, "triggerExit2D", collider2D, other2D, flag);

                property.SetValue(listener, (LayerMask)0);
                var clicks = 0;
                listener.Register<PointerEventData>(EventType.OnClick, _ => clicks++);
                var click = new UnityEvent<PointerEventData>();
                UnityEventTools.AddBoolPersistentListener(click, flag.SetActive, false);
                click.SetPersistentListenerState(0, UnityEventCallState.EditorAndRuntime);
                typeof(EventListener).GetField("pointerClick", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(listener, click);
                flag.SetActive(true);
                listener.OnPointerClick(null);
                Assert.That(clicks, Is.EqualTo(1));
                Assert.That(flag.activeSelf, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(other);
                Object.DestroyImmediate(other2D);
                Object.DestroyImmediate(flag);
            }
        }

        private static void CheckFilter<T>(EventListener listener, PropertyInfo property, EventType type,
            string fieldName, T payload, GameObject other, GameObject flag)
        {
            var calls = 0;
            var unityEvent = new UnityEvent<T>();
            UnityEventTools.AddBoolPersistentListener(unityEvent, flag.SetActive, false);
            unityEvent.SetPersistentListenerState(0, UnityEventCallState.EditorAndRuntime);
            typeof(EventListener).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(listener, unityEvent);
            var method = typeof(EventListener).GetMethod(type.ToString(), BindingFlags.Instance | BindingFlags.NonPublic);
            var token = listener.Register<T>(type, _ =>
            {
                Assert.That(flag.activeSelf, Is.True, "Code must precede the same callback's Inspector event.");
                calls++;
            });
            foreach (var item in new[] { (Physics.AllLayers, 0, true), (0, 0, false), (1 << 31, 0, false), (1 << 31, 31, true) })
            {
                property.SetValue(listener, (LayerMask)item.Item1);
                other.layer = item.Item2;
                flag.SetActive(true);
                var before = calls;
                method.Invoke(listener, new object[] { payload });
                Assert.That(calls - before, Is.EqualTo(item.Item3 ? 1 : 0), type.ToString());
                Assert.That(flag.activeSelf, Is.EqualTo(!item.Item3), type.ToString());
            }
            var changeMask = listener.Register<T>(type, _ => property.SetValue(listener, (LayerMask)0));
            property.SetValue(listener, (LayerMask)Physics.AllLayers);
            flag.SetActive(true);
            var previous = calls;
            method.Invoke(listener, new object[] { payload });
            Assert.That(calls, Is.EqualTo(previous + 1));
            Assert.That(flag.activeSelf, Is.False, "A code callback changes only the next message's filter.");
            flag.SetActive(true);
            method.Invoke(listener, new object[] { payload });
            Assert.That(calls, Is.EqualTo(previous + 1));
            Assert.That(flag.activeSelf, Is.True);
            changeMask.Unregister();
            token.Unregister();
        }

        [Test]
        public void PersistentUnityEventSurvivesDefaultCleanupAndExplicitTrueOnlyDisconnectsTheField()
        {
            var root = new GameObject("Events.Listener.PersistentTest");
            var receiverRoot = new GameObject("Events.Listener.PersistentReceiver");
            try
            {
                var listener = root.AddComponent<EventListener>();
                var receiver = receiverRoot.AddComponent<EventListener>();
                var order = new List<string>();
                receiver.Register<PointerEventData>(EventType.OnClick, _ => order.Add("persistent"));
                var unityEvent = new UnityEvent<PointerEventData>();
                UnityEventTools.AddPersistentListener(unityEvent, receiver.OnPointerClick);
                unityEvent.SetPersistentListenerState(0, UnityEventCallState.EditorAndRuntime);
                var field = typeof(EventListener).GetField("pointerClick", BindingFlags.Instance | BindingFlags.NonPublic);
                field.SetValue(listener, unityEvent);
                listener.Register<PointerEventData>(EventType.OnClick, _ => order.Add("code"));
                listener.OnPointerClick(null);
                Assert.That(order, Is.EqualTo(new[] { "code", "persistent" }));
                listener.UnregisterAll();
                order.Clear();
                listener.OnPointerClick(null);
                Assert.That(order, Is.EqualTo(new[] { "persistent" }));
                Assert.That(unityEvent.GetPersistentEventCount(), Is.EqualTo(1));
                listener.UnregisterAll(true);
                order.Clear();
                listener.OnPointerClick(null);
                Assert.That(order, Is.Empty);
                unityEvent.Invoke(null);
                Assert.That(order, Is.EqualTo(new[] { "persistent" }));
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(receiverRoot);
            }
        }

        [Test]
        public void ListenerExtensionsDoNotCreateOnRemovalAndResetStopsOldCodeSnapshot()
        {
            var root = new GameObject("Events.Listener.ExtensionTest");
            try
            {
                root.transform.RemoveAllEvent();
                root.transform.UnregisterEvent(EventType.OnClick);
                Assert.That(root.GetComponent<EventListener>(), Is.Null);
                var calls = new List<string>();
                root.transform.OnClick(_ =>
                {
                    calls.Add("first");
                    root.transform.UnregisterEvent(EventType.OnClick);
                    root.transform.OnClick(__ => calls.Add("new"));
                });
                var old = root.transform.OnClick(_ => calls.Add("old"));
                var listener = root.GetComponent<EventListener>();
                listener.OnPointerClick(null);
                Assert.That(calls, Is.EqualTo(new[] { "first" }));
                old.Unregister();
                listener.OnPointerClick(null);
                Assert.That(calls, Is.EqualTo(new[] { "first", "new" }));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ListenerCleanupDoesNotDependOnTheSharedEasyEventPoolLifetime()
        {
            var root = new GameObject("Events.Listener.PoolLifetimeTest");
            var pool = SafeObjectPool<EasyEvent<PointerEventData>>.Instance;
            Exception cleanupError = null;
            try
            {
                var listener = root.AddComponent<EventListener>();
                var oldCalls = 0;
                listener.Register<PointerEventData>(EventType.OnClick, _ =>
                {
                    pool.Dispose();
                    try
                    {
                        listener.UnregisterAll();
                    }
                    catch (Exception error)
                    {
                        cleanupError = error;
                    }
                });
                listener.Register<PointerEventData>(EventType.OnClick, _ => oldCalls++);
                listener.OnPointerClick(null);
                Assert.That(cleanupError, Is.Null, "组件自有代码监听的结束不应依赖全局池仍存活。");
                Assert.That(oldCalls, Is.Zero, "清理必须终结旧 owner 的在途快照。");
                listener.OnPointerClick(null);
                Assert.That(oldCalls, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(root);
                pool.Dispose();
            }
        }
    }
}
