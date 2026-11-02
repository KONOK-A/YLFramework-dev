using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DG.Tweening;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Actions;
using Object = UnityEngine.Object;
using YLFramework.Integrations.DOTween;

namespace YLFramework.Tests.Actions.DOTween
{
    using DOTween = global::DG.Tweening.DOTween;

    public class DOTweenActionTests
    {
        private readonly List<ActionLease> _actions = new List<ActionLease>();
        private readonly Dictionary<Tween, object> _tweens = new Dictionary<Tween, object>();
        private Action _restoreSettings;
        private bool _ownsEnvironment;

        [SetUp]
        public void SetUp()
        {
            _ownsEnvironment = false;
            Assert.That(Application.isPlaying, Is.True, "DOTween 1.2.825 must initialize in Play Mode for Kill and recycling tests.");
            Assert.That(Object.FindObjectsOfType<MonoUpdateActionExecutor>(true), Is.Empty, "Do not run alongside another owner's ActionKit controllers.");
            Assert.That(Object.FindObjectsOfType<ActionRecycleDriver>(true), Is.Empty, "Only this fixture's recycle driver may be released.");
            Assert.That(ActionQueue.PendingCount, Is.Zero);
            var contextType = SynchronizationContext.Current?.GetType();
            Assert.That(contextType, Is.Not.Null, "Run in the real Unity Play Mode Test Runner with its original synchronization context.");
            Assert.That(contextType.FullName, Is.EqualTo("UnityEngine.UnitySynchronizationContext"));
            Assert.That(contextType.Assembly, Is.SameAs(typeof(Object).Assembly));
            Assert.That(contextType.IsSealed, Is.True);
            _ownsEnvironment = true;

            var timeScale = DOTween.timeScale;
            var unscaledTimeScale = DOTween.unscaledTimeScale;
            var recyclable = DOTween.defaultRecyclable;
            var safeMode = DOTween.useSafeMode;
            var log = DOTween.logBehaviour;
            var safeLog = DOTween.safeModeLogBehaviour;
            var nestedFailure = DOTween.nestedTweenFailureBehaviour;
            var report = DOTween.showUnityEditorReport;
            var smooth = DOTween.useSmoothDeltaTime;
            var maxSmooth = DOTween.maxSmoothUnscaledTime;
            var gizmos = DOTween.drawGizmos;
            var debug = DOTween.debugMode;
            var targetId = DOTween.debugStoreTargetId;
            var update = DOTween.defaultUpdateType;
            var independent = DOTween.defaultTimeScaleIndependent;
            var autoPlay = DOTween.defaultAutoPlay;
            var autoKill = DOTween.defaultAutoKill;
            var loop = DOTween.defaultLoopType;
            var ease = DOTween.defaultEaseType;
            var overshoot = DOTween.defaultEaseOvershootOrAmplitude;
            var period = DOTween.defaultEasePeriod;
            _restoreSettings = () =>
            {
                DOTween.timeScale = timeScale;
                DOTween.unscaledTimeScale = unscaledTimeScale;
                DOTween.defaultRecyclable = recyclable;
                DOTween.useSafeMode = safeMode;
                DOTween.logBehaviour = log;
                DOTween.safeModeLogBehaviour = safeLog;
                DOTween.nestedTweenFailureBehaviour = nestedFailure;
                DOTween.showUnityEditorReport = report;
                DOTween.useSmoothDeltaTime = smooth;
                DOTween.maxSmoothUnscaledTime = maxSmooth;
                DOTween.drawGizmos = gizmos;
                DOTween.debugMode = debug;
                DOTween.debugStoreTargetId = targetId;
                DOTween.defaultUpdateType = update;
                DOTween.defaultTimeScaleIndependent = independent;
                DOTween.defaultAutoPlay = autoPlay;
                DOTween.defaultAutoKill = autoKill;
                DOTween.defaultLoopType = loop;
                DOTween.defaultEaseType = ease;
                DOTween.defaultEaseOvershootOrAmplitude = overshoot;
                DOTween.defaultEasePeriod = period;
            };
            // First Init can load DOTweenSettings. Preserve the caller's settings and retain the service.
            Assert.That(DOTween.Init(recyclable, safeMode, log), Is.Not.Null,
                "A live DOTween service must be initialized before creating lifecycle test tweens.");
            _restoreSettings();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_ownsEnvironment)
            {
                yield break;
            }

            try
            {
                // Only the current fixture-created source may be cleared, never a foreign pool reuse.
                foreach (var entry in _tweens)
                {
                    var tween = entry.Key;
                    if (!tween.IsActive() || !ReferenceEquals(tween.target, entry.Value))
                    {
                        continue;
                    }

                    tween.onComplete = tween.onKill = tween.onPlay = tween.onPause = null;
                    tween.OnStart(null);
                }
                foreach (var lease in _actions)
                {
                    if (lease.Matches && !lease.Action.IsDeinitialized)
                    {
                        lease.Action.Deinit();
                    }
                }

                ActionQueue.Drain();
                ActionRecycleDriver.ReleaseAll();
                // Drain is not the Unity context queue. Let pending posts leave their callback stacks.
                yield return UnityContextTurn();
                yield return null;
            }
            finally
            {
                foreach (var entry in _tweens)
                {
                    if (entry.Key.IsActive() && ReferenceEquals(entry.Key.target, entry.Value))
                    {
                        entry.Key.Kill(false);
                    }
                }

                _actions.Clear();
                _tweens.Clear();
                _restoreSettings?.Invoke();
                _restoreSettings = null;
                _ownsEnvironment = false;
            }
        }

        private T Own<T>(T action) where T : IAction
        {
            _actions.Add(new ActionLease(action));
            return action;
        }

        private Tween NewTween(bool autoKill = true, bool independent = false, bool preservePause = false)
        {
            var value = 0f;
            var source = new object();
            var tween = DOTween.To(() => value, next => value = next, 1f, 1f)
                .SetUpdate(UpdateType.Manual, independent).SetEase(Ease.Linear)
                .SetAutoKill(autoKill).SetRecyclable(false).SetTarget(source);
            // 1.2.825 retains onPause in pooled objects; never inherit an unrelated cached callback.
            // The sole preservation case explicitly Play()s the reused tween without calling its old onPause.
            if (!preservePause)
            {
                tween.OnPause(null).Pause();
            }

            _tweens[tween] = source;
            return tween;
        }

        private static IEnumerator UnityContextTurn()
        {
            var executed = false;
            SynchronizationContext.Current.Post(_ => executed = true, null);
            var limit = Time.realtimeSinceStartup + 2f;
            while (!executed && Time.realtimeSinceStartup < limit)
            {
                yield return null;
            }

            Assert.That(executed, Is.True, "The original Unity context must process Post through a real Player tick; no nested Exec pump is used.");
        }

        private void Advance(float scaled = 1.1f, float unscaled = 1.1f)
        {
            // Supply the fixture clock directly, formerly global scale=1, without changing another owner's clock.
            // Allocation-specific tokens also isolate recycling and additions made by callbacks.
            foreach (var entry in _tweens.ToArray())
            {
                if (entry.Key.IsActive() && ReferenceEquals(entry.Key.target, entry.Value))
                {
                    entry.Key.ManualUpdate(scaled, unscaled);
                }
            }
        }

        [Test]
        public void OwnedManualAdvanceLeavesUntrackedTweensAndGlobalTimeScalesUntouched()
        {
            var value = 0f;
            var token = new object();
            var complete = 0;
            var killed = 0;
            var foreign = DOTween.To(() => value, next => value = next, 1f, 1f)
                .SetUpdate(UpdateType.Manual).SetAutoKill(false).SetRecyclable(false).SetTarget(token)
                .OnPause(null).OnComplete(() => complete++).OnKill(() => killed++).Play();
            try
            {
                var elapsed = foreign.Elapsed();
                var timeScale = DOTween.timeScale;
                var unscaledScale = DOTween.unscaledTimeScale;
                var own = NewTween(false).Play();
                Advance();
                Assert.That(own.IsComplete(), Is.True);
                Assert.That(foreign.IsActive(), Is.True);
                Assert.That(foreign.IsPlaying(), Is.True);
                Assert.That(foreign.Elapsed(), Is.EqualTo(elapsed));
                Assert.That(value, Is.Zero);
                Assert.That(complete, Is.Zero);
                Assert.That(killed, Is.Zero);
                Assert.That(DOTween.timeScale, Is.EqualTo(timeScale));
                Assert.That(DOTween.unscaledTimeScale, Is.EqualTo(unscaledScale));
            }
            finally
            {
                if (foreign.IsActive() && ReferenceEquals(foreign.target, token))
                {
                    foreign.Kill(false);
                }
            }
        }

        [Test]
        public void OwnedManualAdvanceDefersTweensAddedByCallbacksUntilTheNextBatch()
        {
            Tween next = null;
            NewTween().OnComplete(() => next = NewTween(false).Play()).Play();
            Advance();
            Assert.That(next, Is.Not.Null);
            Assert.That(next.IsActive(), Is.True);
            Assert.That(next.Elapsed(), Is.Zero);
            Advance();
            Assert.That(next.IsComplete(), Is.True);
        }

        [Test]
        public void FactoryRunsOnceAndActionDeltaDoesNotDriveTheTween()
        {
            var calls = 0;
            Tween tween = null;
            var action = Own(DOTweenAction.Allocate(() =>
            {
                calls++;
                return tween = NewTween();
            }));
            Assert.That(action.Execute(100), Is.False);
            Assert.That(action.Execute(100), Is.False);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(tween.Elapsed(), Is.Zero);
            Advance();
            Assert.That(action.Execute(0), Is.True);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void ToActionPausesImmediatelyAndStartRestartsTheExistingTween()
        {
            var tween = NewTween(false).Play();
            Advance(0.3f, 0.3f);
            Assert.That(tween.Elapsed(), Is.GreaterThan(0));
            var action = Own(tween.ToAction());
            Assert.That(tween.IsPlaying(), Is.False);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(tween.IsPlaying(), Is.True);
            Assert.That(tween.Elapsed(), Is.Zero);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void NaturalCompletionSucceedsWithEitherAutoKillSetting(bool autoKill)
        {
            var completions = 0;
            var kills = 0;
            var tween = NewTween(autoKill).OnComplete(() => completions++).OnKill(() => kills++);
            var action = Own(tween.ToAction());
            action.Execute(0);
            Advance();
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Finished));
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(action.Execute(0), Is.True);
            Assert.That(completions, Is.EqualTo(1));
            Assert.That(kills, Is.EqualTo(autoKill ? 1 : 0));
            Assert.That(tween.IsActive(), Is.EqualTo(!autoKill));
            action.Deinit();
            Assert.That(kills, Is.EqualTo(autoKill ? 1 : 0), "A finished lease must detach, not kill an autoKill=false tween.");
        }

        [Test]
        public void ExternalPauseIsNotCompletionAndExternalPlayCanContinue()
        {
            var tween = NewTween();
            var action = Own(tween.ToAction());
            action.Execute(0);
            tween.Pause();
            Advance();
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(action.IsDeinitialized, Is.False);
            tween.Play();
            Advance();
            Assert.That(action.Execute(0), Is.True);
        }

        [Test]
        public void KillFalseCancelsWithoutSuccess()
        {
            var complete = 0;
            var tween = NewTween().OnComplete(() => complete++);
            var action = Own(tween.ToAction());
            action.Execute(0);
            tween.Kill(false);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(complete, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CompleteAndKillTrueSucceedOnlyAfterActualCompletion(bool kill)
        {
            var complete = 0;
            var tween = NewTween(false).OnComplete(() => complete++);
            var action = Own(tween.ToAction());
            action.Execute(0);
            if (kill)
            {
                tween.Kill(true);
            }
            else
            {
                tween.Complete();
            }

            Assert.That(complete, Is.EqualTo(1));
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(action.Execute(0), Is.True);
            Assert.That(tween.IsActive(), Is.EqualTo(!kill));
        }

        private IEnumerator CheckDeinitOfPausedTween(bool actionPause)
        {
            var complete = 0;
            var killed = 0;
            var tween = NewTween(false).OnComplete(() => complete++).OnKill(() => killed++);
            var action = Own(tween.ToAction());
            action.Execute(0);
            if (actionPause)
            {
                action.IsPaused = true;
            }
            else
            {
                tween.Pause();
            }

            action.Deinit();
            action.Deinit();
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Execute(0), Is.False);
            Assert.That(tween.IsActive(), Is.True);
            Assert.That(killed, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => tween.ToAction(), "Logical close retains the source reservation until physical Kill.");
            var competing = Own(DOTweenAction.Allocate(() => tween));
            Assert.Throws<InvalidOperationException>(() => competing.Execute(0));
            Assert.That(competing.IsDeinitialized, Is.True);
            ActionQueue.Drain();
            Assert.That(tween.IsActive(), Is.True, "ActionQueue.Drain must not synchronously run posted Unity cleanup.");
            yield return UnityContextTurn();
            Assert.That(tween.IsActive(), Is.False);
            Assert.That(killed, Is.EqualTo(1));
            Assert.That(complete, Is.Zero);
            Assert.That(action.Status, Is.Not.EqualTo(ActionStatus.Finished));
        }

        [UnityTest]
        public IEnumerator DeinitKillsAnExternallyPausedTweenWithoutCompletingIt() => CheckDeinitOfPausedTween(false);

        [UnityTest]
        public IEnumerator DeinitKillsAnActionPausedTweenWithoutCompletingIt() => CheckDeinitOfPausedTween(true);

        [Test]
        public void ActionPauseResumeForwardsOnlyItsOwnedPause()
        {
            var tween = NewTween();
            var action = Own(tween.ToAction());
            action.Execute(0);
            action.IsPaused = true;
            Assert.That(tween.IsPlaying(), Is.False);
            Advance(0.4f, 0.4f);
            Assert.That(tween.Elapsed(), Is.Zero);
            action.IsPaused = false;
            Assert.That(tween.IsPlaying(), Is.True);
            Advance(0.4f, 0.4f);
            Assert.That(tween.Elapsed(), Is.EqualTo(0.4f).Within(0.001f));
            tween.Pause();
            action.IsPaused = true;
            action.IsPaused = false;
            Assert.That(tween.IsPlaying(), Is.False, "Resume must not steal an external pause.");
            Assert.That(action.Execute(0), Is.False);
        }

        [Test]
        public void PausedBeforeStartupPausesTheNewTweenUntilResume()
        {
            var tween = NewTween();
            var action = Own(tween.ToAction());
            action.IsPaused = true;
            action.Execute(0);
            Assert.That(tween.IsPlaying(), Is.False);
            Advance();
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            action.IsPaused = false;
            Advance();
            Assert.That(action.Execute(0), Is.True);
        }

        [Test]
        public void CallbacksAreAppendedAndFinishDetachesOnlyLeaseCallbacks()
        {
            var trace = new List<string>();
            TweenCallback complete = () => trace.Add("before-complete");
            TweenCallback kill = () => trace.Add("before-kill");
            TweenCallback appended = () => trace.Add("after-complete");
            TweenCallback appendedKill = () => trace.Add("after-kill");
            var tween = NewTween(false).OnComplete(complete).OnKill(kill);
            var action = Own(tween.ToAction());
            action.Execute(0);
            tween.onComplete += appended;
            tween.onKill += appendedKill;
            Assert.That(tween.onComplete.GetInvocationList().Length, Is.EqualTo(3));
            Assert.That(tween.onKill.GetInvocationList().Length, Is.EqualTo(3));
            Advance();
            Assert.That(action.Execute(0), Is.True);
            Assert.That(tween.onComplete, Is.EqualTo(complete + appended));
            Assert.That(tween.onKill, Is.EqualTo(kill + appendedKill));
            action.Deinit();
            tween.Kill(false);
            CollectionAssert.AreEqual(new[] { "before-complete", "after-complete", "before-kill", "after-kill" }, trace);
        }

        [UnityTest]
        public IEnumerator ResetKillsTheOldPausedLeaseAndCallsFreshFactoryOnNextExecution()
        {
            var created = new List<Tween>();
            var action = Own(DOTweenAction.Allocate(() =>
            {
                var tween = NewTween();
                created.Add(tween);
                return tween;
            }));
            action.Execute(0);
            action.IsPaused = true;
            var version = ((IActionExecutionVersion)action).ExecutionVersion;
            action.Reset();
            Assert.That(created[0].IsActive(), Is.True);
            Assert.Throws<InvalidOperationException>(() => created[0].ToAction());
            Assert.That(action.IsPaused, Is.False);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(((IActionExecutionVersion)action).ExecutionVersion, Is.GreaterThan(version));
            action.Execute(0);
            Assert.That(created.Count, Is.EqualTo(2));
            Assert.That(created[1], Is.Not.SameAs(created[0]));
            Assert.That(created[1].IsPlaying(), Is.True);
            yield return UnityContextTurn();
            Assert.That(created[0].IsActive(), Is.False);
            Assert.That(created[1].IsPlaying(), Is.True);
        }

        [UnityTest]
        public IEnumerator FactoryDeinitBeforeReturningKillsTheReturnedTweenWithoutStartingIt()
        {
            DOTweenAction action = null;
            Tween returned = null;
            var starts = 0;
            action = Own(DOTweenAction.Allocate(() =>
            {
                action.Deinit();
                return returned = NewTween().OnStart(() => starts++);
            }));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(returned.IsActive(), Is.True);
            Assert.Throws<InvalidOperationException>(() => returned.ToAction());
            Advance();
            Assert.That(starts, Is.Zero);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
            yield return UnityContextTurn();
            Assert.That(returned.IsActive(), Is.False);
            Assert.That(starts, Is.Zero);
        }

        [UnityTest]
        public IEnumerator FactoryResetBeforeReturningCannotOverwriteTheNewExecutionVersion()
        {
            DOTweenAction action = null;
            Tween abandoned = null;
            var calls = 0;
            action = Own(DOTweenAction.Allocate(() =>
            {
                calls++;
                if (calls == 1)
                {
                    action.Reset();
                }

                var tween = NewTween();
                if (calls == 1)
                {
                    abandoned = tween;
                }

                return tween;
            }));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(abandoned.IsActive(), Is.True);
            Assert.Throws<InvalidOperationException>(() => abandoned.ToAction());
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(action.IsDeinitialized, Is.False);
            action.Execute(0);
            Assert.That(calls, Is.EqualTo(2));
            Advance();
            Assert.That(action.Execute(0), Is.True);
            yield return UnityContextTurn();
            Assert.That(abandoned.IsActive(), Is.False);
        }

        [UnityTest]
        public IEnumerator NestedStartupInsideFactoryKeepsTheInnerLeaseAndKillsTheOuterReturn()
        {
            DOTweenAction action = null;
            Tween outer = null;
            Tween inner = null;
            var calls = 0;
            action = Own(DOTweenAction.Allocate(() =>
            {
                calls++;
                if (calls == 1)
                {
                    action.Reset();
                    Assert.That(action.Execute(0), Is.False);
                    return outer = NewTween();
                }
                return inner = NewTween();
            }));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(outer.IsActive(), Is.True);
            Assert.Throws<InvalidOperationException>(() => outer.ToAction());
            Assert.That(inner.IsPlaying(), Is.True);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Advance();
            Assert.That(action.Execute(0), Is.True);
            yield return UnityContextTurn();
            Assert.That(outer.IsActive(), Is.False);
        }

        [UnityTest]
        public IEnumerator FactoryDeinitDrainAndReuseCannotChangeTheNewUnstartedAction()
        {
            DOTweenAction original = null;
            DOTweenAction fresh = null;
            Tween abandoned = null;
            var next = NewTween();
            original = Own(DOTweenAction.Allocate(() =>
            {
                original.Deinit();
                ActionQueue.Drain();
                fresh = Own(DOTweenAction.Allocate(() => next));
                return abandoned = NewTween();
            }));
            var originalId = original.ActionId;
            Assert.That(original.Execute(0), Is.False);
            Assert.That(fresh, Is.SameAs(original));
            Assert.That(fresh.ActionId, Is.Not.EqualTo(originalId));
            Assert.That(fresh.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(fresh.IsDeinitialized, Is.False);
            Assert.That(abandoned.IsActive(), Is.True);
            Assert.Throws<InvalidOperationException>(() => abandoned.ToAction());
            fresh.Execute(0);
            Assert.That(next.IsPlaying(), Is.True);
            next.Complete();
            Assert.That(fresh.Execute(0), Is.True);
            yield return UnityContextTurn();
            Assert.That(abandoned.IsActive(), Is.False);
        }

        private IEnumerator CheckSynchronousOnPlayCancel(bool reset)
        {
            var tween = NewTween(false).Play();
            Advance(0.1f, 0.1f);
            tween.Pause();
            DOTweenAction action = null;
            var plays = 0;
            tween.OnPlay(() =>
            {
                plays++;
                if (reset)
                {
                    action.Reset();
                }
                else
                {
                    action.Deinit();
                }
            });
            action = Own(DOTweenAction.Allocate(() => tween));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(plays, Is.EqualTo(1), "The real Restart must invoke the previously initialized tween's OnPlay synchronously.");
            Assert.That(tween.IsActive(), Is.True);
            Assert.Throws<InvalidOperationException>(() => tween.ToAction());
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(action.IsDeinitialized, Is.EqualTo(!reset));
            yield return UnityContextTurn();
            Assert.That(tween.IsActive(), Is.False);
        }

        [UnityTest]
        public IEnumerator SynchronousUserOnPlayCanDeinitDuringRestart() => CheckSynchronousOnPlayCancel(false);

        [UnityTest]
        public IEnumerator SynchronousUserOnPlayCanResetDuringRestart() => CheckSynchronousOnPlayCancel(true);

        [UnityTest]
        public IEnumerator SynchronousOnPlayResetAndNestedStartupKeepTheReplacementLease()
        {
            var first = NewTween(false).Play();
            Advance(0.1f, 0.1f);
            first.Pause();
            var next = NewTween();
            var factories = 0;
            DOTweenAction action = null;
            first.OnPlay(() =>
            {
                action.Reset();
                action.Execute(0);
            });
            action = Own(DOTweenAction.Allocate(() => ++factories == 1 ? first : next));
            Assert.That(action.Execute(0), Is.False);
            Assert.That(factories, Is.EqualTo(2));
            Assert.That(first.IsActive(), Is.True);
            Assert.Throws<InvalidOperationException>(() => first.ToAction());
            Assert.That(next.IsPlaying(), Is.True);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(action.IsDeinitialized, Is.False);
            next.Complete();
            Assert.That(action.Execute(0), Is.True);
            yield return UnityContextTurn();
            Assert.That(first.IsActive(), Is.False);
        }

        [UnityTest]
        public IEnumerator UserOnStartCanDeinitWithoutLateSuccess()
        {
            DOTweenAction action = null;
            var tween = NewTween(false).OnStart(() => action.Deinit());
            action = Own(tween.ToAction() as DOTweenAction);
            action.Execute(0);
            Advance(0.1f, 0.1f);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(tween.IsActive(), Is.True);
            Assert.Throws<InvalidOperationException>(() => tween.ToAction());
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.Status, Is.Not.EqualTo(ActionStatus.Finished));
            yield return UnityContextTurn();
            Assert.That(tween.IsActive(), Is.False);
        }

        [Test]
        public void UserOnCompleteResetAndNestedStartDoNotFinishTheNewLease()
        {
            DOTweenAction action = null;
            Tween next = null;
            var calls = 0;
            action = Own(DOTweenAction.Allocate(() =>
            {
                calls++;
                var tween = NewTween(false);
                if (calls == 1)
                {
                    tween.OnComplete(() =>
                    {
                        action.Reset();
                        action.Execute(0);
                    });
                }
                else
                {
                    next = tween;
                }

                return tween;
            }));
            action.Execute(0);
            Advance();
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(next.IsActive(), Is.True);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(action.IsDeinitialized, Is.False);
            next.Complete();
            Assert.That(action.Execute(0), Is.True);
        }

        [UnityTest]
        public IEnumerator UserOnKillCanStartAReplacementWithoutOldKillCancellingIt()
        {
            DOTweenAction action = null;
            Tween old = null;
            Tween next = null;
            var calls = 0;
            action = Own(DOTweenAction.Allocate(() =>
            {
                calls++;
                var tween = NewTween();
                if (calls == 1)
                {
                    old = tween.OnKill(() =>
                    {
                        action.Reset();
                        action.Execute(0);
                    });
                }
                else
                {
                    next = tween;
                }

                return tween;
            }));
            action.Execute(0);
            action.Reset();
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(old.IsActive(), Is.True);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.Throws<InvalidOperationException>(() => old.ToAction());
            yield return UnityContextTurn();
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(old.IsActive(), Is.False);
            Assert.That(next.IsPlaying(), Is.True);
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
        }

        [UnityTest]
        public IEnumerator LateOldCallbacksCannotAffectTheRecycledActionInstance()
        {
            var oldTween = NewTween(false);
            var oldAction = Own(oldTween.ToAction() as DOTweenAction);
            oldAction.Execute(0);
            var lateComplete = oldTween.onComplete;
            var lateKill = oldTween.onKill;
            var oldId = oldAction.ActionId;
            oldAction.Deinit();
            ActionQueue.Drain();
            Assert.That(oldTween.IsActive(), Is.True);
            Assert.Throws<InvalidOperationException>(() => oldTween.ToAction());
            var nextTween = NewTween();
            var reused = Own(DOTweenAction.Allocate(() => nextTween));
            Assert.That(reused, Is.SameAs(oldAction));
            Assert.That(reused.ActionId, Is.Not.EqualTo(oldId));
            reused.Execute(0);
            lateComplete();
            lateKill();
            Assert.That(reused.IsDeinitialized, Is.False);
            Assert.That(reused.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(nextTween.IsPlaying(), Is.True);
            yield return UnityContextTurn();
            Assert.That(oldTween.IsActive(), Is.False);
            Assert.That(reused.IsDeinitialized, Is.False);
            Assert.That(nextTween.IsPlaying(), Is.True);
        }

        [Test]
        public void RecyclableTweenReuseCannotBeKilledByTheOldAction()
        {
            var oldTween = NewTween().SetRecyclable(true);
            var action = Own(oldTween.ToAction());
            action.Execute(0);
            oldTween.Kill(false);
            var reused = NewTween();
            Assert.That(reused, Is.SameAs(oldTween), "This test must exercise real DOTween pool reuse, not merely two distinct tween references.");
            reused.Play();
            action.Deinit();
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(reused.IsActive(), Is.True);
            Assert.That(reused.IsPlaying(), Is.True);
        }

        [UnityTest]
        public IEnumerator ToActionBeforeStartDoesNotClaimATweenReusedAfterExternalKill()
        {
            var tween = NewTween().SetRecyclable(true);
            var action = Own(tween.ToAction());
            tween.Kill(false);
            var foreign = NewTween().Play();
            Assert.That(foreign, Is.SameAs(tween));
            Advance(0.2f, 0.2f);
            var position = foreign.Elapsed();
            Assert.That(action.Execute(0), Is.False);
            action.Deinit();
            Assert.That(action.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(foreign.IsActive(), Is.True);
            Assert.That(foreign.IsPlaying(), Is.True);
            Assert.That(foreign.Elapsed(), Is.EqualTo(position));
            yield return UnityContextTurn();
            Assert.That(foreign.IsActive(), Is.True);
            Assert.That(foreign.IsPlaying(), Is.True);
            Assert.That(foreign.Elapsed(), Is.EqualTo(position));
        }

        [UnityTest]
        public IEnumerator ToActionOnPauseKillAndReuseCannotTransferOwnershipToTheNewTween()
        {
            var tween = NewTween().SetRecyclable(true).Play();
            Advance(0.1f, 0.1f);
            var pauses = 0;
            Tween foreign = null;
            tween.OnPause(() =>
            {
                pauses++;
                tween.Kill(false);
                foreign = NewTween().Play();
            });
            var action = Own(tween.ToAction());
            Assert.That(pauses, Is.EqualTo(1), "Exercise the real synchronous Pause callback, not an unstarted tween.");
            Assert.That(foreign, Is.SameAs(tween));
            Advance(0.2f, 0.2f);
            var position = foreign.Elapsed();
            Assert.That(action.Execute(0), Is.False);
            action.Deinit();
            Assert.That(foreign.IsActive(), Is.True);
            Assert.That(foreign.IsPlaying(), Is.True);
            Assert.That(action.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(foreign.Elapsed(), Is.EqualTo(position));
            yield return UnityContextTurn();
            Assert.That(foreign.IsActive(), Is.True);
            Assert.That(foreign.IsPlaying(), Is.True);
            Assert.That(foreign.Elapsed(), Is.EqualTo(position));
        }

        private IEnumerator CheckExternalOnKillReentrancy(bool reset)
        {
            IAction action = null;
            var kills = 0;
            var tween = NewTween().SetRecyclable(true).OnKill(() =>
            {
                kills++;
                if (reset)
                {
                    action.Reset();
                }
                else
                {
                    action.Deinit();
                }
            });
            action = Own(tween.ToAction());
            action.Execute(0);
            Assert.DoesNotThrow(() => tween.Kill(false));
            Assert.That(kills, Is.EqualTo(1));
            Assert.That(tween.IsActive(), Is.False);
            Assert.That(action.Status, Is.Not.EqualTo(ActionStatus.Finished));
            var first = NewTween();
            var second = NewTween();
            Assert.That(first, Is.SameAs(tween));
            Assert.That(second, Is.Not.SameAs(first), "A recursive Despawn must not put the same object in the pool twice.");
            action.Deinit();
            Assert.That(first.IsActive(), Is.True);
            Assert.That(second.IsActive(), Is.True);
            yield return UnityContextTurn();
            Assert.That(first.IsActive(), Is.True);
            Assert.That(second.IsActive(), Is.True);
            Assert.That(kills, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ExternalOnKillReentrantDeinitDoesNotDespawnTwice() => CheckExternalOnKillReentrancy(false);

        [UnityTest]
        public IEnumerator ExternalOnKillReentrantResetDoesNotDespawnTwice() => CheckExternalOnKillReentrancy(true);

        [Test]
        public void OverwrittenKillMarkerConservativelyCancelsWithoutKillingActiveTween()
        {
            var tween = NewTween(false);
            var action = Own(tween.ToAction());
            action.Execute(0);
            var replacementCalls = 0;
            tween.OnKill(() => replacementCalls++);
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(tween.IsActive(), Is.True);
            Assert.That(tween.IsPlaying(), Is.True);
            Assert.That(replacementCalls, Is.Zero);
        }

        [Test]
        public void LostMarkerAndLateOldKillDoNotTouchTheForeignReusedTween()
        {
            var tween = NewTween().SetRecyclable(true);
            var action = Own(tween.ToAction());
            action.Execute(0);
            var lateKill = tween.onKill;
            tween.OnKill(() => { });
            tween.Kill(false);
            var foreign = NewTween().Play();
            Assert.That(foreign, Is.SameAs(tween));
            Assert.That(action.IsDeinitialized, Is.False, "Replacing OnKill is an unsupported observation boundary, not reliable immediate cancellation.");
            lateKill();
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(foreign.IsActive(), Is.True);
            Assert.That(foreign.IsPlaying(), Is.True);
        }

        [Test]
        public void LostKillMarkerIsNotReplacedByTheSurvivingOnPauseCallback()
        {
            var pauses = 0;
            var tween = NewTween().SetRecyclable(true).OnPause(() => pauses++);
            var action = Own(tween.ToAction());
            action.Execute(0);
            tween.OnKill(() => { });
            tween.Kill(false);
            var foreign = NewTween(preservePause: true).Play();
            Assert.That(foreign, Is.SameAs(tween));
            Assert.That(foreign.onPause, Is.Not.Null, "DOTween 1.2.825 keeps onPause across Reset; it must not be an ownership marker.");
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(foreign.IsActive(), Is.True);
            Assert.That(pauses, Is.Zero);
        }

        [Test]
        public void DuplicateTweenOwnerIsRejectedWithoutCancellingTheFirstOwner()
        {
            var tween = NewTween();
            var first = Own(tween.ToAction());
            Assert.Throws<InvalidOperationException>(() => tween.ToAction(), "Pre-start binding already reserves the source.");
            first.Execute(0);
            Assert.Throws<InvalidOperationException>(() => tween.ToAction());
            var second = Own(DOTweenAction.Allocate(() => tween));
            Assert.Throws<InvalidOperationException>(() => second.Execute(0));
            Assert.That(second.IsDeinitialized, Is.True);
            Assert.That(first.IsDeinitialized, Is.False);
            Assert.That(first.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(tween.IsPlaying(), Is.True);
            Advance();
            Assert.That(first.Execute(0), Is.True);
        }

        [Test]
        public void NullFactoryAndNullTweenAreRejected()
        {
            Assert.Throws<ArgumentNullException>(() => DOTweenAction.Allocate(null));
            Assert.Throws<ArgumentNullException>(() => ((Tween)null).ToAction());
            var action = Own(DOTweenAction.Allocate(() => null));
            Assert.Throws<ArgumentNullException>(() => action.Execute(0));
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
        }

        [Test]
        public void InactiveTweenIsRejectedByBothEntryPoints()
        {
            var tween = NewTween();
            tween.Kill(false);
            Assert.Throws<InvalidOperationException>(() => tween.ToAction());
            var action = Own(DOTweenAction.Allocate(() => tween));
            Assert.Throws<InvalidOperationException>(() => action.Execute(0));
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
        }

        [Test]
        public void ThrowingFactoryPropagatesTheOriginalExceptionAndCancels()
        {
            var error = new InvalidOperationException("fixture factory failed");
            var action = Own(DOTweenAction.Allocate(() => throw error));
            Assert.That(Assert.Throws<InvalidOperationException>(() => action.Execute(0)), Is.SameAs(error));
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingOrNonUnityContextRejectsBeforeFactoryOrPause(bool nonUnityContext)
        {
            var originalContext = SynchronizationContext.Current;
            var tween = NewTween(false).Play();
            Advance(0.2f, 0.2f);
            var position = tween.Elapsed();
            var pauses = 0;
            tween.OnPause(() => pauses++);
            var complete = tween.onComplete;
            var kill = tween.onKill;
            var factories = 0;
            var action = Own(DOTweenAction.Allocate(() =>
            {
                factories++;
                return tween;
            }));
            try
            {
                SynchronizationContext.SetSynchronizationContext(nonUnityContext ? new SynchronizationContext() : null);
                Assert.Throws<InvalidOperationException>(() => action.Execute(0));
                Assert.Throws<InvalidOperationException>(() => tween.ToAction());
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(originalContext);
            }
            Assert.That(factories, Is.Zero);
            Assert.That(pauses, Is.Zero);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(tween.IsActive(), Is.True);
            Assert.That(tween.IsPlaying(), Is.True);
            Assert.That(tween.Elapsed(), Is.EqualTo(position));
            Assert.That(tween.onComplete, Is.SameAs(complete));
            Assert.That(tween.onKill, Is.SameAs(kill));
        }

        [Test]
        public void WorkerWithOriginalUnityContextStillRejectsBeforeFactoryOrPause()
        {
            var originalContext = SynchronizationContext.Current;
            var tween = NewTween(false).Play();
            var pauses = 0;
            tween.OnPause(() => pauses++);
            var complete = tween.onComplete;
            var kill = tween.onKill;
            var factories = 0;
            var action = Own(DOTweenAction.Allocate(() =>
            {
                factories++;
                return tween;
            }));
            Exception startError = null;
            Exception bindError = null;
            var worker = new Thread(() =>
            {
                var previous = SynchronizationContext.Current;
                try
                {
                    // Reuse the genuine captured context to test the thread check, not a fake type.
                    SynchronizationContext.SetSynchronizationContext(originalContext);
                    try
                    {
                        action.Execute(0);
                    }
                    catch (Exception exception)
                    {
                        startError = exception;
                    }
                    try
                    {
                        tween.ToAction();
                    }
                    catch (Exception exception)
                    {
                        bindError = exception;
                    }
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }
            });
            worker.IsBackground = true;
            worker.Start();
            Assert.That(worker.Join(2000), Is.True, "The rejection path must not wait for Unity or execute a Tween factory on a worker.");
            Assert.That(startError, Is.TypeOf<InvalidOperationException>());
            Assert.That(bindError, Is.TypeOf<InvalidOperationException>());
            Assert.That(factories, Is.Zero);
            Assert.That(pauses, Is.Zero);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(tween.IsActive(), Is.True);
            Assert.That(tween.IsPlaying(), Is.True);
            Assert.That(tween.onComplete, Is.SameAs(complete));
            Assert.That(tween.onKill, Is.SameAs(kill));
            Assert.That(SynchronizationContext.Current, Is.SameAs(originalContext));
        }

        [UnityTest]
        public IEnumerator BoundResetClosesReservationAndCannotRestartTheSameTween()
        {
            var tween = NewTween(false);
            var action = Own(tween.ToAction());
            action.Execute(0);
            Advance(0.3f, 0.3f);
            var position = tween.Elapsed();
            action.Reset();
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(tween.IsActive(), Is.True);
            Assert.Throws<InvalidOperationException>(() => tween.ToAction());
            Assert.That(action.Execute(0), Is.False);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(tween.Elapsed(), Is.EqualTo(position), "Bound startup must not reattach or Restart after Reset.");
            yield return UnityContextTurn();
            Assert.That(tween.IsActive(), Is.False);
            Assert.That(action.Execute(0), Is.False);
        }

        [Test]
        public void BoundPreFirstResetPreservesTheOriginalMarkerWithoutReattaching()
        {
            var kills = 0;
            TweenCallback userKill = () => kills++;
            var tween = NewTween(false).OnKill(userKill);
            var action = Own(tween.ToAction() as DOTweenAction);
            var completeMarker = tween.onComplete;
            var killMarker = tween.onKill;
            var version = ((IActionExecutionVersion)action).ExecutionVersion;
            action.Reset();
            action.Reset();
            Assert.That(((IActionExecutionVersion)action).ExecutionVersion, Is.GreaterThan(version));
            Assert.That(action.Status, Is.EqualTo(ActionStatus.NotStart));
            Assert.That(action.IsDeinitialized, Is.False);
            Assert.That(tween.onComplete, Is.SameAs(completeMarker));
            Assert.That(tween.onKill, Is.SameAs(killMarker));
            Assert.That(tween.onKill.GetInvocationList().Length, Is.EqualTo(2));
            Assert.Throws<InvalidOperationException>(() => tween.ToAction());
            Assert.That(action.Execute(0), Is.False);
            Assert.That(tween.IsPlaying(), Is.True);
            Advance();
            Assert.That(action.Execute(0), Is.True);
            Assert.That(tween.onKill, Is.SameAs(userKill));
            Assert.That(kills, Is.Zero);
        }

        [UnityTest]
        public IEnumerator SequenceAppendBoundTweenPreservesItsFirstExecutionAndUserCallbacks()
        {
            var calls = 0;
            var completes = 0;
            var kills = 0;
            TweenCallback userKill = () => kills++;
            var tween = NewTween(false).OnComplete(() => completes++).OnKill(userKill);
            var child = Own(tween.ToAction());
            var marker = tween.onKill;
            var sequence = Own(ActionKit.Sequence().Append(child).Append(Own(ActionKit.Callback(() => calls++))));
            Assert.That(sequence.Execute(0), Is.False);
            Assert.That(tween.onKill, Is.SameAs(marker), "Sequence startup Reset must preserve the original prebinding, not detach and reacquire.");
            Assert.That(child.IsDeinitialized, Is.False);
            Assert.That(tween.IsPlaying(), Is.True);
            Advance();
            Assert.That(sequence.Execute(0), Is.True);
            Assert.That(completes, Is.EqualTo(1));
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(tween.onKill, Is.SameAs(userKill));
            sequence.Deinit();
            yield return UnityContextTurn();
            Assert.That(tween.IsActive(), Is.True);
            Assert.That(kills, Is.Zero, "Completed autoKill=false ownership is returned to the user, not physically killed later.");
        }

        [Test]
        public void PreStartCompletionThenCompositeResetDoesNotCompleteTheRestartedBindingEarly()
        {
            var completes = 0;
            var calls = 0;
            var tween = NewTween(false).OnComplete(() => completes++);
            var child = Own(tween.ToAction());
            tween.Complete();
            Assert.That(completes, Is.EqualTo(1));
            Assert.That(tween.IsComplete(), Is.True);
            Assert.That(child.Status, Is.EqualTo(ActionStatus.NotStart), "Pre-start completion must not skip the first bound startup.");
            Assert.That(child.IsDeinitialized, Is.False);
            var marker = tween.onKill;
            var sequence = Own(ActionKit.Sequence().Append(child).Append(Own(ActionKit.Callback(() => calls++))));
            Assert.That(sequence.Execute(0), Is.False);
            Assert.That(tween.onKill, Is.SameAs(marker));
            Assert.That(tween.IsComplete(), Is.False);
            Assert.That(tween.Elapsed(), Is.Zero);
            Assert.That(child.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(calls, Is.Zero);
            Assert.That(sequence.Execute(0), Is.False);
            Advance();
            Assert.That(sequence.Execute(0), Is.True);
            Assert.That(completes, Is.EqualTo(2));
            Assert.That(calls, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator BoundRepeatCannotReclaimTheTweenAfterItsFirstExecution()
        {
            var completes = 0;
            var tween = NewTween(false).OnComplete(() => completes++);
            var child = Own(tween.ToAction());
            var repeat = Own(ActionKit.Repeat(2).Append(child));
            repeat.Execute(0);
            repeat.Execute(0);
            Assert.That(tween.IsPlaying(), Is.True);
            Advance();
            Assert.That(repeat.Execute(0), Is.False);
            Assert.That(completes, Is.EqualTo(1));
            Assert.That(repeat.Execute(0), Is.False);
            Assert.That(repeat.IsDeinitialized, Is.True);
            Assert.That(child.IsDeinitialized, Is.True);
            Assert.That(repeat.Status, Is.Not.EqualTo(ActionStatus.Finished));
            Assert.That(tween.IsActive(), Is.True);
            Assert.That(tween.IsComplete(), Is.True);
            yield return UnityContextTurn();
            Assert.That(tween.IsActive(), Is.True);
            Assert.That(tween.IsComplete(), Is.True);
            Assert.That(completes, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator LogicalDeinitDoesNotPromisePhysicalPlaybackStopBeforeTheContextTurn()
        {
            var complete = 0;
            var killed = 0;
            var tween = NewTween(false).OnComplete(() => complete++).OnKill(() => killed++);
            var action = Own(tween.ToAction());
            action.Execute(0);
            action.Deinit();
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(action.Execute(0), Is.False);
            Assert.That(tween.IsActive(), Is.True);
            Assert.That(tween.IsPlaying(), Is.True, "Cancellation is immediate logically, not a synchronous physical Pause/Kill guarantee.");
            Advance(0.2f, 0.2f);
            Assert.That(tween.Elapsed(), Is.EqualTo(0.2f).Within(0.001f));
            Assert.That(killed, Is.Zero);
            Assert.That(complete, Is.Zero);
            yield return UnityContextTurn();
            Assert.That(tween.IsActive(), Is.False);
            Assert.That(killed, Is.EqualTo(1));
            Assert.That(complete, Is.Zero);
        }

        [UnityTest]
        public IEnumerator PendingKillWithLostMarkerDoesNotKillTheNewOwner()
        {
            var tween = NewTween(false);
            var old = Own(tween.ToAction());
            old.Execute(0);
            old.Deinit();
            Assert.Throws<InvalidOperationException>(() => tween.ToAction());
            var kills = 0;
            tween.OnKill(() => kills++);
            // Explicit callback replacement loses the old reservation; posted cleanup must yield ownership.
            var replacement = Own(tween.ToAction());
            replacement.Execute(0);
            Advance(0.2f, 0.2f);
            var position = tween.Elapsed();
            yield return UnityContextTurn();
            Assert.That(old.IsDeinitialized, Is.True);
            Assert.That(replacement.IsDeinitialized, Is.False);
            Assert.That(replacement.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(tween.IsActive(), Is.True);
            Assert.That(tween.IsPlaying(), Is.True);
            Assert.That(tween.Elapsed(), Is.EqualTo(position));
            Assert.That(kills, Is.Zero);
            tween.Complete();
            Assert.That(replacement.Execute(0), Is.True);
        }

        [Test]
        public void RepeatUsesOneFreshFactoryTweenPerRound()
        {
            var created = new List<Tween>();
            var repeat = Own(ActionKit.Repeat(3).DOTween(() =>
            {
                var tween = NewTween();
                created.Add(tween);
                return tween;
            }));
            var finished = repeat.Execute(0);
            for (var step = 0; step < 12 && !finished; step++)
            {
                Advance();
                finished = repeat.Execute(0);
            }
            Assert.That(finished, Is.True);
            Assert.That(repeat.IsDeinitialized, Is.False);
            Assert.That(created.Count, Is.EqualTo(3));
            Assert.That(new HashSet<Tween>(created).Count, Is.EqualTo(3));
            foreach (var tween in created)
            {
                Assert.That(tween.IsActive(), Is.False);
            }
        }

        private IEnumerator CheckCompositeTweenCancellation(bool parallel)
        {
            var calls = 0;
            var tween = NewTween();
            var siblingTween = NewTween();
            var child = Own(tween.ToAction());
            var sibling = Own(siblingTween.ToAction());
            var group = Own(parallel ? (ISequence)ActionKit.Parallel() : ActionKit.Sequence());
            group.Append(child).Append(sibling);
            var sequence = Own(ActionKit.Sequence().Append(group).Append(Own(ActionKit.Callback(() => calls++))));
            sequence.Execute(0);
            sequence.Execute(0);
            tween.Kill(false);
            Assert.That(sequence.Execute(0), Is.False);
            Assert.That(sequence.IsDeinitialized, Is.True);
            Assert.That(group.IsDeinitialized, Is.True);
            Assert.That(sibling.IsDeinitialized, Is.True);
            Assert.That(siblingTween.IsActive(), Is.True);
            Assert.Throws<InvalidOperationException>(() => siblingTween.ToAction());
            Assert.That(calls, Is.Zero);
            yield return UnityContextTurn();
            Assert.That(siblingTween.IsActive(), Is.False);
            Assert.That(calls, Is.Zero);
        }

        [UnityTest]
        public IEnumerator SequenceCancelsWhenATweenChildIsKilled() => CheckCompositeTweenCancellation(false);

        [UnityTest]
        public IEnumerator ParallelCancelsWhenATweenChildIsKilled() => CheckCompositeTweenCancellation(true);

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void CompositeCancellationSurvivesChildActionPoolReuseWithoutTouchingNewLease(int kind)
        {
            var oldTween = NewTween();
            var child = Own(oldTween.ToAction() as DOTweenAction);
            ISequence group;
            if (kind == 0)
            {
                group = Own(ActionKit.Sequence());
            }
            else if (kind == 1)
            {
                group = Own(ActionKit.Parallel());
            }
            else
            {
                group = Own(ActionKit.Repeat(2));
            }

            group.Append(child);
            group.Execute(0);
            group.Execute(0);
            Assert.That(oldTween.IsPlaying(), Is.True);
            var oldId = child.ActionId;
            oldTween.Kill(false);
            ActionQueue.Drain();
            var nextTween = NewTween();
            var fresh = Own(DOTweenAction.Allocate(() => nextTween));
            Assert.That(fresh, Is.SameAs(child));
            Assert.That(fresh.ActionId, Is.Not.EqualTo(oldId));
            fresh.Execute(0);
            Assert.That(group.Execute(0), Is.False);
            Assert.That(group.IsDeinitialized, Is.True);
            group.Deinit();
            Assert.That(fresh.IsDeinitialized, Is.False);
            Assert.That(fresh.Status, Is.EqualTo(ActionStatus.Started));
            Assert.That(nextTween.IsPlaying(), Is.True);
            nextTween.Complete();
            Assert.That(fresh.Execute(0), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TweenOwnUpdateSettingSelectsItsClock(bool independent)
        {
            var tween = NewTween(false, independent);
            var action = Own(tween.ToAction());
            action.Execute(0);
            action.Execute(100);
            Advance(0, 0.5f);
            Assert.That(tween.IsTimeScaleIndependent(), Is.EqualTo(independent));
            Assert.That(tween.Elapsed(), Is.EqualTo(independent ? 0.5f : 0f).Within(0.001f));
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Started));
        }
    }
}
