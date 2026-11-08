using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Actions;
using Kit = YLFramework.Actions.ActionKit;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Actions
{
    public class ScreenTransitionPlayModeTests
    {
        private readonly List<IAction> _actions = new List<IAction>();
        private readonly List<IActionController> _controllers = new List<IActionController>();
        private ScreenTransitionCanvas _canvas;
        private ActionRecycleDriver _previousRecycle;
        private float _timeScale;
        private bool _ownsEnvironment;
        private bool _ownsActionSession;

        [SetUp]
        public void SetUp()
        {
            _ownsEnvironment = false;
            _ownsActionSession = false;
            var canOwnActionSession = ActionFixtureSession.CanOwnUninitializedSession();
            _timeScale = Time.timeScale;
            _previousRecycle = Object.FindObjectOfType<ActionRecycleDriver>(true);
            Assert.That(Object.FindObjectOfType<ScreenTransitionCanvas>() == null, Is.True,
                "This fixture must own its transition canvas.");
            Assert.That(Object.FindObjectOfType<ActionGlobalHost>(true) == null, Is.True,
                "This fixture must own its global executor.");
            Assert.That(Resources.Load<GameObject>(ScreenTransitionCanvas.ResourcePath), Is.Not.Null,
                "The main package must contain its owned screen-transition prefab; consumers do not regenerate product assets.");
            _ownsEnvironment = true;
            _ownsActionSession = canOwnActionSession;
            Time.timeScale = 1;
            ActionQueue.Drain();
            _canvas = ScreenTransitionCanvas.Instance;
        }

        private T Own<T>(T action) where T : IAction
        {
            _actions.Add(action);
            return action;
        }

        private IActionController Track(IActionController controller)
        {
            _controllers.Add(controller);
            return controller;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_ownsEnvironment)
            {
                yield break;
            }

            Time.timeScale = _timeScale;
            var host = Object.FindObjectOfType<ActionGlobalHost>(true);
            if (host)
            {
                Object.Destroy(host.gameObject);
            }

            var canvas = Object.FindObjectOfType<ScreenTransitionCanvas>();
            if (canvas)
            {
                Object.Destroy(canvas.gameObject);
            }

            yield return null;
            foreach (var action in _actions)
            {
                if (!action.IsDeinitialized)
                {
                    action.Deinit();
                }
            }
            foreach (var controller in _controllers)
            {
                if (controller.Action != null)
                {
                    controller.Recycle();
                }
            }
            _actions.Clear();
            _controllers.Clear();
            ActionQueue.Drain();
            var recycle = Object.FindObjectOfType<ActionRecycleDriver>(true);
            if (recycle && recycle != _previousRecycle)
            {
                Object.Destroy(recycle.gameObject);
            }

            if (_ownsActionSession)
            {
                try
                {
                    Kit.Shutdown();
                }
                finally
                {
                    _ownsActionSession = false;
                }
            }
            yield return null;
        }

        private static IEnumerator Until(Func<bool> condition)
        {
            var end = Time.realtimeSinceStartup + 2f;
            while (!condition() && Time.realtimeSinceStartup < end)
            {
                yield return null;
            }

            Assert.That(condition(), Is.True, "The expected transition did not complete within the test bound.");
        }

        [UnityTest]
        public IEnumerator NewResourceInstantiatesOnePersistentCanvasAndKeepsTheImageReference()
        {
            Assert.That(_canvas, Is.Not.Null);
            Assert.That(_canvas.colorImage, Is.Not.Null);
            Assert.That(ScreenTransitionCanvas.Instance, Is.SameAs(_canvas));
            Assert.That(Object.FindObjectsOfType<ScreenTransitionCanvas>().Length, Is.EqualTo(1));
            Assert.That(_canvas.gameObject.scene.name, Is.EqualTo("DontDestroyOnLoad"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator FadeInAndFadeOutRunInSequenceAndFinishTheReturnedController()
        {
            var completed = 0;
            var sequence = Own(Kit.Sequence()
                .Append(Own(Kit.ScreenTransition.FadeIn().Duration(0.01f)))
                .Append(Own(Kit.ScreenTransition.FadeOut().Duration(0.01f))));
            var controller = Track(sequence.StartGlobal(() => completed++));
            yield return Until(() => completed == 1);
            Assert.That(_canvas.colorImage.color.a, Is.Zero);
            Assert.That(sequence.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
        }

        [UnityTest]
        public IEnumerator IgnoreTimeScaleCanDriveFadeWhileTheScaledTransitionWaits()
        {
            Time.timeScale = 0;
            var completed = 0;
            var fade = Own(Kit.ScreenTransition.FadeIn().Duration(0.01f));
            var controller = Track(fade.StartGlobal(() => completed++));
            yield return null;
            yield return null;
            Assert.That(completed, Is.Zero);
            controller.IgnoreTimeScale();
            yield return Until(() => completed == 1);
            Assert.That(_canvas.colorImage.color.a, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator InOutCompletesOnItsSelectedOwnerWithoutCreatingAGlobalHost()
        {
            var middle = 0;
            var finished = 0;
            Time.timeScale = 20;
            var transition = Own(Kit.ScreenTransition.FadeInOut(() =>
                {
                    Assert.That(_canvas.colorImage.color.a, Is.EqualTo(1));
                    middle++;
                }));
            var controller = Track(transition.Start(_canvas, () => finished++));
            yield return Until(() => controller.Action == null);
            Assert.That(middle, Is.EqualTo(1));
            Assert.That(_canvas.colorImage.color.a, Is.Zero);
            Assert.That(transition.IsDeinitialized, Is.True);
            Assert.That(finished, Is.EqualTo(1));
            Assert.That(Object.FindObjectOfType<ActionGlobalHost>(true), Is.Null);
        }

        [UnityTest]
        public IEnumerator PausedAndCanceledInOutHasNoDetachedChildOrCompletionCallback()
        {
            var middle = 0;
            var finished = 0;
            Time.timeScale = 0;
            var transition = Own(Kit.ScreenTransition.FadeInOut(() => middle++));
            var controller = Track(transition.Start(_canvas, () => finished++));
            controller.Pause();
            controller.IgnoreTimeScale();
            yield return null;
            yield return null;
            Assert.That(_canvas.colorImage.color.a, Is.Zero);
            controller.Deinit();
            Time.timeScale = 1;
            yield return Until(() => controller.Action == null);
            yield return null;
            Assert.That(transition.IsDeinitialized, Is.True);
            Assert.That(_canvas.colorImage.color.a, Is.Zero);
            Assert.That(middle, Is.Zero);
            Assert.That(finished, Is.Zero);
            Assert.That(Object.FindObjectOfType<ActionGlobalHost>(true), Is.Null);
        }

        [UnityTest]
        public IEnumerator InOutUsesTheSelectedUnscaledClockWithoutDetachingItsChildren()
        {
            var middle = 0;
            var finished = 0;
            Time.timeScale = 0;
            var transition = Own(Kit.ScreenTransition.FadeInOut(() => middle++));
            var controller = Track(transition.Start(_canvas, () => finished++)).IgnoreTimeScale();
            var end = Time.realtimeSinceStartup + 4;
            while (controller.Action != null && Time.realtimeSinceStartup < end)
            {
                yield return null;
            }
            Assert.That(controller.Action, Is.Null);
            Assert.That(middle, Is.EqualTo(1));
            Assert.That(finished, Is.EqualTo(1));
            Assert.That(_canvas.colorImage.color.a, Is.Zero);
            Assert.That(Object.FindObjectOfType<ActionGlobalHost>(true), Is.Null);
        }

        [UnityTest]
        public IEnumerator InOutMiddleExceptionCancelsTheOwnedSequenceWithoutAHiddenFadeOut()
        {
            var finished = 0;
            Time.timeScale = 20;
            var transition = Own(Kit.ScreenTransition.FadeInOut(() => throw new InvalidOperationException("transition middle fixture")));
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("InvalidOperationException: transition middle fixture"));
            var controller = Track(transition.Start(_canvas, () => finished++));
            yield return Until(() => controller.Action == null);
            Assert.That(transition.IsDeinitialized, Is.True);
            Assert.That(finished, Is.Zero);
            Assert.That(_canvas.colorImage.color.a, Is.EqualTo(1));
            Assert.That(Object.FindObjectOfType<ActionGlobalHost>(true), Is.Null);
        }

        [UnityTest]
        public IEnumerator DestroyedCanvasIsRecreatedFromTheNewResource()
        {
            var previous = _canvas;
            Object.Destroy(previous.gameObject);
            yield return null;
            Assert.That(previous == null, Is.True);
            _canvas = ScreenTransitionCanvas.Instance;
            Assert.That(_canvas, Is.Not.Null);
            Assert.That(_canvas.colorImage, Is.Not.Null);
            Assert.That(Object.FindObjectsOfType<ScreenTransitionCanvas>().Length, Is.EqualTo(1));
        }
    }
}
