using System;
using System.Collections.Generic;
using NUnit.Framework;
using YLFramework.Async;

namespace YLFramework.Foundation.Tests
{
    public class CustomAsyncOperationTests
    {
        private sealed class Probe : CustomAsyncOperation<Probe>
        {
            public readonly List<string> Calls = new List<string>();
            public void ReportProgress(float value) => Progress = value;
            public void Finish(string failure = null) => Complete(failure);
            public void FinishSilently(string failure = null) => CompleteWithoutNotify(failure);
            protected override void OnCompleted() => Calls.Add("hook");
            protected override void OnAbort() => Calls.Add("abort");
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
        public void CompletionNotifiesLegacyThenRegisteredCallbacksThenHook()
        {
            var operation = new Probe();
#pragma warning disable CS0618
            operation.Completed += value =>
            {
                Assert.That(value, Is.SameAs(operation));
                operation.Calls.Add("legacy");
            };
#pragma warning restore CS0618
            operation.AddCompleteEvent(value =>
            {
                Assert.That(value, Is.SameAs(operation));
                Assert.That(value.IsDone, Is.True);
                Assert.That(value.Progress, Is.EqualTo(1f));
                Assert.That(value.Error, Is.EqualTo("failure"));
                operation.Calls.Add("registered");
            });
            operation.Finish("failure");
            Assert.That(operation.Calls, Is.EqualTo(new[] { "legacy", "registered", "hook" }));
        }

        [Test]
        public void RepeatedCompletionUpdatesErrorWithoutRepeatingNotifications()
        {
            var operation = new Probe();
            operation.AddCompleteEvent(_ => operation.Calls.Add("registered"));
            operation.Finish("first");
            operation.ReportProgress(0.3f);
            operation.Finish("second");
            Assert.That(operation.Error, Is.EqualTo("second"));
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
        public void RemoveAllOnlyClearsRegisteredCallbacksNotLegacyEvent()
        {
            var operation = new Probe();
#pragma warning disable CS0618
            operation.Completed += _ => operation.Calls.Add("legacy");
#pragma warning restore CS0618
            operation.AddCompleteEvent(_ => operation.Calls.Add("registered"));
            operation.RemoveAllCompleteEvent();
            operation.Finish();
            Assert.That(operation.Calls, Is.EqualTo(new[] { "legacy", "hook" }));
        }

        [Test]
        public void SilentCompletionRunsHookButNotEventsAndStillAllowsLateCallback()
        {
            var operation = new Probe();
#pragma warning disable CS0618
            operation.Completed += _ => operation.Calls.Add("legacy");
#pragma warning restore CS0618
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
            Assert.That(operation.Error, Is.Null);
            Assert.That(operation.Calls, Is.EqualTo(new[] { "hook", "late" }));
        }

        [Test]
        public void AbortCompletesBeforeAbortHookAndRepeatedAbortStillRunsAbortHook()
        {
            var operation = new Probe();
            operation.AddCompleteEvent(_ => operation.Calls.Add("registered"));
            operation.Abort();
            operation.Abort();
            Assert.That(operation.IsDone, Is.True);
            Assert.That(operation.Progress, Is.EqualTo(1f));
            Assert.That(operation.Error, Is.EqualTo("request abort!"));
            Assert.That(operation.Calls, Is.EqualTo(new[] { "registered", "hook", "abort", "abort" }));
        }

        [Test]
        public void AbortAfterCompletionChangesErrorWithoutAnotherCompletion()
        {
            var operation = new Probe();
            operation.Finish();
            operation.Abort();
            Assert.That(operation.Error, Is.EqualTo("request abort!"));
            Assert.That(operation.Calls, Is.EqualTo(new[] { "hook", "abort" }));
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
            Assert.That(operation.Calls, Is.Empty);
            operation.Finish();
            Assert.That(operation.Calls, Is.Empty);
        }
    }
}
