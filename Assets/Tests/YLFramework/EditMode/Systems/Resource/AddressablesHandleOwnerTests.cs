using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Resource.Addressables")]
    public sealed class AddressablesHandleOwnerTests
    {
        private sealed class ManualOperation : AsyncOperationBase<string>
        {
            public int Destroyed;
            protected override void Execute()
            {
            }
            public void Finish() => Complete("official operation", true, (string)null);
            protected override void Destroy() => Destroyed++;
        }

        [Test]
        public void TakingACompletedReferenceReleasesItWithoutAnExtraRetain()
        {
            using var manager = new ResourceManager();
            var operation = new ManualOperation();
            var handle = manager.StartOperation(operation, default);
            operation.Finish();
            IDisposable owner = null;
            try
            {
                owner = Create("Take", manager, handle);
                owner.Dispose();
                owner.Dispose();
                Assert.That(operation.Destroyed, Is.EqualTo(1));
            }
            finally
            {
                if (owner == null && handle.IsValid())
                    manager.Release(handle);
                owner?.Dispose();
            }
        }

        [Test]
        public void EachObserverOwnsItsOwnReferenceAndNeverReleasesTheExternalOwner()
        {
            using var manager = new ResourceManager();
            var operation = new ManualOperation();
            var handle = manager.StartOperation(operation, default);
            operation.Finish();
            using var first = Create("Observe", manager, handle);
            using var second = Create("Observe", manager, handle);
            first.Dispose();
            second.Dispose();

            Assert.That(handle.IsValid(), Is.True);
            Assert.That(handle.Result, Is.EqualTo("official operation"));
            Assert.That(operation.Destroyed, Is.Zero);
            manager.Release(handle);
            Assert.That(operation.Destroyed, Is.EqualTo(1));
        }

        [Test]
        public void DisposingAnInFlightOwnerKeepsItsReferenceUntilTheActualCompletion()
        {
            using var manager = new ResourceManager();
            var operation = new ManualOperation();
            var handle = manager.StartOperation(operation, default);
            IDisposable owner = null;
            try
            {
                owner = Create("Take", manager, handle);
                owner.Dispose();
                Assert.That(handle.IsValid(), Is.True);
                Assert.That(operation.Destroyed, Is.Zero);
                Assert.That(Read<bool>(owner, "IsDraining"), Is.True);

                operation.Finish();
                Assert.That(operation.Destroyed, Is.EqualTo(1));
                Assert.That(Read<bool>(owner, "IsDraining"), Is.False);
            }
            finally
            {
                if (handle.IsValid() && !handle.IsDone)
                    operation.Finish();
                if (owner == null && handle.IsValid())
                    manager.Release(handle);
                owner?.Dispose();
            }
        }

        [Test]
        public void AnExternalAutoReleaseCannotConsumeTheIndependentObserverPin()
        {
            using var manager = new ResourceManager();
            var operation = new ManualOperation();
            var handle = manager.StartOperation(operation, default);
            handle.Completed += completed => manager.Release(completed);
            using var owner = Create("Observe", manager, handle);

            operation.Finish();
            Assert.That(Read<AsyncOperationHandle<string>>(owner, "BorrowedHandle").Result, Is.EqualTo("official operation"));
            Assert.That(operation.Destroyed, Is.Zero);
            owner.Dispose();
            Assert.That(operation.Destroyed, Is.EqualTo(1));
        }

        [Test]
        public void InvalidHandlesAreRejectedBeforeAnyReferenceIsAcquired()
        {
            using var manager = new ResourceManager();
            Assert.Throws<ArgumentException>(() => Create("Take", manager, default));
            Assert.Throws<ArgumentException>(() => Create("Observe", manager, default));
        }

        [Test]
        public void WorkerThreadCannotDisposeTheMainThreadReference()
        {
            using var manager = new ResourceManager();
            var operation = new ManualOperation();
            var handle = manager.StartOperation(operation, default);
            operation.Finish();
            using var owner = Create("Take", manager, handle);

            Exception error = Task.Run(() =>
            {
                try
                {
                    owner.Dispose();
                    return null;
                }
                catch (Exception exception) { return exception; }
            }).GetAwaiter().GetResult();

            Assert.That(error, Is.TypeOf<InvalidOperationException>());
            Assert.That(Read<AsyncOperationHandle<string>>(owner, "BorrowedHandle").Result, Is.EqualTo("official operation"));
        }

        private static IDisposable Create(string factory, ResourceManager manager, AsyncOperationHandle<string> handle)
        {
            var type = typeof(YLFramework.Resource.ResourceSystem).Assembly.GetType("YLFramework.Resource.AddressablesHandleOwner`1");
            Assert.That(type, Is.Not.Null, "The official handle ownership adapter is missing.");
            var method = type.MakeGenericType(typeof(string)).GetMethod(factory, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "The reference acquisition rule must be explicit.");
            try
            {
                return (IDisposable)method.Invoke(null, new object[] { manager, handle });
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        private static T Read<T>(IDisposable owner, string property) =>
            (T)owner.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
    }
}
