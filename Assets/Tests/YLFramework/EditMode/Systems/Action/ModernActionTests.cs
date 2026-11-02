using System;
using System.Collections.Generic;
using NUnit.Framework;
using YLFramework.Actions;
using Kit = YLFramework.Actions.ActionKit;

namespace YLFramework.Tests.Actions
{
    public class ModernActionTests
    {
        private readonly List<IAction> _owned = new List<IAction>();

        private T Own<T>(T action) where T : IAction
        {
            _owned.Add(action);
            return action;
        }

        private sealed class DisposableData : IDisposable
        {
            public int Disposals;

            public void Dispose()
            {
                Disposals++;
            }
        }

        private sealed class ExecutionResultProbe : IAction, IActionExecutionVersion
        {
            public ulong ActionId { get; set; } = 1;
            public ActionStatus Status
            {
                get; set;
            }
            public bool IsDeinitialized
            {
                get; set;
            }
            public bool IsPaused
            {
                get; set;
            }
            public ulong ExecutionVersion
            {
                get; private set;
            }
            public Action Starting;
            public Action Executing;
            public Action Finishing;
            public int Finishes;

            public void OnStart() => Starting?.Invoke();
            public void OnExecute(float dt) => Executing?.Invoke();
            public void OnFinish()
            {
                Finishes++;
                Finishing?.Invoke();
            }
            public void Deinit() => IsDeinitialized = true;
            public void Reset()
            {
                ExecutionVersion++;
                Status = ActionStatus.NotStart;
                IsPaused = false;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExecutePreservesFinishedCancellationResultWithoutCallingTheFinishHook(bool duringStart)
        {
            var action = new ExecutionResultProbe();
            Action cancelAndFinish = () =>
            {
                action.Deinit();
                action.Finish();
            };
            if (duringStart)
            {
                action.Starting = cancelAndFinish;
            }
            else
            {
                action.Executing = cancelAndFinish;
                Assert.That(action.Execute(0), Is.False);
            }
            Assert.That(action.Execute(0), Is.True);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Finishes, Is.Zero);
            Assert.That(action.Execute(0), Is.False, "An already cancelled action must not run again.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExecuteDoesNotTreatCancellationWithoutFinishedStateAsCompletion(bool duringStart)
        {
            var action = new ExecutionResultProbe();
            if (duringStart)
            {
                action.Starting = action.Deinit;
            }
            else
            {
                action.Executing = action.Deinit;
                Assert.That(action.Execute(0), Is.False);
            }
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.Finishes, Is.Zero);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ExecuteDoesNotFinishAReplacementIdOrResetGeneration(bool duringStart, bool reset)
        {
            var action = new ExecutionResultProbe();
            Action replaceAndFinish = () =>
            {
                if (reset)
                {
                    action.Reset();
                }
                else
                {
                    action.ActionId++;
                }

                action.Finish();
            };
            if (duringStart)
            {
                action.Starting = replaceAndFinish;
            }
            else
            {
                action.Executing = replaceAndFinish;
                Assert.That(action.Execute(0), Is.False);
            }
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.Finishes, Is.Zero);
        }

        [Test]
        public void ExecuteKeepsTheCompletionResultWhenItsFinishHookDeinitializesTheSameLease()
        {
            var action = new ExecutionResultProbe { Status = ActionStatus.Finished };
            action.Finishing = action.Deinit;
            Assert.That(action.Execute(0), Is.True);
            Assert.That(action.Finishes, Is.EqualTo(1));
            Assert.That(action.IsDeinitialized, Is.True);
        }

        private sealed class ThrowingDeinitAction : AbstractAction<ThrowingDeinitAction>
        {
            public Exception Failure;

            public ThrowingDeinitAction()
            {
            }

            protected override void OnDeinit()
            {
                var failure = Failure;
                Failure = null;
                throw failure;
            }
            protected override void OnReset() => Failure = null;
        }

        [SetUp]
        public void SetUp()
        {
            ActionQueue.Drain();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var action in _owned)
            {
                if (!action.IsDeinitialized)
                {
                    action.Deinit();
                }
            }
            _owned.Clear();
            ActionQueue.Drain();
        }

        public sealed class ProbeAction : AbstractAction<ProbeAction>
        {
            public int Starts;
            public int Executions;
            public int Finishes;
            public int Deinits;
            public bool ObservedDeinitedInHook;
            public float LastDelta;
            public bool FinishOnStart;
            public bool FinishOnExecute;

            public override void OnStart()
            {
                Starts++;
                if (FinishOnStart)
                {
                    this.Finish();
                }
            }

            public override void OnExecute(float dt)
            {
                Executions++;
                LastDelta = dt;
                if (FinishOnExecute)
                {
                    this.Finish();
                }
            }

            public override void OnFinish()
            {
                Finishes++;
            }

            protected override void OnReset()
            {
                Starts = Executions = Finishes = Deinits = 0;
                LastDelta = 0;
                FinishOnStart = FinishOnExecute = ObservedDeinitedInHook = false;
            }

            protected override void OnDeinit()
            {
                Deinits++;
                ObservedDeinitedInHook = IsDeinitialized;
            }
        }

        [Test]
        public void StateAndUpdateEnumsKeepTheirNumericValues()
        {
            CollectionAssert.AreEqual(new[] { "NotStart", "Started", "Finished" }, Enum.GetNames(typeof(ActionStatus)));
            Assert.That((int)ActionStatus.NotStart, Is.Zero);
            Assert.That((int)ActionStatus.Started, Is.EqualTo(1));
            Assert.That((int)ActionStatus.Finished, Is.EqualTo(2));
            Assert.That((int)ActionUpdateModes.ScaledDeltaTime, Is.Zero);
            Assert.That((int)ActionUpdateModes.UnscaledDeltaTime, Is.EqualTo(1));
        }

        [Test]
        public void FirstExecuteOnlyStartsThenSubsequentExecuteUsesTheSuppliedDelta()
        {
            var action = Own(ProbeAction.Allocate());
            Assert.That(action.Execute(5f), Is.False);
            Assert.That(action.Starts, Is.EqualTo(1));
            Assert.That(action.Executions, Is.Zero);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            action.FinishOnExecute = true;
            Assert.That(action.Execute(0.25f), Is.True);
            Assert.That(action.LastDelta, Is.EqualTo(0.25f));
            Assert.That(action.Finishes, Is.EqualTo(1));
            Assert.That(action.IsDeinitialized, Is.False, "Manual Execute does not own Deinit.");
        }

        [Test]
        public void StartingFinishedAndStartingPausedKeepTheOriginalDistinctSemantics()
        {
            var immediate = Own(ProbeAction.Allocate());
            immediate.FinishOnStart = true;
            Assert.That(immediate.Execute(1f), Is.True);
            Assert.That(immediate.Starts, Is.EqualTo(1));
            Assert.That(immediate.Finishes, Is.EqualTo(1));

            var paused = Own(ProbeAction.Allocate());
            paused.IsPaused = true;
            Assert.That(paused.Execute(1f), Is.False);
            Assert.That(paused.Starts, Is.EqualTo(1));
            Assert.That(paused.Execute(1f), Is.False);
            Assert.That(paused.Executions, Is.Zero);
            paused.IsPaused = false;
            paused.Execute(0.5f);
            Assert.That(paused.Executions, Is.EqualTo(1));
        }

        [Test]
        public void FinishBeforeStartAndRepeatedFinishedExecutionRemainCharacterized()
        {
            var action = Own(ProbeAction.Allocate());
            action.Finish();
            Assert.That(action.Execute(0), Is.True);
            Assert.That(action.Execute(0), Is.True);
            Assert.That(action.Starts, Is.Zero);
            Assert.That(action.Finishes, Is.EqualTo(2), "AK-906: direct Execute repeats OnFinish.");
        }

        [Test]
        public void AbstractActionDeinitIsDeferredAndIdempotentButResetDoesNotReactivateIt()
        {
            var action = Own(ProbeAction.Allocate());
            var oldId = action.ActionId;
            action.Deinit();
            action.Deinit();
            Assert.That(action.Deinits, Is.EqualTo(1));
            Assert.That(action.ObservedDeinitedInHook, Is.True);
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
            action.Reset();
            Assert.That(action.IsDeinitialized, Is.True);
            var other = Own(ProbeAction.Allocate());
            Assert.That(other, Is.Not.SameAs(action));
            ActionQueue.Drain();
            var reused = Own(ProbeAction.Allocate());
            Assert.That(reused, Is.SameAs(action));
            Assert.That(reused.IsDeinitialized, Is.False);
            Assert.That(reused.ActionId, Is.Not.EqualTo(oldId));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CallbackFinishesOnItsFirstExecuteIncludingNullCallback(bool useNull)
        {
            var calls = 0;
            var action = Own(Kit.Callback(useNull ? (Action)null : () => calls++));
            Assert.That(action.Execute(0), Is.True);
            Assert.That(calls, Is.EqualTo(useNull ? 0 : 1));
            action.Reset();
            Assert.That(action.Execute(0), Is.True);
            Assert.That(calls, Is.EqualTo(useNull ? 0 : 2));
        }

        [Test]
        public void ConditionDoesNotPollDuringStartAndWaitsForItsPredicate()
        {
            var ready = false;
            var polls = 0;
            var calls = 0;
            var action = Own(Kit.Condition(() =>
            {
                polls++;
                return ready;
            }, () => calls++));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(polls, Is.Zero);
            Assert.That(action.Execute(1), Is.False);
            ready = true;
            Assert.That(action.Execute(0), Is.True);
            Assert.That(polls, Is.EqualTo(2));
            Assert.That(calls, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConditionCannotInvokeOrFinishAReplacementLease(bool duringPredicate)
        {
            IAction action = null;
            IAction replacement = null;
            var oldCalls = 0;
            var replacementCalls = 0;
            Action replace = () =>
            {
                action.Deinit();
                ActionQueue.Drain();
                replacement = Own(ActionKit.Condition(() => false, () => replacementCalls++));
                Assert.That(replacement, Is.SameAs(action));
            };
            action = Own(ActionKit.Condition(() =>
            {
                if (duringPredicate)
                {
                    replace();
                }

                return true;
            }, () =>
            {
                oldCalls++;
                if (!duringPredicate)
                {
                    replace();
                }
            }));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.Execute(1), Is.False);
            Assert.That(replacement, Is.Not.Null);
            Assert.That(replacement.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(oldCalls, Is.EqualTo(duringPredicate ? 0 : 1));
            Assert.That(replacementCalls, Is.Zero);
            Assert.That(replacement.Execute(0), Is.False);
            Assert.That(replacement.Execute(1), Is.False);
        }

        [Test]
        public void DelayChecksElapsedBeforeAccumulatingTheCurrentDelta()
        {
            var calls = 0;
            var action = Own(Kit.Delay(1f, () => calls++));
            Assert.That(action.Execute(20f), Is.False);
            Assert.That(action.Execute(1f), Is.False);
            Assert.That(calls, Is.Zero);
            Assert.That(action.Execute(0), Is.True);
            Assert.That(calls, Is.EqualTo(1));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        public void NonPositiveDelayStillRequiresAnExecutionAfterStart(float seconds)
        {
            var calls = 0;
            var action = Own(Kit.Delay(seconds, () => calls++));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.Execute(0), Is.True);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void DynamicDelayIsEvaluatedOncePerStartAndPoolReuseClearsFactory()
        {
            var evaluations = 0;
            var originalFinishes = 0;
            var seconds = 2f;
            var generator = Kit.IdGenerator;
            var action = Own(Delay.Allocate(() =>
            {
                evaluations++;
                return seconds;
            }, () => originalFinishes++));
            var originalId = action.ActionId;
            Assert.That(originalId, Is.EqualTo(generator));
            Assert.That(Kit.IdGenerator, Is.EqualTo(generator + 1));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.Execute(1f), Is.False);
            Assert.That(evaluations, Is.EqualTo(1));
            seconds = 3f;
            action.IsPaused = true;
            action.Reset();
            Assert.That(action.IsPaused, Is.False);
            Assert.That(action.CurrentSeconds, Is.Zero);
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.DelayTime, Is.EqualTo(3f));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(evaluations, Is.EqualTo(2));
            action.Deinit();
            action.Deinit();
            Assert.That(action.DelayTimeFactory, Is.Null);
            Assert.That(action.OnDelayFinish, Is.Null);
            Assert.That(originalFinishes, Is.Zero);
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
            var other = Own(Delay.Allocate(() => 9f));
            Assert.That(other, Is.Not.SameAs(action));
            Assert.That(other.ActionId, Is.Not.EqualTo(originalId));
            ActionQueue.Drain();
            var fixedFinishes = 0;
            var fixedDelay = Own((Delay)Kit.Delay(0f, () => fixedFinishes++));
            var fixedId = fixedDelay.ActionId;
            Assert.That(fixedDelay, Is.SameAs(action));
            Assert.That(fixedId, Is.Not.EqualTo(originalId));
            Assert.That(fixedDelay.IsDeinitialized, Is.False);
            Assert.That(fixedDelay.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(fixedDelay.CurrentSeconds, Is.Zero);
            Assert.That(fixedDelay.DelayTimeFactory, Is.Null);
            Assert.That(fixedDelay.Execute(0), Is.False);
            Assert.That(fixedDelay.DelayTime, Is.Zero);
            Assert.That(fixedDelay.Execute(0), Is.True);
            Assert.That(fixedFinishes, Is.EqualTo(1));
            Assert.That(originalFinishes, Is.Zero);
            Assert.That(evaluations, Is.EqualTo(2));
            fixedDelay.Deinit();
            ActionQueue.Drain();
            var dynamicDelay = Own(Delay.Allocate(() =>
            {
                evaluations++;
                return 1f;
            }));
            Assert.That(dynamicDelay, Is.SameAs(fixedDelay));
            Assert.That(dynamicDelay.ActionId, Is.Not.EqualTo(fixedId));
            Assert.That(dynamicDelay.Execute(0), Is.False);
            Assert.That(dynamicDelay.DelayTime, Is.EqualTo(1f));
            Assert.That(dynamicDelay.Execute(0), Is.False);
            Assert.That(evaluations, Is.EqualTo(3));
        }

        [Test]
        public void CancellingDelayBeforeStartClearsDelegatesWithoutInvokingThem()
        {
            var evaluations = 0;
            var finishes = 0;
            var action = Own(Delay.Allocate(() =>
            {
                evaluations++;
                return 1f;
            }, () => finishes++));
            action.Deinit();
            action.Reset();
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.DelayTimeFactory, Is.Null);
            Assert.That(action.OnDelayFinish, Is.Null);
            Assert.That(evaluations, Is.Zero);
            Assert.That(finishes, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(-2)]
        public void NonPositiveFrameDelayFinishesAfterStartWithoutWaitingForAnotherFrame(int frames)
        {
            var calls = 0;
            var action = Own(Kit.DelayFrame(frames, () => calls++));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.Execute(0), Is.True);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void LerpReportsStartIntermediateEndAndFinishInOrder()
        {
            var values = new List<float>();
            var finished = 0;
            var action = Own(Kit.Lerp(2, 6, 2, values.Add, () => finished++));
            Assert.That(action.Execute(100), Is.False);
            Assert.That(action.Execute(1), Is.False);
            Assert.That(action.Execute(1), Is.True);
            CollectionAssert.AreEqual(new[] { 2f, 4f, 6f }, values);
            Assert.That(finished, Is.EqualTo(1));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        public void NonPositiveLerpDurationReportsBothEndpoints(float duration)
        {
            var values = new List<float>();
            var action = Own(Lerp.Allocate(3f, -1f, duration, values.Add));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.Execute(0), Is.True);
            CollectionAssert.AreEqual(new[] { 3f, -1f }, values);
        }

        [Test]
        public void ForcedLerpFinishDoesNotInvokeItsInterpolationFinishCallback()
        {
            var calls = 0;
            var action = Own(Lerp.Allocate(0, 1, 1, null, () => calls++));
            action.Finish();
            Assert.That(action.Execute(0), Is.True);
            Assert.That(calls, Is.Zero);
        }

        [Test]
        public void SequenceRunsSynchronousChildrenTogetherAndDefersTheirDisposal()
        {
            var trace = new List<int>();
            var child = Own(Kit.Callback(() => trace.Add(1)));
            var sequence = Own(Kit.Sequence().Append(child).Callback(() => trace.Add(2)));
            Assert.That(sequence.Execute(0), Is.True);
            CollectionAssert.AreEqual(new[] { 1, 2 }, trace);
            Assert.That(child.IsDeinitialized, Is.False);
            sequence.Reset();
            Assert.That(sequence.Execute(0), Is.True);
            CollectionAssert.AreEqual(new[] { 1, 2, 1, 2 }, trace);
            sequence.Deinit();
            Assert.That(child.IsDeinitialized, Is.True);
        }

        [Test]
        public void NestedSequenceStartsNextChildWithZeroRatherThanRemainingDelta()
        {
            var calls = 0;
            var sequence = Own(Kit.Sequence()
                .Sequence(inner => inner.Callback(() => calls++))
                .Delay(1f, () => calls++)
                .Callback(() => calls++));
            Assert.That(sequence.Execute(10f), Is.False);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(sequence.Execute(1f), Is.False);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(sequence.Execute(0), Is.True);
            Assert.That(calls, Is.EqualTo(3));
        }

        [Test]
        public void EmptySequenceFinishesImmediatelyWhileEmptyParallelDoesNot()
        {
            var sequence = Own(Kit.Sequence());
            var parallel = Own(Kit.Parallel());
            Assert.That(sequence.Execute(0), Is.True);
            Assert.That(parallel.Execute(0), Is.False);
            Assert.That(parallel.Execute(1), Is.False, "AK-910 remains an open original issue.");
        }

        [Test]
        public void ParallelHandlesAnEarlyCompletedChildAndResetsAllChildren()
        {
            var ready = false;
            var calls = 0;
            var parallel = Own(Kit.Parallel().Condition(() => ready).Callback(() => calls++));
            Assert.That(parallel.Execute(0), Is.False);
            Assert.That(parallel.Execute(0), Is.False);
            Assert.That(calls, Is.EqualTo(1));
            ready = true;
            Assert.That(parallel.Execute(0), Is.True);
            parallel.Reset();
            Assert.That(parallel.Execute(0), Is.False);
            Assert.That(parallel.Execute(0), Is.False);
            Assert.That(parallel.Execute(0), Is.True);
            Assert.That(calls, Is.EqualTo(2));
        }

        [TestCase(1)]
        [TestCase(3)]
        public void PositiveRepeatRunsExactlyTheRequestedNumberOfRounds(int count)
        {
            var calls = 0;
            var repeat = Own(Kit.Repeat(count).Callback(() => calls++));
            Assert.That(repeat.Execute(0), Is.False);
            for (var index = 0; index < count; index++)
            {
                Assert.That(repeat.Execute(0), Is.EqualTo(index == count - 1));
            }
            Assert.That(calls, Is.EqualTo(count));
        }

        [TestCase(-1, 4)]
        [TestCase(0, 4)]
        [TestCase(-2, 0)]
        public void RepeatBoundaryBehaviorRemainsCharacterized(int count, int expectedCalls)
        {
            var calls = 0;
            var repeat = Own(Kit.Repeat(count).Callback(() => calls++));
            Assert.That(repeat.Execute(0), Is.False);
            for (var index = 0; index < 4; index++)
            {
                Assert.That(repeat.Execute(0), Is.False);
            }

            Assert.That(calls, Is.EqualTo(expectedCalls));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConditionSequenceEvaluatesTheBranchOnlyOnce(bool condition)
        {
            var polls = 0;
            var calls = 0;
            var branch = Own(ConditionSequence.Allocate(() =>
            {
                polls++;
                return condition;
            },
                inner => inner.Callback(() => calls++)));
            Assert.That(branch.Execute(0), Is.EqualTo(!condition));
            if (condition)
            {
                Assert.That(branch.Execute(0), Is.True);
            }

            Assert.That(polls, Is.EqualTo(1));
            Assert.That(calls, Is.EqualTo(condition ? 1 : 0));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CancelledConditionSequenceDeinitsItsChildExactlyOnce(bool startChild)
        {
            ISequence child = null;
            var leaf = Own(ProbeAction.Allocate());
            var branch = Own(ConditionSequence.Allocate(() => true, inner =>
            {
                child = Own(inner);
                inner.Append(leaf);
            }));
            var originalId = branch.ActionId;
            Assert.That(branch.Execute(0), Is.False);
            if (startChild)
            {
                Assert.That(branch.Execute(0), Is.False);
            }

            Assert.That(leaf.Starts, Is.EqualTo(startChild ? 1 : 0));
            branch.Deinit();
            branch.Deinit();
            Assert.That(branch.IsDeinitialized, Is.True);
            Assert.That(child.IsDeinitialized, Is.True);
            Assert.That(leaf.IsDeinitialized, Is.True);
            Assert.That(leaf.Deinits, Is.EqualTo(1));
            Assert.That(leaf.Finishes, Is.Zero);
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(3));
            branch.Reset();
            Assert.That(branch.IsDeinitialized, Is.True);
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(3));
            ActionQueue.Drain();
            var reused = Own(ConditionSequence.Allocate(() => false, inner => Assert.Fail("Cancelled settings leaked.")));
            Assert.That(reused, Is.SameAs(branch));
            Assert.That(reused.ActionId, Is.Not.EqualTo(originalId));
            Assert.That(reused.IsDeinitialized, Is.False);
            Assert.That(reused.Execute(0), Is.True);
        }

        [Test]
        public void ConditionSequenceResetDeinitsThePreviousChildBeforeReevaluating()
        {
            var ready = true;
            var children = new List<ISequence>();
            var leaves = new List<ProbeAction>();
            var branch = Own(ConditionSequence.Allocate(() => ready, inner =>
            {
                children.Add(Own(inner));
                var leaf = Own(ProbeAction.Allocate());
                leaves.Add(leaf);
                inner.Append(leaf);
            }));
            var originalId = branch.ActionId;
            Assert.That(branch.Execute(0), Is.False);
            Assert.That(branch.Execute(0), Is.False);
            Assert.That(branch.Execute(0.25f), Is.False);
            branch.IsPaused = true;
            branch.Reset();
            branch.Reset();
            Assert.That(children[0].IsDeinitialized, Is.True);
            Assert.That(leaves[0].Deinits, Is.EqualTo(1));
            Assert.That(leaves[0].Finishes, Is.Zero);
            Assert.That(branch.IsDeinitialized, Is.False);
            Assert.That(branch.IsPaused, Is.False);
            Assert.That(branch.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(branch.ActionId, Is.EqualTo(originalId));
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(2));
            ready = false;
            Assert.That(branch.Execute(0), Is.True);
            Assert.That(children.Count, Is.EqualTo(1));
            Assert.That(leaves[0].Executions, Is.EqualTo(1));
            branch.Reset();
            ready = true;
            Assert.That(branch.Execute(0), Is.False);
            Assert.That(children.Count, Is.EqualTo(2));
            Assert.That(children[1], Is.Not.SameAs(children[0]));
            Assert.That(leaves[1], Is.Not.SameAs(leaves[0]));
            Assert.That(branch.Execute(0), Is.False);
            branch.Deinit();
            Assert.That(children[1].IsDeinitialized, Is.True);
            Assert.That(leaves[1].Deinits, Is.EqualTo(1));
            Assert.That(leaves[0].Deinits, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConditionSequenceSettingFailureCleansChildAndPreservesOriginalException(bool failDuringDeinit)
        {
            var settingFailure = new InvalidOperationException("setting failed");
            var deinitFailure = new InvalidOperationException("child cleanup failed");
            ISequence child = null;
            var leaf = Own(ProbeAction.Allocate());
            var branch = Own(ConditionSequence.Allocate(() => true, inner =>
            {
                child = Own(inner);
                inner.Append(leaf);
                if (failDuringDeinit)
                {
                    var throwing = ThrowingDeinitAction.Allocate();
                    throwing.Failure = deinitFailure;
                    inner.Append(throwing);
                }
                throw settingFailure;
            }));
            var exception = Assert.Throws<InvalidOperationException>(() => branch.Execute(0));
            Assert.That(exception, Is.SameAs(settingFailure));
            Assert.That(child.IsDeinitialized, Is.True);
            Assert.That(leaf.Deinits, Is.EqualTo(1));
            Assert.That(leaf.Starts, Is.Zero);
            Assert.That(leaf.Finishes, Is.Zero);
            if (failDuringDeinit)
            {
                var cleanup = exception.Data["ConditionSequence.DeinitException"] as AggregateException;
                Assert.That(cleanup, Is.Not.Null);
                CollectionAssert.Contains(cleanup.Flatten().InnerExceptions, deinitFailure);
            }
            var pending = ActionQueue.PendingCount;
            branch.Reset();
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(pending));
            branch.Deinit();
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(pending + 1));
            Assert.That(leaf.Deinits, Is.EqualTo(1));
        }

        [Test]
        public void CustomTypedDataCallbacksAndReplacementArePreserved()
        {
            var trace = new List<string>();
            ICustomApi<int> custom = null;
            var action = Own(Kit.Custom<int>(api =>
            {
                custom = api;
                api.Data = 7;
                api.OnStart(() => trace.Add("replaced"));
                api.OnStart(() => trace.Add("start"));
                api.OnExecute(dt =>
                {
                    api.Data += (int)dt;
                    api.Finish();
                });
                api.OnFinish(() => trace.Add("finish"));
            }));
            Assert.That(action.Execute(10), Is.False);
            Assert.That(action.Execute(2), Is.True);
            Assert.That(custom.Data, Is.EqualTo(9));
            CollectionAssert.AreEqual(new[] { "start", "finish" }, trace);
            action.Reset();
            Assert.That(custom.Data, Is.EqualTo(9), "Reset keeps the current lease's user data.");
            action.Deinit();
            Assert.That(custom.Data, Is.Zero);
            CollectionAssert.AreEqual(new[] { "start", "finish" }, trace);
        }

        [Test]
        public void TypedCustomClearsUserDataWithoutDisposingItAndDefersReuse()
        {
            var data = new DisposableData();
            var calls = 0;
            ICustomApi<DisposableData> api = null;
            var action = Own(Kit.Custom<DisposableData>(value =>
            {
                api = value;
                value.Data = data;
                value.OnStart(() => calls++);
                value.OnExecute(dt => calls++);
                value.OnFinish(() => calls++);
            }));
            var originalId = action.ActionId;
            action.Deinit();
            action.Deinit();
            Assert.That(api.Data, Is.Null);
            Assert.That(data.Disposals, Is.Zero);
            Assert.That(calls, Is.Zero);
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
            var other = Own(Kit.Custom<DisposableData>(value => value.Data = data));
            Assert.That(other, Is.Not.SameAs(action));
            ActionQueue.Drain();
            ICustomApi<DisposableData> reusedApi = null;
            var reused = Own(Kit.Custom<DisposableData>(value => reusedApi = value));
            Assert.That(reused, Is.SameAs(action));
            Assert.That(reused.ActionId, Is.Not.EqualTo(originalId));
            Assert.That(reusedApi.Data, Is.Null);
            Assert.That(reused.Execute(0), Is.False);
            Assert.That(reused.Execute(0), Is.False);
            reused.Finish();
            Assert.That(reused.Execute(0), Is.True);
            Assert.That(calls, Is.Zero);
            Assert.That(data.Disposals, Is.Zero);
        }

        [Test]
        public void UntypedCustomDefersReuseAndRefreshesIdWhileItsCallbackIsExecuting()
        {
            var generator = Kit.IdGenerator;
            var data = new DisposableData();
            var starts = 0;
            var executions = 0;
            var finishes = 0;
            ICustomApi<object> api = null;
            IAction first = null;
            IAction second = null;
            first = Own(Kit.Custom(value =>
            {
                api = value;
                value.Data = data;
                value.OnStart(() => starts++);
                value.OnExecute(dt =>
                {
                    executions++;
                    first.Deinit();
                    first.Deinit();
                    second = Own(Kit.Custom(next => Assert.That(next.Data, Is.Null)));
                    Assert.That(second, Is.Not.SameAs(first));
                    value.Finish();
                });
                value.OnFinish(() => finishes++);
            }));
            var originalId = first.ActionId;
            Assert.That(originalId, Is.EqualTo(generator));
            Assert.That(Kit.IdGenerator, Is.EqualTo(generator + 1));
            Assert.That(first.Execute(0), Is.False);
            Assert.That(first.Execute(0), Is.True);
            Assert.That(first.IsDeinitialized, Is.True);
            Assert.That(first.ActionId, Is.EqualTo(originalId));
            Assert.That(api.Data, Is.Null);
            Assert.That(data.Disposals, Is.Zero);
            Assert.That(starts, Is.EqualTo(1));
            Assert.That(executions, Is.EqualTo(1));
            Assert.That(finishes, Is.Zero);
            Assert.That(second.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(second.IsDeinitialized, Is.False);
            Assert.That(second.ActionId, Is.Not.EqualTo(originalId));
            Assert.That(Kit.IdGenerator, Is.EqualTo(generator + 2));
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
            first.Reset();
            Assert.That(first.IsDeinitialized, Is.True);
            ActionQueue.Drain();
            ICustomApi<object> reusedApi = null;
            var reused = Own(Kit.Custom(value => reusedApi = value));
            Assert.That(reused, Is.SameAs(first));
            Assert.That(reusedApi, Is.SameAs(api));
            Assert.That(reusedApi.Data, Is.Null);
            Assert.That(reused.ActionId, Is.Not.EqualTo(originalId));
            Assert.That(Kit.IdGenerator, Is.EqualTo(generator + 3));
            Assert.That(reused.IsDeinitialized, Is.False);
            Assert.That(reused.Execute(0), Is.False);
            Assert.That(reused.Execute(0), Is.False);
            Assert.That(starts, Is.EqualTo(1));
            Assert.That(executions, Is.EqualTo(1));
            Assert.That(finishes, Is.Zero);
        }

        [Test]
        public void FluentCombinationsReturnTheirParentAndExecuteTheirChildren()
        {
            var calls = 0;
            var sequence = Own(Kit.Sequence());
            Assert.That(sequence.Parallel(inner => inner.Callback(() => calls++)), Is.SameAs(sequence));
            Assert.That(sequence.Repeat(2, inner => inner.Callback(() => calls++)), Is.SameAs(sequence));
            Assert.That(sequence.ConditionSequence(() => true, inner => inner.Callback(() => calls++)), Is.SameAs(sequence));
            Assert.That(sequence.Custom<int>(api => api.OnStart(api.Finish)), Is.SameAs(sequence));
            Assert.That(sequence.Custom(api => api.OnStart(api.Finish)), Is.SameAs(sequence));
            Assert.That(sequence.Lerp(2, 4, 0), Is.SameAs(sequence));
            Assert.That(sequence.Lerp01(0), Is.SameAs(sequence));
            var completed = false;
            for (var iteration = 0; iteration < 20 && !completed; iteration++)
            {
                completed = sequence.Execute(0);
            }

            Assert.That(completed, Is.True);
            Assert.That(calls, Is.EqualTo(4));
        }

        [Test]
        public void InfiniteRepeatAndDynamicDelayFluentOverloadsRemainCallable()
        {
            var factoryCalls = 0;
            var sequence = Own(Kit.Sequence());
            Assert.That(sequence.Delay(() =>
            {
                factoryCalls++;
                return 0;
            }), Is.SameAs(sequence));
            Assert.That(sequence.Repeat(inner => inner.Callback(null)), Is.SameAs(sequence));
            Assert.That(sequence.Execute(0), Is.False);
            Assert.That(sequence.Execute(0), Is.False);
            Assert.That(factoryCalls, Is.EqualTo(1));
        }
    }
}
