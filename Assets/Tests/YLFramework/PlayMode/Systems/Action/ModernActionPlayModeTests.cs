using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Actions;
using Kit = YLFramework.Actions.ActionKit;

namespace YLFramework.Tests.Actions
{
    public class ModernActionPlayModeTests
    {
        private readonly List<IAction> _owned = new List<IAction>();
        private ActionRecycleDriver _previousDriver;
        private float _previousTimeScale;
        private Scene _createdScene;
        private AsyncOperation _sceneUnload;

        private T Own<T>(T action) where T : IAction
        {
            _owned.Add(action);
            return action;
        }

        [SetUp]
        public void SetUp()
        {
            _previousDriver = Object.FindObjectOfType<ActionRecycleDriver>(true);
            _previousTimeScale = Time.timeScale;
            _createdScene = default;
            _sceneUnload = null;
            ActionQueue.Drain();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Time.timeScale = _previousTimeScale;
            foreach (var action in _owned)
            {
                if (!action.IsDeinitialized)
                {
                    action.Deinit();
                }
            }
            _owned.Clear();
            ActionQueue.Drain();
            var current = Object.FindObjectOfType<ActionRecycleDriver>(true);
            if (current && current != _previousDriver)
            {
                Object.Destroy(current.gameObject);
            }

            if (_sceneUnload != null)
            {
                yield return _sceneUnload;
            }
            else if (_createdScene.IsValid() && _createdScene.isLoaded)
            {
                yield return SceneManager.UnloadSceneAsync(_createdScene);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator FrameDelayUsesActualFramesInsteadOfSuppliedDelta()
        {
            var calls = 0;
            var action = Own(Kit.DelayFrame(2, () => calls++));
            var frame = Time.frameCount;
            Assert.That(action.Execute(1000), Is.False);
            Assert.That(action.Execute(1000), Is.False);
            yield return null;
            Assert.That(Time.frameCount, Is.EqualTo(frame + 1));
            Assert.That(action.Execute(1000), Is.False);
            yield return null;
            Assert.That(action.Execute(0), Is.True);
            Assert.That(calls, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator NextFrameFactoryAndBothFrameFluentMethodsRemainAvailable()
        {
            var calls = 0;
            var single = Own(Kit.NextFrame(() => calls++));
            Assert.That(single.Execute(0), Is.False);
            var sequence = Own(Kit.Sequence().DelayFrame(0).NextFrame().Callback(() => calls++));
            Assert.That(sequence.Execute(0), Is.False);
            Assert.That(sequence.Execute(0), Is.False);
            yield return null;
            Assert.That(single.Execute(0), Is.True);
            Assert.That(sequence.Execute(0), Is.True);
            Assert.That(calls, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator PausedFrameDelayObservesElapsedGlobalFramesAfterResume()
        {
            Time.timeScale = 0;
            var calls = 0;
            var action = Own(Kit.NextFrame(() => calls++));
            action.Execute(0);
            action.IsPaused = true;
            yield return null;
            yield return null;
            Assert.That(action.Execute(0), Is.False);
            Assert.That(calls, Is.Zero);
            action.IsPaused = false;
            Assert.That(action.Execute(0), Is.True);
            Assert.That(calls, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ManualSecondsAndCompositionUseCallerDeltaEvenWhenTimeScaleIsZero()
        {
            Time.timeScale = 0;
            var calls = 0;
            var sequence = Own(Kit.Sequence().Callback(() => calls++).Delay(1f).Callback(() => calls++));
            Assert.That(sequence.Execute(10), Is.False);
            yield return null;
            Assert.That(sequence.Execute(0.5f), Is.False);
            yield return null;
            Assert.That(sequence.Execute(0.5f), Is.False);
            yield return null;
            Assert.That(sequence.Execute(0), Is.True);
            Assert.That(calls, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator RuntimeRecycleIsDeferredAndReusesTheActionAfterUpdate()
        {
            var first = Own(Kit.Condition(() => false));
            var id = first.ActionId;
            first.Deinit();
            Assert.That(ActionQueue.PendingCount, Is.GreaterThan(0));
            var second = Own(Kit.Condition(() => false));
            Assert.That(second, Is.Not.SameAs(first));
            yield return null;
            yield return null;
            Assert.That(ActionQueue.PendingCount, Is.Zero);
            var reused = Own(Kit.Condition(() => false));
            Assert.That(reused, Is.SameAs(first));
            Assert.That(reused.ActionId, Is.Not.EqualTo(id));
            Assert.That(reused.IsDeinitialized, Is.False);
        }

        [UnityTest]
        public IEnumerator RecycleDriverIsAdoptedAfterStaticResetAndRecreatedAfterDestruction()
        {
            Assert.That(_previousDriver == null, Is.True, "Run this lifecycle test without an existing module driver; it will not destroy another owner's host.");
            ActionRecycleDriver.EnsureCreated();
            var original = Object.FindObjectOfType<ActionRecycleDriver>(true);
            Assert.That(original, Is.Not.Null);
            ActionRecycleDriver.ResetSession();
            ActionRecycleDriver.EnsureCreated();
            Assert.That(Object.FindObjectOfType<ActionRecycleDriver>(true), Is.SameAs(original));
            Assert.That(Object.FindObjectsOfType<ActionRecycleDriver>(true).Length, Is.EqualTo(1));
            Object.Destroy(original.gameObject);
            yield return null;
            Assert.That(original == null, Is.True);
            var action = Own(Kit.Callback(null));
            action.Deinit();
            var recreated = Object.FindObjectOfType<ActionRecycleDriver>(true);
            Assert.That(recreated, Is.Not.Null);
            yield return null;
            yield return null;
            Assert.That(ActionQueue.PendingCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator RecycleDriverSurvivesTheSceneWhereItWasCreated()
        {
            Assert.That(_previousDriver == null, Is.True, "This test must create and own its driver.");
            var previousScene = SceneManager.GetActiveScene();
            var scene = SceneManager.CreateScene("ActionKit.Recycle.SceneTest");
            _createdScene = scene;
            try
            {
                SceneManager.SetActiveScene(scene);
                ActionRecycleDriver.EnsureCreated();
                var driver = Object.FindObjectOfType<ActionRecycleDriver>(true);
                Assert.That(driver, Is.Not.Null);
                Assert.That(driver.gameObject.scene, Is.Not.EqualTo(scene));
                SceneManager.SetActiveScene(previousScene);
                _sceneUnload = SceneManager.UnloadSceneAsync(scene);
                yield return _sceneUnload;
                Assert.That(driver != null, Is.True);
                var action = Own(Kit.Callback(null));
                action.Deinit();
                yield return null;
                yield return null;
                Assert.That(ActionQueue.PendingCount, Is.Zero);
            }
            finally
            {
                SceneManager.SetActiveScene(previousScene);
                if (_sceneUnload == null && scene.IsValid() && scene.isLoaded)
                {
                    _sceneUnload = SceneManager.UnloadSceneAsync(scene);
                }
            }
        }
    }
}
