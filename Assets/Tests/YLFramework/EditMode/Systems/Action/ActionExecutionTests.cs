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

        [TestCase("condition")]
        [TestCase("branchCondition")]
        [TestCase("branchSetting")]
        [TestCase("task")]
        [TestCase("coroutine")]
        [TestCase("dynamicDelay")]
        [TestCase("custom")]
        [TestCase("typedCustom")]
        public void RequiredDelegatesAreRejectedBeforeAllocation(string input)
        {
            var id = Kit.IdGenerator;
            Assert.Throws<ArgumentNullException>(() =>
            {
                switch (input)
                {
                    case "condition":
                        Own(ConditionAction.Allocate(null));
                        break;
                    case "branchCondition":
                        Own(ConditionSequence.Allocate(null, _ => { }));
                        break;
                    case "branchSetting":
                        Own(ConditionSequence.Allocate(() => false, null));
                        break;
                    case "task":
                        Own(TaskAction.Allocate(null));
                        break;
                    case "coroutine":
                        Own(Kit.Coroutine(null));
                        break;
                    case "dynamicDelay":
                        Own(Delay.Allocate((Func<float>)null));
                        break;
                    case "custom":
                        Own(Kit.Custom(null));
                        break;
                    case "typedCustom":
                        Own(Kit.Custom<int>(null));
                        break;
                }
            });
            Assert.That(Kit.IdGenerator, Is.EqualTo(id), "Invalid input must not consume a pool lease.");
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void NonFiniteDurationsAreRejectedBeforeAllocation(float value)
        {
            var id = Kit.IdGenerator;
            Assert.Throws<ArgumentOutOfRangeException>(() => Own(Kit.Delay(value, null)));
            Assert.Throws<ArgumentOutOfRangeException>(() => Own(Lerp.Allocate(0, 1, value)));
            Assert.That(Kit.IdGenerator, Is.EqualTo(id));
            var evaluations = 0;
            var dynamic = Own(Delay.Allocate(() => { evaluations++; return value; }));
            Assert.Throws<ArgumentOutOfRangeException>(() => dynamic.Execute(0));
            Assert.That(evaluations, Is.EqualTo(1));
        }

        [Test]
        public void UnsupportedRepeatCountsAreRejectedBeforeAllocation()
        {
            var id = Kit.IdGenerator;
            Assert.Throws<ArgumentOutOfRangeException>(() => Own(Kit.Repeat(-2)));
            Assert.That(Kit.IdGenerator, Is.EqualTo(id));
        }

        [Test]
        public void InvalidStartDoesNotInitializeOrConsumeTheCandidate()
        {
            Kit.Shutdown();
            var candidate = Own(Kit.Condition(() => false));
            Assert.Throws<ArgumentNullException>(() => candidate.Start(null));
            Assert.That(Kit.IsInitialized, Is.False);
            Assert.That(candidate.IsDeinitialized, Is.False);
            Assert.Throws<ArgumentNullException>(() => ((IAction)null).Start(_executor));
            Assert.That(Kit.IsInitialized, Is.False);
        }

        [Test]
        public void InvalidUpdateModeCannotChangeTheStandardController()
        {
            var controller = Track(Own(Kit.Condition(() => false)).Start(_executor));
            Assert.Throws<ArgumentOutOfRangeException>(() => controller.UpdateMode = (ActionUpdateModes)42);
            Assert.That(controller.UpdateMode, Is.EqualTo(ActionUpdateModes.ScaledDeltaTime));
            Assert.That(_executor.ScheduledCount, Is.EqualTo(1));
        }

        private sealed class CustomController : IActionController
        {
            public ulong ActionId
            {
                get; set;
            }
            public IAction Action
            {
                get; set;
            }
            public ActionUpdateModes UpdateMode
            {
                get; set;
            }
            public bool IsPaused
            {
                get => Action.IsPaused; set => Action.IsPaused = value;
            }
            public void Reset() => Action.Reset();
            public void Deinit() => Action.Deinit();
            public void Recycle() => Action = null;
        }

        private ISequence Combination(string kind) => Own(kind == "sequence" ? Kit.Sequence() :
            kind == "parallel" ? (ISequence)Kit.Parallel() : Kit.Repeat(1));

        [Test]
        public void AnonymousSafeStartExposesExactlyTheThreePlannedSignatures()
        {
            var extensions = typeof(YLFramework.Actions.IActionExtensions);
            foreach (var name in new[] { "StartHandle", "StartHandleCurrentScene", "StartHandleGlobal" })
            {
                var parameters = name == "StartHandle"
                    ? new[] { typeof(IAction), typeof(MonoBehaviour), typeof(Action) }
                    : new[] { typeof(IAction), typeof(Action) };
                var method = extensions.GetMethod(name, parameters);
                Assert.That(method, Is.Not.Null, name);
                Assert.That(method.ReturnType, Is.EqualTo(typeof(ActionHandle)));
                Assert.That(method.GetParameters()[parameters.Length - 1].IsOptional, Is.True);
            }
        }

        private static object HandleField(ActionHandle handle, string name) => typeof(ActionHandle)
            .GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(handle);

        [Test]
        public void AnonymousHandleIsSafeAfterTaskActionAndControllerReuseIncludingZeroId()
        {
            var action = Own(Kit.Condition(() => false));
            action.ActionId = 0;
            var old = action.StartHandle(_executor);
            var controller = (ActionController)HandleField(old, "_controller");
            var task = HandleField(old, "_task");
            Assert.That(old.IsActive && old.Pause(), Is.True);
            controller.Reset();
            Assert.That(old.IsActive && old.Resume(), Is.True);
            Assert.Throws<ArgumentOutOfRangeException>(() => old.SetUpdateMode((ActionUpdateModes)42));
            Assert.That(old.SetUpdateMode(ActionUpdateModes.UnscaledDeltaTime), Is.True);
            Assert.That(old.Cancel(), Is.True);
            ActionQueue.Drain();
            var replacement = Own(Kit.Condition(() => false));
            Assert.That(replacement, Is.SameAs(action));
            replacement.ActionId = 0;
            var current = replacement.StartHandle(_executor);
            Assert.That(HandleField(current, "_controller"), Is.SameAs(controller));
            Assert.That(HandleField(current, "_task"), Is.SameAs(task));
            Assert.That(old.IsActive || old.Pause() || old.Resume() || old.Cancel() ||
                old.SetUpdateMode((ActionUpdateModes)42), Is.False);
            Assert.That(current.IsActive, Is.True);
            Assert.That(replacement.IsPaused, Is.False);
            Assert.That(controller.UpdateMode, Is.EqualTo(ActionUpdateModes.ScaledDeltaTime));
        }

        [Test]
        public void AnonymousStartDoesNotUseNameRegistrationAndCoexistsWithNamedExecution()
        {
            var handle = Own(Kit.Condition(() => false)).StartHandle(_executor);
            var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            Assert.That(typeof(MonoUpdateActionExecutor).GetField("_namedTasks", fields).GetValue(_executor), Is.Null);
            Assert.That(typeof(MonoUpdateActionExecutor).GetField("_nextNameToken", fields).GetValue(_executor), Is.EqualTo(0UL));
            var named = Own(Kit.Condition(() => false)).StartNamed(_executor, "named");
            Assert.That(handle.Cancel(), Is.True);
            Assert.That(named.IsActive, Is.True);
            Assert.That(Kit.ContainsNamed(_executor, "named"), Is.True);
        }

        [Test]
        public void SynchronousCompletionAndCancellationReentrancyCannotConfuseAnonymousHandles()
        {
            ActionHandle replacement = default;
            var completions = 0;
            var completed = Own(Kit.Callback(null)).StartHandle(_executor, () =>
            {
                completions++;
                replacement = Own(Kit.Condition(() => false)).StartHandle(_executor);
            });
            Assert.That(completions, Is.EqualTo(1));
            Assert.That(completed.IsActive || completed.Cancel(), Is.False);
            Assert.That(replacement.IsActive, Is.True);
            var child = Own(CleanupProbe.Allocate());
            ActionHandle reentrant = default;
            child.Cleanup = () => reentrant = Own(Kit.Condition(() => false)).StartHandle(_executor);
            var cancelled = child.StartHandle(_executor, () => completions++);
            Assert.That(cancelled.Cancel(), Is.True);
            Assert.That(cancelled.IsActive || cancelled.Cancel(), Is.False);
            Assert.That(reentrant.IsActive, Is.True);
            Assert.That(completions, Is.EqualTo(1));
            Kit.Shutdown();
            Assert.That(reentrant.IsActive || replacement.Cancel(), Is.False);
        }

        public sealed class CleanupProbe : AbstractAction<CleanupProbe>
        {
            internal Action Cleanup;
            protected override void OnDeinit()
            {
                var callback = Cleanup;
                Cleanup = null;
                callback?.Invoke();
            }
        }

        public sealed class RejectingSequence : AbstractAction<RejectingSequence>, ISequence
        {
            internal IAction Rejected;
            internal Exception Failure;
            public ISequence Append(IAction action)
            {
                Rejected = action;
                throw Failure;
            }
            protected override void OnDeinit()
            {
                Rejected = null;
                Failure = null;
            }
        }

        [TestCase("callback")]
        [TestCase("condition")]
        [TestCase("branch")]
        [TestCase("delay")]
        [TestCase("dynamicDelay")]
        [TestCase("frames")]
        [TestCase("nextFrame")]
        [TestCase("lerp")]
        [TestCase("lerp01")]
        [TestCase("task")]
        [TestCase("coroutine")]
        [TestCase("observable")]
        public void RejectedLeafExtensionCleansOnlyItsNewChildAndPreservesTheAppendError(string kind)
        {
            var parent = Own(RejectingSequence.Allocate());
            var original = new InvalidOperationException("append rejected before transfer");
            parent.Failure = original;
            var invoked = false;
            var error = Assert.Throws<InvalidOperationException>(() =>
            {
                switch (kind)
                {
                    case "callback":
                        parent.Callback(() => invoked = true);
                        break;
                    case "condition":
                        parent.Condition(() => invoked = true);
                        break;
                    case "branch":
                        parent.ConditionSequence(() => invoked = true, _ => invoked = true);
                        break;
                    case "delay":
                        parent.Delay(1, () => invoked = true);
                        break;
                    case "dynamicDelay":
                        parent.Delay(() => { invoked = true; return 1; });
                        break;
                    case "frames":
                        parent.DelayFrame(1);
                        break;
                    case "nextFrame":
                        parent.NextFrame();
                        break;
                    case "lerp":
                        parent.Lerp(0, 1, 1, _ => invoked = true);
                        break;
                    case "lerp01":
                        parent.Lerp01(1, _ => invoked = true);
                        break;
                    case "task":
                        parent.Task(() => { invoked = true; return System.Threading.Tasks.Task.CompletedTask; });
                        break;
                    case "coroutine":
                        parent.Coroutine(() => { invoked = true; return null; });
                        break;
                    case "observable":
                        parent.UniRx<int>(() => { invoked = true; return null; });
                        break;
                }
            });
            var child = Own(parent.Rejected);
            Assert.That(error, Is.SameAs(original));
            Assert.That(child, Is.Not.Null);
            Assert.That(child.IsDeinitialized, Is.True);
            Assert.That(parent.IsDeinitialized, Is.False);
            Assert.That(invoked, Is.False, "Rejected children must never be started.");
        }

        [Test]
        public void LeafExtensionAfterParentDeinitReturnsItsConditionLeaseToThePool()
        {
            var seed = Own(ConditionAction.Allocate(() => false));
            seed.Deinit();
            ActionQueue.Drain();
            var parent = Own(Kit.Sequence());
            parent.Deinit();
            Assert.Throws<InvalidOperationException>(() => parent.Condition(() => false));
            Assert.That(seed.IsDeinitialized, Is.True, "The allocated child rejected by Append still needs cleanup.");
            ActionQueue.Drain();
            Assert.That(Own(ConditionAction.Allocate(() => false)), Is.SameAs(seed));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RejectedAudioLeafReleasesItsActionWithoutDestroyingTheBorrowedClip(bool withOptions)
        {
            var parent = Own(RejectingSequence.Allocate());
            var original = new InvalidOperationException("audio append rejected");
            parent.Failure = original;
            var clip = AudioClip.Create("RejectedActionBorrow", 16, 1, 8000, false);
            try
            {
                var error = Assert.Throws<InvalidOperationException>(() =>
                {
                    if (withOptions)
                        YLFramework.Audio.PlaySoundExtension.PlaySoundWithOptions(parent, clip, null);
                    else
                        YLFramework.Audio.PlaySoundExtension.PlaySound(parent, clip);
                });
                var child = Own(parent.Rejected);
                Assert.That(error, Is.SameAs(original));
                Assert.That(child.IsDeinitialized, Is.True);
                Assert.That(clip != null, Is.True);
                Assert.That(parent.IsDeinitialized, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        [TestCase("sequence")]
        [TestCase("parallel")]
        [TestCase("repeat")]
        public void AppendRejectsInvalidAndDuplicateChildrenWithoutTakingTheirOwnership(string kind)
        {
            var parent = Combination(kind);
            Assert.Throws<ArgumentNullException>(() => parent.Append(null));
            Assert.Throws<InvalidOperationException>(() => parent.Append(parent));
            var released = Own(Kit.Callback(null));
            released.Deinit();
            Assert.Throws<InvalidOperationException>(() => parent.Append(released));
            var child = Own(Kit.Condition(() => false));
            parent.Append(child);
            Assert.Throws<InvalidOperationException>(() => parent.Append(child));
            var running = Own(Kit.Condition(() => false));
            running.Start(_executor);
            Assert.Throws<InvalidOperationException>(() => parent.Append(running));
            Assert.That(running.IsDeinitialized, Is.False);
            parent.Deinit();
            var unowned = Own(Kit.Callback(null));
            Assert.Throws<InvalidOperationException>(() => parent.Append(unowned));
            Assert.That(unowned.IsDeinitialized, Is.False);
        }

        [TestCase("sequence")]
        [TestCase("parallel")]
        [TestCase("repeat")]
        public void AppendDuringDeinitCannotLeakIntoTheNextPoolLease(string kind)
        {
            var parent = Combination(kind);
            var unowned = Own(Kit.Callback(null));
            var rejected = false;
            var child = Own(CleanupProbe.Allocate());
            child.Cleanup = () =>
            {
                Assert.Throws<InvalidOperationException>(() => parent.Append(unowned));
                rejected = true;
            };
            parent.Append(child);
            parent.Deinit();
            Assert.That(rejected, Is.True);
            ActionQueue.Drain();
            var replacement = Combination(kind);
            replacement.Deinit();
            Assert.That(unowned.IsDeinitialized, Is.False);
        }

        [TestCase("sequence")]
        [TestCase("parallel")]
        [TestCase("repeat")]
        [TestCase("infiniteRepeat")]
        [TestCase("custom")]
        [TestCase("typedCustom")]
        public void ConfigurationFailureCleansNewResponsibilityAndKeepsThePrimaryException(string kind)
        {
            var parent = Own(Kit.Sequence());
            var original = new InvalidOperationException("configuration");
            IAction created = null;
            var custom = kind == "custom" || kind == "typedCustom";
            var child = custom ? null : Own(FaultProbe.Allocate());
            if (child != null)
                child.CleanupError = new InvalidOperationException("cleanup");
            Action<ISequence> configure = nested =>
            {
                created = nested;
                nested.Append(child);
                throw original;
            };
            var error = Assert.Throws<InvalidOperationException>(() =>
            {
                switch (kind)
                {
                    case "sequence":
                        parent.Sequence(configure);
                        break;
                    case "parallel":
                        parent.Parallel(configure);
                        break;
                    case "repeat":
                        parent.Repeat(2, configure);
                        break;
                    case "infiniteRepeat":
                        parent.Repeat(configure);
                        break;
                    case "custom":
                        Kit.Custom(api => { created = (IAction)api; api.Data = new object(); throw original; });
                        break;
                    case "typedCustom":
                        Kit.Custom<int>(api => { created = (IAction)api; api.Data = 42; throw original; });
                        break;
                }
            });
            if (created != null)
                Own(created);
            Assert.That(error, Is.SameAs(original));
            Assert.That(created.IsDeinitialized, Is.True);
            if (child != null)
            {
                Assert.That(child.IsDeinitialized, Is.True);
                Assert.That(error.Data["ActionKit.CleanupException"], Is.InstanceOf<Exception>());
            }
            Assert.That(parent.IsDeinitialized, Is.False);
        }

        [Test]
        public void NullNestedConfigurationDoesNotAllocateAndRejectedAppendCleansTheNewChild()
        {
            var parent = Own(Kit.Sequence());
            var id = Kit.IdGenerator;
            Assert.Throws<ArgumentNullException>(() => parent.Sequence(null));
            Assert.Throws<ArgumentNullException>(() => parent.Parallel(null));
            Assert.Throws<ArgumentNullException>(() => parent.Repeat((Action<ISequence>)null));
            Assert.That(Kit.IdGenerator, Is.EqualTo(id));
            parent.Deinit();
            IAction nested = null;
            Assert.Throws<InvalidOperationException>(() => parent.Sequence(value => nested = value));
            Assert.That(nested.IsDeinitialized, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InvalidCustomUpdateModeNeverLeavesASilentScheduledTask(bool duringExecution)
        {
            var action = Own(Kit.Condition(() => false));
            var controller = Track(new CustomController { Action = action, ActionId = action.ActionId });
            var completed = 0;
            if (duringExecution)
            {
                _executor.Execute(controller, _ => completed++);
                controller.UpdateMode = (ActionUpdateModes)42;
                Assert.Throws<ArgumentOutOfRangeException>(() => _executor.Tick(0, 0));
                Assert.That(action.IsDeinitialized, Is.True);
            }
            else
            {
                controller.UpdateMode = (ActionUpdateModes)42;
                Assert.Throws<ArgumentOutOfRangeException>(() => _executor.Execute(controller));
                Assert.That(action.IsDeinitialized, Is.False);
            }
            Assert.That(_executor.ScheduledCount, Is.Zero);
            Assert.That(completed, Is.Zero);
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
