using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Actions;
using YLFramework.Events;
using YLFramework.Foundation;
using Kit = YLFramework.Actions.ActionKit;
using Object = UnityEngine.Object;
using YLFramework.Async;
using YLFramework.Update;

namespace YLFramework.Tests.Actions
{
    public class GlobalMessagePlayModeTests
    {
        private readonly List<GameObject> _roots = new List<GameObject>();
        private readonly List<Action> _cleanup = new List<Action>();
        private float _timeScale;
        private bool _ownsEnvironment;
        private bool _ownsActionSession;
        private UpdateManager _previousUpdateHost;

        [SetUp]
        public void SetUp()
        {
            _ownsActionSession = false;
            var canOwnActionSession = ActionFixtureSession.CanOwnUninitializedSession();
            _timeScale = Time.timeScale;
            _ownsEnvironment = false;
            _previousUpdateHost = Object.FindObjectOfType<UpdateManager>(true);
            Assert.That(canOwnActionSession, Is.True,
                "This fixture must exclusively own ActionKit before Shutdown or session reset.");
            Assert.That(_previousUpdateHost, Is.Null, "Session reset must not terminate another public Update owner.");
            Assert.That(Object.FindObjectOfType<CoroutineStarter>(true), Is.Null,
                "Session reset must not terminate another coroutine owner.");
            Assert.That(Object.FindObjectOfType<ActionGlobalHost>(true) == null, Is.True,
                "This fixture must own the module global host; it will not modify another owner's subscriptions.");
            _ownsEnvironment = true;
            _ownsActionSession = canOwnActionSession;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_ownsEnvironment)
            {
                yield break;
            }

            Time.timeScale = _timeScale;
            foreach (var cleanup in _cleanup)
            {
                cleanup();
            }

            _cleanup.Clear();
            var global = Object.FindObjectOfType<ActionGlobalHost>(true);
            if (global)
            {
                _roots.Add(global.gameObject);
            }

            foreach (var root in _roots)
            {
                if (root)
                {
                    Object.Destroy(root);
                }
            }
            _roots.Clear();
            yield return null;
            ActionGlobalHost.ResetSession();
            if (_ownsActionSession)
            {
                try
                {
                    Kit.Shutdown();
                }
                finally
                {
                    _ownsActionSession = false;
                }
                yield return null;
            }
            ActionFixtureSession.ReleaseIdleUpdateHost(_previousUpdateHost);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ThreeGlobalPhasesUseThePublicSourceAndRespectHostActivation()
        {
            Time.timeScale = 1;
            var counts = new int[3];
            Kit.OnUpdate.Register(() => counts[0]++);
            Kit.OnFixedUpdate.Register(() => counts[1]++);
            Kit.OnLateUpdate.Register(() => counts[2]++);
            var host = ActionGlobalHost.Instance;
            var source = Object.FindObjectOfType<UpdateManager>(true);
            Assert.That(source, Is.Not.Null, "Action 三相位应复用公共源。");
            yield return new WaitForFixedUpdate();
            yield return null;
            yield return null;
            Assert.That(counts[0] > 0 && counts[1] > 0 && counts[2] > 0, Is.True);
            try
            {
                source.enabled = false;
                var before = (int[])counts.Clone();
                yield return new WaitForFixedUpdate();
                yield return null;
                yield return null;
                CollectionAssert.AreEqual(before, counts, "不得残留独立的三相位转发。");
            }
            finally
            {
                source.enabled = true;
            }
            foreach (var inactiveObject in new[] { false, true })
            {
                if (inactiveObject)
                {
                    host.gameObject.SetActive(false);
                }
                else
                {
                    host.enabled = false;
                }

                var before = (int[])counts.Clone();
                yield return new WaitForFixedUpdate();
                yield return null;
                yield return null;
                CollectionAssert.AreEqual(before, counts);
                if (inactiveObject)
                {
                    host.gameObject.SetActive(true);
                }
                else
                {
                    host.enabled = true;
                }

                var updateBefore = counts[0];
                yield return null;
                yield return null;
                Assert.That(counts[0], Is.EqualTo(updateBefore + 2), "恢复后只有一份桥接。");
            }
        }

        [UnityTest]
        public IEnumerator PublicSourceContinuesAfterActionFailFastAndActionCleanupIsLocal()
        {
            var eventAfterFailure = 0;
            var otherDomain = 0;
            var update = Kit.OnUpdate;
            var failing = update.Register(() => throw new InvalidOperationException("Action bridge expected"));
            update.Register(() => eventAfterFailure++);
            var token = UpdateManager.AddUpdateEvent(() => otherDomain++);
            _cleanup.Add(token.Unregister);
            var source = Object.FindObjectOfType<UpdateManager>(true);
            source.enabled = false;
            LogAssert.Expect(LogType.Exception, new Regex("Action bridge expected"));
            source.SendMessage("Update");
            Assert.That(eventAfterFailure, Is.Zero, "Action 内部仍为 fail-fast。");
            Assert.That(otherDomain, Is.EqualTo(1), "公共广播继续其他领域。");
            failing.Unregister();
            update.UnregisterAll();
            Kit.Shutdown();
            source.SendMessage("Update");
            Assert.That(otherDomain, Is.EqualTo(2));
            source.enabled = true;
            yield return null;
        }

        [UnityTest]
        public IEnumerator ReenableDuringPublicSnapshotRejectsTheOldBridgeRound()
        {
            var calls = 0;
            ActionGlobalHost host = null;
            var token = UpdateManager.AddUpdateEvent(() =>
            {
                host.enabled = false;
                host.enabled = true;
            });
            _cleanup.Add(token.Unregister);
            Kit.OnUpdate.Register(() => calls++);
            host = ActionGlobalHost.Instance;
            var source = Object.FindObjectOfType<UpdateManager>(true);
            source.enabled = false;
            source.SendMessage("Update");
            Assert.That(calls, Is.Zero, "普通退订仍在公共快照中，旧轮次必须自行拒绝。");
            token.Unregister();
            source.SendMessage("Update");
            Assert.That(calls, Is.EqualTo(1));
            source.enabled = true;
            yield return null;
        }

        [UnityTest]
        public IEnumerator QuitClosesCreationBeforeCallbacksAndOldHostCannotCloseNewSession()
        {
            var session = typeof(UpdateManager).Assembly.GetType("YLFramework.Singletons.SingletonSessionState");
            var reset = session.GetMethod("ResetForNewSession", BindingFlags.Static | BindingFlags.NonPublic);
            var host = ActionGlobalHost.Instance;
            var created = false;
            var coroutineStarted = false;
            host.OnApplicationQuitEvent.Register(() =>
            {
                Kit.Shutdown();
                created = ActionGlobalHost.Instance != null;
                coroutineStarted = CoroutineStarter.Start(Empty()) != null;
                UpdateManager.AddUpdateEvent(() => { });
            });
            try
            {
                host.SendMessage("OnApplicationQuit");
                Assert.That(created || coroutineStarted, Is.False, "退出通知之前必须关闭创建门禁。");
                reset.Invoke(null, null);
                var next = ActionGlobalHost.Instance;
                host.SendMessage("OnApplicationQuit");
                var calls = 0;
                Kit.OnUpdate.Register(() => calls++);
                yield return null;
                yield return null;
                Assert.That(ActionGlobalHost.Instance, Is.SameAs(next));
                Assert.That(calls, Is.GreaterThan(0), "旧宿主 Quit 不应关闭新会话。");
            }
            finally
            {
                reset.Invoke(null, null);
            }
        }

        private static IEnumerator Empty()
        {
            yield break;
        }

        [UnityTest]
        public IEnumerator SevenPropertiesAreStableModuleOwnedEventsOnOneHost()
        {
            Assert.That(Kit.OnUpdate, Is.SameAs(Kit.OnUpdate));
            Assert.That(Kit.OnFixedUpdate, Is.SameAs(Kit.OnFixedUpdate));
            Assert.That(Kit.OnLateUpdate, Is.SameAs(Kit.OnLateUpdate));
            Assert.That(Kit.OnGUI, Is.SameAs(Kit.OnGUI));
            Assert.That(Kit.OnApplicationFocus, Is.SameAs(Kit.OnApplicationFocus));
            Assert.That(Kit.OnApplicationPause, Is.SameAs(Kit.OnApplicationPause));
            Assert.That(Kit.OnApplicationQuit, Is.SameAs(Kit.OnApplicationQuit));
            Assert.That(Kit.OnUpdate.GetType().Namespace, Is.EqualTo("YLFramework.Events"));
            Assert.That(Object.FindObjectsOfType<ActionGlobalHost>(true).Length, Is.EqualTo(1));
            yield return null;
        }

        [UnityTest]
        public IEnumerator NativeUpdateFixedAndLateMessagesRunAndCanBeUnregistered()
        {
            Time.timeScale = 1;
            var updates = 0;
            var fixedUpdates = 0;
            var lateUpdates = 0;
            var update = Kit.OnUpdate;
            var fixedUpdate = Kit.OnFixedUpdate;
            var lateUpdate = Kit.OnLateUpdate;
            Action onUpdate = () => updates++;
            Action onFixed = () => fixedUpdates++;
            Action onLate = () => lateUpdates++;
            update.Register(onUpdate);
            fixedUpdate.Register(onFixed);
            lateUpdate.Register(onLate);
            _cleanup.Add(() => update.Unregister(onUpdate));
            _cleanup.Add(() => fixedUpdate.Unregister(onFixed));
            _cleanup.Add(() => lateUpdate.Unregister(onLate));
            yield return new WaitForFixedUpdate();
            yield return null;
            yield return null;
            Assert.That(updates, Is.GreaterThan(0));
            Assert.That(fixedUpdates, Is.GreaterThan(0));
            Assert.That(lateUpdates, Is.GreaterThan(0));
            update.Unregister(onUpdate);
            fixedUpdate.Unregister(onFixed);
            lateUpdate.Unregister(onLate);
            var counts = new[] { updates, fixedUpdates, lateUpdates };
            yield return null;
            yield return null;
            CollectionAssert.AreEqual(counts, new[] { updates, fixedUpdates, lateUpdates });
        }

        [UnityTest]
        public IEnumerator GuiFocusPauseAndQuitHandlersForwardTheirValuesWithoutQuittingUnity()
        {
            var gui = 0;
            var focus = new List<bool>();
            var pause = new List<bool>();
            var quit = 0;
            var update = Kit.OnUpdate;
            var host = ActionGlobalHost.Instance;
            host.enabled = false;
            Action guiCallback = () => gui++;
            Action<bool> focusCallback = focus.Add;
            Action<bool> pauseCallback = pause.Add;
            Action quitCallback = () =>
            {
                Assert.That(Kit.OnUpdate, Is.SameAs(update));
                quit++;
            };
            host.OnGuiEvent.Register(guiCallback);
            host.OnApplicationFocusEvent.Register(focusCallback);
            host.OnApplicationPauseEvent.Register(pauseCallback);
            host.OnApplicationQuitEvent.Register(quitCallback);
            _cleanup.Add(() => host.OnGuiEvent.Unregister(guiCallback));
            _cleanup.Add(() => host.OnApplicationFocusEvent.Unregister(focusCallback));
            _cleanup.Add(() => host.OnApplicationPauseEvent.Unregister(pauseCallback));
            _cleanup.Add(() => host.OnApplicationQuitEvent.Unregister(quitCallback));
            host.SendMessage("OnGUI");
            host.SendMessage("OnApplicationFocus", false);
            host.SendMessage("OnApplicationFocus", true);
            host.SendMessage("OnApplicationPause", true);
            host.SendMessage("OnApplicationPause", false);
            // 模拟 Quit 会关闭共享门禁；恢复本测试拥有的会话，不能污染后续用例。
            var session = typeof(UpdateManager).Assembly.GetType("YLFramework.Singletons.SingletonSessionState");
            try
            {
                host.SendMessage("OnApplicationQuit");
            }
            finally
            {
                session.GetMethod("ResetForNewSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            }
            Assert.That(gui, Is.EqualTo(1));
            CollectionAssert.AreEqual(new[] { false, true }, focus);
            CollectionAssert.AreEqual(new[] { true, false }, pause);
            Assert.That(quit, Is.EqualTo(1));
            yield return null;
        }
    }
}
