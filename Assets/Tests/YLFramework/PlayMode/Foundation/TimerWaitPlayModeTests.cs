using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Timing;
using Object = UnityEngine.Object;

namespace YLFramework.Foundation.Tests
{
    public class TimerWaitPlayModeTests
    {
        private GameObject _object;
        private TimerWaitOwner _owner;

        [SetUp]
        public void SetUp()
        {
            _object = new GameObject("YLFoundation.TimerWaitFixture");
            _owner = _object.AddComponent<TimerWaitOwner>();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_object)
            {
                Object.Destroy(_object);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator UnityYieldResumesOnCapturedTerminalState()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var handle = scheduler.DelayFrames(1);
            var wait = handle.WaitForTermination();
            var resumed = false;
            _owner.StartCoroutine(Wait(wait, () => resumed = true));
            yield return null;
            Assert.That(resumed, Is.False);
            scheduler.Advance(0, 0, 1);
            yield return Until(() => resumed);
            Assert.That(wait.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(wait.IsCompleted, Is.True);

            var cancelled = scheduler.Delay(10);
            var cancelledWait = cancelled.WaitForTermination();
            resumed = false;
            _owner.StartCoroutine(Wait(cancelledWait, () => resumed = true));
            cancelled.Cancel();
            yield return Until(() => resumed);
            Assert.That(cancelledWait.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(cancelledWait.IsCompleted, Is.False);
        }

        [UnityTest]
        public IEnumerator StoppingWaitingCoroutineLeavesTimerScheduledAndAbleToComplete()
        {
            using var scheduler = new TimerScheduler(0, 0, 0);
            var calls = 0;
            var handle = scheduler.Delay(1, () => calls++);
            var wait = handle.WaitForTermination();
            var resumed = false;
            var wrapper = _owner.StartCoroutine(Wait(wait, () => resumed = true));
            _owner.StopCoroutine(wrapper);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Scheduled));
            scheduler.Advance(0, 1, 1);
            yield return null;
            yield return null;
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(handle.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(wait.IsCompleted, Is.True);
            Assert.That(resumed, Is.False);
        }

        private static IEnumerator Wait(TimerWait wait, Action resumed)
        {
            yield return wait;
            resumed();
        }

        private static IEnumerator Until(Func<bool> condition)
        {
            var deadline = Time.realtimeSinceStartupAsDouble + 3;
            while (!condition() && Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
            }

            Assert.That(condition(), Is.True);
        }
    }

    public sealed class TimerWaitOwner : MonoBehaviour
    {
    }
}
