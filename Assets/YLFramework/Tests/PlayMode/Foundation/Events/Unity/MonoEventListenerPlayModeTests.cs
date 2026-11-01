using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TestTools;
using YLFramework.Core;
using Object = UnityEngine.Object;
using YLFramework.Events;

namespace YLFramework.Tests.CommonSystem
{
    public class MonoEventListenerPlayModeTests
    {
        private GameObject _root;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_root)
            {
                Object.Destroy(_root);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator RuntimePhysicsListenersAcceptAllLayersAndInitializeTheirEvents()
        {
            _root = new GameObject("CommonSystem runtime defaults");
            _root.SetActive(false);
            foreach (var type in new[] { typeof(CollisionEventListener), typeof(TriggerEventListener) })
            {
                var listener = _root.AddComponent(type);
                var mask = type.GetField("layerMask", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(((LayerMask)mask.GetValue(listener)).value, Is.EqualTo(Physics.AllLayers), type.Name);
                var property = type.GetProperty("PhysicsLayerMask");
                Assert.That(property, Is.Not.Null, type.Name);
                property.SetValue(listener, (LayerMask)0);
                Assert.That(((LayerMask)mask.GetValue(listener)).value, Is.Zero);
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (typeof(UnityEventBase).IsAssignableFrom(field.FieldType))
                    {
                        Assert.That(field.GetValue(listener), Is.Not.Null, field.Name);
                    }
                }
            }
            var animation = _root.AddComponent<AnimationEventListener>();
            Assert.That(animation.animationEvent, Is.Not.Null);
            yield return null;
        }

        private Component AddLifecycleListener()
        {
            _root = new GameObject("CommonSystem lifecycle");
            _root.SetActive(false);
            var type = typeof(YLFramework.Events.EventListener).Assembly.GetType("YLFramework.Events.MonoEventListener");
            Assert.That(type, Is.Not.Null, "The local lifecycle bridge must belong to the YL event namespace.");
            return _root.AddComponent(type);
        }

        private static UnityEvent Event(Component listener, string name)
        {
            var field = listener.GetType().GetField(name);
            Assert.That(field, Is.Not.Null, name);
            Assert.That(field.FieldType, Is.EqualTo(typeof(UnityEvent)), name);
            var value = (UnityEvent)field.GetValue(listener);
            Assert.That(value, Is.Not.Null, name);
            return value;
        }

        [UnityTest]
        public IEnumerator NativeLifecyclePreservesOrderAndOnlyStartsOnce()
        {
            var listener = AddLifecycleListener();
            var calls = new List<string>();
            foreach (var name in new[] { "onAwake", "onStart", "onEnable", "onDisable", "onDestroy" })
            {
                var captured = name;
                Event(listener, name).AddListener(() => calls.Add(captured));
            }
            Assert.That(calls, Is.Empty);
            _root.SetActive(true);
            CollectionAssert.AreEqual(new[] { "onAwake", "onEnable" }, calls);
            yield return null;
            CollectionAssert.AreEqual(new[] { "onAwake", "onEnable", "onStart" }, calls);
            _root.SetActive(false);
            _root.SetActive(true);
            yield return null;
            Object.Destroy(_root);
            yield return null;
            CollectionAssert.AreEqual(new[]
            {
                "onAwake", "onEnable", "onStart", "onDisable", "onEnable", "onDisable", "onDestroy"
            }, calls);
        }

        [UnityTest]
        public IEnumerator EnableCallbackMayDisableAndDestroyItsOwnObject()
        {
            var listener = AddLifecycleListener();
            var calls = new List<string>();
            Event(listener, "onEnable").AddListener(() =>
            {
                calls.Add("enable");
                _root.SetActive(false);
            });
            Event(listener, "onDisable").AddListener(() =>
            {
                calls.Add("disable");
                Object.Destroy(_root);
            });
            Event(listener, "onDestroy").AddListener(() => calls.Add("destroy"));
            _root.SetActive(true);
            yield return null;
            CollectionAssert.AreEqual(new[] { "enable", "disable", "destroy" }, calls);
        }
    }
}
