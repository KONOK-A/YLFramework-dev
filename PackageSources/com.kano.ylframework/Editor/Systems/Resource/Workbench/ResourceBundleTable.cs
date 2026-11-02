using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace YLFramework.Editor.Resource
{
    /// <summary>以分包预估为基础展示来源与依赖，有实际构建时补充其 Manifest 和文件测量。</summary>
    internal sealed class ResourceBundleTable : TreeView
    {
        /// <summary>关联预估包、可选实际构建包及预计算展示和依赖关系。</summary>
        internal sealed class Row : TreeViewItem
        {
            internal ResourceBuildBundle Bundle;
            internal ResourceBuiltBundle Built;
            internal string Groups;
            internal string[] Paths;
            internal string[] Dependencies;
            internal string[] Dependents;
            internal string[] Cells;
            internal string Search;
        }

        private Row[] _rows = Array.Empty<Row>();
        private readonly Dictionary<int, Row> _byId = new();
        internal Row Selected => GetSelection().Count == 1 && _byId.TryGetValue(GetSelection()[0], out var value) ? value : null;
        internal ResourceBundlePlacement? Placement;
        internal bool SharedOnly;
        internal Action SelectionUpdated;

        /// <summary>创建分包列表和多列排序入口。</summary>
        internal ResourceBundleTable() : base(new TreeViewState(), CreateHeader())
        {
            rowHeight = ResourceWorkbenchGui.RowHeight;
            showBorder = true;
            showAlternatingRowBackgrounds = false;
            multiColumnHeader.height = ResourceWorkbenchGui.BarHeight;
            multiColumnHeader.sortingChanged += _ => Reload();
            Reload();
        }

        /// <summary>定义源、构建和交付文件量各自独立的统计列。</summary>
        private static MultiColumnHeader CreateHeader()
        {
            var names = new[] { "用途", "分包方式", "源文件量", "源文件", "依赖", "来源分组", "构建文件量", "交付文件量" };
            var help = new[] { "资源包的用途及包名末尾；完整包名可在详情复制。", "最终 Bundle 的分包方式，不一定与组的打包设置相同。", "源文件的逻辑字节之和，不是内存用量。", "参与此包构建的源文件数量。", "此包直接依赖的其他包数量。", "引入此包内容的分组及其打包方式。", "实际构建文件字节；预估阶段不可用。", "编码后的交付文件字节；预估阶段不可用。" };
            var widths = new[] { 150f, 105, 90, 65, 55, 130, 90, 100 };
            return new MultiColumnHeader(new MultiColumnHeaderState(names.Select((name, i) => new MultiColumnHeaderState.Column
            {
                headerContent = new GUIContent(name, help[i]),
                width = widths[i],
                minWidth = 55,
                autoResize = i == 0,
                canSort = true,
                allowToggleVisibility = i != 0
            }).ToArray())
            {
                visibleColumns = new[] { 0, 5, 1, 3, 4 }
            });
        }

        /// <summary>传播来源组到依赖包并投影行内容，实际构建存在时优先展示其依赖和测量。</summary>
        internal void SetData(ResourceBuildMap map, ResourceBuildResult build)
        {
            var built = build?.Bundles.ToDictionary(x => x.Name, StringComparer.Ordinal);
            var delivery = build?.DeliveryFiles.ToDictionary(x => x.Name, StringComparer.Ordinal);
            var groups = map.Bundles.ToDictionary(x => x.Name, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (var asset in map.Assets)
            {
                if (asset.Source != null)
                {
                    groups[asset.BundleName].Add(asset.Source.GroupId + " / " + (asset.Source.Packing == ResourcePacking.Together ? "按组合包" : "按文件分包"));
                }
            }

            var bundles = map.Bundles.ToDictionary(x => x.Name, StringComparer.Ordinal);
            var incoming = map.Bundles.ToDictionary(x => x.Name, x => x.Dependents.Length, StringComparer.Ordinal);
            var pending = new Queue<string>(incoming.Where(x => x.Value == 0).Select(x => x.Key));
            while (pending.Count > 0)
            {
                var name = pending.Dequeue();
                foreach (var dependency in bundles[name].Dependencies)
                {
                    groups[dependency].UnionWith(groups[name]);
                    if (--incoming[dependency] == 0)
                    {
                        pending.Enqueue(dependency);
                    }
                }
            }
            _rows = map.Bundles.Select((bundle, index) =>
            {
                var actual = built != null && built.TryGetValue(bundle.Name, out var b) ? b : null;
                var dependencies = actual?.Dependencies.ToArray() ?? bundle.Dependencies;
                var associated = string.Join(", ", groups[bundle.Name]);
                var kind = bundle.IsScene ? "场景" : bundle.IsShared ? "共享依赖" : "主资源";
                var suffix = bundle.Name.Substring(Math.Max(0, bundle.Name.Length - 12));
                var cells = new[] { kind + " · " + suffix, PlacementName(bundle.Placement), bundle.SourceBytes.ToString(), bundle.AssetPaths.Length.ToString(),
                    dependencies.Length.ToString(), associated, actual?.FileBytes.ToString() ?? "未计算",
                    delivery != null && delivery.TryGetValue(bundle.Name, out var file) ? file.Length.ToString() : "未计算" };
                return new Row
                {
                    id = index + 1,
                    depth = 0,
                    displayName = cells[0],
                    Bundle = bundle,
                    Built = actual,
                    Groups = associated,
                    Paths = bundle.AssetPaths,
                    Dependencies = dependencies,
                    Dependents = actual?.Dependents.ToArray() ?? bundle.Dependents,
                    Cells = cells,
                    Search = string.Join("\n", cells) + "\n" + bundle.Name + "\n" + string.Join("\n", bundle.AssetPaths)
                };
            }).ToArray();
            _byId.Clear();
            foreach (var row in _rows)
            {
                _byId.Add(row.id, row);
            }

            SetSelection(Array.Empty<int>());
            Reload();
        }

        /// <summary>按完整包名在当前快照查找行，供依赖树复用。</summary>
        internal Row Find(string name) => _rows.FirstOrDefault(x => x.Bundle.Name == name);
        /// <summary>将最终分包原因映射为展示名称，不等同于原组的打包意图。</summary>
        internal static string PlacementName(ResourceBundlePlacement value) => value switch
        {
            ResourceBundlePlacement.Group => "按组合包",
            ResourceBundlePlacement.PerAsset => "按文件",
            ResourceBundlePlacement.SharedDependencies => "共享依赖",
            ResourceBundlePlacement.SceneDependencies => "场景依赖",
            ResourceBundlePlacement.CycleMerge => "循环归并",
            _ => value.ToString()
        };
        /// <summary>清除过滤后定位指定完整包名，使依赖树跳转目标可见。</summary>
        internal void SelectBundle(string name)
        {
            var row = Find(name);
            if (row == null)
            {
                return;
            }

            searchString = "";
            Placement = null;
            SharedOnly = false;
            Reload();
            SetSelection(new[] { row.id }, TreeViewSelectionOptions.RevealAndFrame);
        }

        /// <summary>为当前分包快照建立平铺树根。</summary>
        protected override TreeViewItem BuildRoot()
        {
            var root = new TreeViewItem(0, -1, "资源包");
            foreach (var row in _rows)
            {
                root.AddChild(row);
            }

            root.children ??= new List<TreeViewItem>();
            return root;
        }

        /// <summary>组合分包方式、共享包和文本过滤，按原始数值或文本生成排序行。</summary>
        protected override IList<TreeViewItem> BuildRows(TreeViewItem root)
        {
            var filtered = _rows.Where(x => (!Placement.HasValue || x.Bundle.Placement == Placement) && (!SharedOnly || x.Bundle.IsShared) &&
                (string.IsNullOrEmpty(searchString) || x.Search.IndexOf(searchString, StringComparison.OrdinalIgnoreCase) >= 0));
            var column = multiColumnHeader.sortedColumnIndex;
            if (column >= 0)
            {
                if (column == 2 || column == 3 || column == 4 || column == 6 || column == 7)
                {
                    // 未构建的量保持未知，只用 -1 作为排序键，不写回为真实字节值。
                    long Number(Row row) => long.TryParse(row.Cells[column], out var value) ? value : -1;
                    filtered = multiColumnHeader.IsSortedAscending(column) ? filtered.OrderBy(Number).ThenBy(x => x.id) : filtered.OrderByDescending(Number).ThenBy(x => x.id);
                }
                else
                {
                    filtered = multiColumnHeader.IsSortedAscending(column) ? filtered.OrderBy(x => x.Cells[column], StringComparer.Ordinal).ThenBy(x => x.id) :
                    filtered.OrderByDescending(x => x.Cells[column], StringComparer.Ordinal).ThenBy(x => x.id);
                }
            }
            return filtered.Cast<TreeViewItem>().ToList();
        }

        /// <summary>将字节列格式化显示，并在提示中保留完整包名、来源组和原始值。</summary>
        protected override void RowGUI(RowGUIArgs args)
        {
            var row = (Row)args.item;
            for (var i = 0; i < args.GetNumVisibleColumns(); i++)
            {
                var rect = args.GetCellRect(i);
                CenterRectUsingSingleLineHeight(ref rect);
                rect.xMin += 5;
                var column = args.GetColumn(i);
                var text = row.Cells[column];
                if ((column == 2 || column == 6 || column == 7) && long.TryParse(text, out var bytes))
                {
                    text = ResourceWorkbenchGui.Bytes(bytes);
                }

                EditorGUI.LabelField(rect, new GUIContent(text, "完整包名：" + row.Bundle.Name + "\n来源分组：" + row.Groups + "\n本列原值：" + row.Cells[column]));
            }
        }
        /// <summary>通知宿主更新当前包的详情上下文。</summary>
        protected override void SelectionChanged(IList<int> selectedIds) => SelectionUpdated?.Invoke();
    }
}
