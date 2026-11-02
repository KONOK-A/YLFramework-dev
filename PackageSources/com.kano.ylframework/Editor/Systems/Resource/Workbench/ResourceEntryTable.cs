using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>展示选择定义或解析目录的资源表，以项目和条目身份维护选择，不靠排序后的行号编辑。</summary>
    internal sealed class ResourceEntryTable : TreeView
    {
        /// <summary>单行的来源身份、可选解析条目和预计算显示内容。</summary>
        internal sealed class Row : TreeViewItem
        {
            internal string Guid;
            internal string Group;
            internal string Path;
            internal string Address;
            internal bool IsFolder;
            internal ResourceCatalogEntry Entry;
            internal string Search;
            internal string[] Cells;
            internal GUIContent[] Content;
        }

        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<int, Row> _byId = new();
        private readonly HashSet<int> _visibleIds = new();
        private Row[] _rows = Array.Empty<Row>();
        private string _projectGuid;
        private bool _isResolved;
        internal Action<Row> Locate;
        internal Action<Row> Context;
        internal Action SelectionUpdated;
        internal int Count => _rows.Length;
        internal int VisibleCount => _visibleIds.Count;
        internal Row SingleSelection => GetSelection().Count == 1 && _byId.TryGetValue(GetSelection()[0], out var row) ? row : null;
        internal int VisibleSelectionCount
        {
            get
            {
                var count = 0;
                foreach (var id in GetSelection())
                {
                    if (_visibleIds.Contains(id))
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        /// <summary>使用独立默认布局创建资源表。</summary>
        internal ResourceEntryTable() : this(new ResourceWorkbenchLayout()) { }
        /// <summary>沿用调用方列布局并建立表格刷新与排序行为。</summary>
        internal ResourceEntryTable(ResourceWorkbenchLayout layout) : base(new TreeViewState(), Header(layout))
        {
            rowHeight = ResourceWorkbenchGui.RowHeight;
            showBorder = true;
            showAlternatingRowBackgrounds = false;
            enableItemHovering = true;
            multiColumnHeader.sortingChanged += _ => Reload();
            multiColumnHeader.height = ResourceWorkbenchGui.BarHeight;
            Reload();
        }

        /// <summary>创建资源列，并在结构兼容时恢复原列宽、可见性和排序状态。</summary>
        private static MultiColumnHeader Header(ResourceWorkbenchLayout layout)
        {
            var names = new[] { "名称", "类型 / 目录", "地址", "定义状态", "Group", "完整路径" };
            var widths = new[] { 210f, 88, 210, 98, 110, 300 };
            var state = new MultiColumnHeaderState(names.Select((name, index) => new MultiColumnHeaderState.Column
            {
                headerContent = new GUIContent(name),
                width = widths[index],
                minWidth = index == 0 || index == 2 ? 100 : 64,
                autoResize = index == 0 || index == 2,
                canSort = true,
                allowToggleVisibility = index != 0
            }).ToArray())
            {
                visibleColumns = new[] { 0, 1, 2, 3 }
            };
            if (layout.columns != null && MultiColumnHeaderState.CanOverwriteSerializedFields(layout.columns, state))
            {
                MultiColumnHeaderState.OverwriteSerializedFields(layout.columns, state);
            }

            layout.columns = state;
            return new MultiColumnHeader(state);
        }

        /// <summary>从原始选择定义建立行，失效草稿仍可见，目录仅显示定义而不在此展开。</summary>
        internal void SetDefinitions(ResourceProject project, int group)
        {
            SetProject(project);
            _isResolved = false;
            var next = new List<Row>();
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            ResourceProjectPaths directories = null;
            try
            {
                directories = ResourceWorkbenchGui.DisplayPaths(project);
            }
            catch (InvalidOperationException) { /* 无效草稿仍显示原条目；正式解析会给出具体阻塞原因。 */ }
            for (var index = 0; index < project.groups.Count; index++)
            {
                if (group >= 0 && index != group)
                {
                    continue;
                }

                var owner = project.groups[index];
                foreach (var selection in owner.selections)
                {
                    var path = AssetDatabase.GUIDToAssetPath(selection.guid);
                    var folder = AssetDatabase.IsValidFolder(path);
                    var legal = directories != null && directories.ContainsSource(path);
                    var relative = legal ? directories.DisplayAddress(path) : path;
                    var address = folder ? relative + "/*" : string.IsNullOrEmpty(selection.address) ? relative : selection.address;
                    var key = "d/" + owner.id + "/" + selection.guid;
                    occurrences.TryGetValue(key, out var occurrence);
                    occurrences[key] = occurrence + 1;
                    var row = MakeRow(key + "/" + occurrence, selection.guid, owner.id, path, address,
                        folder ? "目录" : AssetDatabase.GetMainAssetTypeAtPath(path)?.Name ?? "资源缺失", !legal ? "目录范围无效" : folder ? "目录定义" : "文件定义");
                    row.IsFolder = folder;
                    row.Search += "\n" + string.Join("\n", selection.aliases ?? Array.Empty<string>());
                    next.Add(row);
                }
            }
            UpdateRows(next.ToArray());
        }

        /// <summary>以解析后的主资产和子资产建立只读行，禁止把这些行映射为可编辑选择项。</summary>
        internal void SetResolved(ResourceProject project, IEnumerable<ResourceCatalogEntry> entries, string group)
        {
            SetProject(project);
            _isResolved = true;
            UpdateRows(entries.Select(entry =>
            {
                var row = MakeRow("r/" + entry.AssetId, entry.AssetId.Substring(0, 32), group, entry.Location.Locator,
                    entry.Address.ToString(), entry.Location.AssetType.Name, entry.Address.SubAsset == null ? "解析主资产" : "派生子资产");
                row.Entry = entry;
                row.Search += "\n" + string.Join("\n", entry.Aliases);
                return row;
            }).ToArray());
        }

        /// <summary>项目 GUID 变化时清除临时行身份与选择，防止跨项目复用行号。</summary>
        private void SetProject(ResourceProject project)
        {
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project));
            if (guid == _projectGuid)
            {
                return;
            }

            _projectGuid = guid;
            _ids.Clear();
            SetSelection(Array.Empty<int>());
        }

        /// <summary>为同一项目中的稳定行键分配 ID，并预计算单元格、搜索文本和图标提示。</summary>
        private Row MakeRow(string key, string guid, string group, string path, string address, string type, string status)
        {
            if (!_ids.TryGetValue(key, out var id))
            {
                _ids.Add(key, id = _ids.Count + 1);
            }

            var name = string.IsNullOrEmpty(path) ? "[资源GUID失效]" : Path.GetFileName(path);
            var cells = new[] { name, type, address, status, group, path };
            return new Row
            {
                id = id,
                depth = 0,
                displayName = name,
                Guid = guid,
                Group = group,
                Path = path,
                Address = address,
                Search = string.Join("\n", cells),
                Cells = cells,
                Content = cells.Select((cell, index) => new GUIContent(cell, index == 0 ? AssetDatabase.GetCachedIcon(path) : null, path + "\n" + address)).ToArray()
            };
        }

        /// <summary>替换行快照后重建索引，仅保留仍存在的选择，搜索隐藏项不会因此丢失。</summary>
        private void UpdateRows(Row[] values)
        {
            _rows = values;
            _byId.Clear();
            foreach (var row in _rows)
            {
                _byId.Add(row.id, row);
            }

            var selection = GetSelection().Where(_byId.ContainsKey).ToArray();
            Reload();
            SetSelection(selection);
        }

        /// <summary>将完整 UI 选择按当前组和唯一 GUID 重解为定义索引；身份变化时拒绝整批操作。</summary>
        internal int[] GetSelectedIndices(ResourceProject project, int group)
        {
            if (_isResolved || group < 0 || group >= project.groups.Count)
            {
                return Array.Empty<int>();
            }

            if (AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project)) != _projectGuid)
            {
                throw new InvalidOperationException("Project已切换，请重新选择。");
            }

            var owner = project.groups[group];
            var indices = owner.selections.Select((value, index) => (Guid: value.guid, index)).ToLookup(x => x.Guid, StringComparer.Ordinal);
            var result = new List<int>();
            foreach (var id in GetSelection())
            {
                if (!_byId.TryGetValue(id, out var row) || row.Group != owner.id || Enumerable.Count<(string Guid, int index)>(indices[row.Guid]) != 1)
                {
                    throw new InvalidOperationException("定义身份已变化或存在重复GUID，请刷新后重新选择；未执行部分操作。");
                }

                result.Add(Enumerable.Single<(string Guid, int index)>(indices[row.Guid]).index);
            }
            return result.OrderBy(x => x).ToArray();
        }

        /// <summary>捕获唯一选择定义的项目、组、内容和地址身份；解析模式不保存行选择。</summary>
        internal ResourceWorkbenchRowState[] CaptureSelection(ResourceProject project)
        {
            if (_isResolved)
            {
                return Array.Empty<ResourceWorkbenchRowState>();
            }

            var saved = new List<ResourceWorkbenchRowState>();
            foreach (var id in GetSelection())
            {
                if (!_byId.TryGetValue(id, out var row))
                {
                    continue;
                }

                var groups = project.groups.Where(x => x.id == row.Group).ToArray();
                var definitions = groups.SelectMany(x => x.selections).Where(x => x.guid == row.Guid).ToArray();
                if (groups.Length != 1 || definitions.Length != 1)
                {
                    continue;
                }

                saved.Add(new ResourceWorkbenchRowState
                {
                    projectGuid = _projectGuid,
                    group = row.Group,
                    guid = row.Guid,
                    definition = groups[0].packing + "/" + JsonUtility.ToJson(definitions[0]),
                    address = row.Address
                });
            }
            return saved.ToArray();
        }

        /// <summary>仅恢复全部身份仍一致的定义行，并返回失效而未恢复的选择数。</summary>
        internal int RestoreSelection(ResourceProject project, ResourceWorkbenchRowState[] saved)
        {
            var selection = new List<int>();
            foreach (var item in saved ?? Array.Empty<ResourceWorkbenchRowState>())
            {
                if (_isResolved || item.projectGuid != _projectGuid)
                {
                    continue;
                }

                var groups = project.groups.Where(x => x.id == item.group).ToArray();
                var definitions = groups.SelectMany(x => x.selections).Where(x => x.guid == item.guid).ToArray();
                var matches = _rows.Where(x => x.Group == item.group && x.Guid == item.guid && x.Address == item.address).ToArray();
                if (groups.Length == 1 && definitions.Length == 1 && matches.Length == 1 &&
                    item.definition == groups[0].packing + "/" + JsonUtility.ToJson(definitions[0]))
                {
                    selection.Add(matches[0].id);
                }
            }
            SetSelection(selection);
            return (saved?.Length ?? 0) - selection.Count;
        }

        /// <summary>将当前行快照挂到不可见根节点，空表也提供有效子集合。</summary>
        protected override TreeViewItem BuildRoot()
        {
            var root = new TreeViewItem(0, -1, "Root");
            foreach (var row in _rows)
            {
                root.AddChild(row);
            }

            root.children ??= new List<TreeViewItem>();
            return root;
        }

        /// <summary>按搜索和列排序生成可见行，同时记录可见 ID 以区分隐藏选择。</summary>
        protected override IList<TreeViewItem> BuildRows(TreeViewItem root)
        {
            var filtered = _rows.Where(x => string.IsNullOrEmpty(searchString) || x.Search.IndexOf(searchString, StringComparison.OrdinalIgnoreCase) >= 0);
            var column = multiColumnHeader.sortedColumnIndex;
            if (column >= 0)
            {
                filtered = multiColumnHeader.IsSortedAscending(column)
                ? filtered.OrderBy(x => x.Cells[column], StringComparer.OrdinalIgnoreCase).ThenBy(x => x.id)
                : filtered.OrderByDescending(x => x.Cells[column], StringComparer.OrdinalIgnoreCase).ThenBy(x => x.id);
            }

            var result = filtered.Cast<TreeViewItem>().ToList();
            _visibleIds.Clear();
            foreach (var row in result)
            {
                _visibleIds.Add(row.id);
            }

            return result;
        }

        /// <summary>只绘制当前可见列的预计算内容，复用原路径和地址提示。</summary>
        protected override void RowGUI(RowGUIArgs args)
        {
            var row = (Row)args.item;
            for (var index = 0; index < args.GetNumVisibleColumns(); index++)
            {
                var rect = args.GetCellRect(index);
                CenterRectUsingSingleLineHeight(ref rect);
                rect.xMin += 6;
                EditorGUI.LabelField(rect, row.Content[args.GetColumn(index)]);
            }
        }

        /// <summary>通知宿主更新详情和持久化 UI 选择。</summary>
        protected override void SelectionChanged(IList<int> selectedIds) => SelectionUpdated?.Invoke();
        /// <summary>将仍存在的双击行交回宿主定位源资产。</summary>
        protected override void DoubleClickedItem(int id)
        {
            if (_byId.TryGetValue(id, out var row))
            {
                Locate?.Invoke(row);
            }
        }
        /// <summary>右键未选中行时先更新选择，再由宿主提供上下文动作。</summary>
        protected override void ContextClickedItem(int id)
        {
            if (!IsSelected(id))
            {
                SetSelection(new[] { id });
            }

            if (_byId.TryGetValue(id, out var row))
            {
                Context?.Invoke(row);
            }
        }
    }
}
