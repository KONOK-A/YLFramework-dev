using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Actions;
using YLFramework.Resource;
using YLFramework.Timing;
using Kit = YLFramework.Actions.ActionKit;
using Clock = YLFramework.Timing.Timer;

namespace YLFramework.ActionTiming.Acceptance
{
    [UnityEngine.Scripting.Preserve]
    public sealed class ActionTimingPlayerProbe : MonoBehaviour
    {
        [Serializable]
        public sealed class Owner
        {
            public int schemaVersion = 1;
            public string purpose = "YLTesting.ActionTimingPlayer";
            public string token;
            public string player;
            public string inputFingerprint;
            public string sourceCommit;
        }

        [Serializable]
        public sealed class Report
        {
            public int schemaVersion = 1;
            public string purpose = "YLTesting.ActionTimingPlayer";
            public string status = "Failed";
            public string error;
            public string unityVersion;
            public string platform;
            public bool isEditor;
            public bool developmentBuild;
            public string backend;
            public string runtimeAssembly;
            public string probeAssembly;
            public string catalogDigest = "";
            public int pid;
            public int pointerBits;
            public int leaseCount = -1;
            public int recordCount = -1;
            public int activeActions = -1;
            public int activeTimers = -1;
            public string token;
            public string inputFingerprint;
            public string sourceCommit;
            public string[] cases;
            public string[] frameworkAssemblies;
        }

        public string runToken;
        public string inputFingerprint;
        public string sourceCommit;
        private readonly List<IAction> _nodes = new();
        private readonly List<ActionHandle> _actions = new();
        private readonly List<TimerHandle> _timers = new();
        private readonly List<GameObject> _objects = new();
        private readonly List<string> _cases = new();
        private string _runtimeError;
        private readonly object _diagnosticGate = new();
        private string _expectedError;
        private int _expectedErrors;
        private int _mainThread;

        [UnityEngine.Scripting.Preserve]
        public sealed class OwnerBehaviour : MonoBehaviour
        {
        }

        private struct Payload
        {
            internal int Value;
        }

        private sealed class Subject<T> : IObservable<T>
        {
            private IObserver<T> _observer;
            internal int Disposals;
            public IDisposable Subscribe(IObserver<T> observer)
            {
                Require(_observer == null, "Duplicate subscription.");
                _observer = observer;
                return new Subscription(this);
            }
            internal void Next(T value) => _observer?.OnNext(value);
            internal void Complete() => _observer?.OnCompleted();
            private sealed class Subscription : IDisposable
            {
                private Subject<T> _source;
                internal Subscription(Subject<T> source) => _source = source;
                public void Dispose()
                {
                    var source = _source;
                    if (source == null)
                        return;
                    _source = null;
                    source._observer = null;
                    source.Disposals++;
                }
            }
        }

        private IEnumerator Start()
        {
            var report = new Report();
            var stack = new Stack<IEnumerator>();
            string output = null;
            var scale = Time.timeScale;
            var background = Application.runInBackground;
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            BeginDiagnostics();
            try
            {
                try
                {
                    output = Prepare(report);
                    Require(!Kit.IsInitialized && ResourceSystem.Current == null, "Do not adopt a foreign framework session.");
                    DontDestroyOnLoad(gameObject);
                    Application.runInBackground = true;
                    Time.timeScale = 1;
                    stack.Push(Run());
                }
                catch (Exception error) { report.error = error.ToString(); }
                while (report.error == null && stack.Count != 0)
                {
                    bool more = false;
                    object current = null;
                    try
                    {
                        Require(RuntimeError == null, RuntimeError);
                        more = stack.Peek().MoveNext();
                        if (more)
                            current = stack.Peek().Current;
                        Require(RuntimeError == null, RuntimeError);
                    }
                    catch (Exception error) { report.error = error.ToString(); }
                    if (report.error != null)
                        break;
                    if (!more)
                        (stack.Pop() as IDisposable)?.Dispose();
                    else if (current is IEnumerator nested)
                        stack.Push(nested);
                    else
                        yield return current;
                }
            }
            finally
            {
                try
                {
                    while (stack.Count != 0)
                        (stack.Pop() as IDisposable)?.Dispose();
                    Cleanup();
                    lock (_diagnosticGate)
                        Require(_expectedError == null, "An expected diagnostic was never verified.");
                    Require(RuntimeError == null, RuntimeError);
                    report.activeActions = _actions.Count(handle => handle.IsActive);
                    report.activeTimers = _timers.Count(handle => handle.IsActive);
                    Require(report.activeActions == 0 && report.activeTimers == 0 && _nodes.All(node => node.IsDeinitialized), "Owned responsibilities remain active.");
                    Require(ResourceSystem.Current == null, "The probe must not create a Resource authority.");
                    report.leaseCount = report.recordCount = 0;
                }
                catch (Exception cleanup) { report.error = report.error == null ? cleanup.ToString() : report.error + "\nCleanup: " + cleanup; }
                EndDiagnostics();
                Time.timeScale = scale;
                Application.runInBackground = background;
                report.cases = _cases.ToArray();
                report.frameworkAssemblies = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name)
                    .Where(name => name.StartsWith("YLFramework", StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                report.status = report.error == null && _cases.Count == 23 ? "Passed" : "Failed";
                var persisted = false;
                try
                {
                    if (output != null)
                    {
                        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                        writer.Write(JsonUtility.ToJson(report, true));
                        writer.Flush();
                        persisted = true;
                    }
                }
                finally { Application.Quit(persisted && report.status == "Passed" ? 0 : 1); }
            }
        }

        private string Prepare(Report report)
        {
            report.unityVersion = Application.unityVersion;
            report.platform = Application.platform.ToString();
            report.isEditor = Application.isEditor;
            report.developmentBuild = UnityEngine.Debug.isDebugBuild;
#if ENABLE_IL2CPP
            report.backend = "IL2CPP";
#elif ENABLE_MONO
            report.backend = "Mono";
#else
            report.backend = "Unsupported";
#endif
            report.runtimeAssembly = typeof(Kit).Assembly.GetName().Name;
            report.probeAssembly = typeof(ActionTimingPlayerProbe).Assembly.GetName().Name;
            report.pid = Process.GetCurrentProcess().Id;
            report.pointerBits = IntPtr.Size * 8;
            report.token = runToken;
            report.inputFingerprint = inputFingerprint;
            report.sourceCommit = sourceCommit;
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "-ylFeatureResult");
            Require(index > 0 && index + 1 < args.Length, "Missing owned result path.");
            var player = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
            var result = Path.GetFullPath(args[index + 1]);
            Require(Path.GetDirectoryName(result).Equals(player, StringComparison.OrdinalIgnoreCase) &&
                (Path.GetFileName(result) == "result-1.json" || Path.GetFileName(result) == "result-2.json") &&
                !File.Exists(result) && !Directory.Exists(result), "Result must be new and owned by this Player.");
            NoLinks(player);
            var ownerPath = Path.Combine(player, ".action-timing-owner.json");
            NoLinks(ownerPath);
            var owner = JsonUtility.FromJson<Owner>(File.ReadAllText(ownerPath));
            Require(owner != null && owner.schemaVersion == 1 && owner.purpose == report.purpose &&
                Guid.TryParseExact(runToken, "N", out _) && owner.token == runToken && owner.inputFingerprint == inputFingerprint &&
                owner.sourceCommit == sourceCommit && Path.GetFullPath(owner.player).Equals(player, StringComparison.OrdinalIgnoreCase), "Owner and serialized source identity differ.");
            Require(!report.isEditor && report.backend == "IL2CPP" && !report.developmentBuild && report.pointerBits == 64 &&
                report.platform == "WindowsPlayer", "Only an actual x64 Release IL2CPP Player qualifies.");
            return result;
        }

        private IEnumerator Run()
        {
            Throws<ArgumentNullException>(() => Kit.Condition(null));
            Throws<ArgumentNullException>(() => Kit.Task(null));
            Throws<ArgumentOutOfRangeException>(() => Kit.Delay(float.NaN, null));
            Throws<ArgumentOutOfRangeException>(() => Kit.Repeat(-2));
            Pass("Action.Arguments");

            var order = new List<int>();
            var sequenceDone = 0;
            Start(Own(Kit.Sequence().Callback(() => order.Add(1)).Delay(0).Parallel(p => p.Callback(() => order.Add(2)))
                .Repeat(2, repeat => repeat.Callback(() => order.Add(3)))).StartHandle(this, () => sequenceDone++));
            yield return Until(() => sequenceDone == 1);
            Require(order.SequenceEqual(new[] { 1, 2, 3, 3 }), "Composition order/count changed.");
            Pass("Action.Composition");

            var first = Own(Kit.Custom<Payload>(api => api.Data = new Payload { Value = 17 }));
            first.ActionId = 0;
            var stale = Start(first.StartHandle(this));
            Require(stale.Pause() && stale.Resume() && stale.Cancel(), "Anonymous control failed.");
            yield return null;
            yield return null;
            var data = 0;
            var reused = Own(Kit.Custom<Payload>(api => { api.Data = new Payload { Value = 29 }; api.OnStart(() => data = api.Data.Value); }));
            Require(ReferenceEquals(first, reused), "The actual typed action pool was not reused.");
            reused.ActionId = 0;
            var current = Start(reused.StartHandle(this));
            Require(!stale.IsActive && !stale.Cancel() && !stale.Pause() && current.IsActive && data == 29, "A stale pooled lease affected a new run.");
            var text = "";
            var referenceData = Start(Own(Kit.Custom<string>(api => { api.Data = "aot"; api.OnStart(() => text = api.Data); })).StartHandle(this));
            Require(text == "aot", "Reference-type generic callbacks were stripped.");
            current.Cancel();
            referenceData.Cancel();
            Pass("Action.GenericLeaseReuse");

            var oldNamed = Start(Own(Kit.Condition(() => false)).StartNamed(this, "aot"));
            var named = Start(Own(Kit.Condition(() => false)).ReplaceNamed(this, "aot"));
            Require(!oldNamed.IsActive && !oldNamed.Cancel() && named.IsActive && Kit.ContainsNamed(this, "aot"), "Named replacement lost its exact lease.");
            named.Cancel();
            Require(!Kit.ContainsNamed(this, "aot"), "Cancelled name remained registered.");
            Pass("Action.NamedReplace");

            Time.timeScale = 0;
            var scaledDone = 0;
            var unscaledDone = 0;
            var scaled = Start(Own(Kit.Delay(.01f, null)).StartHandle(this, () => scaledDone++));
            var unscaled = Start(Own(Kit.Delay(.01f, null)).StartHandle(this, () => unscaledDone++));
            Require(unscaled.SetUpdateMode(ActionUpdateModes.UnscaledDeltaTime), "Cannot select Unscaled.");
            yield return Until(() => unscaledDone == 1);
            Require(scaled.IsActive && scaledDone == 0, "Scaled action advanced at timeScale zero.");
            Time.timeScale = 1;
            yield return Until(() => scaledDone == 1);
            Pass("Action.Clocks");

            var host = Create("ActionOwner");
            var ownerBehaviour = host.AddComponent<OwnerBehaviour>();
            var updates = 0;
            var local = Start(Own(Kit.Custom(api => api.OnExecute(_ => updates++))).StartHandle(ownerBehaviour));
            ownerBehaviour.enabled = false;
            yield return Until(() => updates > 0);
            host.SetActive(false);
            var paused = updates;
            yield return null;
            yield return null;
            Require(local.IsActive && updates == paused, "GameObject disable must pause, not finish.");
            UnityEngine.Object.Destroy(host);
            yield return null;
            yield return null;
            Require(!local.IsActive, "Destroyed owner retained its execution.");
            Pass("Action.OwnerLifetime");

            var originalScene = SceneManager.GetActiveScene();
            var scene = SceneManager.CreateScene("ActionTimingTemporary");
            SceneManager.SetActiveScene(scene);
            var sceneAction = Start(Own(Kit.Condition(() => false)).StartHandleCurrentScene());
            var global = Start(Own(Kit.Condition(() => false)).StartHandleGlobal());
            SceneManager.SetActiveScene(originalScene);
            yield return SceneManager.UnloadSceneAsync(scene);
            Require(!sceneAction.IsActive && global.IsActive, "Scene/global scope boundary changed.");
            global.Cancel();
            Pass("Action.SceneGlobalLifetime");

            var taskDone = 0;
            var immediate = Start(Own(Kit.Task(() => Task.CompletedTask)).StartHandle(this, () => taskDone++));
            Require(!immediate.IsActive && taskDone == 1, "Synchronous task completion changed.");
            var completionThread = 0;
            Start(Own(Kit.Task(() => Task.Run(() => 42))).StartHandle(this, () => { completionThread = Thread.CurrentThread.ManagedThreadId; taskDone++; }));
            yield return Until(() => taskDone == 2);
            Require(completionThread == _mainThread, "Task completion escaped Unity's context.");
            Pass("Action.Task");

            var producer = new TaskCompletionSource<int>();
            var cancelled = Start(Own(Kit.Task(() => producer.Task)).StartHandle(this, () => taskDone++));
            Require(cancelled.Cancel() && !producer.Task.IsCompleted, "Cancellation took over the external producer.");
            producer.SetResult(1);
            yield return null;
            yield return null;
            Require(taskDone == 2, "Late cancelled task reported success.");
            ExpectError("Expected_ActionTiming_Task");
            var fault = Start(Own(Kit.Task(() => Task.FromException(new InvalidOperationException("Expected_ActionTiming_Task")))).StartHandle(this));
            yield return Until(() => !fault.IsActive);
            VerifyExpectedError();
            Pass("Action.TaskCancelFault");

            var coroutineDone = 0;
            Start(Own(Kit.Coroutine(() => CoroutineBody(() => coroutineDone++))).StartHandle(this));
            yield return Until(() => coroutineDone == 1);
            var stopped = Start(Own(Kit.Coroutine(() => CoroutineBody(() => coroutineDone++))).StartHandle(this));
            stopped.Cancel();
            yield return null;
            yield return null;
            Require(coroutineDone == 1, "Cancelled coroutine still executed its tail.");
            Pass("Action.Coroutine");

            var source = new Subject<int>();
            var observed = 0;
            Start(Own(source.ToAction()).StartHandle(this, () => { Require(Thread.CurrentThread.ManagedThreadId == _mainThread, "Observable completion escaped main thread."); observed++; }));
            var signal = Task.Run(() => source.Next(7));
            yield return Until(() => SenderCompleted(signal) && observed == 1);
            Require(source.Disposals == 1, "Observable subscription was not released once.");
            var empty = new Subject<int>();
            var emptyHandle = Start(Own(empty.ToAction()).StartHandle(this, () => observed++));
            empty.Complete();
            yield return Until(() => !emptyHandle.IsActive);
            Require(observed == 1 && empty.Disposals == 1, "Empty observable became successful or leaked its subscription.");
            Pass("Action.Observable");

            ExpectError("Expected_ActionTiming_Worker");
            var logged = Task.Run(() => UnityEngine.Debug.LogException(new InvalidOperationException("Expected_ActionTiming_Worker")));
            yield return Until(() => SenderCompleted(logged));
            VerifyExpectedError();
            Pass("Diagnostics.ThreadedLog");

            var failedSender = Task.Run((Action)(() => throw new InvalidOperationException("Expected_ActionTiming_Sender")));
            yield return Until(() => failedSender.IsCompleted);
            Throws<InvalidOperationException>(() => SenderCompleted(failedSender));
            Pass("Diagnostics.SenderFault");

            var fade = Own(Kit.ScreenTransition.FadeIn().Duration(0));
            Require(!fade.Execute(0) && fade.Execute(0), "Zero-duration Fade changed its two-step boundary.");
            Require(fade.Status == ActionStatus.Finished, "Fade did not complete.");
            fade.Deinit();
            Pass("Action.FadeNumeric");

            var frame = Time.frameCount;
            var delivered = -1;
            Keep(Clock.DelayFrames(2, () => delivered = Time.frameCount));
            Require(delivered == -1, "Frame timer ran inline.");
            yield return Until(() => delivered >= 0);
            Require(delivered >= frame + 2, "Frame timer fired before its rendered-frame boundary.");
            Pass("Timing.Frames");

            Time.timeScale = 0;
            var defaultCalls = 0;
            var scaledCalls = 0;
            Keep(Clock.Delay(.01, () => defaultCalls++));
            var timerScaled = Keep(Clock.Delay(.01, () => scaledCalls++, TimerClock.Scaled));
            yield return Until(() => defaultCalls == 1);
            Require(timerScaled.IsActive && scaledCalls == 0, "Default and Scaled clocks were conflated.");
            var frozen = Keep(Clock.DelayFrames(3));
            frozen.Pause();
            var remaining = frozen.RemainingFrames;
            yield return null;
            yield return null;
            Require(frozen.RemainingFrames == remaining && frozen.Resume(), "Pause did not freeze remaining frames.");
            yield return Until(() => frozen.IsFinished);
            Time.timeScale = 1;
            yield return Until(() => scaledCalls == 1);
            Pass("Timing.ClocksPause");

            using (var scheduler = new TimerScheduler(0, 0, 0))
            {
                var rounds = new List<long>();
                var periodic = scheduler.Repeat(0, 2, rounds.Add, 3);
                scheduler.Advance(0, 0, 1);
                scheduler.Advance(0, 100, 2);
                scheduler.Advance(0, 100, 2);
                Require(rounds.SequenceEqual(new long[] { 1, 2 }), "Fixed-delay timer caught up or dispatched twice in one frame.");
                scheduler.Advance(0, 102, 3);
                Require(periodic.Status == TimerState.Completed && periodic.ExecutionCount == 3, "Total repeat count changed.");
            }
            Pass("Timing.FixedDelay");

            var scope = new TimerScope();
            TimerHandle scopePending = default;
            var scopeCancelled = 0;
            var scopeLate = 0;
            using (scope)
            {
                TimerHandle inner = default;
                var old = Keep(scope.PostNamed("replace", 100, onCancelled: _ => inner = Keep(scope.PostNamed("replace", 100))));
                var outer = scope.ReplaceNamed("replace", 100);
                Require(!outer.IsActive && old.Status == TimerState.Cancelled && inner.IsActive && scope.Contains("replace"), "Replacement overwrote a reentrant name owner.");
                var other = Keep(scope.Delay(100));
                var group = new TimerHandleGroup();
                group.Add(inner);
                group.Add(other);
                group.Add(inner);
                group.CancelAll();
                Require(inner.Status == TimerState.Cancelled && other.Status == TimerState.Cancelled && !scope.Contains("replace"), "Group cancellation lost exact scope responsibility.");
                scopePending = Keep(scope.DelayFrames(100, () => scopeLate++, _ => scopeCancelled++));
                Require(scopePending.IsActive, "Scope disposal must be tested against an active run.");
            }
            Require(scope.IsDisposed && scopePending.Status == TimerState.Cancelled && scopeCancelled == 1, "Disposing an active scope did not cancel exactly once.");
            Throws<ObjectDisposedException>(() => scope.Delay(0));
            yield return null;
            yield return null;
            Require(scopeLate == 0 && scopeCancelled == 1, "Disposed scope executed a late callback.");
            Pass("Timing.ScopeReplaceGroupDispose");

            var slot = new ReusableTimer();
            TimerHandle slotPending = default;
            var slotCancelled = 0;
            var slotLate = 0;
            using (slot)
            {
                var old = Keep(slot.StartFrames(100));
                var wait = old.WaitForTermination();
                old.Cancel();
                Require(!wait.keepWaiting && wait.Status == TimerState.Cancelled, "Wait did not snapshot cancellation.");
                var calls = 0;
                var fresh = Keep(slot.StartFrames(1, () => calls++));
                Require(old.Status == TimerState.Invalidated && !old.Cancel() && fresh.IsActive, "Old slot generation controlled a new run.");
                yield return Until(() => calls == 1);
                Require(wait.Status == TimerState.Cancelled && fresh.Status == TimerState.Completed, "Old waiter followed the new generation.");
                slotPending = Keep(slot.StartFrames(100, () => slotLate++, _ => slotCancelled++));
                Require(slotPending.IsActive, "Slot disposal must be tested against an active run.");
            }
            Require(slot.IsDisposed && slotPending.Status == TimerState.Cancelled && slotCancelled == 1, "Disposing an active slot did not cancel exactly once.");
            Throws<ObjectDisposedException>(() => slot.Start(0));
            yield return null;
            yield return null;
            Require(slotLate == 0 && slotCancelled == 1, "Disposed slot executed a late callback.");
            Pass("Timing.ReusableWaitDispose");

            ExpectError("Expected_ActionTiming_Timer");
            var broken = Keep(Clock.Delay(0, () => throw new InvalidOperationException("Expected_ActionTiming_Timer")));
            var afterFault = 0;
            Keep(Clock.Delay(0, () => afterFault++));
            yield return Until(() => broken.Status == TimerState.Faulted && afterFault == 1);
            VerifyExpectedError();
            var firstFrame = -1;
            var secondFrame = -1;
            Keep(Clock.Delay(0, () => { firstFrame = Time.frameCount; Keep(Clock.Delay(0, () => secondFrame = Time.frameCount)); }));
            yield return Until(() => secondFrame >= 0);
            Require(secondFrame > firstFrame, "Callback registration ran inline in the same dispatch.");
            Pass("Timing.FaultReentrancy");

            var componentHost = Create("TimerComponent");
            componentHost.SetActive(false);
            var component = componentHost.AddComponent<TimerObject>();
            component.delay = 100;
            component.onCompleted = new UnityEngine.Events.UnityEvent();
            component.onAborted = new UnityEngine.Events.UnityEvent();
            var completed = 0;
            var aborted = 0;
            component.onCompleted.AddListener(() => completed++);
            component.onAborted.AddListener(() => aborted++);
            componentHost.SetActive(true);
            component.enabled = false;
            Require(aborted == 1 && completed == 0, "Component disable did not cancel exactly once.");
            component.delay = 0;
            component.enabled = true;
            yield return Until(() => completed == 1);
            component.enabled = false;
            Require(aborted == 1, "Completed timer produced an abort notification.");
            Pass("Timing.ComponentLifetime");

            var foreignUpdates = 0;
            var foreignFrames = 0;
            var subscription = YLFramework.Update.UpdateManager.AddUpdateEvent(() => foreignUpdates++);
            var foreign = StartCoroutine(ForeignFrames(() => foreignFrames++));
            try
            {
                var action = Start(Own(Kit.Condition(() => false)).StartHandleGlobal());
                var timer = Keep(Clock.Delay(100));
                Kit.Shutdown();
                Require(!action.IsActive && timer.IsActive, "Action shutdown crossed into Timer.");
                var next = Start(Own(Kit.Condition(() => false)).StartHandleGlobal());
                Clock.Shutdown();
                Require(timer.Status == TimerState.Invalidated && next.IsActive, "Timer shutdown crossed into Action.");
                var beforeUpdates = foreignUpdates;
                var beforeFrames = foreignFrames;
                yield return null;
                yield return null;
                Require(foreignUpdates > beforeUpdates && foreignFrames > beforeFrames, "Module shutdown cleared foreign Update/coroutine responsibility.");
                next.Cancel();
            }
            finally
            {
                subscription.Unregister();
                StopCoroutine(foreign);
            }
            Pass("Modules.ShutdownIsolation");

            Cleanup();
            yield return null;
            Require(_actions.All(handle => !handle.IsActive) && _timers.All(handle => !handle.IsActive), "Cleanup left an active lease.");
            Pass("Cleanup.Ownership");
        }

        private string RuntimeError
        {
            get
            {
                lock (_diagnosticGate)
                    return _runtimeError;
            }
        }
        private void BeginDiagnostics() => Application.logMessageReceivedThreaded += CaptureError;
        private void EndDiagnostics() => Application.logMessageReceivedThreaded -= CaptureError;
        private static bool SenderCompleted(Task task)
        {
            if (!task.IsCompleted)
                return false;
            task.GetAwaiter().GetResult();
            return true;
        }

        private T Own<T>(T node) where T : IAction
        {
            _nodes.Add(node);
            return node;
        }
        private ActionHandle Start(ActionHandle handle)
        {
            _actions.Add(handle);
            return handle;
        }
        private TimerHandle Keep(TimerHandle handle)
        {
            _timers.Add(handle);
            return handle;
        }
        private GameObject Create(string name)
        {
            var value = new GameObject(name);
            _objects.Add(value);
            return value;
        }
        private void Pass(string name)
        {
            Require(!_cases.Contains(name) && RuntimeError == null, "Duplicate case or unexpected runtime diagnostic: " + name);
            _cases.Add(name);
            UnityEngine.Debug.Log("ACTION_TIMING_IL2CPP " + name + " Passed");
        }
        private static IEnumerator Until(Func<bool> ready)
        {
            var deadline = Time.realtimeSinceStartup + 5;
            while (!ready() && Time.realtimeSinceStartup < deadline)
                yield return null;
            Require(ready(), "The actual Player did not satisfy a bounded runtime condition.");
        }
        private static IEnumerator CoroutineBody(Action callback)
        {
            yield return null;
            callback();
        }
        private static IEnumerator ForeignFrames(Action callback)
        {
            while (true)
            {
                callback();
                yield return null;
            }
        }
        private void Cleanup()
        {
            foreach (var handle in _actions)
                handle.Cancel();
            foreach (var handle in _timers)
                handle.Cancel();
            foreach (var node in _nodes)
                if (!node.IsDeinitialized)
                    node.Deinit();
            foreach (var value in _objects)
                if (value)
                    UnityEngine.Object.Destroy(value);
            Kit.Shutdown();
            Clock.Shutdown();
        }
        private void ExpectError(string marker)
        {
            lock (_diagnosticGate)
            {
                Require(_expectedError == null, "Nested diagnostic expectation.");
                _expectedError = "InvalidOperationException: " + marker;
                _expectedErrors = 0;
            }
        }
        private void VerifyExpectedError()
        {
            lock (_diagnosticGate)
            {
                Require(_expectedErrors == 1, "Expected exactly one owned fault diagnostic.");
                _expectedError = null;
            }
        }
        private void CaptureError(string message, string trace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
                return;
            lock (_diagnosticGate)
            {
                if (_expectedError != null && message.StartsWith(_expectedError, StringComparison.Ordinal))
                {
                    _expectedErrors++;
                    return;
                }
                _runtimeError ??= message + "\n" + trace;
            }
        }
        private static void Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name);
        }
        private static void NoLinks(string path)
        {
            for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Linked Player paths are refused.");
        }
        private static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
