using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Timing;
using Object = UnityEngine.Object;

namespace YLFramework.Foundation.Tests
{
    public class TimerBindingPlayModeTests
    {
        private GameObject _owner;
        private GameObject _replacement;
        private TimerScheduler _scheduler;

        [SetUp]
        public void SetUp()
        {
            _scheduler = new TimerScheduler(0, 0, 0);
            _owner = new GameObject("YLTimer.BindingPlayOwner");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _scheduler.Dispose();
            if (_owner)
            {
                Object.Destroy(_owner);
            }

            if (_replacement)
            {
                Object.Destroy(_replacement);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator NeverActivatedPausedOwnerIsCancelledOnNextAdvanceAfterDestroy()
        {
            _owner.SetActive(false);
            var cancels = 0;
            var handle = _scheduler.Delay(100, onCancelled: _ => cancels++);
            handle.BindTo(_owner, TimerGameObjectLifetime.CancelOnDestroy);
            handle.Pause();
            Object.Destroy(_owner);
            yield return null;
            _scheduler.Advance(0, 0, 1);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(cancels, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator DisableCancelsPausedRunButDestroyOnlyRunContinues()
        {
            var stopped = _scheduler.Delay(100);
            var survives = _scheduler.Delay(0);
            stopped.BindTo(_owner);
            survives.BindTo(_owner, TimerGameObjectLifetime.CancelOnDestroy);
            stopped.Pause();
            _owner.SetActive(false);
            Assert.That(stopped.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(survives.IsActive, Is.True);
            yield return null;
            _scheduler.Advance(0, 0, 1);
            Assert.That(survives.Status, Is.EqualTo(TimerState.Completed));
        }

        [UnityTest]
        public IEnumerator CallbackRebindAndRestartIsNotCancelledByOldListenerSnapshot()
        {
            _replacement = new GameObject("YLTimer.BindingReplacement");
            using var slot = new ReusableTimer(_scheduler);
            TimerHandle next = default;
            var calls = 0;
            var handle = slot.Start(10, onCancelled: _ =>
            {
                next = slot.Start(0, () => calls++);
                next.BindTo(_replacement);
            });
            handle.BindTo(_owner);
            _owner.SetActive(false);
            yield return null;
            _scheduler.Advance(0, 0, 1);
            Assert.That(handle.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(next.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(calls, Is.EqualTo(1));
        }
    }
}
