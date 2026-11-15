using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Async;
using YLFramework.Http;

namespace YLFramework.Tests.Http
{
    public class HttpSessionTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string OwnedScene = "Assets/YLTestingProfile/Empty.unity";
        private readonly List<UnityWebRequest> _transports = new();
        private readonly List<DownloadHandler> _downloads = new();
        private TcpListener _listener;
        private bool _optionsEnabled;
        private EnterPlayModeOptions _options;
        private Action<UnityWebRequest> _pre;
        private Action<UnityWebRequest, float> _post;
        private SceneSetup[] _previousScenes;

        private static Type SessionType => typeof(CoroutineStarter).Assembly
            .GetType("YLFramework.Singletons.SingletonSessionState", true);

        [SetUp]
        public void SetUp()
        {
            _optionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _options = EditorSettings.enterPlayModeOptions;
            Assert.That(Enumerable.Range(0, SceneManager.sceneCount)
                .All(index => !SceneManager.GetSceneAt(index).isDirty), Is.True,
                "Never save or discard dirty scene contents.");
            var preparation = SceneManager.GetSceneByPath(OwnedScene);
            Assert.That(preparation.IsValid() && preparation.isLoaded, Is.True,
                "Use the saved preparation scene already loaded by the owned session runner.");
            _previousScenes = EditorSceneManager.GetSceneManagerSetup();
            EditorSceneManager.OpenScene(OwnedScene, OpenSceneMode.Single);
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            try
            {
                if (EditorApplication.isPlaying)
                {
                    yield return new ExitPlayMode();
                }
            }
            finally
            {
                try
                {
                    HttpManager.PreWebRequest -= _pre;
                    HttpManager.PostWebRequest -= _post;
                    _listener?.Stop();
                    _listener = null;
                    _transports.Clear();
                    _downloads.Clear();
                    if (_previousScenes != null)
                    {
                        EditorSceneManager.RestoreSceneManagerSetup(_previousScenes);
                        _previousScenes = null;
                    }
                }
                finally
                {
                    EditorSettings.enterPlayModeOptionsEnabled = _optionsEnabled;
                    EditorSettings.enterPlayModeOptions = _options;
                }
            }
        }

        [UnityTest]
        [Category("YL.Special.CoreSession")]
        public IEnumerator TwoRealPlaysInvalidateOldHttpTasksAndResetHooksWithoutDomainReload()
        {
            yield return new EnterPlayMode();
            Assert.That(FindHosts(), Is.Empty, "The owned fresh Play must not contain a foreign coroutine host.");
            var oldSession = SessionType.GetProperty("ExecutionSession", PrivateStatic).GetValue(null);
            var oldPreCalls = 0;
            var oldPostCalls = 0;
            _pre = native =>
            {
                oldPreCalls++;
                Capture(native);
            };
            _post = (_, _) => oldPostCalls++;
            HttpManager.PreWebRequest += _pre;
            HttpManager.PostWebRequest += _post;
            var old = HttpManager.Get(Url, null, null);
            Assert.That(FindHosts(), Is.Empty, "HTTP must send independently without creating a coroutine host.");
            var oldTask = old.AsTask();
            var oldNativeOperation = typeof(SendWebRequest).GetField("_nativeOperation", PrivateInstance).GetValue(old);
            Assert.That(old.Status, Is.EqualTo(HttpRequestStatus.Pending));
            Assert.That(oldTask.IsCompleted, Is.False);
            Assert.That(oldNativeOperation, Is.Not.Null);
            yield return new ExitPlayMode();

            Assert.That(old.Status, Is.EqualTo(HttpRequestStatus.Invalidated));
            Assert.That(oldTask.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(oldTask.GetAwaiter().GetResult(), Is.SameAs(old));
            Assert.That(PendingRequests(), Is.Empty);
            AssertReleased();
            var oldError = old.Error;
            var oldElapsed = old.ElapsedSeconds;
            Assert.That(oldPreCalls, Is.EqualTo(1));
            Assert.That(oldPostCalls, Is.Zero, "Invalidation must not expose a native Post observation.");

            yield return new EnterPlayMode();
            Assert.That(SessionType.GetProperty("ExecutionSession", PrivateStatic).GetValue(null), Is.Not.SameAs(oldSession));
            var newPreCalls = 0;
            _pre = native =>
            {
                newPreCalls++;
                Capture(native);
            };
            HttpManager.PreWebRequest += _pre;
            var current = HttpManager.Get(Url, null, null);
            var currentTask = current.AsTask();
            Assert.That(current.Status, Is.EqualTo(HttpRequestStatus.Pending));
            Assert.That(currentTask.IsCompleted, Is.False);
            Assert.That(oldPreCalls, Is.EqualTo(1), "Old Pre subscriptions cannot cross into a new Play.");
            Assert.That(newPreCalls, Is.EqualTo(1));
            Assert.That(typeof(HttpManager).GetField("PostWebRequest", PrivateStatic).GetValue(null), Is.Null);
            Assert.That(PendingRequests(), Is.EqualTo(new[] { current }));

            // 两次 Play 是真实会话证据；这里定向模拟旧原生回调，验证退出后仍有防重门禁。
            typeof(SendWebRequest).GetMethod("OnNativeCompleted", PrivateInstance)
                .Invoke(old, new[] { oldNativeOperation });
            old.Abort();
            Assert.That(old.Error, Is.EqualTo(oldError));
            Assert.That(old.ElapsedSeconds, Is.EqualTo(oldElapsed));
            Assert.That(old.AsTask(), Is.SameAs(oldTask));
            Assert.That(current.Status, Is.EqualTo(HttpRequestStatus.Pending));
            current.Abort();
            Assert.That(current.Status, Is.EqualTo(HttpRequestStatus.Canceled));
            Assert.That(currentTask.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(currentTask.GetAwaiter().GetResult(), Is.SameAs(current));
            Assert.That(PendingRequests(), Is.Empty);
            AssertReleased();
            yield return new ExitPlayMode();
            Assert.That(oldPostCalls, Is.Zero);
        }

        // 保持连接而不应答，使退出前的 Pending 不依赖外网、关闭端口或固定 sleep。
        private string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/pending";

        private void Capture(UnityWebRequest native)
        {
            native.timeout = 60;
            _transports.Add(native);
            _downloads.Add(native.downloadHandler);
        }

        private static SendWebRequest[] PendingRequests() => ((IEnumerable)typeof(HttpManager)
            .GetField("_pendingRequests", PrivateStatic).GetValue(null)).Cast<SendWebRequest>().ToArray();

        private static CoroutineStarter[] FindHosts() => UnityEngine.Object.FindObjectsByType<CoroutineStarter>(
            UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None);

        private void AssertReleased()
        {
            var transportPointer = typeof(UnityWebRequest).GetField("m_Ptr", PrivateInstance);
            var downloadPointer = typeof(DownloadHandler).GetField("m_Ptr", PrivateInstance);
            Assert.That(transportPointer, Is.Not.Null);
            Assert.That(downloadPointer, Is.Not.Null);
            foreach (var native in _transports)
            {
                Assert.That(transportPointer.GetValue(native), Is.EqualTo(IntPtr.Zero));
            }
            foreach (var download in _downloads)
            {
                Assert.That(downloadPointer.GetValue(download), Is.EqualTo(IntPtr.Zero));
            }
        }
    }
}
