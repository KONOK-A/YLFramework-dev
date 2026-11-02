using System;
using System.Collections.Generic;
using NUnit.Framework;
using YLFramework.Actions;

namespace YLFramework.Tests.Actions
{
    public class CompositeCancellationTests
    {
        private readonly List<IAction> _owned = new List<IAction>();

        private T Own<T>(T action) where T : IAction
        {
            _owned.Add(action);
            return action;
        }

        private ISequence CreateComposite(int kind)
        {
            switch (kind)
            {
                case 0:
                    return Own(Sequence.Allocate());
                case 1:
                    return Own(Parallel.Allocate());
                case 2:
                    return Own(Repeat.Allocate(2));
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
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
                if (action is ProbeAction probe)
                {
                    probe.OnDeinited = null;
                }

                if (action is FailingAction failing)
                {
                    failing.Failure = null;
                }
            }
            try
            {
                foreach (var action in _owned)
                {
                    if (!action.IsDeinitialized)
                    {
                        action.Deinit();
                    }
                }
            }
            finally
            {
                _owned.Clear();
                ActionQueue.Drain();
            }
        }

        private sealed class ProbeAction : IAction
        {
            public Action OnStarted;
            public Action<float> OnExecuted;
            public Action OnDeinited;
            public int Starts;
            public int Executions;
            public int Finishes;
            public int Deinits;
            public float LastDelta;

            public ulong ActionId
            {
                get; set;
            }
            public ActionStatus Status
            {
                get; set;
            }
            public bool IsPaused
            {
                get; set;
            }
            public bool IsDeinitialized
            {
                get; set;
            }

            public void OnStart()
            {
                Starts++;
                OnStarted?.Invoke();
            }

            public void OnExecute(float dt)
            {
                Executions++;
                LastDelta = dt;
                OnExecuted?.Invoke(dt);
            }

            public void OnFinish()
            {
                Finishes++;
            }

            public void Reset()
            {
                Status = ActionStatus.NotStart;
                IsPaused = false;
            }

            public void Deinit()
            {
                if (IsDeinitialized)
                {
                    return;
                }

                IsDeinitialized = true;
                Deinits++;
                OnDeinited?.Invoke();
            }
        }

        public sealed class FailingAction : AbstractAction<FailingAction>
        {
            public Exception Failure;
            public int Deinits;
            public bool ObservedDeinited;

            protected override void OnReset()
            {
                Failure = null;
                Deinits = 0;
                ObservedDeinited = false;
            }

            protected override void OnDeinit()
            {
                Deinits++;
                ObservedDeinited = IsDeinitialized;
                if (Failure != null)
                {
                    throw Failure;
                }
            }
        }

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        [TestCase(2, true)]
        public void CancelledChildBeforeStartCancelsParentWithoutStartingSiblings(int kind, bool cancelFirst)
        {
            var cancelled = Own(new ProbeAction());
            var sibling = Own(new ProbeAction());
            var callbacks = 0;
            var callback = Own(CallbackAction.Allocate(() => callbacks++));
            cancelled.Deinit();
            var parent = CreateComposite(kind);
            parent.Append(cancelFirst ? cancelled : sibling);
            parent.Append(cancelFirst ? sibling : cancelled);
            parent.Append(callback);

            Assert.That(parent.Execute(1f), Is.False);
            Assert.That(parent.IsDeinitialized, Is.True);
            Assert.That(parent.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(cancelled.Deinits, Is.EqualTo(1));
            Assert.That(sibling.Deinits, Is.EqualTo(1));
            Assert.That(sibling.Starts, Is.Zero);
            Assert.That(callback.IsDeinitialized, Is.True);
            Assert.That(callbacks, Is.Zero);
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(kind == 2 ? 3 : 2));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void ChildCancellingDuringStartCancelsParentBeforeNextCallback(int kind)
        {
            var child = Own(new ProbeAction());
            child.OnStarted = child.Deinit;
            var callbacks = 0;
            var callback = Own(CallbackAction.Allocate(() => callbacks++));
            var parent = CreateComposite(kind).Append(child).Append(callback);

            Assert.That(parent.Execute(0), Is.False);
            if (!parent.IsDeinitialized)
            {
                Assert.That(parent.Execute(0.25f), Is.False);
            }

            Assert.That(parent.IsDeinitialized, Is.True);
            Assert.That(parent.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(child.Starts, Is.EqualTo(1));
            Assert.That(child.Finishes, Is.Zero);
            Assert.That(child.Deinits, Is.EqualTo(1));
            Assert.That(callback.IsDeinitialized, Is.True);
            Assert.That(callbacks, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void ChildCancellingDuringExecuteCancelsParentWithoutExecutingSiblings(int kind)
        {
            var child = Own(new ProbeAction());
            child.OnExecuted = dt => child.Deinit();
            var callbacks = 0;
            var sibling = Own(new ProbeAction { OnExecuted = dt => callbacks++ });
            var parent = CreateComposite(kind).Append(child).Append(sibling);

            Assert.That(parent.Execute(0), Is.False);
            Assert.That(parent.Execute(0.25f), Is.False);
            if (!parent.IsDeinitialized)
            {
                Assert.That(parent.Execute(0.25f), Is.False);
            }

            Assert.That(parent.IsDeinitialized, Is.True);
            Assert.That(parent.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(child.LastDelta, Is.EqualTo(0.25f));
            Assert.That(child.Deinits, Is.EqualTo(1));
            Assert.That(child.Finishes, Is.Zero);
            Assert.That(sibling.Deinits, Is.EqualTo(1));
            Assert.That(sibling.Executions, Is.Zero);
            Assert.That(callbacks, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void CurrentChildCancellingAnotherChildStopsAllFurtherWork(int kind)
        {
            var child = Own(new ProbeAction());
            var sibling = Own(new ProbeAction());
            var callbacks = 0;
            sibling.OnExecuted = dt => callbacks++;
            child.OnExecuted = dt => sibling.Deinit();
            var parent = CreateComposite(kind).Append(child).Append(sibling);

            Assert.That(parent.Execute(0), Is.False);
            Assert.That(parent.Execute(0.25f), Is.False);
            if (!parent.IsDeinitialized)
            {
                Assert.That(parent.Execute(0.25f), Is.False);
            }

            Assert.That(parent.IsDeinitialized, Is.True);
            Assert.That(parent.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(child.Deinits, Is.EqualTo(1));
            Assert.That(sibling.Deinits, Is.EqualTo(1));
            Assert.That(callbacks, Is.Zero);
        }

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        [TestCase(2, true)]
        public void TaskFailureOrCancellationDeinitStateCancelsComposite(int kind, bool faulted)
        {
            // Simulate TaskAction's terminal Deinit state; this test drives only the parent logic.
            var failure = faulted ? new InvalidOperationException("task failed") : null;
            Exception observedFailure = null;
            var taskChild = Own(new ProbeAction { OnDeinited = () => observedFailure = failure });
            var callbacks = 0;
            var sibling = Own(new ProbeAction { OnExecuted = dt => callbacks++ });
            var parent = CreateComposite(kind).Append(taskChild).Append(sibling);
            Assert.That(parent.Execute(0), Is.False);
            Assert.That(parent.Execute(0), Is.False);
            taskChild.Deinit();

            Assert.That(parent.Execute(0), Is.False);
            Assert.That(observedFailure, Is.SameAs(failure));
            Assert.That(taskChild.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(taskChild.Finishes, Is.Zero);
            Assert.That(taskChild.Deinits, Is.EqualTo(1));
            Assert.That(parent.IsDeinitialized, Is.True);
            Assert.That(parent.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(sibling.Deinits, Is.EqualTo(1));
            Assert.That(callbacks, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void CleanupFailuresStillDeinitDistinctChildrenAndRecycleTheParent(int kind)
        {
            var trace = new List<int>();
            var firstFailure = new InvalidOperationException("first cleanup failed");
            var lastFailure = new InvalidOperationException("last cleanup failed");
            var first = Own(new ProbeAction
            {
                OnDeinited = () =>
                {
                    trace.Add(1);
                    throw firstFailure;
                }
            });
            var middle = Own(new ProbeAction { OnDeinited = () => trace.Add(2) });
            var last = Own(new ProbeAction
            {
                OnDeinited = () =>
                {
                    trace.Add(3);
                    throw lastFailure;
                }
            });
            var parent = CreateComposite(kind).Append(first).Append(middle).Append(last);
            var originalId = parent.ActionId;
            parent.Execute(0);
            parent.Execute(0);
            parent.Append(first).Append(parent);

            var exception = Assert.Throws<AggregateException>(() => parent.Deinit());
            CollectionAssert.AreEqual(new Exception[] { firstFailure, lastFailure }, exception.Flatten().InnerExceptions);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, trace);
            Assert.That(first.IsDeinitialized, Is.True);
            Assert.That(middle.IsDeinitialized, Is.True);
            Assert.That(last.IsDeinitialized, Is.True);
            Assert.That(first.Deinits, Is.EqualTo(1));
            Assert.That(middle.Deinits, Is.EqualTo(1));
            Assert.That(last.Deinits, Is.EqualTo(1));
            Assert.That(parent.IsDeinitialized, Is.True);
            var pending = kind == 2 ? 2 : 1;
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(pending));
            parent.Deinit();
            parent.Reset();
            Assert.That(parent.IsDeinitialized, Is.True);
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(pending));

            ActionQueue.Drain();
            var reused = CreateComposite(kind);
            Assert.That(reused, Is.SameAs(parent));
            Assert.That(reused.ActionId, Is.Not.EqualTo(originalId));
            Assert.That(reused.IsDeinitialized, Is.False);
            var runs = 0;
            var freshChild = Own(new ProbeAction());
            freshChild.OnStarted = () =>
            {
                runs++;
                freshChild.Finish();
            };
            reused.Append(freshChild);
            var completed = false;
            for (var index = 0; index < 6 && !completed; index++)
            {
                completed = reused.Execute(0);
            }

            Assert.That(completed, Is.True);
            Assert.That(runs, Is.EqualTo(kind == 2 ? 2 : 1));
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, trace);
        }

        [Test]
        public void AbstractActionDeinitFailureStillQueuesExactlyOnceButCannotBeReused()
        {
            var failure = new InvalidOperationException("hook failed");
            var action = Own(FailingAction.Allocate());
            var originalId = action.ActionId;
            action.Failure = failure;
            var exception = Assert.Throws<InvalidOperationException>(() => action.Deinit());
            Assert.That(exception, Is.SameAs(failure));
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.ObservedDeinited, Is.True);
            Assert.That(action.Deinits, Is.EqualTo(1));
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
            action.Deinit();
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
            var other = Own(FailingAction.Allocate());
            Assert.That(other, Is.Not.SameAs(action));

            ActionQueue.Drain();
            var reused = Own(FailingAction.Allocate());
            Assert.That(reused, Is.Not.SameAs(action));
            Assert.That(reused.ActionId, Is.Not.EqualTo(originalId));
            Assert.That(reused.IsDeinitialized, Is.False);
            Assert.That(reused.Failure, Is.Null);
            Assert.That(reused.Deinits, Is.Zero);
            Assert.That(reused.Status, Is.EqualTo(ActionStatus.NotStart));
            action.Deinit();
            Assert.That(action.Deinits, Is.EqualTo(1), "失败的领域清理不得被延迟丢弃重放。");
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void LiveChildExecutionFailurePreservesTheOriginalException(int kind)
        {
            var failure = new InvalidOperationException("execution failed");
            var child = Own(new ProbeAction
            {
                OnStarted = () =>
                {
                    throw failure;
                }
            });
            var sibling = Own(new ProbeAction());
            var parent = CreateComposite(kind).Append(child).Append(sibling);
            if (kind != 0)
            {
                Assert.That(parent.Execute(0), Is.False);
            }

            var exception = Assert.Throws<InvalidOperationException>(() => parent.Execute(0));
            Assert.That(exception, Is.SameAs(failure));
            Assert.That(child.IsDeinitialized, Is.False);
            Assert.That(parent.IsDeinitialized, Is.False);
            Assert.That(sibling.Starts, Is.Zero);
            Assert.That(ActionQueue.PendingCount, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void NestedCancellationCleanupFailureStillQueuesEveryComposite(int kind)
        {
            var innerFailure = new InvalidOperationException("inner cleanup failed");
            var outerFailure = new InvalidOperationException("outer cleanup failed");
            var taskChild = Own(new ProbeAction());
            var innerSibling = Own(new ProbeAction
            {
                OnDeinited = () =>
                {
                    throw innerFailure;
                }
            });
            var inner = Own(Sequence.Allocate()).Append(taskChild).Append(innerSibling);
            var outerSibling = Own(new ProbeAction
            {
                OnDeinited = () =>
                {
                    throw outerFailure;
                }
            });
            var parent = CreateComposite(kind).Append(inner).Append(outerSibling);
            Assert.That(parent.Execute(0), Is.False);
            Assert.That(parent.Execute(0), Is.False);
            taskChild.Deinit();

            var exception = Assert.Throws<AggregateException>(() => parent.Execute(0));
            CollectionAssert.AreEquivalent(new Exception[] { innerFailure, outerFailure }, exception.Flatten().InnerExceptions);
            Assert.That(parent.IsDeinitialized, Is.True);
            Assert.That(parent.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(inner.IsDeinitialized, Is.True);
            Assert.That(taskChild.Deinits, Is.EqualTo(1));
            Assert.That(innerSibling.Deinits, Is.EqualTo(1));
            Assert.That(outerSibling.Deinits, Is.EqualTo(1));
            Assert.That(outerSibling.Finishes, Is.Zero);
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(kind == 2 ? 3 : 2));
        }
    }
}
