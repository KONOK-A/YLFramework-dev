using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using NUnit.Framework;
using YLFramework.Resource;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Resource.Addressables")]
    public sealed class ResourceRequestAwaitTests
    {
        [Test]
        public void AwaitClaimsOnceAndAutomaticallyEndsRequestTracking()
        {
            int released = 0;
            int ended = 0;
            var value = new object();
            var request = new ResourceRequest<object>(_ => released++);
            request.OwnershipEnded = () => ended++;
            object awaiter = Awaiter(request);
            int continued = 0;
            ((INotifyCompletion)awaiter).OnCompleted(() => continued++);

            Assert.That(IsCompleted(awaiter), Is.False);
            request.SetSuccess(value);
            request.NotifyCompletion();
            request.NotifyCompletion();

            Assert.That(continued, Is.EqualTo(1));
            Assert.That(Result<object>(awaiter), Is.SameAs(value));
            Assert.That(request.IsDisposed, Is.True);
            Assert.That(ended, Is.EqualTo(1));
            Assert.That(released, Is.Zero);
            Assert.Throws<ObjectDisposedException>(() => Result<object>(awaiter));
        }

        [Test]
        public void AwaitReservationRejectsAnotherAwaitAndManualClaim()
        {
            using var request = new ResourceRequest<string>();
            var awaiter = Awaiter(request);
            Assert.Throws<InvalidOperationException>(() => Awaiter(request));
            request.SetSuccess("one owner");

            Assert.Throws<InvalidOperationException>(() => request.TakeResult());
            Assert.That(Result<string>(awaiter), Is.EqualTo("one owner"));
        }

        [Test]
        public void AwaitCancellationThrowsOperationCanceledAndStillEndsTracking()
        {
            int ended = 0;
            var request = new ResourceRequest<string>();
            request.OwnershipEnded = () => ended++;
            object awaiter = Awaiter(request);
            request.Cancel();

            var exception = Assert.Throws<OperationCanceledException>(() => Result<string>(awaiter));

            Assert.That(exception.InnerException, Is.SameAs(request.Error));
            Assert.That(request.IsDisposed, Is.True);
            Assert.That(ended, Is.EqualTo(1));
        }

        [Test]
        public void AwaitFailurePreservesOriginalResourceErrorAndClosesRequest()
        {
            var request = new ResourceRequest<string>();
            var error = new ResourceException(ResourceError.LoadFailed, "original cause", new InvalidOperationException("provider"));
            object awaiter = Awaiter(request);
            request.SetFailure(ResourceRequestStatus.Failed, error);

            Assert.That(Assert.Throws<ResourceException>(() => Result<string>(awaiter)), Is.SameAs(error));
            Assert.That(request.IsDisposed, Is.True);
        }

        [Test]
        public void ReadingAnIncompleteAwaiterDoesNotCancelOrDisposeItsRequest()
        {
            using var request = new ResourceRequest<string>();
            object awaiter = Awaiter(request);

            Assert.Throws<InvalidOperationException>(() => Result<string>(awaiter));
            Assert.That(request.IsDisposed, Is.False);
            request.SetSuccess("later");
            Assert.That(Result<string>(awaiter), Is.EqualTo("later"));
        }

        [Test]
        public void AwaitContinuationCanBeRegisteredOnlyOnce()
        {
            using var request = new ResourceRequest<string>();
            object awaiter = Awaiter(request);
            var completion = (INotifyCompletion)awaiter;
            int calls = 0;
            completion.OnCompleted(() => calls++);

            Assert.Throws<InvalidOperationException>(() => completion.OnCompleted(() => calls++));
            request.Cancel();
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void DisposingAPendingAwaitedRequestIsCancellationRatherThanADisposedObjectFailure()
        {
            var request = new ResourceRequest<string>();
            object awaiter = Awaiter(request);
            request.Dispose();

            Assert.Throws<OperationCanceledException>(() => Result<string>(awaiter));
            Assert.Throws<ObjectDisposedException>(() => Result<string>(awaiter));
        }

        [Test]
        public async Task ActualCompilerAwaitClaimsTheResultAndClosesTheRequest()
        {
            var request = new ResourceRequest<string>();
            var waiting = Wait(request);
            Assert.That(waiting.IsCompleted, Is.False);
            request.SetSuccess("compiler continuation");
            request.NotifyCompletion();

            Assert.That(await waiting, Is.EqualTo("compiler continuation"));
            Assert.That(request.IsDisposed, Is.True);
        }

        private static async Task<string> Wait(ResourceRequest<string> request) => await request;

        private static object Awaiter<T>(ResourceRequest<T> request)
        {
            MethodInfo method = request.GetType().GetMethod("GetAwaiter", BindingFlags.Public | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "ResourceRequest does not implement its single-consumer await contract.");
            return Invoke(method, request);
        }

        private static bool IsCompleted(object awaiter) => (bool)awaiter.GetType().GetProperty("IsCompleted").GetValue(awaiter);

        private static T Result<T>(object awaiter) => (T)Invoke(awaiter.GetType().GetMethod("GetResult"), awaiter);

        private static object Invoke(MethodInfo method, object value)
        {
            try
            {
                return method.Invoke(value, null);
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }
}
