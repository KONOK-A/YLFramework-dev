using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using YLFramework.Async;

namespace YLFramework.Foundation.Tests
{
    public class CustomAsyncOperationTests
    {
        private sealed class Probe : CustomAsyncOperation<Probe>
        {
            public readonly List<string> Calls = new List<string>();
            public Action CompletedHook;
            public Action AbortHook;
            public void ReportProgress(float value) => Progress = value;
            public void Finish(string failure = null) => Complete(failure);
            public void FinishSilently(string failure = null) => CompleteWithoutNotify(failure);
            protected override void OnCompleted()
            {
                Calls.Add("hook");
                CompletedHook?.Invoke();
            }
            protected override void OnAbort()
            {
                Calls.Add("abort");
                AbortHook?.Invoke();
            }
        }

        [Test]
        public void NewOperationWaitsAndHidesErrorUntilCompletion()
        {
            var operation = new Probe();
            Assert.That(operation.IsDone, Is.False);
            Assert.That(operation.keepWaiting, Is.True);
            Assert.That(operation.MoveNext(), Is.True);
            Assert.That(operation.Progress, Is.Zero);
            Assert.That(operation.Error, Is.EqualTo("\u6b63\u5728\u68c0\u6d4b\u4e2d..."));
        }

        [TestCase(-0.25f)]
        [TestCase(0.4f)]
        [TestCase(1.5f)]
        public void ProgressIsNotClampedAndCompletionSetsItToOne(float progress)
        {
            var operation = new Probe();
            operation.ReportProgress(progress);
            Assert.That(operation.Progress, Is.EqualTo(progress));
            Assert.That(operation.IsDone, Is.False);
            operation.Finish();
            Assert.That(operation.Progress, Is.EqualTo(1f));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("load failed")]
        public void CompletionExposesExactErrorAndStopsWaiting(string error)
        {
            var operation = new Probe();
            operation.Finish(error);
            Assert.That(operation.Error, Is.EqualTo(error));
            Assert.That(operation.IsDone, Is.True);
            Assert.That(operation.keepWaiting, Is.False);
            Assert.That(operation.MoveNext(), Is.False);
        }

        [Test]
        public void CompletionNotifiesCallbacksInRegistrationOrderThenHook()
        {
            var operation = new Probe();
            Action<Probe> first = value =>
            {
                Assert.That(value, Is.SameAs(operation));
                operation.Calls.Add("first");
            };
            operation.AddCompleteEvent(first);
            operation.AddCompleteEvent(value =>
            {
                Assert.That(value, Is.SameAs(operation));
                Assert.That(value.IsDone, Is.True);
                Assert.That(value.Progress, Is.EqualTo(1f));
                Assert.That(value.Error, Is.EqualTo("failure"));
                operation.Calls.Add("registered");
            });
            operation.Finish("failure");
            operation.RemoveCompleteEvent(first);
            Assert.That(operation.Calls, Is.EqualTo(new[] { "first", "registered", "hook" }));
        }

        [Test]
        public void FirstCompletionFreezesErrorAndProgress()
        {
            var operation = new Probe();
            operation.AddCompleteEvent(_ => operation.Calls.Add("registered"));
            operation.Finish("first");
            operation.ReportProgress(0.3f);
            Assert.That(operation.Progress, Is.EqualTo(1f));
            operation.Finish("second");
            Assert.That(operation.Error, Is.EqualTo("first"));
            Assert.That(operation.Progress, Is.EqualTo(1f));
            Assert.That(operation.Calls, Is.EqualTo(new[] { "registered", "hook" }));
        }

        [Test]
        public void RegistrationAfterCompletionImmediatelyNotifiesEachRegistration()
        {
            var operation = new Probe();
            operation.Finish();
            var count = 0;
            Action<Probe> callback = value =>
            {
                Assert.That(value, Is.SameAs(operation));
                count++;
            };
            operation.AddCompleteEvent(callback);
            operation.AddCompleteEvent(callback);
            operation.AddCompleteEvent(null);
            Assert.That(count, Is.EqualTo(2));
            Assert.That(operation.Calls, Is.EqualTo(new[] { "hook" }));
        }

        [Test]
        public void RemovedCallbacksAreNotInvoked()
        {
            var operation = new Probe();
            var removedCount = 0;
            var retainedCount = 0;
            Action<Probe> removed = _ => removedCount++;
            operation.AddCompleteEvent(removed);
            operation.AddCompleteEvent(_ => retainedCount++);
            operation.RemoveCompleteEvent(removed);
            operation.Finish();
            Assert.That(removedCount, Is.Zero);
            Assert.That(retainedCount, Is.EqualTo(1));
        }

        [Test]
        public void RemoveAllClearsCompletionCallbacksWithoutSkippingTheHook()
        {
            var operation = new Probe();
            operation.AddCompleteEvent(_ => operation.Calls.Add("registered"));
            operation.RemoveAllCompleteEvent();
            operation.Finish();
            Assert.That(operation.Calls, Is.EqualTo(new[] { "hook" }));
        }

        [Test]
        public void SilentCompletionRunsHookButNotEventsAndStillAllowsLateCallback()
        {
            var operation = new Probe();
            operation.AddCompleteEvent(_ => operation.Calls.Add("registered"));
            operation.FinishSilently("silent failure");
            Assert.That(operation.IsDone, Is.True);
            Assert.That(operation.keepWaiting, Is.False);
            Assert.That(operation.Progress, Is.EqualTo(1f));
            Assert.That(operation.Error, Is.EqualTo("silent failure"));
            Assert.That(operation.Calls, Is.EqualTo(new[] { "hook" }));
            operation.AddCompleteEvent(_ => operation.Calls.Add("late"));
            operation.FinishSilently("replacement");
            operation.Finish();
            Assert.That(operation.Error, Is.EqualTo("silent failure"));
            Assert.That(operation.Calls, Is.EqualTo(new[] { "hook", "late" }));
        }

        [Test]
        public void AbortIsIdempotentAndLateProducerCannotOverwriteCancellation()
        {
            var operation = new Probe();
            operation.AddCompleteEvent(_ => operation.Calls.Add("registered"));
            operation.Abort();
            operation.Abort();
            operation.Finish();
            Assert.That(operation.IsDone, Is.True);
            Assert.That(operation.Progress, Is.EqualTo(1f));
            Assert.That(operation.Error, Is.EqualTo("request abort!"));
            Assert.That(operation.Calls, Is.EqualTo(new[] { "registered", "hook", "abort" }));
        }

        [Test]
        public void AbortAfterCompletionPreservesSuccessWithoutRunningAbortHook()
        {
            var operation = new Probe();
            operation.Finish();
            operation.Abort();
            Assert.That(operation.Error, Is.Null);
            Assert.That(operation.Calls, Is.EqualTo(new[] { "hook" }));
        }

        [Test]
        public void CallbackExceptionPropagatesAfterStateChangeAndInterruptsNotification()
        {
            var operation = new Probe();
            var failure = new InvalidOperationException("callback failure");
            operation.AddCompleteEvent(_ => throw failure);
            operation.AddCompleteEvent(_ => operation.Calls.Add("later"));
            Assert.That(Assert.Throws<InvalidOperationException>(() => operation.Finish()), Is.SameAs(failure));
            Assert.That(operation.IsDone, Is.True);
            Assert.That(operation.Progress, Is.EqualTo(1f));
            Assert.That(operation.Calls, Is.EqualTo(new[] { "hook" }));
            operation.Finish();
            Assert.That(operation.Calls, Is.EqualTo(new[] { "hook" }));
        }

        [Test]
        public void CallbackFailureStillRunsCompletionAndAbortHooksOnce()
        {
            var operation = new Probe();
            var failure = new InvalidOperationException("callback failure");
            operation.AddCompleteEvent(_ => throw failure);
            Assert.That(Assert.Throws<InvalidOperationException>(operation.Abort), Is.SameAs(failure));
            operation.Abort();
            operation.Finish("late");
            Assert.That(operation.Error, Is.EqualTo("request abort!"));
            Assert.That(operation.Calls, Is.EqualTo(new[] { "hook", "abort" }));
        }

        [Test]
        public void CompletionAndAbortFailuresArePreservedInOccurrenceOrder()
        {
            var operation = new Probe();
            var callbackError = new InvalidOperationException("callback");
            var completionError = new InvalidOperationException("completion");
            var abortError = new InvalidOperationException("abort");
            operation.AddCompleteEvent(_ => throw callbackError);
            operation.CompletedHook = () => throw completionError;
            operation.AbortHook = () => throw abortError;
            var error = Assert.Throws<AggregateException>(operation.Abort);
            Assert.That(error.InnerExceptions, Is.EqualTo(new[] { callbackError, completionError, abortError }));
            Assert.That(operation.IsDone, Is.True);
            Assert.That(operation.Calls, Is.EqualTo(new[] { "hook", "abort" }));
            Assert.DoesNotThrow(operation.Abort);
        }

        [Test]
        public void ReentrantCompletionCannotChangeCommittedResultOrRepeatHooks()
        {
            var operation = new Probe();
            operation.CompletedHook = () => operation.Finish("hook overwrite");
            operation.AddCompleteEvent(value =>
            {
                value.Abort();
                value.FinishSilently("callback overwrite");
                value.ReportProgress(0);
            });
            operation.Finish("first");
            Assert.That(operation.Error, Is.EqualTo("first"));
            Assert.That(operation.Progress, Is.EqualTo(1f));
            Assert.That(operation.Calls, Is.EqualTo(new[] { "hook" }));
        }

        [Test]
        public void CompletionReleasesStoredSubscriptionsIncludingLateAndSilentRegistrations()
        {
            foreach (var silently in new[] { false, true })
            {
                var operation = new Probe();
                operation.AddCompleteEvent(_ => { });
                if (silently)
                    operation.FinishSilently();
                else
                    operation.Finish();
                operation.AddCompleteEvent(_ => { });
                var field = typeof(CustomAsyncOperation<Probe>).GetField("_completed", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(field.GetValue(operation), Is.Null);
            }
        }

        [Test]
        public void AsTaskExposesOnePublicTaskBridgeWithoutWritableCompletionState()
        {
            var method = typeof(Probe).GetMethod("AsTask", Type.EmptyTypes);
            Assert.That(method, Is.Not.Null);
            Assert.That(method.ReturnType, Is.EqualTo(typeof(Task<Probe>)));
            var state = typeof(CustomAsyncOperation<Probe>).GetProperty("IsCompleted", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(state.GetSetMethod(true), Is.Null);
        }

        [TestCase(false, null)]
        [TestCase(true, null)]
        [TestCase(false, "failure")]
        [TestCase(true, "failure")]
        public void AsTaskCachesOneTaskAndReturnsTheSameCompletedRequest(bool finishFirst, string error)
        {
            var operation = new Probe();
            if (finishFirst)
                operation.Finish(error);
            var task = GetTask(operation);
            Assert.That(GetTask(operation), Is.SameAs(task));
            Assert.That(task.IsCompleted, Is.EqualTo(finishFirst));
            operation.Finish(error);
            Assert.That(task.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(task.GetAwaiter().GetResult(), Is.SameAs(operation));
            Assert.That(operation.Error, Is.EqualTo(error));
            Assert.That(GetTask(operation), Is.SameAs(task));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemovingCallbacksCannotRemoveTaskFromSilentCompletionOrAbort(bool silently)
        {
            var operation = new Probe();
            var task = GetTask(operation);
            operation.AddCompleteEvent(_ => Assert.Fail("Removed callback must not run."));
            operation.RemoveAllCompleteEvent();
            if (silently)
                operation.FinishSilently("silent");
            else
                operation.Abort();
            Assert.That(task.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(task.GetAwaiter().GetResult(), Is.SameAs(operation));
            Assert.That(operation.Error, Is.EqualTo(silently ? "silent" : "request abort!"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NotificationAndHookFailuresCannotStrandTask(bool abort)
        {
            var operation = new Probe();
            var task = GetTask(operation);
            var callbackError = new InvalidOperationException("callback");
            var hookError = new InvalidOperationException("hook");
            operation.AddCompleteEvent(_ => throw callbackError);
            operation.CompletedHook = () =>
            {
                Assert.That(task.IsCompleted, Is.False);
                throw hookError;
            };
            operation.AbortHook = () => Assert.That(task.IsCompleted, Is.False);
            var exception = Assert.Throws<AggregateException>(() =>
            {
                if (abort)
                    operation.Abort();
                else
                    operation.Finish();
            });
            Assert.That(exception.InnerExceptions, Is.EqualTo(new[] { callbackError, hookError }));
            Assert.That(task.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(task.GetAwaiter().GetResult(), Is.SameAs(operation));
            Assert.That(operation.Calls, Is.EqualTo(abort ? new[] { "hook", "abort" } : new[] { "hook" }));
        }

        [Test]
        public void AsTaskRequestedInsideNotificationWaitsForFinalization()
        {
            var operation = new Probe();
            Task<Probe> task = null;
            operation.AddCompleteEvent(_ =>
            {
                task = GetTask(operation);
                Assert.That(task.IsCompleted, Is.False);
            });
            operation.CompletedHook = () => Assert.That(task.IsCompleted, Is.False);
            operation.Finish();
            Assert.That(task.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(task.GetAwaiter().GetResult(), Is.SameAs(operation));
        }

        private static Task<Probe> GetTask(Probe operation)
            => operation.AsTask();

        [Test]
        public void ReentrantRegistrationAndRemovalKeepTheCommittedStateAndOriginalNotificationSnapshot()
        {
            var operation = new Probe();
            Action<Probe> later = _ => operation.Calls.Add("later");
            Action<Probe> immediate = value =>
            {
                Assert.That(value.IsDone, Is.True);
                Assert.That(value.Progress, Is.EqualTo(1f));
                operation.Calls.Add("immediate");
            };
            Action<Probe> first = _ =>
            {
                operation.Calls.Add("first");
                operation.RemoveCompleteEvent(later);
                operation.AddCompleteEvent(immediate);
            };
            operation.AddCompleteEvent(first);
            operation.AddCompleteEvent(later);
            operation.Finish();
            operation.RemoveCompleteEvent(first);
            operation.RemoveCompleteEvent(immediate);
            operation.Finish();
            Assert.That(operation.Calls, Is.EqualTo(new[] { "first", "immediate", "later", "hook" }));
        }
    }
}
