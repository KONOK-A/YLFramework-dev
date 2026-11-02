using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace YLFramework.Editor.Resource
{
    /// <summary>区分当前配置预估与真实构建报告的分包分析页，只展示原快照保留的来源和测量。</summary>
    internal sealed class ResourceAnalysisView
    {
        private ResourceBundleTable _table;
        private ResourceBundleDependencyTree _tree;
        private ResourceBuildMap _displayed;
        private ResourceBuildResult _displayedBuild;
        private ResourceBundleTable.Row _selected;
        private Dictionary<string, ResourceBuildAsset> _mainAssets;
        private Dictionary<string, ResourceBuiltFile> _raw;
        private Dictionary<string, ResourceBuiltFile> _delivery;
        private Dictionary<string, ResourceBundleChange> _changes;
        private int _source;
        private int _detail;
        private int _placement;
        private bool _showCompactDetail;
        private bool _sharedOnly;
        private bool _showIdentity;
        private bool _showErrorDetails;
        private string _search = "";
        private string _selectedPath;
        private bool _isDisplayedStale;
        private Vector2 _detailScroll;
        private Vector2 _fileScroll;
        internal Exception Error;
        internal ResourceAtlasPlan AtlasPending;
        /// <summary>选择实际构建来源并请求下次绘制重新绑定该报告。</summary>
        internal void UseExact()
        {
            _source = 1;
            _displayed = null;
        }
        /// <summary>清除分析来源、表格选择和错误上下文，准备切换项目。</summary>
        internal void Reset()
        {
            _displayed = null;
            _displayedBuild = null;
            _table = null;
            _tree = null;
            _selected = null;
            _source = _detail = _placement = 0;
            _search = "";
            Error = null;
            AtlasPending = null;
        }

        /// <summary>按选定来源展示分包快照及其独立过期状态，计算动作仅通过显式回调触发。</summary>
        internal void Draw(ResourceProject project, ResourceBuildMap preview, ResourceEditorBuildReport report, bool reportStale, bool previewStale,
            float width, Action refresh, Action<ResourceBuildSource> locate)
        {
            EditorGUILayout.BeginHorizontal(ResourceWorkbenchGui.Toolbar);
            var next = ResourceWorkbenchGui.Navigation(_source, new[] { "当前配置预估", "这次构建结果" }, false, GUILayout.Width(230));
            if (next != _source)
            {
                _source = next;
                _displayed = null;
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("计算当前预估", ResourceWorkbenchGui.ToolbarButton, GUILayout.Width(124)))
            {
                _source = 0;
                refresh();
            }
            EditorGUILayout.EndHorizontal();
            var build = _source == 1 ? report?.Build : null;
            var stale = _source == 1 ? reportStale : previewStale;
            _isDisplayedStale = stale;
            var map = _source == 1 ? build?.Map : preview;
            if (_source == 0 && AtlasPending != null)
            {
                EditorGUILayout.HelpBox("图集需要生成或更新，当前还不能计算最终分包。请到“资源配置 > 图集”完成准备；旧结果不代表新规则。", MessageType.Warning);
            }

            if (Error != null && _source == 0)
            {
                EditorGUILayout.HelpBox("分包预估未完成。请查看错误详情并修正问题后重新计算；下方仍保留上次结果。", MessageType.Error);
                _showErrorDetails = ResourceWorkbenchGui.Disclosure(_showErrorDetails, "分包计算错误详情");
                if (_showErrorDetails)
                {
                    GUILayout.Label(ResourceEditorText.FailureDetails(Error), ResourceWorkbenchGui.Secondary);
                }
            }
            if (map == null)
            {
                GUILayout.Space(16);
                GUILayout.Label(_source == 0 ? "尚无分包预估" : "这条执行记录没有构建结果", ResourceWorkbenchGui.Title);
                GUILayout.Label(_source == 0 ? "点击“计算当前预估”，查看资源如何分包以及包之间的依赖；这一步不会构建或发布。" : "先在构建页执行，或从底部“输出”的执行记录中选择已有构建。临时构建报告不会跨重载恢复。", ResourceWorkbenchGui.Secondary);
                GUILayout.FlexibleSpace();
                return;
            }
            if (_table == null || !ReferenceEquals(_displayed, map) || !ReferenceEquals(_displayedBuild, build))
            {
                Bind(map, build);
            }

            GUILayout.Label((_source == 0 ? "预估 / " + (map.ContainsUnsavedDefinitions ? "含未保存配置" : "计算时配置已保存") : "构建结果 / " + report.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")) +
                " / " + map.ProjectId + " / " + (build?.Platform ?? map.Platform), EditorStyles.boldLabel);
            if (stale)
            {
                EditorGUILayout.HelpBox("这是较早的结果，配置或资源可能已变化。来源仍保留，但不代表当前输入；请重新计算或构建。", MessageType.Warning);
            }

            GUILayout.Label("资源包（Bundle） " + map.Bundles.Count + " / 主资源 " + map.Assets.Count + " / 分组 " + map.Groups.Count +
                " / 循环归并 " + map.MergedBundleCount + " / 估计减少的重复源文件量 " + ResourceWorkbenchGui.Bytes(map.AvoidedDuplicateSourceBytes), ResourceWorkbenchGui.Secondary);
            EditorGUILayout.BeginHorizontal(ResourceWorkbenchGui.Toolbar);
            GUI.SetNextControlName("Resource.BundleSearch");
            var text = ResourceWorkbenchGui.SearchField(_search, "筛选分包：名称、源路径、分包方式", GUILayout.MinWidth(90));
            if (text != _search)
            {
                _search = text;
                _table.searchString = _search;
            }
            var names = new[] { "全部分包方式", "按组", "按文件", "共享依赖", "场景依赖", "循环归并" };
            var filter = EditorGUILayout.Popup(_placement, names, ResourceWorkbenchGui.Popup, GUILayout.Width(width < 800 ? 120 : 160), GUILayout.Height(ResourceWorkbenchGui.FieldHeight));
            var onlyShared = ResourceWorkbenchGui.ToolbarToggle(_sharedOnly, "仅共享", 74);
            if (filter != _placement || _sharedOnly != onlyShared)
            {
                _placement = filter;
                _sharedOnly = onlyShared;
                _table.Placement = _placement == 0 ? null : (ResourceBundlePlacement?)(_placement - 1);
                _table.SharedOnly = _sharedOnly;
                _table.Reload();
            }
            if (width < 900 && GUILayout.Button(_showCompactDetail ? "返回列表" : "所选详情", EditorStyles.toolbarButton, GUILayout.Width(74)))
            {
                _showCompactDetail = !_showCompactDetail;
            }

            EditorGUILayout.EndHorizontal();
            var wide = width >= 900;
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            if (wide || !_showCompactDetail)
            {
                _table.OnGUI(GUILayoutUtility.GetRect(0, 100000, 100, 100000, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)));
            }

            if (!ReferenceEquals(_selected, _table.Selected))
            {
                _selected = _table.Selected;
                _detailScroll = _fileScroll = Vector2.zero;
                _selectedPath = _selected?.Paths.FirstOrDefault();
                _tree = null;
            }
            if (wide || _showCompactDetail)
            {
                var detailWidth = wide ? width * .44f : width;
                EditorGUILayout.BeginVertical(GUILayout.Width(detailWidth), GUILayout.ExpandHeight(true));
                DrawDetails(project, detailWidth, locate);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>切换显示快照并缓存来源与制品查找表，避免重绘时反复投影同份数据。</summary>
        private void Bind(ResourceBuildMap map, ResourceBuildResult build)
        {
            _table ??= new ResourceBundleTable();
            _table.SetData(map, build);
            _table.searchString = _search;
            _table.Placement = _placement == 0 ? null : (ResourceBundlePlacement?)(_placement - 1);
            _table.SharedOnly = _sharedOnly;
            _table.Reload();
            _displayed = map;
            _displayedBuild = build;
            _mainAssets = map.Assets.ToDictionary(x => x.SourcePath, StringComparer.Ordinal);
            _raw = build?.Files.ToDictionary(x => x.Name, StringComparer.Ordinal);
            _delivery = build?.DeliveryFiles.ToDictionary(x => x.Name, StringComparer.Ordinal);
            _changes = build?.BundleChanges.ToDictionary(x => x.Name, StringComparer.Ordinal);
            _selected = null;
            _tree = null;
        }

        /// <summary>展示所选包的源文件、包级依赖或构建值，不推断文件级引入链。</summary>
        private void DrawDetails(ResourceProject project, float width, Action<ResourceBuildSource> locate)
        {
            if (_selected == null)
            {
                GUILayout.Label("选择一个资源包，查看来源与依赖。", ResourceWorkbenchGui.Secondary);
                return;
            }
            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);
            GUILayout.Label(ResourceBundleTable.PlacementName(_selected.Bundle.Placement) + (_selected.Bundle.IsShared ? " · 共享" : _selected.Bundle.IsScene ? " · 场景" : " · 主资源"), EditorStyles.boldLabel);
            GUILayout.Label("来源分组：" + _selected.Groups, EditorStyles.wordWrappedLabel);
            var next = ResourceWorkbenchGui.Navigation(_detail, new[] { "源文件", "依赖", "被依赖", "构建值" });
            if (next != _detail)
            {
                _detail = next;
                _tree = null;
            }
            if (_detail == 0)
            {
                DrawFiles(width, locate);
            }
            else if (_detail == 1 || _detail == 2)
            {
                GUILayout.Label("这里展示包之间的依赖，不代表某个文件的精确引入链。双击节点可定位相应包。", ResourceWorkbenchGui.Secondary);
                _tree ??= new ResourceBundleDependencyTree(_table, _selected.Bundle.Name, _detail == 2, name =>
                {
                    _table.SelectBundle(name);
                    _search = "";
                    _placement = 0;
                    _sharedOnly = false;
                });
                var rect = GUILayoutUtility.GetRect(50, 260, GUILayout.ExpandWidth(true));
                _tree.OnGUI(rect);
            }
            else
            {
                DrawBuildValues(width);
            }

            _showIdentity = ResourceWorkbenchGui.Disclosure(_showIdentity, "技术详情：完整包名与来源");
            if (_showIdentity)
            {
                ResourceWorkbenchGui.CopyLine("完整包名（BundleName）", _selected.Bundle.Name, width - 20);
                ResourceWorkbenchGui.Metric("分包方式原值", _selected.Bundle.Placement.ToString());
                if (_selectedPath != null && _mainAssets.TryGetValue(_selectedPath, out var main))
                {
                    ResourceWorkbenchGui.CopyLine("源文件 GUID", main.Guid, width - 20);
                    if (main.Source != null)
                    {
                        ResourceWorkbenchGui.CopyLine("原登记路径", main.Source.SelectionPath, width - 20);
                    }
                }
                else if (_selectedPath != null)
                {
                    GUILayout.Label("原结果没有记录此依赖文件的独立 GUID，不能仅凭当前路径确认它仍是同一文件。", ResourceWorkbenchGui.Secondary);
                }

                if (_changes != null && _changes.TryGetValue(_selected.Bundle.Name, out var change))
                {
                    GUILayout.Label("内容对比原因（原始记录）：\n" + change.Reason, ResourceWorkbenchGui.Secondary);
                }

                GUILayout.Label("源文件字节、构建文件、交付文件和发布复制量各有不同含义，不能据此推算原生内存。", ResourceWorkbenchGui.Secondary);
            }
            EditorGUILayout.EndScrollView();
        }

        /// <summary>区分可加载主资源与依赖文件，按保留的 GUID 或过期状态限制旧路径定位。</summary>
        private void DrawFiles(float width, Action<ResourceBuildSource> locate)
        {
            GUILayout.Label("主资源有直接加载地址；依赖文件随包参与构建，没有独立加载地址。", ResourceWorkbenchGui.Secondary);
            var rowHeight = ResourceWorkbenchGui.RowHeight;
            var rect = GUILayoutUtility.GetRect(80, Mathf.Clamp(_selected.Paths.Length * rowHeight, rowHeight, rowHeight * 4), GUILayout.ExpandWidth(true));
            var scrollable = _selected.Paths.Length > 4;
            if (scrollable)
            {
                _fileScroll = GUI.BeginScrollView(rect, _fileScroll, new Rect(0, 0, Math.Max(20, rect.width - 18), _selected.Paths.Length * rowHeight));
            }

            var first = scrollable ? Math.Max(0, (int)(_fileScroll.y / rowHeight)) : 0;
            for (var i = first; i < Math.Min(_selected.Paths.Length, first + 5); i++)
            {
                var path = _selected.Paths[i];
                var main = _mainAssets.TryGetValue(path, out var asset);
                var label = (main ? "主资源  " : "依赖文件  ") + System.IO.Path.GetFileName(path);
                if (GUI.Toggle(new Rect((scrollable ? 0 : rect.x) + 2, (scrollable ? 0 : rect.y) + i * rowHeight, Math.Max(20, rect.width - 24), rowHeight), _selectedPath == path,
                    new GUIContent(label, path + (main ? "\n" + asset.Address : "\n无直接加载地址")), EditorStyles.miniButton))
                {
                    _selectedPath = path;
                }
            }
            if (scrollable)
            {
                GUI.EndScrollView();
            }

            if (_selectedPath == null)
            {
                return;
            }

            ResourceWorkbenchGui.CopyLine("源文件路径", _selectedPath, width - 20);
            var isMain = _mainAssets.TryGetValue(_selectedPath, out var currentAsset);
            var currentIdentity = isMain ? AssetDatabase.GUIDToAssetPath(currentAsset.Guid) == _selectedPath : !_isDisplayedStale;
            using (new EditorGUI.DisabledScope(!currentIdentity))
            {
                if (GUILayout.Button("定位源资产", ResourceWorkbenchGui.Button))
                {
                    EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(_selectedPath));
                }
            }

            if (!currentIdentity)
            {
                GUILayout.Label("源文件或当前配置已变化，不能再按旧路径定位。请重新计算或构建。", ResourceWorkbenchGui.Secondary);
            }

            if (!_mainAssets.TryGetValue(_selectedPath, out var selectedAsset))
            {
                GUILayout.Label("这是随包构建的依赖文件；报告未记录它由哪个具体文件引入。", ResourceWorkbenchGui.Secondary);
                return;
            }
            ResourceWorkbenchGui.CopyLine("可加载主地址", selectedAsset.Address.ToString(), width - 20);
            var origin = selectedAsset.Source;
            if (origin == null)
            {
                GUILayout.Label("此报告没有保留来源配置，无法定位；不会用当前项目配置替代原记录。", ResourceWorkbenchGui.Secondary);
                return;
            }
            GUILayout.Label("计算时来源：" + origin.GroupId + " / " + (origin.Packing == ResourcePacking.Together ? "按组合包" : "按文件分包") + (origin.AtlasRuleId == null ? " / 资源选择项" : " / 自动图集规则 " + origin.AtlasRuleId), EditorStyles.boldLabel);
            ResourceWorkbenchGui.Metric("登记来源", System.IO.Path.GetFileName(origin.SelectionPath), origin.SelectionPath);
            if (origin.Aliases.Count > 0)
            {
                GUILayout.Label("地址别名：" + string.Join(", ", origin.Aliases), EditorStyles.wordWrappedLabel);
            }

            if (origin.Extensions.Count > 0)
            {
                GUILayout.Label("扩展名筛选：" + string.Join(", ", origin.Extensions), EditorStyles.wordWrappedLabel);
            }

            if (GUILayout.Button("定位资源配置中的来源", ResourceWorkbenchGui.Button))
            {
                locate(origin);
            }
        }

        /// <summary>仅在真实构建存在时展示原始与交付文件量及摘要，不由字节量推测原生内存。</summary>
        private void DrawBuildValues(float width)
        {
            if (_displayedBuild == null)
            {
                GUILayout.Label("预估只提供源文件量。构建文件量、交付文件量和 SHA256 需实际构建后查看；原生内存未测量。", ResourceWorkbenchGui.Secondary);
                return;
            }
            var name = _selected.Bundle.Name;
            _raw.TryGetValue(name, out var file);
            _delivery.TryGetValue(name, out var encoded);
            _changes.TryGetValue(name, out var change);
            ResourceWorkbenchGui.Metric("源文件量", ResourceWorkbenchGui.Bytes(_selected.Bundle.SourceBytes), _selected.Bundle.SourceBytes + " B");
            ResourceWorkbenchGui.Metric("构建文件量（raw）", ResourceWorkbenchGui.Bytes(file?.Length), file?.Length + " B");
            ResourceWorkbenchGui.Metric("交付文件量", ResourceWorkbenchGui.Bytes(encoded?.Length), encoded?.Length + " B");
            ResourceWorkbenchGui.Metric("内容是否变化", change?.ContentChanged.HasValue == true ? change.ContentChanged.Value ? "已变化" : "未变化" : "未知（无可比基线）");
            ResourceWorkbenchGui.Metric("原生内存", "未测量");
            ResourceWorkbenchGui.CopyLine("raw SHA256", file?.Sha256 ?? "不可用", width - 20);
            ResourceWorkbenchGui.CopyLine("delivery SHA256", encoded?.Sha256 ?? "不可用", width - 20);
            ResourceWorkbenchGui.CopyLine("InputFingerprint", _displayedBuild.InputFingerprint, width - 20);
            GUILayout.Label("原始构建文件总字节（含 Manifest 配套文件）=" + _displayedBuild.Files.Sum(x => x.Length) + " / Bundle 原始文件字节=" + _displayedBuild.BundleFileBytes +
                " / 交付文件总字节=" + _displayedBuild.DeliveryBytes + "。发布复制量由对应的发布报告提供，不能将 Builder.CopiedBytes 视为实测值。", ResourceWorkbenchGui.Secondary);
        }
    }

    /// <summary>按展开路径展示包级正向或反向依赖，用路径身份保持节点状态并限制单次展开量。</summary>
    internal sealed class ResourceBundleDependencyTree : TreeView
    {
        private readonly ResourceBundleTable _table;
        private readonly string _bundle;
        private readonly bool _reverse;
        private readonly Action<string> _locate;
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _names = new();
        /// <summary>绑定当前分包表、起始包和依赖方向，默认展开起始节点。</summary>
        internal ResourceBundleDependencyTree(ResourceBundleTable table, string bundle, bool reverse, Action<string> locate) : base(new TreeViewState())
        {
            this._table = table;
            this._bundle = bundle;
            this._reverse = reverse;
            this._locate = locate;
            rowHeight = 24;
            showBorder = true;
            Reload();
            SetExpanded(1, true);
        }
        /// <summary>迭代生成已展开的依赖路径，标记重复引用；单次最多处理 16384 个待访节点。</summary>
        protected override TreeViewItem BuildRoot()
        {
            var root = new TreeViewItem(0, -1, "包依赖");
            _names.Clear();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<(TreeViewItem Parent, string Name, string Key)>();
            pending.Push((root, _bundle, _bundle));
            var count = 0;
            while (pending.Count > 0 && count++ < 16384)
            {
                var item = pending.Pop();
                if (!_ids.TryGetValue(item.Key, out var id))
                {
                    _ids.Add(item.Key, id = _ids.Count + 1);
                }

                var row = _table.Find(item.Name);
                if (row == null)
                {
                    continue;
                }

                _names[id] = item.Name;
                var repeated = !visited.Add(item.Name);
                var node = new TreeViewItem(id, item.Parent.depth + 1, row.displayName + (repeated ? "（重复引用）" : ""));
                item.Parent.AddChild(node);
                var edges = _reverse ? row.Dependents : row.Dependencies;
                if (edges.Length == 0)
                {
                    continue;
                }

                if (!IsExpanded(id))
                {
                    node.children = CreateChildListForCollapsedParent();
                    continue;
                }
                for (var i = edges.Length - 1; i >= 0; i--)
                {
                    pending.Push((node, edges[i], item.Key + "/" + edges[i]));
                }
            }
            if (pending.Count > 0)
            {
                root.AddChild(new TreeViewItem(int.MaxValue, 0, "本次展开已达16384个节点上限，可双击跳转后继续查看。"));
            }

            root.children ??= new List<TreeViewItem>();
            return root;
        }
        /// <summary>按新的展开状态重建可见依赖节点。</summary>
        protected override void ExpandedStateChanged() => Reload();
        /// <summary>将有效节点的完整包名交回分析页定位。</summary>
        protected override void DoubleClickedItem(int id)
        {
            if (_names.TryGetValue(id, out var name))
            {
                _locate(name);
            }
        }
    }
}
