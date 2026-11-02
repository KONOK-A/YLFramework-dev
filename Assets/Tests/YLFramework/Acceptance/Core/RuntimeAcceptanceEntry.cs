using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Audio;
using YLFramework.Http;
using YLFramework.Utilities;
using YLFramework.Grids;
using YLFramework.Utilities.Json;
using YLFramework.Async;
using YLFramework.UI;

namespace YLFramework.Core.Tests
{
    /// <summary>5.5 真实 Play / Windows Mono 综合门禁；仅验收程序集使用，不随框架导出。</summary>
    public sealed class RuntimeAcceptanceEntry : MonoBehaviour
    {
        [global::UnityEngine.Serialization.FormerlySerializedAs("ColorPrefab")]
        public GameObject colorPrefab;
        [global::UnityEngine.Serialization.FormerlySerializedAs("ServerUrl")]
        public string serverUrl;
        public static string Result;
        public static int Round;
        public static int PreparedRound
        {
            get; private set;
        }
        private static readonly object _domainIdentity = new object();
        private static object _previousDomain;
        private static object _previousSession;
        private static SendWebRequest _previousHttp;
        private static UnityWebRequest _previousNative;
        private static DownloadHandler _previousDownload;
        private static UploadHandler _previousUpload;
        private static UnityWebRequestAsyncOperation _previousOperation;
        private static Action<AsyncOperation> _previousNotification;
        private static AudioPlayRequest _previousAudio;
        private static Provider _previousProvider;
        private static AudioHandle _previousHandle;
        private static LoadPanelRequest<ColorSelectorWindow> _previousPanel;
        private static ColorSelectorWindow _previousWindow;
        private static int _oldPreCalls;
        private static int _oldPostCalls;
        private static int _oldCompletedCalls;
        private static bool _wasExitRejected;
        private UnityWebRequest _native;
        private DownloadHandler _download;
        private UploadHandler _upload;
        private SendWebRequest _request;
        private readonly Owner _module = new Owner();
        private ColorSelectorWindow _window;
        private AudioClip _clip;
        private Provider _provider;
        private AudioPlayRequest _audio;
        private AudioHandle _handle;
        private bool _isQuitArmed;
        private Exception _callbackFailure;
        private Action<UnityWebRequest> _pre;
        private Action<UnityWebRequest, float> _post;
        private static readonly BindingFlags _staticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

        private sealed class Owner : Module
        {
            public override void OnStart()
            {
            }
        }
        private sealed class LeaseOwner : IDisposable
        {
            internal int Releases;
            public void Dispose()
            {
                Releases++;
            }
        }
        private sealed class Provider : IAudioClipProvider
        {
            internal Action<AudioClipLease, Exception> Complete;
            internal readonly LeaseOwner Active = new LeaseOwner();
            internal AudioClip Clip;
            public bool CanCache => false;
            public AudioClipLease Load(string key) => new AudioClipLease(Clip, Active);
            public void LoadAsync(string key, Action<AudioClipLease, Exception> completed, Action<float> progress)
            {
                Complete = completed;
                progress(.25f);
            }
        }

        private static object Session => typeof(CoroutineStarter).Assembly
            .GetType("YLFramework.Singletons.SingletonSessionState", true)
            .GetProperty("ExecutionSession", _staticPrivate).GetValue(null);
        private static int Pending => (int)typeof(HttpManager).GetField("_pendingRequests", _staticPrivate)
            .GetValue(null).GetType().GetProperty("Count").GetValue(
                typeof(HttpManager).GetField("_pendingRequests", _staticPrivate).GetValue(null));
        private static IntPtr Pointer(object value)
        {
            if (value == null)
            {
                return IntPtr.Zero;
            }

            var type = value.GetType();
            while (type != null)
            {
                var field = type.GetField("m_Ptr", BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    return (IntPtr)field.GetValue(value);
                }

                type = type.BaseType;
            }
            throw new InvalidOperationException("Missing native pointer evidence.");
        }

        private IEnumerator Start()
        {
            DontDestroyOnLoad(gameObject);
            Result = "RUNNING";
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-runtime55-url")
                {
                    serverUrl = args[i + 1];
                }
            }

            var flow = Run();
            while (true)
            {
                bool next;
                try
                {
                    next = flow.MoveNext();
                }
                catch (Exception error)
                {
                    Result = "FAILED: " + error;
                    Debug.LogError("RUNTIME55 " + Result);
                    (flow as IDisposable)?.Dispose();
                    _request?.Abort();
                    _module.Close();
                    AudioKit.Shutdown();
                    if (_clip)
                    {
                        Destroy(_clip);
                    }

                    if (!Application.isEditor)
                    {
                        Application.Quit(1);
                    }

                    yield break;
                }
                if (!next)
                {
                    break;
                }

                yield return flow.Current;
            }
        }

        private IEnumerator Run()
        {
            Round++;
            Check(!string.IsNullOrEmpty(serverUrl), "controlled loopback service configured");
            Check(Pending == 0, "fresh HTTP pending table");
            if (_previousHttp != null)
            {
                Check(ReferenceEquals(_previousDomain, _domainIdentity), "same script domain across real Play");
                Check(!ReferenceEquals(_previousSession, Session), "new Foundation execution session");
                VerifyExit();
                Check(_oldPreCalls == 4 && _oldPostCalls == 2 && _oldCompletedCalls == 1,
                    "previous HTTP subscriptions cannot receive new session work");
            }
            _pre = value =>
            {
                _native = value;
                _download = value.downloadHandler;
                _upload = value.uploadHandler;
            };
            _post = (value, elapsed) =>
            {
                try
                {
                    Check(Pointer(value) != IntPtr.Zero && elapsed >= 0, "post synchronously borrows live transport");
                }
                catch (Exception error)
                {
                    if (_callbackFailure == null)
                    {
                        _callbackFailure = error;
                    }
                }
            };
            HttpManager.PreWebRequest += _pre;
            HttpManager.PostWebRequest += _post;
            var ownedOldPre = 0;
            var ownedOldPost = 0;
            var acceptingRound = Round;
            HttpManager.PreWebRequest += _ =>
            {
                if (Round == acceptingRound)
                {
                    ownedOldPre++;
                }
                else
                {
                    _oldPreCalls++;
                }
            };
            HttpManager.PostWebRequest += (_, __) =>
            {
                if (Round == acceptingRound)
                {
                    ownedOldPost++;
                }
                else
                {
                    _oldPostCalls++;
                }
            };

            _request = HttpManager.Get(serverUrl + "/ok", null, null);
            var deadline = Time.realtimeSinceStartup + 10;
            while (!_request.IsDone && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Check(_request.IsDone && _request.Status == HttpRequestStatus.Succeeded && _request.Text == "success-body",
                "real UnityWebRequest success / stable snapshot");
            Released();
            _request = HttpManager.Get(serverUrl + "/error", null, null);
            deadline = Time.realtimeSinceStartup + 10;
            while (!_request.IsDone && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Check(_request.IsDone && _request.Status == HttpRequestStatus.Failed && _request.ResponseCode == 503 &&
                _request.Text == "protocol-body", "real protocol failure retains response");
            Released();
            _request = HttpManager.PostBody(serverUrl + "/hold", "{}", null);
            yield return new WaitForSecondsRealtime(.25f);
            Check(!_request.IsDone && Pointer(_native) != IntPtr.Zero && Pointer(_upload) != IntPtr.Zero,
                "exclusive native transport and upload genuinely in flight");
            _request.Abort();
            _request.Abort();
            Check(_request.Status == HttpRequestStatus.Canceled, "idempotent physical cancellation");
            Released();

            var startup = ModuleManager.StartUpModule(_module);
            yield return startup;
            Check(startup.Exception == null && _module.State == ModuleState.Running, "representative Core owner startup");
            _window = _module.LoadPanel<ColorSelectorWindow>(colorPrefab, ColorSelectorWindow.Arguments(Color.green), overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple });
            Check(_window && _window.Module == _module && _window.CurrentColor == Color.green, "formal View/Panel attaches to its owner");
            _window.Close();
            var canceled = _module.LoadPanelAsync<ColorSelectorWindow>(colorPrefab, overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple });
            Check(!canceled.IsDone, "closed window creates genuinely pending Panel request");
            canceled.Abort();
            Check(canceled.Status == LoadViewStatus.Canceled, "Panel cancellation does not cancel shared producer");
            _window = _module.LoadPanel<ColorSelectorWindow>(colorPrefab, ColorSelectorWindow.Arguments(Color.magenta), overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple });

            _clip = AudioClip.Create("runtime55-owned", 44100, 1, 44100, false);
            // 验收只配置当前自有会话，不触发设置通知或写入游戏偏好键。
            AudioKit.Settings.IsSoundOn.SetValueWithoutCallback(true);
            AudioKit.Settings.IsSoundMuted.SetValueWithoutCallback(false);
            _provider = new Provider { Clip = _clip };
            _handle = AudioKit.PlaySoundNamed(new AudioClipAddress(_provider, "active"), AudioSoundOptions.MusicDefaults);
            _audio = AudioKit.PlaySoundAsync(new AudioClipAddress(_provider, "pending"));
            Check(_handle.IsValid && !_audio.IsDone, "Audio keeps independent voice / pending resource ownership: handle=" +
                _handle.IsValid + " pending=" + _audio.State + "/" + _audio.EndReason);
            if (_previousAudio != null)
            {
                var late = new LeaseOwner();
                _previousProvider.Complete(new AudioClipLease(_clip, late), null);
                _previousHandle.Stop();
                _previousAudio.Cancel();
                _previousHttp.Abort();
                _previousNotification(_previousOperation);
                Check(late.Releases == 1 && _handle.IsValid && _previousHttp.Status == HttpRequestStatus.Invalidated,
                    "late old Audio lease and native HTTP notification cannot mutate new session");
                Check(_oldPreCalls == 4 && _oldPostCalls == 2 && _oldCompletedCalls == 1, "old closures remain isolated");
            }
            Check(AssemblyTool.GetType(typeof(Owner).FullName) == typeof(Owner), "Foundation FullName lookup works in Player");
            Check(new CustomClassNameGui("runtime55").GetName() == "runtime55", "Attributes runtime metadata usable");
            Check(!string.IsNullOrEmpty(StringTools.FormatByte(1L << 40)), "Runtime byte formatter usable");
            var grid = new NormalGrid<int>(2, 2);
            grid[1, 1] = 55;
            Check(grid[1, 1] == 55 && new JSONObject("{\"probe\":55}").HasField("probe"), "Grid / JSONObject distinct tools usable");

            _previousWindow = _window;
            _request = HttpManager.PostBody(serverUrl + "/hold", "{}", null);
            yield return new WaitForSecondsRealtime(.25f);
            _previousPanel = _module.LoadPanelAsync<ColorSelectorWindow>(colorPrefab, overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple });
            Check(!_request.IsDone && !_previousPanel.IsDone, "HTTP / Panel really pending before real exit");
            Check(_window && _window.Module == _module && _window.IsLoaded, "real exit begins with an active owned View");
            if (_callbackFailure != null)
            {
                throw new InvalidOperationException("HTTP observer assertion failed.", _callbackFailure);
            }

            _previousHttp = _request;
            _previousNative = _native;
            _previousDownload = _download;
            _previousUpload = _upload;
            _previousAudio = _audio;
            _previousProvider = _provider;
            _previousHandle = _handle;
            _previousSession = Session;
            _previousDomain = _domainIdentity;
            _previousOperation = (UnityWebRequestAsyncOperation)typeof(SendWebRequest)
                    .GetField("_nativeOperation", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_request);
            _previousNotification = (Action<AsyncOperation>)Delegate.CreateDelegate(typeof(Action<AsyncOperation>), _request,
                typeof(SendWebRequest).GetMethod("OnNativeCompleted", BindingFlags.NonPublic | BindingFlags.Instance));
            _oldPreCalls = ownedOldPre;
            _oldPostCalls = ownedOldPost;
            _oldCompletedCalls = 0;
            _wasExitRejected = false;
            _request.AddCompleteEvent(_ =>
            {
                _oldCompletedCalls++;
                int hosts = UnityEngine.Object.FindObjectsOfType<CoroutineStarter>(true).Length;
                var denied = HttpManager.Get(serverUrl + "/ok", null, null);
                _wasExitRejected = denied.Status == HttpRequestStatus.Invalidated &&
                    hosts == UnityEngine.Object.FindObjectsOfType<CoroutineStarter>(true).Length;
                Check(_wasExitRejected, "real exit callback rejects new request / replacement host");
            });
            Application.quitting += OnQuit;
            _isQuitArmed = true;
            PreparedRound = Round;
            Result = "READY round=" + Round;
            Debug.Log("RUNTIME55 " + Result + " domain=" + _domainIdentity.GetHashCode() + " platform=" + Application.platform);
            if (!Application.isEditor)
            {
                Application.Quit(0);
            }
        }

        private void Released()
        {
            if (_callbackFailure != null)
            {
                throw new InvalidOperationException("HTTP observer assertion failed.", _callbackFailure);
            }

            Check(Pointer(_native) == IntPtr.Zero && Pointer(_download) == IntPtr.Zero &&
                Pointer(_upload) == IntPtr.Zero, "native request / download / upload released before completed observation");
        }

        private void OnQuit()
        {
            try
            {
                if (_callbackFailure != null)
                {
                    throw new InvalidOperationException("HTTP observer assertion failed.", _callbackFailure);
                }
                // Application.quitting 在场景对象销毁之前通知；活跃 View 的解绑另在真实 Exit 后验证。
                VerifyExit(Round, false);
                Check(_provider.Active.Releases == 1 && !_handle.IsValid && _audio.EndReason == AudioPlayEnd.Shutdown,
                    "real quit releases only Audio-owned lease / identities");
                Debug.Log("RUNTIME55 QUIT_PASS round=" + Round);
            }
            catch (Exception error)
            {
                Result = "FAILED: " + error;
                Debug.LogError("RUNTIME55 " + Result);
                if (!Application.isEditor)
                {
                    Environment.Exit(1);
                }
            }
            finally
            {
                if (_clip)
                {
                    Destroy(_clip);
                }
            }
        }

        public static void VerifyExit(int expectedRound = 0, bool afterSceneTeardown = true)
        {
            Check(PreparedRound > 0 && (expectedRound == 0 || PreparedRound == expectedRound), "exit evidence belongs to expected ready round");
            Check(_previousHttp.IsDone && _previousHttp.Status == HttpRequestStatus.Invalidated && Pending == 0,
                "real exit invalidates old HTTP and empties pending table");
            Check(Pointer(_previousNative) == IntPtr.Zero && Pointer(_previousDownload) == IntPtr.Zero &&
                Pointer(_previousUpload) == IntPtr.Zero, "real exit releases exclusive native transport and handlers");
            Check(_previousPanel.IsDone && _previousPanel.Status == LoadViewStatus.Invalidated,
                "real quit invalidates pending Panel before scene teardown");
            if (afterSceneTeardown)
            {
                Check(!_previousWindow || _previousWindow.Module == null, "real scene teardown releases previously active View owner");
            }

            Check(_previousAudio.IsDone && !_previousHandle.IsValid && _wasExitRejected && _oldCompletedCalls == 1,
                "real exit cleans Audio / rejects HTTP callback reentry exactly once");
            Check(_previousProvider.Active.Releases == 1 && _previousAudio.EndReason == AudioPlayEnd.Shutdown,
                "real exit returns Audio-owned active lease exactly once");
        }

        private void OnDestroy()
        {
            if (_isQuitArmed)
            {
                Application.quitting -= OnQuit;
            }

            HttpManager.PreWebRequest -= _pre;
            HttpManager.PostWebRequest -= _post;
            if (!_isQuitArmed)
            {
                _audio?.Cancel();
                _handle.Stop();
            }
            if (_clip)
            {
                Destroy(_clip);
            }
        }

        private static void Check(bool condition, string contract)
        {
            if (!condition)
            {
                throw new InvalidOperationException("RUNTIME55 contract: " + contract);
            }

            Debug.Log("RUNTIME55 CHECK " + contract);
        }
    }
}
