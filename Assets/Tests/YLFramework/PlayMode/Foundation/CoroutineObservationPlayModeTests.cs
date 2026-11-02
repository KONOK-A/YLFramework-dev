using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
using YLFramework.Async;

namespace YLFramework.Foundation.Tests
{
    public class CoroutineObservationPlayModeTests
    {
        private readonly List<CoroutineStarter> _ownedHosts = new();
        private readonly List<GameObject> _objects = new();
        private readonly List<CoroutineObservation> _observations = new();

        [SetUp]
        public void SetUp()
        {
            Assert.That(FindHosts(), Is.Empty, "不接管用户已有 Starter，包括停用宿主。");
            Assert.That(typeof(CoroutineStarter).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null), Is.Null, "不接管未释放的静态 owner。");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var observation in _observations)
            {
                observation.Dispose();
            }

            _observations.Clear();
            foreach (var obj in _objects)
            {
                if (obj)
                {
                    Object.Destroy(obj);
                }
            }

            _objects.Clear();
            foreach (var host in _ownedHosts)
            {
                if (host)
                {
                    Object.Destroy(host.gameObject);
                }
            }

            _ownedHosts.Clear();
            yield return null;
        }

        [UnityTest]
        public IEnumerator NaturalCompletionInvokesOnceAndMatchesNativeWait()
        {
            var owner = CreateOwner();
            var observer = CreateOwner();
            var finish = false;
            var naturalEnd = false;
            var producer = owner.StartCoroutine(Produce(() => finish, () => { }, () => naturalEnd = true));
            var nativeProducer = owner.StartCoroutine(Produce(() => finish, () => { }, () => { }));
            var callbacks = 0;
            var nativeEnded = false;
            observer.StartCoroutine(NativeWait(nativeProducer, () => nativeEnded = true));
            var observation = Observe(producer, () => callbacks++);
            Assert.That(observation.IsStarted, Is.True);
            yield return null;
            Assert.That(callbacks, Is.Zero);
            Assert.That(nativeEnded, Is.False);
            finish = true;
            yield return Until(() => callbacks == 1 && nativeEnded);
            Assert.That(naturalEnd, Is.True);
            observation.Dispose();
            yield return null;
            Assert.That(callbacks, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ProducerStopMatchesActualNativeWaitWithoutClaimingBusinessSuccess()
        {
            var observer = CreateOwner();
            var ticks = 0;
            var naturalEnd = false;
            var producer = StartProducer(Produce(() => false, () => ticks++, () => naturalEnd = true));
            var nativeProducer = StartProducer(Produce(() => false, () => { }, () => { }));
            var nativeEnded = false;
            observer.StartCoroutine(NativeWait(nativeProducer, () => nativeEnded = true));
            var callbacks = 0;
            Assert.That(Observe(producer, () => callbacks++).IsStarted, Is.True);
            yield return null;
            Assert.That(nativeEnded, Is.False);
            Assert.That(callbacks, Is.Zero);
            CoroutineStarter.Stop(producer);
            CoroutineStarter.Stop(nativeProducer);
            var ticksAtStop = ticks;
            for (var i = 0; i < 6; i++)
            {
                yield return null;
            }

            Assert.That(ticks, Is.EqualTo(ticksAtStop));
            Assert.That(naturalEnd, Is.False);
            Assert.That(nativeEnded, Is.False, "当前 Unity 2022.3 的停止操作不会恢复借用 Coroutine 的原生等待。");
            Assert.That(callbacks, Is.EqualTo(nativeEnded ? 1 : 0), "观察只遵循 Unity 原生 Coroutine 等待。");
            Debug.Log($"Producer Stop: native wait resumed={nativeEnded}, observation callbacks={callbacks}");
        }

        [UnityTest]
        public IEnumerator ProducerOwnerDestroyMatchesActualNativeWaitWhileStarterSurvives()
        {
            var owner = CreateOwner();
            var observer = CreateOwner();
            var ticks = 0;
            var naturalEnd = false;
            var producer = owner.StartCoroutine(Produce(() => false, () => ticks++, () => naturalEnd = true));
            var nativeProducer = owner.StartCoroutine(Produce(() => false, () => { }, () => { }));
            var nativeEnded = false;
            observer.StartCoroutine(NativeWait(nativeProducer, () => nativeEnded = true));
            var callbacks = 0;
            Assert.That(Observe(producer, () => callbacks++).IsStarted, Is.True);
            yield return null;
            Assert.That(nativeEnded, Is.False);
            Assert.That(callbacks, Is.Zero);
            Object.Destroy(owner.gameObject);
            yield return null;
            var ticksAfterDestroy = ticks;
            for (var i = 0; i < 6; i++)
            {
                yield return null;
            }

            Assert.That(owner == null, Is.True);
            Assert.That(ticks, Is.EqualTo(ticksAfterDestroy));
            Assert.That(naturalEnd, Is.False);
            Assert.That(nativeEnded, Is.False, "当前 Unity 2022.3 的 owner 销毁不会恢复借用 Coroutine 的原生等待。");
            Assert.That(FindHosts().Length, Is.EqualTo(1), "只销毁 producer owner，不销毁观察宿主。");
            Assert.That(callbacks, Is.EqualTo(nativeEnded ? 1 : 0));
            Debug.Log($"Producer owner destroy: native wait resumed={nativeEnded}, observation callbacks={callbacks}");
        }

        [UnityTest]
        public IEnumerator DisposeStopsOnlyItsWrapperAndProducerAndOtherObservationContinue()
        {
            var ticks = 0;
            var finish = false;
            var producer = StartProducer(Produce(() => finish, () => ticks++, () => { }));
            var otherProducer = StartProducer(Produce(() => finish, () => { }, () => { }));
            var stoppedCallbacks = 0;
            var survivingCallbacks = 0;
            var stopped = Observe(producer, () => stoppedCallbacks++);
            var survivor = Observe(otherProducer, () => survivingCallbacks++);
            Assert.That(stopped.IsStarted && survivor.IsStarted, Is.True);
            stopped.Dispose();
            stopped.Dispose();
            var ticksAtStop = ticks;
            yield return null;
            yield return null;
            Assert.That(ticks, Is.GreaterThan(ticksAtStop), "释放观察不能停止借用 producer。");
            finish = true;
            yield return Until(() => survivingCallbacks == 1);
            Assert.That(stoppedCallbacks, Is.Zero);
            Assert.That(FindHosts().Length, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ExistingNativeWaiterExplicitlyRefusesASecondObservation()
        {
            var owner = CreateOwner();
            var finish = false;
            var producer = owner.StartCoroutine(Produce(() => finish, () => { }, () => { }));
            var nativeEnded = false;
            owner.StartCoroutine(NativeWait(producer, () => nativeEnded = true));
            var callbacks = 0;
            var refused = Observe(producer, () => callbacks++);
            Assert.That(refused.IsStarted, Is.False);
            refused.Dispose();
            finish = true;
            yield return Until(() => nativeEnded);
            Assert.That(callbacks, Is.Zero);
        }

        [UnityTest]
        public IEnumerator InactiveStarterExplicitlyRefusesWithoutReplacingHostOrStoppingProducer()
        {
            var owner = CreateOwner();
            var ticks = 0;
            var producer = owner.StartCoroutine(Produce(() => false, () => ticks++, () => { }));
            var callbacks = 0;
            var first = Observe(producer, () => callbacks++);
            Assert.That(first.IsStarted, Is.True);
            var host = FindHosts()[0];
            host.gameObject.SetActive(false);
            var refused = Observe(producer, () => callbacks++);
            Assert.That(refused.IsStarted, Is.False);
            refused.Dispose();
            first.Dispose();
            Assert.That(FindHosts(), Is.EqualTo(new[] { host }), "拒绝不能创建替代宿主。");
            var ticksAtRefusal = ticks;
            yield return null;
            yield return null;
            Assert.That(ticks, Is.GreaterThan(ticksAtRefusal));
            Assert.That(callbacks, Is.Zero);
        }

        private CoroutineObservationOwner CreateOwner()
        {
            var obj = new GameObject("YLFoundation.CoroutineObservationFixture");
            _objects.Add(obj);
            return obj.AddComponent<CoroutineObservationOwner>();
        }

        private CoroutineObservation Observe(Coroutine producer, Action callback)
        {
            var before = FindHosts();
            Assert.That(Array.TrueForAll(before, host => _ownedHosts.Contains(host)), Is.True,
                "并行出现的非自有 Starter 不能被 fixture 接管。");
            try
            {
                var observation = CoroutineObservation.Observe(producer, callback);
                _observations.Add(observation);
                return observation;
            }
            finally
            {
                TrackNewHosts(before);
            }
        }

        private Coroutine StartProducer(IEnumerator producer)
        {
            var before = FindHosts();
            Assert.That(Array.TrueForAll(before, host => _ownedHosts.Contains(host)), Is.True);
            try
            {
                var coroutine = CoroutineStarter.Start(producer);
                Assert.That(coroutine, Is.Not.Null, "不擅自打开已经关闭的执行 gate。");
                return coroutine;
            }
            finally
            {
                TrackNewHosts(before);
            }
        }

        private void TrackNewHosts(CoroutineStarter[] before)
        {
            foreach (var host in FindHosts())
            {
                if (Array.IndexOf(before, host) < 0 && !_ownedHosts.Contains(host))
                {
                    _ownedHosts.Add(host);
                }
            }
        }

        private static CoroutineStarter[] FindHosts() => Object.FindObjectsByType<CoroutineStarter>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        private static IEnumerator Produce(Func<bool> canFinish, Action tick, Action naturalEnd)
        {
            while (!canFinish())
            {
                tick();
                yield return null;
            }
            naturalEnd();
        }

        private static IEnumerator NativeWait(Coroutine producer, Action ended)
        {
            yield return producer;
            ended();
        }

        private static IEnumerator Until(Func<bool> condition)
        {
            var deadline = Time.realtimeSinceStartupAsDouble + 3;
            while (!condition() && Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
            }

            Assert.That(condition(), Is.True, "真实 Unity 等待未在期限内结束。");
        }
    }

    public sealed class CoroutineObservationOwner : MonoBehaviour
    {
    }
}
