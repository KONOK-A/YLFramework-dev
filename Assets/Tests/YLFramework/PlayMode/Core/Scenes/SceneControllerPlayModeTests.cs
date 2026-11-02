using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Core;
using YLFramework.Resource;
using Module = YLFramework.Core.Module;
using SceneController = YLFramework.Core.SceneController;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YLFramework.Tests.CommonSystem
{
    public class SceneControllerPlayModeTests
    {
        private sealed class TestModule : Module
        {
            internal Action Closing;
            public override void OnStart()
            {
            }
            protected override void OnClose() => Closing?.Invoke();
        }

        private sealed class OtherModule : Module
        {
            public override void OnStart()
            {
            }
        }

        private sealed class ProbeRequest : CoreResourceRequest<CoreSceneResult>
        {
            private CoreResourceStatus _state;
            private Action<CoreResourceRequest<CoreSceneResult>> _observers;
            private bool _isDisposed;
            private bool _isTaken;
            internal bool ThrowOnSubscribe;
            internal bool ThrowOnCancel;
            internal bool ThrowOnDispose;
            internal int Cancels;
            internal int Disposals;
            internal int Takes;
            internal int ObserverCount => _observers?.GetInvocationList().Length ?? 0;
            public override CoreResourceStatus Status => _state;
            public override float Progress => _state == CoreResourceStatus.Pending ? 0.25f : 1;
            public override CoreResourceException Error => null;
            public override bool IsDisposed => _isDisposed;
            public override event Action<CoreResourceRequest<CoreSceneResult>> Completed
            {
                add
                {
                    if (_isDisposed)
                    {
                        throw new ObjectDisposedException(nameof(ProbeRequest));
                    }

                    if (_state != CoreResourceStatus.Pending)
                    {
                        value?.Invoke(this);
                    }
                    else
                    {
                        _observers += value;
                    }

                    if (ThrowOnSubscribe)
                    {
                        throw new InvalidOperationException("subscribe failed");
                    }
                }
                remove => _observers -= value;
            }
            internal void Complete(CoreResourceStatus status = CoreResourceStatus.Succeeded)
            {
                if (_state != CoreResourceStatus.Pending)
                {
                    return;
                }

                _state = status;
                var snapshot = _observers?.GetInvocationList();
                if (snapshot == null)
                {
                    return;
                }

                foreach (Action<CoreResourceRequest<CoreSceneResult>> action in snapshot)
                {
                    try
                    {
                        action(this);
                    }
                    catch (Exception error)
                    {
                        Debug.LogException(error);
                    }
                }
            }
            public override CoreSceneResult TakeResult()
            {
                if (_isDisposed)
                {
                    throw new ObjectDisposedException(nameof(ProbeRequest));
                }

                if (_state != CoreResourceStatus.Succeeded || _isTaken)
                {
                    throw new InvalidOperationException();
                }

                _isTaken = true;
                Takes++;
                return default;
            }
            public override void Cancel()
            {
                if (_isDisposed || _state != CoreResourceStatus.Pending)
                {
                    return;
                }

                Cancels++;
                if (ThrowOnCancel)
                {
                    throw new InvalidOperationException("cancel failed");
                }

                Complete(CoreResourceStatus.Canceled);
            }
            public override void Dispose()
            {
                if (_isDisposed)
                {
                    return;
                }

                Disposals++;
                if (ThrowOnDispose)
                {
                    throw new InvalidOperationException("dispose failed");
                }

                try
                {
                    Cancel();
                }
                finally
                {
                    _isDisposed = true;
                    _observers = null;
                }
            }
        }

        private sealed class ProbeProvider : ICoreResourceProvider
        {
            internal readonly List<(string Project, string Address, LoadSceneMode Mode)> Calls = new();
            internal readonly List<ProbeRequest> Requests = new();
            internal Func<ProbeRequest> Create;
            public CoreResourceRequest<CoreSceneResult> LoadSceneAsync(string projectId, string address, LoadSceneMode mode)
            {
                Calls.Add((projectId, address, mode));
                var request = Create == null ? new ProbeRequest() : Create();
                if (request != null)
                {
                    Requests.Add(request);
                }

                return request;
            }
            public CoreResourceRequest<string> ReadTextAsync(string projectId, string address) => throw new NotSupportedException();
            public CorePrefabLease AcquirePrefab(string projectId, string address) => throw new NotSupportedException();
            public CoreResourceRequest<CorePrefabLease> AcquirePrefabAsync(string projectId, string address) => throw new NotSupportedException();
        }

        private TestModule _module;
        private readonly List<Module> _owners = new();
        private SceneController _controller;
        private ProbeProvider _provider;
        private Module _previousCurrent;
        private ResourceSystem _resources;
        private bool _hasTimedOut;
        private Action _abortPlaySession;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _hasTimedOut = false;
#if UNITY_EDITOR
            _abortPlaySession = () => EditorApplication.isPlaying = false;
#else
            abortPlaySession = Application.Quit;
#endif
            Assert.That(CoreResourceAccess.Provider, Is.Null, "Do not replace an external provider.");
            Assert.That(ModuleManager.GetModule<TestModule>(), Is.Null);
            _owners.Clear();
            var manager = typeof(ModuleManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            _previousCurrent = (Module)typeof(ModuleManager).GetField("_currentModule", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(manager);
            _provider = new ProbeProvider();
            CoreResourceAccess.Bind(_provider);
            _module = new TestModule { ProjectName = "controller-project" };
            _owners.Add(_module);
            var startup = ModuleManager.StartUpModule(_module);
            yield return startup;
            Assert.That(startup.Error, Is.Null.Or.Empty);
            Assert.That(_module.State, Is.EqualTo(ModuleState.Running));
            _controller = _module.LoadController<SceneController>();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // 超时已请求结束隔离会话；不能在仍有原生生产者的现场继续普通清理或下一用例。
            if (_hasTimedOut)
            {
                yield break;
            }

            foreach (var request in _provider.Requests)
            {
                request.ThrowOnCancel = request.ThrowOnDispose = false;
            }

            foreach (var owner in _owners.AsEnumerable().Reverse())
            {
                if (ReferenceEquals(ModuleManager.GetModule(owner.GetType()), owner))
                {
                    owner.Close();
                }
                else
                {
                    owner.RemoveController<SceneController>();
                }
            }
            foreach (var request in _provider.Requests)
            {
                request.Dispose();
            }

            var manager = typeof(ModuleManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            typeof(ModuleManager).GetField("_currentModule", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(manager, _previousCurrent);
            if (_resources != null)
            {
                foreach (var record in _resources.GetSceneRecords().Where(record => record.IsLoaded))
                {
                    using var unload = _resources.UnloadSceneAsync(record);
                    yield return Await(() => unload.IsDone, "owned scene unload");
                    Assert.That(unload.Error, Is.Null);
                }
                using var shutdown = _resources.ShutdownAsync();
                yield return Await(() => shutdown.IsDone, "resource shutdown");
                Assert.That(shutdown.Error, Is.Null);
                _resources = null;
            }
            CoreResourceAccess.Unbind(_provider);
        }

        [Test]
        public void ForwardsTheExactRequestAndDoesNotConsumeOrDisposeSuccess()
        {
            using var request = _controller.LoadSceneAsync("scene");
            Assert.That(request, Is.SameAs(_provider.Requests[0]));
            Assert.That(_provider.Calls[0], Is.EqualTo(("controller-project", "scene", LoadSceneMode.Single)));
            Assert.That(request.Progress, Is.EqualTo(0.25f));
            _provider.Requests[0].Complete();
            Assert.That(request.IsDisposed, Is.False);
            Assert.That(_provider.Requests[0].Takes, Is.Zero);
            request.TakeResult();
            Assert.Throws<InvalidOperationException>(() => request.TakeResult());
            _module.Close();
            Assert.That(request.IsDisposed, Is.False);
        }

        [Test]
        public void DuplicateKeysAreLocalOrdinalAndIndependentOfMode()
        {
            using var first = _controller.LoadSceneAsync("p", "scene", LoadSceneMode.Additive);
            Assert.That(Assert.Throws<CoreResourceException>(() => _controller.LoadSceneAsync("p", "scene")).Kind,
                Is.EqualTo(CoreResourceError.InProgress));
            Assert.That(_provider.Calls.Count, Is.EqualTo(1));
            using var second = _controller.LoadSceneAsync("P", "scene");
            using var third = _controller.LoadSceneAsync("p", "Scene");
            var other = new OtherModule();
            _owners.Add(other);
            using var independent = other.LoadController<SceneController>().LoadSceneAsync("p", "scene");
            Assert.That(_provider.Calls.Count, Is.EqualTo(4));
            first.Cancel();
            using var repeated = _controller.LoadSceneAsync("p", "scene");
            Assert.That(repeated, Is.Not.SameAs(first));
        }

        [TestCase(CoreResourceStatus.Succeeded)]
        [TestCase(CoreResourceStatus.Failed)]
        [TestCase(CoreResourceStatus.Canceled)]
        [TestCase(CoreResourceStatus.Invalidated)]
        public void EveryTerminalReleasesItsKeyBeforeCallerCompletion(CoreResourceStatus status)
        {
            using var first = _controller.LoadSceneAsync("scene");
            CoreResourceRequest<CoreSceneResult> next = null;
            first.Completed += _ => next = _controller.LoadSceneAsync("scene");
            _provider.Requests[0].Complete(status);
            Assert.That(next, Is.SameAs(_provider.Requests[1]));
            Assert.That(_provider.Requests[0].ObserverCount, Is.EqualTo(1), "Only the caller's observer remains.");
            Assert.That(first.IsDisposed, Is.False);
        }

        [Test]
        public void AlreadyCompletedRequestLeavesNoPendingEntryOrTrackingObserver()
        {
            _provider.Create = () =>
            {
                var request = new ProbeRequest();
                request.Complete();
                return request;
            };
            using var first = _controller.LoadSceneAsync("scene");
            using var next = _controller.LoadSceneAsync("scene");
            Assert.That(_provider.Calls.Count, Is.EqualTo(2));
            Assert.That(_provider.Requests[0].ObserverCount, Is.Zero);
            var notifications = 0;
            first.Completed += _ => notifications++;
            Assert.That(notifications, Is.EqualTo(1));
            _controller.CancelPendingLoads();
            Assert.That(first.IsDisposed, Is.False);
        }

        [Test]
        public void CallerDisposeReleasesItsReservationWithoutASecondBackend()
        {
            var first = _controller.LoadSceneAsync("scene");
            first.Dispose();
            using var second = _controller.LoadSceneAsync("scene");
            Assert.That(_provider.Calls.Count, Is.EqualTo(2));
        }

        [Test]
        public void ReentryInsideProviderSeesTheReservation()
        {
            _provider.Create = () =>
            {
                Assert.That(Assert.Throws<CoreResourceException>(() => _controller.LoadSceneAsync("scene")).Kind,
                    Is.EqualTo(CoreResourceError.InProgress));
                return new ProbeRequest();
            };
            using var request = _controller.LoadSceneAsync("scene");
            Assert.That(_provider.Calls.Count, Is.EqualTo(1));
        }

        [Test]
        public void SynchronousProviderFailureAndNullDoNotPoisonTheKey()
        {
            var failure = new InvalidOperationException("provider failed");
            _provider.Create = () => throw failure;
            Assert.That(Assert.Throws<InvalidOperationException>(() => _controller.LoadSceneAsync("scene")), Is.SameAs(failure));
            _provider.Create = () => null;
            Assert.Throws<InvalidOperationException>(() => _controller.LoadSceneAsync("scene"));
            _provider.Create = null;
            using var request = _controller.LoadSceneAsync("scene");
            Assert.That(_provider.Calls.Count, Is.EqualTo(3));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedSubscriptionReleasesTheUnreturnedRequest(bool alreadyDone)
        {
            var failed = new ProbeRequest { ThrowOnSubscribe = true };
            if (alreadyDone)
            {
                failed.Complete();
            }

            _provider.Create = () => failed;
            Assert.Throws<InvalidOperationException>(() => _controller.LoadSceneAsync("scene"));
            Assert.That(failed.IsDisposed, Is.True);
            Assert.That(failed.ObserverCount, Is.Zero);
            _provider.Create = null;
            using var next = _controller.LoadSceneAsync("scene");
        }

        [Test]
        public void FailedDeliveryKeepsTheOriginalAndEveryCleanupFailure()
        {
            _provider.Create = () => new ProbeRequest
            {
                ThrowOnSubscribe = true,
                ThrowOnCancel = true,
                ThrowOnDispose = true
            };
            var error = Assert.Throws<AggregateException>(() => _controller.LoadSceneAsync("scene"));
            Assert.That(error.Flatten().InnerExceptions.Select(item => item.Message),
                Is.EquivalentTo(new[] { "subscribe failed", "cancel failed", "dispose failed" }));
        }

        [Test]
        public void CancelingAReservedCallCannotDeleteItsReplacement()
        {
            var unreturned = new ProbeRequest();
            CoreResourceRequest<CoreSceneResult> replacement = null;
            _provider.Create = () =>
            {
                _provider.Create = null;
                _controller.CancelPendingLoads();
                replacement = _controller.LoadSceneAsync("scene");
                return unreturned;
            };
            Assert.Throws<InvalidOperationException>(() => _controller.LoadSceneAsync("scene"));
            Assert.That(unreturned.IsDisposed, Is.True);
            Assert.That(replacement.IsDisposed, Is.False);
            Assert.That(Assert.Throws<CoreResourceException>(() => _controller.LoadSceneAsync("scene")).Kind,
                Is.EqualTo(CoreResourceError.InProgress));
        }

        [Test]
        public void SuccessfulCompletionMayCloseTheModuleWithoutLosingTheResult()
        {
            using var request = _controller.LoadSceneAsync("scene");
            request.Completed += _ => _module.Close();
            _provider.Requests[0].Complete();
            Assert.That(_module.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(request.IsDisposed, Is.False);
            request.TakeResult();
        }

        [Test]
        public void ProviderReturningAfterExitCannotJoinAReusedControllerInstance()
        {
            var old = new ProbeRequest();
            CoreResourceRequest<CoreSceneResult> next = null;
            _provider.Create = () =>
            {
                _provider.Create = null;
                _module.RemoveController(_controller);
                _module.AddController(_controller);
                next = _controller.LoadSceneAsync("scene");
                return old;
            };
            Assert.Throws<InvalidOperationException>(() => _controller.LoadSceneAsync("scene"));
            Assert.That(old.IsDisposed, Is.True);
            Assert.That(next.IsDisposed, Is.False);
            Assert.That(Assert.Throws<CoreResourceException>(() => _controller.LoadSceneAsync("scene")).Kind,
                Is.EqualTo(CoreResourceError.InProgress));
        }

        [Test]
        public void CancelSnapshotDoesNotCancelWorkCreatedByItsObservers()
        {
            using var first = _controller.LoadSceneAsync("scene");
            CoreResourceRequest<CoreSceneResult> next = null;
            first.Completed += _ => next = _controller.LoadSceneAsync("scene");
            _controller.CancelPendingLoads();
            Assert.That(first.IsDisposed, Is.True);
            Assert.That(next.Status, Is.EqualTo(CoreResourceStatus.Pending));
            _controller.CancelPendingLoads();
            Assert.That(next.IsDisposed, Is.True);
            _controller.CancelPendingLoads();
        }

        [Test]
        public void ClosingRejectsWorkBeforeExitAndCancelsOnlyThisOwner()
        {
            var other = new OtherModule();
            _owners.Add(other);
            using var independent = other.LoadController<SceneController>().LoadSceneAsync("scene");
            using var pending = _controller.LoadSceneAsync("scene");
            var rejected = 0;
            _module.Closing = () =>
            {
                Assert.Throws<InvalidOperationException>(() => _controller.LoadSceneAsync("new"));
                rejected++;
            };
            pending.Completed += _ =>
            {
                Assert.Throws<InvalidOperationException>(() => _controller.LoadSceneAsync("new"));
                rejected++;
            };
            _module.Close();
            Assert.That(rejected, Is.EqualTo(2));
            Assert.That(pending.IsDisposed, Is.True);
            Assert.That(independent.Status, Is.EqualTo(CoreResourceStatus.Pending));
            Assert.Throws<InvalidOperationException>(() => _controller.LoadSceneAsync("new"));
        }

        [Test]
        public void CleanupFailuresDoNotSkipLaterRequestsOrHideTheirCauses()
        {
            var first = (ProbeRequest)_controller.LoadSceneAsync("first");
            var second = (ProbeRequest)_controller.LoadSceneAsync("second");
            first.ThrowOnCancel = first.ThrowOnDispose = true;
            var error = Assert.Throws<AggregateException>(() => _controller.CancelPendingLoads());
            Assert.That(error.Flatten().InnerExceptions.Select(item => item.Message),
                Is.EquivalentTo(new[] { "cancel failed", "dispose failed" }));
            foreach (var cause in error.Flatten().InnerExceptions)
            {
                var context = cause.Data["YLFramework.SceneController.Context"] as string;
                Assert.That(context, Is.Not.Null, "Cleanup failures must identify their scene load.");
                Assert.That(context,
                    Does.Contain(_module.ModuleName).And.Contain("project=controller-project")
                        .And.Contain("address=first").And.Contain("mode=Single"));
            }
            Assert.That(second.IsDisposed, Is.True);
            Assert.That(first.Disposals, Is.EqualTo(1));
            _provider.Create = null;
            using var retry = _controller.LoadSceneAsync("first");
        }

        [Test]
        public void UnboundProviderAndInvalidInputNeverStartABackendRequest()
        {
            Assert.Throws<ArgumentException>(() => _controller.LoadSceneAsync(" "));
            Assert.Throws<ArgumentException>(() => _controller.LoadSceneAsync(" ", "scene"));
            Assert.Throws<ArgumentOutOfRangeException>(() => _controller.LoadSceneAsync("scene", (LoadSceneMode)99));
            Assert.Throws<InvalidOperationException>(() => new SceneController().LoadSceneAsync("scene"));
            CoreResourceAccess.Unbind(_provider);
            Assert.That(Assert.Throws<CoreResourceException>(() => _controller.LoadSceneAsync("scene")).Kind,
                Is.EqualTo(CoreResourceError.Unavailable));
            CoreResourceAccess.Bind(_provider);
            Assert.That(_provider.Calls, Is.Empty);
        }

        [Test]
        public void CallsFromAnotherThreadAreRejectedBeforeReadingUnityOrTheProvider()
        {
            var error = Task.Run(() =>
            {
                try
                {
                    _controller.LoadSceneAsync("scene");
                    return null;
                }
                catch (Exception failure)
                {
                    return failure;
                }
            }).GetAwaiter().GetResult();
            Assert.That(error, Is.TypeOf<InvalidOperationException>());
            Assert.That(_provider.Calls, Is.Empty);
        }

#if UNITY_EDITOR
        private void BindRealResources()
        {
            const string Root = "Assets/Tests/YLFramework/Fixtures/CommonSystem/ResourceProject.asset";
            Assert.That(ResourceSystem.Current, Is.Null, "Never replace an external Resource authority.");
            var projectType = Type.GetType("YLFramework.Editor.Resource.ResourceProject, YLFramework.Editor", true);
            var project = AssetDatabase.LoadAssetAtPath(Root, projectType);
            Assert.That(project, Is.Not.Null, "Create the owned CommonSystem fixtures in EditMode first.");
            var projects = Array.CreateInstance(projectType, 1);
            projects.SetValue(project, 0);
            var simulationType = Type.GetType("YLFramework.Editor.Resource.ResourceEditorSimulation, YLFramework.Editor", true);
            var simulation = Activator.CreateInstance(simulationType, new object[] { projects, null });
            var config = (ResourceConfiguration)simulationType.GetMethod("CreateConfiguration", Type.EmptyTypes)
                .Invoke(simulation, null);
            using var initialize = ResourceSystem.InitializeAsync(config);
            Assert.That(initialize.Error, Is.Null);
            _resources = initialize.TakeResult();
            _resources.BindCoreResourceProvider();
            _module.ProjectName = "YL.CommonSystem.Fixture";
        }

        [UnityTest]
        public IEnumerator RealAdditiveIntentsStayIndependentAndSurviveModuleClose()
        {
            BindRealResources();
            var active = SceneManager.GetActiveScene();
            using var first = _controller.LoadSceneAsync("CommonTarget", LoadSceneMode.Additive);
            yield return Await(() => !first.keepWaiting, "first additive scene");
            Assert.That(first.Status, Is.EqualTo(CoreResourceStatus.Succeeded), first.Error?.ToString());
            Assert.That(first.Progress, Is.EqualTo(1));
            Assert.That(first.IsDisposed, Is.False);
            var firstScene = first.TakeResult().Scene;
            Assert.That(firstScene.isLoaded, Is.True);
            Assert.Throws<InvalidOperationException>(() => first.TakeResult());

            using var second = _controller.LoadSceneAsync("CommonTarget", LoadSceneMode.Additive);
            yield return Await(() => !second.keepWaiting, "second additive scene");
            Assert.That(second.Status, Is.EqualTo(CoreResourceStatus.Succeeded), second.Error?.ToString());
            var secondScene = second.TakeResult().Scene;
            Assert.That(secondScene.handle, Is.Not.EqualTo(firstScene.handle));
            Assert.That(secondScene.path, Is.EqualTo(firstScene.path));
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(active));

            using var canceled = _controller.LoadSceneAsync("CommonTarget", LoadSceneMode.Additive);
            using var independent = CoreResourceAccess.Provider.LoadSceneAsync(_module.ProjectName, "CommonNeutral", LoadSceneMode.Additive);
            _module.Close();
            Assert.That(canceled.IsDisposed, Is.True);
            Assert.That(canceled.Status, Is.EqualTo(CoreResourceStatus.Canceled));
            yield return Await(() => !independent.keepWaiting, "independent scene request");
            Assert.That(independent.Status, Is.EqualTo(CoreResourceStatus.Succeeded), independent.Error?.ToString());
            var independentScene = independent.TakeResult().Scene;
            Assert.That(independentScene.isLoaded && firstScene.isLoaded && secondScene.isLoaded, Is.True);
            Assert.That(first.IsDisposed || second.IsDisposed, Is.False);
            Assert.That(canceled.Status, Is.EqualTo(CoreResourceStatus.Canceled));
            yield return null;
        }

        [UnityTest]
        public IEnumerator RealFailureKeepsItsTerminalAndDoesNotReserveTheAddress()
        {
            BindRealResources();
            using var first = _controller.LoadSceneAsync("missing-common-scene", LoadSceneMode.Additive);
            yield return Await(() => !first.keepWaiting, "missing scene");
            Assert.That(first.Status, Is.EqualTo(CoreResourceStatus.Failed));
            Assert.That(first.Error, Is.Not.Null);
            using var second = _controller.LoadSceneAsync("missing-common-scene", LoadSceneMode.Additive);
            Assert.That(second, Is.Not.SameAs(first));
            _module.Close();
            Assert.That(first.IsDisposed, Is.False);
        }
#endif

        [UnityTest]
        public IEnumerator TimeoutRequestsIsolationAbortBeforeReportingFailure()
        {
            var previousAbort = _abortPlaySession;
            var previousTimeout = _hasTimedOut;
            var requested = false;
            _abortPlaySession = () => requested = true;
            try
            {
                var wait = Await(() => false, "owned timeout probe", 0);
                Assert.Throws<AssertionException>(() => wait.MoveNext());
                Assert.That(_hasTimedOut, Is.True);
                Assert.That(requested, Is.True, "A timeout must abort the isolated Play session, not let later cases inherit it.");
            }
            finally
            {
                _abortPlaySession = previousAbort;
                _hasTimedOut = previousTimeout;
            }
            yield return null;
        }

        [Test]
        public void TimedOutTeardownDoesNotTouchTheAuthorityBeingAborted()
        {
            var previousTimeout = _hasTimedOut;
            _hasTimedOut = true;
            try
            {
                Assert.That(TearDown().MoveNext(), Is.False);
                Assert.That(_module.State, Is.EqualTo(ModuleState.Running),
                    "Ordinary cleanup must not run after the isolated session was aborted.");
                Assert.That(CoreResourceAccess.Provider, Is.SameAs(_provider));
            }
            finally
            {
                _hasTimedOut = previousTimeout;
            }
        }

        private IEnumerator Await(Func<bool> finished, string operation, float timeoutSeconds = 10)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!finished())
            {
                if (Time.realtimeSinceStartup >= deadline)
                {
                    _hasTimedOut = true;
                    _abortPlaySession();
                    Assert.Fail("Timed out: " + operation);
                }
                yield return null;
            }
        }
    }
}
