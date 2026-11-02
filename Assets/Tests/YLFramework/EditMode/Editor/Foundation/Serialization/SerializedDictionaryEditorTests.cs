using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using YLFramework.Foundation.Tests;
using Object = UnityEngine.Object;

namespace YLFramework.Tests
{
    public class SerializedDictionaryEditorTests
    {
        private static IEnumerator WaitForBinding(Func<bool> ready, string message)
        {
            // Unity 2022.3 的绑定按时间间隔轮询，固定帧数不能代表绑定或结构刷新完成。
            var deadline = EditorApplication.timeSinceStartup + 3;
            while (!ready() && EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(ready(), Is.True, message);
        }

        internal static PropertyDrawer GetDrawer(SerializedProperty property)
        {
            var utility = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.ScriptAttributeUtility");
            var handler = utility.GetMethod("GetHandler", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { property });
            return (PropertyDrawer)handler.GetType().GetProperty("propertyDrawer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(handler);
        }

        [TestCase("Direct")]
        [TestCase("Derived")]
        [TestCase("Nested")]
        [TestCase("ObjectKeys")]
        public void UnityActuallyBindsFormalDrawerAndProvidesBothUiPaths(string field)
        {
            var asset = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            try
            {
                using (var serialized = new SerializedObject(asset))
                {
                    var property = serialized.FindProperty(field);
                    var drawer = GetDrawer(property);
                    Assert.That(drawer, Is.Not.Null, "Unity 未绑定正式容器的 Drawer");
                    Assert.That(drawer.GetType().FullName, Is.EqualTo("YLFramework.Editor.Foundation.SerializedDictionaryDrawer"));
                    Assert.That(drawer.GetType().GetMethod("OnGUI").DeclaringType, Is.EqualTo(drawer.GetType()));
                    var ui = drawer.CreatePropertyGUI(property);
                    Assert.That(ui.Q<PropertyField>(), Is.Not.Null);
                    Assert.That(ui.Q<HelpBox>().text, Does.Contain("原始 0").And.Contain("有效 0"));
                }
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void DiagnosticsReadPendingSerializedKeysAndNeverDeleteDuplicateRows()
        {
            var asset = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            try
            {
                asset.Direct.Add("a", 1);
                asset.Direct.Add("b", 2);
                Assert.That(asset.Direct.Count, Is.EqualTo(2));
                using (var serialized = new SerializedObject(asset))
                {
                    var property = serialized.FindProperty("Direct");
                    var rows = property.FindPropertyRelative("datas");
                    rows.GetArrayElementAtIndex(1).FindPropertyRelative("Key").stringValue = "a";
                    var drawer = GetDrawer(property);
                    Assert.That(drawer, Is.Not.Null);
                    var ui = drawer.CreatePropertyGUI(property);
                    Assert.That(ui.Q<HelpBox>().text, Does.Contain("原始 2").And.Contain("有效 1").And.Contain("重复").And.Contain("1"));
                    Assert.That(rows.arraySize, Is.EqualTo(2));
                    serialized.ApplyModifiedProperties();
                    Assert.That(asset.Direct.Count, Is.EqualTo(1));
                    Assert.That(asset.Direct["a"], Is.EqualTo(1));
                    Assert.That(asset.Direct.InvalidEntryCount, Is.EqualTo(1));
                    rows.GetArrayElementAtIndex(1).FindPropertyRelative("Key").stringValue = "repaired";
                    serialized.ApplyModifiedProperties();
                    Assert.That(asset.Direct["repaired"], Is.EqualTo(2));
                    Assert.That(asset.Direct.InvalidEntryCount, Is.Zero);
                }
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void ListElementAndEnumKeysUseActualGenericDrawerAndTypedComparer()
        {
            var go = new GameObject("DictionaryEditorTest");
            try
            {
                var component = go.AddComponent<SerializedDictionaryEditorComponent>();
                component.Listed.Add(new global::YLFramework.Serialization.SerializedDictionary<string, int> { { "a", 1 } });
                component.EnumKeys.Add(DictionaryEditorKey.First, 1);
                component.EnumKeys.Add(DictionaryEditorKey.Second, 2);
                using (var serialized = new SerializedObject(component))
                {
                    var element = serialized.FindProperty("Listed").GetArrayElementAtIndex(0);
                    Assert.That(GetDrawer(element).CreatePropertyGUI(element).Q<HelpBox>().text, Does.Contain("有效 1"));
                    var property = serialized.FindProperty("EnumKeys");
                    var key = property.FindPropertyRelative("datas").GetArrayElementAtIndex(1).FindPropertyRelative("Key");
                    key.enumValueIndex = 0;
                    Assert.That(GetDrawer(property).CreatePropertyGUI(property).Q<HelpBox>().text, Does.Contain("有效 1").And.Contain("重复"));
                    serialized.ApplyModifiedProperties();
                    Assert.That(component.EnumKeys.Count, Is.EqualTo(1));
                    Assert.That(component.EnumKeys.InvalidEntryCount, Is.EqualTo(1));
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ObjectReferenceNoneNeverClaimsToDistinguishClrNullFromUnityFakeNull()
        {
            var asset = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            try
            {
                JsonUtility.FromJsonOverwrite("{\"ObjectKeys\":{\"datas\":[{\"Key\":{\"instanceID\":0},\"Value\":1}]}}", asset);
                Assert.That(asset.ObjectKeys.Count, Is.EqualTo(1));
                using (var serialized = new SerializedObject(asset))
                {
                    var property = serialized.FindProperty("ObjectKeys");
                    Assert.That(GetDrawer(property).CreatePropertyGUI(property).Q<HelpBox>().text,
                        Does.Contain("待确认 1").And.Contain("fake-null"));
                }
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [UnityTest]
        public IEnumerator LegacyImguiInspectorDrawsExpandedFieldsWithoutMutatingRawRows()
        {
            var asset = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            var window = ScriptableObject.CreateInstance<DictionaryInspectorTestWindow>();
            DictionaryLegacyTestEditor editor = null;
            try
            {
                asset.Direct.Add("a", 1);
                editor = (DictionaryLegacyTestEditor)UnityEditor.Editor.CreateEditor(asset, typeof(DictionaryLegacyTestEditor));
                var rows = editor.serializedObject.FindProperty("Direct.datas");
                rows.isExpanded = true;
                rows.GetArrayElementAtIndex(0).isExpanded = true;
                var before = JsonUtility.ToJson(asset);
                window.LegacyEditor = editor;
                window.position = new Rect(100, 100, 650, 700);
                window.Show();
                window.SendEvent(new Event { type = EventType.Repaint });
                yield return null;
                yield return null;
                Assert.That(editor.DrawCount, Is.GreaterThan(0));
                Assert.That(editor.DrawnHeight, Is.GreaterThan(EditorGUIUtility.singleLineHeight * 3));
                Assert.That(JsonUtility.ToJson(asset), Is.EqualTo(before));
                editor.serializedObject.FindProperty("Direct.datas.Array.data[0].Key").stringValue = "imgui";
                editor.serializedObject.FindProperty("Direct.datas.Array.data[0].Value").intValue = 5;
                editor.serializedObject.ApplyModifiedProperties();
                window.Repaint();
                yield return null;
                Assert.That(asset.Direct.ContainsKey("a"), Is.False);
                Assert.That(asset.Direct["imgui"], Is.EqualTo(5));
            }
            finally
            {
                window.LegacyEditor = null;
                window.Release();
                if (editor != null)
                {
                    Object.DestroyImmediate(editor);
                }

                Undo.ClearUndo(asset);
                Object.DestroyImmediate(asset);
            }
        }

        [UnityTest]
        public IEnumerator MultiObjectEditableFieldsHaveNoNativeArrayStructureContextCommands()
        {
            var a = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            var b = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            var window = ScriptableObject.CreateInstance<DictionaryInspectorTestWindow>();
            DictionaryLegacyTestEditor editor = null;
            try
            {
                a.Direct.Add("a", 1);
                b.Direct.Add("b", 2);
                editor = (DictionaryLegacyTestEditor)UnityEditor.Editor.CreateEditor(new Object[] { a, b }, typeof(DictionaryLegacyTestEditor));
                var property = editor.serializedObject.FindProperty("Direct");
                var ui = GetDrawer(property).CreatePropertyGUI(property);
                editor.MenuPaths = ui.Query<PropertyField>().ToList().Select(f => f.bindingPath).ToArray();
                window.LegacyEditor = editor;
                window.Show();
                yield return null;
                yield return null;
                Assert.That(editor.MenuLabels, Is.Not.Null);
                Assert.That(editor.MenuLabels.Any(s => s == L10n.Tr("Duplicate Array Element") || s == L10n.Tr("Delete Array Element")), Is.False,
                    string.Join(";", editor.MenuLabels));
                Assert.That(editor.MenuPaths, Does.Contain("Direct.datas.Array.data[0].Key").And.Contain("Direct.datas.Array.data[0].Value"), string.Join(";", editor.MenuLabels));
            }
            finally
            {
                window.LegacyEditor = null;
                window.Release();
                if (editor != null)
                {
                    Object.DestroyImmediate(editor);
                }

                Object.DestroyImmediate(a);
                Object.DestroyImmediate(b);
            }
        }

        [UnityTest]
        public IEnumerator ImguiScrollableDiagnosticsAllocateStableHeightEvenInNarrowRects()
        {
            var asset = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            var window = ScriptableObject.CreateInstance<DictionaryInspectorTestWindow>();
            DictionaryLegacyTestEditor editor = null;
            try
            {
                asset.Direct.Add("a", 1);
                editor = (DictionaryLegacyTestEditor)UnityEditor.Editor.CreateEditor(asset, typeof(DictionaryLegacyTestEditor));
                window.LegacyEditor = editor;
                editor.NarrowWidth = 140;
                window.position = new Rect(100, 100, 650, 500);
                window.Show();
                yield return null;
                yield return null;
                var wideHeight = editor.DrawnHeight;
                Assert.That(wideHeight, Is.GreaterThan(0));
                window.position = new Rect(100, 100, 180, 500);
                window.Repaint();
                yield return null;
                yield return null;
                Assert.That(editor.DrawnHeight, Is.EqualTo(wideHeight), "诊断区应固定高度并滚动，不能因窗口与实际 Rect 宽度不符覆盖后续字段");
                Assert.That(asset.Direct["a"], Is.EqualTo(1));
            }
            finally
            {
                window.LegacyEditor = null;
                window.Release();
                if (editor != null)
                {
                    Object.DestroyImmediate(editor);
                }

                Object.DestroyImmediate(asset);
            }
        }

        [UnityTest]
        public IEnumerator DefaultToolkitInspectorEditsBoundFieldsAndUndoRedoRefreshesProjection()
        {
            var asset = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            var window = ScriptableObject.CreateInstance<DictionaryInspectorTestWindow>();
            UnityEditor.Editor editor = null;
            try
            {
                asset.Direct.Add("a", 1);
                Assert.That(asset.Direct["a"], Is.EqualTo(1));
                // 显式验收 Unity 原生 Inspector，不借用第三方或其他 CustomEditor。
                editor = UnityEditor.Editor.CreateEditor(asset, typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GenericInspector"));
                var rows = editor.serializedObject.FindProperty("Direct.datas");
                rows.isExpanded = true;
                rows.GetArrayElementAtIndex(0).isExpanded = true;
                window.position = new Rect(100, 100, 650, 700);
                window.rootVisualElement.Add(new InspectorElement(editor));
                window.Show();
                yield return WaitForBinding(() =>
                    window.rootVisualElement.Query<TextField>().ToList().Count(f => f.bindingPath == "Direct.datas.Array.data[0].Key" && f.value == "a") == 1 &&
                    window.rootVisualElement.Query<IntegerField>().ToList().Count(f => f.bindingPath == "Direct.datas.Array.data[0].Value" && f.value == 1) == 1,
                    "默认 Inspector 未在时限内生成唯一绑定的 Key/Value 控件。");
                var key = window.rootVisualElement.Query<TextField>().ToList()
                    .SingleOrDefault(f => f.bindingPath == "Direct.datas.Array.data[0].Key");
                var value = window.rootVisualElement.Query<IntegerField>().ToList()
                    .SingleOrDefault(f => f.bindingPath == "Direct.datas.Array.data[0].Value");
                Assert.That(key, Is.Not.Null, "默认 Inspector 未生成绑定的 Key 控件: " + string.Join(";", window.rootVisualElement.Query<TextField>().ToList().Select(f => f.label + ":" + f.bindingPath)));
                Assert.That(value, Is.Not.Null);
                Undo.IncrementCurrentGroup();
                key.value = "edited";
                value.value = 7;
                yield return null;
                Undo.FlushUndoRecordObjects();
                Assert.That(asset.Direct.ContainsKey("a"), Is.False);
                Assert.That(asset.Direct["edited"], Is.EqualTo(7));
                Undo.PerformUndo();
                yield return WaitForBinding(() => key.value == "a" && value.value == 1, "Undo 后绑定控件未刷新。");
                Assert.That(asset.Direct["a"], Is.EqualTo(1));
                Assert.That(asset.Direct.ContainsKey("edited"), Is.False);
                Undo.PerformRedo();
                yield return WaitForBinding(() => key.value == "edited" && value.value == 7, "Redo 后绑定控件未刷新。");
                Assert.That(asset.Direct["edited"], Is.EqualTo(7));
                rows = editor.serializedObject.FindProperty("Direct.datas");
                rows.arraySize = 2;
                rows.GetArrayElementAtIndex(1).isExpanded = true;
                rows.GetArrayElementAtIndex(1).FindPropertyRelative("Key").stringValue = "added";
                rows.GetArrayElementAtIndex(1).FindPropertyRelative("Value").intValue = 3;
                editor.serializedObject.ApplyModifiedProperties();
                yield return WaitForBinding(() => window.rootVisualElement.Query<IntegerField>().ToList()
                    .Count(f => f.bindingPath == "Direct.datas.Array.data[1].Value" && f.value == 3) == 1,
                    "新增行未在时限内生成唯一绑定的 Value 控件。");
                var addedValue = window.rootVisualElement.Query<IntegerField>().ToList()
                    .Single(f => f.bindingPath == "Direct.datas.Array.data[1].Value");
                addedValue.value = 8;
                yield return null;
                Assert.That(asset.Direct["added"], Is.EqualTo(8));
                rows = editor.serializedObject.FindProperty("Direct.datas");
                rows.MoveArrayElement(1, 0);
                editor.serializedObject.ApplyModifiedProperties();
                yield return null;
                yield return null;
                Assert.That(asset.Direct.Keys, Is.EqualTo(new[] { "added", "edited" }));
                rows = editor.serializedObject.FindProperty("Direct.datas");
                rows.DeleteArrayElementAtIndex(0);
                editor.serializedObject.ApplyModifiedProperties();
                yield return null;
                yield return null;
                Assert.That(asset.Direct.Keys, Is.EqualTo(new[] { "edited" }));
                Assert.That(asset.Direct["edited"], Is.EqualTo(7));
            }
            finally
            {
                window.Release();
                if (editor != null)
                {
                    Object.DestroyImmediate(editor);
                }

                Undo.ClearUndo(asset);
                Object.DestroyImmediate(asset);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MultiObjectRowsAreDiagnosedSeparatelyAndNeverExposeStructuralControls(bool differentLength)
        {
            var a = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            var b = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            a.name = "First target";
            b.name = "Second target";
            try
            {
                a.Direct.Add("a", 1);
                b.Direct.Add("b", 2);
                if (differentLength)
                {
                    b.Direct.Add("extra", 3);
                }

                var beforeA = JsonUtility.ToJson(a);
                var beforeB = JsonUtility.ToJson(b);
                using (var serialized = new SerializedObject(new Object[] { a, b }))
                {
                    var property = serialized.FindProperty("Direct");
                    var ui = GetDrawer(property).CreatePropertyGUI(property);
                    var help = ui.Q<HelpBox>().text;
                    Assert.That(help, Does.Contain("First target").And.Contain("Second target").And.Contain("禁止批量增删/重排"));
                    Assert.That(ui.Query<PropertyField>().ToList().Any(f => f.bindingPath == "Direct.datas"), Is.False);
                    Assert.That(ui.Q<Foldout>().enabledSelf, Is.EqualTo(!differentLength));
                    Assert.That(JsonUtility.ToJson(a), Is.EqualTo(beforeA));
                    Assert.That(JsonUtility.ToJson(b), Is.EqualTo(beforeB));
                    if (!differentLength)
                    {
                        var value = serialized.FindProperty("Direct.datas.Array.data[0].Value");
                        Assert.That(value.hasMultipleDifferentValues, Is.True);
                        value.intValue = 9;
                        serialized.ApplyModifiedProperties();
                        Assert.That(a.Direct["a"], Is.EqualTo(9));
                        Assert.That(b.Direct["b"], Is.EqualTo(9));
                        Assert.That(a.Direct.ContainsKey("b"), Is.False);
                        Assert.That(b.Direct.ContainsKey("a"), Is.False);
                    }
                }
            }
            finally
            {
                Undo.ClearUndo(a);
                Undo.ClearUndo(b);
                Object.DestroyImmediate(a);
                Object.DestroyImmediate(b);
            }
        }

        [UnityTest]
        public IEnumerator ToolkitMultiObjectMixedFieldEditKeepsIndependentKeysAndDisablesChangedStructure()
        {
            var a = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            var b = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            var window = ScriptableObject.CreateInstance<DictionaryInspectorTestWindow>();
            var serialized = new SerializedObject(new Object[] { a, b });
            try
            {
                a.Direct.Add("a", 1);
                b.Direct.Add("b", 2);
                serialized.Update();
                var property = serialized.FindProperty("Direct");
                property.isExpanded = true;
                property.FindPropertyRelative("datas").GetArrayElementAtIndex(0).isExpanded = true;
                var ui = GetDrawer(property).CreatePropertyGUI(property);
                ui.Bind(serialized);
                window.rootVisualElement.Add(ui);
                window.Show();
                yield return WaitForBinding(() => ui.Query<IntegerField>().ToList()
                    .Any(f => f.bindingPath == "Direct.datas.Array.data[0].Value" && f.showMixedValue),
                    "多选 Value 控件未在时限内完成混合值绑定。");
                var value = ui.Query<IntegerField>().ToList().Single(f => f.bindingPath == "Direct.datas.Array.data[0].Value");
                Assert.That(value.showMixedValue, Is.True);
                value.value = 9;
                yield return null;
                Assert.That(a.Direct["a"], Is.EqualTo(9));
                Assert.That(b.Direct["b"], Is.EqualTo(9));
                Assert.That(a.Direct.ContainsKey("b"), Is.False);
                Assert.That(b.Direct.ContainsKey("a"), Is.False);
                using (var second = new SerializedObject(b))
                {
                    var rows = second.FindProperty("Direct.datas");
                    rows.arraySize++;
                    rows.GetArrayElementAtIndex(1).FindPropertyRelative("Key").stringValue = "extra";
                    second.ApplyModifiedProperties();
                }
                serialized.Update();
                yield return WaitForBinding(() => !ui.Q<Foldout>().enabledSelf && ui.Q<HelpBox>().text.Contains("禁止批量编辑"),
                    "目标结构不一致后，原生变更监听未在时限内禁用多选结构编辑。");
                Assert.That(ui.Q<Foldout>().enabledSelf, Is.False);
                Assert.That(ui.Q<HelpBox>().text, Does.Contain("禁止批量编辑"));
                Assert.That(a.Direct.Count, Is.EqualTo(1));
                Assert.That(b.Direct.Count, Is.EqualTo(2));
            }
            finally
            {
                window.Release();
                serialized.Dispose();
                Undo.ClearUndo(a);
                Undo.ClearUndo(b);
                Object.DestroyImmediate(a);
                Object.DestroyImmediate(b);
            }
        }

        [Test]
        public void PrefabOverrideApplyRevertAndReloadPreserveRowsAndProjection()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var folder = "Assets/Tests/YLFramework/EditMode/DictionaryTemp-" + Guid.NewGuid().ToString("N");
            var path = folder + "/dictionary.prefab";
            GameObject original = null;
            GameObject instance = null;
            GameObject contents = null;
            try
            {
                AssetDatabase.CreateFolder("Assets/Tests/YLFramework/EditMode", folder.Substring(folder.LastIndexOf('/') + 1));
                original = new GameObject("DictionaryPrefab");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(original, scene);
                var originalDictionary = original.AddComponent<SerializedDictionaryEditorComponent>().Direct;
                originalDictionary.Add("a", 1);
                originalDictionary.Add("b", 2);
                var prefab = PrefabUtility.SaveAsPrefabAsset(original, path);
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                var component = instance.GetComponent<SerializedDictionaryEditorComponent>();
                Assert.That(component.Direct["a"], Is.EqualTo(1));
                using (var serialized = new SerializedObject(component))
                {
                    var key = serialized.FindProperty("Direct.datas.Array.data[0].Key");
                    var value = serialized.FindProperty("Direct.datas.Array.data[0].Value");
                    Assert.That(GetDrawer(serialized.FindProperty("Direct")), Is.Not.Null);
                    key.stringValue = "applied";
                    value.intValue = 4;
                    serialized.FindProperty("Direct.datas.Array.data[1].Key").stringValue = "applied";
                    serialized.ApplyModifiedProperties();
                    Assert.That(component.Direct.ContainsKey("a"), Is.False);
                    Assert.That(component.Direct["applied"], Is.EqualTo(4));
                    Assert.That(component.Direct.InvalidEntryCount, Is.EqualTo(1));
                    Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, false), Is.True);
                    PrefabUtility.ApplyPrefabInstance(instance, InteractionMode.AutomatedAction);
                    serialized.Update();
                    value = serialized.FindProperty("Direct.datas.Array.data[0].Value");
                    value.intValue = 8;
                    serialized.ApplyModifiedProperties();
                    Assert.That(component.Direct["applied"], Is.EqualTo(8));
                    PrefabUtility.RevertPrefabInstance(instance, InteractionMode.AutomatedAction);
                    Assert.That(component.Direct["applied"], Is.EqualTo(4));
                    Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, false), Is.False);
                }
                Object.DestroyImmediate(instance);
                instance = null;
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                contents = PrefabUtility.LoadPrefabContents(path);
                Assert.That(contents.GetComponent<SerializedDictionaryEditorComponent>().Direct["applied"], Is.EqualTo(4));
                Assert.That(contents.GetComponent<SerializedDictionaryEditorComponent>().Direct.ContainsKey("a"), Is.False);
                var restored = contents.GetComponent<SerializedDictionaryEditorComponent>();
                Assert.That(restored.Direct.InvalidEntryCount, Is.EqualTo(1));
                using (var serialized = new SerializedObject(restored))
                {
                    var rows = serialized.FindProperty("Direct.datas");
                    Assert.That(rows.arraySize, Is.EqualTo(2));
                    rows.GetArrayElementAtIndex(1).FindPropertyRelative("Key").stringValue = "repaired";
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    Assert.That(restored.Direct["repaired"], Is.EqualTo(2));
                    Assert.That(restored.Direct.InvalidEntryCount, Is.Zero);
                    PrefabUtility.SaveAsPrefabAsset(contents, path);
                }
                PrefabUtility.UnloadPrefabContents(contents);
                contents = PrefabUtility.LoadPrefabContents(path);
                Assert.That(contents.GetComponent<SerializedDictionaryEditorComponent>().Direct["repaired"], Is.EqualTo(2));
                Assert.That(contents.GetComponent<SerializedDictionaryEditorComponent>().Direct.InvalidEntryCount, Is.Zero);
            }
            finally
            {
                if (contents != null)
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }

                if (instance != null)
                {
                    Object.DestroyImmediate(instance);
                }

                if (original != null)
                {
                    Object.DestroyImmediate(original);
                }

                EditorSceneManager.ClosePreviewScene(scene);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }

    internal class DictionaryInspectorTestWindow : EditorWindow
    {
        internal UnityEditor.Editor LegacyEditor;
        internal void Release()
        {
            LegacyEditor = null;
            rootVisualElement.Unbind();
            rootVisualElement.Clear();
            Close();
            // 尚未 Show 的窗口没有容器，Close 不会销毁 CreateInstance 创建的对象。
            if (this != null)
            {
                Object.DestroyImmediate(this);
            }
        }

        private void OnGUI()
        {
            if (LegacyEditor != null)
            {
                LegacyEditor.OnInspectorGUI();
            }
        }
    }

    [CanEditMultipleObjects]
    internal class DictionaryLegacyTestEditor : UnityEditor.Editor
    {
        internal int DrawCount;
        internal float DrawnHeight;
        internal float NarrowWidth;
        internal string[] MenuPaths;
        internal string[] MenuLabels;
        public override void OnInspectorGUI()
        {
            serializedObject.UpdateIfRequiredOrScript();
            var property = serializedObject.FindProperty("Direct");
            DrawnHeight = EditorGUI.GetPropertyHeight(property, true);
            if (NarrowWidth > 0)
            {
                EditorGUI.PropertyField(new Rect(0, 0, NarrowWidth, DrawnHeight), property, true);
            }
            else
            {
                EditorGUILayout.PropertyField(property, true);
            }

            if (MenuPaths != null)
            {
                var labels = new List<string>();
#if UNITY_6000_2_OR_NEWER
                var parameterTypes = new[] { typeof(SerializedProperty), typeof(SerializedProperty), typeof(GenericMenu) };
#else
                var parameterTypes = new[] { typeof(SerializedProperty), typeof(SerializedProperty), typeof(GenericMenu), typeof(VisualElement) };
#endif
                var method = typeof(EditorGUI).GetMethod("FillPropertyContextMenu", BindingFlags.Static | BindingFlags.NonPublic,
                    null, parameterTypes, null);
                Assert.That(method, Is.Not.Null, "The expected native property context-menu signature must exist.");
                foreach (var path in MenuPaths)
                {
                    var arguments = new object[parameterTypes.Length];
                    arguments[0] = serializedObject.FindProperty(path);
                    arguments[2] = new GenericMenu();
                    var menu = (GenericMenu)method.Invoke(null, arguments);
                    var items = (IEnumerable)typeof(GenericMenu).GetProperty("menuItems", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(menu);
                    foreach (var item in items)
                    {
                        var content = (GUIContent)item.GetType().GetField("content", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(item);
                        labels.Add(content.text);
                    }
                }
                MenuLabels = labels.ToArray();
            }
            serializedObject.ApplyModifiedProperties();
            DrawCount++;
        }
    }
}
