using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Events;
using EventType = YLFramework.Events.EventType;
using Object = UnityEngine.Object;

namespace YLFramework.Tests
{
    public class EventsLifecyclePlayModeTests
    {
        private readonly List<GameObject> _roots = new List<GameObject>();
        private Scene _scene;
        private Scene _otherScene;
        private AsyncOperation _unload;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var root in _roots)
            {
                if (root)
                {
                    Object.Destroy(root);
                }
            }
            _roots.Clear();
            if (_unload != null)
            {
                yield return _unload;
            }
            else if (_scene.IsValid() && _scene.isLoaded)
            {
                yield return SceneManager.UnloadSceneAsync(_scene);
            }

            if (_otherScene.IsValid() && _otherScene.isLoaded)
            {
                yield return SceneManager.UnloadSceneAsync(_otherScene);
            }

            _scene = default;
            _otherScene = default;
            _unload = null;
            yield return null;
        }

        private GameObject Root()
        {
            var root = new GameObject("Events.Lifecycle.PlayModeTest");
            _roots.Add(root);
            return root;
        }

        [UnityTest]
        public IEnumerator DestroyBindingWorksWithTheGameObjectOverload()
        {
            var source = new EasyEvent();
            var root = Root();
            var calls = 0;
            source.Register(() => calls++).UnregisterWhenGameObjectDestroyed(root);
            source.Trigger();
            Object.Destroy(root);
            yield return null;
            source.Trigger();
            Assert.That(calls, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator DestroyBindingWorksWithTheComponentOverload()
        {
            var source = new EasyEvent();
            var root = Root();
            var calls = 0;
            source.Register(() => calls++).UnregisterWhenGameObjectDestroyed(root.transform);
            Object.Destroy(root);
            yield return null;
            source.Trigger();
            Assert.That(calls, Is.Zero);
        }

        [UnityTest]
        public IEnumerator DisableBindingsSupportBothOverloadsAndRepeatedEnableDisable()
        {
            var source = new EasyEvent();
            var root = Root();
            var calls = 0;
            source.Register(() => calls++).UnregisterWhenDisabled(root);
            source.Trigger();
            root.SetActive(false);
            source.Trigger();
            root.SetActive(true);
            source.Register(() => calls++).UnregisterWhenDisabled(root.transform);
            source.Trigger();
            root.SetActive(false);
            source.Trigger();
            Assert.That(calls, Is.EqualTo(2));
            yield return null;
        }

        [UnityTest]
        public IEnumerator TriggerDeduplicatesTokensAndAllowsRemovingAnOwnedToken()
        {
            var source = new EasyEvent();
            var root = Root();
            var trigger = root.AddComponent<OnDestroyUnregisterTrigger>();
            var calls = 0;
            var token = source.Register(() => calls++);
            Assert.That(trigger.AddUnregister(token), Is.SameAs(token));
            trigger.AddUnregister(token);
            trigger.Unregister();
            Assert.DoesNotThrow(() => trigger.Unregister());
            source.Trigger();
            Assert.That(calls, Is.Zero);
            var detached = source.Register(() => calls++);
            trigger.AddUnregister(detached);
            trigger.RemoveUnregister(detached);
            Object.Destroy(root);
            yield return null;
            source.Trigger();
            Assert.That(calls, Is.EqualTo(1));
            detached.Unregister();
        }

        [UnityTest]
        public IEnumerator SceneBindingKeepsTheOriginalAnyUnloadedSceneScope()
        {
            var source = new EasyEvent();
            var calls = 0;
            source.Register(() => calls++).UnregisterWhenAnySceneUnloaded();
            var trigger = SceneUnregisterTrigger.Instance;
            Assert.That(SceneUnregisterTrigger.Instance, Is.SameAs(trigger));
            source.Trigger();
            _scene = SceneManager.CreateScene("Events.Lifecycle.UnloadTest");
            Assert.That(trigger.gameObject.scene, Is.Not.EqualTo(_scene));
            _unload = SceneManager.UnloadSceneAsync(_scene);
            yield return _unload;
            source.Trigger();
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(trigger != null, Is.True);
        }

        [UnityTest]
        public IEnumerator TriggerAttemptsAllFailuresAndRetainsNewAndFailedResponsibilities()
        {
            var trigger = Root().AddComponent<OnDestroyUnregisterTrigger>();
            var attempts = 0;
            var succeeded = 0;
            var added = 0;
            var fail = true;
            var firstError = new InvalidOperationException("first cancellation");
            var secondError = new ArgumentException("second cancellation");
            trigger.AddUnregister(new CustomUnregister(() =>
            {
                attempts++;
                if (fail)
                {
                    throw firstError;
                }
            }));
            trigger.AddUnregister(new CustomUnregister(() =>
            {
                if (fail)
                {
                    throw secondError;
                }
            }));
            trigger.AddUnregister(new CustomUnregister(() =>
            {
                succeeded++;
                trigger.AddUnregister(new CustomUnregister(() => added++));
                trigger.Unregister();
            }));
            var error = Assert.Throws<AggregateException>(() => trigger.Unregister());
            Assert.That(error.InnerExceptions, Is.EquivalentTo(new Exception[] { firstError, secondError }));
            Assert.That(succeeded, Is.EqualTo(1));
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(added, Is.Zero);
            fail = false;
            trigger.Unregister();
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(added, Is.EqualTo(1));
            Assert.DoesNotThrow(() => trigger.Unregister());
            yield return null;
        }

        [UnityTest]
        public IEnumerator SceneTriggerDestructionCancelsPendingSubscriptions()
        {
            var source = new EventChannel();
            source.Register(() => { }).UnregisterWhenAnySceneUnloaded();
            _scene = SceneManager.CreateScene("Events.Lifecycle.PendingScene");
            source.Register(() => { }).UnregisterWhenSceneUnloaded(_scene);
            var trigger = SceneUnregisterTrigger.Instance;
            Object.Destroy(trigger.gameObject);
            yield return null;
            Assert.That(source.ListenerCount, Is.Zero);
            source.Dispose();
        }

        [UnityTest]
        public IEnumerator AdditiveSceneBindingCapturesExplicitOwnerAndKeepsAnySceneDistinct()
        {
            var source = new EventChannel();
            _scene = SceneManager.CreateScene("Events.Lifecycle.Owner");
            _otherScene = SceneManager.CreateScene("Events.Lifecycle.Other");
            var root = Root();
            SceneManager.MoveGameObjectToScene(root, _scene);
            var ownerCalls = 0;
            var otherCalls = 0;
            var anyCalls = 0;
            source.Register(() => ownerCalls++).UnregisterWhenSceneUnloaded(root.scene);
            source.Register(() => otherCalls++).UnregisterWhenSceneUnloaded(_otherScene);
            source.Register(() => anyCalls++).UnregisterWhenAnySceneUnloaded();
            SceneManager.MoveGameObjectToScene(root, _otherScene);
            yield return SceneManager.UnloadSceneAsync(_otherScene);
            source.Trigger();
            Assert.That(ownerCalls, Is.EqualTo(1));
            Assert.That(otherCalls, Is.Zero);
            Assert.That(anyCalls, Is.Zero);
            _unload = SceneManager.UnloadSceneAsync(_scene);
            yield return _unload;
            source.Trigger();
            Assert.That(ownerCalls, Is.EqualTo(1));
            Assert.That(source.ListenerCount, Is.Zero);
            source.Register(() => { }).UnregisterWhenSceneUnloaded(_scene);
            source.Register(() => { }).UnregisterWhenSceneUnloaded(default);
            Assert.That(source.ListenerCount, Is.Zero);
            source.Dispose();
        }

        [UnityTest]
        public IEnumerator SceneCleanupKeepsFailuresAndDefersReentrantRegistrations()
        {
            _scene = SceneManager.CreateScene("Events.Lifecycle.ReentrantOwner");
            var trigger = SceneUnregisterTrigger.Instance;
            var fail = true;
            var attempts = 0;
            var sceneCalls = 0;
            var laterCalls = 0;
            var error = new InvalidOperationException("scene cancellation failed");
            new CustomUnregister(() =>
            {
                attempts++;
                if (fail)
                {
                    throw error;
                }
            }).UnregisterWhenSceneUnloaded(_scene);
            new CustomUnregister(() => sceneCalls++).UnregisterWhenSceneUnloaded(_scene);
            new CustomUnregister(() =>
                new CustomUnregister(() => laterCalls++).UnregisterWhenSceneUnloaded(_scene))
                .UnregisterWhenAnySceneUnloaded();
            Assert.That(Assert.Throws<InvalidOperationException>(() => trigger.Unregister()), Is.SameAs(error));
            Assert.That(sceneCalls, Is.EqualTo(1));
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(laterCalls, Is.Zero);
            fail = false;
            trigger.Unregister();
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(laterCalls, Is.EqualTo(1));
            _unload = SceneManager.UnloadSceneAsync(_scene);
            yield return _unload;
            Assert.That(attempts, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator RemovingASceneOwnedTokenTransfersResponsibilityWithoutCancellingIt()
        {
            _scene = SceneManager.CreateScene("Events.Lifecycle.DetachedOwner");
            var source = new EventChannel();
            var token = source.Register(() => { });
            var trigger = SceneUnregisterTrigger.Instance;
            trigger.AddUnregister(token, _scene);
            trigger.RemoveUnregister(token);
            _unload = SceneManager.UnloadSceneAsync(_scene);
            yield return _unload;
            try
            {
                Assert.That(source.ListenerCount, Is.EqualTo(1));
            }
            finally
            {
                token.Unregister();
                source.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator InactiveDisableBindingWaitsForActualDisableAndDeadHostsCancelImmediately()
        {
            var source = new EventChannel();
            var root = Root();
            var component = root.transform;
            root.SetActive(false);
            source.Register(() => { }).UnregisterWhenDisabled(root);
            Assert.That(source.ListenerCount, Is.EqualTo(1));
            root.SetActive(true);
            root.SetActive(false);
            Assert.That(source.ListenerCount, Is.Zero);
            Object.Destroy(root);
            yield return null;
            source.Register(() => { }).UnregisterWhenGameObjectDestroyed(root);
            source.Register(() => { }).UnregisterWhenGameObjectDestroyed(component);
            source.Register(() => { }).UnregisterWhenDisabled(root);
            source.Register(() => { }).UnregisterWhenDisabled(component);
            Assert.That(source.ListenerCount, Is.Zero);
            source.Dispose();
        }

        [UnityTest]
        public IEnumerator DisableAndDestroyReportErrorsAfterAttemptingAllResponsibilities()
        {
            var root = Root();
            var trigger = root.AddComponent<OnDisableUnregisterTrigger>();
            var fail = true;
            var attempts = 0;
            var laterCalls = 0;
            trigger.AddUnregister(new CustomUnregister(() =>
            {
                attempts++;
                if (fail)
                {
                    throw new InvalidOperationException("disable cleanup failed");
                }
            }));
            trigger.AddUnregister(new CustomUnregister(() => laterCalls++));
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: disable cleanup failed"));
            root.SetActive(false);
            Assert.That(laterCalls, Is.EqualTo(1));
            fail = false;
            trigger.Unregister();
            Assert.That(attempts, Is.EqualTo(2));
            trigger.AddUnregister(new CustomUnregister(() =>
            {
                throw new InvalidOperationException("destroy cleanup failed");
            }));
            trigger.AddUnregister(new CustomUnregister(() => laterCalls++));
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: destroy cleanup failed"));
            Object.Destroy(root);
            yield return null;
            Assert.That(laterCalls, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator ListenerKeepsUnityEventChannelAndCodeOwnershipDistinct()
        {
            var root = Root();
            var listener = root.AddComponent<EventListener>();
            var order = new List<string>();
            var unityEvent = new UnityEvent<PointerEventData>();
            unityEvent.AddListener(_ => order.Add("unity"));
            var field = typeof(EventListener).GetField("pointerClick", BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(listener, unityEvent);
            var oldToken = listener.Register<PointerEventData>(EventType.OnClick, _ => order.Add("old"));
            listener.Register<PointerEventData>(EventType.OnClick, null).Unregister();
            listener.OnPointerClick(null);
            Assert.That(order, Is.EqualTo(new[] { "old", "unity" }));
            listener.UnregisterAll();
            Assert.That(field.GetValue(listener), Is.SameAs(unityEvent));
            var token = listener.Register<PointerEventData>(EventType.OnClick, _ => order.Add("new"));
            oldToken.Unregister();
            order.Clear();
            listener.OnPointerClick(null);
            Assert.That(order, Is.EqualTo(new[] { "new", "unity" }));
            root.SetActive(false);
            root.SetActive(true);
            order.Clear();
            listener.OnPointerClick(null);
            Assert.That(order, Is.EqualTo(new[] { "new", "unity" }));
            token.Unregister();
            var failing = listener.Register<PointerEventData>(EventType.OnClick, _ =>
            {
                throw new InvalidOperationException("code failed");
            });
            order.Clear();
            Assert.Throws<InvalidOperationException>(() => listener.OnPointerClick(null));
            Assert.That(order, Is.Empty);
            failing.Unregister();
            listener.UnregisterAll(true);
            Assert.That(field.GetValue(listener), Is.Null);
            listener.OnPointerClick(null);
            Assert.That(order, Is.Empty);
            unityEvent.Invoke(null);
            Assert.That(order, Is.EqualTo(new[] { "unity" }));
            listener.Register<PointerEventData>(EventType.OnClick, _ => order.Add("destroyed"));
            Object.Destroy(root);
            yield return null;
            Assert.Throws<ObjectDisposedException>(() => listener.Register<PointerEventData>(EventType.OnClick, _ => { }));
        }

        [UnityTest]
        public IEnumerator ListenerRejectsWrongNativePayloadBeforeCreatingACodeChannel()
        {
            var listener = Root().AddComponent<EventListener>();
            Assert.Throws<ArgumentException>(() => listener.Register<int>(EventType.OnClick, _ => { }));
            var calls = 0;
            var token = listener.Register<PointerEventData>(EventType.OnClick, _ => calls++);
            listener.OnPointerClick(null);
            Assert.That(calls, Is.EqualTo(1));
            token.Unregister();
            yield return null;
        }
    }
}
