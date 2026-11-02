using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Actions;
using Kit = YLFramework.Actions.ActionKit;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Actions
{
    public class ActionExecutionTests
    {
        private readonly List<IAction> _actions = new List<IAction>();
        private readonly List<IActionController> _controllers = new List<IActionController>();
        private GameObject _root;
        private MonoUpdateActionExecutor _executor;

        [SetUp]
        public void SetUp()
        {
            ActionQueue.Drain();
            _root = new GameObject("ActionKit.Execution.EditModeTest");
            _executor = _root.AddComponent<MonoUpdateActionExecutor>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_root)
            {
                Object.DestroyImmediate(_root);
            }

            foreach (var action in _actions)
            {
                if (!action.IsDeinitialized)
                {
                    action.Deinit();
                }
            }
            foreach (var controller in _controllers)
            {
                if (controller.Action != null)
                {
                    controller.Recycle();
                }
            }
            _actions.Clear();
            _controllers.Clear();
            ActionQueue.Drain();
        }

        private T Own<T>(T action) where T : IAction
        {
            _actions.Add(action);
            return action;
        }

        private IActionController Track(IActionController controller)
        {
            _controllers.Add(controller);
            return controller;
        }

        public sealed class DeltaProbe : AbstractAction<DeltaProbe>
        {
            public float Delta;
            public int Starts;
            public int Executions;
            public bool Complete;

            public override void OnStart() => Starts++;
            public override void OnExecute(float dt)
            {
                Executions++;
                Delta = dt;
                if (Complete)
                {
                    this.Finish();
                }
            }

            protected override void OnReset()
            {
                Starts = Executions = 0;
                Delta = 0;
                Complete = false;
            }
        }

        [Test]
        public void ControllerMembersForwardToTheirActionAndRetainIdGuards()
        {
            var action = Own(DeltaProbe.Allocate());
            var controller = Track(ActionController.Allocate());
            controller.Action = action;
            controller.ActionId = action.ActionId;
            controller.IsPaused = true;
            Assert.That(action.IsPaused, Is.True);
            controller.Resume();
            Assert.That(controller.IsPaused, Is.False);
            controller.Pause();
            Assert.That(action.IsPaused, Is.True);
            action.Execute(0);
            controller.Reset();
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(action.IsPaused, Is.False);
            Assert.That(controller.IgnoreTimeScale(), Is.SameAs(controller));
            Assert.That(controller.UpdateMode, Is.EqualTo(ActionUpdateModes.UnscaledDeltaTime));
            controller.Deinit();
            Assert.That(action.IsDeinitialized, Is.True);
        }

        [Test]
        public void StaleIdsBlockAllForwardedControllerOperations()
        {
            var action = Own(DeltaProbe.Allocate());
            var controller = Track(ActionController.Allocate());
            controller.Action = action;
            controller.ActionId = action.ActionId + 1;
            action.Execute(0);
            controller.Pause();
            controller.Reset();
            controller.Deinit();
            Assert.That(action.IsPaused, Is.False);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(action.IsDeinitialized, Is.False);
            action.IsPaused = true;
            controller.Resume();
            Assert.That(action.IsPaused, Is.True);
            controller.IsPaused = false;
            Assert.That(action.IsPaused, Is.True);
        }

        [Test]
        public void ControllerRecycleClearsTheActionIdAndRestoresScaledMode()
        {
            var action = Own(Kit.Condition(() => false));
            var first = Track(ActionController.Allocate());
            first.Action = action;
            first.ActionId = action.ActionId;
            first.IgnoreTimeScale();
            first.Recycle();
            Assert.That(first.Action, Is.Null);
            Assert.That(first.ActionId, Is.Zero);
            Assert.That(first.UpdateMode, Is.EqualTo(ActionUpdateModes.ScaledDeltaTime));
            var reused = Track(ActionController.Allocate());
            Assert.That(reused, Is.SameAs(first));
            // The null-action allocation is not scheduled, so this test owns its recycling.
            reused.Recycle();
        }

        [Test]
        public void EmptyControllerOperationsAreSafeAndRecyclingIsIdempotent()
        {
            var controller = Track(ActionController.Allocate());
            Assert.DoesNotThrow(() => controller.Deinit());
            Assert.DoesNotThrow(() => controller.Reset());
            Assert.DoesNotThrow(() => controller.Pause());
            Assert.DoesNotThrow(() => controller.Resume());
            Assert.That(controller.IsPaused, Is.False);
            controller.Recycle();
            Assert.DoesNotThrow(() => controller.Recycle());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LocalStartOverloadsExecuteSynchronouslyBeforeReturning(bool typedCallback)
        {
            var calls = 0;
            var callbackObservedBeforeDeinit = false;
            var action = Own(Kit.Callback(() => calls++));
            IActionController callbackController = null;
            var controller = typedCallback
                ? Track(action.Start(_executor, value =>
                {
                    callbackController = value;
                    callbackObservedBeforeDeinit = !value.Action.IsDeinitialized;
                    calls++;
                }))
                : Track(action.Start(_executor, () => calls++));
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null, "Synchronous completion must release the controller too.");
            if (typedCallback)
            {
                Assert.That(callbackController, Is.SameAs(controller));
                Assert.That(callbackObservedBeforeDeinit, Is.True);
            }
        }

        [TestCase(ActionUpdateModes.ScaledDeltaTime, 0.25f)]
        [TestCase(ActionUpdateModes.UnscaledDeltaTime, 0.75f)]
        public void ExecutorSelectsTheConfiguredDeltaAndRecyclesAfterCompletion(ActionUpdateModes mode, float expected)
        {
            var action = Own(DeltaProbe.Allocate());
            var calls = 0;
            var controller = Track(action.Start(_executor, value =>
            {
                Assert.That(value.Action, Is.SameAs(action));
                Assert.That(value.Action.IsDeinitialized, Is.False);
                calls++;
            }));
            controller.UpdateMode = mode;
            Assert.That(action.Starts, Is.EqualTo(1));
            Assert.That(action.Executions, Is.Zero);
            action.Complete = true;
            _executor.Tick(0.25f, 0.75f);
            Assert.That(action.Delta, Is.EqualTo(expected));
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(controller.Action, Is.Null);
            _executor.Tick(1, 1);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void CancellationSkipsFinishNotificationAndIsRemovedOnTheNextTick()
        {
            var action = Own(Kit.Condition(() => false));
            var calls = 0;
            var controller = Track(action.Start(_executor, () => calls++));
            controller.Deinit();
            _executor.Tick(1, 1);
            Assert.That(calls, Is.Zero);
            Assert.That(controller.Action, Is.Null);
        }

        [Test]
        public void RestartingFinishedActionResetsBeforeTheSynchronousStart()
        {
            var starts = 0;
            var action = Own(Kit.Callback(() => starts++));
            action.Finish();
            Track(action.Start(_executor));
            Assert.That(starts, Is.EqualTo(1));
        }

        [Test]
        public void ExecuteByUpdateReturnsTheActionAndUsesTheExistingComponent()
        {
            var action = Own(Kit.Condition(() => true));
            var controller = Track(ActionController.Allocate());
            controller.Action = action;
            controller.ActionId = action.ActionId;
            var calls = 0;
            Assert.That(_executor.ExecuteByUpdate(action, controller, _ => calls++), Is.SameAs(action));
            Assert.That(_root.GetComponents<MonoUpdateActionExecutor>().Length, Is.EqualTo(1));
            _executor.Tick(0, 0);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void ReentrantStartIsPreparedForTheNextTickNotTheCurrentDictionaryIteration()
        {
            var ready = false;
            var first = Own(Kit.Condition(() => ready));
            var second = Own(DeltaProbe.Allocate());
            Track(first.Start(_executor, () => Track(second.Start(_executor))));
            ready = true;
            _executor.Tick(1, 1);
            Assert.That(second.Starts, Is.EqualTo(1));
            Assert.That(second.Executions, Is.Zero);
            _executor.Tick(2, 2);
            Assert.That(second.Executions, Is.EqualTo(1));
        }

        [Test]
        public void DuplicateStartIsRejectedWithoutLosingTheFirstController()
        {
            var ready = false;
            var firstCalls = 0;
            var secondCalls = 0;
            var action = Own(Kit.Condition(() => ready));
            var first = Track(action.Start(_executor, () => firstCalls++));
            Assert.Throws<InvalidOperationException>(() => action.Start(_executor, () => secondCalls++));
            ready = true;
            _executor.Tick(1, 1);
            Assert.That(firstCalls, Is.EqualTo(1));
            Assert.That(secondCalls, Is.Zero);
            Assert.That(first.Action, Is.Null);
            Assert.That(_executor.ScheduledCount, Is.Zero);
        }

        [Test]
        public void DestroyingTheExecutorCancelsItsActionsAndReleasesControllers()
        {
            var action = Own(Kit.Condition(() => false));
            var controller = Track(action.Start(_executor));
            Object.DestroyImmediate(_executor);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
        }

        [Test]
        public void EditorLifecycleCallbacksDoNotAutomaticallyExecuteScheduledActions()
        {
            var polls = 0;
            var action = Own(Kit.Condition(() =>
            {
                polls++;
                return true;
            }));
            var controller = Track(action.Start(_executor));
            _executor.SendMessage("Update");
            Assert.That(polls, Is.Zero);
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(controller.Action, Is.SameAs(action));
            _executor.Tick(0, 0);
            Assert.That(polls, Is.EqualTo(1));
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
        }

        [Test]
        public void SameActionCannotBeScheduledOnTwoExecutors()
        {
            var other = _root.AddComponent<MonoUpdateActionExecutor>();
            var action = Own(Kit.Condition(() => false));
            var controller = Track(action.Start(_executor));
            var second = Track(ActionController.Allocate());
            second.Action = action;
            second.ActionId = action.ActionId;
            Assert.Throws<InvalidOperationException>(() => other.Execute(second));
            Assert.That(controller.Action, Is.SameAs(action));
        }

        [Test]
        public void RestartInsideCompletionIsRejectedWithoutResettingTheFinishingAction()
        {
            var ready = false;
            var action = Own(Kit.Condition(() => ready));
            var controller = Track(action.Start(_executor, () =>
            {
                Assert.That(action.Status, Is.EqualTo(ActionStatus.Finished));
                Assert.Throws<InvalidOperationException>(() => action.Start(_executor));
                Assert.That(action.Status, Is.EqualTo(ActionStatus.Finished));
            }));
            ready = true;
            _executor.Tick(0, 0);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
        }

        public sealed class FaultProbe : AbstractAction<FaultProbe>
        {
            internal Exception StartError;
            internal Exception CleanupError;
            public override void OnStart() => throw StartError;
            protected override void OnDeinit() => throw CleanupError;
        }

        [Test]
        public void OriginalExecutionExceptionIsPreservedWhenCleanupAlsoFails()
        {
            var original = new InvalidOperationException("execution");
            var cleanup = new InvalidOperationException("cleanup");
            var action = Own(FaultProbe.Allocate());
            action.StartError = original;
            action.CleanupError = cleanup;
            var error = Assert.Throws<InvalidOperationException>(() => action.Start(_executor));
            Assert.That(error, Is.SameAs(original));
            Assert.That(error.Data["ActionKit.CleanupException"], Is.InstanceOf<AggregateException>());
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(_executor.ScheduledCount, Is.Zero);
        }

        [Test]
        public void AFailingCallbackCannotRecycleANewControllerLease()
        {
            var original = new InvalidOperationException("callback");
            var next = Own(Kit.Condition(() => false));
            IActionController replacement = null;
            var first = Own(Kit.Callback(null));
            var error = Assert.Throws<InvalidOperationException>(() => first.Start(_executor, controller =>
            {
                controller.Recycle();
                replacement = Track(next.Start(_executor));
                throw original;
            }));
            Assert.That(error, Is.SameAs(original));
            Assert.That(replacement.Action, Is.SameAs(next));
            Assert.That(next.IsDeinitialized, Is.False);
            _executor.Tick(0, 0);
            Assert.That(replacement.Action, Is.SameAs(next));
        }

        public sealed class ResetProbe : AbstractAction<ResetProbe>
        {
            internal Action ResetCallback;
            protected override void OnReset() => ResetCallback?.Invoke();
            protected override void OnDeinit() => ResetCallback = null;
        }

        [Test]
        public void ShutdownDuringFinishedActionResetIsInsideTheRegisteredExecutionScope()
        {
            var action = Own(ResetProbe.Allocate());
            action.Finish();
            action.ResetCallback = Kit.Shutdown;
            IActionController controller = null;
            Assert.DoesNotThrow(() => controller = action.Start(_executor));
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
            Assert.That(Kit.IsInitialized, Is.False);
        }

        [Test]
        public void NestedStartDuringResetIsRejectedBeforeItCanReplaceTheOuterRegistration()
        {
            var action = Own(ResetProbe.Allocate());
            action.Finish();
            action.ResetCallback = () => Assert.Throws<InvalidOperationException>(() => action.Start(_executor));
            var controller = Track(action.Start(_executor));
            Assert.That(controller.Action, Is.SameAs(action));
            Assert.That(_executor.ScheduledCount, Is.EqualTo(1));
        }

        [Test]
        public void ACancelledPooledActionCanStartANewLeaseBeforeTheOldTaskIsRemoved()
        {
            var first = Own(Kit.Condition(() => false));
            var oldController = Track(first.Start(_executor));
            first.Deinit();
            ActionQueue.Drain();
            var replacement = Own(Kit.Condition(() => false));
            Assert.That(replacement, Is.SameAs(first));
            IActionController current = null;
            Assert.DoesNotThrow(() => current = Track(replacement.Start(_executor)));
            _executor.Tick(0, 0);
            Assert.That(oldController.Action, Is.Null);
            Assert.That(current.Action, Is.SameAs(replacement));
            Assert.That(replacement.IsDeinitialized, Is.False);
        }

        [Test]
        public void PublicUpdateHelperDoesNotNotifySuccessAfterTheActionCancelsItself()
        {
            var cancelled = Own(Kit.Custom(api => api.OnStart(() =>
            {
                api.Finish();
                ((IAction)api).Deinit();
            })));
            var other = Track(ActionController.Allocate());
            other.Action = cancelled;
            other.ActionId = cancelled.ActionId;
            var calls = 0;
            Assert.That(_executor.UpdateAction(other, 0, _ => calls++), Is.True);
            Assert.That(calls, Is.Zero);
            Assert.That(cancelled.IsDeinitialized, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SequenceCancellationDuringExecuteReleasesItsControllerInTheSameTick(bool conditional)
        {
            var child = Own(Kit.Condition(() => false));
            var steps = 0;
            var finishes = 0;
            var parent = Own(conditional
                ? Kit.Sequence().ConditionSequence(() => true, inner => inner.Append(child)).Callback(() => steps++)
                : Kit.Sequence().Append(child).Callback(() => steps++));
            var controller = Track(parent.Start(_executor, () => finishes++));
            _executor.Tick(0, 0);
            child.Deinit();
            _executor.Tick(0, 0);
            Assert.That(parent.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
            Assert.That(_executor.ScheduledCount, Is.Zero);
            Assert.That(steps, Is.Zero);
            Assert.That(finishes, Is.Zero);
        }
    }
}
