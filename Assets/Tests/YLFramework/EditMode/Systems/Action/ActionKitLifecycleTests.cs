using System;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Actions;
using Kit = YLFramework.Actions.ActionKit;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Actions
{
    public class ActionKitLifecycleTests
    {
        private Action<Kit> _previousPatch;
        private GameObject _root;

        [SetUp]
        public void SetUp()
        {
            _previousPatch = Kit.OnRegisterPatch;
            Kit.Shutdown();
            Kit.OnRegisterPatch = _ => { };
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Kit.Shutdown();
            }
            finally
            {
                Kit.OnRegisterPatch = _previousPatch;
                if (_root)
                {
                    Object.DestroyImmediate(_root);
                }
            }
        }

        [Test]
        public void InitializeAndLegacyAliasRunThePatchOncePerGeneration()
        {
            var calls = 0;
            Kit observed = null;
            Kit.OnRegisterPatch = context =>
            {
                calls++;
                observed = context;
                Assert.That(Kit.Interface, Is.SameAs(context));
            };
            Kit.Initialize();
            Kit.InitArchitecture();
            Assert.That(Kit.IsInitialized, Is.True);
            Assert.That(Kit.Interface, Is.SameAs(observed));
            Assert.That(calls, Is.EqualTo(1));
            Kit.Shutdown();
            Kit.Initialize();
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void ShutdownDoesNotResetActionIdsOrTheRegistrationPatch()
        {
            var action = Kit.Callback(null);
            var nextId = Kit.IdGenerator;
            action.Deinit();
            var patch = Kit.OnRegisterPatch;
            Kit.Shutdown();
            Assert.That(Kit.IdGenerator, Is.EqualTo(nextId));
            Assert.That(Kit.OnRegisterPatch, Is.SameAs(patch));
            Assert.DoesNotThrow(() => Kit.Shutdown());
        }

        [Test]
        public void FailedInitializationIsVisibleAndCanBeRetried()
        {
            Kit.OnRegisterPatch = _ => throw new InvalidOperationException("expected");
            Assert.Throws<InvalidOperationException>(() => Kit.Initialize());
            Assert.That(Kit.IsInitialized, Is.False);
            Kit.OnRegisterPatch = _ => { };
            Assert.DoesNotThrow(() => Kit.Initialize());
            Assert.That(Kit.IsInitialized, Is.True);
        }

        [Test]
        public void AStaleContextCannotCloseTheNewGeneration()
        {
            var previous = Kit.Interface;
            previous.Deinit();
            var current = Kit.Interface;
            Assert.That(current, Is.Not.SameAs(previous));
            previous.Deinit();
            Assert.That(Kit.IsInitialized, Is.True);
            Assert.That(Kit.Interface, Is.SameAs(current));
        }

        [Test]
        public void ShutdownCancelsLocalExecutorsWithoutDestroyingTheUsersGameObject()
        {
            _root = new GameObject("ActionKit.Lifecycle.LocalOwnerTest");
            var executor = _root.AddComponent<MonoUpdateActionExecutor>();
            var calls = 0;
            var action = Kit.Condition(() => false);
            var controller = action.Start(executor, () => calls++);
            Kit.Shutdown();
            Assert.That(_root != null, Is.True);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
            Assert.That(calls, Is.Zero);
            Assert.That(_root.GetComponent<MonoUpdateActionExecutor>(), Is.Null);
        }

        [Test]
        public void ShutdownFromASynchronousStartCallbackDoesNotMutateAnActiveIterator()
        {
            _root = new GameObject("ActionKit.Lifecycle.ReentrantTest");
            var executor = _root.AddComponent<MonoUpdateActionExecutor>();
            var action = Kit.Callback(Kit.Shutdown);
            IActionController result = null;
            Assert.DoesNotThrow(() => result = action.Start(executor));
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(result.Action, Is.Null);
            Assert.That(Kit.IsInitialized, Is.False);
        }

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(1, true)]
        [TestCase(2, true)]
        public void ShutdownFromACompositeChildDoesNotRunLaterCallbacks(int composition, bool deferred)
        {
            _root = new GameObject("ActionKit.Lifecycle.CompositeShutdownTest");
            var executor = _root.AddComponent<MonoUpdateActionExecutor>();
            var calls = 0;
            var finished = 0;
            ISequence action = composition == 0 ? ActionKit.Sequence() :
                composition == 1 ? ActionKit.Parallel() : ActionKit.Repeat(2);
            if (deferred)
            {
                action.Delay(0.01f);
            }

            action.Callback(ActionKit.Shutdown).Callback(() => calls++);
            var controller = action.Start(executor, () => finished++);
            for (var tick = 0; tick < 3 && !action.IsDeinitialized; tick++)
            {
                executor.Tick(1, 1);
            }

            Assert.That(calls, Is.Zero);
            Assert.That(finished, Is.Zero);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
            Assert.That(ActionKit.IsInitialized, Is.False);
            Assert.That(ActionQueue.PendingCount, Is.Zero);
        }

        [Test]
        public void ShutdownFromAConditionPredicateDoesNotRunItsSuccessCallback()
        {
            _root = new GameObject("ActionKit.Lifecycle.ConditionShutdownTest");
            var executor = _root.AddComponent<MonoUpdateActionExecutor>();
            var calls = 0;
            var action = ActionKit.Condition(() =>
            {
                ActionKit.Shutdown();
                return true;
            }, () => calls++);
            var controller = action.Start(executor, () => calls++);
            Assert.DoesNotThrow(() => executor.Tick(1, 1));
            Assert.That(calls, Is.Zero);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
            Assert.That(ActionQueue.PendingCount, Is.Zero);
        }

        [Test]
        public void ShutdownFromCompletionDoesNotRunAnotherScheduledAction()
        {
            _root = new GameObject("ActionKit.Lifecycle.CompletionShutdownTest");
            var executor = _root.AddComponent<MonoUpdateActionExecutor>();
            var ready = false;
            var first = Kit.Condition(() => ready);
            var secondCalls = 0;
            var second = Kit.Custom(api => api.OnExecute(_ => secondCalls++));
            var firstController = first.Start(executor, Kit.Shutdown);
            var secondController = second.Start(executor);
            ready = true;
            Assert.DoesNotThrow(() => executor.Tick(1, 1));
            Assert.That(secondCalls, Is.Zero);
            Assert.That(firstController.Action, Is.Null);
            Assert.That(secondController.Action, Is.Null);
        }

        [Test]
        public void ShutdownDrainsPendingRecyclesOnlyOnce()
        {
            var calls = 0;
            var pool = new ActionPool<object>(() => new object(), _ => calls++);
            var item = pool.Allocate();
            ActionQueue.AddCallback(new ActionQueueRecycleCallback<object>(pool, item));
            Assert.That(calls, Is.Zero);
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
            Kit.Shutdown();
            Kit.Shutdown();
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(pool.Allocate(), Is.SameAs(item));
            Assert.That(ActionQueue.PendingCount, Is.Zero);
        }
    }
}
