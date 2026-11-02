using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
using YLFramework.Async;

namespace YLFramework.Foundation.Tests
{
    public class CoroutineStarterTests
    {
        private sealed class Probe : CustomAsyncOperation<Probe>
        {
            public void Finish() => Complete();
        }

        private CoroutineStarter _host;
        private readonly List<CoroutineStarter> _ownedHosts = new List<CoroutineStarter>();
        private GameObject _foreignObject;
        private Scene _scene;
        private bool _restoreSession;

        [SetUp]
        public void SetUp()
        {
            Assert.That(FindHosts(), Is.Empty, "Fixture must not take ownership of an existing Foundation host.");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var host in _ownedHosts)
            {
                if (host)
                {
                    Object.Destroy(host.gameObject);
                }
            }

            _ownedHosts.Clear();
            _host = null;
            if (_foreignObject)
            {
                Object.Destroy(_foreignObject);
            }

            if (_scene.IsValid() && _scene.isLoaded)
            {
                yield return SceneManager.UnloadSceneAsync(_scene);
            }

            yield return null;
            if (_restoreSession)
            {
                ResetSession();
                _restoreSession = false;
            }
        }

        [UnityTest]
        public IEnumerator StartCreatesOneIndependentNamedHostAndReusesIt()
        {
            Assert.That(ApplicationTool.IsPlaying, Is.True);
            _foreignObject = new GameObject("CoroutineStarter");
            var ticks = 0;
            var first = StartOwned(Tick(() => ticks++));
            var originalHost = _host;
            var second = StartOwned(Tick(() => ticks++));
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);
            Assert.That(_host, Is.SameAs(originalHost));
            Assert.That(_host.gameObject, Is.Not.SameAs(_foreignObject));
            Assert.That(_host.name, Is.EqualTo("YLFramework.Foundation.CoroutineStarter"));
            Assert.That(FindHosts().Length, Is.EqualTo(1));
            yield return null;
            Assert.That(ticks, Is.GreaterThanOrEqualTo(4));
            CoroutineStarter.Stop(first);
            CoroutineStarter.Stop(second);
            Object.Destroy(_host.gameObject);
            yield return null;
            Assert.That(_foreignObject != null, Is.True);
        }

        [UnityTest]
        public IEnumerator StopOnlyStopsSpecifiedCoroutineAndNullDoesNotCreateHost()
        {
            CoroutineStarter.Stop(null);
            Assert.That(FindHosts(), Is.Empty);
            var stoppedTicks = 0;
            var runningTicks = 0;
            var stopped = StartOwned(Tick(() => stoppedTicks++));
            var running = StartOwned(Tick(() => runningTicks++));
            CoroutineStarter.Stop(stopped);
            CoroutineStarter.Stop(stopped);
            var countAtStop = stoppedTicks;
            yield return null;
            yield return null;
            Assert.That(stoppedTicks, Is.EqualTo(countAtStop));
            Assert.That(runningTicks, Is.GreaterThan(1));
            CoroutineStarter.Stop(running);
        }

        [UnityTest]
        public IEnumerator HostAndCoroutineSurviveOriginatingSceneUnload()
        {
            var previous = SceneManager.GetActiveScene();
            _scene = SceneManager.CreateScene("YLFoundation.CoroutineFixture");
            SceneManager.SetActiveScene(_scene);
            var ticks = 0;
            Coroutine coroutine;
            try
            {
                coroutine = StartOwned(Tick(() => ticks++));
            }
            finally
            {
                SceneManager.SetActiveScene(previous);
            }
            Assert.That(_host.gameObject.scene.name, Is.EqualTo("DontDestroyOnLoad"));
            yield return SceneManager.UnloadSceneAsync(_scene);
            Assert.That(_host != null, Is.True);
            var afterUnload = ticks;
            yield return null;
            yield return null;
            Assert.That(ticks, Is.GreaterThan(afterUnload));
            CoroutineStarter.Stop(coroutine);
        }

        [UnityTest]
        public IEnumerator DestroyedHostStopsItsCoroutineAndNextStartCreatesNewHost()
        {
            var oldTicks = 0;
            var oldCoroutine = StartOwned(Tick(() => oldTicks++));
            var oldHost = _host;
            Object.Destroy(oldHost.gameObject);
            yield return null;
            Assert.That(oldHost == null, Is.True);
            Assert.That(FindHosts(), Is.Empty);
            CoroutineStarter.Stop(oldCoroutine);
            // 回归失败时也清理本次 Stop 意外创建的宿主，不污染后续用例。
            foreach (var host in FindHosts())
            {
                _ownedHosts.Add(host);
            }

            Assert.That(FindHosts(), Is.Empty, "Stop(old) 不得创建替代宿主。");
            var countAfterDestroy = oldTicks;
            var newTicks = 0;
            var coroutine = StartOwned(Tick(() => newTicks++));
            Assert.That(_host, Is.Not.SameAs(oldHost));
            CoroutineStarter.Stop(oldCoroutine);
            yield return null;
            yield return null;
            Assert.That(oldTicks, Is.EqualTo(countAfterDestroy));
            Assert.That(newTicks, Is.GreaterThan(1));
            CoroutineStarter.Stop(coroutine);
        }

        [UnityTest]
        public IEnumerator UntrackedHandleIsNotStoppedEvenOnTheSameHost()
        {
            var tracked = StartOwned(Tick(() => { }));
            var ticks = 0;
            var untracked = _host.StartCoroutine(Tick(() => ticks++));
            CoroutineStarter.Stop(untracked);
            var before = ticks;
            yield return null;
            yield return null;
            Assert.That(ticks, Is.GreaterThan(before), "只停止经静态 Start 返回的句柄。");
            _host.StopCoroutine(untracked);
            CoroutineStarter.Stop(tracked);
        }

        [UnityTest]
        public IEnumerator SessionResetStopsOldWorkAndLateMessagesCannotAffectNewOwner()
        {
            var oldTicks = 0;
            var oldCoroutine = StartOwned(Tick(() => oldTicks++));
            var oldHost = _host;
            _restoreSession = true;
            ResetSession();
            Assert.That(oldHost.enabled, Is.False);
            var newTicks = 0;
            var current = StartOwned(Tick(() => newTicks++));
            var newHost = _host;
            oldHost.SendMessage("OnApplicationQuit");
            oldHost.SendMessage("OnDestroy");
            var afterLateMessage = StartOwned(Tick(() => { }));
            Assert.That(afterLateMessage, Is.Not.Null, "旧消息不能关闭新会话门禁。");
            CoroutineStarter.Stop(afterLateMessage);
            CoroutineStarter.Stop(oldCoroutine);
            var countAtReset = oldTicks;
            yield return null;
            yield return null;
            Assert.That(oldTicks, Is.EqualTo(countAtReset));
            Assert.That(newTicks, Is.GreaterThan(1));
            Assert.That(_host, Is.SameAs(newHost));
            CoroutineStarter.Stop(current);
            var countAtStop = newTicks;
            yield return null;
            Assert.That(newTicks, Is.EqualTo(countAtStop), "旧消息不能清掉新句柄的归属记录。");
        }

        [UnityTest]
        public IEnumerator QuitStopsOwnerAndRefusesStartWithoutCreatingHost()
        {
            var ticks = 0;
            var old = StartOwned(Tick(() => ticks++));
            _restoreSession = true;
            _host.SendMessage("OnApplicationQuit");
            var countAtQuit = ticks;
            Assert.That(StartTracked(Tick(() => Assert.Fail("退出期间不能推进迭代器。"))), Is.Null);
            CoroutineStarter.Stop(old);
            yield return null;
            yield return null;
            Assert.That(ticks, Is.EqualTo(countAtQuit));
            Assert.That(FindHosts(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator SynchronousNestedStartAndStopKeepTheirOwnHandles()
        {
            var innerTicks = 0;
            var outerTicks = 0;
            var outer = StartOwned(RunFirst(() =>
            {
                var inner = StartOwned(Tick(() => innerTicks++));
                CoroutineStarter.Stop(inner);
            }, () => outerTicks++));
            yield return null;
            yield return null;
            Assert.That(innerTicks, Is.EqualTo(1));
            Assert.That(outerTicks, Is.GreaterThan(1));
            CoroutineStarter.Stop(outer);
        }

        [UnityTest]
        public IEnumerator SynchronousSessionResetRejectsOldReturnWithoutStoppingNewWork()
        {
            var newTicks = 0;
            var oldTicks = 0;
            Coroutine current = null;
            CoroutineStarter oldHost = null;
            _restoreSession = true;
            var invalid = StartTracked(RunFirst(() =>
            {
                oldHost = FindHosts()[0];
                ResetSession();
                current = StartOwned(Tick(() => newTicks++));
            }, () => oldTicks++));
            Assert.That(invalid, Is.Null);
            Assert.That(_host, Is.Not.SameAs(oldHost));
            var countAfterStart = oldTicks;
            yield return null;
            yield return null;
            Assert.That(oldTicks, Is.EqualTo(countAfterStart));
            Assert.That(newTicks, Is.GreaterThan(1));
            CoroutineStarter.Stop(current);
        }

        [UnityTest]
        public IEnumerator SynchronousQuitRejectsReturnAndStopsInFlightStart()
        {
            var ticks = 0;
            _restoreSession = true;
            var invalid = StartTracked(RunFirst(() => FindHosts()[0].SendMessage("OnApplicationQuit"),
                () => ticks++));
            Assert.That(invalid, Is.Null);
            var countAfterStart = ticks;
            yield return null;
            yield return null;
            Assert.That(ticks, Is.EqualTo(countAfterStart));
            Assert.That(FindHosts(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator SynchronousDeactivationRejectsReturnAndInactiveHostIsNotReplaced()
        {
            var ticks = 0;
            var invalid = StartTracked(RunFirst(() => FindHosts()[0].gameObject.SetActive(false),
                () => ticks++));
            Assert.That(invalid, Is.Null);
            Assert.That(StartTracked(Tick(() => Assert.Fail("失活宿主不能启动。"))), Is.Null);
            Assert.That(FindHosts().Length, Is.EqualTo(1));
            var countAfterStart = ticks;
            yield return null;
            Assert.That(ticks, Is.EqualTo(countAfterStart));
        }

        [UnityTest]
        public IEnumerator SynchronousDestroyAndRecreateDoesNotAdoptTheOldReturn()
        {
            var oldTicks = 0;
            var newTicks = 0;
            Coroutine current = null;
            var invalid = StartTracked(RunFirst(() =>
            {
                Object.DestroyImmediate(FindHosts()[0].gameObject);
                current = StartOwned(Tick(() => newTicks++));
            }, () => oldTicks++));
            Assert.That(invalid, Is.Null);
            Assert.That(FindHosts().Length, Is.EqualTo(1));
            var countAfterStart = oldTicks;
            yield return null;
            yield return null;
            Assert.That(oldTicks, Is.EqualTo(countAfterStart));
            Assert.That(newTicks, Is.GreaterThan(1));
            CoroutineStarter.Stop(current);
        }

        [UnityTest]
        public IEnumerator InvalidEnumeratorPropagatesAndRollsBackOnlyNewHost()
        {
            Assert.Throws<NullReferenceException>(() => StartTracked(null));
            yield return null;
            Assert.That(FindHosts(), Is.Empty);
            var ticks = 0;
            var current = StartOwned(Tick(() => ticks++));
            var host = _host;
            Assert.Throws<NullReferenceException>(() => StartTracked(null));
            yield return null;
            Assert.That(_host, Is.SameAs(host));
            Assert.That(ticks, Is.GreaterThan(1), "已有宿主的其他工作不随参数错误回滚。");
            CoroutineStarter.Stop(current);
        }

        [UnityTest]
        public IEnumerator YieldingOperationWaitsUntilCompletionThenResumes()
        {
            var operation = new Probe();
            var resumed = false;
            var completed = StartOwned(WaitForOperation(operation, () => resumed = true));
            yield return null;
            Assert.That(resumed, Is.False);
            operation.Finish();
            yield return null;
            yield return null;
            Assert.That(resumed, Is.True);
            var ticks = 0;
            var current = StartOwned(Tick(() => ticks++));
            CoroutineStarter.Stop(completed);
            CoroutineStarter.Stop(completed);
            yield return null;
            yield return null;
            Assert.That(ticks, Is.GreaterThan(1), "自然完成的旧句柄不影响同宿主上的新工作。");
            CoroutineStarter.Stop(current);
        }

        [UnityTest]
        public IEnumerator AbortCompletesOperationButDoesNotCancelProducerCoroutine()
        {
            var operation = new Probe();
            var producerTicks = 0;
            var completionCount = 0;
            var allowProducerCompletion = false;
            operation.AddCompleteEvent(_ => completionCount++);
            StartOwned(Produce(operation, () => producerTicks++, () => allowProducerCompletion));
            operation.Abort();
            var ticksAtAbort = producerTicks;
            yield return null;
            yield return null;
            Assert.That(operation.IsDone, Is.True);
            Assert.That(operation.Error, Is.EqualTo("request abort!"));
            Assert.That(completionCount, Is.EqualTo(1));
            Assert.That(producerTicks, Is.GreaterThan(ticksAtAbort));
            allowProducerCompletion = true;
            yield return null;
            yield return null;
            Assert.That(operation.Error, Is.Null);
            Assert.That(completionCount, Is.EqualTo(1));
        }

        private Coroutine StartOwned(IEnumerator enumerator)
        {
            var coroutine = StartTracked(enumerator);
            var activeHosts = Array.FindAll(FindHosts(), host => host.isActiveAndEnabled);
            Assert.That(activeHosts.Length, Is.EqualTo(1));
            _host = activeHosts[0];
            return coroutine;
        }

        private Coroutine StartTracked(IEnumerator enumerator)
        {
            var before = FindHosts();
            Coroutine coroutine;
            try
            {
                coroutine = CoroutineStarter.Start(enumerator);
            }
            finally
            {
                // Track only this fixture's new hosts, even when Start or its assertion fails.
                foreach (var host in FindHosts())
                {
                    if (Array.IndexOf(before, host) < 0 && !_ownedHosts.Contains(host))
                    {
                        _ownedHosts.Add(host);
                    }
                }
            }
            return coroutine;
        }

        private static void ResetSession() => typeof(ApplicationTool).Assembly
            .GetType("YLFramework.Singletons.SingletonSessionState", true)
            .GetMethod("ResetForNewSession", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);

        private static CoroutineStarter[] FindHosts() => Object.FindObjectsByType<CoroutineStarter>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        private static IEnumerator Tick(Action tick)
        {
            while (true)
            {
                tick();
                yield return null;
            }
        }

        private static IEnumerator RunFirst(Action first, Action tick)
        {
            first();
            while (true)
            {
                tick();
                yield return null;
            }
        }

        private static IEnumerator WaitForOperation(Probe operation, Action resumed)
        {
            yield return operation;
            resumed();
        }

        private static IEnumerator Produce(Probe operation, Action tick, Func<bool> canFinish)
        {
            while (!canFinish())
            {
                tick();
                yield return null;
            }
            operation.Finish();
        }
    }
}
