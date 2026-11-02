using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Actions;
using Kit = YLFramework.Actions.ActionKit;

namespace YLFramework.Tests.Actions
{
    public class CompletedTaskActionTests
    {
        private readonly List<IAction> _actions = new List<IAction>();
        private readonly List<TaskCompletionSource<bool>> _tasks = new List<TaskCompletionSource<bool>>();

        private T Own<T>(T action) where T : IAction
        {
            _actions.Add(action);
            return action;
        }

        [SetUp]
        public void SetUp() => ActionQueue.Drain();

        [TearDown]
        public void TearDown()
        {
            foreach (var action in _actions)
            {
                if (!action.IsDeinitialized)
                {
                    action.Deinit();
                }
            }
            foreach (var task in _tasks)
            {
                task.TrySetResult(true);
            }

            _actions.Clear();
            _tasks.Clear();
            ActionQueue.Drain();
        }

        private TaskCompletionSource<bool> PendingTask()
        {
            var task = new TaskCompletionSource<bool>();
            _tasks.Add(task);
            return task;
        }

        [Test]
        public void AlreadyCompletedTaskCanFinishDuringTheFirstExecute()
        {
            var getters = 0;
            var action = Own(Kit.Task(() =>
            {
                getters++;
                return Task.FromResult(42);
            }));
            Assert.That(action.Execute(0), Is.True);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Finished));
            Assert.That(getters, Is.EqualTo(1));
            Assert.That(action.IsDeinitialized, Is.False);
        }

        [Test]
        public void ResetReevaluatesTheTaskFactoryAndExplicitAllocateRemainsAvailable()
        {
            var getters = 0;
            var action = Own(TaskAction.Allocate(() =>
            {
                getters++;
                return Task.CompletedTask;
            }));
            Assert.That(action.Execute(0), Is.True);
            action.Reset();
            Assert.That(action.Execute(0), Is.True);
            Assert.That(getters, Is.EqualTo(2));
        }

        [Test]
        public void TaskToActionAndSequenceTaskOverloadsAreFullyUsable()
        {
            var calls = 0;
            var taskAction = Own(Task.FromResult(7).ToAction());
            Assert.That(taskAction.Execute(0), Is.True);
            var sequence = Own(Kit.Sequence().Task(() => Task.CompletedTask).Callback(() => calls++));
            Assert.That(sequence.Execute(0), Is.True);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void PausedBeforeFirstExecutionDoesNotPreventTaskStart()
        {
            var calls = 0;
            var action = Own(Kit.Task(() =>
            {
                calls++;
                return Task.CompletedTask;
            }));
            action.IsPaused = true;
            Assert.That(action.Execute(0), Is.True);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void DeinitOfPendingTaskDoesNotDisposeOrCompleteTheExternalTask()
        {
            var task = PendingTask();
            var action = Own(Kit.Task(() => task.Task));
            Assert.That(action.Execute(0), Is.False);
            var pending = ActionQueue.PendingCount;
            Assert.DoesNotThrow(action.Deinit);
            Assert.DoesNotThrow(action.Deinit);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(task.Task.IsCompleted, Is.False);
            Assert.That(task.Task.IsCanceled, Is.False);
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(pending + 1));
        }

        [Test]
        public void CompletedExternalTaskRemainsUsableAfterActionDeinit()
        {
            var task = PendingTask();
            task.SetResult(true);
            var action = Own(task.Task.ToAction());
            try
            {
                Assert.That(action.Execute(0), Is.True);
                action.Deinit();
                Assert.That(((IAsyncResult)task.Task).AsyncWaitHandle, Is.Not.Null);
                Assert.That(task.Task.Result, Is.True);
            }
            finally
            {
                task.Task.Dispose();
                _tasks.Remove(task);
            }
        }

        [Test]
        public void FaultedTaskIsReportedAndDeinitializedWithoutSuccessfulFinish()
        {
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: task-active-fault"));
            var action = Own(Kit.Task(() => Task.FromException(new InvalidOperationException("task-active-fault"))));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.Not.EqualTo(ActionStatus.Finished));
        }

        [Test]
        public void ThrowingFactoryIsReportedAndDeinitializedWithoutSuccessfulFinish()
        {
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: task-factory-fault"));
            var action = Own(Kit.Task(() => throw new InvalidOperationException("task-factory-fault")));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.Not.EqualTo(ActionStatus.Finished));
        }

        [Test]
        public void CancelledTaskIsDeinitializedWithoutSuccessfulFinish()
        {
            var task = PendingTask();
            task.SetCanceled();
            var action = Own(task.Task.ToAction());
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.Not.EqualTo(ActionStatus.Finished));
        }

        [Test]
        public void FaultedOperationCanceledExceptionIsNotMistakenForTaskCancellation()
        {
            LogAssert.Expect(LogType.Exception, new Regex("OperationCanceledException: task-cancellation-fault"));
            var task = Task.FromException(new OperationCanceledException("task-cancellation-fault"));
            var action = Own(task.ToAction());
            Assert.That(action.Execute(0), Is.False);
            Assert.That(task.IsFaulted, Is.True);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.Not.EqualTo(ActionStatus.Finished));
        }
    }
}
