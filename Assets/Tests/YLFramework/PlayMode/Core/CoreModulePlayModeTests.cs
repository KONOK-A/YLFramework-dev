using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Pooling;

namespace YLFramework.Core.Tests
{
    public class CoreModulePlayModeTests
    {
        private static readonly PropertyInfo _managerInstance = typeof(YLFramework.Core.ModuleManager)
            .GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly FieldInfo _currentModule = typeof(YLFramework.Core.ModuleManager)
            .GetField("_currentModule", BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly List<YLFramework.Core.Module> _ownedModules = new List<YLFramework.Core.Module>();
        private object _manager;
        private object _previousCurrentModule;
        private CoreClassPoolScope _classPool;

        [SetUp]
        public void SetUp()
        {
            YLCoreProbeModel.ConstructorAction = null;
            YLCoreProbeController.ConstructorAction = null;
            YLCoreStartupProbeModule.ConstructorAction = null;
            _ownedModules.Clear();
            Assert.That(_managerInstance, Is.Not.Null);
            Assert.That(_currentModule, Is.Not.Null);
            _manager = _managerInstance.GetValue(null);
            _previousCurrentModule = _currentModule.GetValue(_manager);
            _classPool = new CoreClassPoolScope();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            YLCoreProbeModel.ConstructorAction = null;
            YLCoreProbeController.ConstructorAction = null;
            YLCoreStartupProbeModule.ConstructorAction = null;
            var failures = new List<Exception>();
            try
            {
                for (int i = _ownedModules.Count - 1; i >= 0; i--)
                {
                    var module = _ownedModules[i];
                    if (!ReferenceEquals(YLFramework.Core.ModuleManager.GetModule(module.ModuleName), module))
                    {
                        continue;
                    }

                    var baseProbe = module as YLCoreBaseProbeModule;
                    if (baseProbe != null)
                    {
                        baseProbe.AllowClose = true;
                    }

                    try
                    {
                        module.Close();
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                    }
                }
            }
            finally
            {
                // Do not clear shared dictionaries or replace the manager singleton.
                if (_manager != null)
                {
                    _currentModule.SetValue(_manager, _previousCurrentModule);
                }

                try
                {
                    _classPool?.Restore(_ownedModules);
                }
                finally
                {
                    _ownedModules.Clear();
                }
            }
            yield return null;
            if (failures.Count > 0)
            {
                throw new AggregateException("Owned Module cleanup failed.", failures);
            }
        }

        [UnityTest]
        public IEnumerator FixtureRestoreRejectsOwnedRegistrationWithoutDetachingItOrOtherModules()
        {
            var module = new YLCoreLifecycleModule { SeedMvc = true };
            yield return AwaitStartup(StartOwned(module));
            var other = new YLCoreConsumerModule();
            yield return AwaitStartup(StartOwned(other));
            var scope = new CoreClassPoolScope();
            var order = (IList)typeof(ModuleManager).GetField("_registrationOrder", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_manager);
            var previousOrder = new List<object>();
            foreach (var item in order)
            {
                previousOrder.Add(item);
            }

            Assert.Throws<InvalidOperationException>(() => scope.Restore(new[] { module }));
            Assert.That(ModuleManager.GetModule<YLCoreLifecycleModule>(), Is.SameAs(module));
            Assert.That(ModuleManager.GetModule<YLCoreConsumerModule>(), Is.SameAs(other));
            Assert.That(module.State, Is.EqualTo(ModuleState.Running));
            Assert.That(module.StartedModel.Module, Is.SameAs(module));
            Assert.That(module.StartedController.Module, Is.SameAs(module));
            Assert.That(module.StartedModel.ExitCount, Is.Zero);
            Assert.That(module.StartedController.ExitCount, Is.Zero);
            CollectionAssert.AreEqual(previousOrder, order);
            Assert.That(_currentModule.GetValue(_manager), Is.SameAs(other));

            module.Close();
            Assert.DoesNotThrow(() => scope.Restore(new[] { module }));
            Assert.That(order.Contains(module), Is.False);
            Assert.That(ModuleManager.GetModule<YLCoreConsumerModule>(), Is.SameAs(other));
            Assert.That(other.State, Is.EqualTo(ModuleState.Running));
        }

        [UnityTest]
        public IEnumerator EmptyModuleRegistersClosesAndRestarts()
        {
            var module = new YLCoreLifecycleModule();
            Assert.That(module.State, Is.EqualTo(ModuleState.Created));
            Assert.That(module.DefaultStartUpScene, Is.Empty);
            var first = StartOwned(module);
            yield return AwaitStartup(first);

            Assert.That(YLFramework.Core.ModuleManager.GetModule<YLCoreLifecycleModule>(), Is.SameAs(module));
            Assert.That(YLFramework.Core.ModuleManager.AllModules[module.ModuleName], Is.SameAs(module));
            Assert.That(YLFramework.Core.ModuleManager.AllBaseModules.ContainsKey(module.ModuleName), Is.False);
            Assert.That(module.ModuleName, Is.EqualTo(typeof(YLCoreLifecycleModule).FullName));
            Assert.That(module.RegisteredAtStart, Is.True);
            Assert.That(module.ProjectNameAtStart, Is.EqualTo(nameof(YLCoreLifecycleModule)));
            Assert.That(module.StartCount, Is.EqualTo(1));
            Assert.That(module.State, Is.EqualTo(ModuleState.Running));
            AssertIndexesEmpty(module);

            module.Close();
            Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(YLFramework.Core.ModuleManager.GetModule(module.ModuleName), Is.Null);
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(module.RegisteredAtClose, Is.True);
            yield return AwaitStartup(StartOwned(module));
            Assert.That(module.StartCount, Is.EqualTo(2));
            Assert.That(YLFramework.Core.ModuleManager.GetModule<YLCoreLifecycleModule>(), Is.SameAs(module));
            AssertIndexesEmpty(module);
            module.Close();
            Assert.That(module.CloseCount, Is.EqualTo(2));
            Assert.That(YLFramework.Core.ModuleManager.GetModule<YLCoreLifecycleModule>(), Is.Null);
        }

        [UnityTest]
        public IEnumerator RunningStartupIsIdempotentAndReturnsIndependentStableResults()
        {
            var module = new YLCoreLifecycleModule();
            var first = StartOwned(module);
            yield return AwaitStartup(first);
            var count = YLFramework.Core.ModuleManager.AllModules.Count;
            var other = new YLCoreConsumerModule();
            yield return AwaitStartup(StartOwned(other));
            var current = _currentModule.GetValue(_manager);
            var repeated = StartOwned(module);
            Assert.That(repeated.IsDone, Is.True);
            Assert.That(repeated.Error, Is.Null.Or.Empty);
            Assert.That(repeated, Is.Not.SameAs(first));
            Assert.That(_currentModule.GetValue(_manager), Is.SameAs(current));

            Assert.That(module.StartCount, Is.EqualTo(1));
            Assert.That(YLFramework.Core.ModuleManager.AllModules.Count, Is.EqualTo(count + 1));
            Assert.That(YLFramework.Core.ModuleManager.GetModule<YLCoreLifecycleModule>(), Is.SameAs(module));
            module.Close();
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(YLFramework.Core.ModuleManager.GetModule<YLCoreLifecycleModule>(), Is.Null);
            var restarted = StartOwned(module);
            Assert.That(restarted, Is.Not.SameAs(first).And.Not.SameAs(repeated));
            Assert.That(module.StartCount, Is.EqualTo(2));
            Assert.That(first.Module, Is.SameAs(module));
            Assert.That(first.IsDone && repeated.IsDone, Is.True);
            Assert.That(first.Error, Is.Null.Or.Empty);
            Assert.That(repeated.Error, Is.Null.Or.Empty);
        }

        [UnityTest]
        public IEnumerator SynchronousStartupPublishesOneRequestBeforeReentryAndSingletonNeedsNoBaseCall()
        {
            var module = new YLCoreStartupProbeModule();
            StartUpModuleRequest reentrant = null;
            var constructions = YLCoreStartupProbeModule.ConstructionCount;
            module.StartAction = () =>
            {
                Assert.That(module.State, Is.EqualTo(ModuleState.Starting));
                Assert.That(YLCoreStartupProbeModule.Instance, Is.SameAs(module));
                Assert.That(ModuleManager.GetModule(typeof(YLCoreStartupProbeModule)), Is.SameAs(module));
                Assert.That(_currentModule.GetValue(_manager), Is.SameAs(module));
                reentrant = ModuleManager.StartUpModule(module);
                Assert.That(reentrant.IsDone, Is.False);
                Assert.That(ModuleManager.StartUpModule<YLCoreStartupProbeModule>(), Is.SameAs(reentrant));
                Assert.That(ModuleManager.StartUpModule(typeof(YLCoreStartupProbeModule)), Is.SameAs(reentrant));
                Assert.That(ModuleManager.StartUpModule(module.ModuleName), Is.SameAs(reentrant));
                Assert.Throws<InvalidOperationException>(() => module.Close());
                Assert.Throws<InvalidOperationException>(() => ModuleManager.Shutdown());
                YLFramework.Async.CustomAsyncOperation<StartUpModuleRequest> operation = reentrant;
                Assert.Throws<NotSupportedException>(() => operation.Abort());
                Assert.That(operation.IsDone, Is.False);
            };
            var first = StartOwned(module);
            Assert.That(first, Is.SameAs(reentrant));
            Assert.That(first.IsDone, Is.True, "No-yield startup completes synchronously.");
            Assert.That(first.Error, Is.Null.Or.Empty);
            Assert.That(module.StartCount, Is.EqualTo(1));
            Assert.That(YLCoreStartupProbeModule.ConstructionCount, Is.EqualTo(constructions));
            module.StartAction = null;
            StartUpModuleRequest restarted = null;
            first.AddCompleteEvent(_ =>
            {
                Assert.That(module.State, Is.EqualTo(ModuleState.Running));
                module.Close();
                Assert.That(YLCoreStartupProbeModule.Instance, Is.Null);
                restarted = StartOwned(module);
            });
            Assert.That(restarted, Is.Not.SameAs(first));
            Assert.That(restarted.IsDone, Is.True);
            Assert.That(module.StartCount, Is.EqualTo(2));
            Assert.That(first.Error, Is.Null.Or.Empty);
            Assert.Throws<NotSupportedException>(() => first.Abort());
            Assert.That(first.Error, Is.Null.Or.Empty);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ConflictingInstanceCannotStartCloseOwnerOrChangeCurrentAndRegistriesAreReadOnly()
        {
            var module = new YLCoreLifecycleModule();
            yield return AwaitStartup(StartOwned(module));
            var other = new YLCoreConsumerModule();
            yield return AwaitStartup(StartOwned(other));
            var current = _currentModule.GetValue(_manager);
            var rejected = new YLCoreLifecycleModule();
            var addModule = typeof(ModuleManager).GetMethod("AddModule", BindingFlags.Static | BindingFlags.NonPublic);
            addModule.Invoke(null, new object[] { module });
            Assert.That(module.StartCount, Is.EqualTo(1));
            var registrationError = Assert.Throws<TargetInvocationException>(() => addModule.Invoke(null, new object[] { rejected }));
            Assert.That(registrationError.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.Throws<InvalidOperationException>(() => ModuleManager.StartUpModule(rejected));
            rejected.Close();
            Assert.That(rejected.State, Is.EqualTo(ModuleState.Created));
            Assert.That(rejected.StartCount, Is.Zero);
            Assert.That(rejected.CloseCount, Is.Zero);
            Assert.That(ModuleManager.GetModule<YLCoreLifecycleModule>(), Is.SameAs(module));
            Assert.That(_currentModule.GetValue(_manager), Is.SameAs(current));
            var readOnly = ModuleManager.AllModules;
            Assert.That(ModuleManager.AllModules, Is.SameAs(readOnly));
            Assert.Throws<NotSupportedException>(() => ((IDictionary<string, Module>)readOnly).Remove(module.ModuleName));
            Assert.Throws<NotSupportedException>(() => ((IDictionary<string, Module>)ModuleManager.AllBaseModules).Clear());
            module.Close();
            Assert.That(readOnly.ContainsKey(module.ModuleName), Is.False);
            Assert.That(_currentModule.GetValue(_manager), Is.SameAs(current));
            yield return AwaitStartup(StartOwned(rejected));
            Assert.That(ModuleManager.GetModule<YLCoreLifecycleModule>(), Is.SameAs(rejected));
            Assert.Throws<InvalidOperationException>(() => ModuleManager.StartUpModule(module));
            module.Close();
            Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(ModuleManager.GetModule<YLCoreLifecycleModule>(), Is.SameAs(rejected));
            Assert.That(rejected.State, Is.EqualTo(ModuleState.Running));
            Assert.That(rejected.CloseCount, Is.Zero);
            Assert.That(_currentModule.GetValue(_manager), Is.SameAs(rejected));
        }

        [UnityTest]
        public IEnumerator TypeStartupConstructsOnlyWhenMissingAndNeverImplicitlyUsesModulePool()
        {
            using var pool = new ObjectPool<YLCoreStartupProbeModule>(() => new YLCoreStartupProbeModule());
            var pooled = pool.Allocate();
            _ownedModules.Add(pooled);
            Assert.That(pool.Recycle(pooled), Is.True);
            var constructions = YLCoreStartupProbeModule.ConstructionCount;
            var first = ModuleManager.StartUpModule<YLCoreStartupProbeModule>();
            var module = (YLCoreStartupProbeModule)first.Module;
            _ownedModules.Add(module);
            yield return AwaitStartup(first);
            Assert.That(module, Is.Not.SameAs(pooled));
            Assert.That(YLCoreStartupProbeModule.ConstructionCount, Is.EqualTo(constructions + 1));
            Assert.That(ModuleManager.StartUpModule(typeof(YLCoreStartupProbeModule)).Module, Is.SameAs(module));
            Assert.That(ModuleManager.StartUpModule<YLCoreStartupProbeModule>().Module, Is.SameAs(module));
            Assert.That(ModuleManager.StartUpModule(module.ModuleName).Module, Is.SameAs(module));
            Assert.That(YLCoreStartupProbeModule.ConstructionCount, Is.EqualTo(constructions + 1));
            module.Close();
            Assert.That(YLCoreStartupProbeModule.Instance, Is.Null);
            Assert.That(pool.CurStoreCount, Is.EqualTo(1));
            Assert.That(pool.Allocate(), Is.SameAs(pooled), "Startup and Close must not touch an explicit business pool.");
            Assert.That(pool.Recycle(pooled), Is.True);
            Assert.Throws<InvalidOperationException>(() => pool.Recycle(module), "Closed Modules must not be implicitly adopted by the public pool.");
            var next = ModuleManager.StartUpModule(typeof(YLCoreStartupProbeModule));
            _ownedModules.Add(next.Module);
            yield return AwaitStartup(next);
            Assert.That(next.Module, Is.Not.SameAs(module).And.Not.SameAs(pooled));
            Assert.That(first.IsDone, Is.True);
            Assert.That(first.Module, Is.SameAs(module));
        }

        [UnityTest]
        public IEnumerator ModuleConstructorReentryRejectsBeforeSecondConstructionAndReleasesReservation()
        {
            var count = YLCoreStartupProbeModule.ConstructionCount;
            YLCoreStartupProbeModule.ConstructorAction = () => ModuleManager.StartUpModule<YLCoreStartupProbeModule>();
            var error = Assert.Throws<TargetInvocationException>(() => ModuleManager.StartUpModule(typeof(YLCoreStartupProbeModule)));
            Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(YLCoreStartupProbeModule.ConstructionCount, Is.EqualTo(count + 1));
            Assert.That(YLCoreStartupProbeModule.Instance, Is.Null);
            YLCoreStartupProbeModule.ConstructorAction = null;
            var request = ModuleManager.StartUpModule<YLCoreStartupProbeModule>();
            _ownedModules.Add(request.Module);
            yield return AwaitStartup(request);
            Assert.That(request.Module.State, Is.EqualTo(ModuleState.Running));
        }

        [UnityTest]
        public IEnumerator NestedStartupKeepsExecutingOwnerContextAndOuterFailurePreservesSuccessfulInnerCurrent()
        {
            var outer = new YLCoreStartupProbeModule();
            var inner = new YLCoreConsumerModule();
            var getCurrent = typeof(ModuleManager).GetMethod("GetCurrentModule", BindingFlags.Static | BindingFlags.NonPublic);
            outer.StartAction = () =>
            {
                Assert.That(getCurrent.Invoke(null, null), Is.SameAs(outer));
                var nested = StartOwned(inner);
                Assert.That(nested.IsDone, Is.True);
                Assert.That(nested.Error, Is.Null.Or.Empty);
                Assert.That(getCurrent.Invoke(null, null), Is.SameAs(outer), "The active OnStart context owns automatic View loading.");
                throw new InvalidOperationException("outer startup failure after inner success");
            };
            var request = StartOwned(outer);
            Assert.That(request.IsDone, Is.True);
            Assert.That(request.Error, Does.Contain("outer startup failure after inner success"));
            Assert.That(outer.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(ModuleManager.GetModule<YLCoreStartupProbeModule>(), Is.Null);
            Assert.That(YLCoreStartupProbeModule.Instance, Is.Null);
            AssertIndexesEmpty(outer);
            Assert.That(inner.State, Is.EqualTo(ModuleState.Running));
            Assert.That(getCurrent.Invoke(null, null), Is.SameAs(inner));
            Assert.That(_currentModule.GetValue(_manager), Is.SameAs(inner));
            yield return null;
        }

        [UnityTest]
        public IEnumerator StartupRequestUsesFoundationCompletionProgressAndLateCallbacks()
        {
            var module = new YLCoreLifecycleModule();
            var request = StartOwned(module);
            YLFramework.Async.CustomAsyncOperation<YLFramework.Core.StartUpModuleRequest> foundation = request;
            int completionCount = 0;
            YLFramework.Core.StartUpModuleRequest completedRequest = null;
            bool callbackSawStartedModule = false;
            bool callbackSawCompletion = false;
            Action<YLFramework.Core.StartUpModuleRequest> callback = completed =>
            {
                completionCount++;
                completedRequest = completed;
                callbackSawStartedModule = module.StartCount == 1 && module.RegisteredAtStart;
                callbackSawCompletion = completed.IsDone && !completed.keepWaiting &&
                    completed.Progress == 1f && string.IsNullOrEmpty(completed.Error);
            };
            foundation.AddCompleteEvent(callback);
            yield return AwaitStartup(request);

            Assert.That(completionCount, Is.EqualTo(1));
            Assert.That(completedRequest, Is.SameAs(request));
            Assert.That(callbackSawStartedModule, Is.True);
            Assert.That(callbackSawCompletion, Is.True);
            Assert.That(request.Module, Is.SameAs(module));
            Assert.That(request.Step, Is.EqualTo(YLFramework.Core.StartingModuleSteps.LoadScene));
            Assert.That(foundation.Progress, Is.EqualTo(1f));
            Assert.That(foundation.keepWaiting, Is.False);
            Assert.That(foundation.Error, Is.Null.Or.Empty);
            int lateCount = 0;
            foundation.AddCompleteEvent(completed =>
            {
                Assert.That(completed, Is.SameAs(request));
                lateCount++;
            });
            Assert.That(lateCount, Is.EqualTo(1), "A callback added after completion runs immediately.");
            foundation.RemoveCompleteEvent(callback);
            yield return null;
            Assert.That(completionCount, Is.EqualTo(1));
            Assert.That(lateCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ModelsTrackOwnershipIdsMultipleInstancesQueriesAndRemoval()
        {
            var module = new YLCoreLifecycleModule();
            yield return AwaitStartup(StartOwned(module));
            var first = new YLCoreProbeModel { Id = 11 };
            var second = new YLCoreProbeModel { Id = 22 };
            var automatic = new YLCoreDerivedProbeModel();
            module.AddModel(first);
            module.AddModel(second);
            module.AddModel(automatic);

            foreach (var model in new YLCoreProbeModel[] { first, second, automatic })
            {
                Assert.That(model.Module, Is.SameAs(module));
                Assert.That(model.InitCount, Is.EqualTo(1));
                Assert.That(model.RegisteredAtInit, Is.True);
            }
            Assert.That(automatic.Id, Is.GreaterThanOrEqualTo(0L));
            Assert.That(module.GetModel<YLCoreDerivedProbeModel>(automatic.Id), Is.SameAs(automatic));
            Assert.That(module.GetModel<YLCoreProbeModel>(11), Is.SameAs(first));
            Assert.That(module.GetModel(typeof(YLCoreProbeModel), 22), Is.SameAs(second));
            Assert.That(module.GetModel<YLCoreDerivedProbeModel>(), Is.SameAs(automatic));
            Assert.That(module.GetModels<YLCoreProbeModel>(), Is.EquivalentTo(new[] { first, second, automatic }));
            var ambiguous = Assert.Throws<Exception>(() => module.GetModel<YLCoreProbeModel>());
            StringAssert.Contains(typeof(YLCoreProbeModel).FullName, ambiguous.Message);
            Assert.Throws<Exception>(() => module.GetModel(typeof(YLCoreProbeModel)));
            Assert.Throws<Exception>(() => module.LoadModel<YLCoreProbeModel>());
            Assert.Throws<Exception>(() => module.LoadModel(typeof(YLCoreProbeModel)));
            Assert.That(module.GetModels(typeof(YLCoreProbeModel)), Is.EquivalentTo(new[] { first, second, automatic }));

            var duplicate = new YLCoreProbeModel { Id = 11 };
            Assert.Throws<InvalidOperationException>(() => module.AddModel(duplicate));
            Assert.That(duplicate.Module, Is.Null);
            Assert.That(duplicate.InitCount, Is.Zero);
            Assert.That(module.GetModel<YLCoreProbeModel>(11), Is.SameAs(first));
            module.RemoveModel(first);
            Assert.That(first.ExitCount, Is.EqualTo(1));
            Assert.That(first.RegisteredAtExit, Is.True);
            Assert.That(first.Module, Is.Null);
            Assert.That(module.GetModel<YLCoreProbeModel>(11), Is.Null);
            Assert.That(module.GetModel<YLCoreProbeModel>(), Is.SameAs(second));
            Assert.That(module.GetModel(typeof(YLCoreProbeModel)), Is.SameAs(second),
                "Single-object queries use the exact registered type; collections also include derived types.");
            module.ClearModel<YLCoreProbeModel>();
            Assert.That(module.GetModels<YLCoreProbeModel>(), Is.Empty);
            Assert.That(first.ExitCount, Is.EqualTo(1));
            Assert.That(second.ExitCount, Is.EqualTo(1));
            Assert.That(automatic.ExitCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator BaseModelsRequireExplicitOwnerQueriesAndBorrowerCloseDoesNotReleaseThem()
        {
            var baseModule = new YLCoreBaseProbeModule();
            var consumer = new YLCoreConsumerModule();
            yield return AwaitStartup(StartOwned(baseModule));
            yield return AwaitStartup(StartOwned(consumer));
            var shared = new YLCoreProbeModel { Id = 31 };
            var derived = new YLCoreDerivedProbeModel { Id = 32 };
            var local = new YLCoreProbeModel { Id = 31 };
            baseModule.AddModel(shared);
            baseModule.AddModel(derived);

            Assert.That(YLFramework.Core.ModuleManager.AllBaseModules[baseModule.ModuleName], Is.SameAs(baseModule));
            Assert.That(consumer.GetModel<YLCoreProbeModel>(), Is.Null);
            Assert.That(consumer.GetModel<YLCoreProbeModel>(31), Is.Null);
            Assert.That(consumer.GetModel<YLCoreDerivedProbeModel>(32), Is.Null);
            Assert.That(consumer.GetModels<YLCoreProbeModel>(), Is.Empty);
            var owner = YLFramework.Core.ModuleManager.GetModule<YLCoreBaseProbeModule>();
            var borrowed = owner.GetModel<YLCoreProbeModel>(31);
            Assert.That(borrowed, Is.SameAs(shared));
            Assert.That(owner.GetModel<YLCoreProbeModel>(), Is.SameAs(shared));
            Assert.That(owner.GetModel(typeof(YLCoreDerivedProbeModel), 32), Is.SameAs(derived));
            Assert.That(owner.GetModels<YLCoreProbeModel>(), Is.EquivalentTo(new[] { shared, derived }));
            AssertIndexesEmpty(consumer);
            consumer.AddModel(local);
            Assert.That(consumer.GetModel<YLCoreProbeModel>(31), Is.SameAs(local));
            Assert.That(local.Module, Is.SameAs(consumer));
            Assert.That(shared.Module, Is.SameAs(baseModule));
            Assert.That(consumer.GetModels<YLCoreProbeModel>(), Is.EqualTo(new[] { local }));

            consumer.Close();
            Assert.That(local.ExitCount, Is.EqualTo(1));
            Assert.That(shared.ExitCount, Is.Zero);
            Assert.That(derived.ExitCount, Is.Zero);
            Assert.That(borrowed.Module, Is.SameAs(baseModule));
            Assert.That(baseModule.GetModel<YLCoreProbeModel>(31), Is.SameAs(shared));
            Assert.That(YLFramework.Core.ModuleManager.GetModule<YLCoreBaseProbeModule>(), Is.SameAs(baseModule));
        }

        [UnityTest]
        public IEnumerator ControllersAreLocalAndExplicitBorrowingPreservesOwnershipAndExit()
        {
            yield return AssertControllerIsolation(false);
        }

        [UnityTest]
        public IEnumerator ControllerIsolationDoesNotDependOnModuleRegistrationOrder()
        {
            yield return AssertControllerIsolation(true);
        }

        private IEnumerator AssertControllerIsolation(bool consumerFirst)
        {
            var owner = new YLCoreLifecycleModule();
            var consumer = new YLCoreConsumerModule();
            yield return AwaitStartup(StartOwned(consumerFirst ? (YLFramework.Core.Module)consumer : owner));
            yield return AwaitStartup(StartOwned(consumerFirst ? (YLFramework.Core.Module)owner : consumer));
            Assert.That(owner.IsBaseModule, Is.False);
            var shared = owner.LoadController<YLCoreProbeController>();
            Assert.That(owner.LoadController(typeof(YLCoreProbeController)), Is.SameAs(shared));
            Assert.That(owner.LoadController(typeof(YLCoreProbeController).FullName), Is.SameAs(shared));
            Assert.That(consumer.GetController<YLCoreProbeController>(), Is.Null);
            var borrowed = YLFramework.Core.ModuleManager.GetModule<YLCoreLifecycleModule>().GetController<YLCoreProbeController>();
            Assert.That(borrowed, Is.SameAs(shared));
            AssertIndexesEmpty(consumer);
            Assert.That(shared.Module, Is.SameAs(owner));
            Assert.That(shared.OwnerAtInit, Is.SameAs(owner));
            Assert.That(shared.InitCount, Is.EqualTo(1));

            var local = (YLCoreProbeController)consumer.LoadController(typeof(YLCoreProbeController));
            Assert.That(local, Is.Not.SameAs(shared));
            Assert.That(local.Module, Is.SameAs(consumer));
            Assert.That(local.OwnerAtInit, Is.SameAs(consumer));
            Assert.That(local.InitCount, Is.EqualTo(1));
            Assert.That(consumer.LoadController<YLCoreProbeController>(), Is.SameAs(local));
            Assert.That(consumer.GetController(typeof(YLCoreProbeController)), Is.SameAs(local));
            Assert.That(consumer.GetController(typeof(YLCoreProbeController).FullName), Is.SameAs(local));
            consumer.RemoveController<YLCoreProbeController>();
            Assert.That(local.ExitCount, Is.EqualTo(1));
            Assert.That(local.OwnerAtExit, Is.SameAs(consumer));
            Assert.That(shared.ExitCount, Is.Zero);
            Assert.That(consumer.GetController<YLCoreProbeController>(), Is.Null);
            var replacement = (YLCoreProbeController)consumer.LoadController(typeof(YLCoreProbeController).FullName);
            Assert.That(replacement, Is.Not.SameAs(local).And.Not.SameAs(shared));
            Assert.That(replacement.Module, Is.SameAs(consumer));
            consumer.Close();
            Assert.That(local.ExitCount, Is.EqualTo(1));
            Assert.That(replacement.ExitCount, Is.EqualTo(1));
            Assert.That(shared.ExitCount, Is.Zero, "Borrowed controllers are not owned by the consumer.");
            Assert.That(borrowed.Module, Is.SameAs(owner));
            Assert.That(owner.GetController<YLCoreProbeController>(), Is.SameAs(borrowed));
            owner.Close();
            Assert.That(shared.ExitCount, Is.EqualTo(1));
            Assert.That(shared.OwnerAtExit, Is.SameAs(owner));
        }

        [UnityTest]
        public IEnumerator GetQueriesDoNotConstructAndLoadOnlyCreatesMissingLocalObjects()
        {
            var owner = new YLCoreBaseProbeModule();
            var consumer = new YLCoreConsumerModule();
            yield return AwaitStartup(StartOwned(consumer));
            yield return AwaitStartup(StartOwned(owner));
            var sharedModel = owner.LoadModel<YLCoreProbeModel>();
            var sharedController = owner.LoadController<YLCoreProbeController>();
            int modelConstructions = YLCoreProbeModel.ConstructionCount;
            int controllerConstructions = YLCoreProbeController.ConstructionCount;

            Assert.That(consumer.GetModel<YLCoreProbeModel>(), Is.Null);
            Assert.That(consumer.GetModel(typeof(YLCoreProbeModel)), Is.Null);
            Assert.That(consumer.GetModel<YLCoreProbeModel>(sharedModel.Id), Is.Null);
            Assert.That(consumer.GetModel(typeof(YLCoreProbeModel), sharedModel.Id), Is.Null);
            Assert.That(consumer.GetModels<YLCoreProbeModel>(), Is.Empty);
            Assert.That(consumer.GetModels(typeof(YLCoreProbeModel)), Is.Empty);
            Assert.That(consumer.GetController<YLCoreProbeController>(), Is.Null);
            Assert.That(consumer.GetController(typeof(YLCoreProbeController)), Is.Null);
            Assert.That(consumer.GetController(typeof(YLCoreProbeController).FullName), Is.Null);
            Assert.That(consumer.GetController("YLFramework.Core.Tests.NoSuchController"), Is.Null);
            Assert.That(YLCoreProbeModel.ConstructionCount, Is.EqualTo(modelConstructions));
            Assert.That(YLCoreProbeController.ConstructionCount, Is.EqualTo(controllerConstructions));
            AssertIndexesEmpty(consumer);

            var localModel = consumer.LoadModel(typeof(YLCoreProbeModel));
            Assert.That(localModel, Is.Not.SameAs(sharedModel));
            Assert.That(localModel.Module, Is.SameAs(consumer));
            Assert.That(((YLCoreProbeModel)localModel).InitCount, Is.EqualTo(1));
            Assert.That(consumer.LoadModel<YLCoreProbeModel>(), Is.SameAs(localModel));
            Assert.That(consumer.LoadModel(typeof(YLCoreProbeModel)), Is.SameAs(localModel));
            Assert.That(owner.LoadModel(typeof(YLCoreProbeModel)), Is.SameAs(sharedModel));
            var localController = consumer.LoadController<YLCoreProbeController>();
            Assert.That(localController, Is.Not.SameAs(sharedController));
            Assert.That(localController.Module, Is.SameAs(consumer));
            Assert.That(localController.InitCount, Is.EqualTo(1));
            Assert.That(owner.GetController<YLCoreProbeController>(), Is.SameAs(sharedController));
            consumer.Close();
            Assert.That(((YLCoreProbeModel)localModel).ExitCount, Is.EqualTo(1));
            Assert.That(localController.ExitCount, Is.EqualTo(1));
            Assert.That(sharedModel.ExitCount, Is.Zero);
            Assert.That(sharedController.ExitCount, Is.Zero);
            Assert.That(sharedModel.Module, Is.SameAs(owner));
            Assert.That(sharedController.Module, Is.SameAs(owner));
        }

        [UnityTest]
        public IEnumerator CreateControllerOverloadsCreateLocallyWithoutBorrowingOtherModules()
        {
            var owner = new YLCoreLifecycleModule();
            var consumer = new YLCoreConsumerModule();
            yield return AwaitStartup(StartOwned(owner));
            yield return AwaitStartup(StartOwned(consumer));
            var shared = owner.CreateController<YLCoreProbeController>();
            var local = consumer.CreateController<YLCoreProbeController>();
            Assert.That(local, Is.Not.SameAs(shared));
            Assert.That(local.Module, Is.SameAs(consumer));
            Assert.That(local.InitCount, Is.EqualTo(1));
            consumer.RemoveController<YLCoreProbeController>();
            var byType = (YLCoreProbeController)consumer.CreateController(typeof(YLCoreProbeController));
            Assert.That(byType, Is.Not.SameAs(shared).And.Not.SameAs(local));
            Assert.That(byType.Module, Is.SameAs(consumer));
            Assert.That(consumer.GetController<YLCoreProbeController>(), Is.SameAs(byType));
            consumer.RemoveController<YLCoreProbeController>();
            var byName = (YLCoreProbeController)consumer.CreateController(typeof(YLCoreProbeController).FullName);
            Assert.That(byName, Is.Not.SameAs(shared).And.Not.SameAs(byType));
            Assert.That(byName.Module, Is.SameAs(consumer));
            Assert.That(consumer.GetController<YLCoreProbeController>(), Is.SameAs(byName));
            consumer.Close();
            Assert.That(local.ExitCount, Is.EqualTo(1));
            Assert.That(byType.ExitCount, Is.EqualTo(1));
            Assert.That(byName.ExitCount, Is.EqualTo(1));
            Assert.That(shared.Module, Is.SameAs(owner));
            Assert.That(shared.ExitCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator DuplicateCreateControllerRejectsBeforeConstructionAcrossAllEntrypoints()
        {
            var module = new YLCoreLifecycleModule();
            yield return AwaitStartup(StartOwned(module));
            var registered = module.CreateController<YLCoreProbeController>();
            int constructions = YLCoreProbeController.ConstructionCount;
            Assert.Throws<InvalidOperationException>(() => module.CreateController<YLCoreProbeController>());
            Assert.Throws<InvalidOperationException>(() => module.CreateController(typeof(YLCoreProbeController)));
            Assert.Throws<InvalidOperationException>(() => module.CreateController(typeof(YLCoreProbeController).FullName));
            Assert.That(YLCoreProbeController.ConstructionCount, Is.EqualTo(constructions));
            Assert.That(module.LoadController<YLCoreProbeController>(), Is.SameAs(registered));
            Assert.That(registered.InitCount, Is.EqualTo(1));
            module.Close();
            Assert.That(registered.ExitCount, Is.EqualTo(1));
            Assert.That(registered.Module, Is.Null);
        }

        [UnityTest]
        public IEnumerator RegistrationIsIdempotentHasOneOwnerAndRemovalUsesExactInstances()
        {
            var owner = new YLCoreLifecycleModule();
            var consumer = new YLCoreConsumerModule();
            yield return AwaitStartup(StartOwned(owner));
            yield return AwaitStartup(StartOwned(consumer));
            long id = (long)int.MaxValue + 101;
            var model = new YLCoreProbeModel { Id = id };
            var otherModel = new YLCoreProbeModel { Id = id };
            owner.AddModel(model);
            owner.AddModel(model);
            Assert.That(model.InitCount, Is.EqualTo(1));
            Assert.That(owner.GetModel<YLCoreProbeModel>(id), Is.SameAs(model));
            Assert.That(owner.GetModel(typeof(YLCoreProbeModel), id), Is.SameAs(model));
            Assert.Throws<InvalidOperationException>(() => model.Id = id + 1);
            Assert.Throws<InvalidOperationException>(() => consumer.AddModel(model));
            owner.RemoveModel(otherModel);
            consumer.AddModel(otherModel);
            owner.RemoveModel(otherModel);
            consumer.RemoveModel(model);
            Assert.That(model.ExitCount, Is.Zero);
            Assert.That(otherModel.ExitCount, Is.Zero);
            Assert.That(model.Module, Is.SameAs(owner));
            Assert.That(owner.GetModel<YLCoreProbeModel>(id), Is.SameAs(model));

            var controller = owner.LoadController<YLCoreProbeController>();
            owner.AddController(controller);
            var otherController = new YLCoreProbeController();
            owner.RemoveController(otherController);
            Assert.Throws<InvalidOperationException>(() => owner.AddController(otherController));
            Assert.Throws<InvalidOperationException>(() => consumer.AddController(controller));
            consumer.AddController(otherController);
            owner.RemoveController(otherController);
            consumer.RemoveController(controller);
            Assert.That(controller.InitCount, Is.EqualTo(1));
            Assert.That(controller.ExitCount, Is.Zero);
            Assert.That(owner.GetController<YLCoreProbeController>(), Is.SameAs(controller));
            Assert.Throws<ArgumentNullException>(() => owner.AddModel(null));
            Assert.Throws<ArgumentNullException>(() => owner.AddController(null));
            int constructions = YLCoreProbeModel.ConstructionCount;
            Assert.Throws<ArgumentException>(() => owner.CreateController(typeof(YLCoreProbeModel)));
            Assert.Throws<ArgumentException>(() => owner.CreateController<YLFramework.Core.Controller>());
            Assert.That(YLCoreProbeModel.ConstructionCount, Is.EqualTo(constructions));

            owner.RemoveModel(model);
            Assert.That(model.Module, Is.Null);
            model.Id = id + 1;
            consumer.AddModel(model);
            Assert.That(model.InitCount, Is.EqualTo(2));
            Assert.That(model.Module, Is.SameAs(consumer));
            owner.RemoveController(controller);
            Assert.That(controller.Module, Is.Null);
            consumer.RemoveController(otherController);
            consumer.AddController(controller);
            Assert.That(controller.InitCount, Is.EqualTo(2));
            consumer.Close();
            Assert.That(model.ExitCount, Is.EqualTo(2));
            Assert.That(controller.ExitCount, Is.EqualTo(2));
            Assert.That(otherModel.Module, Is.Null);
            Assert.That(otherController.Module, Is.Null);
        }

        [UnityTest]
        public IEnumerator AutomaticIdsSkipExplicitKeysAndDoNotDependOnHashCodes()
        {
            var module = new YLCoreLifecycleModule();
            yield return AwaitStartup(StartOwned(module));
            var explicitModel = new YLCoreConstantHashModel { Id = 1 };
            var first = new YLCoreConstantHashModel();
            var second = new YLCoreConstantHashModel();
            module.AddModel(explicitModel);
            module.AddModel(first);
            module.AddModel(second);
            Assert.That(first.GetHashCode(), Is.EqualTo(second.GetHashCode()));
            Assert.That(first.Id, Is.Not.EqualTo(1L));
            Assert.That(second.Id, Is.Not.EqualTo(1L).And.Not.EqualTo(first.Id));
            Assert.That(module.GetModel<YLCoreConstantHashModel>(1L), Is.SameAs(explicitModel));
            Assert.That(module.GetModel<YLCoreConstantHashModel>(first.Id), Is.SameAs(first));
            Assert.That(module.GetModel<YLCoreConstantHashModel>(second.Id), Is.SameAs(second));
            Assert.That(module.GetModels<YLCoreProbeModel>(), Has.Count.EqualTo(3));
            var maximum = new YLCoreConstantHashModel { Id = long.MaxValue };
            module.AddModel(maximum);
            typeof(YLFramework.Core.Module).GetField("NextModelId", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(module, long.MaxValue);
            var wrapped = new YLCoreConstantHashModel();
            module.AddModel(wrapped);
            Assert.That(wrapped.Id, Is.EqualTo(0L));
            Assert.That(module.GetModel<YLCoreConstantHashModel>(long.MaxValue), Is.SameAs(maximum));
        }

        [UnityTest]
        public IEnumerator InitializationCanQueryAndLoadTemporarySelfButCannotReregisterOrRemoveIt()
        {
            var module = new YLCoreLifecycleModule();
            var consumer = new YLCoreConsumerModule();
            yield return AwaitStartup(StartOwned(module));
            yield return AwaitStartup(StartOwned(consumer));
            var model = new YLCoreProbeModel();
            model.InitAction = self =>
            {
                Assert.That(module.GetModel<YLCoreProbeModel>(self.Id), Is.SameAs(self));
                Assert.That(module.LoadModel<YLCoreProbeModel>(), Is.SameAs(self));
                Assert.That(module.LoadModel(typeof(YLCoreProbeModel)), Is.SameAs(self));
                Assert.Throws<InvalidOperationException>(() => module.AddModel(self));
                Assert.Throws<InvalidOperationException>(() => module.AddModel(new YLCoreProbeModel { Id = self.Id }));
                Assert.Throws<InvalidOperationException>(() => consumer.AddModel(self));
                Assert.Throws<InvalidOperationException>(() => self.Id++);
                Assert.Throws<InvalidOperationException>(() => module.RemoveModel(self));
            };
            module.AddModel(model);
            var controller = new YLCoreProbeController();
            controller.InitAction = self =>
            {
                Assert.That(module.GetController<YLCoreProbeController>(), Is.SameAs(self));
                Assert.That(module.LoadController<YLCoreProbeController>(), Is.SameAs(self));
                Assert.That(module.LoadController(typeof(YLCoreProbeController).FullName), Is.SameAs(self));
                int constructions = YLCoreProbeController.ConstructionCount;
                Assert.Throws<InvalidOperationException>(() => module.CreateController<YLCoreProbeController>());
                Assert.That(YLCoreProbeController.ConstructionCount, Is.EqualTo(constructions));
                Assert.Throws<InvalidOperationException>(() => module.AddController(self));
                Assert.Throws<InvalidOperationException>(() => consumer.AddController(self));
                Assert.Throws<InvalidOperationException>(() => module.RemoveController(self));
            };
            module.AddController(controller);
            module.AddModel(model);
            module.AddController(controller);
            Assert.That(model.InitCount, Is.EqualTo(1));
            Assert.That(controller.InitCount, Is.EqualTo(1));
            AssertIndexesEmpty(consumer);
        }

        [UnityTest]
        public IEnumerator InitializationFailureRollsBackAndCleanupFailurePreservesOriginalException()
        {
            var module = new YLCoreLifecycleModule();
            yield return AwaitStartup(StartOwned(module));
            var initializationError = new InvalidOperationException("MVC initialization failed");
            var cleanupError = new InvalidOperationException("MVC cleanup failed");
            YLCoreProbeModel failedModel = null;
            YLCoreProbeController failedController = null;
            YLCoreProbeModel.ConstructorAction = self =>
            {
                failedModel = self;
                self.InitAction = _ => throw initializationError;
                self.ExitAction = _ => throw cleanupError;
            };
            Assert.That(Assert.Throws<InvalidOperationException>(() => module.LoadModel<YLCoreProbeModel>()), Is.SameAs(initializationError));
            Assert.That(initializationError.Data["YLFramework.Core.CleanupException"], Is.SameAs(cleanupError));
            Assert.That(failedModel.Module, Is.Null);
            Assert.That(failedModel.Id, Is.EqualTo(-1L));
            Assert.That(failedModel.RegisteredAtExit, Is.True);
            Assert.That(failedModel.ExitCount, Is.EqualTo(1));
            AssertIndexesEmpty(module);
            YLCoreProbeModel.ConstructorAction = null;
            failedModel.InitAction = null;
            failedModel.ExitAction = null;
            module.RemoveModel(failedModel);
            Assert.That(failedModel.ExitCount, Is.EqualTo(1));
            module.AddModel(failedModel);
            Assert.That(failedModel.InitCount, Is.EqualTo(2));
            module.RemoveModel(failedModel);

            var controllerInitializationError = new InvalidOperationException("Controller initialization failed");
            YLCoreProbeController.ConstructorAction = self =>
            {
                failedController = self;
                self.InitAction = _ => throw controllerInitializationError;
                self.ExitAction = _ => throw cleanupError;
            };
            Assert.That(Assert.Throws<InvalidOperationException>(() => module.CreateController(typeof(YLCoreProbeController).FullName)), Is.SameAs(controllerInitializationError));
            Assert.That(controllerInitializationError.Data["YLFramework.Core.CleanupException"], Is.SameAs(cleanupError));
            Assert.That(failedController.Module, Is.Null);
            Assert.That(failedController.OwnerAtExit, Is.SameAs(module));
            Assert.That(failedController.ExitCount, Is.EqualTo(1));
            AssertIndexesEmpty(module);
            YLCoreProbeController.ConstructorAction = null;
            module.RemoveController(failedController);
            Assert.That(failedController.ExitCount, Is.EqualTo(1));
            var replacement = module.LoadController<YLCoreProbeController>();
            Assert.That(replacement, Is.Not.SameAs(failedController));
            Assert.That(replacement.Module, Is.SameAs(module));
            Assert.That(replacement.InitCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ExitFailureDetachesExactInstancesAndRecursiveRemovalAttemptsExitOnlyOnce()
        {
            var module = new YLCoreLifecycleModule();
            yield return AwaitStartup(StartOwned(module));
            var exitError = new InvalidOperationException("MVC exit failed");
            var model = module.LoadModel<YLCoreProbeModel>();
            model.ExitAction = self =>
            {
                Assert.That(module.GetModel<YLCoreProbeModel>(self.Id), Is.SameAs(self));
                Assert.That(self.Module, Is.SameAs(module));
                module.RemoveModel(self);
                Assert.Throws<InvalidOperationException>(() => module.LoadModel<YLCoreProbeModel>());
                Assert.Throws<InvalidOperationException>(() => module.AddModel(self));
                throw exitError;
            };
            Assert.That(Assert.Throws<InvalidOperationException>(() => module.RemoveModel(model)), Is.SameAs(exitError));
            Assert.That(model.Module, Is.Null);
            Assert.That(model.ExitCount, Is.EqualTo(1));
            Assert.That(module.GetModels<YLCoreProbeModel>(), Is.Empty);
            module.RemoveModel(model);
            var controller = module.LoadController<YLCoreProbeController>();
            controller.ExitAction = self =>
            {
                Assert.That(module.GetController<YLCoreProbeController>(), Is.SameAs(self));
                Assert.That(self.Module, Is.SameAs(module));
                module.RemoveController(self);
                Assert.Throws<InvalidOperationException>(() => module.LoadController<YLCoreProbeController>());
                Assert.Throws<InvalidOperationException>(() => module.AddController(self));
                throw exitError;
            };
            Assert.That(Assert.Throws<InvalidOperationException>(() => module.RemoveController<YLCoreProbeController>()), Is.SameAs(exitError));
            Assert.That(controller.Module, Is.Null);
            Assert.That(controller.ExitCount, Is.EqualTo(1));
            module.RemoveController(controller);
            Assert.That(model.ExitCount, Is.EqualTo(1));
            Assert.That(controller.ExitCount, Is.EqualTo(1));
            AssertIndexesEmpty(module);
        }

        [UnityTest]
        public IEnumerator ConstructorReentryRejectsBeforeAnotherConstructionAndReleasesReservedKeys()
        {
            var module = new YLCoreLifecycleModule();
            yield return AwaitStartup(StartOwned(module));
            int modelConstructions = YLCoreProbeModel.ConstructionCount;
            YLCoreProbeModel.ConstructorAction = _ => module.LoadModel<YLCoreProbeModel>();
            var modelError = Assert.Throws<TargetInvocationException>(() => module.LoadModel(typeof(YLCoreProbeModel)));
            Assert.That(modelError.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(YLCoreProbeModel.ConstructionCount, Is.EqualTo(modelConstructions + 1));
            AssertIndexesEmpty(module);
            YLCoreProbeModel.ConstructorAction = null;
            var model = module.LoadModel<YLCoreProbeModel>();
            Assert.That(model.InitCount, Is.EqualTo(1));
            module.RemoveModel(model);
            int controllerConstructions = YLCoreProbeController.ConstructionCount;
            YLCoreProbeController.ConstructorAction = _ => module.CreateController<YLCoreProbeController>();
            var controllerError = Assert.Throws<TargetInvocationException>(() => module.CreateController(typeof(YLCoreProbeController)));
            Assert.That(controllerError.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(YLCoreProbeController.ConstructionCount, Is.EqualTo(controllerConstructions + 1));
            AssertIndexesEmpty(module);
            YLCoreProbeController.ConstructorAction = null;
            Assert.That(module.CreateController<YLCoreProbeController>().InitCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator RemovingModelDoesNotPoolOrReuseItsBusinessState()
        {
            var module = new YLCoreLifecycleModule();
            yield return AwaitStartup(StartOwned(module));
            using var pool = new ObjectPool<YLCoreProbeModel>(() => new YLCoreProbeModel());
            var pooled = pool.Allocate();
            Assert.That(pool.Recycle(pooled), Is.True);
            var constructions = YLCoreProbeModel.ConstructionCount;
            var first = module.LoadModel<YLCoreProbeModel>();
            Assert.That(first, Is.Not.SameAs(pooled));
            Assert.That(YLCoreProbeModel.ConstructionCount, Is.EqualTo(constructions + 1));
            first.Events = new List<string> { "old business state" };
            module.RemoveModel(first);
            Assert.That(first.Module, Is.Null);
            Assert.That(pool.CurStoreCount, Is.EqualTo(1));
            Assert.That(pool.Allocate(), Is.SameAs(pooled));
            Assert.That(pool.Recycle(pooled), Is.True);
            Assert.Throws<InvalidOperationException>(() => pool.Recycle(first));
            var second = module.LoadModel(typeof(YLCoreProbeModel)) as YLCoreProbeModel;
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.Events, Is.Null);
            Assert.That(second.InitCount, Is.EqualTo(1));
            Assert.That(second.ExitCount, Is.Zero);
            Assert.That(second.Id, Is.Not.EqualTo(first.Id));
            Assert.That(YLCoreProbeModel.ConstructionCount, Is.EqualTo(constructions + 2));
        }

        [UnityTest]
        public IEnumerator StartupAndCloseOrderClearAllMvcIndexesBeforeRestart()
        {
            var module = new YLCoreLifecycleModule { SeedMvc = true };
            var request = StartOwned(module);
            request.AddCompleteEvent(_ => module.Events.Add("request.complete"));
            yield return AwaitStartup(request);
            Assert.That(module.Events, Is.EqualTo(new[] { "module.start", "model.init", "controller.init", "request.complete" }));
            var oldModel = module.StartedModel;
            var oldController = module.StartedController;
            Assert.That(oldModel.Module, Is.SameAs(module));
            Assert.That(oldController.Module, Is.SameAs(module));
            module.Close();

            Assert.That(module.Events, Is.EqualTo(new[]
            {
                "module.start", "model.init", "controller.init", "request.complete",
                "module.close", "controller.exit", "model.exit"
            }));
            Assert.That(oldModel.ExitCount, Is.EqualTo(1), "The single close pipeline must not notify a child twice.");
            Assert.That(oldController.ExitCount, Is.EqualTo(1));
            Assert.That(oldModel.Module, Is.Null);
            Assert.That(oldController.Module, Is.Null);
            Assert.That(module.RegisteredAtClose, Is.True);
            Assert.That(YLFramework.Core.ModuleManager.GetModule(module.ModuleName), Is.Null);
            AssertIndexesEmpty(module);

            module.SeedMvc = false;
            yield return AwaitStartup(StartOwned(module));
            Assert.That(module.StartCount, Is.EqualTo(2));
            AssertIndexesEmpty(module);
            var newModel = new YLCoreProbeModel { Id = 101 };
            module.AddModel(newModel);
            var newController = module.LoadController<YLCoreProbeController>();
            Assert.That(module.GetModel<YLCoreProbeModel>(101), Is.SameAs(newModel));
            Assert.That(newController, Is.Not.SameAs(oldController));
            Assert.That(newController.InitCount, Is.EqualTo(1));
            module.Close();
            Assert.That(newModel.ExitCount, Is.EqualTo(1));
            Assert.That(newController.ExitCount, Is.EqualTo(1));
            Assert.That(oldModel.ExitCount, Is.EqualTo(1));
            Assert.That(oldController.ExitCount, Is.EqualTo(1));
            Assert.That(module.CloseCount, Is.EqualTo(2));
            AssertIndexesEmpty(module);
        }

        [UnityTest]
        public IEnumerator ClosingKeepsQueriesButRejectsNewObjectsAndRecursiveCloseIsIdempotent()
        {
            var module = new YLCoreLifecycleModule { SeedMvc = true };
            yield return AwaitStartup(StartOwned(module));
            var model = module.StartedModel;
            var controller = module.StartedController;
            module.CloseAction = () =>
            {
                Assert.That(module.State, Is.EqualTo(ModuleState.Closing));
                Assert.That(module.GetModel<YLCoreProbeModel>(101), Is.SameAs(model));
                Assert.That(module.GetController<YLCoreProbeController>(), Is.SameAs(controller));
                Assert.That(module.LoadController<YLCoreProbeController>(), Is.SameAs(controller));
                Assert.Throws<InvalidOperationException>(() => module.AddModel(new YLCoreProbeModel()));
                int constructions = YLCoreConstantHashModel.ConstructionCount;
                Assert.Throws<InvalidOperationException>(() => module.LoadModel<YLCoreConstantHashModel>());
                Assert.That(YLCoreConstantHashModel.ConstructionCount, Is.EqualTo(constructions));
                Assert.Throws<InvalidOperationException>(() => module.AddController(new YLCoreProbeController()));
                Assert.Throws<InvalidOperationException>(() => module.CreateController<YLCoreProbeController>());
                Assert.Throws<InvalidOperationException>(() => module.LoadView<YLCoreSmokeView>((GameObject)null, loadViewArgs: null));
                Assert.Throws<InvalidOperationException>(() => module.LoadViewAsync<YLCoreSmokeView>("blocked", loadViewArgs: null));
                Assert.Throws<InvalidOperationException>(() => module.LoadPanel<YLCoreSmokePanel>("blocked", loadViewArgs: null));
                Assert.Throws<InvalidOperationException>(() => ModuleManager.StartUpModule(module));
                Assert.Throws<InvalidOperationException>(() => ModuleManager.Shutdown());
                module.Close();
            };
            controller.ExitAction = _ => module.Close();
            model.ExitAction = _ => module.Close();
            module.Close();
            module.Close();
            Assert.That(module.CloseCount, Is.EqualTo(1));
            Assert.That(controller.ExitCount, Is.EqualTo(1));
            Assert.That(model.ExitCount, Is.EqualTo(1));
            Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
            AssertIndexesEmpty(module);
            Assert.Throws<InvalidOperationException>(() => module.LoadModel<YLCoreProbeModel>());
        }

        [UnityTest]
        public IEnumerator CreatedModuleCannotStartInsideAnExistingObjectTransaction()
        {
            var module = new YLCoreLifecycleModule();
            var model = new YLCoreProbeModel
            {
                InitAction = _ =>
                {
                    Assert.Throws<InvalidOperationException>(() => StartOwned(module));
                    Assert.That(module.State, Is.EqualTo(ModuleState.Created));
                    Assert.That(ModuleManager.GetModule(module.ModuleName), Is.Null);
                }
            };
            try
            {
                module.AddModel(model);
            }
            finally
            {
                module.RemoveModel(model);
            }
            Assert.That(model.ExitCount, Is.EqualTo(1));
            yield return AwaitStartup(StartOwned(module));
            Assert.That(module.State, Is.EqualTo(ModuleState.Running));
        }

        [UnityTest]
        public IEnumerator ObjectTransactionsCannotCloseTheirOwningModule()
        {
            var module = new YLCoreLifecycleModule();
            yield return AwaitStartup(StartOwned(module));
            var model = new YLCoreProbeModel
            {
                InitAction = _ => Assert.Throws<InvalidOperationException>(() => module.Close()),
                ExitAction = _ => Assert.Throws<InvalidOperationException>(() => module.Close())
            };
            module.AddModel(model);
            module.RemoveModel(model);
            Assert.That(module.State, Is.EqualTo(ModuleState.Running));
            Assert.That(model.ExitCount, Is.EqualTo(1));
            Assert.That(model.Module, Is.Null);
        }

        [UnityTest]
        public IEnumerator CloseCollectsHookAndSiblingErrorsBeforeReportingEndedOwnership()
        {
            var module = new YLCoreLifecycleModule { SeedMvc = true };
            yield return AwaitStartup(StartOwned(module));
            var first = module.StartedModel;
            var sibling = new YLCoreProbeModel { Id = 102 };
            module.AddModel(sibling);
            var controller = module.StartedController;
            var hookError = new InvalidOperationException("hook");
            var controllerError = new InvalidOperationException("controller");
            var modelError = new InvalidOperationException("model");
            module.CloseAction = () => throw hookError;
            controller.ExitAction = _ => throw controllerError;
            first.ExitAction = _ => throw modelError;
            var error = Assert.Throws<AggregateException>(() => module.Close());
            Assert.That(error.Flatten().InnerExceptions, Is.EquivalentTo(new[] { hookError, controllerError, modelError }));
            Assert.That(first.RegisteredAtExit, Is.True);
            Assert.That(controller.OwnerAtExit, Is.SameAs(module));
            Assert.That(first.Module, Is.Null);
            Assert.That(sibling.Module, Is.Null);
            Assert.That(controller.Module, Is.Null);
            Assert.That(first.ExitCount, Is.EqualTo(1));
            Assert.That(sibling.ExitCount, Is.EqualTo(1));
            Assert.That(controller.ExitCount, Is.EqualTo(1));
            Assert.That(module.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(ModuleManager.GetModule(module.ModuleName), Is.Null);
            AssertIndexesEmpty(module);
            module.Close();
            Assert.That(module.CloseCount, Is.EqualTo(1));
            module.CloseAction = null;
            module.SeedMvc = false;
            yield return AwaitStartup(StartOwned(module));
            AssertIndexesEmpty(module);
        }

        private YLFramework.Core.StartUpModuleRequest StartOwned(YLFramework.Core.Module module)
        {
            var registered = YLFramework.Core.ModuleManager.GetModule(module.ModuleName);
            Assert.That(registered == null || ReferenceEquals(registered, module), Is.True,
                "Never replace or close an externally registered fixture module.");
            Assert.That(!YLFramework.Core.ModuleManager.AllBaseModules.TryGetValue(module.ModuleName, out var baseModule) ||
                ReferenceEquals(baseModule, module), Is.True);
            if (!_ownedModules.Contains(module))
            {
                _ownedModules.Add(module);
            }

            var request = YLFramework.Core.ModuleManager.StartUpModule(module);
            Assert.That(request, Is.Not.Null);
            return request;
        }

        private static IEnumerator AwaitStartup(YLFramework.Core.StartUpModuleRequest request)
        {
            Assert.That(request, Is.Not.Null);
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!request.IsDone && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(request.IsDone, Is.True, "Module startup exceeded the five-second timeout.");
            Assert.That(request.Error, Is.Null.Or.Empty);
            // Core不依赖后端缓存释放；完成当帧即可关闭或重启。
        }

        private static void AssertIndexesEmpty(YLFramework.Core.Module module)
        {
            foreach (var name in new[] { "Models", "Controllers", "Views", "TypeViews" })
            {
                var field = typeof(YLFramework.Core.Module).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(field, Is.Not.Null, name);
                Assert.That(((IDictionary)field.GetValue(module)).Count, Is.Zero, name);
            }
        }

    }
}
