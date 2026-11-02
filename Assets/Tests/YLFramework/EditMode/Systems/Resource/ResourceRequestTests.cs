using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Resource;

namespace YLFramework.Tests.Resource
{
    public class ResourceRequestTests
    {
        private sealed class Owner : IDisposable
        {
            public int Releases;
            public void Dispose() => Releases++;
        }

        [Test]
        public void WaitingDoesNotClaimAndDisposeReleasesOnlyUnclaimedResult()
        {
            var owner = new Owner();
            var request = new ResourceRequest<Owner>(value => value.Dispose());
            request.SetSuccess(owner);
            request.Notify();
            Assert.That(request.keepWaiting, Is.False);
            request.Dispose();
            request.Dispose();
            Assert.That(owner.Releases, Is.EqualTo(1));
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            Assert.Throws<ObjectDisposedException>(() => request.TakeResult());

            var claimed = new Owner();
            using (var other = new ResourceRequest<Owner>(value => value.Dispose()))
            {
                other.SetSuccess(claimed);
                Assert.That(other.TakeResult(), Is.SameAs(claimed));
                Assert.Throws<InvalidOperationException>(() => other.TakeResult());
            }
            Assert.That(claimed.Releases, Is.Zero);
            claimed.Dispose();
        }

        [Test]
        public void ObserverExceptionAndReentrantDisposeDoNotSkipOtherObservers()
        {
            var owner = new Owner();
            var request = new ResourceRequest<Owner>(value => value.Dispose());
            var observed = 0;
            request.Completed += _ => throw new InvalidOperationException("resource-observer-test");
            request.Completed += value =>
            {
                observed++;
                value.Dispose();
            };
            request.Completed += value =>
            {
                observed++;
                Assert.That(value.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            };
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: resource-observer-test");
            request.SetSuccess(owner);
            request.Notify();
            Assert.That(observed, Is.EqualTo(2));
            Assert.That(owner.Releases, Is.EqualTo(1));
        }

        [Test]
        public void CancellationFreezesProgressAndCannotBeRewrittenByLateSuccess()
        {
            var progress = 0.4f;
            var canceled = 0;
            var request = new ResourceRequest<Owner>(value => value.Dispose());
            request.Attach(() => canceled++, () => progress);
            request.Cancel();
            progress = 0.9f;
            var late = new Owner();
            request.SetSuccess(late);
            request.Dispose();
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(request.Progress, Is.EqualTo(0.4f));
            Assert.That(canceled, Is.EqualTo(1));
            Assert.That(late.Releases, Is.EqualTo(1));
        }

        [Test]
        public void FailedAndValueResultsHaveExplicitStableClaimSemantics()
        {
            using (var failed = new ResourceRequest<string>())
            {
                var error = new ResourceException(ResourceError.LoadFailed, "test-failure");
                failed.SetFailure(ResourceRequestStatus.Failed, error);
                Assert.That(Assert.Throws<ResourceException>(() => failed.TakeResult()), Is.SameAs(error));
            }
            using (var value = new ResourceRequest<string>())
            {
                value.SetSuccess("copied text");
                var replayed = 0;
                value.Completed += _ => replayed++;
                Assert.That(replayed, Is.EqualTo(1));
                Assert.That(value.TakeResult(), Is.EqualTo("copied text"));
            }
        }

        [Test]
        public void LateMulticastObserversAreIsolatedIndividually()
        {
            using var request = new ResourceRequest<string>();
            request.SetSuccess("value");
            var observed = false;
            Action<ResourceRequest<string>> callbacks = _ => throw new InvalidOperationException("resource-late-multicast");
            callbacks += _ => observed = true;
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: resource-late-multicast");
            request.Completed += callbacks;
            Assert.That(observed, Is.True);
        }
    }
}
