using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;
using YLFramework.Foundation;
using YLFramework.Http;
using Object = UnityEngine.Object;
using YLFramework.Async;

namespace YLFramework.Tests.Http
{
    public class HttpRequestPlayModeTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly List<CoroutineStarter> _ownedHosts = new List<CoroutineStarter>();
        private readonly List<SendWebRequest> _ownedRequests = new List<SendWebRequest>();
        private readonly List<Action<UnityWebRequest>> _preSubscriptions = new List<Action<UnityWebRequest>>();
        private readonly List<Action<UnityWebRequest, float>> _postSubscriptions = new List<Action<UnityWebRequest, float>>();
        private CoroutineStarter[] _baselineHosts;
        private LoopbackServer _server;
        private CultureInfo _previousCulture;
        private CultureInfo _previousUICulture;
        private float _previousTimeScale;
        private bool _isIsolated;
        private bool _restoreExecutionGate;
        private bool _previousExecutionStopping;
        private bool _previousHttpEnding;

        private static Type SessionType => typeof(CoroutineStarter).Assembly
            .GetType("YLFramework.Singletons.SingletonSessionState", true);

        [SetUp]
        public void SetUp()
        {
            _isIsolated = false;
            _restoreExecutionGate = false;
            _baselineHosts = FindHosts();
            Assert.That(SessionType.GetProperty("CanRunExecution", PrivateStatic).GetValue(null), Is.True,
                "Fixture must not reopen a pre-existing closed execution gate.");
            Assert.That(PendingRequests(), Is.Empty, "Fixture must not interrupt another HTTP owner.");
            // 验证当前 Unity 的实际字段，不以反射写指针或逻辑完成代替 Dispose。
            foreach (var type in new[] { typeof(UnityWebRequest), typeof(DownloadHandler),
                         typeof(UploadHandler), typeof(CertificateHandler) })
            {
                Assert.That(type.GetField("m_Ptr", PrivateInstance), Is.Not.Null, type.FullName);
            }

            _previousCulture = CultureInfo.CurrentCulture;
            _previousUICulture = CultureInfo.CurrentUICulture;
            _previousTimeScale = Time.timeScale;
            _isIsolated = true;
            _server = new LoopbackServer();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_isIsolated)
            {
                yield break;
            }

            foreach (var callback in _preSubscriptions)
            {
                HttpManager.PreWebRequest -= callback;
            }

            foreach (var callback in _postSubscriptions)
            {
                HttpManager.PostWebRequest -= callback;
            }

            _preSubscriptions.Clear();
            _postSubscriptions.Clear();
            try
            {
                foreach (var request in _ownedRequests)
                {
                    request.Abort();
                }
            }
            finally
            {
                _ownedRequests.Clear();
                CultureInfo.CurrentCulture = _previousCulture;
                CultureInfo.CurrentUICulture = _previousUICulture;
                Time.timeScale = _previousTimeScale;
                foreach (var host in _ownedHosts)
                {
                    if (host)
                    {
                        Object.Destroy(host.gameObject);
                    }
                }

                _ownedHosts.Clear();
                try
                {
                    _server?.Dispose();
                }
                finally
                {
                    _server = null;
                    if (_restoreExecutionGate)
                    {
                        // 全局 ResetForNewSession 会销毁外部宿主并重置其他模块；这里只还原本夹具关闭的门禁。
                        try
                        {
                            typeof(HttpManager).GetField("_isEnding", PrivateStatic)
                                .SetValue(null, _previousHttpEnding);
                        }
                        finally
                        {
                            SessionType.GetField("_isExecutionStopping", PrivateStatic)
                                .SetValue(null, _previousExecutionStopping);
                            _restoreExecutionGate = false;
                        }
                    }
                }
            }
            yield return null;
            Assert.That(FindHosts(), Is.EquivalentTo(_baselineHosts), "Fixture must preserve external coroutine hosts.");
        }

        [UnityTest]
        public IEnumerator HttpCompletesWithoutCreatingCoroutineHost()
        {
            NativeEvidence evidence = null;
            Pre(native => evidence = new NativeEvidence(native));
            var request = Start(() => HttpManager.Get(_server.Url("/echo"), null, null));
            Assert.That(FindHosts(), Is.EquivalentTo(_baselineHosts),
                "HTTP must not change coroutine hosts when sending.");
            yield return WaitFor(request);
            AssertSucceeded(request);
            evidence.AssertReleased();
            Assert.That(FindHosts(), Is.EquivalentTo(_baselineHosts), "HTTP completion must not change coroutine hosts.");
        }

        [UnityTest]
        public IEnumerator ForeignPreCreatedHostIsNotAdoptedByHttpFixture()
        {
            CoroutineStarter foreign = null;
            Action<UnityWebRequest> callback = _ =>
            {
                foreign = new GameObject("ForeignHttpObserverHost").AddComponent<CoroutineStarter>();
                foreign.StartCoroutine(KeepHostAlive());
            };
            HttpManager.PreWebRequest += callback;
            try
            {
                var request = Start(() => HttpManager.Get(_server.Url("/echo"), null, null));
                Assert.That(foreign, Is.Not.Null);
                Assert.That(_ownedHosts, Has.No.Member(foreign),
                    "A host created by another observer is not owned by the HTTP fixture.");
                yield return WaitFor(request);
                AssertSucceeded(request);
                Assert.That(foreign, Is.Not.Null);
            }
            finally
            {
                HttpManager.PreWebRequest -= callback;
                if (foreign)
                {
                    Object.DestroyImmediate(foreign.gameObject);
                }
            }
        }

        [UnityTest]
        public IEnumerator GetPreservesQueryAndFragmentAndEncodesKeysValuesAndNullUsingInvariantCulture()
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            string configuredUrl = null;
            Pre(native => configuredUrl = native.url);
            var request = Start(() => HttpManager.Get(_server.Url("/echo?existing=keep#client-fragment"),
                new Dictionary<string, object>
                {
                    { "key &=#中文", "value +&=#/雪" }, { "empty", "" }, { "nil", null }, { "number", 12.5m }
                }, Headers()));
            yield return WaitFor(request);
            AssertSucceeded(request);
            var echo = Echo(request);
            Assert.That(echo[0], Is.EqualTo("GET"));
            Assert.That(echo[1], Does.StartWith("/echo?existing=keep&"));
            Assert.That(echo[1], Does.Not.Contain("#"));
            var query = ParseForm(echo[1].Substring(echo[1].IndexOf('?') + 1));
            Assert.That(query.Count, Is.EqualTo(5));
            Assert.That(query["existing"], Is.EqualTo("keep"));
            Assert.That(query["key &=#中文"], Is.EqualTo("value +&=#/雪"));
            Assert.That(query["empty"], Is.Empty);
            Assert.That(query["nil"], Is.Empty);
            Assert.That(query["number"], Is.EqualTo("12.5"));
            Assert.That(configuredUrl, Does.EndWith("#client-fragment"));
            Assert.That(echo[3], Is.EqualTo("fixture-header"));

            request = Start(() => HttpManager.Get(_server.Url("/echo?existing=keep#client-fragment"),
                new Dictionary<string, object>(), null));
            yield return WaitFor(request);
            Assert.That(Echo(request)[1], Is.EqualTo("/echo?existing=keep"));
            request = Start(() => HttpManager.Get(_server.Url("/echo"), null, null));
            yield return WaitFor(request);
            Assert.That(Echo(request)[1], Is.EqualTo("/echo"));
        }

        [UnityTest]
        public IEnumerator GetAddsQuerySeparatorWhenPathItselfEndsWithAmpersand()
        {
            var request = Start(() => HttpManager.Get(_server.Url("/path&"),
                new Dictionary<string, object> { { "q", "v" } }, null));
            yield return WaitFor(request);
            Assert.That(Echo(request)[1], Is.EqualTo("/path&?q=v"));
        }

        [UnityTest]
        public IEnumerator CustomHandlerGetterCancellationCannotPublishSnapshotAfterCompletion()
        {
            for (var index = 0; index < 2; index++)
            {
                SendWebRequest request = null;
                NativeEvidence evidence = null;
                var handler = new SnapshotHandler();
                Action cancel = () => request.Abort();
                if (index == 0)
                {
                    handler.ReadText = cancel;
                }
                else
                {
                    handler.ReadData = cancel;
                }

                Action<UnityWebRequest> configure = native =>
                {
                    native.downloadHandler = handler;
                    evidence = new NativeEvidence(native);
                };
                Pre(configure);
                request = Start(() => HttpManager.Get(_server.Url("/echo"), null, null));
                string completedText = null;
                byte[] completedData = null;
                long completedCode = -1;
                var notified = false;
                request.AddCompleteEvent(_ =>
                {
                    notified = true;
                    completedText = request.Text;
                    completedData = request.Data;
                    completedCode = request.ResponseCode;
                    evidence.AssertReleased();
                });
                yield return WaitFor(request);
                Assert.That(notified, Is.True);
                Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Canceled));
                Assert.That(request.Text, Is.EqualTo(completedText));
                Assert.That(request.Data, Is.SameAs(completedData));
                Assert.That(request.ResponseCode, Is.EqualTo(completedCode));
                evidence.AssertReleased();
                HttpManager.PreWebRequest -= configure;
            }
        }

        [UnityTest]
        public IEnumerator CustomHandlerDataSnapshotSurvivesHandlerDisposalMutatingItsArray()
        {
            var handler = new SnapshotHandler { ClearOnDispose = true };
            NativeEvidence evidence = null;
            Pre(native =>
            {
                native.downloadHandler = handler;
                evidence = new NativeEvidence(native);
            });
            var request = Start(() => HttpManager.Get(_server.Url("/echo"), null, null));
            yield return WaitFor(request);
            AssertSucceeded(request);
            evidence.AssertReleased();
            Assert.That(handler.Bytes, Is.EqualTo(new byte[] { 0, 0, 0 }));
            Assert.That(request.Data, Is.EqualTo(new byte[] { 10, 20, 30 }));
            Assert.That(request.Data, Is.Not.SameAs(handler.Bytes));
        }

        [UnityTest]
        public IEnumerator CustomHandlerGetterFailureCannotPublishPartialSnapshot()
        {
            for (var index = 0; index < 2; index++)
            {
                var failure = new InvalidOperationException("http fixture snapshot failure");
                var handler = new SnapshotHandler();
                Action fail = () => throw failure;
                if (index == 0)
                {
                    handler.ReadText = fail;
                }
                else
                {
                    handler.ReadData = fail;
                }

                NativeEvidence evidence = null;
                Action<UnityWebRequest> configure = native =>
                {
                    native.downloadHandler = handler;
                    evidence = new NativeEvidence(native);
                };
                Pre(configure);
                var request = Start(() => HttpManager.Get(_server.Url("/echo"), null, null));
                yield return WaitFor(request);
                Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Failed));
                Assert.That(request.Exception, Is.SameAs(failure));
                Assert.That(request.ResponseCode, Is.Zero);
                Assert.That(request.Text, Is.Null);
                Assert.That(request.Data, Is.Null);
                Assert.That(request.ResponseHeaders, Is.Null);
                evidence.AssertReleased();
                HttpManager.PreWebRequest -= configure;
            }
        }

        [UnityTest]
        public IEnumerator CustomHandlerGetterExecutionEndCannotPublishSnapshotAfterInvalidation()
        {
            NativeEvidence evidence = null;
            var handler = new SnapshotHandler
            {
                ReadText = () =>
                {
                    CloseExecution();
                    InvokeExecutionEnded();
                }
            };
            Pre(native =>
            {
                native.downloadHandler = handler;
                evidence = new NativeEvidence(native);
            });
            var request = Start(() => HttpManager.Get(_server.Url("/echo"), null, null));
            var completions = 0;
            request.AddCompleteEvent(_ =>
            {
                completions++;
                evidence.AssertReleased();
                Assert.That(request.ResponseCode, Is.Zero);
                Assert.That(request.Text, Is.Null);
                Assert.That(request.Data, Is.Null);
                Assert.That(request.ResponseHeaders, Is.Null);
            });
            yield return WaitFor(request);
            InvokeLateCompletion(request);
            Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Invalidated));
            Assert.That(request.ResponseCode, Is.Zero);
            Assert.That(request.Text, Is.Null);
            Assert.That(request.Data, Is.Null);
            Assert.That(request.ResponseHeaders, Is.Null);
            Assert.That(completions, Is.EqualTo(1));
            evidence.AssertReleased();
        }

        private sealed class SnapshotHandler : DownloadHandlerScript
        {
            public Action ReadText;
            public Action ReadData;
            public bool ClearOnDispose;
            public bool ThrowOnDispose;
            public readonly byte[] Bytes = { 10, 20, 30 };
            protected override string GetText()
            {
                ReadText?.Invoke();
                return "custom snapshot";
            }
            protected override byte[] GetData()
            {
                ReadData?.Invoke();
                return Bytes;
            }
            public override void Dispose()
            {
                if (ClearOnDispose)
                {
                    Array.Clear(Bytes, 0, Bytes.Length);
                }

                base.Dispose();
                if (ThrowOnDispose)
                {
                    ThrowOnDispose = false;
                    throw new InvalidOperationException("http fixture dispose failure");
                }
            }
        }

        [UnityTest]
        public IEnumerator FormAndJsonFactoriesSendCorrectMethodsBodiesContentTypesAndHeaders()
        {
            var form = Start(() => HttpManager.PostForm(_server.Url("/echo"),
                new Dictionary<string, string>
                {
                    { "key &=", "a +&=" }, { "empty", "" }, { "nil", null }, { "unicode", "\u96ea" }
                }, Headers()));
            yield return WaitFor(form);
            AssertSucceeded(form);
            var echo = Echo(form);
            Assert.That(echo[0], Is.EqualTo("POST"));
            Assert.That(echo[2], Does.StartWith("application/x-www-form-urlencoded"));
            Assert.That(echo[3], Is.EqualTo("fixture-header"));
            var fields = ParseForm(echo[4]);
            Assert.That(fields.Count, Is.EqualTo(4));
            Assert.That(fields["key &="], Is.EqualTo("a +&="));
            Assert.That(fields["empty"], Is.Empty);
            Assert.That(fields["nil"], Is.Empty);
            Assert.That(fields["unicode"], Is.EqualTo("\u96ea"));

            var body = new Dictionary<string, object> { { "number", 12.5 }, { "text", "a +&=雪" } };
            var factories = new Func<SendWebRequest>[]
            {
                () => HttpManager.PostBody(_server.Url("/echo"), body, Headers()),
                () => HttpManager.PostBody(_server.Url("/echo"), "{\"number\":12.5,\"text\":\"a +&=雪\"}", Headers()),
                () => HttpManager.Put(_server.Url("/echo"), body, Headers()),
                () => HttpManager.Delete(_server.Url("/echo"), body, Headers())
            };
            var methods = new[] { "POST", "POST", "PUT", "DELETE" };
            for (var i = 0; i < factories.Length; i++)
            {
                var request = Start(factories[i]);
                yield return WaitFor(request);
                AssertSucceeded(request);
                echo = Echo(request);
                Assert.That(echo[0], Is.EqualTo(methods[i]));
                Assert.That(echo[2], Does.StartWith("application/json"));
                Assert.That(echo[3], Is.EqualTo("fixture-header"));
                Assert.That(echo[4], Is.EqualTo("{\"number\":12.5,\"text\":\"a +&=雪\"}"));
            }

            var overrides = Headers();
            overrides["Content-Type"] = "application/vnd.fixture+json";
            var overridden = Start(() => HttpManager.PostBody(_server.Url("/echo"), "{}", overrides));
            yield return WaitFor(overridden);
            Assert.That(Echo(overridden)[2], Is.EqualTo("application/vnd.fixture+json"));
            overridden = Start(() => HttpManager.PostForm(_server.Url("/echo"),
                new Dictionary<string, string> { { "a", "b" } }, overrides));
            yield return WaitFor(overridden);
            Assert.That(Echo(overridden)[2], Is.EqualTo("application/vnd.fixture+json"));
        }

        [UnityTest]
        public IEnumerator NullJsonDictionariesRemainLiteralNullBodies()
        {
            var factories = new Func<SendWebRequest>[]
            {
                () => HttpManager.PostBody(_server.Url("/echo"), (Dictionary<string, object>)null, null),
                () => HttpManager.Put(_server.Url("/echo"), null, null),
                () => HttpManager.Delete(_server.Url("/echo"), null, null)
            };
            var methods = new[] { "POST", "PUT", "DELETE" };
            for (var index = 0; index < factories.Length; index++)
            {
                var request = Start(factories[index]);
                yield return WaitFor(request);
                var echo = Echo(request);
                Assert.That(echo[0], Is.EqualTo(methods[index]));
                Assert.That(echo[2], Does.StartWith("application/json"));
                Assert.That(echo[4], Is.EqualTo("null"));
            }
        }

        [UnityTest]
        public IEnumerator SuccessCommitsSnapshotBeforePostAndReleasesNativeBeforeCompletionAndLateNotification()
        {
            NativeEvidence evidence = null;
            SendWebRequest request = null;
            Task<SendWebRequest> completion = null;
            var calls = new List<string>();
            Pre(native =>
            {
                Assert.That(native.timeout, Is.EqualTo(5));
                native.certificateHandler = new FixtureCertificateHandler();
                evidence = new NativeEvidence(native);
                evidence.AssertLive();
            });
            Post((native, seconds) =>
            {
                calls.Add("post");
                Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Succeeded));
                Assert.That(request.ResponseCode, Is.EqualTo(200));
                Assert.That(request.Text, Is.EqualTo(native.downloadHandler.text));
                Assert.That(request.IsDone, Is.False, "Post borrows native resources before base completion.");
                Assert.That(completion.IsCompleted, Is.False, "Task must not complete during Post.");
                Assert.That(seconds, Is.EqualTo(request.ElapsedSeconds));
                Assert.That(seconds, Is.GreaterThanOrEqualTo(0));
                evidence.AssertLive();
            });
            request = Start(() => HttpManager.PostBody(_server.Url("/echo"), "{}", Headers()));
            completion = request.AsTask();
            Assert.That(request.AsTask(), Is.SameAs(completion));
            var waiter = ObserveTask();
            async Task ObserveTask()
            {
                var result = await completion;
                evidence.AssertReleased();
                Assert.That(result, Is.SameAs(request));
                AssertSucceeded(result);
                Assert.That(result.ResponseHeaders["X-Fixture"], Is.EqualTo("loopback"));
                Assert.That(result.Data, Is.EqualTo(Encoding.UTF8.GetBytes(result.Text)));
                calls.Add("task");
            }

            Action<SendWebRequest> first = _ =>
            {
                evidence.AssertReleased();
                Assert.That(completion.IsCompleted, Is.False, "Task signals after ordinary completion callbacks.");
                calls.Add("first");
            };
            request.AddCompleteEvent(first);
            request.AddCompleteEvent(value =>
            {
                evidence.AssertReleased();
                value.Abort();
                calls.Add("registered");
            });
            yield return WaitFor(request);
            yield return WaitUntil(() => waiter.IsCompleted, 5);
            Assert.That(waiter.Status, Is.EqualTo(TaskStatus.RanToCompletion), waiter.Exception?.ToString());
            Assert.That(completion.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            request.RemoveCompleteEvent(first);
            AssertSucceeded(request);
            Assert.That(request.ResponseHeaders["X-Fixture"], Is.EqualTo("loopback"));
            Assert.That(request.Data, Is.EqualTo(Encoding.UTF8.GetBytes(request.Text)));
            request.AddCompleteEvent(_ =>
            {
                evidence.AssertReleased();
                calls.Add("late");
            });
            Assert.That(calls, Is.EqualTo(new[] { "post", "first", "registered", "task", "late" }));
            var text = request.Text;
            var data = (byte[])request.Data.Clone();
            request.Abort();
            yield return null;
            AssertSucceeded(request);
            Assert.That(request.Text, Is.EqualTo(text));
            Assert.That(request.Data, Is.EqualTo(data));
            Assert.That(request.ResponseHeaders["X-Fixture"], Is.EqualTo("loopback"));
            Assert.That(calls.Count, Is.EqualTo(5));
        }

        [UnityTest]
        public IEnumerator DestroyedIndependentCoroutineHostCannotOrphanHttpOrCreateReplacementHost()
        {
            var host = CreateIndependentHost();
            NativeEvidence evidence = null;
            Pre(native => evidence = new NativeEvidence(native));
            var request = Start(() => HttpManager.PostBody(_server.Url("/echo"), "{}", null));
            Assert.That(request.IsDone, Is.False);
            Object.DestroyImmediate(host.gameObject);
            Assert.That(FindHosts(), Is.EquivalentTo(_baselineHosts));
            yield return WaitFor(request);
            AssertSucceeded(request);
            evidence.AssertReleased();
            Assert.That(FindHosts(), Is.EquivalentTo(_baselineHosts), "Native completion must not change other hosts.");
        }

        [UnityTest]
        public IEnumerator ResponseHeadersUseOrdinalIgnoreCaseAndAreReadOnlySnapshots()
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var request = Start(() => HttpManager.Get(_server.Url("/echo"), null, null));
            yield return WaitFor(request);
            AssertSucceeded(request);
            Assert.That(request.ResponseHeaders["x-fixture"], Is.EqualTo("loopback"));
            Assert.That(request.ResponseHeaders["X-FIXTURE"], Is.EqualTo("loopback"));
            Assert.That(request.ResponseHeaders.ContainsKey("x-FiXtUrE"), Is.True);
            Assert.That(request.ResponseHeaders.TryGetValue("X-fixture", out var value), Is.True);
            Assert.That(value, Is.EqualTo("loopback"));
            var mutable = request.ResponseHeaders as IDictionary<string, string>;
            Assert.That(mutable, Is.Not.Null);
            Assert.That(mutable.IsReadOnly, Is.True);
            Assert.Throws<NotSupportedException>(() => mutable["X-Fixture"] = "changed");
            Assert.Throws<NotSupportedException>(() => mutable.Add("X-New", "changed"));
            Assert.Throws<NotSupportedException>(() => mutable.Remove("X-Fixture"));
            Assert.Throws<NotSupportedException>(() => mutable.Clear());
            Assert.That(request.ResponseHeaders["x-fixture"], Is.EqualTo("loopback"));
        }

        [UnityTest]
        public IEnumerator NoContent204CompletesWithEmptySnapshotAndReleasedNative()
        {
            NativeEvidence evidence = null;
            var posts = 0;
            Pre(native => evidence = new NativeEvidence(native));
            Post((native, seconds) =>
            {
                posts++;
                evidence.AssertLive();
                Assert.That(native.responseCode, Is.EqualTo(204));
            });
            var request = Start(() => HttpManager.Get(_server.Url("/no-content"), null, null));
            yield return WaitFor(request);
            Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Succeeded), request.Error);
            Assert.That(request.NativeResult, Is.EqualTo(UnityWebRequest.Result.Success));
            Assert.That(request.ResponseCode, Is.EqualTo(204));
            Assert.That(request.Error, Is.Null.Or.Empty);
            Assert.That(request.Text, Is.Null.Or.Empty);
            Assert.That(request.Data, Is.Null.Or.Empty);
            Assert.That(request.ResponseHeaders["X-Fixture"], Is.EqualTo("loopback"));
            Assert.That(posts, Is.EqualTo(1));
            evidence.AssertReleased();
        }

        [UnityTest]
        public IEnumerator NativeLateCompletedSubscriptionRunsSynchronouslyWhileTransportIsLive()
        {
            NativeEvidence evidence;
            using (var native = UnityWebRequest.Get(_server.Url("/echo")))
            {
                evidence = new NativeEvidence(native);
                var operation = native.SendWebRequest();
                var firstCalls = 0;
                Action<AsyncOperation> first = _ => firstCalls++;
                operation.completed += first;
                try
                {
                    yield return WaitUntil(() => firstCalls == 1, 5);
                    Assert.That(native.result, Is.EqualTo(UnityWebRequest.Result.Success), native.error);
                    var returned = false;
                    var lateCalls = 0;
                    Action<AsyncOperation> late = value =>
                    {
                        Assert.That(returned, Is.False, "A late native subscription invokes inline.");
                        Assert.That(value, Is.SameAs(operation));
                        evidence.AssertLive();
                        lateCalls++;
                    };
                    try
                    {
                        operation.completed += late;
                        returned = true;
                        Assert.That(lateCalls, Is.EqualTo(1));
                        yield return null;
                        Assert.That(lateCalls, Is.EqualTo(1));
                    }
                    finally
                    {
                        operation.completed -= late;
                    }
                }
                finally
                {
                    operation.completed -= first;
                }
            }
            evidence.AssertReleased();
        }

        [UnityTest]
        public IEnumerator ProtocolFailureKeepsErrorBodyResponseCodeHeadersAndReleasesHandlers()
        {
            NativeEvidence evidence = null;
            Pre(native => evidence = new NativeEvidence(native));
            var request = Start(() => HttpManager.Get(_server.Url("/error"), null, null));
            var completion = request.AsTask();
            yield return WaitFor(request);
            Assert.That(completion.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(completion.GetAwaiter().GetResult(), Is.SameAs(request));
            Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Failed));
            Assert.That(request.Error, Is.Not.Null.And.Not.Empty);
            Assert.That(request.ResponseCode, Is.EqualTo(503));
            Assert.That(request.Text, Is.EqualTo("fixture protocol failure"));
            Assert.That(request.Data, Is.EqualTo(Encoding.UTF8.GetBytes("fixture protocol failure")));
            Assert.That(request.ResponseHeaders["X-Fixture"], Is.EqualTo("loopback"));
            evidence.AssertReleased();
        }

        [UnityTest]
        public IEnumerator TimeoutUsesPreConfiguredNativeTimeoutAndActuallyStopsSlowTransport()
        {
            Time.timeScale = 0;
            NativeEvidence evidence = null;
            Pre(native =>
            {
                Assert.That(native.timeout, Is.EqualTo(5));
                native.timeout = 1;
                evidence = new NativeEvidence(native);
            });
            var started = Time.realtimeSinceStartup;
            var request = Start(() => HttpManager.Get(_server.Url("/slow"), null, null));
            yield return WaitFor(request, 8);
            Assert.That(_server.RequestCount, Is.EqualTo(1), "The real loopback transport must have started.");
            Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Failed));
            Assert.That(request.Error, Is.Not.Null.And.Not.Empty);
            Assert.That(Time.realtimeSinceStartup - started, Is.GreaterThanOrEqualTo(0.5f).And.LessThan(8));
            Assert.That(request.ElapsedSeconds, Is.GreaterThanOrEqualTo(0.5f).And.LessThan(8));
            evidence.AssertReleased();
        }

        [UnityTest]
        public IEnumerator AbortIsIdempotentAndDoesNotCancelAnotherRequestOrAcceptLateNativeCompletion()
        {
            var evidence = new List<NativeEvidence>();
            Pre(native => evidence.Add(new NativeEvidence(native)));
            var canceled = Start(() => HttpManager.Get(_server.Url("/slow"), null, null));
            var completion = canceled.AsTask();
            yield return WaitUntil(() => _server.RequestCount == 1, 5);
            var survivor = Start(() => HttpManager.Get(_server.Url("/echo"), null, null));
            var completions = 0;
            canceled.AddCompleteEvent(_ =>
            {
                evidence[0].AssertReleased();
                completions++;
            });
            Assert.That(canceled.Status, Is.EqualTo(HttpRequestStatus.Pending));
            Assert.That(survivor.Status, Is.EqualTo(HttpRequestStatus.Pending));
            canceled.Abort();
            Assert.That(completion.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(completion.GetAwaiter().GetResult(), Is.SameAs(canceled));
            evidence[0].AssertReleased();
            var error = canceled.Error;
            var text = canceled.Text;
            var data = canceled.Data;
            var headers = canceled.ResponseHeaders;
            var seconds = canceled.ElapsedSeconds;
            canceled.Abort();
            InvokeLateCompletion(canceled);
            yield return WaitFor(survivor);
            yield return null;
            Assert.That(canceled.IsDone, Is.True);
            Assert.That(canceled.Status, Is.EqualTo(HttpRequestStatus.Canceled));
            Assert.That(canceled.Error, Is.EqualTo(error));
            Assert.That(canceled.Text, Is.EqualTo(text));
            Assert.That(canceled.Data, Is.SameAs(data));
            Assert.That(canceled.ResponseHeaders, Is.SameAs(headers));
            Assert.That(canceled.ElapsedSeconds, Is.EqualTo(seconds));
            Assert.That(completions, Is.EqualTo(1));
            evidence[0].AssertReleased();
            evidence[1].AssertReleased();
            AssertSucceeded(survivor);
        }

        [UnityTest]
        public IEnumerator PreCanAbortRegisteredRequestBeforeFactoryReturns()
        {
            NativeEvidence original = null;
            NativeEvidence evidence = null;
            SendWebRequest inPre = null;
            var returned = false;
            Pre(native =>
            {
                Assert.That(returned, Is.False);
                original = new NativeEvidence(native);
                native.downloadHandler = new DownloadHandlerBuffer();
                native.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("replacement"));
                evidence = new NativeEvidence(native);
                var pending = PendingRequests();
                Assert.That(pending.Count, Is.EqualTo(1));
                inPre = pending[0];
                inPre.Abort();
                original.AssertReleased();
                evidence.AssertReleased();
            });
            var request = Start(() => HttpManager.PostBody(_server.Url("/echo"), "{}", null));
            returned = true;
            Assert.That(request, Is.SameAs(inPre));
            Assert.That(request.IsDone, Is.True);
            Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Canceled));
            Assert.That(PendingRequests(), Is.Empty);
            InvokeLateCompletion(request);
            yield return null;
            Assert.That(_server.RequestCount, Is.Zero);
            original.AssertReleased();
            evidence.AssertReleased();
        }

        [UnityTest]
        public IEnumerator ClosedGateRejectsBeforeStartWithoutInvokingHooksOrCreatingHost()
        {
            var preCalled = false;
            Pre(_ => preCalled = true);
            CloseExecution();
            var native = new UnityWebRequest(_server.Url("/echo"), "POST",
                new DownloadHandlerBuffer(), new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}")));
            native.certificateHandler = new FixtureCertificateHandler();
            var evidence = new NativeEvidence(native);
            evidence.AssertLive();
            var request = Start(() => SendOwnedNative(native));
            Assert.That(request.IsDone, Is.True);
            Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Invalidated));
            Assert.That(request.Error, Is.Not.Null.And.Not.Empty);
            Assert.That(preCalled, Is.False);
            Assert.That(FindHosts(), Is.EquivalentTo(_baselineHosts));
            Assert.That(PendingRequests(), Is.Empty);
            evidence.AssertReleased();
            yield return null;
            Assert.That(_server.RequestCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator PreExecutionEndInvalidatesRegisteredRequestAndRejectsReentrantNewRequest()
        {
            NativeEvidence evidence = null;
            NativeEvidence rejectedEvidence = null;
            SendWebRequest rejected = null;
            Pre(native =>
            {
                evidence = new NativeEvidence(native);
                Assert.That(FindHosts(), Is.EquivalentTo(_baselineHosts), "HTTP must not change hosts before Pre.");
                CloseExecution();
                InvokeExecutionEnded();
                var rejectedNative = UnityWebRequest.Get(_server.Url("/echo"));
                rejectedEvidence = new NativeEvidence(rejectedNative);
                rejected = Start(() => SendOwnedNative(rejectedNative));
            });
            var request = Start(() => HttpManager.Get(_server.Url("/slow"), null, null));
            Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Invalidated));
            Assert.That(request.IsDone, Is.True);
            Assert.That(rejected.Status, Is.EqualTo(HttpRequestStatus.Invalidated));
            Assert.That(rejected.IsDone, Is.True);
            evidence.AssertReleased();
            rejectedEvidence.AssertReleased();
            Assert.That(PendingRequests(), Is.Empty);
            yield return null;
            Assert.That(_server.RequestCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator PreDeactivatingIndependentHostDoesNotRejectHttpOrCreateReplacementHost()
        {
            var host = CreateIndependentHost();
            var hostsBeforeHttp = FindHosts();
            var evidence = new List<NativeEvidence>();
            Pre(native =>
            {
                evidence.Add(new NativeEvidence(native));
                host.gameObject.SetActive(false);
            });
            var request = Start(() => HttpManager.Get(_server.Url("/echo"), null, null));
            yield return WaitFor(request);
            AssertSucceeded(request);
            evidence[0].AssertReleased();
            var native = UnityWebRequest.Get(_server.Url("/echo"));
            var independent = Start(() => SendOwnedNative(native));
            yield return WaitFor(independent);
            AssertSucceeded(independent);
            evidence[1].AssertReleased();
            Assert.That(_server.RequestCount, Is.EqualTo(2));
            Assert.That(host.gameObject.activeInHierarchy, Is.False);
            Assert.That(FindHosts(), Is.EquivalentTo(hostsBeforeHttp),
                "HTTP must preserve the inactive owned host and external hosts.");
        }

        [UnityTest]
        public IEnumerator PreExceptionPreservesOriginalFailureAndReleasesReplacedAndCurrentHandlers()
        {
            NativeEvidence original = null;
            NativeEvidence replacement = null;
            var failure = new InvalidOperationException("http fixture pre failure");
            var laterPre = false;
            Pre(native =>
            {
                original = new NativeEvidence(native);
                native.downloadHandler = new DownloadHandlerBuffer();
                native.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("replacement"));
                native.certificateHandler = new FixtureCertificateHandler();
                native.disposeDownloadHandlerOnDispose = false;
                native.disposeUploadHandlerOnDispose = false;
                native.disposeCertificateHandlerOnDispose = false;
                replacement = new NativeEvidence(native);
                throw failure;
            });
            Pre(_ => laterPre = true);
            var nativeRequest = new UnityWebRequest(_server.Url("/echo"), "POST",
                new DownloadHandlerBuffer(), new UploadHandlerRaw(Encoding.UTF8.GetBytes("original")));
            nativeRequest.certificateHandler = new FixtureCertificateHandler();
            var request = Start(() => SendOwnedNative(nativeRequest));
            Assert.That(request.IsDone, Is.True);
            Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Failed));
            Assert.That(request.Exception, Is.SameAs(failure));
            Assert.That(request.Error, Does.Contain("http fixture pre failure"));
            Assert.That(laterPre, Is.False, "Failed configuration must not continue with later pre hooks.");
            original.AssertReleased();
            replacement.AssertReleased();
            Assert.That(PendingRequests(), Is.Empty);
            yield return null;
            Assert.That(_server.RequestCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator PostExceptionIsReportedWithoutChangingSuccessOrSkippingCleanupAndCompletion()
        {
            NativeEvidence evidence = null;
            Pre(native => evidence = new NativeEvidence(native));
            Post((native, seconds) => throw new InvalidOperationException("http fixture post failure"));
            var laterPost = false;
            Post((native, seconds) =>
            {
                evidence.AssertLive();
                laterPost = true;
            });
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: http fixture post failure"));
            var request = Start(() => HttpManager.PostBody(_server.Url("/echo"), "{}", null));
            var completed = false;
            request.AddCompleteEvent(_ =>
            {
                evidence.AssertReleased();
                completed = true;
            });
            yield return WaitFor(request);
            AssertSucceeded(request);
            Assert.That(request.Exception, Is.Null);
            Assert.That(completed, Is.True);
            Assert.That(laterPost, Is.True);
            evidence.AssertReleased();
        }

        [UnityTest]
        public IEnumerator HandlerDisposeFailureStillReleasesOtherOwnedResourcesAndCompletesTask()
        {
            NativeEvidence original = null;
            NativeEvidence replacement = null;
            Pre(native =>
            {
                original = new NativeEvidence(native);
                native.downloadHandler = new SnapshotHandler { ThrowOnDispose = true };
                native.certificateHandler = new FixtureCertificateHandler();
                replacement = new NativeEvidence(native);
            });
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: http fixture dispose failure"));
            var request = Start(() => HttpManager.PostBody(_server.Url("/echo"), "{}", null));
            var completion = request.AsTask();
            var notified = false;
            request.AddCompleteEvent(_ =>
            {
                original.AssertReleased();
                replacement.AssertReleased();
                notified = true;
            });
            yield return WaitFor(request);
            AssertSucceeded(request);
            Assert.That(request.Exception, Is.Null);
            Assert.That(request.Text, Is.EqualTo("custom snapshot"));
            Assert.That(notified, Is.True);
            Assert.That(completion.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            original.AssertReleased();
            replacement.AssertReleased();
        }

        [UnityTest]
        public IEnumerator CompletionExceptionKeepsRegistrationOrderAndInterruptsLaterCallbacksButNotCleanup()
        {
            NativeEvidence evidence = null;
            Pre(native => evidence = new NativeEvidence(native));
            var calls = new List<string>();
            var request = Start(() => HttpManager.Get(_server.Url("/echo"), null, null));
            var completion = request.AsTask();
            Action<SendWebRequest> first = _ =>
            {
                evidence.AssertReleased();
                calls.Add("first");
            };
            request.AddCompleteEvent(first);
            request.AddCompleteEvent(_ =>
            {
                evidence.AssertReleased();
                calls.Add("throw");
                throw new InvalidOperationException("http fixture completion failure");
            });
            request.AddCompleteEvent(_ => calls.Add("must not run"));
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: http fixture completion failure"));
            yield return WaitFor(request);
            request.RemoveCompleteEvent(first);
            AssertSucceeded(request);
            Assert.That(request.Exception, Is.Null);
            Assert.That(completion.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(completion.GetAwaiter().GetResult(), Is.SameAs(request));
            evidence.AssertReleased();
            Assert.That(calls, Is.EqualTo(new[] { "first", "throw" }));
            request.AddCompleteEvent(_ => calls.Add("late"));
            request.Abort();
            InvokeLateCompletion(request);
            Assert.That(calls, Is.EqualTo(new[] { "first", "throw", "late" }));
        }

        [UnityTest]
        public IEnumerator RemovedCompletionCallbackCannotInterruptOtherCallbacksOrLeakNative()
        {
            NativeEvidence evidence = null;
            Pre(native => evidence = new NativeEvidence(native));
            var request = Start(() => HttpManager.Get(_server.Url("/echo"), null, null));
            var registered = false;
            Action<SendWebRequest> removed = _ =>
            {
                evidence.AssertReleased();
                throw new InvalidOperationException("removed http fixture callback");
            };
            request.AddCompleteEvent(removed);
            request.AddCompleteEvent(_ => registered = true);
            request.RemoveCompleteEvent(removed);
            yield return WaitFor(request);
            AssertSucceeded(request);
            Assert.That(registered, Is.True);
            evidence.AssertReleased();
        }

        [UnityTest]
        public IEnumerator PostReentrantAbortDoesNotReleaseBorrowedNativeEarlyAndCanStartIndependentRequest()
        {
            var evidence = new List<NativeEvidence>();
            Pre(native => evidence.Add(new NativeEvidence(native)));
            SendWebRequest first = null;
            SendWebRequest nested = null;
            var firstPost = false;
            Post((native, seconds) =>
            {
                if (firstPost)
                {
                    return;
                }

                firstPost = true;
                Assert.That(first.Status, Is.EqualTo(HttpRequestStatus.Succeeded));
                first.Abort();
                evidence[0].AssertLive();
                Assert.That(native.downloadHandler.text, Is.EqualTo(first.Text));
                nested = Start(() => HttpManager.Get(_server.Url("/echo?nested=yes"), null, null));
                evidence[0].AssertLive();
            });
            first = Start(() => HttpManager.Get(_server.Url("/echo"), null, null));
            var completed = false;
            first.AddCompleteEvent(_ =>
            {
                evidence[0].AssertReleased();
                completed = true;
            });
            yield return WaitFor(first);
            Assert.That(nested, Is.Not.Null);
            yield return WaitFor(nested);
            AssertSucceeded(first);
            AssertSucceeded(nested);
            Assert.That(completed, Is.True);
            evidence[0].AssertReleased();
            evidence[1].AssertReleased();
            Assert.That(Echo(nested)[1], Is.EqualTo("/echo?nested=yes"));
        }

        [UnityTest]
        public IEnumerator ExecutionEndInvalidatesInFlightRequestsAndLateCallbacksCannotChangeTheirSnapshots()
        {
            // 不把同源连接是否并行发送作为取消合同的前置；两条真实传输各自使用独立端口。
            using var secondServer = new LoopbackServer();
            var evidence = new List<NativeEvidence>();
            Pre(native => evidence.Add(new NativeEvidence(native)));
            var first = Start(() => HttpManager.Get(_server.Url("/slow"), null, null));
            var second = Start(() => HttpManager.PostBody(secondServer.Url("/slow"), "{}", null));
            var deadline = Time.realtimeSinceStartup + 5;
            while ((_server.RequestCount != 1 || secondServer.RequestCount != 1) && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            _server.AssertHealthy();
            secondServer.AssertHealthy();
            Assert.That(new[] { _server.RequestCount, secondServer.RequestCount }, Is.EqualTo(new[] { 1, 1 }),
                $"Both owned slow requests must reach their servers before closing execution; first={first.Status}/{first.ResponseCode}/{first.Error}, second={second.Status}/{second.ResponseCode}/{second.Error}; first: {_server.Activity}; second: {secondServer.Activity}.");
            var completed = 0;
            first.AddCompleteEvent(_ =>
            {
                evidence[0].AssertReleased();
                completed++;
            });
            second.AddCompleteEvent(_ =>
            {
                evidence[1].AssertReleased();
                completed++;
            });
            CloseExecution();
            InvokeExecutionEnded();
            var error = first.Error;
            var seconds = first.ElapsedSeconds;
            var data = first.Data;
            InvokeExecutionEnded();
            InvokeLateCompletion(first);
            InvokeLateCompletion(second);
            first.Abort();
            second.Abort();
            yield return null;
            Assert.That(first.IsDone && second.IsDone, Is.True);
            Assert.That(first.Status, Is.EqualTo(HttpRequestStatus.Invalidated));
            Assert.That(second.Status, Is.EqualTo(HttpRequestStatus.Invalidated));
            Assert.That(first.Error, Is.EqualTo(error));
            Assert.That(first.ElapsedSeconds, Is.EqualTo(seconds));
            Assert.That(first.Data, Is.SameAs(data));
            Assert.That(first.ResponseCode, Is.Zero);
            Assert.That(completed, Is.EqualTo(2));
            Assert.That(PendingRequests(), Is.Empty);
            evidence[0].AssertReleased();
            evidence[1].AssertReleased();
        }

        private SendWebRequest Start(Func<SendWebRequest> factory)
        {
            var request = factory();
            _ownedRequests.Add(request);
            return request;
        }

        private CoroutineStarter CreateIndependentHost()
        {
            var host = new GameObject("YLHttpFixture.IndependentCoroutineHost").AddComponent<CoroutineStarter>();
            _ownedHosts.Add(host);
            Assert.That(host.StartCoroutine(KeepHostAlive()), Is.Not.Null);
            return host;
        }

        private static IEnumerator KeepHostAlive()
        {
            while (true)
            {
                yield return null;
            }
        }

        private void Pre(Action<UnityWebRequest> callback)
        {
            _preSubscriptions.Add(callback);
            HttpManager.PreWebRequest += callback;
        }

        private void Post(Action<UnityWebRequest, float> callback)
        {
            _postSubscriptions.Add(callback);
            HttpManager.PostWebRequest += callback;
        }

        private IEnumerator WaitFor(SendWebRequest request, float timeout = 5)
        {
            yield return WaitUntil(() => request.IsDone, timeout);
            Assert.That(request.keepWaiting, Is.False);
            Assert.That(request.Progress, Is.EqualTo(1));
            Assert.That(PendingRequests().Contains(request), Is.False);
        }

        private IEnumerator WaitUntil(Func<bool> condition, float timeout)
        {
            var deadline = Time.realtimeSinceStartup + timeout;
            while (!condition() && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(condition(), Is.True, "Bounded realtime wait expired.");
            _server.AssertHealthy();
        }

        private void CloseExecution()
        {
            if (!_restoreExecutionGate)
            {
                _previousExecutionStopping = (bool)SessionType.GetField("_isExecutionStopping", PrivateStatic)
                    .GetValue(null);
                _previousHttpEnding = (bool)typeof(HttpManager).GetField("_isEnding", PrivateStatic).GetValue(null);
                _restoreExecutionGate = true;
            }
            SessionType.GetMethod("StopExecution", PrivateStatic).Invoke(null, null);
        }

        private static void InvokeExecutionEnded() => typeof(HttpManager)
            .GetMethod("OnExecutionEnded", PrivateStatic).Invoke(null, null);

        private static void InvokeLateCompletion(SendWebRequest request) => typeof(SendWebRequest)
            .GetMethod("OnNativeCompleted", PrivateInstance).Invoke(request, new object[] { null });

        private static SendWebRequest SendOwnedNative(UnityWebRequest native) => (SendWebRequest)typeof(HttpManager)
            .GetMethod("SendWebRequest", PrivateStatic).Invoke(null, new object[] { native });

        private static List<SendWebRequest> PendingRequests()
        {
            var pending = typeof(HttpManager).GetField("_pendingRequests", PrivateStatic);
            Assert.That(pending, Is.Not.Null);
            return new List<SendWebRequest>((HashSet<SendWebRequest>)pending.GetValue(null));
        }

        private static CoroutineStarter[] FindHosts() => Object.FindObjectsByType<CoroutineStarter>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        private static Dictionary<string, string> Headers() =>
            new Dictionary<string, string> { { "X-Test", "fixture-header" } };

        private static void AssertSucceeded(SendWebRequest request)
        {
            Assert.That(request.Status, Is.EqualTo(HttpRequestStatus.Succeeded), request.Error);
            Assert.That(request.IsDone, Is.True);
            Assert.That(request.Error, Is.Null.Or.Empty);
            Assert.That(request.ResponseCode, Is.EqualTo(200));
        }

        private static string[] Echo(SendWebRequest request)
        {
            AssertSucceeded(request);
            var result = request.Text.Split(new[] { '\n' }, 5);
            Assert.That(result.Length, Is.EqualTo(5));
            return result;
        }

        private static Dictionary<string, string> ParseForm(string text)
        {
            var result = new Dictionary<string, string>();
            foreach (var pair in text.Split('&'))
            {
                var separator = pair.IndexOf('=');
                Assert.That(separator, Is.GreaterThanOrEqualTo(0));
                result.Add(Uri.UnescapeDataString(pair.Substring(0, separator).Replace("+", " ")),
                    Uri.UnescapeDataString(pair.Substring(separator + 1).Replace("+", " ")));
            }
            return result;
        }

        private sealed class FixtureCertificateHandler : CertificateHandler
        {
        }

        private sealed class NativeEvidence
        {
            private readonly UnityWebRequest _request;
            private readonly DownloadHandler _download;
            private readonly UploadHandler _upload;
            private readonly CertificateHandler _certificate;

            public NativeEvidence(UnityWebRequest request)
            {
                this._request = request;
                _download = request.downloadHandler;
                _upload = request.uploadHandler;
                _certificate = request.certificateHandler;
            }

            public void AssertLive()
            {
                Assert.That(Pointer(_request), Is.Not.EqualTo(IntPtr.Zero));
                if (_download != null)
                {
                    Assert.That(Pointer(_download), Is.Not.EqualTo(IntPtr.Zero));
                }

                if (_upload != null)
                {
                    Assert.That(Pointer(_upload), Is.Not.EqualTo(IntPtr.Zero));
                }

                if (_certificate != null)
                {
                    Assert.That(Pointer(_certificate), Is.Not.EqualTo(IntPtr.Zero));
                }
            }

            public void AssertReleased()
            {
                Assert.That(Pointer(_request), Is.EqualTo(IntPtr.Zero), "UnityWebRequest native allocation");
                if (_download != null)
                {
                    Assert.That(Pointer(_download), Is.EqualTo(IntPtr.Zero), "DownloadHandler native allocation");
                }

                if (_upload != null)
                {
                    Assert.That(Pointer(_upload), Is.EqualTo(IntPtr.Zero), "UploadHandler native allocation");
                }

                if (_certificate != null)
                {
                    Assert.That(Pointer(_certificate), Is.EqualTo(IntPtr.Zero), "CertificateHandler native allocation");
                }
            }

            private static IntPtr Pointer(object value)
            {
                for (var type = value.GetType(); type != null; type = type.BaseType)
                {
                    var field = type.GetField("m_Ptr", PrivateInstance | BindingFlags.DeclaredOnly);
                    if (field != null)
                    {
                        return (IntPtr)field.GetValue(value);
                    }
                }
                throw new InvalidOperationException("Unity native m_Ptr field was not found.");
            }
        }

        // 后台仅处理 .NET socket/HTTP 字节；Unity 对象和测试断言都留在测试主线程。
        private sealed class LoopbackServer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
            private readonly object _sync = new object();
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly List<Task> _workers = new List<Task>();
            private readonly List<Exception> _failures = new List<Exception>();
            private readonly List<string> _activity = new List<string>();
            private readonly Task _acceptTask;
            private readonly int _port;
            private int _requestCount;
            private bool _isDisposed;

            public int RequestCount => Volatile.Read(ref _requestCount);

            public string Activity
            {
                get
                {
                    lock (_sync)
                        return string.Join(" | ", _activity);
                }
            }

            public LoopbackServer()
            {
                _listener.Start();
                _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _acceptTask = Task.Run(AcceptLoop);
            }

            public string Url(string path) => "http://127.0.0.1:" + _port.ToString(CultureInfo.InvariantCulture) + path;

            public void AssertHealthy()
            {
                lock (_sync)
                {
                    Assert.That(_failures, Is.Empty, "Unexpected loopback fixture failure.");
                }
            }

            private async Task AcceptLoop()
            {
                try
                {
                    while (!_cancellation.IsCancellationRequested)
                    {
                        var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        lock (_sync)
                        {
                            if (_cancellation.IsCancellationRequested)
                            {
                                client.Dispose();
                                break;
                            }
                            _clients.Add(client);
                            _activity.Add("accepted");
                            _workers.Add(Task.Run(() => Serve(client)));
                        }
                    }
                }
                catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested) { }
                catch (SocketException) when (_cancellation.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    lock (_sync)
                    {
                        _failures.Add(exception);
                    }
                }
            }

            private async Task Serve(TcpClient client)
            {
                try
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var headerBytes = new List<byte>();
                        var single = new byte[1];
                        while (headerBytes.Count < 32768)
                        {
                            if (await stream.ReadAsync(single, 0, 1, _cancellation.Token).ConfigureAwait(false) == 0)
                            {
                                return;
                            }

                            headerBytes.Add(single[0]);
                            var count = headerBytes.Count;
                            if (count >= 4 && headerBytes[count - 4] == 13 && headerBytes[count - 3] == 10 &&
                                headerBytes[count - 2] == 13 && headerBytes[count - 1] == 10)
                            {
                                break;
                            }
                        }
                        if (headerBytes.Count >= 32768)
                        {
                            throw new InvalidDataException("Request headers exceed fixture limit.");
                        }

                        var lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                        var first = lines[0].Split(' ');
                        if (first.Length != 3)
                        {
                            throw new InvalidDataException("Malformed request line.");
                        }
                        lock (_sync)
                            _activity.Add(first[0] + " headers");

                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        for (var i = 1; i < lines.Length && lines[i].Length != 0; i++)
                        {
                            var separator = lines[i].IndexOf(':');
                            if (separator <= 0)
                            {
                                throw new InvalidDataException("Malformed request header.");
                            }

                            headers[lines[i].Substring(0, separator)] = lines[i].Substring(separator + 1).Trim();
                        }
                        if (headers.TryGetValue("Expect", out var expect) &&
                            expect.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                            await stream.WriteAsync(interim, 0, interim.Length, _cancellation.Token).ConfigureAwait(false);
                            lock (_sync)
                                _activity.Add(first[0] + " continue sent");
                        }
                        var length = headers.TryGetValue("Content-Length", out var contentLength)
                            ? int.Parse(contentLength, CultureInfo.InvariantCulture) : 0;
                        if (length < 0 || length > 65536)
                        {
                            throw new InvalidDataException("Request body exceeds fixture limit.");
                        }

                        if (headers.ContainsKey("Transfer-Encoding"))
                        {
                            throw new InvalidDataException("Fixture expects Content-Length.");
                        }

                        var body = new byte[length];
                        var offset = 0;
                        while (offset < length)
                        {
                            var read = await stream.ReadAsync(body, offset, length - offset, _cancellation.Token).ConfigureAwait(false);
                            if (read == 0)
                            {
                                return;
                            }

                            offset += read;
                        }
                        Interlocked.Increment(ref _requestCount);
                        lock (_sync)
                            _activity.Add(first[0] + " body received");
                        if (first[1].StartsWith("/slow", StringComparison.Ordinal))
                        {
                            await Task.Delay(15000, _cancellation.Token).ConfigureAwait(false);
                        }

                        var error = first[1].StartsWith("/error", StringComparison.Ordinal);
                        var noContent = first[1].StartsWith("/no-content", StringComparison.Ordinal);
                        headers.TryGetValue("Content-Type", out var contentType);
                        headers.TryGetValue("X-Test", out var testHeader);
                        var text = error ? "fixture protocol failure" : first[0] + "\n" + first[1] + "\n" +
                            (contentType ?? "") + "\n" + (testHeader ?? "") + "\n" + Encoding.UTF8.GetString(body);
                        var responseBody = noContent ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(text);
                        var status = error ? "503 Service Unavailable" : noContent ? "204 No Content" : "200 OK";
                        var responseHeader = Encoding.ASCII.GetBytes("HTTP/1.1 " + status +
                            "\r\nContent-Type: text/plain; charset=utf-8\r\nX-Fixture: loopback\r\nContent-Length: " +
                            responseBody.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(responseHeader, 0, responseHeader.Length, _cancellation.Token).ConfigureAwait(false);
                        await stream.WriteAsync(responseBody, 0, responseBody.Length, _cancellation.Token).ConfigureAwait(false);
                    }
                }
                // 客户端取消/超时会关闭连接；仅网络断开是允许的，解析错误仍报告。
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested) { }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    lock (_sync)
                    {
                        _failures.Add(exception);
                    }
                }
                finally
                {
                    lock (_sync)
                    {
                        _clients.Remove(client);
                    }
                }
            }

            public void Dispose()
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                _cancellation.Cancel();
                _listener.Stop();
                lock (_sync)
                {
                    foreach (var client in _clients)
                    {
                        client.Dispose();
                    }
                }

                var acceptStopped = _acceptTask.Wait(5000);
                Task[] running;
                lock (_sync)
                {
                    running = _workers.ToArray();
                }

                var workersStopped = Task.WaitAll(running, 5000);
                _cancellation.Dispose();
                Assert.That(acceptStopped && workersStopped, Is.True, "Own loopback tasks must stop before fixture ends.");
                AssertHealthy();
            }
        }
    }
}
