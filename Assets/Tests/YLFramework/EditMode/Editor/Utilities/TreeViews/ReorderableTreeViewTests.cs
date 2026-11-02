using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
#if UNITY_6000_2_OR_NEWER
using TreeView = UnityEditor.IMGUI.Controls.TreeView<int>;
using TreeViewItem = UnityEditor.IMGUI.Controls.TreeViewItem<int>;
#endif

namespace YLFramework.Tests.Extensions.Shared
{
    public class ReorderableTreeViewTests
    {
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const string FixtureParent = "Assets/Tests/YLFramework/Fixtures/Extensions/Shared";
        private readonly List<E0ReorderableTestWindow> _windows = new List<E0ReorderableTestWindow>();
        private readonly List<E0ReorderableListAsset> _assets = new List<E0ReorderableListAsset>();
        private string _folder;
        private EditorWindow _previousFocus;

        [SetUp]
        public void SetUp()
        {
            Assert.That(DragAndDrop.objectReferences, Is.Empty, "不接管用户已有的对象拖拽。");
            Assert.That(DragAndDrop.paths, Is.Null.Or.Empty, "不接管用户已有的文件拖拽。");
            _previousFocus = EditorWindow.focusedWindow;
            Assert.That(AssetDatabase.IsValidFolder(FixtureParent), Is.True);
            _folder = FixtureParent + "/Run-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder(FixtureParent, _folder.Substring(_folder.LastIndexOf('/') + 1));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var window in _windows)
            {
                if (window)
                {
                    window.Close();
                }
            }

            _windows.Clear();
            foreach (var asset in _assets)
            {
                if (asset)
                {
                    Undo.ClearUndo(asset);
                }
            }

            _assets.Clear();
            if (_folder != null && AssetDatabase.IsValidFolder(_folder))
            {
                Assert.That(AssetDatabase.DeleteAsset(_folder), Is.True);
            }

            _folder = null;
            if (_previousFocus)
            {
                _previousFocus.Focus();
            }
        }

        private E0ReorderableListAsset Asset(string name)
        {
            var asset = ScriptableObject.CreateInstance<E0ReorderableListAsset>();
            AssetDatabase.CreateAsset(asset, _folder + "/" + name + ".asset");
            foreach (var label in new[] { "A", "B", "C", "D", "E" })
            {
                var entry = new Texture2D(1, 1) { name = label };
                AssetDatabase.AddObjectToAsset(entry, asset);
                asset.items.Add(entry);
            }
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
            _assets.Add(asset);
            return asset;
        }

        private E0ReorderableTestWindow Window(E0ReorderableListAsset asset, Func<IList<Object>> getData = null,
            TreeView prepared = null)
        {
            var generic = typeof(YLFramework.Editor.Common.TreeViewStateGroup).Assembly
                .GetType("YLFramework.Editor.Common.ReorderableTreeView`1");
            Assert.That(generic, Is.Not.Null, "缺少实际列表重排能力，不能只提供字段契约。");
            var tree = prepared ?? (TreeView)Activator.CreateInstance(generic.MakeGenericType(typeof(Object)),
                asset, getData ?? (() => asset.items), new TreeViewState());
            var window = ScriptableObject.CreateInstance<E0ReorderableTestWindow>();
            window.hideFlags = HideFlags.HideAndDontSave;
            window.titleContent = new GUIContent("E0 owned reorder " + _windows.Count);
            window.Tree = tree;
            _windows.Add(window);
            window.ShowUtility();
            window.position = new Rect(160 + _windows.Count * 280, 180, 260, 240);
            tree.Reload();
            return window;
        }

        private static object Invoke(TreeView tree, string name, params (string name, object value)[] fields)
        {
            var method = tree.GetType().GetMethod(name, InstanceMembers);
            Assert.That(method, Is.Not.Null, name);
            var parameter = method.GetParameters().Single().ParameterType;
            var args = Activator.CreateInstance(parameter);
            foreach (var field in fields)
            {
                var property = parameter.GetProperty(field.name, InstanceMembers);
                if (property != null)
                {
                    property.SetValue(args, field.value);
                }
                else
                {
                    var info = parameter.GetField(field.name, InstanceMembers);
                    Assert.That(info, Is.Not.Null, field.name);
                    info.SetValue(args, field.value);
                }
            }
            return method.Invoke(tree, new[] { args });
        }

        private static TreeViewItem Root(TreeView tree) => (TreeViewItem)typeof(TreeView)
            .GetProperty("rootItem", InstanceMembers).GetValue(tree);

        private static void Start(E0ReorderableTestWindow window, params int[] ids)
        {
            window.Tree.SetSelection(ids);
            var item = window.Tree.GetRows().Single(row => row.id == ids[0]);
            Assert.That(Invoke(window.Tree, "CanStartDrag", ("draggedItem", item), ("draggedItemIDs", ids)), Is.True);
            // StartDrag会进入原生OS拖拽循环；自动测试真实准备/放置逻辑，不启动全局桌面拖拽。
            window.Dispatch(EventType.MouseDrag, () =>
                Assert.That(typeof(YLFramework.Editor.Common.ReorderableTreeView<Object>).GetMethod("PrepareDrag", InstanceMembers)
                    .Invoke(window.Tree, new object[] { ids }), Is.True));
        }

        private static DragAndDropVisualMode Drop(E0ReorderableTestWindow window, int index, bool perform = true)
        {
            var mode = DragAndDropVisualMode.None;
            window.Dispatch(perform ? EventType.DragPerform : EventType.DragUpdated, () =>
            {
                var method = window.Tree.GetType().GetMethod("HandleDragAndDrop", InstanceMembers);
                var parameter = method.GetParameters()[0].ParameterType;
                var positionType = parameter.GetProperty("dragAndDropPosition", InstanceMembers)?.PropertyType ??
                    parameter.GetField("dragAndDropPosition", InstanceMembers).FieldType;
                mode = (DragAndDropVisualMode)Invoke(window.Tree, "HandleDragAndDrop",
                    ("parentItem", Root(window.Tree)), ("insertAtIndex", index), ("performDrop", perform),
                    ("dragAndDropPosition", Enum.Parse(positionType, "BetweenItems")));
            });
            return mode;
        }

        [UnityTest]
        public IEnumerator ImmediateDropPreservesDuplicateReferencesAndNullSlots()
        {
            var asset = Asset("duplicates");
            var a = asset.items[0];
            var b = asset.items[1];
            var c = asset.items[2];
            asset.items = new List<Object> { a, b, a, null, c };
            var window = Window(asset);
            yield return null;
            Start(window, 3);
            Assert.That(Drop(window, 5), Is.EqualTo(DragAndDropVisualMode.Move));
            CollectionAssert.AreEqual(new Object[] { a, b, null, c, a }, asset.items);
            Assert.That(window.guiCalls, Is.GreaterThan(0));
        }

        [UnityTest]
        public IEnumerator NonContiguousSelectionMovesInSourceOrder()
        {
            var asset = Asset("multi");
            var before = asset.items.ToArray();
            var window = Window(asset);
            yield return null;
            Start(window, 4, 2);
            Assert.That(Drop(window, 5), Is.EqualTo(DragAndDropVisualMode.Move));
            CollectionAssert.AreEqual(new[] { before[0], before[2], before[4], before[1], before[3] }, asset.items);
        }

        [UnityTest]
        public IEnumerator UndoRedoRefreshesTheListAndAllowsAnotherReorder()
        {
            var asset = Asset("undo");
            var before = asset.items.ToArray();
            var window = Window(asset);
            yield return null;
            Start(window, 1);
            Assert.That(Drop(window, 5), Is.EqualTo(DragAndDropVisualMode.Move));
            var reordered = new[] { before[1], before[2], before[3], before[4], before[0] };
            CollectionAssert.AreEqual(reordered, asset.items);
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            yield return null;
            CollectionAssert.AreEqual(before, asset.items);
            CollectionAssert.AreEqual(new[] { "A", "B", "C", "D", "E" }, window.Tree.GetRows().Select(row => row.displayName));
            Undo.PerformRedo();
            yield return null;
            CollectionAssert.AreEqual(reordered, asset.items);
            Start(window, 5);
            Assert.That(Drop(window, 0), Is.EqualTo(DragAndDropVisualMode.Move));
            CollectionAssert.AreEqual(before, asset.items);
            AssetDatabase.SaveAssetIfDirty(asset);
            var path = AssetDatabase.GetAssetPath(asset);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            CollectionAssert.AreEqual(before, AssetDatabase.LoadAssetAtPath<E0ReorderableListAsset>(path).items);
        }

        [UnityTest]
        public IEnumerator SameTypeWindowsCannotConsumeEachOthersDrag()
        {
            var first = Asset("first");
            var before = first.items.ToArray();
            var second = Asset("second");
            var other = second.items.ToArray();
            var left = Window(first);
            var right = Window(second);
            yield return null;
            Start(left, 1);
            Assert.That(Drop(right, 5), Is.EqualTo(DragAndDropVisualMode.Rejected));
            CollectionAssert.AreEqual(before, first.items);
            CollectionAssert.AreEqual(other, second.items);
            Assert.That(Drop(left, 5), Is.EqualTo(DragAndDropVisualMode.Move), "拒绝外来拖拽不能清除其来源窗口的责任。");
            Assert.That(left.GetInstanceID(), Is.Not.EqualTo(right.GetInstanceID()));
            Assert.That(left.guiCalls, Is.GreaterThan(0));
            Assert.That(right.guiCalls, Is.GreaterThan(0));
        }

        [UnityTest]
        public IEnumerator ChangedListAndInvalidDestinationAreRejectedWithoutMutation()
        {
            var asset = Asset("invalid");
            var window = Window(asset);
            yield return null;
            Start(window, 1);
            Assert.That(Drop(window, -1), Is.EqualTo(DragAndDropVisualMode.Rejected));
            Start(window, 1);
            Assert.That(Drop(window, 6), Is.EqualTo(DragAndDropVisualMode.Rejected));
            Start(window, 1);
            asset.items.Reverse();
            var changed = asset.items.ToArray();
            Assert.That(Drop(window, 5), Is.EqualTo(DragAndDropVisualMode.Rejected));
            CollectionAssert.AreEqual(changed, asset.items);
        }

        [UnityTest]
        public IEnumerator HoverAndNoOpDoNotDirtyTheAsset()
        {
            var asset = Asset("noop");
            var before = asset.items.ToArray();
            var window = Window(asset);
            yield return null;
            Assert.That(EditorUtility.IsDirty(asset), Is.False);
            Start(window, 2);
            Assert.That(Drop(window, 5, false), Is.EqualTo(DragAndDropVisualMode.Move));
            CollectionAssert.AreEqual(before, asset.items);
            Assert.That(EditorUtility.IsDirty(asset), Is.False);
            Assert.That(Drop(window, 2), Is.EqualTo(DragAndDropVisualMode.Move));
            CollectionAssert.AreEqual(before, asset.items);
            Assert.That(EditorUtility.IsDirty(asset), Is.False);
        }

        [UnityTest]
        public IEnumerator DisposedTreeRejectsItsOutstandingDrag()
        {
            var asset = Asset("dispose");
            var before = asset.items.ToArray();
            var window = Window(asset);
            yield return null;
            Start(window, 1);
            ((IDisposable)window.Tree).Dispose();
            Assert.That(Drop(window, 5), Is.EqualTo(DragAndDropVisualMode.Rejected));
            CollectionAssert.AreEqual(before, asset.items);
        }

        [UnityTest]
        public IEnumerator UnselectedRowCannotStartADrag()
        {
            var asset = Asset("unselected");
            var window = Window(asset);
            yield return null;
            window.Tree.SetSelection(Array.Empty<int>());
            Assert.That(Invoke(window.Tree, "CanStartDrag", ("draggedItem", window.Tree.GetRows()[0]),
                ("draggedItemIDs", new[] { 1 })), Is.False);
        }

        [UnityTest]
        public IEnumerator TwoWindowsForTheSameAssetStillHaveIsolatedDragOwnership()
        {
            var asset = Asset("shared-owner");
            var before = asset.items.ToArray();
            var left = Window(asset);
            var right = Window(asset);
            yield return null;
            Start(left, 1);
            Assert.That(Drop(right, 5), Is.EqualTo(DragAndDropVisualMode.Rejected));
            CollectionAssert.AreEqual(before, asset.items);
            Assert.That(Drop(left, 5), Is.EqualTo(DragAndDropVisualMode.Move));
            CollectionAssert.AreEqual(new[] { before[1], before[2], before[3], before[4], before[0] }, asset.items);
        }

        [UnityTest]
        public IEnumerator StaleRowsInAnotherWindowCannotMoveADifferentObject()
        {
            var asset = Asset("stale-rows");
            var before = asset.items.ToArray();
            var left = Window(asset);
            var right = Window(asset);
            yield return null;
            var staleRow = right.Tree.GetRows()[0];
            Start(left, 1);
            Assert.That(Drop(left, 5), Is.EqualTo(DragAndDropVisualMode.Move));
            var current = new[] { before[1], before[2], before[3], before[4], before[0] };
            right.Tree.SetSelection(new[] { staleRow.id });
            Assert.That(Invoke(right.Tree, "CanStartDrag", ("draggedItem", staleRow),
                ("draggedItemIDs", new[] { staleRow.id })), Is.False, "旧行显示A时不能按新索引移动B。");
            CollectionAssert.AreEqual(current, asset.items);
            CollectionAssert.AreEqual(new[] { "B", "C", "D", "E", "A" }, right.Tree.GetRows().Select(row => row.displayName));
            Start(right, 1);
            Assert.That(Drop(right, 5), Is.EqualTo(DragAndDropVisualMode.Move));
            CollectionAssert.AreEqual(new[] { before[2], before[3], before[4], before[0], before[1] }, asset.items);
        }

        [UnityTest]
        public IEnumerator FilteredRowsUseActualDataIndicesAndKeepHiddenNullSlots()
        {
            var asset = Asset("filtered");
            var before = asset.items.ToArray();
            asset.items = new List<Object> { before[0], null, before[1], before[2] };
            var window = Window(asset, prepared: new FilteredTree(asset));
            yield return null;
            Start(window, before[1].GetInstanceID());
            Assert.That(Drop(window, 0), Is.EqualTo(DragAndDropVisualMode.Move));
            CollectionAssert.AreEqual(new Object[] { before[1], before[0], null, before[2] }, asset.items);
        }

        private sealed class FilteredTree : YLFramework.Editor.Common.ReorderableTreeView<Object>
        {
            private readonly E0ReorderableListAsset _asset;

            public FilteredTree(E0ReorderableListAsset asset) : base(asset, () => asset.items, new TreeViewState())
            {
                this._asset = asset;
            }

            protected override void OnBuildRoot(TreeViewItem root, IList<Object> datas)
            {
                foreach (var item in datas)
                {
                    if (item)
                    {
                        root.AddChild(new TreeViewItem(item.GetInstanceID(), 0, item.name));
                    }
                }
            }

            protected override int GetItemIndex(int id) => _asset.items.FindIndex(item => item && item.GetInstanceID() == id);
        }
    }
}
