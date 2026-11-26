using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Resource;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Resource.Addressables")]
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
            request.NotifyCompletion();
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
            request.NotifyCompletion();
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

        [TestCase(false)]
        [TestCase(true)]
        public void ProgressReentryCannotRepeatTerminalDetachOrNotification(bool invalidate)
        {
            var session = ResourceEventHistory.StartSession();
            bool recording = ResourceEventHistory.IsRecording;
            ResourceEventHistory.SetRecording(true);
            try
            {
                using var request = new ResourceRequest<string>
                {
                    EventTrace = ResourceEventHistory.Begin(session, ResourceEventSource.Acquire, null, null)
                };
                int reads = 0, detaches = 0, notifications = 0;
                request.Attach(() => detaches++, () =>
                {
                    if (++reads == 1)
                        request.Cancel();
                    return .4f;
                });
                var expected = invalidate ? ResourceRequestStatus.Invalidated : ResourceRequestStatus.Canceled;
                ResourceRequestStatus observed = ResourceRequestStatus.Pending;
                request.Completed += value => { notifications++; observed = value.Status; };
                var awaiter = request.GetAwaiter();
                if (invalidate)
                {
                    var tracked = (IResourceRequest)request;
                    tracked.PrepareInvalidation();
                    Assert.That(notifications, Is.Zero);
                    tracked.ReleaseInvalidatedResult();
                    tracked.NotifyInvalidation();
                    Assert.That(Assert.Throws<ResourceException>(() => awaiter.GetResult()).Kind, Is.EqualTo(ResourceError.SessionUnavailable));
                }
                else
                {
                    request.Cancel();
                    Assert.Throws<OperationCanceledException>(() => awaiter.GetResult());
                }
                Assert.That(ResourceEventHistory.Read().Length, Is.EqualTo(1));
                Assert.That(ResourceEventHistory.Read()[0].Status, Is.EqualTo(expected));
                Assert.That(reads, Is.EqualTo(1));
                Assert.That(detaches, Is.EqualTo(1));
                Assert.That(notifications, Is.EqualTo(1));
                Assert.That(observed, Is.EqualTo(expected));
                Assert.That(request.Progress, Is.EqualTo(.4f));
            }
            finally
            {
                ResourceEventHistory.SetRecording(recording);
                ResourceEventHistory.Clear();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CancelInsidePublicProgressReadKeepsLastCompletedSample(bool hasPreviousSample)
        {
            using var request = new ResourceRequest<int>();
            int reads = 0, detaches = 0, notifications = 0;
            float atCompletion = -1;
            bool cancelDuringRead = false;
            request.Attach(() => detaches++, () =>
            {
                reads++;
                if (!cancelDuringRead)
                    return .25f;
                cancelDuringRead = false;
                request.Cancel();
                return .75f;
            });
            if (hasPreviousSample)
                Assert.That(request.Progress, Is.EqualTo(.25f));
            request.Completed += value => { notifications++; atCompletion = value.Progress; };
            cancelDuringRead = true;
            float expected = hasPreviousSample ? .25f : 0;
            Assert.That(request.Progress, Is.EqualTo(expected));
            Assert.That(atCompletion, Is.EqualTo(expected));
            Assert.That(request.Progress, Is.EqualTo(expected));
            Assert.That(reads, Is.EqualTo(hasPreviousSample ? 2 : 1));
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(detaches, Is.EqualTo(1));
            Assert.That(notifications, Is.EqualTo(1));
        }

        [Test]
        public void InvalidationFreezesSiblingBeforeCallingProviderProgress()
        {
            using var first = new ResourceRequest<int>();
            using var sibling = new ResourceRequest<int>();
            var awaiter = sibling.GetAwaiter();
            int samples = 0, detaches = 0, notifications = 0;
            first.Attach(null, () =>
            {
                samples++;
                sibling.Cancel();
                return .5f;
            });
            sibling.Attach(() => detaches++, null);
            sibling.Completed += _ => notifications++;
            var batch = new IResourceRequest[] { first, sibling };
            foreach (var request in batch)
                request.PrepareInvalidation();
            Assert.That(samples, Is.Zero);
            foreach (var request in batch)
                request.ReleaseInvalidatedResult();
            Assert.That(notifications, Is.Zero);
            foreach (var request in batch)
                request.NotifyInvalidation();
            Assert.That(samples, Is.EqualTo(1));
            Assert.That(detaches, Is.EqualTo(1));
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(Assert.Throws<ResourceException>(() => awaiter.GetResult()).Kind, Is.EqualTo(ResourceError.SessionUnavailable));
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
