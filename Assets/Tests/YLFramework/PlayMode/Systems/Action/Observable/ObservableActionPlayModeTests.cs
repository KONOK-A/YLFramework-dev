using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Actions;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Actions
{
    public sealed class ObservableActionTestHost : MonoBehaviour
    {
    }

    public class ObservableActionPlayModeTests
    {
        private readonly Dictionary<IAction, ulong> _owned = new Dictionary<IAction, ulong>();
        private readonly List<GameObject> _roots = new List<GameObject>();
        private ObservableActionTestHost _caller;
        private ActionRecycleDriver _previousRecycle;
        private SynchronizationContext _previousContext;
        private int _mainThread;
        private bool _ownsEnvironment;
        private bool _ownsActionSession;

        private sealed class SubscriptionProbe : IDisposable
        {
            public int Disposals;
            public int DisposeThread;
            public void Dispose()
            {
                Disposals++;
                DisposeThread = Thread.CurrentThread.ManagedThreadId;
            }
        }

        private sealed class ObservableProbe : IObservable<int>, IDisposable
        {
            public readonly List<IObserver<int>> Observers = new List<IObserver<int>>();
            public readonly List<SubscriptionProbe> Tokens = new List<SubscriptionProbe>();
            public Action<IObserver<int>> DuringSubscribe;
            public int SubscribeThread;
            public int SourceDisposals;

            public IDisposable Subscribe(IObserver<int> observer)
            {
                SubscribeThread = Thread.CurrentThread.ManagedThreadId;
                Observers.Add(observer);
                var token = new SubscriptionProbe();
                Tokens.Add(token);
                DuringSubscribe?.Invoke(observer);
                return token;
            }

            public void Dispose() => SourceDisposals++;
        }

        private sealed class MainThreadContext : SynchronizationContext
        {
            private readonly int _owner = Thread.CurrentThread.ManagedThreadId;
            private readonly Queue<Action> _pending = new Queue<Action>();
            public int Posts;
            public int PostThread;

            public override void Post(SendOrPostCallback callback, object state)
            {
                lock (_pending)
                {
                    Posts++;
                    PostThread = Thread.CurrentThread.ManagedThreadId;
                    _pending.Enqueue(() => callback(state));
                }
            }

            public void DeliverOnMainThread()
            {
                Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(_owner));
                Action[] pending;
                lock (_pending)
                {
                    pending = _pending.ToArray();
                    _pending.Clear();
                }
                foreach (var callback in pending)
                {
                    callback();
                }
            }
        }

        [SetUp]
        public void SetUp()
        {
            _ownsEnvironment = false;
            _ownsActionSession = false;
            var canOwnActionSession = ActionFixtureSession.CanOwnUninitializedSession();
            _previousContext = SynchronizationContext.Current;
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            Assert.That(Object.FindObjectsOfType<MonoUpdateActionExecutor>(true), Is.Empty, "Use an isolated test scene; this fixture must not stop another owner's actions.");
            Assert.That(Object.FindObjectsOfType<ActionGlobalHost>(true), Is.Empty);
            Assert.That(Object.FindObjectsOfType<ActionKitCurrentScene>(true), Is.Empty);
            _ownsEnvironment = true;
            _ownsActionSession = canOwnActionSession;
            ActionQueue.Drain();
            _previousRecycle = Object.FindObjectOfType<ActionRecycleDriver>(true);
            var root = new GameObject("ActionKit.Observable.TestCaller");
            _roots.Add(root);
            _caller = root.AddComponent<ObservableActionTestHost>();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_previousContext);
            if (!_ownsEnvironment)
            {
                yield break;
            }

            foreach (var root in _roots)
            {
                if (root)
                {
                    Object.Destroy(root);
                }
            }
            _roots.Clear();
            yield return null;
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
                ActionQueue.Drain();
                var recycle = Object.FindObjectOfType<ActionRecycleDriver>(true);
                if (recycle && recycle != _previousRecycle)
                {
                    Object.Destroy(recycle.gameObject);
                }
            }
            if (_ownsActionSession)
            {
                try
                {
                    ActionKit.Shutdown();
                }
                finally
                {
                    _ownsActionSession = false;
                }
            }
            yield return null;
        }

        private T Own<T>(T action) where T : IAction
        {
            _owned[action] = action.ActionId;
            return action;
        }

        private UniRxAction<int> Action(ObservableProbe source) => Own(UniRxAction<int>.Allocate(() => source));

        private IActionController Start(IAction action, int scope, Action<IActionController> onFinish)
        {
            if (scope == 0)
            {
                return action.Start(_caller, onFinish);
            }

            if (scope == 1)
            {
                _roots.Add(ActionKitCurrentScene.SceneComponent.gameObject);
                return action.StartCurrentScene(onFinish);
            }
            if (scope == 2)
            {
                _roots.Add(ActionGlobalHost.Instance.gameObject);
                return action.StartGlobal(onFinish);
            }
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        private static void WithContext(SynchronizationContext context, Action operation)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                operation();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        // Signals never wait for the context and the worker never calls Unity or NUnit.
        private static int OnWorker(Action signal) => Task.Run(() =>
        {
            signal();
            return Thread.CurrentThread.ManagedThreadId;
        }).GetAwaiter().GetResult();

        private static void ExpectError(Exception error) =>
            LogAssert.Expect(LogType.Exception, new Regex(Regex.Escape(error.GetType().Name + ": " + error.Message)));

        private IEnumerator SuccessfulStart(int scope)
        {
            var source = new ObservableProbe();
            var action = Action(source);
            var callbacks = 0;
            var callbackThread = 0;
            IActionController observed = null;
            var beforeDeinit = false;
            var controller = Start(action, scope, current =>
            {
                callbacks++;
                callbackThread = Thread.CurrentThread.ManagedThreadId;
                observed = current;
                beforeDeinit = !current.Action.IsDeinitialized;
            });
            Assert.That(source.SubscribeThread, Is.EqualTo(_mainThread));
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(controller.Action, Is.SameAs(action));
            yield return null;
            source.Observers[0].OnNext(1);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Finished));
            Assert.That(callbacks, Is.Zero);
            source.Observers[0].OnCompleted();
            source.Observers[0].OnError(new InvalidOperationException("terminal after success"));
            yield return null;
            yield return null;
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(observed, Is.SameAs(controller));
            Assert.That(beforeDeinit, Is.True);
            Assert.That(callbackThread, Is.EqualTo(_mainThread));
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(source.Tokens[0].DisposeThread, Is.EqualTo(_mainThread));
            Assert.That(source.SourceDisposals, Is.Zero);
            Assert.That(GetType().Assembly.GetName().Name, Is.EqualTo("YLFramework.Tests.PlayMode"));
        }

        [UnityTest]
        public IEnumerator LocalStartCompletesOnceOnMainThread() => SuccessfulStart(0);
        [UnityTest]
        public IEnumerator SceneStartCompletesOnceOnMainThread() => SuccessfulStart(1);
        [UnityTest]
        public IEnumerator GlobalStartCompletesOnceOnMainThread() => SuccessfulStart(2);

        [UnityTest]
        public IEnumerator SynchronousValueCompletesTheActionCallbackBeforeStartReturns()
        {
            var source = new ObservableProbe();
            var action = Action(source);
            source.DuringSubscribe = observer => observer.OnNext(1);
            var callbacks = 0;
            var controller = action.Start(_caller, () => callbacks++);
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            yield return null;
            yield return null;
            var reused = Action(new ObservableProbe());
            Assert.That(reused, Is.SameAs(action));
            Assert.That(reused.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(reused.IsDeinitialized, Is.False);
            Assert.That(callbacks, Is.EqualTo(1));
        }

        private IEnumerator DestroyHost(int scope)
        {
            var source = new ObservableProbe();
            var action = Action(source);
            var callbacks = 0;
            var controller = Start(action, scope, _ => callbacks++);
            yield return null;
            var host = scope == 0 ? _caller.gameObject : scope == 1 ? ActionKitCurrentScene.SceneComponent.gameObject : ActionGlobalHost.Instance.gameObject;
            Object.Destroy(host);
            yield return null;
            yield return null;
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
            Assert.That(callbacks, Is.Zero);
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            var cancelledStatus = action.Status;
            source.Observers[0].OnNext(1);
            source.Observers[0].OnCompleted();
            source.Observers[0].OnError(new InvalidOperationException("destroyed host observer"));
            Assert.That(action.Status, Is.EqualTo(cancelledStatus));
            Assert.That(source.SourceDisposals, Is.Zero);
        }

        [UnityTest]
        public IEnumerator DestroyingLocalHostCancelsItsSubscription() => DestroyHost(0);
        [UnityTest]
        public IEnumerator DestroyingSceneHostCancelsItsSubscription() => DestroyHost(1);
        [UnityTest]
        public IEnumerator DestroyingGlobalHostCancelsItsSubscription() => DestroyHost(2);

        private IEnumerator TerminalCancellation(bool faulted)
        {
            var source = new ObservableProbe();
            var action = Action(source);
            var callbacks = 0;
            var controller = action.Start(_caller, () => callbacks++);
            yield return null;
            if (faulted)
            {
                var error = new InvalidOperationException("play mode observable failure");
                ExpectError(error);
                source.Observers[0].OnError(error);
            }
            else
            {
                source.Observers[0].OnCompleted();
            }

            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.Not.EqualTo(ActionStatus.Finished));
            yield return null;
            yield return null;
            Assert.That(controller.Action, Is.Null);
            Assert.That(callbacks, Is.Zero);
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(source.SourceDisposals, Is.Zero);
        }

        [UnityTest]
        public IEnumerator EmptyCompletionDoesNotInvokeSuccessfulCallback() => TerminalCancellation(false);
        [UnityTest]
        public IEnumerator ObservableErrorLogsAndDoesNotInvokeSuccessfulCallback() => TerminalCancellation(true);

        [UnityTest]
        public IEnumerator PauseDoesNotStopExternalValuesOrDelayTheirMainThreadFinish()
        {
            var source = new ObservableProbe();
            var action = Action(source);
            var callbacks = 0;
            var controller = action.Start(_caller, () => callbacks++);
            controller.Pause();
            yield return null;
            Assert.That(action.IsPaused, Is.True);
            source.Observers[0].OnNext(1);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Finished));
            yield return null;
            yield return null;
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(controller.Action, Is.Null);
        }

        private IEnumerator ResetDuringStart(bool resetInFactory)
        {
            var source = new ObservableProbe();
            UniRxAction<int> action = null;
            var factoryCalls = 0;
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
                factoryCalls++;
                if (resetInFactory && !reset)
                {
                    reset = true;
                    action.Reset();
                }
                return source;
            }));
            var callbacks = 0;
            var controller = action.Start(_caller, () => callbacks++);
            yield return null;
            yield return null;
            Assert.That(factoryCalls, Is.EqualTo(2), "An executor must restart the slot invalidated by a synchronous Reset.");
            Assert.That(source.Observers.Count, Is.EqualTo(resetInFactory ? 1 : 2));
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(callbacks, Is.Zero);
            source.Observers[source.Observers.Count - 1].OnNext(1);
            yield return null;
            yield return null;
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(controller.Action, Is.Null);
            foreach (var token in source.Tokens)
            {
                Assert.That(token.Disposals, Is.EqualTo(1));
            }

            Assert.That(source.SourceDisposals, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ResetInsideFactoryIsRestartedByTheHost() => ResetDuringStart(true);
        [UnityTest]
        public IEnumerator ResetInsideSubscribeIsRestartedByTheHost() => ResetDuringStart(false);

        private IEnumerator ReuseDuringStart(bool cancelInFactory)
        {
            var first = new ObservableProbe();
            var second = new ObservableProbe();
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
            var oldCallbacks = 0;
            var oldController = original.Start(_caller, () => oldCallbacks++);
            Assert.That(replacement, Is.SameAs(original));
            Assert.That(replacement.ActionId, Is.Not.EqualTo(id));
            Assert.That(replacement.Status, Is.EqualTo(ActionStatus.NotStart), "Start must not let an old Execute stack change the replacement lease.");
            Assert.That(oldController.Action, Is.Null);
            Assert.That(second.Observers, Is.Empty);
            var callbacks = 0;
            var controller = replacement.Start(_caller, () => callbacks++);
            Assert.That(second.Observers.Count, Is.EqualTo(1));
            yield return null;
            Assert.That(oldCallbacks, Is.Zero);
            Assert.That(callbacks, Is.Zero);
            second.Observers[0].OnNext(2);
            yield return null;
            yield return null;
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(oldCallbacks, Is.Zero);
            Assert.That(controller.Action, Is.Null);
            foreach (var token in first.Tokens)
            {
                Assert.That(token.Disposals, Is.EqualTo(1));
            }

            Assert.That(second.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(first.SourceDisposals + second.SourceDisposals, Is.Zero);
        }

        [UnityTest]
        public IEnumerator PoolReuseInsideFactoryPreservesTheNewLeaseDuringStart() => ReuseDuringStart(true);
        [UnityTest]
        public IEnumerator PoolReuseInsideSubscribePreservesTheNewLeaseDuringStart() => ReuseDuringStart(false);

        [UnityTest]
        public IEnumerator WorkerOnNextPostsToCapturedContextWithoutChangingActionOnWorker()
        {
            var context = new MainThreadContext();
            var source = new ObservableProbe();
            var action = Action(source);
            var callbacks = 0;
            var callbackThread = 0;
            IActionController controller = null;
            WithContext(context, () => controller = action.Start(_caller, () =>
            {
                callbacks++;
                callbackThread = Thread.CurrentThread.ManagedThreadId;
            }));
            var worker = OnWorker(() => source.Observers[0].OnNext(1));
            Assert.That(worker, Is.Not.EqualTo(_mainThread));
            Assert.That(context.Posts, Is.EqualTo(1));
            Assert.That(context.PostThread, Is.EqualTo(worker));
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(source.Tokens[0].Disposals, Is.Zero);
            Assert.That(callbacks, Is.Zero);
            context.DeliverOnMainThread();
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Finished));
            Assert.That(callbacks, Is.Zero);
            yield return null;
            yield return null;
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(callbackThread, Is.EqualTo(_mainThread));
            Assert.That(source.Tokens[0].DisposeThread, Is.EqualTo(_mainThread));
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(controller.Action, Is.Null);
        }

        [UnityTest]
        public IEnumerator WorkerOnNextReturnsThroughTheActualUnityContextToMainThread()
        {
            Assert.That(SynchronizationContext.Current, Is.Not.Null, "Unity Play Mode must supply its main-thread synchronization context.");
            var source = new ObservableProbe();
            var action = Action(source);
            var callbacks = 0;
            var callbackThread = 0;
            var controller = action.Start(_caller, () =>
            {
                callbacks++;
                callbackThread = Thread.CurrentThread.ManagedThreadId;
            });
            Assert.That(OnWorker(() => source.Observers[0].OnNext(1)), Is.Not.EqualTo(_mainThread));
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(source.Tokens[0].Disposals, Is.Zero);
            yield return null;
            yield return null;
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(callbackThread, Is.EqualTo(_mainThread));
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(source.Tokens[0].DisposeThread, Is.EqualTo(_mainThread));
            Assert.That(controller.Action, Is.Null);
        }

        [UnityTest]
        public IEnumerator WithoutContextOnlyMainThreadOnExecuteCanConsumeWorkerSignal()
        {
            var source = new ObservableProbe();
            var action = Action(source);
            var id = action.ActionId;
            WithContext(null, () => action.Execute(0));
            Assert.That(OnWorker(() => source.Observers[0].OnNext(1)), Is.Not.EqualTo(_mainThread));
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(source.Tokens[0].Disposals, Is.Zero);
            OnWorker(() => action.OnExecute(0));
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            yield return null;
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(action.Execute(0), Is.True);
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(source.Tokens[0].DisposeThread, Is.EqualTo(_mainThread));
            action.Deinit();
            yield return null;
            yield return null;
            var reused = Action(new ObservableProbe());
            Assert.That(reused, Is.SameAs(action));
            Assert.That(reused.ActionId, Is.Not.EqualTo(id));
        }

        [UnityTest]
        public IEnumerator CancelledPostedSignalCannotTouchAPooledReplacementOrInvokeOldCallback()
        {
            var context = new MainThreadContext();
            var first = new ObservableProbe();
            var original = Action(first);
            var oldId = original.ActionId;
            var oldCallbacks = 0;
            IActionController oldController = null;
            WithContext(context, () => oldController = original.Start(_caller, () => oldCallbacks++));
            OnWorker(() => first.Observers[0].OnNext(1));
            Assert.That(context.Posts, Is.EqualTo(1));
            oldController.Deinit();
            Assert.That(first.Tokens[0].Disposals, Is.EqualTo(1));
            yield return null;
            yield return null;
            Assert.That(oldController.Action, Is.Null);
            var second = new ObservableProbe();
            var replacement = Action(second);
            Assert.That(replacement, Is.SameAs(original));
            Assert.That(replacement.ActionId, Is.Not.EqualTo(oldId));
            var newCallbacks = 0;
            var newController = replacement.Start(_caller, () => newCallbacks++);
            context.DeliverOnMainThread();
            first.Observers[0].OnError(new InvalidOperationException("cancelled posted observer"));
            Assert.That(replacement.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(replacement.IsDeinitialized, Is.False);
            Assert.That(second.Tokens[0].Disposals, Is.Zero);
            Assert.That(oldCallbacks, Is.Zero);
            yield return null;
            Assert.That(newCallbacks, Is.Zero);
            second.Observers[0].OnNext(2);
            yield return null;
            yield return null;
            Assert.That(newCallbacks, Is.EqualTo(1));
            Assert.That(oldCallbacks, Is.Zero);
            Assert.That(newController.Action, Is.Null);
            Assert.That(first.SourceDisposals + second.SourceDisposals, Is.Zero);
        }

        [UnityTest]
        public IEnumerator PostedSignalFromResetSlotCannotFinishTheNextSubscription()
        {
            var context = new MainThreadContext();
            var source = new ObservableProbe();
            var action = Action(source);
            WithContext(context, () => action.Execute(0));
            OnWorker(() => source.Observers[0].OnNext(1));
            action.Reset();
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            action.Execute(0);
            context.DeliverOnMainThread();
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(source.Tokens[1].Disposals, Is.Zero);
            yield return null;
            source.Observers[1].OnNext(2);
            Assert.That(action.Execute(0), Is.True);
            Assert.That(source.Tokens[1].Disposals, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator BackgroundErrorIsOnlyLoggedAndDisposedWhenMainThreadExecutes()
        {
            var source = new ObservableProbe();
            var action = Action(source);
            var error = new InvalidOperationException("worker observable error");
            WithContext(null, () => action.Execute(0));
            OnWorker(() => source.Observers[0].OnError(error));
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(source.Tokens[0].Disposals, Is.Zero);
            LogAssert.NoUnexpectedReceived();
            yield return null;
            ExpectError(error);
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(source.Tokens[0].DisposeThread, Is.EqualTo(_mainThread));
        }

        [UnityTest]
        public IEnumerator FirstWorkerTerminalWinsBeforeMainThreadReceivesThePost()
        {
            var context = new MainThreadContext();
            var source = new ObservableProbe();
            var action = Action(source);
            var callbacks = 0;
            IActionController controller = null;
            WithContext(context, () => controller = action.Start(_caller, () => callbacks++));
            OnWorker(() =>
            {
                source.Observers[0].OnCompleted();
                source.Observers[0].OnNext(1);
                source.Observers[0].OnError(new InvalidOperationException("ignored worker terminal"));
            });
            Assert.That(context.Posts, Is.EqualTo(1));
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(action.IsDeinitialized, Is.False);
            LogAssert.NoUnexpectedReceived();
            context.DeliverOnMainThread();
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(source.Tokens[0].DisposeThread, Is.EqualTo(_mainThread));
            yield return null;
            yield return null;
            Assert.That(controller.Action, Is.Null);
            Assert.That(callbacks, Is.Zero);
            Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator RepeatFactoryCreatesNewStreamsAcrossFrames()
        {
            var sources = new List<ObservableProbe>();
            var repeat = Own(ActionKit.Repeat(2));
            repeat.UniRx(() =>
            {
                var source = new ObservableProbe();
                sources.Add(source);
                return source;
            });
            var callbacks = 0;
            var controller = repeat.Start(_caller, () => callbacks++);
            yield return null;
            yield return null;
            Assert.That(sources.Count, Is.EqualTo(1));
            sources[0].Observers[0].OnNext(1);
            yield return null;
            yield return null;
            yield return null;
            Assert.That(sources.Count, Is.EqualTo(2));
            Assert.That(sources[0].Tokens[0].Disposals, Is.EqualTo(1));
            sources[0].Observers[0].OnCompleted();
            Assert.That(repeat.IsDeinitialized, Is.False);
            Assert.That(callbacks, Is.Zero);
            sources[1].Observers[0].OnNext(2);
            yield return null;
            yield return null;
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(controller.Action, Is.Null);
            foreach (var source in sources)
            {
                Assert.That(source.Tokens[0].Disposals, Is.EqualTo(1));
                Assert.That(source.SourceDisposals, Is.Zero);
            }
        }

        private IEnumerator InactiveParentAndReusedChild(bool faulted)
        {
            var first = new ObservableProbe();
            var child = Action(first);
            var id = child.ActionId;
            var tailCalls = 0;
            var callbacks = 0;
            var parent = Own(ActionKit.Sequence()).Append(child).Callback(() => tailCalls++);
            var controller = parent.Start(_caller, () => callbacks++);
            _caller.gameObject.SetActive(false);
            if (faulted)
            {
                var error = new InvalidOperationException("inactive parent observable failure");
                ExpectError(error);
                first.Observers[0].OnError(error);
            }
            else
            {
                first.Observers[0].OnCompleted();
            }

            Assert.That(child.IsDeinitialized, Is.True);
            yield return null;
            yield return null;
            var second = new ObservableProbe();
            var replacement = Action(second);
            Assert.That(replacement, Is.SameAs(child));
            Assert.That(replacement.ActionId, Is.Not.EqualTo(id));
            _caller.gameObject.SetActive(true);
            yield return null;
            yield return null;
            Assert.That(parent.IsDeinitialized, Is.True, "The inactive parent's old child lease must remain cancelled after recycling.");
            Assert.That(controller.Action, Is.Null);
            Assert.That(tailCalls, Is.Zero);
            Assert.That(callbacks, Is.Zero);
            Assert.That(replacement.IsDeinitialized, Is.False);
            Assert.That(replacement.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(second.Observers, Is.Empty);
            var restarted = replacement.Start(_caller, () => callbacks++);
            second.Observers[0].OnNext(2);
            yield return null;
            yield return null;
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(tailCalls, Is.Zero);
            Assert.That(restarted.Action, Is.Null);
            Assert.That(first.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(second.Tokens[0].Disposals, Is.EqualTo(1));
            Assert.That(first.SourceDisposals + second.SourceDisposals, Is.Zero);
        }

        [UnityTest]
        public IEnumerator InactiveSequenceKeepsEmptyCancellationAcrossChildPoolReuse() => InactiveParentAndReusedChild(false);
        [UnityTest]
        public IEnumerator InactiveSequenceKeepsErrorCancellationAcrossChildPoolReuse() => InactiveParentAndReusedChild(true);

        [UnityTest]
        public IEnumerator ShutdownCancelsAllScopesButKeepsUserHostsAndExternalSources()
        {
            var userSceneRoot = new GameObject("ActionKit.Observable.UserSceneHost");
            _roots.Add(userSceneRoot);
            var userSceneHost = userSceneRoot.AddComponent<ActionKitCurrentScene>();
            ActionKitCurrentScene.CachedSceneComponent = userSceneHost;
            var sources = new[] { new ObservableProbe(), new ObservableProbe(), new ObservableProbe() };
            var actions = new UniRxAction<int>[3];
            var controllers = new IActionController[3];
            var callbacks = 0;
            for (var scope = 0; scope < 3; scope++)
            {
                actions[scope] = Action(sources[scope]);
                // The fixture owns this user host but Shutdown must not destroy it.
                controllers[scope] = scope == 1 ? actions[scope].StartCurrentScene(() => callbacks++) : Start(actions[scope], scope, _ => callbacks++);
            }
            yield return null;
            ActionKit.Shutdown();
            for (var scope = 0; scope < 3; scope++)
            {
                Assert.That(actions[scope].IsDeinitialized, Is.True);
                Assert.That(controllers[scope].Action, Is.Null);
                Assert.That(sources[scope].Tokens[0].Disposals, Is.EqualTo(1));
                Assert.That(sources[scope].SourceDisposals, Is.Zero);
                sources[scope].Observers[0].OnNext(1);
                sources[scope].Observers[0].OnCompleted();
                sources[scope].Observers[0].OnError(new InvalidOperationException("shutdown observer"));
            }
            yield return null;
            yield return null;
            Assert.That(_caller != null, Is.True);
            Assert.That(_caller.gameObject.activeSelf, Is.True);
            Assert.That(userSceneRoot != null, Is.True);
            Assert.That(userSceneHost != null, Is.True);
            Assert.That(Object.FindObjectsOfType<MonoUpdateActionExecutor>(true), Is.Empty);
            Assert.That(Object.FindObjectsOfType<ActionGlobalHost>(true), Is.Empty);
            Assert.That(callbacks, Is.Zero);
            var fresh = new ObservableProbe();
            var replacement = Action(fresh);
            var restarted = replacement.Start(_caller, () => callbacks++);
            fresh.Observers[0].OnNext(2);
            yield return null;
            yield return null;
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(restarted.Action, Is.Null);
        }
    }
}
