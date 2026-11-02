using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Core;
using YLFramework.Integrations.HybridCLR;
using YLFramework.Resource;
using Module = YLFramework.Core.Module;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    public sealed class HybridCLRProbeModule : Module
    {
        public static Action Starting;
        public override string ProjectName
        {
            get => "hybridclr-test"; set
            {
            }
        }
        public override void OnStart() => Starting?.Invoke();
    }

    public sealed class HybridCLRWaitingModule : Module
    {
        public override string ProjectName
        {
            get => "hybridclr-test"; set
            {
            }
        }
        public override string DefaultStartUpScene => "owned-pending-scene";
        public override void OnStart()
        {
        }
    }

    public class HybridCLRModuleTests
    {
        private HybridCLRLoaderTests _fixture;
        private ICoreResourceProvider _previousProvider;
        private ICoreResourceProvider _ownedProvider;
        private PendingScene _pendingScene;
        private Scene _ownedScene;
        private HybridCLRModuleRequest _ownedRequest;
        private int _nativeCalls;
        private string Entry => typeof(HybridCLRProbeModule).Assembly.GetName().Name;

        [SetUp]
        public void SetUp()
        {
            _previousProvider = CoreResourceAccess.Provider;
            _ownedProvider = null;
            _pendingScene = null;
            _ownedScene = default;
            _ownedRequest = null;
            _nativeCalls = 0;
            _fixture = new HybridCLRLoaderTests();
            _fixture.Initialize();
            HybridCLRProbeModule.Starting = null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // RED 断言也要放行自己的场景等待，不遗留无法 Close 的 Starting Module。
            _pendingScene?.Succeed(_ownedScene);
            for (var i = 0; i < 240 && _ownedRequest != null && !_ownedRequest.IsDone; i++)
            {
                yield return null;
            }

            var waiting = ModuleManager.GetModule(typeof(HybridCLRWaitingModule));
            if (waiting != null && ReferenceEquals(waiting, _ownedRequest?.Module))
            {
                waiting.Close();
            }

            var module = ModuleManager.GetModule(typeof(HybridCLRProbeModule));
            if (module != null)
            {
                module.Close();
            }

            HybridCLRProbeModule.Starting = null;
            CoreResourceAccess.Unbind(_ownedProvider);
            yield return _fixture.TearDown();
            if (_ownedScene.IsValid() && _ownedScene.isLoaded)
            {
                yield return SceneManager.UnloadSceneAsync(_ownedScene);
            }

            if (_ownedProvider != null && _previousProvider != null && CoreResourceAccess.Provider == null)
            {
                CoreResourceAccess.Bind(_previousProvider);
            }
        }

        [UnityTest]
        public IEnumerator AThrowingSharedLoadObserverCannotStrandModuleStartup()
        {
            var load = _fixture.Loader.LoadAsync(HybridCLRLoaderTests.Address);
            Assert.That(load.IsDone, Is.False, "This regression needs a real pending resource load.");
            LogAssert.Expect(UnityEngine.LogType.Exception, new System.Text.RegularExpressions.Regex("Intentional shared-load observer"));
            load.AddCompleteEvent(_ => throw new InvalidOperationException("Intentional shared-load observer"));
            var request = _fixture.Loader.StartModuleAsync(HybridCLRLoaderTests.Address, Entry, typeof(HybridCLRProbeModule).FullName);
            for (var i = 0; i < 30 && !request.IsDone; i++)
            {
                yield return null;
            }

            Assert.That(request.IsDone, Is.True);
            Assert.That(request.Exception, Is.Null, request.Error);
            Assert.That(request.Module.State, Is.EqualTo(ModuleState.Running));
        }

        [UnityTest]
        public IEnumerator RemovingPublicLoadObserversCannotRemoveInternalModuleContinuation()
        {
            var load = _fixture.Loader.LoadAsync(HybridCLRLoaderTests.Address);
            Assert.That(load.IsDone, Is.False);
            var request = _fixture.Loader.StartModuleAsync(HybridCLRLoaderTests.Address, Entry, typeof(HybridCLRProbeModule).FullName);
            load.RemoveAllCompleteEvent();
            for (var i = 0; i < 30 && !request.IsDone; i++)
            {
                yield return null;
            }

            Assert.That(request.IsDone, Is.True);
            Assert.That(request.Exception, Is.Null, request.Error);
        }

        [UnityTest]
        public IEnumerator ExactEntryTypeUsesCoreLifecycleAndBlocksDisposalUntilModuleIsClosed()
        {
            var request = _fixture.Loader.StartModuleAsync(HybridCLRLoaderTests.Address, Entry, typeof(HybridCLRProbeModule).FullName);
            Assert.Throws<NotSupportedException>(() => request.Abort());
            yield return request;
            Assert.That(request.Exception, Is.Null, request.Error);
            Assert.That(request.Module.State, Is.EqualTo(ModuleState.Running));
            Assert.Throws<InvalidOperationException>(() => _fixture.Loader.Dispose());
            request.Module.Close();
            _fixture.Loader.Dispose();
            Assert.That(_fixture.ResourcesSystem.IsAvailable, Is.True);
        }

        [UnityTest]
        public IEnumerator ModuleFailureKeepsTheOriginalExceptionAndCanRetryTheSameLoadedCode()
        {
            var failure = new InvalidOperationException("Intentional isolated Module failure.");
            HybridCLRProbeModule.Starting = () => throw failure;
            var failed = _fixture.Loader.StartModuleAsync(HybridCLRLoaderTests.Address, Entry, typeof(HybridCLRProbeModule).FullName);
            yield return failed;
            Assert.That(failed.Exception, Is.SameAs(failure));
            Assert.That(failed.RequiresRestart, Is.False);
            Assert.That(ModuleManager.GetModule(typeof(HybridCLRProbeModule)), Is.Null);
            HybridCLRProbeModule.Starting = null;
            var retry = _fixture.Loader.StartModuleAsync(HybridCLRLoaderTests.Address, Entry, typeof(HybridCLRProbeModule).FullName);
            yield return retry;
            Assert.That(retry.Exception, Is.Null, retry.Error);
            Assert.That(retry.Code, Is.SameAs(failed.Code));
        }

        [UnityTest]
        public IEnumerator MissingModuleTypeFinishesWithoutScanningOtherAssemblies()
        {
            var request = _fixture.Loader.StartModuleAsync(HybridCLRLoaderTests.Address, Entry, "Definitely.Not.A.Module");
            yield return request;
            Assert.That(request.Exception, Is.TypeOf<TypeLoadException>());
            Assert.That(request.Module, Is.Null);
            Assert.That(request.RequiresRestart, Is.False);
        }

        [UnityTest]
        public IEnumerator PendingModuleReportsMonotonicCodeAndCoreSceneProgressBeforeCompletion()
        {
            yield return _fixture.TearDown();
            _fixture.Initialize("Metadata");
            _ownedScene = SceneManager.CreateScene("YLHybridCLR.Progress." + Guid.NewGuid().ToString("N"));
            _pendingScene = new PendingScene { PendingProgress = 0.2f };
            _ownedProvider = new PendingSceneProvider(_pendingScene);
            CoreResourceAccess.Bind(_ownedProvider);
            var load = _fixture.Loader.LoadAsync(HybridCLRLoaderTests.Address);
            Assert.That(load.IsDone, Is.False, "必须经过真实 pending Resource 加载，不能只验证同步完成。");
            _ownedRequest = _fixture.Loader.StartModuleAsync(HybridCLRLoaderTests.Address, Entry, typeof(HybridCLRWaitingModule).FullName);
            YLFramework.Async.CustomAsyncOperation<HybridCLRModuleRequest> observed = _ownedRequest;
            var samples = new List<float>();
            var codeStage = _ownedRequest.Stage;
            var sawCodeProgress = false;
            void Sample()
            {
                samples.Add(observed.Progress);
                if (!load.IsDone && observed.Progress > 0 && observed.Progress < 1)
                {
                    sawCodeProgress = true;
                }
            }
            Sample();
            for (var i = 0; i < 240 && _ownedRequest.Module == null && !_ownedRequest.IsDone; i++)
            {
                yield return null;
                Sample();
            }
            Assert.That(_ownedRequest.Module, Is.Not.Null, _ownedRequest.Error);
            var core = ModuleManager.StartUpModule(typeof(HybridCLRWaitingModule));
            Assert.That(core.IsDone, Is.False, "真实 Core 启动应等待受控场景请求。");
            Assert.That(core.Module, Is.SameAs(_ownedRequest.Module));
            var sceneStage = _ownedRequest.Stage;
            for (var i = 0; i < 4; i++)
            {
                yield return null;
                Sample();
            }
            var low = observed.Progress;
            _pendingScene.PendingProgress = 0.8f;
            for (var i = 0; i < 4; i++)
            {
                yield return null;
                Sample();
            }
            var high = observed.Progress;
            Assert.That(_ownedRequest.IsDone, Is.False);
            Assert.That(core.Progress, Is.GreaterThan(0.5f).And.LessThan(1));
            _pendingScene.Succeed(_ownedScene);
            for (var i = 0; i < 30 && !_ownedRequest.IsDone; i++)
            {
                yield return null;
                Sample();
            }

            Assert.That(_ownedRequest.IsDone, Is.True);
            Assert.That(_ownedRequest.Exception, Is.Null, _ownedRequest.Error);
            Assert.That(sawCodeProgress, Is.True, "代码资源尚未完成时必须转发中间进度。");
            Assert.That(low, Is.GreaterThan(0).And.LessThan(1));
            Assert.That(high, Is.GreaterThan(low).And.LessThan(1), "Core 场景仍在等待时，外层进度必须随 Core 前进。");
            Assert.That(sceneStage, Is.Not.EqualTo(codeStage), "代码加载与 Core 启动必须可区分。");
            for (var i = 0; i < samples.Count; i++)
            {
                Assert.That(samples[i], Is.InRange(0f, 1f));
                if (i > 0)
                {
                    Assert.That(samples[i], Is.GreaterThanOrEqualTo(samples[i - 1]), "切换阶段时不能倒退。");
                }
            }
            Assert.That(observed.Progress, Is.EqualTo(1));
            Assert.That(_ownedRequest.Module.State, Is.EqualTo(ModuleState.Running));
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator EditorFastStartsWithoutCodeResourcesAndRetainsActiveModuleOwnership()
        {
            yield return InitializeWithoutCodeResources();
            var starts = 0;
            HybridCLRProbeModule.Starting = () => starts++;
            _ownedRequest = StartEditorModule(Entry, typeof(HybridCLRProbeModule).FullName);
            for (var i = 0; i < 30 && !_ownedRequest.IsDone; i++)
            {
                yield return null;
            }

            Assert.That(_ownedRequest.IsDone, Is.True);
            Assert.That(_ownedRequest.Exception, Is.Null, _ownedRequest.Error);
            Assert.That(starts, Is.EqualTo(1));
            Assert.That(_ownedRequest.Module, Is.SameAs(ModuleManager.GetModule(typeof(HybridCLRProbeModule))));
            Assert.That(_ownedRequest.Module.State, Is.EqualTo(ModuleState.Running));
            Assert.That(_ownedRequest.Code, Is.Not.Null);
            var mode = typeof(HybridCLRLoadResult).GetProperty("Mode", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(mode, Is.Not.Null, "必须明确区分 EditorFast、EditorSimulation 和 Native。");
            Assert.That(mode.PropertyType.IsEnum, Is.True);
            Assert.That(mode.PropertyType.FullName, Is.EqualTo("YLFramework.Integrations.HybridCLR.HybridCLRLoadMode"));
            Assert.That(mode.GetValue(_ownedRequest.Code).ToString(), Is.EqualTo("EditorFast"));
            Assert.That(_ownedRequest.Code.CodePackageId, Is.Null.Or.Empty, "未经 manifest 校验不能伪造 CodePackageId。");
            Assert.That(_ownedRequest.Code.Assemblies[Entry], Is.SameAs(typeof(HybridCLRProbeModule).Assembly));
            Assert.That(_nativeCalls, Is.Zero);
            Assert.That(_fixture.Process.Projects, Is.Empty);
            Assert.That(_fixture.Process.Assemblies, Is.Empty);
            Assert.That(_fixture.Process.ActiveJob, Is.Null);
            Assert.That(_fixture.ResourcesSystem.GetDiagnosticsReport().History, Is.Empty, "快路径不能尝试读取 manifest、DLL 或 metadata。");
            Assert.That(_fixture.ResourcesSystem.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => _fixture.Loader.Dispose());
            _ownedRequest.Module.Close();
            _fixture.Loader.Dispose();
            Assert.That(_fixture.ResourcesSystem.IsAvailable, Is.True);
        }

        [UnityTest]
        public IEnumerator EditorFastMissingEntryFinishesDiagnosticallyWithoutLoadingNativeCode()
        {
            yield return InitializeWithoutCodeResources();
            const string Missing = "Missing.EditorFast.Entry";
            _ownedRequest = StartEditorModule(Missing, typeof(HybridCLRProbeModule).FullName);
            for (var i = 0; i < 30 && !_ownedRequest.IsDone; i++)
            {
                yield return null;
            }

            Assert.That(_ownedRequest.IsDone, Is.True, "已接受的缺失 entry 请求必须进入失败终态。");
            Assert.That(_ownedRequest.Exception, Is.Not.Null);
            Assert.That(_ownedRequest.Error, Does.Contain(Missing));
            Assert.That(_ownedRequest.Module, Is.Null);
            Assert.That(_ownedRequest.RequiresRestart, Is.False);
            Assert.That(ModuleManager.GetModule(typeof(HybridCLRProbeModule)), Is.Null);
            Assert.That(_nativeCalls, Is.Zero);
            Assert.That(_fixture.Process.Projects, Is.Empty);
            Assert.That(_fixture.Process.Assemblies, Is.Empty);
            Assert.That(_fixture.ResourcesSystem.GetDiagnosticsReport().History, Is.Empty);
            _fixture.Loader.Dispose();
        }

        [UnityTest]
        public IEnumerator EditorFastKeepsModuleOwnershipAfterTheBorrowedResourceSystemShutsDown()
        {
            yield return InitializeWithoutCodeResources();
            _ownedRequest = StartEditorModule(Entry, typeof(HybridCLRProbeModule).FullName);
            yield return _ownedRequest;
            Assert.That(_ownedRequest.Exception, Is.Null);
            Assert.That(_ownedRequest.Module.State, Is.EqualTo(ModuleState.Running));
            using (var shutdown = _fixture.ResourcesSystem.ShutdownAsync())
            {
                yield return shutdown;
                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
            }
            Assert.That(_fixture.ResourcesSystem.IsAvailable, Is.False);
            Assert.That(_ownedRequest.Module.State, Is.EqualTo(ModuleState.Running));
            Assert.Throws<InvalidOperationException>(() => _fixture.Loader.Dispose(), "Resource shutdown is not Module shutdown.");
            _ownedRequest.Module.Close();
            Assert.DoesNotThrow(() => _fixture.Loader.Dispose());
        }

        private IEnumerator InitializeWithoutCodeResources()
        {
            yield return _fixture.TearDown();
            var catalog = new ResourceCatalog("hybridclr-test", Array.Empty<ResourceCatalogEntry>());
            using var init = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { catalog }));
            Assert.That(init.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), init.Error?.ToString());
            _fixture.ResourcesSystem = init.TakeResult();
            _fixture.ResourcesSystem.ConfigureDiagnosticsHistory(8);
            _fixture.Calls.LoadMetadata = _ =>
            {
                _nativeCalls++;
                throw new InvalidOperationException("EditorFast must not load metadata.");
            };
            _fixture.Calls.LoadAssembly = _ =>
            {
                _nativeCalls++;
                throw new InvalidOperationException("EditorFast must not load DLL bytes.");
            };
            _fixture.Loader = new HybridCLRLoader(_fixture.ResourcesSystem, "base-test", _fixture.Calls, _fixture.Process);
            _ownedProvider = _fixture.ResourcesSystem.BindCoreResourceProvider();
        }

        private HybridCLRModuleRequest StartEditorModule(string entryAssemblyName, string moduleTypeFullName)
        {
            // 缺失新 API 时以普通断言 RED，而不是让测试程序集编译失败。
            var method = typeof(HybridCLRLoader).GetMethod("StartEditorModuleAsync", BindingFlags.Instance | BindingFlags.Public,
                null, new[] { typeof(string), typeof(string), typeof(string) }, null);
            Assert.That(method, Is.Not.Null, "缺少 public StartEditorModuleAsync(string, string, string)。");
            Assert.That(method.ReturnType, Is.EqualTo(typeof(HybridCLRModuleRequest)));
            return (HybridCLRModuleRequest)method.Invoke(_fixture.Loader, new object[] { "hybridclr-test", entryAssemblyName, moduleTypeFullName });
        }
#endif

        private sealed class PendingSceneProvider : ICoreResourceProvider
        {
            private readonly PendingScene _request;
            internal PendingSceneProvider(PendingScene request) => this._request = request;
            public CoreResourceRequest<string> ReadTextAsync(string projectId, string address) => throw new NotSupportedException();
            public CorePrefabLease AcquirePrefab(string projectId, string address) => throw new NotSupportedException();
            public CoreResourceRequest<CorePrefabLease> AcquirePrefabAsync(string projectId, string address) => throw new NotSupportedException();
            public CoreResourceRequest<CoreSceneResult> LoadSceneAsync(string projectId, string address, LoadSceneMode mode)
            {
                Assert.That(projectId, Is.EqualTo("hybridclr-test"));
                Assert.That(address, Is.EqualTo("owned-pending-scene"));
                Assert.That(mode, Is.EqualTo(LoadSceneMode.Single));
                return _request;
            }
        }

        private sealed class PendingScene : CoreResourceRequest<CoreSceneResult>
        {
            private CoreResourceStatus _status;
            private CoreSceneResult _result;
            private bool _isTaken;
            internal float PendingProgress;
            public override CoreResourceStatus Status => _status;
            public override float Progress => _status == CoreResourceStatus.Succeeded ? 1 : PendingProgress;
            public override CoreResourceException Error => null;
            private bool _isDisposed;
            public override bool IsDisposed => _isDisposed;
            public override event Action<CoreResourceRequest<CoreSceneResult>> Completed;
            internal void Succeed(Scene scene)
            {
                if (_status != CoreResourceStatus.Pending || _isDisposed)
                {
                    return;
                }

                _result = new CoreSceneResult(scene, false);
                _status = CoreResourceStatus.Succeeded;
                var notify = Completed;
                Completed = null;
                notify?.Invoke(this);
            }
            public override CoreSceneResult TakeResult()
            {
                if (_isDisposed)
                {
                    throw new ObjectDisposedException(nameof(PendingScene));
                }

                if (_isTaken || _status != CoreResourceStatus.Succeeded)
                {
                    throw new InvalidOperationException("No transferable scene result.");
                }

                _isTaken = true;
                return _result;
            }
            public override void Cancel()
            {
                if (_status != CoreResourceStatus.Pending)
                {
                    return;
                }

                _status = CoreResourceStatus.Canceled;
                var notify = Completed;
                Completed = null;
                notify?.Invoke(this);
            }
            public override void Dispose()
            {
                if (_isDisposed)
                {
                    return;
                }
                Cancel();
                _isDisposed = true;
                Completed = null;
            }
        }
    }
}
