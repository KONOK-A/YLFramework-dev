using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Attributes;
using YLFramework.Editor.Foundation;
using YLFramework.Editor.Common;

namespace YLFramework.Core.Editor.Tests
{
    public class DisableEditDrawerTests
    {
        public class ObserveDisabled : UnityEngine.PropertyAttribute
        {
        }
        [CustomPropertyDrawer(typeof(ObserveDisabled))]
        public class ObserveDisabledDrawer : PropertyDrawer
        {
            internal static Action<bool> Observe;
            public override void OnGUI(Rect rect, SerializedProperty property, GUIContent label)
            {
                Observe?.Invoke(GUI.enabled);
                EditorGUI.IntField(rect, label, property.intValue);
            }
        }

        [Serializable]
        public class Data
        {
            [ObserveDisabled] public int value = 17;
        }
        public class Fixture : ScriptableObject
        {
            public Data data = new Data();
            [DisableEdit] public Data readOnlyData = new Data();
        }

        private class DrawingWindow : EditorWindow
        {
            internal Action Draw;
            internal Exception Error;
            internal bool Drawn;
            private void OnGUI()
            {
                if (Drawn || Event.current.type != EventType.Repaint)
                {
                    return;
                }

                try
                {
                    Draw();
                }
                catch (Exception error)
                {
                    Error = error;
                }
                finally
                {
                    Drawn = true;
                }
            }
        }

        [Test]
        public void ExpandedSerializedDataUsesNativeHeightIncludingChildren()
        {
            var target = ScriptableObject.CreateInstance<Fixture>();
            try
            {
                using var serialized = new SerializedObject(target);
                var property = serialized.FindProperty("data");
                property.isExpanded = true;
                float expected = EditorGUI.GetPropertyHeight(property, GUIContent.none, true);
                Assert.That(expected, Is.GreaterThan(EditorGUIUtility.singleLineHeight));
                Assert.That(new DisableEditDrawer().GetPropertyHeight(property, GUIContent.none), Is.EqualTo(expected));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [UnityTest]
        public IEnumerator NativeInspectorBindingDisablesChildrenAndRestoresGuiOnNormalAndThrowingDraws()
        {
            var target = ScriptableObject.CreateInstance<Fixture>();
            var window = ScriptableObject.CreateInstance<DrawingWindow>();
            try
            {
                using var serialized = new SerializedObject(target);
                var property = serialized.FindProperty("readOnlyData");
                property.isExpanded = true;
                window.Draw = () =>
                {
                    bool original = GUI.enabled;
                    var failure = new InvalidOperationException("Owned nested drawer failure");
                    try
                    {
                        foreach (bool enabled in new[] { true, false })
                        {
                            GUI.enabled = enabled;
                            bool observed = false;
                            ObserveDisabledDrawer.Observe = state =>
                            {
                                observed = true;
                                Assert.That(state, Is.False);
                            };
                            EditorGUI.PropertyField(new Rect(0, 0, 250, 60), property, GUIContent.none, true);
                            Assert.That(observed, Is.True, "The expanded child must actually be drawn through the registered DisableEdit drawer.");
                            Assert.That(GUI.enabled, Is.EqualTo(enabled));
                            ObserveDisabledDrawer.Observe = _ => throw failure;
                            Assert.That(Assert.Throws<InvalidOperationException>(() =>
                                EditorGUI.PropertyField(new Rect(0, 0, 250, 60), property, GUIContent.none, true)), Is.SameAs(failure));
                            Assert.That(GUI.enabled, Is.EqualTo(enabled));
                        }
                    }
                    finally
                    {
                        GUI.enabled = original;
                        ObserveDisabledDrawer.Observe = null;
                    }
                    Assert.That(serialized.ApplyModifiedProperties(), Is.False);
                    Assert.That(target.readOnlyData.value, Is.EqualTo(17));
                };
                window.ShowUtility();
                for (int frame = 0; frame < 60 && !window.Drawn; frame++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.Drawn, Is.True);
                if (window.Error != null)
                {
                    throw window.Error;
                }
            }
            finally
            {
                ObserveDisabledDrawer.Observe = null;
                window.Close();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void SerializedPropertyClipboardConsumerResolvesUnityEditorTypeAndRoundTripsValue()
        {
            var target = ScriptableObject.CreateInstance<Fixture>();
            string clipboard = EditorGUIUtility.systemCopyBuffer;
            try
            {
                using var serialized = new SerializedObject(target);
                var source = serialized.FindProperty("data.value");
                var destination = serialized.FindProperty("readOnlyData.value");
                source.intValue = 42;
                source.CopyToClipboard();
                Assert.That(destination.IsCanPaste(), Is.True);
                destination.PasteFromClipboard();
                Assert.That(destination.intValue, Is.EqualTo(42));
                serialized.ApplyModifiedProperties();
                Assert.That(target.readOnlyData.value, Is.EqualTo(42));
            }
            finally
            {
                EditorGUIUtility.systemCopyBuffer = clipboard;
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
