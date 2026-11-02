using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Actions;
using Kit = YLFramework.Actions.ActionKit;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Actions
{
    public class NamedActionTests
    {
        private GameObject _root;
        private MonoUpdateActionExecutor _executor;
        private readonly List<IAction> _actions = new List<IAction>();

        [SetUp]
        public void SetUp()
        {
            ActionQueue.Drain();
            _root = new GameObject("ActionKit.Named.EditModeTest");
            _executor = _root.AddComponent<MonoUpdateActionExecutor>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_root)
            {
                Object.DestroyImmediate(_root);
            }

            foreach (var action in _actions)
            {
                if (!action.IsDeinitialized)
                {
                    action.Deinit();
                }
            }

            _actions.Clear();
            ActionQueue.Drain();
        }

        private T Own<T>(T action) where T : IAction
        {
            _actions.Add(action);
            return action;
        }

        private static ActionController Controller(ActionHandle handle) => (ActionController)typeof(ActionHandle)
            .GetField("_controller", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(handle);

        public sealed class Probe : AbstractAction<Probe>
        {
            internal Action Started;
            internal Action Finished;
            internal Action Cleaning;
            internal bool Complete;
            internal float Delta;
            public override void OnStart() => Started?.Invoke();
            public override void OnExecute(float dt)
            {
                Delta = dt;
                if (Complete)
                {
                    this.Finish();
                }
            }
            public override void OnFinish() => Finished?.Invoke();
            protected override void OnReset()
            {
                Complete = false;
                Delta = 0;
            }
            protected override void OnDeinit()
            {
                try
                {
                    Cleaning?.Invoke();
                }
                finally
                {
                    Started = Finished = Cleaning = null;
                }
            }
        }

        [Test]
        public void MissingQueriesDoNotCreateAnExecutorHostsOrInitializeTheModule()
        {
            Object.DestroyImmediate(_executor);
            var initialized = Kit.IsInitialized;
            var objects = Resources.FindObjectsOfTypeAll<GameObject>().Length;
            var owner = _root.AddComponent<ActionKitCurrentScene>();
            var countWithOwner = Resources.FindObjectsOfTypeAll<GameObject>().Length;
            Assert.That(Kit.TryGetNamed(owner, "missing", out var handle), Is.False);
            Assert.That(handle.IsActive, Is.False);
            Assert.That(Kit.CancelNamed(owner, "missing") || Kit.PauseNamed(owner, "missing") ||
                Kit.ResumeNamed(owner, "missing"), Is.False);
            Assert.That(Kit.ContainsNamedCurrentScene("missing") || Kit.CancelNamedCurrentScene("missing") ||
                Kit.PauseNamedCurrentScene("missing") || Kit.ResumeNamedCurrentScene("missing"), Is.False);
            Assert.That(Kit.TryGetNamedGlobal("missing", out _) || Kit.CancelNamedGlobal("missing") ||
                Kit.PauseNamedGlobal("missing") || Kit.ResumeNamedGlobal("missing"), Is.False);
            Assert.That(_root.GetComponent<MonoUpdateActionExecutor>(), Is.Null);
            Assert.That(Kit.IsInitialized, Is.EqualTo(initialized));
            Assert.That(countWithOwner, Is.EqualTo(objects));
            Assert.That(Resources.FindObjectsOfTypeAll<GameObject>().Length, Is.EqualTo(countWithOwner));
            Assert.That(default(ActionHandle).Cancel() || default(ActionHandle).Pause() ||
                default(ActionHandle).Resume() || default(ActionHandle).SetUpdateMode(ActionUpdateModes.UnscaledDeltaTime), Is.False);
        }

        [Test]
        public void AnonymousStartDoesNotAllocateTheNameIndex()
        {
            Own(Kit.Condition(() => false)).Start(_executor);
            Assert.That(typeof(MonoUpdateActionExecutor).GetField("_namedTasks", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(_executor), Is.Null);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t")]
        public void BlankFlagsAreRejectedWithoutConsumingTheAction(string flag)
        {
            var action = Own(Probe.Allocate());
            Assert.Throws<ArgumentException>(() => action.StartNamed(_executor, flag));
            Assert.Throws<ArgumentException>(() => action.ReplaceNamed(_executor, flag));
            Assert.Throws<ArgumentException>(() => Kit.ContainsNamed(_executor, flag));
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(_executor.ScheduledCount, Is.Zero);
        }

        [Test]
        public void NamesAreOrdinalAndDuplicateStartDoesNotConsumeTheNewAction()
        {
            var first = Own(Probe.Allocate()).StartNamed(_executor, "Move");
            var rejected = Own(Probe.Allocate());
            Assert.Throws<InvalidOperationException>(() => rejected.StartNamed(_executor, "Move"));
            Assert.That(first.IsActive, Is.True);
            Assert.That(rejected.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(rejected.IsDeinitialized, Is.False);
            Assert.That(rejected.StartNamed(_executor, "move").IsActive, Is.True);
            Assert.That(Kit.ContainsNamed(_executor, "MOVE"), Is.False);
        }

        [Test]
        public void RegistrationPrecedesSynchronousStartAndSynchronousCompletionLeavesNoName()
        {
            var observed = false;
            var rejected = Own(Probe.Allocate());
            var immediate = Own(Kit.Callback(() =>
            {
                observed = Kit.TryGetNamed(_executor, "now", out var current) && current.IsActive;
                Assert.Throws<InvalidOperationException>(() => rejected.StartNamed(_executor, "now"));
            })).StartNamed(_executor, "now");
            Assert.That(observed, Is.True);
            Assert.That(immediate.IsActive, Is.False);
            Assert.That(Kit.ContainsNamed(_executor, "now"), Is.False);
            Assert.That(rejected.IsDeinitialized, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RootCompletionCallbacksCanCreateSameNameWithoutOldCleanupRemovingIt(bool internalFinish)
        {
            var old = Own(Probe.Allocate());
            var next = Own(Probe.Allocate());
            var replacement = default(ActionHandle);
            Action restart = () =>
            {
                Assert.That(Kit.ContainsNamed(_executor, "root"), Is.False);
                Assert.Throws<InvalidOperationException>(() => old.StartNamed(_executor, "root"));
                replacement = next.StartNamed(_executor, "root");
            };
            if (internalFinish)
            {
                old.Finished = restart;
            }

            var first = old.StartNamed(_executor, "root", internalFinish ? (Action)null : restart);
            old.Complete = true;
            _executor.Tick(1, 1);
            Assert.That(first.IsActive, Is.False);
            Assert.That(replacement.IsActive, Is.True);
            Assert.That(Kit.TryGetNamed(_executor, "root", out var found), Is.True);
            Assert.That(found.Cancel(), Is.True);
            Assert.That(first.Cancel(), Is.False);
        }

        [Test]
        public void RepeatChildCallbacksKeepTheRootNameUntilItsFinalCompletion()
        {
            var calls = 0;
            var repeat = Own(Kit.Repeat(2).Callback(() =>
            {
                calls++;
                Assert.That(Kit.ContainsNamed(_executor, "repeat"), Is.True);
            }));
            var handle = repeat.StartNamed(_executor, "repeat");
            for (var tick = 0; tick < 8 && handle.IsActive; tick++)
            {
                _executor.Tick(0, 0);
            }

            Assert.That(calls, Is.EqualTo(2));
            Assert.That(handle.IsActive, Is.False);
            Assert.That(Kit.ContainsNamed(_executor, "repeat"), Is.False);
        }

        [Test]
        public void ZeroIdResetAndUpdateModeKeepTheLeaseWhilePooledReuseInvalidatesEveryOperation()
        {
            var action = Own(Probe.Allocate());
            action.ActionId = 0;
            var old = action.StartNamed(_executor, "lease");
            var controller = Controller(old);
            Assert.That(old.IsActive, Is.True);
            Assert.That(old.Pause(), Is.True);
            controller.Reset();
            Assert.That(old.IsActive, Is.True);
            Assert.That(old.SetUpdateMode(ActionUpdateModes.UnscaledDeltaTime), Is.True);
            _executor.Tick(0.1f, 0.7f); // Reset 后的首帧仍只调用 OnStart。
            _executor.Tick(0.1f, 0.7f);
            Assert.That(action.Delta, Is.EqualTo(0.7f));
            Assert.That(old.Cancel(), Is.True);
            ActionQueue.Drain();
            var reusedAction = Own(Probe.Allocate());
            Assert.That(reusedAction, Is.SameAs(action));
            reusedAction.ActionId = 0;
            var current = reusedAction.StartNamed(_executor, "lease");
            Assert.That(Controller(current), Is.SameAs(controller));
            Assert.That(old.IsActive || old.Pause() || old.Resume() || old.Cancel() ||
                old.SetUpdateMode(ActionUpdateModes.UnscaledDeltaTime), Is.False);
            Assert.That(current.IsActive, Is.True);
            Assert.That(reusedAction.IsPaused, Is.False);
            Assert.That(controller.UpdateMode, Is.EqualTo(ActionUpdateModes.ScaledDeltaTime));
        }

        [Test]
        public void ReplaceValidatesBeforeCancelAndFailureDoesNotRestoreOrNotifyTheOldTask()
        {
            var successes = 0;
            var oldAction = Own(Probe.Allocate());
            var old = oldAction.StartNamed(_executor, "replace", () => successes++);
            Assert.Throws<InvalidOperationException>(() => oldAction.ReplaceNamed(_executor, "replace"));
            IAction missing = null;
            Assert.Throws<ArgumentNullException>(() => missing.ReplaceNamed(_executor, "replace"));
            var released = Own(Probe.Allocate());
            released.Deinit();
            Assert.Throws<InvalidOperationException>(() => released.ReplaceNamed(_executor, "replace"));
            Assert.That(old.IsActive, Is.True);
            var broken = Own(Probe.Allocate());
            broken.Started = () => throw new InvalidOperationException("new start");
            Assert.Throws<InvalidOperationException>(() => broken.ReplaceNamed(_executor, "replace"));
            Assert.That(old.IsActive, Is.False);
            Assert.That(successes, Is.Zero);
            Assert.That(Kit.ContainsNamed(_executor, "replace"), Is.False);
            Assert.That(broken.IsDeinitialized, Is.True);
            Assert.That(Own(Probe.Allocate()).ReplaceNamed(_executor, "replace").IsActive, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReplacementCleanupReentryOrShutdownRejectsTheOuterStartWithoutConsumingIt(bool shutdown)
        {
            var old = Own(Probe.Allocate());
            var nested = Own(Probe.Allocate());
            var rejected = Own(Probe.Allocate());
            var nestedHandle = default(ActionHandle);
            old.Cleaning = () =>
            {
                if (shutdown)
                {
                    Kit.Shutdown();
                }
                else
                {
                    nestedHandle = nested.StartNamed(_executor, "replace");
                }
            };
            var previous = old.StartNamed(_executor, "replace");
            Assert.Throws<InvalidOperationException>(() => rejected.ReplaceNamed(_executor, "replace"));
            Assert.That(previous.IsActive, Is.False);
            Assert.That(nestedHandle.IsActive, Is.EqualTo(!shutdown));
            Assert.That(rejected.IsDeinitialized, Is.False);
            Assert.That(rejected.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(Kit.ContainsNamed(_executor, "replace"), Is.EqualTo(!shutdown));
        }

        [Test]
        public void CancellationAndCleanupErrorsReleaseNamesAndHostShutdownInvalidatesHandles()
        {
            var faulted = Own(Probe.Allocate());
            faulted.Cleaning = () => throw new InvalidOperationException("cleanup");
            var handle = faulted.StartNamed(_executor, "fault");
            Assert.Throws<InvalidOperationException>(() => handle.Cancel());
            Assert.That(handle.IsActive, Is.False);
            Assert.That(Kit.ContainsNamed(_executor, "fault"), Is.False);
            var external = Own(Probe.Allocate());
            var cancelled = external.StartNamed(_executor, "cancelled");
            Controller(cancelled).Deinit();
            Assert.That(Kit.ContainsNamed(_executor, "cancelled"), Is.False);
            var live = Own(Probe.Allocate()).StartNamed(_executor, "shutdown");
            Kit.Shutdown();
            Assert.That(live.IsActive, Is.False);
            Assert.That(live.Cancel(), Is.False);
            Assert.That(Kit.ContainsNamed(_executor, "shutdown"), Is.False);
        }
    }
}
