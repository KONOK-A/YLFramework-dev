using System;
using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Actions;
using YLFramework.Events;
using YLFramework.Foundation;
using Kit = YLFramework.Actions.ActionKit;
using Object = UnityEngine.Object;
using YLFramework.Update;

namespace YLFramework.Tests.Actions
{
    public class ActionKitLifecyclePlayModeTests
    {
        private Action<Kit> _previousPatch;
        private GameObject _userRoot;
        private TaskCompletionSource<bool> _pending;
        private bool _ownsActionSession;
        private UpdateManager _previousUpdateHost;

        [SetUp]
        public void SetUp()
        {
            _ownsActionSession = false;
            _previousUpdateHost = Object.FindObjectOfType<UpdateManager>(true);
            var canOwnActionSession = ActionFixtureSession.CanOwnUninitializedSession();
            _previousPatch = Kit.OnRegisterPatch;
            Assert.That(canOwnActionSession, Is.True,
                "This fixture must exclusively own an uninitialized ActionKit session; it will not shut down another owner's state.");
            _ownsActionSession = canOwnActionSession;
            Kit.Shutdown();
            Kit.OnRegisterPatch = _ => { };
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_ownsActionSession)
            {
                yield break;
            }

            try
            {
                Kit.Shutdown();
            }
            finally
            {
                _ownsActionSession = false;
                _pending?.TrySetResult(true);
                _pending = null;
                Kit.OnRegisterPatch = _previousPatch;
                if (_userRoot)
                {
                    Object.Destroy(_userRoot);
                }
            }
            yield return null;
            yield return null;
            ActionFixtureSession.ReleaseIdleUpdateHost(_previousUpdateHost);
            yield return null;
        }

        [UnityTest]
        public IEnumerator InitializationPatchHostConnectsOnlyAfterSuccessfulCommit()
        {
            var calls = 0;
            ActionGlobalHost host = null;
            Kit.OnRegisterPatch = _ =>
            {
                Kit.OnUpdate.Register(() => calls++);
                host = ActionGlobalHost.Instance;
                Assert.That(Object.FindObjectOfType<UpdateManager>(true), Is.SameAs(_previousUpdateHost),
                    "配置回调中取得的宿主尚未提交，不应连接公共源。");
                throw new InvalidOperationException("expected patch failure");
            };
            Assert.Throws<InvalidOperationException>(Kit.Initialize);
            Assert.That(Kit.IsInitialized, Is.False);
            yield return null;
            yield return null;
            Assert.That(calls, Is.Zero, "初始化失败期间不得自动广播。");
            Kit.OnRegisterPatch = _ => { };
            Kit.Initialize();
            Assert.That(ActionGlobalHost.Instance, Is.SameAs(host));
            yield return null;
            var before = calls;
            yield return null;
            Assert.That(calls, Is.EqualTo(before + 1));
        }

        [UnityTest]
        public IEnumerator BorrowedInactiveGlobalHostKeepsItsGameObjectAndDoesNotDuplicateUpdates()
        {
            _userRoot = new GameObject("ActionKit.Lifecycle.BorrowedGlobal");
            _userRoot.SetActive(false);
            var host = _userRoot.AddComponent<ActionGlobalHost>();
            Assert.That(ActionGlobalHost.Instance, Is.SameAs(host));
            var calls = 0;
            Kit.OnUpdate.Register(() => calls++);
            yield return null;
            yield return null;
            Assert.That(calls, Is.Zero);
            _userRoot.SetActive(true);
            yield return null;
            var before = calls;
            yield return null;
            Assert.That(calls, Is.EqualTo(before + 1));
            Kit.Shutdown();
            var next = ActionGlobalHost.Instance;
            Assert.That(next, Is.Not.SameAs(host));
            yield return null;
            Assert.That(_userRoot != null, Is.True);
            Assert.That(host == null, Is.True);
            Assert.That(ActionGlobalHost.Instance, Is.SameAs(next));
        }

        [UnityTest]
        public IEnumerator GlobalHostAndRecycleDriverShareOnlyTheFrameworkHierarchy()
        {
            var host = ActionGlobalHost.Instance;
            ActionRecycleDriver.EnsureCreated();
            var driver = ActionRecycleDriver.Instance;
            Assert.That(host.transform.parent, Is.SameAs(driver.transform.parent));
            Assert.That(host.transform.parent.name, Is.EqualTo("Action"));
            Assert.That(host.transform.root.GetComponent<YLFramework.YLFrameworkRoot>(), Is.Not.Null);
            var finishes = 0;
            var startFrame = Time.frameCount;
            Kit.Condition(() => Time.frameCount > startFrame + 1).StartGlobal(() => finishes++);
            for (var frame = 0; frame < 10 && finishes == 0; frame++)
            {
                yield return null;
            }

            Assert.That(finishes, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ShutdownReleasesGlobalHostEventsAndAllowsImmediateRestart()
        {
            var oldEvent = Kit.OnUpdate;
            var oldCalls = 0;
            oldEvent.Register(() => oldCalls++);
            yield return null;
            Kit.Shutdown();
            var count = oldCalls;
            oldEvent.Trigger();
            Assert.That(oldCalls, Is.EqualTo(count));
            var nextEvent = Kit.OnUpdate;
            Assert.That(nextEvent, Is.Not.SameAs(oldEvent));
            var newCalls = 0;
            Action callback = () => newCalls++;
            nextEvent.Register(callback);
            yield return null;
            yield return null;
            Assert.That(Object.FindObjectsOfType<ActionGlobalHost>(true).Length, Is.EqualTo(1));
            Assert.That(newCalls, Is.GreaterThan(0));
            nextEvent.Unregister(callback);
        }

        [UnityTest]
        public IEnumerator ShutdownInsideMessageStopsOldSnapshotAndKeepsRestartedMessages()
        {
            var oldEvent = Kit.OnUpdate;
            EasyEvent nextEvent = null;
            var calls = 0;
            Action callback = () => calls++;
            oldEvent.Register(() =>
            {
                Kit.Shutdown();
                nextEvent = Kit.OnUpdate;
                nextEvent.Register(callback);
            });
            var oldToken = oldEvent.Register(callback);
            oldEvent.Trigger();
            Assert.That(calls, Is.Zero, "Ended host must not finish the old message snapshot.");
            Assert.That(nextEvent, Is.Not.SameAs(oldEvent));
            oldToken.Unregister();
            nextEvent.Trigger();
            Assert.That(calls, Is.EqualTo(1));
            yield return null;
            yield return null;
            Assert.That(calls, Is.GreaterThan(1));
            Assert.That(Object.FindObjectsOfType<ActionGlobalHost>(true).Length, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ShutdownAfterAnEventWasDisposedStillEndsOtherOwnedMessages()
        {
            var update = Kit.OnUpdate;
            var late = Kit.OnLateUpdate;
            var oldCalls = 0;
            late.Register(() => oldCalls++);
            update.Dispose();
            Assert.DoesNotThrow(Kit.Shutdown);
            late.Trigger();
            Assert.That(oldCalls, Is.Zero);
            var next = Kit.OnUpdate;
            var newCalls = 0;
            next.Register(() => newCalls++);
            yield return null;
            yield return null;
            Assert.That(newCalls, Is.GreaterThan(0));
            Assert.That(Object.FindObjectsOfType<ActionGlobalHost>(true).Length, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ShutdownDetachesPendingTaskWithoutCancellingTheExternalTask()
        {
            _pending = new TaskCompletionSource<bool>();
            var completed = 0;
            var action = Kit.Task(() => _pending.Task);
            var controller = action.StartGlobal(() => completed++);
            Kit.Shutdown();
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
            Assert.That(_pending.Task.IsCompleted, Is.False);
            _pending.SetResult(true);
            yield return null;
            yield return null;
            Assert.That(completed, Is.Zero);
        }

        [UnityTest]
        public IEnumerator CancelledTaskChildCancelsItsSequenceWithoutSuccessfulCompletion()
        {
            _pending = new TaskCompletionSource<bool>();
            var steps = 0;
            var finishes = 0;
            var parent = Kit.Sequence().Task(() => _pending.Task).Callback(() => steps++);
            var controller = parent.StartGlobal(() => finishes++);
            _pending.SetCanceled();
            for (var frame = 0; frame < 8 && !parent.IsDeinitialized; frame++)
            {
                yield return null;
            }

            Assert.That(parent.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
            Assert.That(steps, Is.Zero);
            Assert.That(finishes, Is.Zero);
        }

        [UnityTest]
        public IEnumerator CancelledTaskInsideConditionalSequenceCancelsTheOuterParent()
        {
            _pending = new TaskCompletionSource<bool>();
            var steps = 0;
            var finishes = 0;
            var parent = Kit.Sequence().ConditionSequence(() => true,
                inner => inner.Task(() => _pending.Task).Callback(() => steps++)).Callback(() => steps++);
            var controller = parent.StartGlobal(() => finishes++);
            yield return null;
            _pending.SetCanceled();
            for (var frame = 0; frame < 8 && !parent.IsDeinitialized; frame++)
            {
                yield return null;
            }

            Assert.That(parent.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
            Assert.That(steps, Is.Zero);
            Assert.That(finishes, Is.Zero);
        }

        private static IEnumerator Endless(Action step)
        {
            while (true)
            {
                step();
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator ShutdownStopsModuleCoroutinesButLeavesUserCoroutinesRunning()
        {
            _userRoot = new GameObject("ActionKit.Lifecycle.CoroutineOwner");
            var caller = _userRoot.AddComponent<ActionStartTestCaller>();
            var userSteps = 0;
            var moduleSteps = 0;
            caller.StartCoroutine(Endless(() => userSteps++));
            var action = Kit.Coroutine(() => Endless(() => moduleSteps++));
            var controller = action.Start(caller);
            yield return null;
            Assert.That(moduleSteps, Is.GreaterThan(0));
            Kit.Shutdown();
            var before = userSteps;
            var moduleBefore = moduleSteps;
            yield return null;
            yield return null;
            Assert.That(userSteps, Is.GreaterThan(before));
            Assert.That(moduleSteps, Is.EqualTo(moduleBefore));
            Assert.That(_userRoot != null, Is.True);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
        }

        [UnityTest]
        public IEnumerator ShutdownStopsOnlyTheModuleGlobalCoroutines()
        {
            var steps = 0;
            var action = Kit.Coroutine(() => Endless(() => steps++));
            action.StartGlobal();
            yield return null;
            Kit.Shutdown();
            var count = steps;
            yield return null;
            yield return null;
            Assert.That(steps, Is.EqualTo(count));
            Assert.That(action.IsDeinitialized, Is.True);
        }

        [UnityTest]
        public IEnumerator DestroyingTheCoroutineHostCancelsAnActionScheduledOnALocalCaller()
        {
            _userRoot = new GameObject("ActionKit.Lifecycle.LocalCoroutineOwner");
            var caller = _userRoot.AddComponent<ActionStartTestCaller>();
            var action = Kit.Coroutine(() => Endless(() => { }));
            var completed = 0;
            var controller = action.Start(caller, () => completed++);
            Object.Destroy(ActionGlobalHost.Instance.gameObject);
            yield return null;
            yield return null;
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
            Assert.That(completed, Is.Zero);
        }

        [UnityTest]
        public IEnumerator SameFrameLocalRestartUsesANewExecutorOnTheSameUserObject()
        {
            _userRoot = new GameObject("ActionKit.Lifecycle.SameFrameCaller");
            var caller = _userRoot.AddComponent<ActionStartTestCaller>();
            var previous = Kit.Condition(() => false);
            previous.Start(caller);
            Kit.Shutdown();
            var completed = 0;
            var current = Kit.Condition(() => true);
            var controller = current.Start(caller, () => completed++);
            for (var frame = 0; frame < 5 && completed == 0; frame++)
            {
                yield return null;
            }

            Assert.That(completed, Is.EqualTo(1));
            Assert.That(previous.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
            Assert.That(_userRoot.GetComponents<MonoUpdateActionExecutor>().Length, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ShutdownDoesNotDestroyAUserSuppliedCanvasOrCallerRoot()
        {
            _userRoot = new GameObject("ActionKit.Lifecycle.UserRoot");
            var canvas = _userRoot.AddComponent<ScreenTransitionCanvas>();
            Assert.That(ScreenTransitionCanvas.Instance, Is.SameAs(canvas));
            var caller = _userRoot.AddComponent<ActionStartTestCaller>();
            var action = Kit.Condition(() => false);
            action.Start(caller);
            Kit.Shutdown();
            yield return null;
            Assert.That(_userRoot != null, Is.True);
            Assert.That(canvas != null, Is.True);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(_userRoot.GetComponent<MonoUpdateActionExecutor>(), Is.Null);
        }

        [UnityTest]
        public IEnumerator SameFrameRestartDoesNotLoseTheNewSceneOrRecycleHost()
        {
            var previous = ActionKitCurrentScene.SceneComponent;
            var action = Kit.Condition(() => false);
            action.StartCurrentScene();
            Kit.Shutdown();
            var next = ActionKitCurrentScene.SceneComponent;
            Assert.That(next, Is.Not.SameAs(previous));
            var other = Kit.Condition(() => false);
            other.StartCurrentScene();
            yield return null;
            yield return null;
            Assert.That(ActionKitCurrentScene.CachedSceneComponent, Is.SameAs(next));
            Assert.That(next != null, Is.True);
        }

        [UnityTest]
        public IEnumerator ShutdownClearsModuleEventsButLeavesUserLifecycleTriggersAlone()
        {
            _userRoot = new GameObject("ActionKit.Lifecycle.SubscriptionOwner");
            var trigger = _userRoot.AddComponent<OnDestroyUnregisterTrigger>();
            var update = Kit.OnUpdate;
            var calls = 0;
            var token = update.Register(() => calls++);
            trigger.AddUnregister(token);
            yield return null;
            var callsBeforeShutdown = calls;
            Assert.That(callsBeforeShutdown, Is.GreaterThan(0));
            Kit.Shutdown();
            Assert.That(_userRoot != null, Is.True);
            Assert.That(_userRoot.GetComponent<OnDestroyUnregisterTrigger>() != null, Is.True);
            Assert.DoesNotThrow(() => token.Unregister());
            yield return null;
            Assert.That(calls, Is.EqualTo(callsBeforeShutdown));
        }
    }
}
