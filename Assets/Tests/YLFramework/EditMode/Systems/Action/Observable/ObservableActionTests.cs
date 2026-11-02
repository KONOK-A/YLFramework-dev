using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Actions;

namespace YLFramework.Tests.Actions
{
    public class ObservableActionTests
    {
        private readonly Dictionary<IAction, ulong> _owned = new Dictionary<IAction, ulong>();
        private readonly List<ObservableProbe> _sources = new List<ObservableProbe>();

        private sealed class SubscriptionProbe : IDisposable
        {
            public int Disposals;
            public Exception Failure;
            public Action DuringDispose;

            public void Dispose()
            {
                Disposals++;
                DuringDispose?.Invoke();
                if (Failure != null)
                {
                    throw Failure;
                }
            }
        }

        // Deliberately retains observers after disposal to exercise misbehaving external sources.
        private sealed class ObservableProbe : IObservable<int>, IDisposable
        {
            public readonly List<IObserver<int>> Observers = new List<IObserver<int>>();
            public readonly List<SubscriptionProbe> Tokens = new List<SubscriptionProbe>();
            public Action<IObserver<int>> DuringSubscribe;
            public Exception SubscribeFailure;
            public Exception DisposeFailure;
            public bool ReturnNull;
            public int SourceDisposals;

            public IDisposable Subscribe(IObserver<int> observer)
            {
                Observers.Add(observer);
                var token = new SubscriptionProbe { Failure = DisposeFailure };
                Tokens.Add(token);
                DuringSubscribe?.Invoke(observer);
                if (SubscribeFailure != null)
                {
                    throw SubscribeFailure;
                }

                return ReturnNull ? null : token;
            }

            public void Dispose() => SourceDisposals++;
        }

        private sealed class ProviderProbe
        {
            public ObservableProbe Source;
            public int Calls;
            public IObservable<int> GetSource()
            {
                Calls++;
                return Source;
            }
        }

        [SetUp]
        public void SetUp() => ActionQueue.Drain();

        [TearDown]
        public void TearDown()
        {
            foreach (var source in _sources)
            {
                source.DuringSubscribe = null;
                foreach (var token in source.Tokens)
                {
                    token.Failure = null;
                    token.DuringDispose = null;
                }
            }
            try
            {
                foreach (var lease in _owned)
                {
                    if (lease.Key.ActionId == lease.Value && !lease.Key.IsDeinitialized)
                    {
                        lease.Key.Deinit();
                    }
                }
            }
            finally
            {
                _owned.Clear();
                _sources.Clear();
                ActionQueue.Drain();
            }
        }

        private T Own<T>(T action) where T : IAction
        {
            _owned[action] = action.ActionId;
            return action;
        }

        private ObservableProbe Source()
        {
            var source = new ObservableProbe();
            _sources.Add(source);
            return source;
        }

        private UniRxAction<int> Action(ObservableProbe source) => Own(UniRxAction<int>.Allocate(() => source));

        private ISequence Composite(int kind)
        {
            switch (kind)
            {
                case 0:
                    return Own(ActionKit.Sequence());
                case 1:
                    return Own(ActionKit.Parallel());
                case 2:
                    return Own(ActionKit.Repeat(2));
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private static void ExpectError(Exception error) =>
            LogAssert.Expect(LogType.Exception, new Regex(Regex.Escape(error.GetType().Name + ": " + error.Message)));

        [Test]
        public void PublicContractUsesTheSharedRuntimeAndBclObservableProvider()
        {
            var type = typeof(UniRxAction<int>);
            Assert.That(typeof(UniRxAction<>).FullName, Is.EqualTo("YLFramework.Actions.UniRxAction`1"));
            Assert.That(type.IsPublic, Is.True);
            Assert.That(type.GetInterfaces(), Does.Contain(typeof(IAction)));
            Assert.That(type.Assembly.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
            Assert.That(typeof(ActionKitUniRxExtension).Assembly, Is.SameAs(type.Assembly));
            Assert.That(typeof(ActionKitUniRxExtension).FullName, Is.EqualTo("YLFramework.Actions.ActionKitUniRxExtension"));
            Assert.That(typeof(ActionKitUniRxExtension).IsPublic && typeof(ActionKitUniRxExtension).IsAbstract && typeof(ActionKitUniRxExtension).IsSealed, Is.True);
            Assert.That(typeof(IAction).Assembly.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
            Assert.That(GetType().Assembly.GetName().Name, Is.EqualTo("YLFramework.Tests.EditMode"));
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            var constructors = type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(constructors.Length, Is.EqualTo(1));
            Assert.That(constructors[0].IsPrivate, Is.True);
            Assert.That(constructors[0].GetParameters(), Is.Empty);

            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var methods = type.GetMethods(flags).Where(method => !method.IsSpecialName).ToArray();
            CollectionAssert.AreEquivalent(new[] { "Allocate", "Reset", "Deinit", "OnStart", "OnExecute", "OnFinish" }, methods.Select(method => method.Name));
            var allocate = type.GetMethod("Allocate", flags);
            Assert.That(allocate.IsStatic, Is.True);
            Assert.That(allocate.ReturnType, Is.EqualTo(type));
            Assert.That(allocate.GetParameters().Select(parameter => parameter.ParameterType), Is.EqualTo(new[] { typeof(Func<IObservable<int>>) }));
            Assert.That(allocate.GetParameters()[0].IsOptional, Is.False);
            foreach (var name in new[] { "Reset", "Deinit", "OnStart", "OnFinish" })
            {
                var method = type.GetMethod(name, flags);
                Assert.That(method.IsStatic, Is.False);
                Assert.That(method.ReturnType, Is.EqualTo(typeof(void)));
                Assert.That(method.GetParameters(), Is.Empty);
            }
            Assert.That(type.GetMethod("OnExecute", flags).ReturnType, Is.EqualTo(typeof(void)));
            Assert.That(type.GetMethod("OnExecute", flags).GetParameters().Select(parameter => parameter.ParameterType), Is.EqualTo(new[] { typeof(float) }));
            var properties = type.GetProperties(flags);
            CollectionAssert.AreEquivalent(new[] { "IsPaused", "IsDeinitialized", "ActionId", "Status" }, properties.Select(property => property.Name));
            foreach (var property in properties)
            {
                Assert.That(property.GetGetMethod().IsPublic && property.GetSetMethod().IsPublic, Is.True);
                var expected = property.Name == "ActionId" ? typeof(ulong) : property.Name == "Status" ? typeof(ActionStatus) : typeof(bool);
                Assert.That(property.PropertyType, Is.EqualTo(expected));
            }

            var extensions = typeof(ActionKitUniRxExtension).GetMethods(flags);
            CollectionAssert.AreEquivalent(new[] { "ToAction", "UniRx" }, extensions.Select(method => method.Name));
            var toAction = typeof(ActionKitUniRxExtension).GetMethod("ToAction").MakeGenericMethod(typeof(int));
            var append = typeof(ActionKitUniRxExtension).GetMethod("UniRx").MakeGenericMethod(typeof(int));
            Assert.That(toAction.ReturnType, Is.EqualTo(typeof(IAction)));
            Assert.That(append.ReturnType, Is.EqualTo(typeof(ISequence)));
            Assert.That(toAction.GetParameters().Select(parameter => parameter.ParameterType), Is.EqualTo(new[] { typeof(IObservable<int>) }));
            Assert.That(append.GetParameters().Select(parameter => parameter.ParameterType), Is.EqualTo(new[] { typeof(ISequence), typeof(Func<IObservable<int>>) }));
            Assert.That(toAction.IsDefined(typeof(ExtensionAttribute), false), Is.True);
            Assert.That(append.IsDefined(typeof(ExtensionAttribute), false), Is.True);
            Assert.That(typeof(UniRxAction<>).GetGenericArguments()[0].GetGenericParameterConstraints(), Is.Empty);
            Assert.That(typeof(UniRxAction<>).GetGenericArguments()[0].GenericParameterAttributes, Is.EqualTo(GenericParameterAttributes.None));
            foreach (var method in extensions)
            {
                Assert.That(method.IsStatic && method.IsGenericMethodDefinition, Is.True);
                Assert.That(method.GetGenericArguments().Length, Is.EqualTo(1));
                Assert.That(method.GetGenericArguments()[0].GetGenericParameterConstraints(), Is.Empty);
                Assert.That(method.GetGenericArguments()[0].GenericParameterAttributes, Is.EqualTo(GenericParameterAttributes.None));
                Assert.That(method.GetParameters().Any(parameter => parameter.IsOptional), Is.False);
            }
            Assert.That(type.Assembly.GetReferencedAssemblies().All(reference =>
                new[] { "netstandard", "mscorlib", "System", "System.Core", "Newtonsoft.Json", "BouncyCastle.Cryptography" }.Contains(reference.Name) ||
                reference.Name.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                reference.Name.StartsWith("UnityEditor.", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public void NullArgumentsAreRejectedBeforeAllocatingAnAction()
        {
            Assert.Throws<ArgumentNullException>(() => UniRxAction<int>.Allocate(null));
            Assert.Throws<ArgumentNullException>(() => ActionKitUniRxExtension.ToAction<int>(null));
            Assert.Throws<ArgumentNullException>(() => ActionKitUniRxExtension.UniRx<int>(null, () => Source()));
            var sequence = Own(ActionKit.Sequence());
            Assert.Throws<ArgumentNullException>(() => sequence.UniRx<int>(null));
            Assert.That(ActionQueue.PendingCount, Is.Zero);
        }

        [Test]
        public void ExtensionsRemainLazyAndAppendReturnsTheOriginalSequence()
        {
            var source = Source();
            var direct = Own(source.ToAction());
            Assert.That(direct, Is.TypeOf<UniRxAction<int>>());
            var calls = 0;
            var sequence = Own(ActionKit.Sequence());
            Assert.That(sequence.UniRx(() =>
            {
                calls++;
                return source;
            }), Is.SameAs(sequence));
            Assert.That(calls, Is.Zero);
            Assert.That(source.Observers, Is.Empty);
            Assert.That(sequence.Execute(0), Is.False);
            Assert.That(calls, Is.EqualTo(1));
            source.Observers[0].OnNext(1);
            Assert.That(sequence.Execute(0), Is.True);
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(source.SourceDisposals, Is.Zero);
        }

        [Test]
        public void SynchronousFirstValueFinishesBeforeSubscribeReturnsAndDisposesOnce()
        {
            var source = Source();
            var action = Action(source);
            source.DuringSubscribe = observer =>
            {
                observer.OnNext(42);
                Assert.That(action.Status, Is.EqualTo(ActionStatus.Finished));
                Assert.That(source.Tokens[0].Disposals, Is.Zero);
                observer.OnNext(43);
                observer.OnCompleted();
                observer.OnError(new InvalidOperationException("ignored after first value"));
            };
            Assert.That(action.Execute(0), Is.True);
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            action.OnFinish();
            action.Reset();
            action.Deinit();
            action.Deinit();
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SynchronousEmptyCompletionOrErrorCancelsBeforeHandleIsReturned(bool faulted)
        {
            var source = Source();
            var action = Action(source);
            var failure = new InvalidOperationException("synchronous observable failure");
            if (faulted)
            {
                ExpectError(failure);
            }

            source.DuringSubscribe = observer =>
            {
                if (faulted)
                {
                    observer.OnError(failure);
                }
                else
                {
                    observer.OnCompleted();
                }

                Assert.That(action.IsDeinitialized, Is.True);
                Assert.That(source.Tokens[0].Disposals, Is.Zero);
                observer.OnNext(1);
                observer.OnCompleted();
                observer.OnError(failure);
            };
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
            Assert.That(source.SourceDisposals, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void FirstNotificationWinsOverRepeatedAndOutOfOrderTerminals(int first)
        {
            var source = Source();
            var action = Action(source);
            Assert.That(action.Execute(0), Is.False);
            var observer = source.Observers[0];
            var failure = new InvalidOperationException("first terminal wins");
            if (first == 0)
            {
                observer.OnNext(1);
            }
            else if (first == 1)
            {
                observer.OnCompleted();
            }
            else
            {
                ExpectError(failure);
                observer.OnError(failure);
            }
            observer.OnNext(2);
            observer.OnCompleted();
            observer.OnError(failure);
            observer.OnError(null);
            Assert.That(action.IsDeinitialized, Is.EqualTo(first != 0));
            Assert.That(action.Status == ActionStatus.Finished, Is.EqualTo(first == 0));
            if (first == 0)
            {
                Assert.That(action.Execute(0), Is.True);
            }
            else
            {
                action.OnExecute(0);
            }

            action.OnFinish();
            action.Deinit();
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(source.SourceDisposals, Is.Zero);
        }

        [Test]
        public void NullErrorIsLoggedAndCancelsInsteadOfCompleting()
        {
            var source = Source();
            var action = Action(source);
            action.Execute(0);
            LogAssert.Expect(LogType.Exception, new Regex("ArgumentNullException"));
            source.Observers[0].OnError(null);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FactoryReentrantResetOrCancellationNeverSubscribesTheReturnedSource(bool cancel)
        {
            var source = Source();
            UniRxAction<int> action = null;
            var calls = 0;
            action = Own(UniRxAction<int>.Allocate(() =>
            {
                calls++;
                if (calls == 1)
                {
                    if (cancel)
                    {
                        action.Deinit();
                    }
                    else
                    {
                        action.Reset();
                    }
                }
                return source;
            }));
            action.OnStart();
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(source.Observers, Is.Empty);
            Assert.That(action.IsDeinitialized, Is.EqualTo(cancel));
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
            if (!cancel)
            {
                action.OnStart();
                Assert.That(calls, Is.EqualTo(2));
                Assert.That(source.Observers.Count, Is.EqualTo(1));
                source.Observers[0].OnNext(1);
                Assert.That(action.Status, Is.EqualTo(ActionStatus.Finished));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SubscribeReentrantResetOrCancellationDisposesTheLateHandleExactlyOnce(bool cancel)
        {
            var source = Source();
            var action = Action(source);
            source.DuringSubscribe = observer =>
            {
                if (cancel)
                {
                    action.Deinit();
                }
                else
                {
                    action.Reset();
                }

                Assert.That(source.Tokens[0].Disposals, Is.Zero);
            };
            action.OnStart();
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(action.IsDeinitialized, Is.EqualTo(cancel));
            source.Observers[0].OnNext(1);
            source.Observers[0].OnCompleted();
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
            action.OnFinish();
            action.Deinit();
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
        }

        [Test]
        public void FailedLateHandleCleanupAfterCancellationDiscardsWithoutRetrying()
        {
            var source = Source();
            var failure = new InvalidOperationException("late handle cleanup");
            source.DisposeFailure = failure;
            var action = Action(source);
            source.DuringSubscribe = observer => action.Deinit();
            Assert.That(Assert.Throws<InvalidOperationException>(action.OnStart), Is.SameAs(failure));
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            ActionQueue.Drain();
            var replacement = Action(Source());
            Assert.That(replacement, Is.Not.SameAs(action));
            action.Deinit();
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(source.SourceDisposals, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ResetDuringFactoryOrSubscribeRestartsThroughTheRealExecuteProtocol(bool resetInFactory)
        {
            var source = Source();
            UniRxAction<int> action = null;
            var calls = 0;
            var reset = false;
            source.DuringSubscribe = observer =>
            {
                if (resetInFactory || reset)
                {
                    return;
                }

                reset = true;
                action.Reset();
            };
            action = Own(UniRxAction<int>.Allocate(() =>
            {
                calls++;
                if (resetInFactory && !reset)
                {
                    reset = true;
                    action.Reset();
                }
                return source;
            }));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(source.Observers.Count, Is.EqualTo(resetInFactory ? 0 : 1));
            if (!resetInFactory)
            {
                Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            }

            Assert.That(action.Execute(0), Is.False);
            Assert.That(calls, Is.EqualTo(2), "Reset during startup must restart the factory, not leave Started with no subscription.");
            Assert.That(source.Observers.Count, Is.EqualTo(resetInFactory ? 1 : 2));
            source.Observers.Last().OnNext(1);
            Assert.That(action.Execute(0), Is.True);
            foreach (var token in source.Tokens)
            {
                Assert.That(token.Disposals, Is.EqualTo(1));
            }

            Assert.That(source.SourceDisposals, Is.Zero);
        }

        [Test]
        public void ResetAndNestedStartBeforeOldSubscribeReturnsPreservesTheNewSlot()
        {
            var first = Source();
            var second = Source();
            var calls = 0;
            var action = Own(UniRxAction<int>.Allocate(() => ++calls == 1 ? first : second));
            first.DuringSubscribe = observer =>
            {
                action.Reset();
                action.OnStart();
                observer.OnNext(1);
            };
            action.OnStart();
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(first.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(second.Tokens[0].Disposals, Is.Zero);
            first.Observers[0].OnError(new InvalidOperationException("old reset slot"));
            second.Observers[0].OnNext(2);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Finished));
            Assert.That(action.Execute(0), Is.True);
            Assert.That(second.Tokens[0].Disposals, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CancelAndPoolReuseBeforeOldHandleReturnsCannotTouchTheNewLease(bool throwingHandle)
        {
            var first = Source();
            var second = Source();
            var failure = new InvalidOperationException("late handle disposal failure");
            if (throwingHandle)
            {
                first.DisposeFailure = failure;
            }

            var original = Action(first);
            var oldId = original.ActionId;
            UniRxAction<int> replacement = null;
            first.DuringSubscribe = observer =>
            {
                original.Deinit();
                ActionQueue.Drain();
                replacement = Action(second);
                Assert.That(replacement, Is.SameAs(original));
                Assert.That(replacement.ActionId, Is.Not.EqualTo(oldId));
                replacement.Execute(0);
                observer.OnNext(1);
            };
            if (throwingHandle)
            {
                Assert.That(Assert.Throws<InvalidOperationException>(() => original.OnStart()), Is.SameAs(failure));
            }
            else
            {
                original.OnStart();
            }

            Assert.That(first.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(second.Tokens[0].Disposals, Is.Zero);
            Assert.That(replacement.IsDeinitialized, Is.False);
            Assert.That(replacement.Status, Is.EqualTo(ActionStatus.Started));
            first.Observers[0].OnCompleted();
            first.Observers[0].OnError(failure);
            second.Observers[0].OnNext(2);
            Assert.That(replacement.Execute(0), Is.True);
            Assert.That(second.Tokens[0].Disposals, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OldExecuteCannotChangeAnUnstartedLeaseReusedInsideFactoryOrSubscribe(bool cancelInFactory)
        {
            var first = Source();
            var second = Source();
            UniRxAction<int> original = null;
            UniRxAction<int> replacement = null;
            Action replace = () =>
            {
                original.Deinit();
                ActionQueue.Drain();
                replacement = Action(second);
            };
            first.DuringSubscribe = observer =>
            {
                replace();
                observer.OnNext(1);
            };
            original = Own(UniRxAction<int>.Allocate(() =>
            {
                if (cancelInFactory)
                {
                    replace();
                }

                return first;
            }));
            var id = original.ActionId;
            Assert.That(original.Execute(0), Is.False);
            Assert.That(replacement, Is.SameAs(original));
            Assert.That(replacement.ActionId, Is.Not.EqualTo(id));
            Assert.That(replacement.Status, Is.EqualTo(ActionStatus.NotStart), "An old Execute stack must not write Started into a new ActionId.");
            Assert.That(replacement.IsDeinitialized, Is.False);
            Assert.That(second.Observers, Is.Empty);
            if (!cancelInFactory)
            {
                Assert.That(first.Tokens[0].Disposals, Is.EqualTo(1));
            }

            Assert.That(replacement.Execute(0), Is.False);
            Assert.That(second.Observers.Count, Is.EqualTo(1));
            second.Observers[0].OnNext(2);
            Assert.That(replacement.Execute(0), Is.True);
            Assert.That(second.Tokens[0].Disposals, Is.EqualTo(1));
        }

        [Test]
        public void OldObserverAfterResetCannotCompleteOrCancelTheNextSubscription()
        {
            var source = Source();
            var action = Action(source);
            action.Execute(0);
            var old = source.Observers[0];
            action.IsPaused = true;
            action.Reset();
            Assert.That(action.IsPaused, Is.False);
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            action.Execute(0);
            old.OnNext(1);
            old.OnCompleted();
            old.OnError(new InvalidOperationException("old subscription"));
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(source.Tokens[1].Disposals, Is.Zero);
            source.Observers[1].OnNext(2);
            Assert.That(action.Execute(0), Is.True);
            Assert.That(source.Tokens[1].Disposals, Is.EqualTo(1));
        }

        [Test]
        public void CancelledObjectIsOnlyReusableAfterDrainAndLateObserverCannotCrossActionId()
        {
            var first = Source();
            var original = Action(first);
            var id = original.ActionId;
            original.Execute(0);
            original.Deinit();
            var other = Action(Source());
            Assert.That(other, Is.Not.SameAs(original));
            ActionQueue.Drain();
            var next = Source();
            var reused = Action(next);
            Assert.That(reused, Is.SameAs(original));
            Assert.That(reused.ActionId, Is.Not.EqualTo(id));
            reused.Execute(0);
            first.Observers[0].OnNext(1);
            first.Observers[0].OnCompleted();
            first.Observers[0].OnError(new InvalidOperationException("old pooled lease"));
            Assert.That(reused.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(reused.IsDeinitialized, Is.False);
            Assert.That(next.Tokens[0].Disposals, Is.Zero);
            next.Observers[0].OnNext(2);
            Assert.That(reused.Execute(0), Is.True);
        }

        [Test]
        public void ASlotCannotDeliverAcrossAnExplicitActionIdChange()
        {
            var source = Source();
            var action = Action(source);
            action.Execute(0);
            action.ActionId++;
            Own(action);
            source.Observers[0].OnNext(1);
            action.OnExecute(0);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(source.Tokens[0].Disposals, Is.Zero);
            action.Reset();
            action.Execute(0);
            source.Observers[0].OnCompleted();
            source.Observers[1].OnNext(2);
            Assert.That(action.Execute(0), Is.True);
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(source.Tokens[1].Disposals, Is.EqualTo(1));
        }

        [Test]
        public void DeinitClearsTheGetterAndSubscriptionInsteadOfRetainingTheProvider()
        {
            var provider = new ProviderProbe { Source = Source() };
            var action = Own(UniRxAction<int>.Allocate(provider.GetSource));
            var getter = typeof(UniRxAction<int>).GetField("_observableGetter", BindingFlags.NonPublic | BindingFlags.Instance);
            var subscription = typeof(UniRxAction<int>).GetField("_subscription", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(((Delegate)getter.GetValue(action)).Target, Is.SameAs(provider));
            action.Execute(0);
            action.Deinit();
            Assert.That(getter.GetValue(action), Is.Null);
            Assert.That(subscription.GetValue(action), Is.Null);
            Assert.That(provider.Calls, Is.EqualTo(1));
            Assert.That(provider.Source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(provider.Source.SourceDisposals, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void FinishResetAndDeinitDisposeOnlyTheSubscriptionEvenIfItThrows(int operation)
        {
            var source = Source();
            var failure = new InvalidOperationException("subscription disposal failure");
            source.DisposeFailure = failure;
            var action = Action(source);
            var id = action.ActionId;
            action.Execute(0);
            if (operation == 0)
            {
                source.Observers[0].OnNext(1);
            }

            TestDelegate close;
            if (operation == 0)
            {
                close = () =>
                {
                    action.Execute(0);
                };
            }
            else if (operation == 1)
            {
                close = action.Reset;
            }
            else
            {
                close = action.Deinit;
            }

            Assert.That(Assert.Throws<InvalidOperationException>(close), Is.SameAs(failure));
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            action.OnFinish();
            action.Reset();
            action.Deinit();
            action.Deinit();
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(source.SourceDisposals, Is.Zero);
            ActionQueue.Drain();
            var reused = Action(Source());
            Assert.That(reused, Is.Not.SameAs(action), "订阅释放失败的动作不得重新交付。");
            Assert.That(reused.ActionId, Is.Not.EqualTo(id));
            Assert.That(reused.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(reused.IsDeinitialized, Is.False);
        }

        [Test]
        public void ErrorStillLogsTheOriginalFaultWhenSubscriptionDisposalThrows()
        {
            var source = Source();
            var original = new ArgumentException("observable error during cleanup");
            var cleanup = new InvalidOperationException("error cleanup failure");
            source.DisposeFailure = cleanup;
            var action = Action(source);
            action.Execute(0);
            ExpectError(original);
            Assert.That(Assert.Throws<InvalidOperationException>(() => source.Observers[0].OnError(original)), Is.SameAs(cleanup));
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
            action.Deinit();
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(source.SourceDisposals, Is.Zero);
        }

        [Test]
        public void ExecutorPropagatesFinishDisposalFailureAndDiscardsTheActionButRecyclesTheController()
        {
            var source = Source();
            var failure = new InvalidOperationException("executor finish disposal failure");
            source.DisposeFailure = failure;
            var action = Action(source);
            var id = action.ActionId;
            var root = new GameObject("ActionKit.Observable.EditModeExecutor");
            IActionController controller = null;
            try
            {
                var executor = root.AddComponent<MonoUpdateActionExecutor>();
                controller = ActionController.Allocate();
                controller.Action = action;
                controller.ActionId = id;
                var callbacks = 0;
                executor.Execute(controller, _ => callbacks++);
                source.Observers[0].OnNext(1);
                Assert.That(Assert.Throws<InvalidOperationException>(() => executor.Tick(0, 0)), Is.SameAs(failure));
                Assert.That(action.IsDeinitialized, Is.True);
                Assert.That(controller.Action, Is.Null);
                Assert.That(executor.ScheduledCount, Is.Zero);
                Assert.That(callbacks, Is.Zero);
                Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
                Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
                ActionQueue.Drain();
                var reused = Action(Source());
                Assert.That(reused, Is.Not.SameAs(action));
                Assert.That(reused.ActionId, Is.Not.EqualTo(id));
                Assert.That(reused.IsDeinitialized, Is.False);
                Assert.That(source.SourceDisposals, Is.Zero);
            }
            finally
            {
                foreach (var token in source.Tokens)
                {
                    token.Failure = null;
                }

                if (root)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }

                if (controller != null && controller.Action != null)
                {
                    controller.Recycle();
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FactoryAndSubscribeFailuresPropagateTheOriginalCallerFault(bool subscribe)
        {
            var source = Source();
            var failure = new InvalidOperationException("observable caller fault");
            if (subscribe)
            {
                source.SubscribeFailure = failure;
            }

            var action = Own(UniRxAction<int>.Allocate(() =>
            {
                if (!subscribe)
                {
                    throw failure;
                }

                return source;
            }));
            Assert.That(Assert.Throws<InvalidOperationException>(() => action.Execute(0)), Is.SameAs(failure));
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(source.Observers.Count, Is.EqualTo(subscribe ? 1 : 0));
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
            Assert.That(source.SourceDisposals, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NullFactorySourceAndNullSubscriptionAreCallerFaultsNotSuccessfulActions(bool nullToken)
        {
            var source = Source();
            source.ReturnNull = nullToken;
            var action = Own(UniRxAction<int>.Allocate(() => nullToken ? source : null));
            Assert.Throws<InvalidOperationException>(() => action.Execute(0));
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(source.Observers.Count, Is.EqualTo(nullToken ? 1 : 0));
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
            Assert.That(source.SourceDisposals, Is.Zero);
        }

        [Test]
        public void FactoryFailureAfterNestedResetDoesNotCancelTheReplacementSlot()
        {
            var fresh = Source();
            var failure = new InvalidOperationException("old factory fault after reset");
            UniRxAction<int> action = null;
            var calls = 0;
            action = Own(UniRxAction<int>.Allocate(() =>
            {
                if (++calls == 1)
                {
                    action.Reset();
                    action.OnStart();
                    throw failure;
                }
                return fresh;
            }));
            Assert.That(Assert.Throws<InvalidOperationException>(() => action.OnStart()), Is.SameAs(failure));
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(fresh.Tokens[0].Disposals, Is.Zero);
            fresh.Observers[0].OnNext(1);
            Assert.That(action.Execute(0), Is.True);
            Assert.That(fresh.Tokens[0].Disposals, Is.EqualTo(1));
        }

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        [TestCase(2, true)]
        public void ObservableEmptyCompletionOrErrorCancelsSequenceParallelAndRepeat(int kind, bool faulted)
        {
            var source = Source();
            var siblingSource = Source();
            var child = Action(source);
            var sibling = Action(siblingSource);
            var callbacks = 0;
            var tail = Own(ActionKit.Sequence()).Append(sibling).Callback(() => callbacks++);
            var parent = Composite(kind).Append(child).Append(tail);
            Assert.That(parent.Execute(0), Is.False);
            Assert.That(parent.Execute(0), Is.False);
            Assert.That(source.Observers.Count, Is.EqualTo(1));
            if (faulted)
            {
                var failure = new InvalidOperationException("observable child failed");
                ExpectError(failure);
                source.Observers[0].OnError(failure);
            }
            else
            {
                source.Observers[0].OnCompleted();
            }

            Assert.That(parent.Execute(0), Is.False);
            Assert.That(parent.IsDeinitialized, Is.True);
            Assert.That(parent.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(child.IsDeinitialized, Is.True);
            Assert.That(sibling.IsDeinitialized, Is.True);
            Assert.That(tail.IsDeinitialized, Is.True);
            Assert.That(callbacks, Is.Zero);
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            foreach (var token in siblingSource.Tokens)
            {
                Assert.That(token.Disposals, Is.EqualTo(1));
            }

            Assert.That(source.SourceDisposals + siblingSource.SourceDisposals, Is.Zero);
        }

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        [TestCase(2, true)]
        public void CompositeRetainsCancellationAcrossChildPoolReuseWithoutTouchingTheNewLease(int kind, bool faulted)
        {
            var first = Source();
            var child = Action(first);
            var id = child.ActionId;
            var parent = Composite(kind).Append(child);
            parent.Execute(0);
            parent.Execute(0);
            if (faulted)
            {
                var failure = new InvalidOperationException("child cancelled before parent tick");
                ExpectError(failure);
                first.Observers[0].OnError(failure);
            }
            else
            {
                first.Observers[0].OnCompleted();
            }

            Assert.That(child.IsDeinitialized, Is.True);
            ActionQueue.Drain();
            var second = Source();
            var replacement = Action(second);
            Assert.That(replacement, Is.SameAs(child));
            Assert.That(replacement.ActionId, Is.Not.EqualTo(id));
            Assert.That(parent.Execute(0), Is.False);
            Assert.That(parent.IsDeinitialized, Is.True, "A replaced child lease is still cancellation of the parent's original child.");
            Assert.That(parent.Status, Is.Not.EqualTo(ActionStatus.Finished));
            parent.Deinit();
            Assert.That(replacement.IsDeinitialized, Is.False);
            Assert.That(replacement.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(second.Observers, Is.Empty);
            Assert.That(first.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(replacement.Execute(0), Is.False);
            second.Observers[0].OnNext(2);
            Assert.That(replacement.Execute(0), Is.True);
            Assert.That(second.Tokens[0].Disposals, Is.EqualTo(1));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void CancellingACompositeDisposesEveryStartedObservableChild(int kind)
        {
            var source = Source();
            var child = Action(source);
            var parent = Composite(kind).Append(child);
            parent.Execute(0);
            parent.Execute(0);
            parent.Deinit();
            parent.Deinit();
            Assert.That(child.IsDeinitialized, Is.True);
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            source.Observers[0].OnNext(1);
            Assert.That(parent.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(source.SourceDisposals, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void CompositeResetAllowsReentrantAppendButResetsOnlyItsOriginalSnapshot(int kind)
        {
            var firstSource = Source();
            var laterSource = Source();
            var appendedSource = Source();
            var first = Action(firstSource);
            var later = Action(laterSource);
            var appended = Action(appendedSource);
            var parent = Composite(kind).Append(first).Append(later);
            // Keep the reentry in Reset, not Sequence.OnStart's separate child reset.
            first.Execute(0);
            later.Execute(0);
            appended.Execute(0);
            appended.IsPaused = true;
            var appends = 0;
            firstSource.Tokens[0].DuringDispose = () =>
            {
                appends++;
                Assert.That(parent.Append(appended), Is.SameAs(parent));
            };

            Assert.DoesNotThrow(() => parent.Reset());
            Assert.That(appends, Is.EqualTo(1));
            Assert.That(parent.IsDeinitialized, Is.False);
            Assert.That(parent.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(firstSource.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(laterSource.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(first.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(later.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(appendedSource.Tokens[0].Disposals, Is.Zero);
            Assert.That(appended.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(appended.IsPaused, Is.True);

            parent.Reset();
            Assert.That(appendedSource.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(appended.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(appended.IsPaused, Is.False);
            Assert.That(appends, Is.EqualTo(1));
            Assert.That(firstSource.SourceDisposals + laterSource.SourceDisposals + appendedSource.SourceDisposals, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void CompositeResetCancelsMatchingChildrenWithoutResettingAReusedLaterChild(int kind)
        {
            var firstSource = Source();
            var laterSource = Source();
            var untouchedSource = Source();
            var freshSource = Source();
            var first = Action(firstSource);
            var later = Action(laterSource);
            var untouched = Action(untouchedSource);
            var laterId = later.ActionId;
            var parent = Composite(kind).Append(first).Append(later).Append(untouched);
            first.Execute(0);
            later.Execute(0);
            untouched.Execute(0);
            UniRxAction<int> replacement = null;
            firstSource.Tokens[0].DuringDispose = () =>
            {
                later.Deinit();
                ActionQueue.Drain();
                replacement = Action(freshSource);
                Assert.That(replacement, Is.SameAs(later));
                Assert.That(replacement.ActionId, Is.Not.EqualTo(laterId));
                Assert.That(replacement.Execute(0), Is.False);
            };

            Assert.DoesNotThrow(() => parent.Reset());
            Assert.That(parent.IsDeinitialized, Is.True);
            Assert.That(parent.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(first.IsDeinitialized, Is.True);
            Assert.That(untouched.IsDeinitialized, Is.True);
            Assert.That(firstSource.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(laterSource.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(untouchedSource.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(replacement.IsDeinitialized, Is.False);
            Assert.That(replacement.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(freshSource.Observers.Count, Is.EqualTo(1));
            Assert.That(freshSource.Tokens[0].Disposals, Is.Zero);
            parent.Deinit();
            Assert.That(freshSource.Tokens[0].Disposals, Is.Zero);
            freshSource.Observers[0].OnNext(1);
            Assert.That(replacement.Execute(0), Is.True);
            Assert.That(freshSource.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(firstSource.SourceDisposals + laterSource.SourceDisposals + untouchedSource.SourceDisposals + freshSource.SourceDisposals, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void OldCompositeResetStopsAfterItsParentAndLaterChildAreReusedDuringDispose(int kind)
        {
            var firstSource = Source();
            var laterSource = Source();
            var freshSource = Source();
            var first = Action(firstSource);
            var later = Action(laterSource);
            var parent = Composite(kind).Append(first).Append(later);
            var parentId = parent.ActionId;
            var laterId = later.ActionId;
            first.Execute(0);
            later.Execute(0);
            ISequence replacementParent = null;
            UniRxAction<int> replacementChild = null;
            firstSource.Tokens[0].DuringDispose = () =>
            {
                parent.Deinit();
                ActionQueue.Drain();
                replacementParent = Composite(kind);
                replacementChild = Action(freshSource);
                Assert.That(replacementParent, Is.SameAs(parent));
                Assert.That(replacementParent.ActionId, Is.Not.EqualTo(parentId));
                Assert.That(replacementChild, Is.SameAs(later));
                Assert.That(replacementChild.ActionId, Is.Not.EqualTo(laterId));
                replacementParent.Append(replacementChild);
                Assert.That(replacementParent.Execute(0), Is.False);
                Assert.That(replacementParent.Execute(0), Is.False);
                replacementParent.IsPaused = true;
            };

            Assert.DoesNotThrow(() => parent.Reset());
            Assert.That(replacementParent.IsDeinitialized, Is.False);
            Assert.That(replacementParent.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(replacementParent.IsPaused, Is.True);
            Assert.That(replacementChild.IsDeinitialized, Is.False);
            Assert.That(replacementChild.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(firstSource.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(laterSource.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(freshSource.Observers.Count, Is.EqualTo(1));
            Assert.That(freshSource.Tokens[0].Disposals, Is.Zero);
            replacementParent.IsPaused = false;
            freshSource.Observers[0].OnNext(1);
            Assert.That(replacementParent.Execute(0), Is.True);
            Assert.That(freshSource.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(firstSource.SourceDisposals + laterSource.SourceDisposals + freshSource.SourceDisposals, Is.Zero);
        }

        [Test]
        public void RepeatInvokesTheFactoryAgainAndIgnoresThePreviousIterationObserver()
        {
            var iterations = new List<ObservableProbe>();
            var child = Own(UniRxAction<int>.Allocate(() =>
            {
                var source = Source();
                iterations.Add(source);
                return source;
            }));
            var repeat = Own(ActionKit.Repeat(2)).Append(child);
            Assert.That(repeat.Execute(0), Is.False);
            Assert.That(repeat.Execute(0), Is.False);
            iterations[0].Observers[0].OnNext(1);
            Assert.That(repeat.Execute(0), Is.False);
            Assert.That(iterations[0].Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(repeat.Execute(0), Is.False);
            Assert.That(iterations.Count, Is.EqualTo(2));
            iterations[0].Observers[0].OnNext(99);
            iterations[0].Observers[0].OnCompleted();
            iterations[0].Observers[0].OnError(new InvalidOperationException("previous repeat iteration"));
            Assert.That(child.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(child.IsDeinitialized, Is.False);
            iterations[1].Observers[0].OnNext(2);
            Assert.That(repeat.Execute(0), Is.True);
            Assert.That(iterations[1].Tokens[0].Disposals, Is.EqualTo(1));
            repeat.Deinit();
            foreach (var source in iterations)
            {
                Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
                Assert.That(source.SourceDisposals, Is.Zero);
            }
        }

        [Test]
        public void PausingTheActionDoesNotPauseExternalMainThreadNotifications()
        {
            var source = Source();
            var action = Action(source);
            action.Execute(0);
            action.IsPaused = true;
            source.Observers[0].OnNext(1);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Finished));
            Assert.That(action.IsPaused, Is.True);
            Assert.That(action.Execute(0), Is.True);
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
        }
    }
}
