using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.Networking;
using YLFramework.Http;

namespace YLFramework.Tests.Http
{
    public class HttpRequestTests
    {
        [Test]
        public void HttpHasOneRuntimeServiceOwnerWithoutCoreOrEditorProductDependencies()
        {
            var runtime = typeof(YLFramework.Save.SaveManager).Assembly;
            var core = typeof(YLFramework.Core.Module).Assembly;
            Assert.That(core, Is.SameAs(runtime));
            foreach (var name in new[] { "HttpManager", "SendWebRequest", "HttpRequestStatus" })
            {
                Assert.That(runtime.GetType("YLFramework.Http." + name), Is.Not.Null, name);
                Assert.That(core.GetType("YLFramework.Core." + name), Is.Null, name);
            }
            var request = runtime.GetType("YLFramework.Http.SendWebRequest", true);
            Assert.That(request.BaseType.GetGenericTypeDefinition(),
                Is.EqualTo(typeof(YLFramework.Async.CustomAsyncOperation<>)));
            foreach (var reference in runtime.GetReferencedAssemblies())
            {
                Assert.That(reference.Name, Is.Not.EqualTo("YLFramework.Core.Runtime"));
                Assert.That(reference.Name.EndsWith(".Editor", StringComparison.Ordinal), Is.False);
                Assert.That(new[] { "netstandard", "mscorlib", "System", "System.Core", "Newtonsoft.Json", "BouncyCastle.Cryptography" }
                    .Contains(reference.Name) || reference.Name.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                    reference.Name.StartsWith("UnityEditor.", StringComparison.Ordinal), Is.True, reference.FullName);
            }
        }

        private sealed class OwnedCertificate : CertificateHandler
        {
        }
        private sealed class BadParameter
        {
            public override string ToString() => throw new UnprintableException();
        }
        private sealed class UnprintableException : Exception
        {
            public override string ToString() => throw new InvalidOperationException("Exception formatting failed.");
        }
        [Test]
        public void RefusedStartCompletesInsteadOfLeavingUnownedTransportPending()
        {
            var request = HttpManager.PostBody("http://127.0.0.1:1/", "{}", null);
            Assert.That(request.IsDone, Is.True, "Non-Play execution must reject and release its request.");
            Assert.That(request.Error, Is.Not.Null.And.Not.Empty);
            Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Invalidated));
            var error = request.Error;
            request.Abort();
            Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Invalidated));
            Assert.That(request.Error, Is.EqualTo(error));
        }

        [Test]
        public void ExceptionFormattingFailureCannotLeaveConstructionPendingOrEscapeTheFactory()
        {
            var pending = (HashSet<SendWebRequest>)typeof(HttpManager)
                .GetField("_pendingRequests", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var previous = new HashSet<SendWebRequest>(pending);
            try
            {
                SendWebRequest request = null;
                Assert.DoesNotThrow(() => request = HttpManager.Get("http://127.0.0.1:1/",
                    new Dictionary<string, object> { { "q", new BadParameter() } }, null));
                Assert.That(request.IsDone, Is.True);
                Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Failed));
                Assert.That(request.Exception, Is.TypeOf<UnprintableException>());
                Assert.That(request.Error, Is.Not.Empty);
                Assert.That(pending.Contains(request), Is.False);
            }
            finally
            {
                foreach (var request in new List<SendWebRequest>(pending))
                {
                    if (!previous.Contains(request))
                    {
                        request.Abort();
                    }
                }
            }
        }

        [Test]
        public void RefusedNativeStartReleasesRequestAndAllHandlersEvenWithAutomaticDisposalDisabled()
        {
            var native = new UnityWebRequest("http://127.0.0.1:1/", "POST");
            var download = new DownloadHandlerBuffer();
            var upload = new UploadHandlerRaw(new byte[] { 1, 2 });
            var certificate = new OwnedCertificate();
            native.downloadHandler = download;
            native.uploadHandler = upload;
            native.certificateHandler = certificate;
            native.disposeDownloadHandlerOnDispose = false;
            native.disposeUploadHandlerOnDispose = false;
            native.disposeCertificateHandlerOnDispose = false;
            try
            {
                Assert.That(Pointer(native), Is.Not.EqualTo(IntPtr.Zero));
                Assert.That(Pointer(download), Is.Not.EqualTo(IntPtr.Zero));
                Assert.That(Pointer(upload), Is.Not.EqualTo(IntPtr.Zero));
                Assert.That(Pointer(certificate), Is.Not.EqualTo(IntPtr.Zero));
                var operation = (SendWebRequest)typeof(HttpManager).GetMethod("SendWebRequest", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { native });
                Assert.That(operation.IsDone, Is.True);
                Assert.That(operation.Status, Is.EqualTo(HttpRequestStatus.Invalidated));
                Assert.That(Pointer(native), Is.EqualTo(IntPtr.Zero));
                Assert.That(Pointer(download), Is.EqualTo(IntPtr.Zero));
                Assert.That(Pointer(upload), Is.EqualTo(IntPtr.Zero));
                Assert.That(Pointer(certificate), Is.EqualTo(IntPtr.Zero));
                operation.Abort();
                operation.Abort();
                Assert.That(operation.Status, Is.EqualTo(HttpRequestStatus.Invalidated));
            }
            finally
            {
                native.Dispose();
                download.Dispose();
                upload.Dispose();
                certificate.Dispose();
            }
        }

        [Test]
        public void ConstructionAndJsonArgumentErrorsReturnStableFailedRequests()
        {
            var invalidUri = HttpManager.Get("not an absolute URI", null, null);
            var nullJson = HttpManager.PostBody("http://127.0.0.1:1/", (string)null, null);
            foreach (var request in new[] { invalidUri, nullJson })
            {
                Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Failed));
                Assert.That(request.IsDone, Is.True);
                Assert.That(request.Exception, Is.InstanceOf<ArgumentException>());
                var error = request.Error;
                request.Abort();
                Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Failed));
                Assert.That(request.Error, Is.EqualTo(error));
            }
        }

        private static IntPtr Pointer(object target)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField("m_Ptr", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    return (IntPtr)field.GetValue(target);
                }
            }
            throw new InvalidOperationException("Native pointer evidence is unavailable for " + target.GetType());
        }
    }
}
