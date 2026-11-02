using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using YLFramework.DataAssets;
using YLFramework.Editor.Common;
#if UNITY_6000_2_OR_NEWER
using TreeViewItem = UnityEditor.IMGUI.Controls.TreeViewItem<int>;
#endif

namespace YLFramework.Editor.DataAssets
{
    /// <summary>将根列表槽位展示为多列树，保留坏引用的可选中性，并提供显式作者操作。</summary>
    public sealed class ScriptableObjectListTree : ReorderableTreeView<ScriptableObjectData>
    {
        private readonly ScriptableObjectConfigs _owner;
        private ScriptableObjectData[] _rows = Array.Empty<ScriptableObjectData>();
        private readonly GUIContent _content = new GUIContent();
        /// <summary>单选条目时返回条目引用，多选、空选或缺失槽位时通知空引用。</summary>
        public Action<ScriptableObjectData> Selected;
        /// <summary>作者命令刷新完成或实际拖动重排后的通知，不保证资产已保存。</summary>
        public Action Changed;

        /// <summary>绑定根的实时列表并建立表头和行快照；使用方结束时须释放树的继承订阅。</summary>
        /// <param name="owner">列表及其 Undo 数据的所属根。</param>
        /// <param name="state">窗口保留的树展示状态。</param>
        public ScriptableObjectListTree(ScriptableObjectConfigs owner, TreeViewState state)
            : base(owner, () => owner.configs, state, new MultiColumnHeader(CreateHeader()))
        {
            this._owner = owner;
            rowHeight = 25;
            showBorder = true;
            multiColumnHeader.sortingChanged += SortChanged;
            Reload();
        }

        /// <summary>配置图标、ID 与名称列，仅允许按 ID 或名称排序。</summary>
        private static MultiColumnHeaderState CreateHeader() => new MultiColumnHeaderState(new[]
        {
            new MultiColumnHeaderState.Column { headerContent = new GUIContent("Icon"), width = 42, minWidth = 42, maxWidth = 42, canSort = false, autoResize = false },
            new MultiColumnHeaderState.Column { headerContent = new GUIContent("ID"), width = 65, minWidth = 45, canSort = true, autoResize = false },
            new MultiColumnHeaderState.Column { headerContent = new GUIContent("Name"), width = 220, minWidth = 80, canSort = true, autoResize = true }
        });

        /// <summary>保存当前列表引用快照，并为每个槽位创建独立行，不删除空项或合并重复 ID。</summary>
        protected override void OnBuildRoot(TreeViewItem root, IList<ScriptableObjectData> data)
        {
            _rows = data.ToArray();
            // TreeView ID是槽位，不是业务ID；坏配置/重复ID仍可选中并修复。
            for (var i = 0; i < _rows.Length; i++)
            {
                var item = _rows[i];
                root.AddChild(new TreeViewItem(i + 1, 0, item ? item.id + " " + item.configName : "(Missing reference)"));
            }
        }

        /// <summary>按树行 ID 借读快照条目；越界或缺失槽位返回空引用。</summary>
        /// <param name="rowId">从 1 开始的槽位行 ID，不是配置的业务 ID。</param>
        public ScriptableObjectData GetData(int rowId) => rowId > 0 && rowId <= _rows.Length ? _rows[rowId - 1] : null;

        /// <summary>重建树后按真实对象引用恢复选择，避免列表重排将选择转移到其他条目。</summary>
        public void RefreshKeepingSelection()
        {
            var selected = GetSelection().Select(GetData).Where(item => item).ToArray();
            Reload();
            RestoreSelection(selected);
        }

        /// <summary>逐个匹配仍存在的选中引用，并通知当前单选结果。</summary>
        private void RestoreSelection(IEnumerable<ScriptableObjectData> selected)
        {
            var remaining = new List<ScriptableObjectData>(selected);
            var ids = new List<int>();
            for (var i = 0; i < _rows.Length; i++)
            {
                var index = remaining.FindIndex(item => ReferenceEquals(item, _rows[i]));
                if (index < 0)
                {
                    continue;
                }

                ids.Add(i + 1);
                remaining.RemoveAt(index);
            }
            SetSelection(ids);
            Selected?.Invoke(ids.Count == 1 ? GetData(ids[0]) : null);
        }

        /// <summary>在基类检测到列表或 Undo 变化时使用保留选择的刷新路径。</summary>
        protected override void RefreshDatas() => RefreshKeepingSelection();

        /// <summary>将树的单选槽位转换为条目回调，不将多选解释为单个编辑目标。</summary>
        protected override void SelectionChanged(IList<int> selectedIds)
        {
            Selected?.Invoke(selectedIds.Count == 1 ? GetData(selectedIds[0]) : null);
        }

        /// <summary>绘制每行的图标预览、业务 ID 和名称说明，缺失条目仍保留行展示。</summary>
        protected override void RowGUI(RowGUIArgs args)
        {
            var data = GetData(args.item.id);
            for (var i = 0; i < args.GetNumVisibleColumns(); i++)
            {
                var rect = args.GetCellRect(i);
                switch (args.GetColumn(i))
                {
                    case 0:
                        if (data && data.icon != null)
                        {
                            EditorGUI.LabelField(rect, EditorAssetTool.GetTextureGuiContent(data.icon.projectName, data.icon.iconName));
                        }

                        break;
                    case 1:
                        EditorGUI.LabelField(rect, data ? data.id.ToString() : "-");
                        break;
                    case 2:
                        _content.text = data ? data.configName : "(Missing reference)";
                        _content.tooltip = data ? data.description : "Use Unlink to remove this missing slot.";
                        EditorGUI.LabelField(rect, _content);
                        break;
                }
            }
        }

        /// <summary>在树空白区域打开新条目创建菜单。</summary>
        protected override void ContextClicked() => ShowCreateMenu();

        /// <summary>基于当前选中槽位构建解除引用、复制、确认删除和创建菜单。</summary>
        protected override void ContextClickedItem(int id)
        {
            if (!GetSelection().Contains(id))
            {
                SetSelection(new[] { id });
            }

            var selected = GetSelection().OrderBy(value => value).Select(value => (row: value - 1, data: GetData(value))).ToArray();
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Unlink References (Undo)"), false, () => Run(() =>
            {
                foreach (var item in selected.Reverse())
                {
                    ScriptableObjectConfigsController.RemoveReference(_owner, item.row, item.data);
                }
            }));
            if (selected.All(item => item.data))
            {
                menu.AddItem(new GUIContent("Copy"), false, () =>
                {
                    var copies = new List<ScriptableObjectData>();
                    Run(() =>
                    {
                        foreach (var item in selected)
                        {
                            copies.Add(ScriptableObjectConfigsController.CopyData(_owner, item.data));
                        }
                    }, () => copies);
                });
                menu.AddItem(new GUIContent("Delete Owned Assets..."), false, () => Run(() =>
                {
                    if (selected.Any(item => !ScriptableObjectConfigsController.IsOwned(_owner, item.data)))
                    {
                        throw new InvalidOperationException("The selection contains external assets. Unlink those references instead.");
                    }

                    if (!EditorUtility.DisplayDialog("Delete DataAssets", "Delete selected owned assets? Standalone asset files cannot be restored by Undo. External references are never deleted.", "Delete", "Cancel"))
                    {
                        return;
                    }

                    foreach (var item in selected)
                    {
                        if (item.data)
                        {
                            ScriptableObjectConfigsController.DeleteData(_owner, item.data, true);
                        }
                    }
                }));
            }
            menu.AddSeparator(string.Empty);
            AddCreateItems(menu);
            menu.ShowAsContext();
        }

        /// <summary>显示根绑定允许的具体类型创建菜单，创建后选择新条目。</summary>
        public void ShowCreateMenu()
        {
            var menu = new GenericMenu();
            AddCreateItems(menu);
            menu.ShowAsContext();
        }

        /// <summary>按可用条目类型填充创建命令，绑定失效时用禁用菜单项显示原因。</summary>
        private void AddCreateItems(GenericMenu menu)
        {
            try
            {
                foreach (var type in ScriptableObjectConfigsController.GetDataTypes(_owner))
                {
                    var selectedType = type;
                    menu.AddItem(new GUIContent("Create/" + type.FullName.Replace('.', '/')), false,
                        () =>
                        {
                            ScriptableObjectData created = null;
                            Run(() => created = ScriptableObjectConfigsController.CreateData(selectedType, _owner), () => new[] { created });
                        });
                }
            }
            catch (InvalidOperationException error)
            {
                menu.AddDisabledItem(new GUIContent(error.Message));
            }
        }

        /// <summary>将表头排序选择转交作者控制器，并沿用命令后的树刷新流程。</summary>
        private void SortChanged(MultiColumnHeader header) => Run(() => ScriptableObjectConfigsController.Sort(_owner,
            header.sortedColumnIndex == 1 ? DataAssetSort.Id : DataAssetSort.Name, header.IsSortedAscending(header.sortedColumnIndex)));

        /// <summary>执行作者命令，显示预期输入或文件错误，再重建树并恢复原选择或指定新选择。</summary>
        private void Run(Action operation, Func<IEnumerable<ScriptableObjectData>> selectAfter = null)
        {
            var selected = GetSelection().Select(GetData).Where(item => item).ToArray();
            try
            {
                operation();
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is System.IO.IOException)
            {
                EditorUtility.DisplayDialog("DataAssets", error.Message, "Close");
            }
            Reload();
            RestoreSelection(selectAfter == null ? selected : selectAfter().Where(item => item));
            Changed?.Invoke();
        }

        /// <summary>在基类完成带 Undo 的实际列表重排后使根索引失效并通知窗口。</summary>
        protected override void OnDragEnd()
        {
            _owner.ClearCache();
            Changed?.Invoke();
        }
    }
}
