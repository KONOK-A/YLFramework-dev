using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using YLFramework.Events;
using Object = UnityEngine.Object;
using UEditor = UnityEditor.Editor;

namespace YLFramework.Tests.Editor
{
    public sealed class EventListenerDerivedProbe : EventListener
    {
        [SerializeField] private int extraValue;
        public int ExtraValue => extraValue;
    }

    public class EventListenerEditorTests
    {
        private readonly List<GameObject> _roots = new List<GameObject>();
        private readonly List<UEditor> _editors = new List<UEditor>();
        private Scene _scene;
        private string _folder;

        [SetUp]
        public void SetUp() => _scene = EditorSceneManager.NewPreviewScene();

        [TearDown]
        public void TearDown()
        {
            foreach (var editor in _editors)
            {
                if (editor)
                {
                    Object.DestroyImmediate(editor);
                }
            }

            foreach (var root in _roots)
            {
                if (!root)
                {
                    continue;
                }

                foreach (var component in root.GetComponents<Component>())
                {
                    if (component)
                    {
                        Undo.ClearUndo(component);
                    }
                }

                Object.DestroyImmediate(root);
            }
            _editors.Clear();
            _roots.Clear();
            if (_scene.IsValid())
            {
                EditorSceneManager.ClosePreviewScene(_scene);
            }

            if (_folder != null)
            {
                Assert.That(AssetDatabase.DeleteAsset(_folder), Is.True);
            }

            _folder = null;
        }

        private T Create<T>() where T : EventListener
        {
            var root = new GameObject("EventListener.NativeInspectorTest");
            SceneManager.MoveGameObjectToScene(root, _scene);
            _roots.Add(root);
            return root.AddComponent<T>();
        }

        private UEditor Inspector(params Object[] targets)
        {
            var editor = UEditor.CreateEditor(targets);
            _editors.Add(editor);
            Assert.That(editor.GetType().FullName, Is.EqualTo("YLFramework.Editor.Foundation.EventListenerEditor"));
            Assert.That(editor.GetType().Assembly.GetName().Name, Is.EqualTo("YLFramework.Editor"));
            return editor;
        }

        private static UnityEvent<PointerEventData> Click(EventListener listener) =>
            (UnityEvent<PointerEventData>)typeof(EventListener)
                .GetField("pointerClick", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(listener);

        private static void SetPersistentCall(SerializedObject serialized, EventListener receiver, string method)
        {
            serialized.Update();
            var calls = serialized.FindProperty("pointerClick").FindPropertyRelative("m_PersistentCalls.m_Calls");
            calls.arraySize = 1;
            var call = calls.GetArrayElementAtIndex(0);
            call.FindPropertyRelative("m_Target").objectReferenceValue = receiver;
            call.FindPropertyRelative("m_TargetAssemblyTypeName").stringValue = typeof(EventListener).AssemblyQualifiedName;
            call.FindPropertyRelative("m_MethodName").stringValue = method;
            call.FindPropertyRelative("m_Mode").intValue = (int)PersistentListenerMode.EventDefined;
            call.FindPropertyRelative("m_CallState").intValue = (int)UnityEventCallState.EditorAndRuntime;
            Assert.That(serialized.ApplyModifiedProperties(), Is.True);
            Undo.FlushUndoRecordObjects();
        }

        [Test]
        public void NativeEditorIsSelectedForBaseDerivedAndMultipleObjects()
        {
            Inspector(Create<EventListener>());
            Inspector(Create<EventListenerDerivedProbe>());
            var editor = Inspector(Create<EventListener>(), Create<EventListener>());
            Assert.That(editor.GetType().IsDefined(typeof(CanEditMultipleObjects), false), Is.True);
            Assert.That(editor.serializedObject.isEditingMultipleObjects, Is.True);
        }

        [Test]
        public void DisplayMappingCoversRealSerializedEventsWithoutChangingTheirSchema()
        {
            var listener = Create<EventListener>();
            var editor = Inspector(listener);
            var map = ((ValueTuple<string, GUIContent>[][])editor.GetType()
                .GetField("_eventFields", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
            var serializedFields = typeof(EventListener).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(field => field.IsDefined(typeof(SerializeField), false)).OrderBy(field => field.MetadataToken).ToArray();
            var fields = serializedFields.Where(field => typeof(UnityEventBase).IsAssignableFrom(field.FieldType)).ToArray();
            CollectionAssert.AreEqual(new[] { "physicsLayerMask" }, serializedFields.Except(fields).Select(field => field.Name));
            Assert.That(editor.serializedObject.FindProperty("physicsLayerMask").propertyType, Is.EqualTo(SerializedPropertyType.LayerMask));
            Assert.That(fields.Length, Is.EqualTo(20));
            CollectionAssert.AreEqual(fields.Select(field => field.Name), map.SelectMany(group => group).Select(entry => entry.Item1));
            Assert.That(map.SelectMany(group => group).Select(entry => entry.Item1).Distinct().Count(), Is.EqualTo(20));
            CollectionAssert.AreEqual(new[] { 8, 6, 6 }, map.Select(group => group.Length));
            Assert.That(string.Join("|", map.SelectMany(group => group).Select(entry => entry.Item2.text)), Is.EqualTo(
                "指针进入|指针离开|指针点击|指针按下|指针抬起|开始拖拽|拖拽中|结束拖拽|" +
                "碰撞进入|碰撞保持|碰撞离开|碰撞进入2D|碰撞保持2D|碰撞离开2D|" +
                "Trigger进入|Trigger保持|Trigger离开|Trigger进入2D|Trigger保持2D|Trigger离开2D"));
            CollectionAssert.AreEqual(new[] { "鼠标事件", "碰撞事件", "Trigger事件" },
                (string[])editor.GetType().GetField("_tabs", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
            var payloads = Enumerable.Repeat(typeof(PointerEventData), 8).Concat(Enumerable.Repeat(typeof(Collision), 3))
                .Concat(Enumerable.Repeat(typeof(Collision2D), 3)).Concat(Enumerable.Repeat(typeof(Collider), 3))
                .Concat(Enumerable.Repeat(typeof(Collider2D), 3)).ToArray();
            for (var i = 0; i < fields.Length; i++)
            {
                Assert.That(fields[i].FieldType, Is.EqualTo(typeof(UnityEvent<>).MakeGenericType(payloads[i])));
                Assert.That(editor.serializedObject.FindProperty(fields[i].Name), Is.Not.Null);
                Assert.That(fields[i].GetCustomAttributesData().Any(attribute => attribute.AttributeType.Namespace?.StartsWith("Sirenix") == true), Is.False);
            }
            Assert.That(typeof(EventListener).GetCustomAttributesData().Any(attribute => attribute.AttributeType.Namespace?.StartsWith("Sirenix") == true), Is.False);
        }

        [Test]
        public void DerivedFieldsRemainVisibleEditableAndUndoableWithoutShowingScriptOrGroupedEventsTwice()
        {
            var listener = Create<EventListenerDerivedProbe>();
            var editor = Inspector(listener);
            var extra = (IEnumerable<SerializedProperty>)editor.GetType()
                .GetMethod("AdditionalProperties", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(editor, null);
            var properties = extra.ToArray();
            try
            {
                CollectionAssert.AreEquivalent(new[] { "physicsLayerMask", "extraValue" }, properties.Select(property => property.propertyPath));
                Undo.IncrementCurrentGroup();
                properties.Single(property => property.propertyPath == "extraValue").intValue = 42;
                Assert.That(editor.serializedObject.ApplyModifiedProperties(), Is.True);
                Undo.FlushUndoRecordObjects();
                Assert.That(listener.ExtraValue, Is.EqualTo(42));
                Undo.PerformUndo();
                Assert.That(listener.ExtraValue, Is.Zero);
                Undo.PerformRedo();
                Assert.That(listener.ExtraValue, Is.EqualTo(42));
            }
            finally
            {
                foreach (var property in properties)
                {
                    property.Dispose();
                }
            }
        }

        [Test]
        public void PhysicalMaskSupportsMultiEditUndoAndRedo()
        {
            var first = Create<EventListener>();
            var second = Create<EventListener>();
            var editor = Inspector(first, second);
            var property = editor.serializedObject.FindProperty("physicsLayerMask");
            Assert.That(property, Is.Not.Null);
            Assert.That(property.intValue, Is.EqualTo(Physics.AllLayers));
            Undo.IncrementCurrentGroup();
            property.intValue = 0;
            Assert.That(editor.serializedObject.ApplyModifiedProperties(), Is.True);
            Undo.FlushUndoRecordObjects();
            using var observed = new SerializedObject(new Object[] { first, second });
            Assert.That(observed.FindProperty("physicsLayerMask").hasMultipleDifferentValues, Is.False);
            Assert.That(observed.FindProperty("physicsLayerMask").intValue, Is.Zero);
            Undo.PerformUndo();
            observed.Update();
            Assert.That(observed.FindProperty("physicsLayerMask").intValue, Is.EqualTo(Physics.AllLayers));
            Undo.PerformRedo();
            observed.Update();
            Assert.That(observed.FindProperty("physicsLayerMask").intValue, Is.Zero);
        }

        [Test]
        public void OldPrefabWithoutTheFilterKeepsAllLayersAndPersistentTargets()
        {
            const string Legacy = "Assets/Tests/YLFramework/Fixtures/CommonSystem/EventListener.Legacy.prefab";
            AssetDatabase.ImportAsset(Legacy, ImportAssetOptions.ForceUpdate);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Legacy);
            Assert.That(prefab, Is.Not.Null);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, _scene);
            _roots.Add(instance);
            var listener = instance.GetComponent<EventListener>();
            var editor = Inspector(listener);
            var mask = editor.serializedObject.FindProperty("physicsLayerMask");
            Assert.That(mask, Is.Not.Null);
            Assert.That(mask.intValue, Is.EqualTo(Physics.AllLayers));
            var click = Click(listener);
            Assert.That(click.GetPersistentEventCount(), Is.EqualTo(1));
            Assert.That(click.GetPersistentMethodName(0), Is.EqualTo("SetActive"));
            var target = instance.transform.Find("Persistent target").gameObject;
            Assert.That(click.GetPersistentTarget(0), Is.SameAs(target));
            click.SetPersistentListenerState(0, UnityEventCallState.EditorAndRuntime);
            listener.OnPointerClick(null);
            Assert.That(target.activeSelf, Is.False);

            mask.intValue = 0;
            Assert.That(editor.serializedObject.ApplyModifiedProperties(), Is.True);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            _folder = "Assets/Tests/YLFramework/EventListenerEditor-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/Tests/YLFramework", System.IO.Path.GetFileName(_folder));
            var saved = _folder + "/ExplicitNone.prefab";
            Assert.That(PrefabUtility.SaveAsPrefabAsset(instance, saved), Is.Not.Null);
            AssetDatabase.ImportAsset(saved, ImportAssetOptions.ForceUpdate);
            using var reloaded = new SerializedObject(AssetDatabase.LoadAssetAtPath<GameObject>(saved).GetComponent<EventListener>());
            Assert.That(reloaded.FindProperty("physicsLayerMask").intValue, Is.Zero);
        }

        [Test]
        public void MultiObjectPersistentEventsUseSerializedEditsAndPreserveUndoRedo()
        {
            var first = Create<EventListener>();
            var second = Create<EventListener>();
            var receiver = Create<EventListener>();
            var editor = Inspector(first, second);
            Undo.IncrementCurrentGroup();
            SetPersistentCall(editor.serializedObject, receiver, nameof(EventListener.OnPointerEnter));
            foreach (var listener in new[] { first, second })
            {
                Assert.That(Click(listener).GetPersistentEventCount(), Is.EqualTo(1));
                Assert.That(Click(listener).GetPersistentTarget(0), Is.SameAs(receiver));
                Assert.That(Click(listener).GetPersistentMethodName(0), Is.EqualTo(nameof(EventListener.OnPointerEnter)));
            }
            Undo.PerformUndo();
            foreach (var listener in new[] { first, second })
            {
                Assert.That(Click(listener)?.GetPersistentEventCount() ?? 0, Is.Zero);
            }

            Undo.PerformRedo();
            foreach (var listener in new[] { first, second })
            {
                Assert.That(Click(listener).GetPersistentEventCount(), Is.EqualTo(1));
            }

            editor.serializedObject.Update();
            editor.serializedObject.FindProperty("pointerClick").FindPropertyRelative("m_PersistentCalls.m_Calls").arraySize = 0;
            Assert.That(editor.serializedObject.ApplyModifiedProperties(), Is.True);
            foreach (var listener in new[] { first, second })
            {
                Assert.That(Click(listener).GetPersistentEventCount(), Is.Zero);
            }
        }

        [Test]
        public void OwnedTemporaryPrefabPreservesPersistentTargetsThroughOverrideApplyAndRevert()
        {
            var listener = Create<EventListener>();
            var editor = Inspector(listener);
            SetPersistentCall(editor.serializedObject, listener, nameof(EventListener.OnPointerEnter));
            _folder = "Assets/Tests/YLFramework/EventListenerEditor-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets/Tests/YLFramework", System.IO.Path.GetFileName(_folder)), Is.Not.Empty);
            var path = _folder + "/Listener.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(listener.gameObject, path);
            Assert.That(prefab, Is.Not.Null);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, _scene);
            _roots.Add(instance);
            var component = instance.GetComponent<EventListener>();
            Assert.That(Click(component).GetPersistentTarget(0), Is.SameAs(component));
            var instanceEditor = Inspector(component);
            SetPersistentCall(instanceEditor.serializedObject, component, nameof(EventListener.OnPointerExit));
            instanceEditor.serializedObject.Update();
            var property = instanceEditor.serializedObject.FindProperty("pointerClick");
            Assert.That(property.prefabOverride, Is.True);
            PrefabUtility.ApplyPropertyOverride(property, path, InteractionMode.AutomatedAction);
            Assert.That(Click(prefab.GetComponent<EventListener>()).GetPersistentMethodName(0), Is.EqualTo(nameof(EventListener.OnPointerExit)));
            SetPersistentCall(instanceEditor.serializedObject, component, nameof(EventListener.OnPointerUp));
            instanceEditor.serializedObject.Update();
            PrefabUtility.RevertPropertyOverride(instanceEditor.serializedObject.FindProperty("pointerClick"), InteractionMode.AutomatedAction);
            Assert.That(Click(component).GetPersistentMethodName(0), Is.EqualTo(nameof(EventListener.OnPointerExit)));
            instanceEditor.serializedObject.Update();
            var mask = instanceEditor.serializedObject.FindProperty("physicsLayerMask");
            mask.intValue = 0;
            Assert.That(instanceEditor.serializedObject.ApplyModifiedProperties(), Is.True);
            instanceEditor.serializedObject.Update();
            mask = instanceEditor.serializedObject.FindProperty("physicsLayerMask");
            Assert.That(mask.prefabOverride, Is.True);
            PrefabUtility.ApplyPropertyOverride(mask, path, InteractionMode.AutomatedAction);
            Assert.That(prefab.GetComponent<EventListener>().PhysicsLayerMask.value, Is.Zero);
            instanceEditor.serializedObject.Update();
            mask.intValue = 1 << 31;
            Assert.That(instanceEditor.serializedObject.ApplyModifiedProperties(), Is.True);
            instanceEditor.serializedObject.Update();
            mask = instanceEditor.serializedObject.FindProperty("physicsLayerMask");
            PrefabUtility.RevertPropertyOverride(mask, InteractionMode.AutomatedAction);
            Assert.That(component.PhysicsLayerMask.value, Is.Zero);
            Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(prefab), Is.Zero);
        }
    }
}
