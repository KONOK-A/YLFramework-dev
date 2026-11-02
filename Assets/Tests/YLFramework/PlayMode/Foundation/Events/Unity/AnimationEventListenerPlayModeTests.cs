#if UNITY_EDITOR
using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YLFramework.Core;
using Object = UnityEngine.Object;
using YLFramework.Events;

namespace YLFramework.Tests.CommonSystem
{
    public class AnimationEventListenerPlayModeTests
    {
        private GameObject _root;
        private string _folder;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_root)
            {
                Object.Destroy(_root);
            }

            yield return null;
            if (!string.IsNullOrEmpty(_folder))
            {
                AssetDatabase.DeleteAsset(_folder);
            }
        }

        [UnityTest]
        public IEnumerator NativeClipDispatchesItsStringToAPersistentUnityEventTarget()
        {
            const string Fixtures = "Assets/Tests/YLFramework/Fixtures/CommonSystem";
            if (!AssetDatabase.IsValidFolder(Fixtures))
            {
                AssetDatabase.CreateFolder("Assets/Tests/YLFramework/Fixtures", "CommonSystem");
            }

            var name = "Animation-" + Guid.NewGuid().ToString("N");
            _folder = Fixtures + "/" + name;
            AssetDatabase.CreateFolder(Fixtures, name);
            var clip = new AnimationClip { name = "Walk", frameRate = 30 };
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.x"),
                AnimationCurve.Linear(0, 0, 0.2f, 0.01f));
            AnimationUtility.SetAnimationEvents(clip, new[]
            {
                new UnityEngine.AnimationEvent
                {
                    time = 0.05f, functionName = "OnAnimationEvent", stringParameter = "walk"
                }
            });
            AssetDatabase.CreateAsset(clip, _folder + "/Walk.anim");
            var controller = AnimatorController.CreateAnimatorControllerAtPath(_folder + "/Walk.controller");
            var machine = controller.layers[0].stateMachine;
            var state = machine.AddState("Walk");
            state.motion = clip;
            machine.defaultState = state;
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssetIfDirty(controller);

            _root = new GameObject("CommonSystem native clip");
            _root.SetActive(false);
            var listener = _root.AddComponent<AnimationEventListener>();
            var textObject = new GameObject("Persistent string receiver", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(_root.transform, false);
            var text = textObject.GetComponent<Text>();
            var setter = (UnityAction<string>)Delegate.CreateDelegate(typeof(UnityAction<string>), text,
                typeof(Text).GetProperty(nameof(Text.text)).GetSetMethod());
            UnityEventTools.AddPersistentListener(listener.animationEvent, setter);
            Assert.That(listener.animationEvent.GetPersistentEventCount(), Is.EqualTo(1));
            string observed = null;
            listener.animationEvent.AddListener(value => observed = value);
            var animator = _root.AddComponent<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.runtimeAnimatorController = controller;
            _root.SetActive(true);
            animator.Update(0);
            animator.Play("Walk", 0, 0);
            animator.Update(0.1f);
            Assert.That(text.text, Is.EqualTo("walk"),
                $"bridge={observed}, initialized={animator.isInitialized}, length={clip.length}, " +
                $"time={animator.GetCurrentAnimatorStateInfo(0).normalizedTime}, events={clip.events.Length}");
            yield return null;
        }
    }
}
#endif
