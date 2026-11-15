using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using CoroutineStarter = YLFramework.Async.CoroutineStarter;

namespace YLFramework.Core.Tests
{
    [Category("YL.Special.CoreSession")]
    public class CoreUISessionTests
    {
        private SceneSetup[] _previousScenes;
        private Action _restoreInputFocus;
        private Action _restoreStartupSession;

        [UnityTearDown]
        public IEnumerator RestoreOwnedSession()
        {
            RestoreInputFocus();
            try
            {
                if (EditorApplication.isPlaying)
                {
                    EditorApplication.isPaused = false;
                    yield return new ExitPlayMode();
                }
            }
            finally
            {
                EditorApplication.isPaused = false;
                var restore = _restoreStartupSession;
                _restoreStartupSession = null;
                try
                {
                    restore?.Invoke();
                }
                finally
                {
                    if (_previousScenes != null)
                    {
                        EditorSceneManager.RestoreSceneManagerSetup(_previousScenes);
                        _previousScenes = null;
                    }
                }
            }
        }

        [UnityTest]
        public IEnumerator OriginalUIInputAndPendingOwnershipSurviveTwoActualPlaysWithoutDomainReload()
        {
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled &&
                (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0, Is.True,
                "The explicit owned session profile must prepare Domain Reload settings before discovery.");
            Assert.That(Enumerable.Range(0, SceneManager.sceneCount).All(index => !SceneManager.GetSceneAt(index).isDirty), Is.True,
                "Do not discard or automatically save a dirty scene to run acceptance.");
            _previousScenes = EditorSceneManager.GetSceneManagerSetup();
            EditorSceneManager.OpenScene("Assets/Tests/YLFramework/Acceptance/Core/UIAcceptance.unity", OpenSceneMode.Single);
            var entryType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("YLFramework.Core.Tests.UIAcceptanceEntry"))
                .Single(type => type != null);
            var roundField = entryType.GetField("Round");
            var resultField = entryType.GetField("Result");
            roundField.SetValue(null, 0);
            resultField.SetValue(null, null);
            for (int round = 1; round <= 2; round++)
            {
                yield return new EnterPlayMode(false);
                ConfigureOwnedInputFocus();
                var deadline = EditorApplication.timeSinceStartup + 30;
                while ((string)resultField.GetValue(null) != "READY round=" + round && EditorApplication.timeSinceStartup < deadline)
                {
                    var result = (string)resultField.GetValue(null);
                    Assert.That(result == null || !result.StartsWith("FAILED", StringComparison.Ordinal), Is.True, result);
                    yield return null;
                }
                Assert.That(resultField.GetValue(null), Is.EqualTo("READY round=" + round));
                Assert.That(roundField.GetValue(null), Is.EqualTo(round), "No intervening compile or Domain Reload may reset the original probe.");
                RestoreInputFocus();
                var entry = UnityEngine.Object.FindObjectOfType(entryType);
                Assert.That(entry, Is.Not.Null);
                EditorApplication.isPaused = true;
                entryType.GetMethod("PrepareExit").Invoke(entry, null);
                yield return new ExitPlayMode();
                EditorApplication.isPaused = false;
                Assert.That(entryType.GetMethod("VerifyExit").Invoke(null, null), Is.EqualTo("EXIT_PASS round=" + round));
            }
        }

        [UnityTest]
        public IEnumerator TwoActualPlaysInvalidatePendingStartupTaskAndAllowExplicitRestartWithoutDomainReload()
        {
            var oldProvider = new PendingStartupProvider();
            var currentProvider = new PendingStartupProvider();
            PrepareOwnedStartupSession(oldProvider, currentProvider);
            var managerInstance = typeof(ModuleManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic);
            var startingField = typeof(ModuleManager).GetField("_startingModules", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(managerInstance, Is.Not.Null);
            Assert.That(startingField, Is.Not.Null);
            var module = new PendingStartupModule();

            yield return new EnterPlayMode(false);
            Assert.That(CoreResourceAccess.Provider, Is.Null, "Never replace a foreign provider in the owned empty Play.");
            var oldManager = managerInstance.GetValue(null);
            var oldModules = ModuleManager.AllModules;
            var oldStarting = (IDictionary<Type, StartUpModuleRequest>)startingField.GetValue(oldManager);
            CoreResourceAccess.Bind(oldProvider);
            var old = ModuleManager.StartUpModule(module);
            var oldTask = old.AsTask();
            var routineField = typeof(StartUpModuleRequest).GetField("_routine", BindingFlags.Instance | BindingFlags.NonPublic);
            var oldRoutine = routineField?.GetValue(old) as IEnumerator;
            int oldCallbacks = 0;
            StartUpModuleRequest oldNotification = null;
            bool closedBeforeNotification = false;
            old.AddCompleteEvent(done =>
            {
                oldCallbacks++;
                oldNotification = done;
                closedBeforeNotification = module.State == ModuleState.Closed && oldProvider.Text.IsDisposed &&
                    !oldStarting.ContainsKey(module.GetType()) && !oldModules.ContainsKey(module.ModuleName);
            });
            Assert.That(oldProvider.TextCalls, Is.EqualTo(1));
            Assert.That(oldProvider.Project, Is.EqualTo(module.ProjectName));
            Assert.That(oldProvider.Address, Is.EqualTo(PendingStartupModule.TableAddress));
            Assert.That(old.Step, Is.EqualTo(StartingModuleSteps.PreloadConfigTables));
            Assert.That(old.Module, Is.SameAs(module));
            Assert.That(module.State, Is.EqualTo(ModuleState.Starting));
            Assert.That(oldModules[module.ModuleName], Is.SameAs(module));
            Assert.That(oldStarting[module.GetType()], Is.SameAs(old));
            Assert.That(ModuleManager.StartUpModule(module), Is.SameAs(old));
            Assert.That(old.AsTask(), Is.SameAs(oldTask));
            Assert.Throws<InvalidOperationException>(() => module.Close());
            Assert.Throws<InvalidOperationException>(() => ModuleManager.Shutdown());
            YLFramework.Async.CustomAsyncOperation<StartUpModuleRequest> operation = old;
            Assert.Throws<NotSupportedException>(() => operation.Abort());
            yield return null;
            Assert.That(old.IsDone, Is.False);
            Assert.That(oldTask.IsCompleted, Is.False, "Rejected Close, Shutdown and Abort must not cancel startup.");
            Assert.That(module.State, Is.EqualTo(ModuleState.Starting));
            Assert.That(oldCallbacks, Is.Zero);
            Assert.That(oldProvider.Text.Status, Is.EqualTo(CoreResourceStatus.Pending));
            Assert.That(oldProvider.Text.CancelCalls, Is.Zero);
            Assert.That(oldProvider.Text.DisposeCalls, Is.Zero);

            yield return new ExitPlayMode();
            Assert.That(old.IsDone, Is.True, "Stopping the real Play coroutine must not strand the accepted startup request.");
            Assert.That(oldRoutine, Is.Not.Null, "Capture the actual producer while pending, not a replacement iterator.");
            Assert.That(routineField.GetValue(old), Is.Null);
            Assert.That(oldRoutine.MoveNext(), Is.False, "The actual stopped producer must have been disposed.");
            Assert.That(oldTask.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(oldTask.GetAwaiter().GetResult(), Is.SameAs(old));
            Assert.That(old.AsTask(), Is.SameAs(oldTask));
            Assert.That(old.Exception, Is.TypeOf<InvalidOperationException>());
            Assert.That(old.keepWaiting, Is.False);
            Assert.That(old.Progress, Is.EqualTo(1f));
            Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(module.StartCount, Is.Zero);
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(oldCallbacks, Is.EqualTo(1));
            Assert.That(oldNotification, Is.SameAs(old));
            Assert.That(closedBeforeNotification, Is.True);
            // Text is a value, not an asset lease; Core owes exactly one consumer-request disposal.
            Assert.That(oldProvider.Text.IsDisposed, Is.True);
            Assert.That(oldProvider.Text.DisposeCalls, Is.EqualTo(1));
            Assert.That(oldProvider.Text.TakeCalls, Is.Zero);
            Assert.That(oldModules.ContainsKey(module.ModuleName), Is.False);
            Assert.That(oldStarting.ContainsKey(module.GetType()), Is.False);
            Assert.That(oldStarting.Values, Has.No.Member(old));
            var oldException = old.Exception;
            var oldError = old.Error;

            yield return new EnterPlayMode(false);
            var currentManager = managerInstance.GetValue(null);
            var currentModules = ModuleManager.AllModules;
            var currentStarting = (IDictionary<Type, StartUpModuleRequest>)startingField.GetValue(currentManager);
            Assert.That(currentManager, Is.Not.SameAs(oldManager));
            Assert.That(currentModules, Is.Not.SameAs(oldModules));
            Assert.That(currentStarting, Is.Not.SameAs(oldStarting));
            Assert.That(ModuleManager.GetModule(module.ModuleName), Is.Null);
            Assert.That(currentStarting.ContainsKey(module.GetType()), Is.False);
            Assert.That(currentStarting.Values, Has.No.Member(old));
            Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(module.StartCount, Is.Zero, "A new Play must not automatically restart the old module.");
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            CoreResourceAccess.Bind(currentProvider);
            var current = ModuleManager.StartUpModule(module);
            var currentTask = current.AsTask();
            int currentCallbacks = 0;
            current.AddCompleteEvent(_ => currentCallbacks++);
            Assert.That(current, Is.Not.SameAs(old));
            Assert.That(current.IsDone, Is.False);
            Assert.That(currentTask.IsCompleted, Is.False);
            Assert.That(module.State, Is.EqualTo(ModuleState.Starting));
            Assert.That(currentProvider.TextCalls, Is.EqualTo(1));
            Assert.That(currentStarting[module.GetType()], Is.SameAs(current));

            // 直接推进已捕获的真实旧 producer；不能另造 StartUp() 迭代器冒充旧路径。
            Assert.That(oldRoutine.MoveNext(), Is.False);
            oldProvider.Text.Succeed("[]");
            yield return null;
            Assert.That(old.Exception, Is.SameAs(oldException));
            Assert.That(old.Error, Is.EqualTo(oldError));
            Assert.That(old.AsTask(), Is.SameAs(oldTask));
            Assert.That(oldTask.GetAwaiter().GetResult(), Is.SameAs(old));
            Assert.That(oldCallbacks, Is.EqualTo(1));
            Assert.That(oldProvider.Text.DisposeCalls, Is.EqualTo(1));
            Assert.That(oldProvider.Text.TakeCalls, Is.Zero);
            Assert.That(current.IsDone, Is.False);
            Assert.That(currentTask.IsCompleted, Is.False);
            Assert.That(currentCallbacks, Is.Zero);
            Assert.That(module.StartCount, Is.Zero);
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(currentModules[module.ModuleName], Is.SameAs(module));
            Assert.That(currentStarting[module.GetType()], Is.SameAs(current));
            Assert.That(oldModules.ContainsKey(module.ModuleName), Is.False);
            Assert.That(oldStarting.ContainsKey(module.GetType()), Is.False);

            currentProvider.Text.Succeed("[]");
            var deadline = EditorApplication.timeSinceStartup + 10;
            while (!current.IsDone && EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(current.IsDone, Is.True, "Explicit restart must finish through the new provider's real startup coroutine.");
            Assert.That(current.Exception, Is.Null, current.Error);
            Assert.That(current.Error, Is.Null.Or.Empty);
            Assert.That(currentTask.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(currentTask.GetAwaiter().GetResult(), Is.SameAs(current));
            Assert.That(current.AsTask(), Is.SameAs(currentTask));
            Assert.That(currentCallbacks, Is.EqualTo(1));
            Assert.That(module.State, Is.EqualTo(ModuleState.Running));
            Assert.That(module.StartCount, Is.EqualTo(1));
            Assert.That(currentProvider.Text.TakeCalls, Is.EqualTo(1));
            Assert.That(currentProvider.Text.DisposeCalls, Is.EqualTo(1));
            Assert.That(currentProvider.Text.IsDisposed, Is.True);
            Assert.That(currentStarting.ContainsKey(module.GetType()), Is.False);
            Assert.That(old.Exception, Is.SameAs(oldException));
            Assert.That(old.Error, Is.EqualTo(oldError));
            Assert.That(oldCallbacks, Is.EqualTo(1));
            module.Close();
            Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(module.CloseCount, Is.EqualTo(2));
            Assert.That(ModuleManager.GetModule(module.ModuleName), Is.Null);
            yield return new ExitPlayMode();
            Assert.That(old.Exception, Is.SameAs(oldException));
            Assert.That(oldCallbacks, Is.EqualTo(1));
            Assert.That(currentCallbacks, Is.EqualTo(1));
            Assert.That(oldProvider.Text.DisposeCalls, Is.EqualTo(1));
            Assert.That(currentProvider.Text.DisposeCalls, Is.EqualTo(1));
            Assert.That(module.CloseCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator DeactivatedOwnedCoroutineHostInvalidatesStartupAndRequiresExplicitReactivation()
            => AssertOwnedCoroutineHostLoss(false);

        [UnityTest]
        public IEnumerator DestroyedOwnedCoroutineHostInvalidatesStartupAndAllowsExplicitRestart()
            => AssertOwnedCoroutineHostLoss(true);

        [UnityTest]
        public IEnumerator DisabledComponentThenDeactivatedGameObjectStillInvalidatesStartup()
            => AssertOwnedCoroutineHostLoss(false, true);

        private IEnumerator AssertOwnedCoroutineHostLoss(bool destroy, bool disableComponentFirst = false)
        {
            var oldProvider = new PendingStartupProvider();
            var inactiveProvider = new PendingStartupProvider();
            var currentProvider = new PendingStartupProvider();
            PrepareOwnedStartupSession(oldProvider, inactiveProvider, currentProvider);
            yield return new EnterPlayMode(false);
            AssertFreshOwnedStartupPlay();
            CoreResourceAccess.Bind(oldProvider);
            var module = new PendingStartupModule();
            var old = ModuleManager.StartUpModule(module);
            var oldTask = old.AsTask();
            int oldCallbacks = 0;
            old.AddCompleteEvent(_ => oldCallbacks++);
            Assert.That(old.IsDone, Is.False);
            Assert.That(oldTask.IsCompleted, Is.False);
            Assert.That(oldProvider.TextCalls, Is.EqualTo(1));
            Assert.That(module.State, Is.EqualTo(ModuleState.Starting));
            var host = OnlyOwnedCoroutineHost();
            var hostObject = host.gameObject;
            Assert.That(hostObject.activeInHierarchy, Is.True);
            if (disableComponentFirst)
            {
                host.enabled = false;
                yield return null;
                Assert.That(old.IsDone, Is.False, "Disabling the component alone cannot cancel startup.");
                Assert.That(oldProvider.Text.DisposeCalls, Is.Zero);
            }
            if (destroy)
            {
                UnityEngine.Object.Destroy(hostObject);
                var deadline = EditorApplication.timeSinceStartup + 10;
                while (host && EditorApplication.timeSinceStartup < deadline)
                {
                    yield return null;
                }
                Assert.That(host == null && hostObject == null, Is.True, "Wait for the owned host's actual deferred destruction.");
                Assert.That(FindCoroutineHosts(), Is.Empty);
            }
            else
            {
                hostObject.SetActive(false);
                yield return null;
                Assert.That(OnlyOwnedCoroutineHost(), Is.SameAs(host), "Deactivation must not silently replace the existing host.");
                Assert.That(hostObject.activeInHierarchy, Is.False);
            }

            AssertTerminalStartup(old, oldTask);
            Assert.That(old.Exception, Is.TypeOf<InvalidOperationException>());
            Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(module.StartCount, Is.Zero);
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(oldCallbacks, Is.EqualTo(1));
            Assert.That(oldProvider.Text.IsDisposed, Is.True);
            Assert.That(oldProvider.Text.DisposeCalls, Is.EqualTo(1));
            Assert.That(oldProvider.Text.TakeCalls, Is.Zero);
            AssertStartupDetached(old);
            var oldException = old.Exception;
            var oldError = old.Error;

            if (!destroy)
            {
                CoreResourceAccess.Bind(inactiveProvider);
                var rejected = ModuleManager.StartUpModule(module);
                var rejectedTask = rejected.AsTask();
                int rejectedCallbacks = 0;
                rejected.AddCompleteEvent(_ => rejectedCallbacks++);
                Assert.That(rejected, Is.Not.SameAs(old));
                AssertTerminalStartup(rejected, rejectedTask);
                Assert.That(rejected.Exception, Is.TypeOf<InvalidOperationException>());
                Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
                Assert.That(module.CloseCount, Is.EqualTo(2));
                Assert.That(inactiveProvider.TextCalls, Is.Zero, "Start returning null must not execute the startup iterator.");
                Assert.That(inactiveProvider.Text.DisposeCalls, Is.Zero, "Core never acquired this provider's consumer request.");
                Assert.That(rejectedCallbacks, Is.EqualTo(1));
                AssertStartupDetached(rejected);
                Assert.That(OnlyOwnedCoroutineHost(), Is.SameAs(host));
                Assert.That(hostObject.activeInHierarchy, Is.False);
                hostObject.SetActive(true);
                yield return null;
                Assert.That(OnlyOwnedCoroutineHost(), Is.SameAs(host));
                Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
                Assert.That(module.StartCount, Is.Zero, "Reactivation cannot revive the stopped startup or retry it automatically.");
                Assert.That(module.CloseCount, Is.EqualTo(2));
                Assert.That(rejectedCallbacks, Is.EqualTo(1));
                Assert.That(rejected.Exception, Is.TypeOf<InvalidOperationException>());
            }

            CoreResourceAccess.Bind(currentProvider);
            var current = ModuleManager.StartUpModule(module);
            var currentTask = current.AsTask();
            int currentCallbacks = 0;
            current.AddCompleteEvent(_ => currentCallbacks++);
            Assert.That(current, Is.Not.SameAs(old));
            Assert.That(current.IsDone, Is.False);
            Assert.That(currentTask.IsCompleted, Is.False);
            Assert.That(currentProvider.TextCalls, Is.EqualTo(1));
            var currentHost = OnlyOwnedCoroutineHost();
            Assert.That(ReferenceEquals(currentHost, host), Is.EqualTo(!destroy));
            Assert.That(currentHost.gameObject.activeInHierarchy, Is.True);
            oldProvider.Text.Succeed("[]");
            yield return null;
            Assert.That(old.Exception, Is.SameAs(oldException));
            Assert.That(old.Error, Is.EqualTo(oldError));
            Assert.That(oldCallbacks, Is.EqualTo(1));
            Assert.That(oldProvider.Text.TakeCalls, Is.Zero);
            Assert.That(oldProvider.Text.DisposeCalls, Is.EqualTo(1));
            Assert.That(current.IsDone, Is.False);
            Assert.That(currentTask.IsCompleted, Is.False);
            Assert.That(currentCallbacks, Is.Zero);
            Assert.That(module.State, Is.EqualTo(ModuleState.Starting));
            Assert.That(module.StartCount, Is.Zero, "A late result from the stopped startup cannot call OnStart.");
            Assert.That(module.CloseCount, Is.EqualTo(destroy ? 1 : 2));
            Assert.That(ModuleManager.GetModule(module.ModuleName), Is.SameAs(module));

            currentProvider.Text.Succeed("[]");
            yield return AwaitOwnedStartup(current);
            AssertTerminalStartup(current, currentTask);
            Assert.That(current.Exception, Is.Null, current.Error);
            Assert.That(current.Error, Is.Null.Or.Empty);
            Assert.That(module.State, Is.EqualTo(ModuleState.Running));
            Assert.That(module.StartCount, Is.EqualTo(1));
            Assert.That(currentCallbacks, Is.EqualTo(1));
            Assert.That(currentProvider.Text.TakeCalls, Is.EqualTo(1));
            Assert.That(currentProvider.Text.DisposeCalls, Is.EqualTo(1));
            Assert.That(old.Exception, Is.SameAs(oldException));
            Assert.That(oldCallbacks, Is.EqualTo(1));
            module.Close();
            Assert.That(module.CloseCount, Is.EqualTo(destroy ? 2 : 3));
            Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
            AssertStartupDetached(current);
            yield return new ExitPlayMode();
            Assert.That(oldCallbacks, Is.EqualTo(1));
            Assert.That(currentCallbacks, Is.EqualTo(1));
            Assert.That(oldProvider.Text.DisposeCalls, Is.EqualTo(1));
            Assert.That(currentProvider.Text.DisposeCalls, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator DisabledOwnedCoroutineHostComponentKeepsStartupPendingAndCanComplete()
        {
            var provider = new PendingStartupProvider();
            PrepareOwnedStartupSession(provider);
            yield return new EnterPlayMode(false);
            AssertFreshOwnedStartupPlay();
            CoreResourceAccess.Bind(provider);
            var module = new PendingStartupModule();
            var request = ModuleManager.StartUpModule(module);
            var task = request.AsTask();
            int callbacks = 0;
            request.AddCompleteEvent(_ => callbacks++);
            var host = OnlyOwnedCoroutineHost();
            Assert.That(provider.TextCalls, Is.EqualTo(1));
            Assert.That(host.enabled, Is.True);
            host.enabled = false;
            yield return null;
            Assert.That(host.gameObject.activeInHierarchy, Is.True);
            Assert.That(OnlyOwnedCoroutineHost(), Is.SameAs(host));
            Assert.That(request.IsDone, Is.False, "Disabling only the MonoBehaviour does not stop its Unity coroutine.");
            Assert.That(task.IsCompleted, Is.False);
            Assert.That(module.State, Is.EqualTo(ModuleState.Starting));
            Assert.That(module.CloseCount, Is.Zero);
            Assert.That(callbacks, Is.Zero);
            Assert.That(provider.Text.Status, Is.EqualTo(CoreResourceStatus.Pending));
            Assert.That(provider.Text.CancelCalls, Is.Zero);
            Assert.That(provider.Text.DisposeCalls, Is.Zero);
            provider.Text.Succeed("[]");
            yield return AwaitOwnedStartup(request);
            AssertTerminalStartup(request, task);
            Assert.That(request.Exception, Is.Null, request.Error);
            Assert.That(request.Error, Is.Null.Or.Empty);
            Assert.That(module.State, Is.EqualTo(ModuleState.Running));
            Assert.That(module.StartCount, Is.EqualTo(1));
            Assert.That(module.CloseCount, Is.Zero);
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(provider.Text.TakeCalls, Is.EqualTo(1));
            Assert.That(provider.Text.DisposeCalls, Is.EqualTo(1));
            Assert.That(host.enabled, Is.False);
            module.Close();
            Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(module.CloseCount, Is.EqualTo(1));
            AssertStartupDetached(request);
            yield return new ExitPlayMode();
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(provider.Text.DisposeCalls, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator OnStartDeactivatingOwnedCoroutineHostDefersRollbackUntilHookReturns()
        {
            var provider = new PendingStartupProvider();
            PrepareOwnedStartupSession(provider);
            yield return new EnterPlayMode(false);
            AssertFreshOwnedStartupPlay();
            CoreResourceAccess.Bind(provider);
            var module = new PendingStartupModule();
            var request = ModuleManager.StartUpModule(module);
            var task = request.AsTask();
            int callbacks = 0;
            request.AddCompleteEvent(_ => callbacks++);
            var host = OnlyOwnedCoroutineHost();
            bool hookEntered = false;
            bool hookReachedEnd = false;
            bool waitingInsideHook = false;
            bool registeredInsideHook = false;
            ModuleState stateBeforeDeactivation = ModuleState.Created;
            ModuleState stateAfterDeactivation = ModuleState.Created;
            int closesBeforeDeactivation = -1;
            int closesAfterDeactivation = -1;
            int callbacksInsideHook = -1;
            module.StartAction = () =>
            {
                hookEntered = true;
                stateBeforeDeactivation = module.State;
                closesBeforeDeactivation = module.CloseCount;
                host.gameObject.SetActive(false);
                stateAfterDeactivation = module.State;
                closesAfterDeactivation = module.CloseCount;
                waitingInsideHook = !request.IsDone && !task.IsCompleted;
                registeredInsideHook = ReferenceEquals(ModuleManager.GetModule(module.ModuleName), module);
                callbacksInsideHook = callbacks;
                hookReachedEnd = true;
            };
            provider.Text.Succeed("[]");
            yield return AwaitOwnedStartup(request);
            Assert.That(hookEntered && hookReachedEnd, Is.True, "The hook must finish normally, not throw to trigger rollback.");
            Assert.That(stateBeforeDeactivation, Is.EqualTo(ModuleState.Starting));
            Assert.That(stateAfterDeactivation, Is.EqualTo(ModuleState.Starting), "Host loss must not reenter Close inside OnStart.");
            Assert.That(closesBeforeDeactivation, Is.Zero);
            Assert.That(closesAfterDeactivation, Is.Zero);
            Assert.That(waitingInsideHook, Is.True);
            Assert.That(registeredInsideHook, Is.True);
            Assert.That(callbacksInsideHook, Is.Zero);
            AssertTerminalStartup(request, task);
            Assert.That(request.Exception, Is.TypeOf<InvalidOperationException>());
            Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(module.StartCount, Is.EqualTo(1));
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(provider.Text.IsDisposed, Is.True);
            Assert.That(provider.Text.TakeCalls, Is.EqualTo(1));
            Assert.That(provider.Text.DisposeCalls, Is.EqualTo(1));
            AssertStartupDetached(request);
            Assert.That(OnlyOwnedCoroutineHost(), Is.SameAs(host));
            Assert.That(host.gameObject.activeInHierarchy, Is.False);
            var exception = request.Exception;
            provider.Text.Succeed("[]");
            yield return null;
            Assert.That(request.Exception, Is.SameAs(exception));
            Assert.That(module.StartCount, Is.EqualTo(1));
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(provider.Text.DisposeCalls, Is.EqualTo(1));
            yield return new ExitPlayMode();
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(module.CloseCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator SceneInputDisposalInvalidationPreventsStartingANewBusinessHook()
        {
            var provider = new PendingStartupProvider();
            PrepareOwnedStartupSession(provider);
            yield return new EnterPlayMode(false);
            AssertFreshOwnedStartupPlay();
            CoreResourceAccess.Bind(provider);
            var module = new PendingStartupModule { UseScene = true };
            var request = ModuleManager.StartUpModule(module);
            var task = request.AsTask();
            var scene = SceneManager.GetActiveScene();
            var host = OnlyOwnedCoroutineHost();
            bool disposalReachedEnd = false;
            bool deferredInsideDisposal = false;
            int callbacks = 0;
            request.AddCompleteEvent(_ => callbacks++);
            provider.Scene.DisposeAction = () =>
            {
                host.gameObject.SetActive(false);
                deferredInsideDisposal = !request.IsDone && module.State == ModuleState.Starting && module.CloseCount == 0;
                disposalReachedEnd = true;
            };
            Assert.That(request.Step, Is.EqualTo(StartingModuleSteps.LoadScene));
            Assert.That(provider.TextCalls, Is.Zero);
            Assert.That(provider.SceneCalls, Is.EqualTo(1));
            Assert.That(provider.Address, Is.EqualTo(PendingStartupModule.SceneAddress));
            Assert.That(provider.SceneMode, Is.EqualTo(LoadSceneMode.Single));
            provider.Scene.Succeed(new CoreSceneResult(scene, false));
            yield return AwaitOwnedStartup(request);
            Assert.That(disposalReachedEnd && deferredInsideDisposal, Is.True);
            AssertTerminalStartup(request, task);
            Assert.That(request.Exception, Is.TypeOf<InvalidOperationException>());
            Assert.That(module.StartCount, Is.Zero, "An interrupted startup cannot enter an OnStart hook that has not begun.");
            Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(provider.Scene.TakeCalls, Is.EqualTo(1));
            Assert.That(provider.Scene.DisposeCalls, Is.EqualTo(1));
            Assert.That(provider.Scene.IsDisposed, Is.True);
            Assert.That(scene.IsValid() && scene.isLoaded, Is.True, "CoreSceneResult does not grant scene unload ownership.");
            AssertStartupDetached(request);
            yield return new ExitPlayMode();
            Assert.That(callbacks, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ExternalModelInitializationHostLossDefersStartupRollbackUntilAddModelReturns()
        {
            var provider = new PendingStartupProvider();
            PrepareOwnedStartupSession(provider);
            yield return new EnterPlayMode(false);
            AssertFreshOwnedStartupPlay();
            CoreResourceAccess.Bind(provider);
            var module = new PendingStartupModule();
            var request = ModuleManager.StartUpModule(module);
            var task = request.AsTask();
            var host = OnlyOwnedCoroutineHost();
            var model = new HostLossProbeModel();
            int callbacks = 0;
            StartUpModuleRequest notification = null;
            Exception notificationException = null;
            bool notifiedInsideInit = false;
            bool cleanupBeforeNotification = false;
            request.AddCompleteEvent(done =>
            {
                callbacks++;
                notification = done;
                notificationException = done.Exception;
                notifiedInsideInit = model.IsInsideInit;
                cleanupBeforeNotification = model.InitReturned && module.State == ModuleState.Closed &&
                    module.CloseCount == 1 && model.ExitCount == 1 && model.Module == null &&
                    provider.Text.IsDisposed && provider.Text.DisposeCalls == 1 &&
                    ModuleManager.GetModule(module.ModuleName) == null;
            });
            bool waitingInsideInit = false;
            bool registeredInsideInit = false;
            bool ownedInsideInit = false;
            ModuleState stateInsideInit = ModuleState.Created;
            int closesInsideInit = -1;
            int exitsInsideInit = -1;
            int callbacksInsideInit = -1;
            Exception firstFailure = null;
            model.InitAction = () =>
            {
                host.gameObject.SetActive(false);
                waitingInsideInit = !request.IsDone && !task.IsCompleted;
                stateInsideInit = module.State;
                closesInsideInit = module.CloseCount;
                exitsInsideInit = model.ExitCount;
                callbacksInsideInit = callbacks;
                registeredInsideInit = ReferenceEquals(ModuleManager.GetModule(module.ModuleName), module);
                ownedInsideInit = ReferenceEquals(model.Module, module) &&
                    ReferenceEquals(module.GetModel<HostLossProbeModel>(model.Id), model);
                firstFailure = request.Exception;
            };
            Assert.That(request.IsDone, Is.False);
            Assert.That(task.IsCompleted, Is.False);
            Assert.That(module.State, Is.EqualTo(ModuleState.Starting));
            Assert.That(provider.TextCalls, Is.EqualTo(1));
            Assert.That(provider.Text.Status, Is.EqualTo(CoreResourceStatus.Pending));
            Assert.That(provider.Text.DisposeCalls, Is.Zero);

            // This external transaction runs while the actual startup producer is suspended on its text input.
            module.AddModel(model);
            Assert.That(model.InitReturned, Is.True, "AddModel must return normally, not throw to trigger rollback.");
            Assert.That(waitingInsideInit, Is.True, "Host loss cannot finish startup inside an external Model.OnInit stack.");
            Assert.That(stateInsideInit, Is.EqualTo(ModuleState.Starting));
            Assert.That(closesInsideInit, Is.Zero);
            Assert.That(exitsInsideInit, Is.Zero);
            Assert.That(callbacksInsideInit, Is.Zero);
            Assert.That(registeredInsideInit && ownedInsideInit, Is.True);
            AssertTerminalStartup(request, task);
            Assert.That(request.Exception, Is.TypeOf<InvalidOperationException>());
            Assert.That(request.Exception, Is.SameAs(firstFailure));
            Assert.That(notificationException, Is.SameAs(firstFailure));
            Assert.That(module.State, Is.EqualTo(ModuleState.Closed), "Rollback must complete before the external AddModel returns.");
            Assert.That(module.StartCount, Is.Zero);
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(model.InitCount, Is.EqualTo(1));
            Assert.That(model.ExitCount, Is.EqualTo(1));
            Assert.That(model.ExitedInsideInit, Is.False);
            Assert.That(model.OwnerAtExit, Is.SameAs(module));
            Assert.That(model.Module, Is.Null);
            Assert.That(module.GetModel<HostLossProbeModel>(model.Id), Is.Null);
            Assert.That(module.GetModels<HostLossProbeModel>(), Is.Empty);
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(notification, Is.SameAs(request));
            Assert.That(notifiedInsideInit, Is.False);
            Assert.That(cleanupBeforeNotification, Is.True);
            Assert.That(provider.Text.IsDisposed, Is.True);
            Assert.That(provider.Text.DisposeCalls, Is.EqualTo(1));
            Assert.That(provider.Text.TakeCalls, Is.Zero);
            AssertStartupDetached(request);
            Assert.That(OnlyOwnedCoroutineHost(), Is.SameAs(host));
            Assert.That(host.gameObject.activeInHierarchy, Is.False);
            var error = request.Error;
            provider.Text.Succeed("[]");
            yield return null;
            Assert.That(request.Exception, Is.SameAs(firstFailure));
            Assert.That(request.Error, Is.EqualTo(error));
            Assert.That(task.GetAwaiter().GetResult(), Is.SameAs(request));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(module.StartCount, Is.Zero);
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(model.ExitCount, Is.EqualTo(1));
            Assert.That(model.Module, Is.Null);
            Assert.That(provider.Text.DisposeCalls, Is.EqualTo(1));
            Assert.That(provider.Text.TakeCalls, Is.Zero);
            AssertStartupDetached(request);
            yield return new ExitPlayMode();
            Assert.That(request.Exception, Is.SameAs(firstFailure));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(model.ExitCount, Is.EqualTo(1));
            Assert.That(provider.Text.DisposeCalls, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ExternalControllerInitializationHostLossDefersStartupRollbackUntilAddControllerReturns()
        {
            var provider = new PendingStartupProvider();
            PrepareOwnedStartupSession(provider);
            yield return new EnterPlayMode(false);
            AssertFreshOwnedStartupPlay();
            CoreResourceAccess.Bind(provider);
            var module = new PendingStartupModule();
            var request = ModuleManager.StartUpModule(module);
            var task = request.AsTask();
            var host = OnlyOwnedCoroutineHost();
            var controller = new HostLossProbeController();
            int callbacks = 0;
            StartUpModuleRequest notification = null;
            Exception notificationException = null;
            bool notifiedInsideInit = false;
            bool cleanupBeforeNotification = false;
            request.AddCompleteEvent(done =>
            {
                callbacks++;
                notification = done;
                notificationException = done.Exception;
                notifiedInsideInit = controller.IsInsideInit;
                cleanupBeforeNotification = controller.InitReturned && module.State == ModuleState.Closed &&
                    module.CloseCount == 1 && controller.ExitCount == 1 && controller.Module == null &&
                    provider.Text.IsDisposed && provider.Text.DisposeCalls == 1 &&
                    ModuleManager.GetModule(module.ModuleName) == null;
            });
            bool waitingInsideInit = false;
            bool ownedInsideInit = false;
            ModuleState stateInsideInit = ModuleState.Created;
            int closesInsideInit = -1;
            int exitsInsideInit = -1;
            int callbacksInsideInit = -1;
            Exception firstFailure = null;
            controller.InitAction = () =>
            {
                host.gameObject.SetActive(false);
                waitingInsideInit = !request.IsDone && !task.IsCompleted;
                stateInsideInit = module.State;
                closesInsideInit = module.CloseCount;
                exitsInsideInit = controller.ExitCount;
                callbacksInsideInit = callbacks;
                ownedInsideInit = ReferenceEquals(controller.Module, module) &&
                    ReferenceEquals(module.GetController<HostLossProbeController>(), controller) &&
                    ReferenceEquals(ModuleManager.GetModule(module.ModuleName), module);
                firstFailure = request.Exception;
            };
            Assert.That(request.IsDone, Is.False);
            Assert.That(task.IsCompleted, Is.False);
            Assert.That(provider.TextCalls, Is.EqualTo(1));
            Assert.That(provider.Text.Status, Is.EqualTo(CoreResourceStatus.Pending));
            // AddController adopts this non-pooled object only for the duration of its registration.
            module.AddController(controller);
            Assert.That(controller.InitReturned, Is.True);
            Assert.That(waitingInsideInit, Is.True, "Host loss cannot finish startup inside an external Controller.OnInit stack.");
            Assert.That(stateInsideInit, Is.EqualTo(ModuleState.Starting));
            Assert.That(closesInsideInit, Is.Zero);
            Assert.That(exitsInsideInit, Is.Zero);
            Assert.That(callbacksInsideInit, Is.Zero);
            Assert.That(ownedInsideInit, Is.True);
            AssertTerminalStartup(request, task);
            Assert.That(request.Exception, Is.TypeOf<InvalidOperationException>());
            Assert.That(request.Exception, Is.SameAs(firstFailure));
            Assert.That(notificationException, Is.SameAs(firstFailure));
            Assert.That(module.State, Is.EqualTo(ModuleState.Closed), "Rollback must finish before AddController returns.");
            Assert.That(module.StartCount, Is.Zero);
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(controller.InitCount, Is.EqualTo(1));
            Assert.That(controller.ExitCount, Is.EqualTo(1));
            Assert.That(controller.ExitedInsideInit, Is.False);
            Assert.That(controller.OwnerAtExit, Is.SameAs(module));
            Assert.That(controller.Module, Is.Null);
            Assert.That(module.GetController<HostLossProbeController>(), Is.Null);
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(notification, Is.SameAs(request));
            Assert.That(notifiedInsideInit, Is.False);
            Assert.That(cleanupBeforeNotification, Is.True);
            Assert.That(provider.Text.IsDisposed, Is.True);
            Assert.That(provider.Text.DisposeCalls, Is.EqualTo(1));
            Assert.That(provider.Text.TakeCalls, Is.Zero);
            AssertStartupDetached(request);
            var error = request.Error;
            provider.Text.Succeed("[]");
            yield return null;
            Assert.That(request.Exception, Is.SameAs(firstFailure));
            Assert.That(request.Error, Is.EqualTo(error));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(module.StartCount, Is.Zero);
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(controller.ExitCount, Is.EqualTo(1));
            Assert.That(controller.Module, Is.Null);
            Assert.That(provider.Text.DisposeCalls, Is.EqualTo(1));
            AssertStartupDetached(request);
            yield return new ExitPlayMode();
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(controller.ExitCount, Is.EqualTo(1));
            Assert.That(module.CloseCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ExternalViewOnLoadedHostLossDefersStartupRollbackUntilAttachViewReturns()
        {
            var provider = new PendingStartupProvider();
            PrepareOwnedStartupSession(provider);
            yield return new EnterPlayMode(false);
            AssertFreshOwnedStartupPlay();
            Assert.That(YLCoreSmokeView.LoadingProbe, Is.Null);
            Assert.That(YLCoreSmokeView.ArgsProbe, Is.Null);
            Assert.That(YLCoreSmokeView.EnabledProbe, Is.Null);
            Assert.That(YLCoreSmokeView.DisabledProbe, Is.Null);
            Assert.That(YLCoreSmokeView.SharedEventHandler, Is.Null);
            Assert.That(YLCoreSmokeView.DuplicateProbeHandler, Is.False);
            Assert.That(YLCoreSmokeView.SignatureConflictEvent, Is.Null);
            CoreResourceAccess.Bind(provider);
            var module = new PendingStartupModule();
            var request = ModuleManager.StartUpModule(module);
            var task = request.AsTask();
            var host = OnlyOwnedCoroutineHost();
            var instance = new GameObject("YL.CoreSession.ExternalViewHostLoss");
            instance.SetActive(false);
            YLCoreSmokeView view = null;
            try
            {
                view = instance.AddComponent<YLCoreSmokeView>();
                int id = view.Id;
                int callbacks = 0;
                StartUpModuleRequest notification = null;
                Exception notificationException = null;
                bool insideLoadedAction = false;
                bool cleanupBeforeNotification = false;
                request.AddCompleteEvent(done =>
                {
                    callbacks++;
                    notification = done;
                    notificationException = done.Exception;
                    cleanupBeforeNotification = !insideLoadedAction && view.ArgsCount == 1 && view.RemovedCount == 1 &&
                        view.Module == null && !view.IsLoaded && module.State == ModuleState.Closed &&
                        module.CloseCount == 1 && provider.Text.IsDisposed && provider.Text.DisposeCalls == 1 &&
                        ModuleManager.GetModule(module.ModuleName) == null;
                });
                bool waitingInsideLoaded = false;
                bool ownedInsideLoaded = false;
                ModuleState stateInsideLoaded = ModuleState.Created;
                int closesInsideLoaded = -1;
                int removesInsideLoaded = -1;
                int callbacksInsideLoaded = -1;
                Exception firstFailure = null;
                view.LoadedAction = () =>
                {
                    insideLoadedAction = true;
                    host.gameObject.SetActive(false);
                    waitingInsideLoaded = !request.IsDone && !task.IsCompleted;
                    stateInsideLoaded = module.State;
                    closesInsideLoaded = module.CloseCount;
                    removesInsideLoaded = view.RemovedCount;
                    callbacksInsideLoaded = callbacks;
                    ownedInsideLoaded = ReferenceEquals(view.Module, module) &&
                        ReferenceEquals(module.GetView<YLCoreSmokeView>(id), view) &&
                        ReferenceEquals(ModuleManager.GetModule(module.ModuleName), module);
                    firstFailure = request.Exception;
                    insideLoadedAction = false;
                };
                bool removedInsideLoaded = false;
                Module ownerAtRemove = null;
                view.RemoveAction = () =>
                {
                    removedInsideLoaded |= insideLoadedAction || view.ArgsCount != 1;
                    ownerAtRemove = view.Module;
                };
                Assert.That(request.IsDone, Is.False);
                Assert.That(task.IsCompleted, Is.False);
                Assert.That(provider.TextCalls, Is.EqualTo(1));
                Assert.That(provider.Text.Status, Is.EqualTo(CoreResourceStatus.Pending));
                // Attach borrows this inactive scene object; its internally created args stay owned by the call.
                var attached = module.AttachView(view);
                Assert.That(attached, Is.SameAs(view));
                Assert.That(waitingInsideLoaded, Is.True, "Host loss cannot finish startup inside an external View.OnLoaded stack.");
                Assert.That(stateInsideLoaded, Is.EqualTo(ModuleState.Starting));
                Assert.That(closesInsideLoaded, Is.Zero);
                Assert.That(removesInsideLoaded, Is.Zero);
                Assert.That(callbacksInsideLoaded, Is.Zero);
                Assert.That(ownedInsideLoaded, Is.True);
                AssertTerminalStartup(request, task);
                Assert.That(request.Exception, Is.TypeOf<InvalidOperationException>());
                Assert.That(request.Exception, Is.SameAs(firstFailure));
                Assert.That(notificationException, Is.SameAs(firstFailure));
                Assert.That(module.State, Is.EqualTo(ModuleState.Closed), "Rollback must finish before AttachView returns.");
                Assert.That(module.StartCount, Is.Zero);
                Assert.That(module.CloseCount, Is.EqualTo(1));
                Assert.That(view.LoadedCount, Is.EqualTo(1));
                Assert.That(view.ArgsCount, Is.EqualTo(1), "The complete OnLoaded body must return before removal.");
                Assert.That(view.RemovedCount, Is.EqualTo(1));
                Assert.That(removedInsideLoaded, Is.False);
                Assert.That(ownerAtRemove, Is.SameAs(module));
                Assert.That(view.Module, Is.Null);
                Assert.That(view.IsLoaded, Is.False);
                Assert.That(view.EnabledCount, Is.Zero, "Attach must not activate the caller's inactive object.");
                Assert.That(module.GetView<YLCoreSmokeView>(id), Is.Null);
                Assert.That(module.GetViews<YLCoreSmokeView>(), Is.Empty);
                Assert.That(instance != null && view != null, Is.True, "Core does not own destruction of the attached GameObject.");
                Assert.That(instance.activeSelf, Is.False);
                Assert.That(callbacks, Is.EqualTo(1));
                Assert.That(notification, Is.SameAs(request));
                Assert.That(cleanupBeforeNotification, Is.True);
                Assert.That(provider.Text.IsDisposed, Is.True);
                Assert.That(provider.Text.DisposeCalls, Is.EqualTo(1));
                Assert.That(provider.Text.TakeCalls, Is.Zero);
                AssertStartupDetached(request);
                var error = request.Error;
                provider.Text.Succeed("[]");
                yield return null;
                Assert.That(request.Exception, Is.SameAs(firstFailure));
                Assert.That(request.Error, Is.EqualTo(error));
                Assert.That(callbacks, Is.EqualTo(1));
                Assert.That(module.StartCount, Is.Zero);
                Assert.That(module.CloseCount, Is.EqualTo(1));
                Assert.That(view.LoadedCount, Is.EqualTo(1));
                Assert.That(view.RemovedCount, Is.EqualTo(1));
                Assert.That(view.Module, Is.Null);
                Assert.That(provider.Text.DisposeCalls, Is.EqualTo(1));
                AssertStartupDetached(request);
            }
            finally
            {
                if (view)
                {
                    view.LoadedAction = null;
                    view.RemoveAction = null;
                }
                if (instance)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
            Assert.That(instance == null, Is.True);
            Assert.That(module.CloseCount, Is.EqualTo(1));
            yield return new ExitPlayMode();
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(provider.Text.DisposeCalls, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ExternalModelInitializationFailureAfterHostLossPreservesOriginalErrorAndDefersNestedExit()
        {
            var provider = new PendingStartupProvider();
            PrepareOwnedStartupSession(provider);
            yield return new EnterPlayMode(false);
            AssertFreshOwnedStartupPlay();
            CoreResourceAccess.Bind(provider);
            var module = new PendingStartupModule();
            var request = ModuleManager.StartUpModule(module);
            var task = request.AsTask();
            var host = OnlyOwnedCoroutineHost();
            var model = new HostLossProbeModel();
            var initializationError = new ApplicationException("Owned Model initialization failed after host loss.");
            int callbacks = 0;
            bool cleanupBeforeNotification = false;
            request.AddCompleteEvent(_ =>
            {
                callbacks++;
                cleanupBeforeNotification = !model.IsInsideInit && model.ExitCount == 1 && model.Module == null &&
                    module.State == ModuleState.Closed && module.CloseCount == 1 &&
                    provider.Text.IsDisposed && provider.Text.DisposeCalls == 1;
            });
            bool waitingInsideInit = false;
            bool waitingInsideExit = false;
            bool ownedInsideExit = false;
            ModuleState stateInsideInit = ModuleState.Created;
            ModuleState stateInsideExit = ModuleState.Created;
            int closesInsideExit = -1;
            int disposesInsideExit = -1;
            int callbacksInsideExit = -1;
            Exception firstFailure = null;
            model.InitAction = () =>
            {
                host.gameObject.SetActive(false);
                waitingInsideInit = !request.IsDone && !task.IsCompleted;
                stateInsideInit = module.State;
                firstFailure = request.Exception;
                throw initializationError;
            };
            model.ExitAction = () =>
            {
                waitingInsideExit = !request.IsDone && !task.IsCompleted;
                stateInsideExit = module.State;
                closesInsideExit = module.CloseCount;
                disposesInsideExit = provider.Text.DisposeCalls;
                callbacksInsideExit = callbacks;
                ownedInsideExit = ReferenceEquals(model.Module, module) &&
                    ReferenceEquals(module.GetModel<HostLossProbeModel>(model.Id), model);
            };
            Assert.That(request.IsDone, Is.False);
            Assert.That(task.IsCompleted, Is.False);
            Assert.That(provider.Text.Status, Is.EqualTo(CoreResourceStatus.Pending));
            var surfaced = Assert.Throws<ApplicationException>(() => module.AddModel(model));
            Assert.That(surfaced, Is.SameAs(initializationError), "The external API must preserve the original initialization exception.");
            Assert.That(waitingInsideInit, Is.True);
            Assert.That(stateInsideInit, Is.EqualTo(ModuleState.Starting));
            Assert.That(waitingInsideExit, Is.True, "The nested catch-to-Exit rollback is still inside the external AddModel transaction.");
            Assert.That(stateInsideExit, Is.EqualTo(ModuleState.Starting));
            Assert.That(closesInsideExit, Is.Zero);
            Assert.That(disposesInsideExit, Is.Zero);
            Assert.That(callbacksInsideExit, Is.Zero);
            Assert.That(ownedInsideExit, Is.True);
            AssertTerminalStartup(request, task);
            Assert.That(request.Exception, Is.TypeOf<InvalidOperationException>());
            Assert.That(request.Exception, Is.SameAs(firstFailure));
            Assert.That(request.Exception, Is.Not.SameAs(initializationError));
            Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(module.StartCount, Is.Zero);
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(model.InitCount, Is.EqualTo(1));
            Assert.That(model.InitReturned, Is.False);
            Assert.That(model.ExitCount, Is.EqualTo(1));
            Assert.That(model.ExitedInsideInit, Is.False);
            Assert.That(model.OwnerAtExit, Is.SameAs(module));
            Assert.That(model.Module, Is.Null);
            Assert.That(module.GetModels<HostLossProbeModel>(), Is.Empty);
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(cleanupBeforeNotification, Is.True);
            Assert.That(provider.Text.DisposeCalls, Is.EqualTo(1));
            Assert.That(provider.Text.TakeCalls, Is.Zero);
            AssertStartupDetached(request);
            provider.Text.Succeed("[]");
            yield return null;
            Assert.That(request.Exception, Is.SameAs(firstFailure));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(model.ExitCount, Is.EqualTo(1));
            Assert.That(model.Module, Is.Null);
            Assert.That(module.StartCount, Is.Zero);
            Assert.That(module.CloseCount, Is.EqualTo(1));
            yield return new ExitPlayMode();
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(model.ExitCount, Is.EqualTo(1));
            Assert.That(provider.Text.DisposeCalls, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator WaitingViewAbortHostLossReturnsAllInputsBeforeStartupCompletion()
            => AssertWaitingPresentationAbortInputs(false);

        [UnityTest]
        public IEnumerator WaitingPanelAbortHostLossReturnsAllInputsBeforeStartupCompletion()
            => AssertWaitingPresentationAbortInputs(true);

        private IEnumerator AssertWaitingPresentationAbortInputs(bool panel)
        {
            var provider = new PendingStartupProvider();
            PrepareOwnedStartupSession(provider);
            yield return new EnterPlayMode(false);
            AssertFreshOwnedStartupPlay();
            CoreResourceAccess.Bind(provider);
            var module = new PendingStartupModule();
            var startup = ModuleManager.StartUpModule(module);
            var startupTask = startup.AsTask();
            var host = OnlyOwnedCoroutineHost();
            GameObject rootObject = null;
            Action abort = null;
            int startupCallbacks = 0;
            int childCallbacks = 0;
            try
            {
                UIRootContext root = null;
                if (panel)
                {
                    rootObject = new GameObject("YL.CoreSession.WaitingPanelAbortRoot", typeof(RectTransform),
                        typeof(Canvas), typeof(UnityEngine.UI.GraphicRaycaster));
                    rootObject.SetActive(false);
                    rootObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                    root = CorePanelTestRoots.Create(rootObject);
                }
                var args = LoadViewArgs.New();
                args.SetObject("retained", module);
                LoadViewRequest<YLCoreSmokeView> viewRequest = null;
                LoadPanelRequest<YLCoreSmokePanel> panelRequest = null;
                Task childTask;
                string address = panel ? "YL.CoreSession.WaitingAbortPanel" : "YL.CoreSession.WaitingAbortView";
                if (panel)
                {
                    panelRequest = module.LoadPanelAsync<YLCoreSmokePanel>(address, loadViewArgs: args, root: root);
                    childTask = panelRequest.AsTask();
                    abort = panelRequest.Abort;
                }
                else
                {
                    viewRequest = module.LoadViewAsync<YLCoreSmokeView>(address, loadViewArgs: args);
                    childTask = viewRequest.AsTask();
                    abort = viewRequest.Abort;
                }
                object childNotification = null;
                Exception argsAtChildNotification = null;
                Action<object> childCompleted = done =>
                {
                    childCallbacks++;
                    childNotification = done;
                    argsAtChildNotification = CaptureArgsReadError(args);
                };
                if (panel)
                {
                    panelRequest.AddCompleteEvent(done => childCompleted(done));
                }
                else
                {
                    viewRequest.AddCompleteEvent(done => childCompleted(done));
                }

                // Resource acquisition is asynchronous; only the pre-Abort handshake may advance frames.
                var deadline = EditorApplication.timeSinceStartup + 10;
                while (provider.PrefabCalls == 0 && !childTask.IsCompleted && EditorApplication.timeSinceStartup < deadline)
                {
                    yield return null;
                }
                Assert.That(provider.PrefabCalls, Is.EqualTo(1), "Abort must release an actually acquired pending prefab consumer.");
                Assert.That(provider.PrefabProject, Is.EqualTo(module.ProjectName));
                Assert.That(provider.PrefabAddress, Is.EqualTo(address));
                Assert.That(provider.Prefab.Status, Is.EqualTo(CoreResourceStatus.Pending));
                Assert.That(provider.Prefab.DisposeCalls, Is.Zero);
                Assert.That(provider.Prefab.TakeCalls, Is.Zero);
                Assert.That(provider.Text.Status, Is.EqualTo(CoreResourceStatus.Pending));
                Assert.That(provider.Text.DisposeCalls, Is.Zero);
                Assert.That(startup.IsDone, Is.False);
                Assert.That(startupTask.IsCompleted, Is.False);
                Assert.That(childTask.IsCompleted, Is.False);
                Assert.That(panel ? panelRequest.Status : viewRequest.Status, Is.EqualTo(LoadViewStatus.Pending));
                Assert.That(CaptureArgsReadError(args), Is.TypeOf<InvalidOperationException>(),
                    "The request must own its args, not return them before Abort.");
                Assert.That(module.GetViews<View>(), Is.Empty);
                Assert.That(UnityEngine.Object.FindObjectsByType<View>(FindObjectsInactive.Include, FindObjectsSortMode.None), Is.Empty,
                    "No prefab was supplied, so no View or Panel instance may exist.");
                Assert.That(OnlyOwnedCoroutineHost(), Is.SameAs(host));

                bool aborting = false;
                bool notifiedDuringAbort = false;
                bool allInputsBeforeNotification = false;
                bool closedBeforeNotification = false;
                bool disposeActionReturned = false;
                Exception argsAtStartupNotification = null;
                StartUpModuleRequest startupNotification = null;
                Exception notificationFailure = null;
                startup.AddCompleteEvent(done =>
                {
                    startupCallbacks++;
                    startupNotification = done;
                    notificationFailure = done.Exception;
                    notifiedDuringAbort = aborting;
                    argsAtStartupNotification = CaptureArgsReadError(args);
                    allInputsBeforeNotification = disposeActionReturned && provider.Text.IsDisposed && provider.Prefab.IsDisposed &&
                        provider.Text.DisposeCalls == 1 && provider.Prefab.DisposeCalls == 1;
                    closedBeforeNotification = module.State == ModuleState.Closed && module.CloseCount == 1 &&
                        ModuleManager.GetModule(module.ModuleName) == null;
                });
                int disposeCallbacks = 0;
                bool waitingInsideDispose = false;
                ModuleState stateInsideDispose = ModuleState.Created;
                int closesInsideDispose = -1;
                int notificationsInsideDispose = -1;
                Exception argsInsideDispose = null;
                Exception firstFailure = null;
                provider.Prefab.DisposeAction = () =>
                {
                    disposeCallbacks++;
                    host.gameObject.SetActive(false);
                    waitingInsideDispose = !startup.IsDone && !startupTask.IsCompleted;
                    stateInsideDispose = module.State;
                    closesInsideDispose = module.CloseCount;
                    notificationsInsideDispose = startupCallbacks;
                    argsInsideDispose = CaptureArgsReadError(args);
                    firstFailure = startup.Exception;
                    disposeActionReturned = true;
                };

                aborting = true;
                abort();
                aborting = false;
                Assert.That(disposeCallbacks, Is.EqualTo(1));
                Assert.That(waitingInsideDispose, Is.True, "Startup cannot complete inside Abort's prefab-input release stack.");
                Assert.That(stateInsideDispose, Is.EqualTo(ModuleState.Starting));
                Assert.That(closesInsideDispose, Is.Zero);
                Assert.That(notificationsInsideDispose, Is.Zero);
                Assert.That(argsInsideDispose, Is.TypeOf<InvalidOperationException>(),
                    "The still-owned args must remain unreturned while prefab Dispose is executing.");
                AssertTerminalStartup(startup, startupTask);
                Assert.That(startup.Exception, Is.TypeOf<InvalidOperationException>());
                Assert.That(startup.Exception, Is.SameAs(firstFailure));
                Assert.That(notificationFailure, Is.SameAs(firstFailure));
                Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
                Assert.That(module.StartCount, Is.Zero);
                Assert.That(module.CloseCount, Is.EqualTo(1));
                Assert.That(provider.Text.IsDisposed, Is.True);
                Assert.That(provider.Text.DisposeCalls, Is.EqualTo(1));
                Assert.That(provider.Text.TakeCalls, Is.Zero);
                Assert.That(provider.Prefab.IsDisposed, Is.True);
                Assert.That(provider.Prefab.DisposeCalls, Is.EqualTo(1));
                Assert.That(provider.Prefab.TakeCalls, Is.Zero);
                Assert.That(provider.Prefab.Status, Is.EqualTo(CoreResourceStatus.Canceled));
                Assert.That(CaptureArgsReadError(args), Is.TypeOf<ObjectDisposedException>(), "Abort must return its args synchronously.");
                Assert.That(argsAtStartupNotification, Is.TypeOf<ObjectDisposedException>(),
                    "All inputs, including args, must be returned before startup completion observers run.");
                Assert.That(startupCallbacks, Is.EqualTo(1));
                Assert.That(startupNotification, Is.SameAs(startup));
                Assert.That(notifiedDuringAbort, Is.True, "Transaction exit must settle startup before Abort returns, not on another frame.");
                Assert.That(allInputsBeforeNotification, Is.True);
                Assert.That(closedBeforeNotification, Is.True);
                Assert.That(childTask.Status, Is.EqualTo(TaskStatus.RanToCompletion));
                Assert.That(childCallbacks, Is.EqualTo(1));
                Assert.That(argsAtChildNotification, Is.TypeOf<ObjectDisposedException>());
                if (panel)
                {
                    Assert.That(panelRequest.IsDone, Is.True);
                    Assert.That(panelRequest.Status, Is.EqualTo(LoadViewStatus.Canceled));
                    Assert.That(panelRequest.Exception, Is.TypeOf<OperationCanceledException>());
                    Assert.That(panelRequest.Panel, Is.Null);
                    Assert.That(panelRequest.AsTask(), Is.SameAs(childTask));
                    Assert.That(panelRequest.AsTask().GetAwaiter().GetResult(), Is.SameAs(panelRequest));
                    Assert.That(childNotification, Is.SameAs(panelRequest));
                }
                else
                {
                    Assert.That(viewRequest.IsDone, Is.True);
                    Assert.That(viewRequest.Status, Is.EqualTo(LoadViewStatus.Canceled));
                    Assert.That(viewRequest.Exception, Is.TypeOf<OperationCanceledException>());
                    Assert.That(viewRequest.View, Is.Null);
                    Assert.That(viewRequest.AsTask(), Is.SameAs(childTask));
                    Assert.That(viewRequest.AsTask().GetAwaiter().GetResult(), Is.SameAs(viewRequest));
                    Assert.That(childNotification, Is.SameAs(viewRequest));
                }
                AssertStartupDetached(startup);
                Assert.That(module.GetViews<View>(), Is.Empty);
                Assert.That(UnityEngine.Object.FindObjectsByType<View>(FindObjectsInactive.Include, FindObjectsSortMode.None), Is.Empty);
                abort();
                Assert.That(startupCallbacks, Is.EqualTo(1));
                Assert.That(childCallbacks, Is.EqualTo(1));
                Assert.That(disposeCallbacks, Is.EqualTo(1));
                Assert.That(module.CloseCount, Is.EqualTo(1));
                Assert.That(provider.Prefab.DisposeCalls, Is.EqualTo(1));
            }
            finally
            {
                provider.Prefab.DisposeAction = null;
                try
                {
                    abort?.Invoke();
                }
                finally
                {
                    if (rootObject)
                    {
                        UnityEngine.Object.DestroyImmediate(rootObject);
                    }
                }
            }
            yield return new ExitPlayMode();
            Assert.That(startupCallbacks, Is.EqualTo(1));
            Assert.That(childCallbacks, Is.EqualTo(1));
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(provider.Text.DisposeCalls, Is.EqualTo(1));
            Assert.That(provider.Prefab.DisposeCalls, Is.EqualTo(1));
        }

        private static Exception CaptureArgsReadError(LoadViewArgs args)
        {
            // Observe the public owned/returned read gate; assert exact errors outside reentrant callbacks.
            try
            {
                args.GetObject("retained");
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        private void PrepareOwnedStartupSession(params PendingStartupProvider[] providers)
        {
            const string OwnedScene = "Assets/YLTestingProfile/Empty.unity";
            Assert.That(EditorApplication.isPlaying, Is.False);
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled &&
                (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0, Is.True,
                "The explicit owned session profile must prepare Domain Reload settings before discovery.");
            Assert.That(Enumerable.Range(0, SceneManager.sceneCount).All(index => !SceneManager.GetSceneAt(index).isDirty), Is.True,
                "Never save or discard dirty scene contents to run a session regression.");
            var preparation = SceneManager.GetSceneByPath(OwnedScene);
            Assert.That(preparation.IsValid() && preparation.isLoaded && preparation.rootCount == 0, Is.True,
                "Use the saved empty preparation scene already loaded by the owned session runner.");
            var providerSlot = typeof(CoreResourceAccess).GetField("_provider", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(providerSlot, Is.Not.Null);
            var previousProvider = providerSlot.GetValue(null);
            bool previousOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            var previousOptions = EditorSettings.enterPlayModeOptions;
            _previousScenes = EditorSceneManager.GetSceneManagerSetup();
            _restoreStartupSession = () =>
            {
                try
                {
                    foreach (var provider in providers)
                    {
                        if (!provider.Text.IsDisposed)
                        {
                            provider.Text.Dispose();
                        }
                        if (provider.SceneCalls != 0 && !provider.Scene.IsDisposed)
                        {
                            provider.Scene.Dispose();
                        }
                        if (provider.PrefabCalls != 0 && !provider.Prefab.IsDisposed)
                        {
                            provider.Prefab.Dispose();
                        }
                    }
                }
                finally
                {
                    foreach (var provider in providers)
                    {
                        CoreResourceAccess.Unbind(provider);
                    }
                    // Bind rejects EditMode; restore only the captured slot, never a later external binding.
                    if (providerSlot.GetValue(null) == null)
                    {
                        providerSlot.SetValue(null, previousProvider);
                    }
                    EditorSettings.enterPlayModeOptions = previousOptions;
                    EditorSettings.enterPlayModeOptionsEnabled = previousOptionsEnabled;
                }
            };
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorSceneManager.OpenScene(OwnedScene, OpenSceneMode.Single);
        }

        private static CoroutineStarter[] FindCoroutineHosts() => UnityEngine.Object.FindObjectsByType<CoroutineStarter>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        private static void AssertFreshOwnedStartupPlay()
        {
            Assert.That(EditorApplication.isPlaying, Is.True);
            Assert.That(CoreResourceAccess.Provider, Is.Null, "Never replace a foreign resource provider.");
            Assert.That(ModuleManager.AllModules, Is.Empty, "Never close or replace another fixture's modules.");
            Assert.That(ModuleManager.AllBaseModules, Is.Empty);
            Assert.That(FindCoroutineHosts(), Is.Empty, "Host lifecycle tests require a fresh Play without shared maintenance hosts.");
        }

        private static CoroutineStarter OnlyOwnedCoroutineHost()
        {
            var hosts = FindCoroutineHosts();
            Assert.That(hosts, Has.Length.EqualTo(1), "Only the sole host created after this test's startup may be controlled.");
            return hosts[0];
        }

        private static IEnumerator AwaitOwnedStartup(StartUpModuleRequest request)
        {
            var deadline = EditorApplication.timeSinceStartup + 10;
            while (!request.IsDone && EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(request.IsDone, Is.True, "The owned startup coroutine must reach a terminal result.");
        }

        private static void AssertTerminalStartup(StartUpModuleRequest request, Task<StartUpModuleRequest> task)
        {
            Assert.That(request.IsDone, Is.True, "Losing the real coroutine host must not strand an accepted startup request.");
            Assert.That(task.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(task.GetAwaiter().GetResult(), Is.SameAs(request));
            Assert.That(request.AsTask(), Is.SameAs(task));
            Assert.That(request.keepWaiting, Is.False);
            Assert.That(request.Progress, Is.EqualTo(1f));
        }

        private static void AssertStartupDetached(StartUpModuleRequest request)
        {
            Assert.That(ModuleManager.GetModule(request.Module.ModuleName), Is.Null);
            var manager = typeof(ModuleManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var starting = (IDictionary<Type, StartUpModuleRequest>)typeof(ModuleManager)
                .GetField("_startingModules", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            Assert.That(starting.ContainsKey(request.Module.GetType()), Is.False);
            Assert.That(starting.Values, Has.No.Member(request));
        }

        private sealed class PendingStartupModule : Module
        {
            internal const string TableAddress = "YL.CoreSession.PendingStartupTable";
            internal const string SceneAddress = "YL.CoreSession.PendingStartupScene";
            internal int StartCount;
            internal int CloseCount;
            internal Action StartAction;
            internal bool UseScene;
            public override Dictionary<string, Type> PreloadConfigTables => UseScene ? null : new Dictionary<string, Type>
            {
                { TableAddress, typeof(Model[]) }
            };
            public override string DefaultStartUpScene => UseScene ? SceneAddress : string.Empty;
            public override void OnStart()
            {
                StartCount++;
                StartAction?.Invoke();
            }
            protected override void OnClose() => CloseCount++;
        }

        private sealed class HostLossProbeModel : Model
        {
            internal Action InitAction;
            internal Action ExitAction;
            internal int InitCount;
            internal int ExitCount;
            internal bool IsInsideInit;
            internal bool InitReturned;
            internal bool ExitedInsideInit;
            internal Module OwnerAtExit;
            public override void OnInit()
            {
                InitCount++;
                IsInsideInit = true;
                try
                {
                    InitAction?.Invoke();
                    InitReturned = true;
                }
                finally
                {
                    IsInsideInit = false;
                }
            }
            public override void OnExit()
            {
                ExitCount++;
                ExitedInsideInit |= IsInsideInit;
                OwnerAtExit = Module;
                ExitAction?.Invoke();
            }
        }

        private sealed class HostLossProbeController : Controller
        {
            internal Action InitAction;
            internal int InitCount;
            internal int ExitCount;
            internal bool IsInsideInit;
            internal bool InitReturned;
            internal bool ExitedInsideInit;
            internal Module OwnerAtExit;
            public override void OnInit()
            {
                InitCount++;
                IsInsideInit = true;
                try
                {
                    InitAction?.Invoke();
                    InitReturned = true;
                }
                finally
                {
                    IsInsideInit = false;
                }
            }
            public override void OnExit()
            {
                ExitCount++;
                ExitedInsideInit |= IsInsideInit;
                OwnerAtExit = Module;
            }
        }

        private sealed class PendingStartupProvider : ICoreResourceProvider
        {
            internal readonly PendingStartupInput<string> Text = new PendingStartupInput<string>();
            internal readonly PendingStartupInput<CoreSceneResult> Scene = new PendingStartupInput<CoreSceneResult>();
            internal readonly PendingStartupInput<CorePrefabLease> Prefab = new PendingStartupInput<CorePrefabLease>();
            internal int TextCalls;
            internal int SceneCalls;
            internal int PrefabCalls;
            internal LoadSceneMode SceneMode;
            internal string Project;
            internal string Address;
            internal string PrefabProject;
            internal string PrefabAddress;
            public CoreResourceRequest<string> ReadTextAsync(string projectId, string address)
            {
                TextCalls++;
                if (TextCalls != 1)
                {
                    throw new InvalidOperationException("The owned provider serves one startup only.");
                }
                Project = projectId;
                Address = address;
                return Text;
            }
            public CorePrefabLease AcquirePrefab(string projectId, string address) => throw new NotSupportedException();
            public CoreResourceRequest<CorePrefabLease> AcquirePrefabAsync(string projectId, string address)
            {
                PrefabCalls++;
                if (PrefabCalls != 1)
                {
                    throw new InvalidOperationException("The owned provider serves one pending presentation input only.");
                }
                PrefabProject = projectId;
                PrefabAddress = address;
                return Prefab;
            }
            public CoreResourceRequest<CoreSceneResult> LoadSceneAsync(string projectId, string address, LoadSceneMode mode)
            {
                SceneCalls++;
                if (SceneCalls != 1)
                {
                    throw new InvalidOperationException("The owned provider serves one scene startup only.");
                }
                Project = projectId;
                Address = address;
                SceneMode = mode;
                return Scene;
            }
        }

        private sealed class PendingStartupInput<T> : CoreResourceRequest<T>
        {
            private CoreResourceStatus _status;
            private CoreResourceException _error;
            private Action<CoreResourceRequest<T>> _completed;
            private bool _isDisposed;
            private bool _isTaken;
            private T _value;
            internal int CancelCalls;
            internal int DisposeCalls;
            internal int TakeCalls;
            internal Action DisposeAction;
            public override CoreResourceStatus Status => _status;
            public override float Progress => _status == CoreResourceStatus.Succeeded ? 1f : 0f;
            public override CoreResourceException Error => _error;
            public override bool IsDisposed => _isDisposed;
            public override event Action<CoreResourceRequest<T>> Completed
            {
                add
                {
                    if (_isDisposed)
                    {
                        throw new ObjectDisposedException(nameof(PendingStartupInput<T>));
                    }
                    if (_status == CoreResourceStatus.Pending)
                    {
                        _completed += value;
                    }
                    else
                    {
                        Notify(value);
                    }
                }
                remove => _completed -= value;
            }
            internal void Succeed(T value)
            {
                if (_status != CoreResourceStatus.Pending || _isDisposed)
                {
                    return;
                }
                _value = value;
                _status = CoreResourceStatus.Succeeded;
                Publish();
            }
            public override T TakeResult()
            {
                TakeCalls++;
                if (_isDisposed)
                {
                    throw new ObjectDisposedException(nameof(PendingStartupInput<T>));
                }
                if (_error != null)
                {
                    throw _error;
                }
                if (_status != CoreResourceStatus.Succeeded || _isTaken)
                {
                    throw new InvalidOperationException("The owned startup input is not available for transfer.");
                }
                _isTaken = true;
                var value = _value;
                _value = default;
                return value;
            }
            public override void Cancel()
            {
                CancelCalls++;
                if (_status != CoreResourceStatus.Pending)
                {
                    return;
                }
                _status = CoreResourceStatus.Canceled;
                _error = new CoreResourceException(CoreResourceError.LoadFailed, "The owned startup consumer canceled its wait.",
                    new OperationCanceledException());
                Publish();
            }
            public override void Dispose()
            {
                DisposeCalls++;
                if (_isDisposed)
                {
                    return;
                }
                _isDisposed = true;
                try
                {
                    if (_status == CoreResourceStatus.Pending)
                    {
                        Cancel();
                    }
                }
                finally
                {
                    _value = default;
                    _completed = null;
                    DisposeAction?.Invoke();
                }
            }
            private void Publish()
            {
                var snapshot = _completed;
                _completed = null;
                Notify(snapshot);
            }
            private void Notify(Action<CoreResourceRequest<T>> snapshot)
            {
                if (snapshot == null)
                {
                    return;
                }
                foreach (Action<CoreResourceRequest<T>> observer in snapshot.GetInvocationList())
                {
                    try
                    {
                        observer(this);
                    }
                    catch (Exception error)
                    {
                        Debug.LogException(error);
                    }
                }
            }
        }

        private void ConfigureOwnedInputFocus()
        {
            var input = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem", true);
            var property = input.GetProperty("settings");
            var backgroundProperty = input.GetProperty("runInBackground");
            var previous = (UnityEngine.Object)property.GetValue(null);
            var flags = previous.hideFlags;
            var background = backgroundProperty.GetValue(null);
            var settings = UnityEngine.Object.Instantiate(previous);
            _restoreInputFocus = () =>
            {
                property.SetValue(null, previous);
                previous.hideFlags = flags;
                backgroundProperty.SetValue(null, background);
                if (settings)
                {
                    UnityEngine.Object.DestroyImmediate(settings);
                }
            };
            // 与既有 Core 原生输入回归相同，只配置自建设备的临时设置，不改工程输入资产。
            var focus = settings.GetType().GetProperty("editorInputBehaviorInPlayMode");
            var backgroundMode = settings.GetType().GetProperty("backgroundBehavior");
            focus.SetValue(settings, Enum.Parse(focus.PropertyType, "AllDeviceInputAlwaysGoesToGameView"));
            backgroundMode.SetValue(settings, Enum.Parse(backgroundMode.PropertyType, "IgnoreFocus"));
            previous.hideFlags = flags | HideFlags.HideInInspector;
            property.SetValue(null, settings);
            backgroundProperty.SetValue(null, true);
        }

        private void RestoreInputFocus()
        {
            var restore = _restoreInputFocus;
            _restoreInputFocus = null;
            restore?.Invoke();
        }
    }
}
