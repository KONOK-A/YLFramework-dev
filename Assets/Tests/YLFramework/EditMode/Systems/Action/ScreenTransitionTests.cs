using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YLFramework.Actions;
using Kit = YLFramework.Actions.ActionKit;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Actions
{
    public class ScreenTransitionTests
    {
        private readonly List<IAction> _actions = new List<IAction>();
        private GameObject _root;
        private ScreenTransitionCanvas _canvas;

        [SetUp]
        public void SetUp()
        {
            ActionQueue.Drain();
            Assert.That(Object.FindObjectOfType<ScreenTransitionCanvas>() == null, Is.True,
                "This fixture must own its screen-transition canvas.");
            _root = new GameObject("ActionKit.ScreenTransition.EditModeTest", typeof(RectTransform),
                typeof(Canvas), typeof(ScreenTransitionCanvas));
            var imageRoot = new GameObject("ColorImage", typeof(RectTransform), typeof(Image));
            imageRoot.transform.SetParent(_root.transform, false);
            _canvas = _root.GetComponent<ScreenTransitionCanvas>();
            _canvas.colorImage = imageRoot.GetComponent<Image>();
            _canvas.colorImage.color = Color.black;
        }

        private T Own<T>(T action) where T : IAction
        {
            _actions.Add(action);
            return action;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var action in _actions)
            {
                if (!action.IsDeinitialized)
                {
                    action.Deinit();
                }
            }
            _actions.Clear();
            ActionQueue.Drain();
            if (_root)
            {
                Object.DestroyImmediate(_root);
            }
        }

        [Test]
        public void FadeInPreservesBlackZeroToOneInOneSecond()
        {
            var fade = Own(Kit.ScreenTransition.FadeIn());
            Assert.That(fade.Execute(0), Is.False);
            Assert.That(_canvas.colorImage.color, Is.EqualTo(new Color(0, 0, 0, 0)));
            Assert.That(fade.Execute(0.25f), Is.False);
            Assert.That(_canvas.colorImage.color.a, Is.EqualTo(0.25f));
            Assert.That(fade.Execute(0.75f), Is.True);
            Assert.That(_canvas.colorImage.color, Is.EqualTo(Color.black));
        }

        [Test]
        public void FadeOutPreservesBlackOneToZeroInOneSecond()
        {
            var fade = Own(Kit.ScreenTransition.FadeOut());
            fade.Execute(0);
            Assert.That(_canvas.colorImage.color, Is.EqualTo(Color.black));
            Assert.That(fade.Execute(0.5f), Is.False);
            Assert.That(_canvas.colorImage.color.a, Is.EqualTo(0.5f));
            Assert.That(fade.Execute(0.5f), Is.True);
            Assert.That(_canvas.colorImage.color.a, Is.Zero);
        }

        [Test]
        public void EveryFluentSettingReturnsTheFadeAndAlphaChangesPreserveRgb()
        {
            var fade = Own(ScreenTransitionFade.Allocate());
            Assert.That(fade.Color(new Color(1, 0.25f, 0.5f, 0.3f)), Is.SameAs(fade));
            Assert.That(fade.FromAlpha(0.2f), Is.SameAs(fade));
            Assert.That(fade.ToAlpha(0.8f), Is.SameAs(fade));
            Assert.That(fade.Duration(2), Is.SameAs(fade));
            fade.Execute(0);
            Assert.That(_canvas.colorImage.color.r, Is.EqualTo(1));
            Assert.That(_canvas.colorImage.color.a, Is.EqualTo(0.2f));
            Assert.That(fade.Execute(1), Is.False);
            Assert.That(_canvas.colorImage.color.a, Is.EqualTo(0.5f).Within(0.00001f));
            Assert.That(_canvas.colorImage.color.g, Is.EqualTo(0.25f));
            Assert.That(_canvas.colorImage.color.b, Is.EqualTo(0.5f));
            Assert.That(fade.Execute(1), Is.True);
            Assert.That(_canvas.colorImage.color.a, Is.EqualTo(0.8f));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        public void NonPositiveDurationKeepsTheOriginalCompletionOnTheFirstExecutionAfterStart(float duration)
        {
            var fade = Own(Kit.ScreenTransition.FadeIn().Duration(duration));
            Assert.That(fade.Execute(0), Is.False);
            Assert.That(fade.Execute(0.1f), Is.True);
            Assert.That(_canvas.colorImage.color.a, Is.EqualTo(1));
        }

        [Test]
        public void ResetRestartsElapsedTimeWithoutChangingTheFluentSettings()
        {
            var fade = Own(Kit.ScreenTransition.FadeIn().Duration(2));
            fade.Execute(0);
            fade.Execute(1);
            Assert.That(_canvas.colorImage.color.a, Is.EqualTo(0.5f));
            fade.Reset();
            fade.Execute(0);
            Assert.That(_canvas.colorImage.color.a, Is.Zero);
            fade.Execute(1);
            Assert.That(_canvas.colorImage.color.a, Is.EqualTo(0.5f));
        }

        [Test]
        public void ForcedFinishOnlySetsTargetAlphaWithoutApplyingTheConfiguredStartColor()
        {
            _canvas.colorImage.color = Color.green;
            var fade = Own(Kit.ScreenTransition.FadeIn());
            fade.Finish();
            Assert.That(fade.Execute(0), Is.True);
            Assert.That(_canvas.colorImage.color, Is.EqualTo(Color.green));
        }

        [Test]
        public void RecycledFadeStartsWithDefaultColorAlphaDurationAndElapsedTime()
        {
            var first = Own(ScreenTransitionFade.Allocate().Color(Color.red).FromAlpha(0.3f).ToAlpha(0.7f).Duration(2));
            first.Execute(0);
            first.Execute(1);
            first.Deinit();
            ActionQueue.Drain();
            var reused = Own(ScreenTransitionFade.Allocate());
            Assert.That(reused, Is.SameAs(first));
            reused.Execute(0);
            Assert.That(_canvas.colorImage.color, Is.EqualTo(new Color(0, 0, 0, 0)));
            Assert.That(reused.Execute(0.5f), Is.False);
            Assert.That(_canvas.colorImage.color.a, Is.Zero);
            Assert.That(reused.Execute(0.5f), Is.True);
        }

        [Test]
        public void InOutIsARegularSequenceThatCanBeDrivenToCompletionWithoutAGlobalHost()
        {
            IAction action = Own(Kit.ScreenTransition.FadeInOut());
            Assert.That(action, Is.InstanceOf<ISequence>(), "The factory must return an owned sequence, not a detached global launcher.");
            var completed = 0;
            ((ISequence)action).Callback(() => completed++);
            Assert.That(action.Execute(0), Is.False);
            Assert.That(_canvas.colorImage.color.a, Is.Zero);
            Assert.That(action.Execute(0.5f), Is.False);
            Assert.That(_canvas.colorImage.color.a, Is.EqualTo(0.5f));
            Assert.That(action.Execute(0.5f), Is.False);
            Assert.That(_canvas.colorImage.color.a, Is.EqualTo(1));
            Assert.That(action.Execute(1), Is.True);
            Assert.That(_canvas.colorImage.color.a, Is.Zero);
            Assert.That(completed, Is.EqualTo(1));
            Assert.That(Object.FindObjectOfType<ActionGlobalHost>(true), Is.Null);
        }

        [Test]
        public void FactorySequenceInvokesTheMiddleCallbackOnceAndClearsItBeforePoolReuse()
        {
            var middle = 0;
            var transition = Own(Kit.ScreenTransition.FadeInOut(() =>
            {
                Assert.That(_canvas.colorImage.color.a, Is.EqualTo(1));
                middle++;
            }));
            transition.Execute(0);
            Assert.That(transition.Execute(1), Is.False);
            Assert.That(middle, Is.EqualTo(1));
            Assert.That(transition.Execute(1), Is.True);
            transition.Deinit();
            ActionQueue.Drain();
            var reused = Own(Kit.ScreenTransition.FadeInOut());
            reused.Execute(0);
            reused.Execute(1);
            Assert.That(reused.Execute(1), Is.True);
            Assert.That(middle, Is.EqualTo(1));
            Assert.That(_canvas.colorImage.color.a, Is.Zero);
            Assert.That(Object.FindObjectOfType<ActionGlobalHost>(true), Is.Null);
        }

        [Test]
        public void AnExistingSceneCanvasHasPriorityOverResourceInstantiation()
        {
            Assert.That(ScreenTransitionCanvas.Instance, Is.SameAs(_canvas));
            Assert.That(Object.FindObjectsOfType<ScreenTransitionCanvas>().Length, Is.EqualTo(1));
        }

        [Test]
        public void CreatedResourceHasTheModuleScriptAndOriginalPrefabConfiguration()
        {
            var prefab = Resources.Load<GameObject>(ScreenTransitionCanvas.ResourcePath);
            Assert.That(prefab, Is.Not.Null,
                "The main package must contain its owned screen-transition prefab; consumers do not regenerate product assets.");
            Assert.That(AssetDatabase.GetAssetPath(prefab),
                Is.EqualTo("Packages/com.kano.ylframework/Runtime/Systems/Action/Resources/ActionKit/ScreenTransitionCanvas.prefab"));
            Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(prefab), Is.Zero);
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefab)),
                Is.EqualTo("2a0bf8e4b9b44ec46a47f771105de1e4"));
            var component = prefab.GetComponent<ScreenTransitionCanvas>();
            Assert.That(component, Is.Not.Null);
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(component))),
                Is.EqualTo("256f0a9016a7098469c8afed875a5d50"));
            foreach (var child in prefab.GetComponentsInChildren<Transform>(true))
            {
                Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject), Is.Zero);
                foreach (var attached in child.GetComponents<Component>())
                {
                    Assert.That(attached, Is.Not.Null);
                }
            }
            Assert.That(component.colorImage, Is.Not.Null);
            Assert.That(component.colorImage.transform.parent, Is.EqualTo(prefab.transform));
            var canvas = prefab.GetComponent<Canvas>();
            Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
            Assert.That(canvas.sortingOrder, Is.EqualTo(10000));
            var scaler = prefab.GetComponent<CanvasScaler>();
            Assert.That(scaler.uiScaleMode, Is.EqualTo(CanvasScaler.ScaleMode.ConstantPixelSize));
            Assert.That(scaler.scaleFactor, Is.EqualTo(1));
            Assert.That(scaler.referencePixelsPerUnit, Is.EqualTo(100));
            Assert.That(component.colorImage.color, Is.EqualTo(Color.black));
            Assert.That(component.colorImage.raycastTarget, Is.True);
            Assert.That(component.colorImage.sprite, Is.Null);
            Assert.That(prefab.GetComponent<GraphicRaycaster>(), Is.Null, "Do not add input behavior absent from the original resource.");
            var rect = component.colorImage.rectTransform;
            Assert.That(rect.anchorMin, Is.EqualTo(Vector2.zero));
            Assert.That(rect.anchorMax, Is.EqualTo(Vector2.one));
            Assert.That(rect.sizeDelta, Is.EqualTo(Vector2.zero));
            foreach (var script in prefab.GetComponentsInChildren<MonoBehaviour>(true))
            {
                Assert.That(script, Is.Not.Null);
                var assembly = script.GetType().Assembly.GetName().Name;
                Assert.That(assembly == "YLFramework.Runtime" || assembly == "UnityEngine.UI", Is.True,
                    "Unexpected prefab script assembly: " + assembly);
            }
        }
    }
}
