using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace YLFramework.Editor.Resource
{
    /// <summary>展示调用方已读取的发布版本快照；索引当前项不表示应用正在使用的版本。</summary>
    internal sealed class ResourceReleaseTable : TreeView
    {
        /// <summary>保存精确发布身份及本次索引比较结果的显示行。</summary>
        internal sealed class Row : TreeViewItem
        {
            internal ResourceRelease Release;
            internal ResourceReleaseIdentity Identity;
            internal bool IsCurrent;
            internal string[] Cells;
            internal string Search;
        }
        private Row[] _rows = Array.Empty<Row>();
        private readonly Dictionary<int, Row> _ids = new();
        internal Row Selected => GetSelection().Count == 1 && _ids.TryGetValue(GetSelection()[0], out var row) ? row : null;
        /// <summary>建立版本表列与排序刷新行为。</summary>
        internal ResourceReleaseTable() : base(new TreeViewState(), Header())
        {
            rowHeight = ResourceWorkbenchGui.RowHeight;
            showBorder = true;
            showAlternatingRowBackgrounds = false;
            multiColumnHeader.height = ResourceWorkbenchGui.BarHeight;
            multiColumnHeader.sortingChanged += _ => Reload();
            Reload();
        }
        /// <summary>定义版本身份、索引关系和交付统计列，默认只展开常用列。</summary>
        private static MultiColumnHeader Header()
        {
            var names = new[] { "版本摘要", "资源兼容标识（App）", "发布索引", "Bundle数", "交付逻辑字节", "Unity" };
            var help = new[] { "CatalogDigest：此发布版本的内容摘要，完整值可在详情中复制。", "AppContentCompatibilityId：构建时使用的资源兼容标识，不是游戏版本号。", "与本次读取的发布索引比较；不代表应用正在使用的版本。", "此版本包含的 Bundle 数量。", "Bundle 交付文件的逻辑字节之和，不含支持文件，也不是实际磁盘占用。", "此版本构建时的 Unity 兼容标识。" };
            var widths = new[] { 180f, 160, 110, 75, 130, 130 };
            return new MultiColumnHeader(new MultiColumnHeaderState(names.Select((name, i) => new MultiColumnHeaderState.Column
            {
                headerContent = new GUIContent(name, help[i]),
                width = widths[i],
                minWidth = 65,
                autoResize = i == 0,
                canSort = true,
                allowToggleVisibility = i != 0
            }).ToArray())
            {
                visibleColumns = new[] { 0, 1, 2 }
            });
        }
        /// <summary>替换版本快照并按完整摘要保留选择；未知或过期索引关系由调用方明确传入。</summary>
        internal void SetData(IReadOnlyList<ResourceRelease> releases, string currentDigest)
        {
            var old = Selected?.Identity.CatalogDigest;
            _rows = releases.Select((release, i) =>
            {
                var catalog = release.Catalog;
                var current = catalog.CatalogDigest == currentDigest;
                var cells = new[] { catalog.CatalogDigest.Substring(0, 16) + "...", catalog.AppContentCompatibilityId,
                    current ? "索引当前版本" : currentDigest == null ? "未读取 / 已过期" : "非索引当前版本", catalog.Bundles.Count.ToString(),
                    catalog.Bundles.Sum(x => x.Length).ToString(), catalog.UnityCompatibility };
                return new Row
                {
                    id = i + 1,
                    depth = 0,
                    displayName = cells[0],
                    Release = release,
                    Identity = new ResourceReleaseIdentity(release),
                    IsCurrent = current,
                    Cells = cells,
                    Search = catalog.CatalogDigest + "\n" + string.Join("\n", cells)
                };
            }).ToArray();
            _ids.Clear();
            foreach (var row in _rows)
            {
                _ids.Add(row.id, row);
            }

            var retained = _rows.FirstOrDefault(x => x.Identity.CatalogDigest == old);
            SetSelection(retained == null ? Array.Empty<int>() : new[] { retained.id });
            Reload();
        }
        /// <summary>按完整版本摘要选择并滚动定位，不按截断显示值匹配。</summary>
        internal void Select(string digest)
        {
            var row = _rows.FirstOrDefault(x => x.Identity.CatalogDigest == digest);
            if (row != null)
            {
                SetSelection(new[] { row.id }, TreeViewSelectionOptions.RevealAndFrame);
            }
        }
        /// <summary>为已登记版本快照建立平铺树根。</summary>
        protected override TreeViewItem BuildRoot()
        {
            var root = new TreeViewItem(0, -1, "已登记版本");
            foreach (var row in _rows)
            {
                root.AddChild(row);
            }

            root.children ??= new List<TreeViewItem>();
            return root;
        }
        /// <summary>筛选版本行并按列排序，Bundle 数与交付字节使用数值而非文本排序。</summary>
        protected override IList<TreeViewItem> BuildRows(TreeViewItem root)
        {
            var filtered = _rows.Where(x => string.IsNullOrEmpty(searchString) || x.Search.IndexOf(searchString, StringComparison.OrdinalIgnoreCase) >= 0);
            var column = multiColumnHeader.sortedColumnIndex;
            if (column >= 0)
            {
                if (column == 3 || column == 4)
                {
                    filtered = multiColumnHeader.IsSortedAscending(column) ? filtered.OrderBy(x => long.Parse(x.Cells[column])).ThenBy(x => x.id) :
                        filtered.OrderByDescending(x => long.Parse(x.Cells[column])).ThenBy(x => x.id);
                }
                else
                {
                    filtered = multiColumnHeader.IsSortedAscending(column) ? filtered.OrderBy(x => x.Cells[column], StringComparer.Ordinal).ThenBy(x => x.id) :
                    filtered.OrderByDescending(x => x.Cells[column], StringComparer.Ordinal).ThenBy(x => x.id);
                }
            }
            return filtered.Cast<TreeViewItem>().ToList();
        }
        /// <summary>绘制可见版本列，并通过提示保留完整技术身份。</summary>
        protected override void RowGUI(RowGUIArgs args)
        {
            var row = (Row)args.item;
            for (var i = 0; i < args.GetNumVisibleColumns(); i++)
            {
                var rect = args.GetCellRect(i);
                CenterRectUsingSingleLineHeight(ref rect);
                rect.xMin += 5;
                EditorGUI.LabelField(rect, new GUIContent(row.Cells[args.GetColumn(i)], "版本技术信息：\n" + row.Identity));
            }
        }
    }
}
