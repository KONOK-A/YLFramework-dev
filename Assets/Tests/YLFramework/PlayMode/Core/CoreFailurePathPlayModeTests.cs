using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace YLFramework.Core.Tests
{
    public sealed class YLCoreFailureProbeModule : YLFramework.Core.Module
    {
        public bool FailStart;
        public bool FailClose;
        public int CloseCount;
        public override void OnStart()
        {
            AddModel(new YLCoreFailureProbeModel { Id = 1 });
            if (FailStart)
            {
                throw new InvalidOperationException("YLCoreSmoke startup failure");
            }
        }
        protected override void OnClose()
        {
            CloseCount++;
            if (FailClose)
            {
                throw new InvalidOperationException("YLCoreSmoke close failure");
            }
        }
    }

    public sealed class YLCoreFailureProbeModel : YLFramework.Core.Model
    {
        public int ExitCount;
        public override void OnInit()
        {
        }
        public override void OnExit() => ExitCount++;
    }

    public sealed class YLCoreShutdownProbeModule<T> : SingletonModule<YLCoreShutdownProbeModule<T>>
    {
        public bool IsBase;
        public bool FailBaseGetter;
        public bool EqualModules;
        public Exception ProjectGetterError;
        public Exception TableGetterError;
        public Exception SceneGetterError;
        public int ProjectReadCount;
        public bool RegisteredAtProjectRead;
        public Action StartAction;
        public Action CloseAction;
        public int CloseCount;
        public override bool IsBaseModule => FailBaseGetter ? throw new InvalidOperationException("base classification failure") : IsBase;
        public override string ProjectName
        {
            get
            {
                ProjectReadCount++;
                RegisteredAtProjectRead = ReferenceEquals(ModuleManager.GetModule(ModuleName), this);
                if (ProjectGetterError != null)
                {
                    throw ProjectGetterError;
                }

                return base.ProjectName;
            }
            set => base.ProjectName = value;
        }
        [Obsolete("已过时!")]
        public override Dictionary<string, Type> PreloadConfigTables => TableGetterError != null ? throw TableGetterError : null;
        public override string DefaultStartUpScene => SceneGetterError != null ? throw SceneGetterError : string.Empty;
        public override bool Equals(object obj) => ReferenceEquals(this, obj) || (EqualModules && obj is Module);
        public override int GetHashCode() => EqualModules ? 1 : base.GetHashCode();
        public override void OnStart() => StartAction?.Invoke();
        protected override void OnClose()
        {
            CloseCount++;
            CloseAction?.Invoke();
        }
    }

    public class CoreFailurePathPlayModeTests
    {
        private YLCoreFailureProbeModule _module;
        private CoreClassPoolScope _classPool;

        [SetUp]
        public void SetUp()
        {
            Assert.That(ModuleManager.AllModules, Is.Empty, "Characterize failures only in the independent test scene.");
            _classPool = new CoreClassPoolScope();
            _module = new YLCoreFailureProbeModule();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _module.FailClose = false;
            try
            {
                if (ReferenceEquals(ModuleManager.GetModule<YLCoreFailureProbeModule>(), _module))
                {
                    _module.Close();
                }
            }
            finally
            {
                _classPool.Restore(new Module[] { _module });
            }
            yield return null;
            Assert.That(ModuleManager.GetModule<YLCoreFailureProbeModule>(), Is.Null);
        }

        [UnityTest]
        public IEnumerator StartupFailureAutomaticallyClosesAndDoesNotPolluteCurrent()
        {
            var other = new YLCoreDirectViewModule();
            try
            {
                var otherRequest = ModuleManager.StartUpModule(other);
                Assert.That(otherRequest.Error, Is.Null.Or.Empty);
                _module.FailStart = true;
                var request = ModuleManager.StartUpModule(_module);
                var deadline = Time.realtimeSinceStartup + 5;
                while (!request.IsDone && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.That(request.IsDone, Is.True);
                Assert.That(request.Error, Does.Contain("YLCoreSmoke startup failure"));
                Assert.That(request.Progress, Is.EqualTo(1));
                AssertStartupDetached(_module);
                Assert.That(_module.CloseCount, Is.EqualTo(1));
                var manager = typeof(ModuleManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                Assert.That(typeof(ModuleManager).GetField("_currentModule", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager), Is.SameAs(other));
                _module.Close();
                Assert.That(_module.CloseCount, Is.EqualTo(1));
                _module.FailStart = false;
                var restarted = ModuleManager.StartUpModule(_module);
                Assert.That(restarted.IsDone, Is.True);
                Assert.That(restarted.Error, Is.Null.Or.Empty);
                Assert.That(_module.State, Is.EqualTo(ModuleState.Running));
                Assert.That(request.Error, Does.Contain("YLCoreSmoke startup failure"));
            }
            finally
            {
                other.Close();
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator OnStartFailureUsesSingleClosePipelineForControllerViewAndModels()
        {
            var failed = new YLCoreShutdownProbeModule<YLCoreProbeModel>();
            var events = new List<string>();
            var original = new InvalidOperationException("OnStart failure with MVC");
            var bound = new YLCoreProbeModel { Id = 1, ExitAction = _ => events.Add("bound.exit") };
            var remaining = new YLCoreProbeModel { Id = 2, ExitAction = _ => events.Add("remaining.exit") };
            var controller = new YLCoreProbeController { ExitAction = _ => events.Add("controller.exit") };
            var prefab = DirectViewPrefab("YLCoreStartupRollbackPrefab");
            YLCoreSmokeView view = null;
            try
            {
                failed.CloseAction = () =>
                {
                    events.Add("module.close");
                    Assert.That(failed.State, Is.EqualTo(ModuleState.Closing));
                    Assert.That(ModuleManager.GetModule(failed.ModuleName), Is.SameAs(failed));
                    Assert.That(failed.GetController<YLCoreProbeController>(), Is.SameAs(controller));
                    Assert.That(failed.GetView(prefab.name), Is.SameAs(view));
                    Assert.That(bound.ExitCount + remaining.ExitCount, Is.Zero);
                    failed.Close();
                };
                failed.StartAction = () =>
                {
                    failed.AddModel(bound);
                    failed.AddModel(remaining);
                    failed.AddController(controller);
                    view = failed.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null);
                    view.BoundModel = bound;
                    view.ViewRemoved = () => events.Add("view.remove");
                    throw original;
                };
                var request = ModuleManager.StartUpModule(failed);
                Assert.That(request.IsDone, Is.True);
                Assert.That(request.Exception, Is.SameAs(original));
                Assert.That(request.Error, Does.Contain(original.Message));
                Assert.That(events, Is.EqualTo(new[] { "module.close", "controller.exit", "view.remove", "bound.exit", "remaining.exit" }));
                AssertStartupDetached(failed);
                Assert.That(view.Module, Is.Null);
                Assert.That(view.RemovedCount, Is.EqualTo(1));
                Assert.That(view.gameObject.activeSelf, Is.False);
                Assert.That(bound.Module, Is.Null);
                Assert.That(remaining.Module, Is.Null);
                Assert.That(controller.Module, Is.Null);
                failed.Close();
                Assert.That(failed.CloseCount, Is.EqualTo(1));
                Assert.That(controller.ExitCount, Is.EqualTo(1));
                Assert.That(bound.ExitCount, Is.EqualTo(1));
                Assert.That(remaining.ExitCount, Is.EqualTo(1));
                Assert.That(view.RemovedCount, Is.EqualTo(1));
            }
            finally
            {
                failed.CloseAction = null;
                try
                {
                    failed.Close();
                }
                finally
                {
                    _classPool.Restore(new Module[] { failed });
                    ReleaseDirectView(prefab, view);
                }
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator PartialModelInitializationFailureExitsCompletedAndFailedObjectsOnlyOnce()
        {
            var failed = new YLCoreShutdownProbeModule<YLCoreProbeModel>();
            var original = new InvalidOperationException("second Model initialization failure");
            var completed = new YLCoreProbeModel { Id = 1 };
            var partial = new YLCoreProbeModel { Id = 2, InitAction = _ => throw original };
            try
            {
                failed.StartAction = () =>
                {
                    failed.AddModel(completed);
                    failed.AddModel(partial);
                };
                var request = ModuleManager.StartUpModule(failed);
                Assert.That(request.IsDone, Is.True);
                Assert.That(request.Exception, Is.SameAs(original));
                AssertStartupDetached(failed);
                Assert.That(completed.InitCount, Is.EqualTo(1));
                Assert.That(partial.InitCount, Is.EqualTo(1));
                Assert.That(completed.ExitCount, Is.EqualTo(1));
                Assert.That(partial.ExitCount, Is.EqualTo(1));
                Assert.That(completed.RegisteredAtExit && partial.RegisteredAtExit, Is.True);
                Assert.That(completed.Module, Is.Null);
                Assert.That(partial.Module, Is.Null);
                failed.Close();
                Assert.That(failed.CloseCount, Is.EqualTo(1));
                Assert.That(completed.ExitCount, Is.EqualTo(1));
                Assert.That(partial.ExitCount, Is.EqualTo(1));
            }
            finally
            {
                failed.Close();
                _classPool.Restore(new Module[] { failed });
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator StartupPreservesOriginalAndAllObjectAndModuleCleanupExceptionsIncludingExistingData()
        {
            const string Key = "YLFramework.Core.CleanupException";
            for (int existing = 0; existing < 2; existing++)
            {
                var failed = new YLCoreShutdownProbeModule<YLCoreProbeModel>();
                var original = new InvalidOperationException("original Model init error");
                var objectCleanup = new InvalidOperationException("failed Model object cleanup error");
                var siblingCleanup = new InvalidOperationException("completed Model rollback error");
                var moduleCleanup = new InvalidOperationException("module rollback hook error");
                var previousCleanup = new InvalidOperationException("previous cleanup data");
                if (existing != 0)
                {
                    original.Data[Key] = previousCleanup;
                }

                var completed = new YLCoreProbeModel { Id = 1, ExitAction = _ => throw siblingCleanup };
                var partial = new YLCoreProbeModel
                {
                    Id = 2,
                    InitAction = _ => throw original,
                    ExitAction = _ => throw objectCleanup
                };
                try
                {
                    failed.StartAction = () =>
                    {
                        failed.AddModel(completed);
                        failed.AddModel(partial);
                    };
                    failed.CloseAction = () => throw moduleCleanup;
                    var request = ModuleManager.StartUpModule(failed);
                    Assert.That(request.IsDone, Is.True);
                    Assert.That(request.Exception, Is.SameAs(original));
                    Assert.That(original.Data[Key], Is.TypeOf<AggregateException>());
                    var errors = ((AggregateException)original.Data[Key]).Flatten().InnerExceptions;
                    var expected = new List<Exception> { objectCleanup, siblingCleanup, moduleCleanup };
                    if (existing != 0)
                    {
                        expected.Add(previousCleanup);
                    }

                    Assert.That(errors, Is.EquivalentTo(expected));
                    Assert.That(request.Error, Does.Contain(original.ToString()));
                    foreach (var error in expected)
                    {
                        Assert.That(request.Error, Does.Contain(error.ToString()));
                    }

                    AssertStartupDetached(failed);
                    Assert.That(completed.Module, Is.Null);
                    Assert.That(partial.Module, Is.Null);
                    Assert.That(completed.ExitCount, Is.EqualTo(1));
                    Assert.That(partial.ExitCount, Is.EqualTo(1));
                    failed.Close();
                    Assert.That(failed.CloseCount, Is.EqualTo(1));
                }
                finally
                {
                    failed.CloseAction = null;
                    completed.ExitAction = null;
                    partial.ExitAction = null;
                    try
                    {
                        failed.Close();
                    }
                    finally
                    {
                        _classPool.Restore(new Module[] { failed });
                    }
                }
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator FailureCallbacksSeeDetachedClosedModuleAndCanRestartSameOrNewInstance()
        {
            for (int newInstance = 0; newInstance < 2; newInstance++)
            {
                var failed = new YLCoreShutdownProbeModule<YLCoreProbeModel>();
                var original = new InvalidOperationException("failure before completion callback");
                StartUpModuleRequest restarted = null;
                StartUpModuleRequest pending = null;
                int callbacks = 0;
                try
                {
                    failed.StartAction = () =>
                    {
                        failed.AddModel(new YLCoreProbeModel());
                        pending = ModuleManager.StartUpModule(failed);
                        pending.AddCompleteEvent(completed =>
                        {
                            callbacks++;
                            Assert.That(completed, Is.SameAs(pending));
                            Assert.That(completed.IsDone, Is.True);
                            Assert.That(completed.Progress, Is.EqualTo(1));
                            Assert.That(completed.Exception, Is.SameAs(original));
                            AssertStartupDetached(failed);
                            Assert.That(YLCoreShutdownProbeModule<YLCoreProbeModel>.Instance, Is.Null);
                            failed.StartAction = null;
                            restarted = newInstance == 0 ? ModuleManager.StartUpModule(failed) :
                                ModuleManager.StartUpModule<YLCoreShutdownProbeModule<YLCoreProbeModel>>();
                        });
                        throw original;
                    };
                    var request = ModuleManager.StartUpModule(failed);
                    Assert.That(callbacks, Is.EqualTo(1));
                    Assert.That(request, Is.SameAs(pending));
                    Assert.That(restarted, Is.Not.Null.And.Not.SameAs(request));
                    Assert.That(restarted.IsDone, Is.True);
                    Assert.That(restarted.Error, Is.Null.Or.Empty);
                    Assert.That(restarted.Exception, Is.Null);
                    Assert.That(restarted.Module.State, Is.EqualTo(ModuleState.Running));
                    Assert.That(ReferenceEquals(restarted.Module, failed), Is.EqualTo(newInstance == 0));
                    Assert.That(ModuleManager.GetModule(failed.ModuleName), Is.SameAs(restarted.Module));
                    Assert.That(request.Exception, Is.SameAs(original));
                    Assert.That(request.Error, Does.Contain(original.Message));
                    Assert.That(failed.CloseCount, Is.EqualTo(1));
                }
                finally
                {
                    failed.StartAction = null;
                    try
                    {
                        restarted?.Module.Close();
                        failed.Close();
                    }
                    finally
                    {
                        _classPool.Restore(new Module[] { failed, restarted?.Module });
                    }
                }
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator ProjectNameDefaultsBeforeStartupAndIsCapturedWithoutDependingOnOnStart()
        {
            var failed = new YLCoreShutdownProbeModule<YLCoreProbeModel>();
            var original = new InvalidOperationException("project capture before failed OnStart");
            try
            {
                Assert.That(failed.ProjectName, Is.EqualTo(failed.GetType().Name));
                failed.ProjectName = "YLCoreExplicitStartupProject";
                failed.ProjectReadCount = 0;
                failed.StartAction = () =>
                {
                    Assert.That(failed.ProjectReadCount, Is.GreaterThan(0), "Startup captures the project even when no table or scene is requested.");
                    throw original;
                };
                var request = ModuleManager.StartUpModule(failed);
                Assert.That(request.IsDone, Is.True);
                Assert.That(request.Exception, Is.SameAs(original));
                Assert.That(failed.ProjectName, Is.EqualTo("YLCoreExplicitStartupProject"));
                AssertStartupDetached(failed);
            }
            finally
            {
                failed.Close();
                _classPool.Restore(new Module[] { failed });
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator PreStartupGetterFailuresReleaseRecordsAndOnlyCloseAcquiredRegistration()
        {
            for (int getter = 0; getter < 3; getter++)
            {
                var failed = new YLCoreShutdownProbeModule<YLCoreProbeModel>();
                for (int closed = 0; closed < 2; closed++)
                {
                    var originalState = failed.State;
                    var closes = failed.CloseCount;
                    var original = new InvalidOperationException("pre-start getter failure " + getter);
                    bool started = false;
                    failed.StartAction = () => started = true;
                    if (getter == 0)
                    {
                        failed.ProjectGetterError = original;
                    }

                    if (getter == 1)
                    {
                        failed.TableGetterError = original;
                    }

                    if (getter == 2)
                    {
                        failed.SceneGetterError = original;
                    }

                    try
                    {
                        var request = ModuleManager.StartUpModule(failed);
                        Assert.That(request.IsDone, Is.True);
                        Assert.That(request.Exception, Is.SameAs(original));
                        Assert.That(request.Error, Does.Contain(original.Message));
                        Assert.That(started, Is.False);
                        if (getter == 0 && !failed.RegisteredAtProjectRead)
                        {
                            Assert.That(failed.State, Is.EqualTo(originalState));
                            Assert.That(failed.CloseCount, Is.EqualTo(closes));
                            AssertStartupRecordsDetached(failed);
                        }
                        else
                        {
                            AssertStartupDetached(failed);
                            Assert.That(failed.CloseCount, Is.EqualTo(closes + 1));
                        }
                    }
                    finally
                    {
                        failed.ProjectGetterError = null;
                        failed.TableGetterError = null;
                        failed.SceneGetterError = null;
                        failed.StartAction = null;
                        try
                        {
                            failed.Close();
                        }
                        finally
                        {
                            _classPool.Restore(new Module[] { failed });
                        }
                    }
                    // 下一轮在真正Closed的实例上失败，不能把注册前失败一律重置成Created。
                    try
                    {
                        Assert.That(ModuleManager.StartUpModule(failed).Error, Is.Null.Or.Empty);
                    }
                    finally
                    {
                        failed.Close();
                    }
                }
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator OuterFailurePreservesNestedSuccessfulModuleAndExplicitlyBorrowedMvc()
        {
            var outer = new YLCoreShutdownProbeModule<YLCoreProbeModel>();
            var inner = new YLCoreDirectViewModule();
            var prefab = DirectViewPrefab("YLCoreBorrowedStartupViewPrefab");
            YLCoreProbeModel sharedModel = null;
            YLCoreProbeController sharedController = null;
            YLCoreSmokeView sharedView = null;
            var original = new InvalidOperationException("outer failed after borrowing MVC");
            try
            {
                outer.StartAction = () =>
                {
                    Assert.That(ModuleManager.StartUpModule(inner).Error, Is.Null.Or.Empty);
                    sharedModel = inner.LoadModel<YLCoreProbeModel>();
                    sharedController = inner.LoadController<YLCoreProbeController>();
                    sharedView = inner.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null);
                    var owner = ModuleManager.GetModule<YLCoreDirectViewModule>();
                    Assert.That(owner.GetModel<YLCoreProbeModel>(), Is.SameAs(sharedModel));
                    Assert.That(owner.GetController<YLCoreProbeController>(), Is.SameAs(sharedController));
                    Assert.That(owner.GetView(prefab.name, sharedView.Id), Is.SameAs(sharedView));
                    throw original;
                };
                var request = ModuleManager.StartUpModule(outer);
                Assert.That(request.IsDone, Is.True);
                Assert.That(request.Exception, Is.SameAs(original));
                AssertStartupDetached(outer);
                Assert.That(outer.CloseCount, Is.EqualTo(1));
                Assert.That(ModuleManager.GetModule<YLCoreDirectViewModule>(), Is.SameAs(inner));
                Assert.That(inner.State, Is.EqualTo(ModuleState.Running));
                Assert.That(sharedModel.Module, Is.SameAs(inner));
                Assert.That(sharedController.Module, Is.SameAs(inner));
                Assert.That(sharedView.Module, Is.SameAs(inner));
                Assert.That(sharedModel.ExitCount, Is.Zero);
                Assert.That(sharedController.ExitCount, Is.Zero);
                Assert.That(sharedView.RemovedCount, Is.Zero);
                Assert.That(sharedView.gameObject.activeSelf, Is.True);
                var manager = typeof(ModuleManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                Assert.That(typeof(ModuleManager).GetField("_currentModule", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager), Is.SameAs(inner));
            }
            finally
            {
                try
                {
                    outer.Close();
                    inner.Close();
                }
                finally
                {
                    _classPool.Restore(new Module[] { outer, inner });
                    ReleaseDirectView(prefab, sharedView);
                }
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator CloseFailureDetachesOnlyOwnedRegistrationAndKeepsOtherCurrent()
        {
            var startup = ModuleManager.StartUpModule(_module);
            var deadline = Time.realtimeSinceStartup + 5;
            while (!startup.IsDone && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(startup.IsDone, Is.True);
            Assert.That(startup.Error, Is.Null.Or.Empty);
            var other = new YLCoreDirectViewModule();
            try
            {
                var otherStartup = ModuleManager.StartUpModule(other);
                deadline = Time.realtimeSinceStartup + 5;
                while (!otherStartup.IsDone && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.That(otherStartup.IsDone, Is.True);
                Assert.That(otherStartup.Error, Is.Null.Or.Empty);
                yield return null;
                _module.FailClose = true;
                Assert.Throws<AggregateException>(() => _module.Close());
                Assert.That(_module.State, Is.EqualTo(ModuleState.Closed));
                Assert.That(ModuleManager.GetModule<YLCoreFailureProbeModule>(), Is.Null);
                Assert.That(ModuleManager.GetModule<YLCoreDirectViewModule>(), Is.SameAs(other));
                var manager = typeof(ModuleManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                Assert.That(typeof(ModuleManager).GetField("_currentModule", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager), Is.SameAs(other));
            }
            finally
            {
                if (ReferenceEquals(ModuleManager.GetModule<YLCoreDirectViewModule>(), other))
                {
                    other.Close();
                }

                _classPool.Restore(new Module[] { _module, other });
            }
        }

        [UnityTest]
        public IEnumerator CloseFailureEndsOwnershipBeforeReportingAndAllowsExplicitRestart()
        {
            var request = ModuleManager.StartUpModule(_module);
            var deadline = Time.realtimeSinceStartup + 5;
            while (!request.IsDone && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(request.IsDone, Is.True);
            Assert.That(request.Error, Is.Null.Or.Empty);
            yield return null;
            var model = _module.GetModel<YLCoreFailureProbeModel>(1);
            _module.FailClose = true;
            var error = Assert.Throws<AggregateException>(() => _module.Close());
            Assert.That(error.InnerExceptions[0].Message, Is.EqualTo("YLCoreSmoke close failure"));
            Assert.That(model.ExitCount, Is.EqualTo(1));
            Assert.That(_module.GetModels<YLCoreFailureProbeModel>(), Is.Empty);
            Assert.That(model.Module, Is.Null);
            Assert.That(ModuleManager.GetModule<YLCoreFailureProbeModule>(), Is.Null);
            Assert.That(_module.State, Is.EqualTo(ModuleState.Closed));
            _module.Close();
            Assert.That(_module.CloseCount, Is.EqualTo(1));
            Assert.That(model.ExitCount, Is.EqualTo(1));
            Assert.That(ModuleManager.GetModule<YLCoreFailureProbeModule>(), Is.Null);
            Assert.That(_module.State, Is.EqualTo(ModuleState.Closed));
            _module.FailClose = false;
            Assert.That(ModuleManager.StartUpModule(_module).Error, Is.Null.Or.Empty);
        }

        [UnityTest]
        public IEnumerator ShutdownClosesOrdinaryThenBaseInReverseRegistrationOrderAndContinuesAfterErrors()
        {
            var events = new List<string>();
            var baseFirst = new YLCoreShutdownProbeModule<YLCoreProbeModel> { IsBase = true };
            var ordinaryFirst = new YLCoreShutdownProbeModule<YLCoreProbeController>();
            var baseLast = new YLCoreShutdownProbeModule<YLCoreFailureProbeModel> { IsBase = true };
            var ordinaryLast = new YLCoreShutdownProbeModule<YLCoreFailureProbeModule>();
            var owned = new Module[] { baseFirst, ordinaryFirst, baseLast, ordinaryLast };
            try
            {
                foreach (var item in owned)
                {
                    var request = ModuleManager.StartUpModule(item);
                    Assert.That(request.IsDone, Is.True);
                    Assert.That(request.Error, Is.Null.Or.Empty);
                }
                Assert.Throws<InvalidOperationException>(() => baseFirst.Close());
                Assert.That(baseFirst.State, Is.EqualTo(ModuleState.Running));
                Assert.That(baseFirst.CloseCount, Is.Zero);
                var model = baseFirst.LoadModel<YLCoreProbeModel>();
                var closeError = new InvalidOperationException("ordinary close");
                ordinaryLast.CloseAction = () =>
                {
                    events.Add("ordinary.last");
                    Assert.That(baseFirst.GetModel<YLCoreProbeModel>(), Is.SameAs(model));
                    Assert.That(baseFirst.State, Is.EqualTo(ModuleState.Running));
                    Assert.Throws<InvalidOperationException>(() => ModuleManager.StartUpModule(_module));
                    Assert.Throws<InvalidOperationException>(() => ordinaryFirst.Close());
                    ModuleManager.Shutdown();
                    throw closeError;
                };
                ordinaryFirst.CloseAction = () => events.Add("ordinary.first");
                baseLast.CloseAction = () => events.Add("base.last");
                baseFirst.CloseAction = () => events.Add("base.first");
                var error = Assert.Throws<AggregateException>(() => ModuleManager.Shutdown());
                Assert.That(error.InnerExceptions, Is.EqualTo(new[] { closeError }));
                Assert.That(events, Is.EqualTo(new[] { "ordinary.last", "ordinary.first", "base.last", "base.first" }));
                Assert.That(ModuleManager.AllModules, Is.Empty);
                Assert.That(ModuleManager.AllBaseModules, Is.Empty);
                var manager = typeof(ModuleManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                foreach (var name in new[] { "_currentModule", "_startupContext" })
                {
                    Assert.That(typeof(ModuleManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager), Is.Null, name);
                }

                foreach (var name in new[] { "_startingModules", "_registrationOrder" })
                {
                    Assert.That((ICollection)typeof(ModuleManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager), Is.Empty, name);
                }

                Assert.That(model.Module, Is.Null);
                Assert.That(model.ExitCount, Is.EqualTo(1));
                Assert.That(YLCoreShutdownProbeModule<YLCoreProbeModel>.Instance, Is.Null);
                foreach (var item in owned)
                {
                    Assert.That(item.State, Is.EqualTo(ModuleState.Closed));
                }

                ModuleManager.Shutdown();
                Assert.That(ordinaryLast.CloseCount, Is.EqualTo(1));
                Assert.That(baseFirst.CloseCount, Is.EqualTo(1));
                ordinaryLast.CloseAction = null;
                Assert.That(ModuleManager.StartUpModule(ordinaryLast).Error, Is.Null.Or.Empty);
            }
            finally
            {
                ordinaryLast.CloseAction = null;
                ModuleManager.Shutdown();
                _classPool.Restore(owned);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator FailedBaseStartupAutomaticallyClosesAndSingletonCanRestart()
        {
            var failed = new YLCoreShutdownProbeModule<YLCoreProbeModel> { IsBase = true };
            var model = new YLCoreProbeModel();
            try
            {
                failed.StartAction = () =>
                {
                    failed.AddModel(model);
                    throw new InvalidOperationException("base startup failure");
                };
                failed.CloseAction = () =>
                {
                    Assert.That(failed.State, Is.EqualTo(ModuleState.Closing));
                    Assert.That(model.ExitCount, Is.Zero);
                    Assert.That(failed.GetModel<YLCoreProbeModel>(), Is.SameAs(model));
                };
                var request = ModuleManager.StartUpModule(failed);
                Assert.That(request.IsDone, Is.True);
                Assert.That(request.Error, Does.Contain("base startup failure"));
                AssertStartupDetached(failed);
                Assert.That(failed.CloseCount, Is.EqualTo(1));
                Assert.That(model.ExitCount, Is.EqualTo(1));
                Assert.That(model.Module, Is.Null);
                Assert.That(YLCoreShutdownProbeModule<YLCoreProbeModel>.Instance, Is.Null);
                failed.Close();
                Assert.That(failed.CloseCount, Is.EqualTo(1));
                failed.StartAction = null;
                failed.CloseAction = null;
                Assert.That(ModuleManager.StartUpModule(failed).Error, Is.Null.Or.Empty);
                Assert.That(failed.State, Is.EqualTo(ModuleState.Running));
                Assert.That(failed.GetModels<Model>(), Is.Empty);
            }
            finally
            {
                failed.CloseAction = null;
                failed.IsBase = false;
                failed.Close();
                _classPool.Restore(new Module[] { failed });
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator ShutdownTracksReferenceIdentityAndPartiallyRegisteredStartupFailure()
        {
            var first = new YLCoreShutdownProbeModule<YLCoreProbeModel> { EqualModules = true };
            var second = new YLCoreShutdownProbeModule<YLCoreProbeController> { EqualModules = true };
            var failed = new YLCoreShutdownProbeModule<YLCoreFailureProbeModel> { FailBaseGetter = true };
            var owned = new Module[] { first, second, failed };
            try
            {
                Assert.That(ModuleManager.StartUpModule(first).Error, Is.Null.Or.Empty);
                Assert.That(ModuleManager.StartUpModule(second).Error, Is.Null.Or.Empty);
                second.Close();
                var request = ModuleManager.StartUpModule(failed);
                Assert.That(request.IsDone, Is.True);
                Assert.That(request.Error, Does.Contain("base classification failure"));
                AssertStartupDetached(failed);
                Assert.That(failed.CloseCount, Is.EqualTo(1));
                ModuleManager.Shutdown();
                Assert.That(ModuleManager.AllModules, Is.Empty);
                Assert.That(first.CloseCount, Is.EqualTo(1));
                Assert.That(second.CloseCount, Is.EqualTo(1));
                Assert.That(failed.CloseCount, Is.EqualTo(1));
                Assert.That(first.State, Is.EqualTo(ModuleState.Closed));
                Assert.That(failed.State, Is.EqualTo(ModuleState.Closed));
            }
            finally
            {
                ModuleManager.Shutdown();
                _classPool.Restore(owned);
            }
            yield return null;
        }

        private static void AssertStartupDetached(Module failed)
        {
            Assert.That(failed.State, Is.EqualTo(ModuleState.Closed));
            AssertStartupRecordsDetached(failed);
        }

        private static void AssertStartupRecordsDetached(Module failed)
        {
            Assert.That(ModuleManager.GetModule(failed.ModuleName), Is.Null);
            Assert.That(ModuleManager.AllBaseModules.ContainsKey(failed.ModuleName), Is.False);
            foreach (var name in new[] { "Models", "Controllers", "Views", "TypeViews" })
            {
                Assert.That((IDictionary)typeof(Module).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(failed), Is.Empty, name);
            }

            var manager = typeof(ModuleManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            foreach (var name in new[] { "_currentModule", "_startupContext" })
            {
                Assert.That(typeof(ModuleManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager), Is.Not.SameAs(failed), name);
            }

            var starting = (IDictionary)typeof(ModuleManager).GetField("_startingModules", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            Assert.That(starting.Contains(failed.GetType()), Is.False);
            var order = (IList)typeof(ModuleManager).GetField("_registrationOrder", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            foreach (var registered in order)
            {
                Assert.That(ReferenceEquals(registered, failed), Is.False);
            }
        }

        private static GameObject DirectViewPrefab(string name)
        {
            var prefab = new GameObject(name);
            prefab.SetActive(false);
            prefab.AddComponent<YLCoreSmokeView>();
            return prefab;
        }

        private static void ReleaseDirectView(GameObject prefab, YLCoreSmokeView view)
        {
            var loader = UnityEngine.Object.FindFirstObjectByType<YLFramework.Pooling.GameObjectPool>();
            if (loader && prefab)
            {
                loader.Clear("YL.Core.Direct/" + prefab.GetInstanceID());
            }

            if (view)
            {
                UnityEngine.Object.Destroy(view.gameObject);
            }

            if (prefab)
            {
                UnityEngine.Object.Destroy(prefab);
            }
        }

    }
}
