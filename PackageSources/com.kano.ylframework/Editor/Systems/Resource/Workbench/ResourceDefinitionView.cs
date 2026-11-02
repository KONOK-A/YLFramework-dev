using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Editor.Resource
{
    /// <summary>编辑项目的选择定义并展示可失效的解析预览，源资产与发布版本不归此视图管理。</summary>
    internal sealed class ResourceDefinitionView : IDisposable
    {
        private ResourceBuildMap _map;
        private ResourceCatalog _catalog;
        private ResourceEditorSimulation _simulation;
        private int _group;
        private int _mode;
        private string _search = "";
        private bool _focusSearch;
        private bool _showGroupSettings;
        private bool _showHelp;
        private bool _isDirty = true;
        private bool _isPreviewStale;
        private int _renderedMode = -1;
        private int _observedDirty = -1;
        private string _selectGuid;
        private DateTime _previewAt;
        private ResourceEntryTable _table;
        private string _dropHint;
        private float _tableWidth;
        private ResourceProject _renameProject;
        private ResourceGroup _renameGroup;
        private string _renameText;
        private string _renameOriginal;
        private readonly ResourceAtlasView _atlas = new();
        internal bool IsAtlasMode => _mode == 2;
        private readonly ResourceWorkbenchLayout _fallbackLayout = new();
        private Vector2 _groupScroll;
        private Vector2 _detailScroll;
        private bool _hasRestoredUI;
        private string _restoredNotice;

        /// <summary>清除当前项目的预览、选择和待定改名上下文，准备绑定另一项目。</summary>
        internal void Reset()
        {
            _group = 0;
            _mode = 0;
            _search = "";
            _selectGuid = null;
            _map = null;
            _catalog = null;
            _simulation = null;
            _table = null;
            _renderedMode = -1;
            _observedDirty = -1;
            _isDirty = true;
            _isPreviewStale = false;
            _renameProject = null;
            _renameGroup = null;
            _renameText = null;
            _renameOriginal = null;
            _atlas.Reset();
            _hasRestoredUI = false;
            _restoredNotice = null;
        }
        /// <summary>标记列表待刷新和旧解析结果过期，保留旧快照供明确区分。</summary>
        internal void Invalidate()
        {
            _isDirty = true;
            if (_catalog != null)
            {
                _isPreviewStale = true;
            }

            _atlas.Invalidate();
        }
        /// <summary>同时清空搜索输入和表格过滤条件，不改变行选择。</summary>
        internal void ClearSearch()
        {
            _search = "";
            if (_table != null)
            {
                _table.searchString = "";
            }
        }
        /// <summary>只清空 UI 行选择，不移除资源登记。</summary>
        internal void ClearSelection() => _table?.SetSelection(Array.Empty<int>());
        /// <summary>优先清除搜索，搜索为空时再清除选择。</summary>
        internal void Escape()
        {
            if (!string.IsNullOrEmpty(_search))
            {
                ClearSearch();
            }
            else
            {
                ClearSelection();
            }
        }
        /// <summary>必要时离开图集页，并请求下一次重绘聚焦资源搜索框。</summary>
        internal void FocusSearch()
        {
            if (_mode == 2)
            {
                _mode = 0;
                _isDirty = true;
            }
            _focusSearch = true;
        }
        /// <summary>当前分组改名输入是否尚未应用到定义。</summary>
        internal bool HasPendingRename => _renameProject && _renameGroup != null && _renameText != null && _renameText != _renameOriginal;
        /// <summary>复核原分组及名称后通过正式入口应用改名，支持 Undo 但不保存资产。</summary>
        internal bool CommitRename()
        {
            if (!HasPendingRename)
            {
                return false;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("Unity状态转换中，尚未提交Group名称；请稳定后重试。");
            }

            var index = _renameProject.groups.IndexOf(_renameGroup);
            if (index < 0)
            {
                throw new InvalidOperationException("重命名来源已变化，请重新选择Group。");
            }

            if (_renameGroup.id != _renameOriginal)
            {
                throw new InvalidOperationException("Group名称已被外部编辑或Undo修改，请重新选择后编辑。");
            }

            ResourceEditorOperations.RenameGroup(_renameProject, index, _renameText);
            _renameGroup = _renameProject.groups[index];
            _renameText = _renameOriginal = _renameGroup.id;
            _isDirty = true;
            return true;
        }
        /// <summary>释放视图持有的快照和 UI 引用，不销毁借用资产，也不代为提交待定改名。</summary>
        public void Dispose() => Reset();

        /// <summary>接收同次解析的分包、目录和模拟器，切到解析页并更新列表。</summary>
        internal void SetPreview(ResourceBuildMap value, ResourceCatalog index, ResourceEditorSimulation resolver)
        {
            _map = value;
            _catalog = index;
            _simulation = resolver;
            _mode = 1;
            _isPreviewStale = false;
            _previewAt = DateTime.UtcNow;
            _isDirty = true;
            var project = AssetDatabase.LoadAssetAtPath<ResourceProject>(AssetDatabase.GUIDToAssetPath(value.ProjectGuid));
            if (project)
            {
                RefreshRows(project, _fallbackLayout);
            }
        }

        /// <summary>选取未冲突的默认技术标识建立空组，并转到该组的主资源编辑。</summary>
        internal void AddGroup(ResourceProject project)
        {
            var number = project.groups.Count + 1;
            while (project.groups.Any(x => string.Equals(x.id, "group-" + number, StringComparison.OrdinalIgnoreCase)))
            {
                number++;
            }

            _group = ResourceEditorOperations.AddGroup(project, "group-" + number);
            ClearSelection();
            _mode = 0;
            _isDirty = true;
        }

        /// <summary>协调定义、解析和图集三种模式的绘制，按脏计数刷新列表并处理待定改名。</summary>
        internal void Draw(ResourceProject project, EditorWindow window, float availableWidth, Action changed, Action<Func<string>> run, Action<Func<string>> queue = null, Action refresh = null)
        {
            var layout = (window as ResServiceEditorWindow)?.Layout ?? _fallbackLayout;
            if (_group >= project.groups.Count)
            {
                _group = Math.Max(-1, project.groups.Count - 1);
                ClearSelection();
                _isDirty = true;
            }
            if (_observedDirty != EditorUtility.GetDirtyCount(project))
            {
                _observedDirty = EditorUtility.GetDirtyCount(project);
                _isDirty = true;
            }
            RefreshRows(project, layout);
            DrawToolbar(project, layout, availableWidth, changed, run, refresh);
            if (_restoredNotice != null)
            {
                GUILayout.Label(_restoredNotice, ResourceWorkbenchGui.Secondary);
            }

            if (_mode == 0 && project.groups.Count == 0)
            {
                GUILayout.Space(18);
                GUILayout.Label("先创建一个 Group，再添加主资源", ResourceWorkbenchGui.Title);
                GUILayout.Label("Group用于组织资源并设置分包方式，不是磁盘文件夹。同一个Group可以包含不同资源目录中的文件；登记目录本身不会把资源加入构建。", ResourceWorkbenchGui.Secondary);
                GUILayout.Space(8);
                if (ResourceWorkbenchGui.PrimaryAction("创建第一个 Group", GUILayout.Width(190)))
                {
                    run(() =>
                    {
                        AddGroup(project);
                        changed();
                        return "已创建 Group，尚未保存。";
                    });
                }

                if (GUILayout.Button("设置资源目录", ResourceWorkbenchGui.Button, GUILayout.Width(190)))
                {
                    layout.settings = true;
                }

                if (GUILayout.Button("查看完整入门流程", ResourceWorkbenchGui.Button, GUILayout.Width(190)))
                {
                    (window as ResServiceEditorWindow)?.ShowGuide();
                }

                GUILayout.FlexibleSpace();
                return;
            }
            if (_showGroupSettings && _group >= 0 && _mode == 0)
            {
                DrawGroup(project, changed, run);
            }

            if (_mode == 2)
            {
                _atlas.Draw(project, availableWidth, changed, run, queue ?? run);
                return;
            }
            if (_mode == 1)
            {
                GUILayout.Label(_catalog == null ? "尚未计算，请点击‘计算预览’。" : (_isPreviewStale ? "配置已变化，当前显示上次结果。请重新计算预览。" : "最近计算于 " + _previewAt.ToLocalTime().ToString("HH:mm:ss")) + "  此处只解析配置，不会构建资源。", ResourceWorkbenchGui.Secondary);
            }

            if (_showHelp)
            {
                EditorGUILayout.HelpBox("在 Unity Project 窗口选择文件或目录后，使用“添加资源...”菜单中的所选项入口，也可以直接拖入列表。目录会在解析或构建时展开；选中行只是编辑选择，不决定哪些资源参与构建。\nCtrl/Shift 多选；从定义移除不会删除源文件。子资产按主文件登记，不单独选择 Sprite。", MessageType.None);
            }

            DrawBody(availableWidth, project, window, layout, changed, run);
            DrawBatch(project, changed, run, window);
            if (HasPendingRename && (!_showGroupSettings || _mode != 0 || _group != project.groups.IndexOf(_renameGroup) || GUI.GetNameOfFocusedControl() != "Resource.GroupId"))
            {
                run(() =>
                {
                    CommitRename();
                    changed();
                    return "Group名称已应用，可Undo；尚未保存。";
                });
            }

            if (_focusSearch && Event.current.type == EventType.Repaint && GUI.enabled)
            {
                EditorGUI.FocusTextInControl("Resource.EntrySearch");
                _focusSearch = GUI.GetNameOfFocusedControl() != "Resource.EntrySearch";
            }
        }

        /// <summary>组织模式、搜索和资源添加入口，窄窗口通过菜单保留分组选择。</summary>
        private void DrawToolbar(ResourceProject project, ResourceWorkbenchLayout layout, float width, Action changed, Action<Func<string>> run, Action refresh)
        {
            EditorGUILayout.BeginHorizontal(ResourceWorkbenchGui.Toolbar);
            var nextMode = ResourceWorkbenchGui.Navigation(_mode, new[] { "主资源", "解析预览", "图集" }, false, GUILayout.Width(248));
            if (nextMode != _mode)
            {
                _mode = nextMode;
                ClearSelection();
                _isDirty = true;
                _detailScroll = Vector2.zero;
            }
            if (width < 1000 || !layout.showGroups)
            {
                if (GUILayout.Button(_group < 0 ? "全部Group（只读）" : project.groups[_group].id, EditorStyles.toolbarDropDown, GUILayout.MinWidth(90)))
                {
                    GroupMenu(project);
                }
            }
            GUILayout.FlexibleSpace();
            if (width >= 1000)
            {
                layout.showGroups = ResourceWorkbenchGui.ToolbarToggle(layout.showGroups, "显示Group", 90, "只控制左侧分组列表的显示，不改变 Group 或分包方式。");
            }

            if (GUILayout.Button(width >= 1000 ? (layout.showDetails ? "收起详情" : "展开详情") : (layout.compactDetails ? "返回列表" : "查看详情"), ResourceWorkbenchGui.ToolbarButton, GUILayout.Width(70)))
            {
                if (width >= 1000)
                {
                    layout.showDetails = !layout.showDetails;
                }
                else
                {
                    layout.compactDetails = !layout.compactDetails;
                }
            }
            _showHelp = ResourceWorkbenchGui.ToolbarToggle(_showHelp, "帮助", 56);
            EditorGUILayout.EndHorizontal();
            if (_mode == 2)
            {
                return;
            }

            EditorGUILayout.BeginHorizontal(ResourceWorkbenchGui.Toolbar);
            GUI.SetNextControlName("Resource.EntrySearch");
            var next = ResourceWorkbenchGui.SearchField(_search, _mode == 0 ? "筛选主资源：名称、路径、GUID、地址" : "筛选解析结果：路径、地址、别名、GUID", GUILayout.MinWidth(100));
            if (next != _search)
            {
                _search = next;
                _table.searchString = _search;
            }
            if (GUILayout.Button("x", ResourceWorkbenchGui.ToolbarButton, GUILayout.Width(26)))
            {
                ClearSearch();
                GUI.FocusControl(null);
            }
            using (new EditorGUI.DisabledScope(_mode != 0 || _group < 0))
            {
                if (GUILayout.Button(new GUIContent("添加资源...", "打开添加菜单；实际来源为 Unity Project 窗口当前选中的文件或目录，也可拖入列表"), ResourceWorkbenchGui.ToolbarButton, GUILayout.Width(92)))
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("添加 Unity Project 窗口所选文件或目录"), false, () => Add(project, Selection.objects, changed, run));
                    menu.AddDisabledItem(new GUIContent("也可拖入列表；子资产按主文件登记"));
                    menu.ShowAsContext();
                }
                _showGroupSettings = ResourceWorkbenchGui.ToolbarToggle(_showGroupSettings, "组设置", 76);
            }
            if (GUILayout.Button("+ Group", ResourceWorkbenchGui.ToolbarButton, GUILayout.Width(68)))
            {
                run(() =>
                {
                    AddGroup(project);
                    changed();
                    return "已新增空Group，尚未保存。";
                });
            }

            if (_mode == 1 && refresh != null && GUILayout.Button("计算预览", ResourceWorkbenchGui.ToolbarButton, GUILayout.Width(74)))
            {
                refresh();
            }

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>编辑当前组的改名草稿与分包设置，并在用户确认后移除组定义。</summary>
        private void DrawGroup(ResourceProject project, Action changed, Action<Func<string>> run)
        {
            if (_group < 0 || _group >= project.groups.Count)
            {
                return;
            }

            var current = project.groups[_group];
            if (_renameProject != project || _renameGroup != current)
            {
                if (HasPendingRename)
                {
                    run(() =>
                    {
                        CommitRename();
                        changed();
                        return "前一Group名称已应用，尚未保存。";
                    });
                }

                if (HasPendingRename)
                {
                    EditorGUILayout.HelpBox("前一Group的名称尚未应用，请返回修正后再编辑。", MessageType.Warning);
                    return;
                }
                _renameProject = project;
                _renameGroup = current;
                _renameText = _renameOriginal = current.id;
            }
            // 已提交文本不是新输入；外部Undo/Inspector改名应成为新的显示基线，不能被旧文本再次提交。
            else if (!HasPendingRename && current.id != _renameOriginal)
            {
                _renameText = _renameOriginal = current.id;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUI.SetNextControlName("Resource.GroupId");
            var enter = Event.current.rawType == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            _renameText = ResourceWorkbenchGui.TextField("Group 名称（ID）", _renameText, false, "Enter、离开字段或Ctrl+S时应用；只修改定义，可Undo。Group是选择和分包意图，不是磁盘目录。");
            if (enter && HasPendingRename)
            {
                run(() =>
                {
                    CommitRename();
                    changed();
                    return "Group名称已应用，尚未保存。";
                });
            }

            if (GUILayout.Button("删除组", ResourceWorkbenchGui.Button, GUILayout.Width(64)) && EditorUtility.DisplayDialog("删除 Group 定义？",
                "Group: " + current.id + "\n显式选择：" + current.selections.Count + "\n仅删除定义及下次目录成员，不删除源资产、已发布 Bundle 或历史版本。可 Undo。", "删除定义", "取消"))
            {
                run(() =>
                {
                    ResourceEditorOperations.RemoveGroup(project, _group);
                    _renameProject = null;
                    _renameGroup = null;
                    _renameText = null;
                    _renameOriginal = null;
                    _showGroupSettings = false;
                    ClearSelection();
                    _isDirty = true;
                    changed();
                    return "Group已移除，源资产未删除。";
                });
            }

            EditorGUILayout.EndHorizontal();
            if (_group >= project.groups.Count)
            {
                return;
            }

            using var serialized = new SerializedObject(project);
            ResourceWorkbenchGui.Property(serialized.FindProperty("groups").GetArrayElementAtIndex(_group).FindPropertyRelative("packing"),
                new GUIContent("分包方式", "Together：组内主资产合包；PerAsset：按文件 GUID 分包。共享依赖和 Scene 会单独处理。"));
            if (serialized.ApplyModifiedProperties())
            {
                changed();
            }
        }

        /// <summary>提供具体分组及全部分组只读聚合的切换菜单。</summary>
        private void GroupMenu(ResourceProject project)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("全部Group（只读）"), _group < 0, () => ChooseGroup(-1));
            for (var index = 0; index < project.groups.Count; index++)
            {
                var captured = index;
                menu.AddItem(new GUIContent(project.groups[index].id), _group == index, () => ChooseGroup(captured));
            }
            menu.ShowAsContext();
        }

        /// <summary>切换分组上下文并重置选择和滚动位置，等待列表刷新。</summary>
        private void ChooseGroup(int index)
        {
            _group = index;
            ClearSelection();
            _isDirty = true;
            _groupScroll = Vector2.zero;
            _detailScroll = Vector2.zero;
        }

        /// <summary>按模式与脏状态重建行，并仅在项目、定义和地址身份仍匹配时恢复旧选择。</summary>
        private void RefreshRows(ResourceProject project, ResourceWorkbenchLayout layout)
        {
            var restore = !_hasRestoredUI && layout.selectionProjectGuid == AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project));
            if (restore)
            {
                _search = layout.entrySearch ?? "";
                if (layout.selectionGroup == null)
                {
                    _group = -1;
                }
                else
                {
                    var groups = project.groups.Select((value, index) => (Id: value.id, index)).Where(x => x.Id == layout.selectionGroup).ToArray();
                    if (groups.Length == 1)
                    {
                        _group = groups[0].index;
                    }
                    else
                    {
                        _restoredNotice = "上次查看的 Group 已变化，请重新选择；不会选择替代条目。";
                    }
                }
            }
            _hasRestoredUI = true;
            _table ??= new ResourceEntryTable(layout);
            if (!_isDirty && _renderedMode == _mode)
            {
                return;
            }

            var oldMode = _renderedMode;
            _renderedMode = _mode;
            _isDirty = false;
            if (_mode == 0)
            {
                _table.SetDefinitions(project, _group);
            }
            else if (_mode == 1)
            {
                IEnumerable<ResourceCatalogEntry> entries = _catalog?.Entries ?? Array.Empty<ResourceCatalogEntry>();
                if (_group >= 0 && _group < project.groups.Count && _map != null)
                {
                    var members = new HashSet<string>(_map.Groups.TryGetValue(project.groups[_group].id, out var ids) ? ids : Array.Empty<string>());
                    var guids = new HashSet<string>(_map.Assets.Where(x => members.Contains(x.AssetId)).Select(x => x.Guid));
                    entries = entries.Where(x => guids.Contains(x.AssetId.Substring(0, 32)));
                }
                _table.SetResolved(project, entries, _group < 0 ? "全部Group" : project.groups[_group].id);
            }
            _table.searchString = _search;
            if (restore)
            {
                if (_table.RestoreSelection(project, layout.selectedRows) > 0)
                {
                    _restoredNotice = "部分上次选择已删除、重复或发生变化，已清除；请重新选择。";
                }
            }
            else if (_selectGuid != null)
            {
                var row = _table.GetRows().Cast<ResourceEntryTable.Row>().FirstOrDefault(x => x.Guid == _selectGuid);
                if (row != null)
                {
                    _table.SetSelection(new[] { row.id });
                }

                _selectGuid = null;
            }
            else if (oldMode != _mode && _table.GetRows().Count > 0)
            {
                _table.SetSelection(new[] { _table.GetRows()[0].id });
            }
        }

        /// <summary>按可用宽度安排分组、表格和详情，并在 TreeView 消费前接管外部资产拖放。</summary>
        private void DrawBody(float width, ResourceProject project, EditorWindow window, ResourceWorkbenchLayout layout, Action changed, Action<Func<string>> run)
        {
            var wide = width >= 1000;
            if (!wide && layout.compactDetails)
            {
                DrawInspector(width, project, changed, run);
                return;
            }
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            if (wide && layout.showGroups)
            {
                layout.groupWidth = Mathf.Clamp(layout.groupWidth, 130, 260);
                DrawGroups(GUILayoutUtility.GetRect(layout.groupWidth, layout.groupWidth, 0, 100000, GUILayout.ExpandHeight(true)), project);
                var split = GUILayoutUtility.GetRect(6, 6, 0, 100000, GUILayout.ExpandHeight(true));
                ResourceWorkbenchGui.Splitter(split, ref layout.groupWidth, 130, 260);
            }
            var rect = GUILayoutUtility.GetRect(0, 100000, 80, 100000, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            _table.Locate = row => run(() => Locate(row));
            _table.Context = row => ContextMenu(row, project, changed, run);
            _table.SelectionUpdated = () =>
            {
                _detailScroll = Vector2.zero;
                StoreUIState(project, layout);
                if (window)
                {
                    window.Repaint();
                }
            };
            // TreeView默认拖放会消费事件；外部资产必须先走定义的统一归属/编辑门禁。
            HandleDrop(rect, project, changed, run);
            _table.OnGUI(rect);
            if (Event.current.type == EventType.Repaint && Mathf.Abs(_tableWidth - rect.width) > 1)
            {
                _tableWidth = rect.width;
                _table.multiColumnHeader.ResizeToFit();
                if (window)
                {
                    window.Repaint();
                }
            }
            if (!string.IsNullOrEmpty(_dropHint))
            {
                GUI.Box(new Rect(rect.x + 6, rect.yMax - 48, rect.width - 12, 42), _dropHint, EditorStyles.helpBox);
            }

            if (_table.VisibleCount == 0)
            {
                GUI.Label(new Rect(rect.x + 12, rect.y + 42, rect.width - 24, 60),
                _table.Count == 0 ? _mode == 1 ? "尚无可显示的解析结果。请显式刷新预览。" : project.groups.Count == 0 ? "尚无Group。点击 + Group 新建，再添加资源；专用根在项目设置中指定。" : _group < 0 ? "全部Group只读聚合；请进入具体Group添加资源。" : "Group为空。添加或拖入文件/目录；不删除真实资产。" : "没有匹配记录；已选的隐藏条目仍保留。", ResourceWorkbenchGui.Secondary);
            }

            if (wide && layout.showDetails)
            {
                var max = Mathf.Min(440, width - (layout.showGroups ? layout.groupWidth : 0) - 350);
                layout.detailWidth = Mathf.Clamp(layout.detailWidth, 280, max);
                var split = GUILayoutUtility.GetRect(6, 6, 0, 100000, GUILayout.ExpandHeight(true));
                ResourceWorkbenchGui.Splitter(split, ref layout.detailWidth, 280, max, -1);
                DrawInspector(layout.detailWidth, project, changed, run);
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>只记录主资源模式的搜索与选择身份，不把解析结果保存为另一份资源定义。</summary>
        internal void StoreUIState(ResourceProject project, ResourceWorkbenchLayout layout)
        {
            if (!project || _table == null || _mode != 0)
            {
                return;
            }

            layout.selectionProjectGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project));
            layout.selectionGroup = _group >= 0 && _group < project.groups.Count ? project.groups[_group].id : null;
            layout.entrySearch = _search;
            layout.selectedRows = _table.CaptureSelection(project);
        }

        /// <summary>只绘制滚动可见的分组行，组内数量表示选择定义数而非解析资产数。</summary>
        private void DrawGroups(Rect rect, ResourceProject project)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            GUI.Label(new Rect(rect.x + 8, rect.y + 6, rect.width - 16, 22), "资源分组", EditorStyles.boldLabel);
            rect.yMin += 32;
            _groupScroll = GUI.BeginScrollView(rect, _groupScroll, new Rect(0, 0, rect.width - 18, (project.groups.Count + 1) * 27));
            var first = Mathf.Max(0, (int)(_groupScroll.y / 27));
            var end = Math.Min(project.groups.Count + 1, first + Mathf.CeilToInt(rect.height / 27) + 1);
            for (var index = first; index < end; index++)
            {
                var target = index - 1;
                var name = target < 0 ? "全部Group（只读）" : project.groups[target].id + "  (" + project.groups[target].selections.Count + ")";
                if (ResourceWorkbenchGui.SelectableRow(new Rect(4, index * 27, rect.width - 26, 26), target == _group, new GUIContent(name, target < 0 ? "聚合浏览，不执行跨Group编辑" : project.groups[target].packing.ToString())) && target != _group)
                {
                    ChooseGroup(target);
                }
            }
            GUI.EndScrollView();
        }

        /// <summary>按唯一 GUID 定位可编辑定义；聚合与解析模式仅展示详情，子资产不单独编辑。</summary>
        private void DrawInspector(float width, ResourceProject project, Action changed, Action<Func<string>> run)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Width(width), GUILayout.ExpandHeight(true));
            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll, GUILayout.ExpandHeight(true));
            GUILayout.Space(6);
            GUILayout.Label(_table.GetSelection().Count > 1 ? "批量选择" : "条目详情", ResourceWorkbenchGui.Title);
            var row = _table.SingleSelection;
            if (row == null)
            {
                GUILayout.Label(_table.GetSelection().Count == 0 ? "选择资源行查看详情。Ctrl/Shift可多选。" : "共选 " + _table.GetSelection().Count + " 项，当前可见 " + _table.VisibleSelectionCount + " 项。\n批量操作位于列表底部；不会静默批改地址。", ResourceWorkbenchGui.Secondary);
            }
            else
            {
                GUILayout.Label(new GUIContent(row.displayName, row.Path), EditorStyles.boldLabel, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
                GUILayout.Label(row.Cells[1] + " / " + row.Group + (_group < 0 || _mode == 1 ? " / 只读" : ""), ResourceWorkbenchGui.Secondary);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("定位", ResourceWorkbenchGui.Button, GUILayout.Width(50)))
                {
                    run(() => Locate(row));
                }

                EditorGUILayout.EndHorizontal();
                ResourceWorkbenchGui.CopyLine("源路径", row.Path, width - 22);
                if (_mode == 0)
                {
                    var owner = project.groups.FindIndex(x => x.id == row.Group);
                    var indices = owner < 0 ? Array.Empty<int>() : project.groups[owner].selections.Select((value, index) => (value, index)).Where(x => x.value.guid == row.Guid).Select(x => x.index).ToArray();
                    if (indices.Length != 1)
                    {
                        EditorGUILayout.HelpBox("定义已变化或GUID重复，请刷新/重新选择；未编辑其他条目。", MessageType.Warning);
                    }
                    else
                    {
                        using var disabled = new EditorGUI.DisabledScope(_group < 0);
                        using var serialized = new SerializedObject(project);
                        serialized.Update();
                        var item = serialized.FindProperty("groups").GetArrayElementAtIndex(owner).FindPropertyRelative("selections").GetArrayElementAtIndex(indices[0]);
                        var labelWidth = EditorGUIUtility.labelWidth;
                        EditorGUIUtility.labelWidth = Mathf.Min(110, width * .35f);
                        if (row.IsFolder)
                        {
                            GUILayout.Label("解析或构建时递归展开目录。扩展名留空表示全部可打包类型；示例：.png。", ResourceWorkbenchGui.Secondary);
                            ResourceWorkbenchGui.Property(item.FindPropertyRelative("extensions"), new GUIContent("扩展名筛选"));
                        }
                        else
                        {
                            ResourceWorkbenchGui.Property(item.FindPropertyRelative("address"), new GUIContent("资源地址", "留空使用所属资源目录的地址前缀和相对路径。地址与别名在资源项目内不得重复或发生大小写冲突。"));
                            ResourceWorkbenchGui.Property(item.FindPropertyRelative("aliases"), new GUIContent("地址别名", "Alias 是同一主资产的额外地址，不会复制资源。"));
                        }
                        if (serialized.ApplyModifiedProperties())
                        {
                            changed();
                        }

                        EditorGUIUtility.labelWidth = labelWidth;
                    }
                }
                else
                {
                    ResourceWorkbenchGui.CopyLine("解析地址", row.Address, width - 22);
                    ResourceWorkbenchGui.CopyLine("AssetId / localId", row.Entry.AssetId, width - 22);
                    ResourceWorkbenchGui.CopyLine("地址别名", string.Join("\n", row.Entry.Aliases), width - 22);
                    if (row.Entry.Address.SubAsset != null)
                    {
                        GUILayout.Label("子资产由主文件导入派生，继承主文件地址/alias；不独立编辑。", ResourceWorkbenchGui.Secondary);
                    }
                }
                GUILayout.Space(8);
                ResourceWorkbenchGui.CopyLine("源GUID", row.Guid, width - 22);
                if (_map != null)
                {
                    GUILayout.Label("分包归属与依赖见“分包分析”；预估不代表已构建结果。", ResourceWorkbenchGui.Secondary);
                }
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        /// <summary>在 Project 窗口定位源资产，解析行通过模拟器重验后取得借用对象。</summary>
        private string Locate(ResourceEntryTable.Row row)
        {
            Selection.activeObject = row.Entry == null ? AssetDatabase.LoadMainAssetAtPath(row.Path) : _simulation.Load(row.Entry.Address, row.Entry.Location.AssetType);
            EditorGUIUtility.PingObject(Selection.activeObject);
            return "已在Unity Project窗口定位源资产；未执行运行时加载。";
        }

        /// <summary>为可编辑组提供拖放范围提示；接收后仍由添加服务检查类型、路径和重复登记。</summary>
        private void HandleDrop(Rect rect, ResourceProject project, Action changed, Action<Func<string>> run)
        {
            var current = Event.current;
            if (current.rawType == EventType.DragExited)
            {
                _dropHint = null;
            }

            if (!GUI.enabled || _group < 0 || _group >= project.groups.Count || _mode != 0 || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                if (rect.Contains(current.mousePosition) && (current.rawType == EventType.DragUpdated || current.rawType == EventType.DragPerform))
                {
                    _dropHint = "当前只读或编辑受阻；请进入具体Group的选择定义。";
                    DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                    current.Use();
                }
                return;
            }
            if (!rect.Contains(current.mousePosition) || (current.type != EventType.DragUpdated && current.type != EventType.DragPerform))
            {
                return;
            }

            var paths = DragAndDrop.objectReferences.Select(AssetDatabase.GetAssetPath).ToArray();
            var valid = false;
            try
            {
                var directories = ResourceWorkbenchGui.DisplayPaths(project);
                valid = paths.Length > 0 && paths.All(path => directories.ContainsSource(path) && !ResourceProjectPaths.Contains(ResourceProjectPaths.GeneratedDirectory(project), path));
            }
            catch (InvalidOperationException) { /* 拖放只显示准入提示；添加和构建仍由统一入口校验。 */ }
            _dropHint = valid ? "添加主文件或文件夹，子资产由主文件派生。添加时会继续检查资源类型和重复条目。" :
                "无法添加：资源不在有效的资源目录中，或属于生成目录。请先在‘项目设置’中检查目录登记；不会自动扩大范围。";
            DragAndDrop.visualMode = valid ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (current.type == EventType.DragPerform)
            {
                if (valid)
                {
                    DragAndDrop.AcceptDrag();
                    Add(project, DragAndDrop.objectReferences, changed, run);
                }
                _dropHint = null;
            }
            current.Use();
        }

        /// <summary>委托正式入口加入主文件或目录定义，随后清搜索并定位新条目。</summary>
        private void Add(ResourceProject project, Object[] assets, Action changed, Action<Func<string>> run) => run(() =>
        {
            ResourceEditorOperations.AddSelections(project, _group, assets);
            ClearSearch();
            _selectGuid = project.groups[_group].selections.Last().guid;
            ClearSelection();
            _mode = 0;
            _isDirty = true;
            changed();
            return "已加入主文件/目录选择定义，尚未保存；子资产由主文件派生，未修改Importer。";
        });

        /// <summary>展示总选择与可见选择差异，仅在具体组的定义模式提供批量编辑。</summary>
        private void DrawBatch(ResourceProject project, Action changed, Action<Func<string>> run, EditorWindow window)
        {
            EditorGUILayout.BeginHorizontal(ResourceWorkbenchGui.Toolbar);
            GUILayout.Label("选中 " + _table.GetSelection().Count + " / 可见选中 " + _table.VisibleSelectionCount + " / 匹配 " + _table.VisibleCount, EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("全选匹配", ResourceWorkbenchGui.ToolbarButton, GUILayout.Width(65)))
            {
                _table.SelectAllRows();
            }

            if (GUILayout.Button("清选择", ResourceWorkbenchGui.ToolbarButton, GUILayout.Width(52)))
            {
                ClearSelection();
            }

            using (new EditorGUI.DisabledScope(_mode != 0 || _group < 0 || _table.GetSelection().Count == 0))
            {
                if (GUILayout.Button("移动", ResourceWorkbenchGui.Menu, GUILayout.Width(50)))
                {
                    MoveMenu(project, changed, run);
                }

                if (GUILayout.Button("从定义移除", ResourceWorkbenchGui.ToolbarButton, GUILayout.Width(82)))
                {
                    Remove(project, changed, run);
                }
            }
            if (_mode == 0 && GUILayout.Button(new GUIContent("前往构建发布", "只切换页面；请先保存资源项目，不会自动构建。"), ResourceWorkbenchGui.ToolbarButton, GUILayout.Width(108)))
            {
                (window as ResServiceEditorWindow)?.NavigateTask(2);
            }

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>捕获完整选择并在菜单执行时重解组与条目身份，移动包含搜索隐藏项的集合。</summary>
        private void MoveMenu(ResourceProject project, Action changed, Action<Func<string>> run)
        {
            var source = _group;
            var sourceId = project.groups[source].id;
            var selection = _table.GetSelection().ToArray();
            var menu = new GenericMenu();
            for (var index = 0; index < project.groups.Count; index++)
            {
                if (index == source)
                {
                    continue;
                }

                var targetId = project.groups[index].id;
                menu.AddItem(new GUIContent(project.groups[index].id), false, () => run(() =>
                {
                    _table.SetSelection(selection);
                    var sourceIndex = project.groups.FindIndex(x => x.id == sourceId);
                    var targetIndex = project.groups.FindIndex(x => x.id == targetId);
                    if (sourceIndex < 0 || targetIndex < 0)
                    {
                        throw new InvalidOperationException("Group已变化，请重新打开移动菜单。");
                    }

                    ResourceEditorOperations.MoveSelections(project, sourceIndex, _table.GetSelectedIndices(project, sourceIndex), targetIndex);
                    ClearSelection();
                    _isDirty = true;
                    changed();
                    return "已移动完整选中集合（包含筛选隐藏项），地址/alias保留，可Undo。";
                }));
            }
            if (menu.GetItemCount() == 0)
            {
                menu.AddDisabledItem(new GUIContent("需要另一个Group"));
            }

            menu.ShowAsContext();
        }

        /// <summary>经当前定义索引复核后移除完整选择集合，不删除源文件。</summary>
        private void Remove(ResourceProject project, Action changed, Action<Func<string>> run) => run(() =>
        {
            ResourceEditorOperations.RemoveSelections(project, _group, _table.GetSelectedIndices(project, _group));
            ClearSelection();
            _isDirty = true;
            changed();
            return "已移除完整选择定义集合（含隐藏项），未删除源文件，可Undo。";
        });

        /// <summary>按聚合或编辑模式提供定位、复制及完整选择集合的操作入口。</summary>
        private void ContextMenu(ResourceEntryTable.Row row, ResourceProject project, Action changed, Action<Func<string>> run)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("定位源资产"), false, () => run(() => Locate(row)));
            menu.AddItem(new GUIContent("复制完整路径"), false, () => EditorGUIUtility.systemCopyBuffer = row.Path);
            menu.AddItem(new GUIContent("复制完整地址"), false, () => EditorGUIUtility.systemCopyBuffer = row.Address);
            if (_group < 0 && _mode == 0)
            {
                menu.AddItem(new GUIContent("进入所属Group编辑"), false, () =>
                {
                    ChooseGroup(project.groups.FindIndex(x => x.id == row.Group));
                    _selectGuid = row.Guid;
                });
            }

            if (_group >= 0 && _mode == 0)
            {
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("移动完整选中集合..."), false, () => MoveMenu(project, changed, run));
                menu.AddItem(new GUIContent("从定义移除完整选中集合（可Undo）"), false, () => Remove(project, changed, run));
            }
            menu.ShowAsContext();
        }

        /// <summary>复核分包报告中的来源身份后跳转到原选择项或图集规则，不用当前近似项替代。</summary>
        internal void LocateSource(ResourceProject project, ResourceBuildSource source)
        {
            var location = ResourceBuildSourceLocator.Resolve(project, source);
            if (location.Rule >= 0)
            {
                _mode = 2;
                _atlas.SelectRule(location.Rule);
            }
            else
            {
                _mode = 0;
                _group = location.Group;
                _selectGuid = source.SelectionGuid;
                _isDirty = true;
            }
        }
    }
}
