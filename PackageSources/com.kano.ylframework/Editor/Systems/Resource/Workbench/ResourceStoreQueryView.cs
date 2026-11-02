using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>管理显式缓存根和完整项目列表的有界只读查询，不挂载、恢复或清理 ArtifactStore。</summary>
    internal sealed class ResourceStoreQueryView : IDisposable
    {
        /// <summary>操作者填写的一项查询身份，只在窗口会话保留，不从工作台项目推断。</summary>
        private sealed class ProjectInput
        {
            internal string ProjectId = "";
            internal string Platform = "";
            internal string App = "";
        }
        private readonly List<ProjectInput> _projects = new() { new ProjectInput() };
        private string _root = "";
        private int _maxEntries = 10000;
        private IEnumerator _query;
        private CancellationTokenSource _cancellation;
        // 每轮查询单独持有身份；停止后旧回调即使到达也不能覆盖新结果。
        private object _runningIdentity;
        private ArtifactStoreReport _report;
        private string _reportIdentity;
        private string _queryIdentity;
        private string _reportSummary;
        private string _reportNotes;
        private Exception _error;
        private Exception _cleanupError;
        private bool _showErrorDetails;
        private string _note;
        private bool _isReportOld;
        private bool _showResults;
        private bool _showCompactDetail;
        private ResourceArtifactTable _table;
        private Vector2 _scroll;
        private Vector2 _resultPageScroll;
        private bool _showInputHelp;
        private bool _showTechnical;
        private string _lifecycleDetail;
        internal bool IsQuerying => _query != null;

        /// <summary>绑定明确查询参数并创建枚举器，同一时间只允许一个查询；实际扫描由 Tick 分段推进。</summary>
        internal void Begin(string path, ResourceProjectMount[] closure, int steps)
        {
            if (_query != null)
            {
                throw new InvalidOperationException("一次只能运行一个磁盘缓存查询，请先停止当前查询。");
            }

            if (closure == null || closure.Any(x => x == null || x.BuiltIn != null || x.Remote != null))
            {
                throw new ArgumentException("编辑器查询只接收完整的项目参数列表，不能包含内置版本快照或远程配置对象。");
            }

            _isReportOld = _report != null;
            var store = new ArtifactStore(path, closure);
            _cancellation = new CancellationTokenSource();
            _runningIdentity = new object();
            var identity = _runningIdentity;
            _queryIdentity = store.RootPath + "\n" + string.Join("\n", closure.Select(x => x.ProjectId + " / " + x.Platform + " / App=" + x.AppContentCompatibilityId)) + "\n扫描步骤上限=" + steps;
            _error = _cleanupError = null;
            _note = _report == null ? "正在查询磁盘缓存。" : "正在查询，当前显示上次结果。";
            _showResults = true;
            _lifecycleDetail = "QueryAsync 在 Editor 主线程分段执行，每次 MoveNext 最多推进32步；同一时间只运行一个查询。";
            try
            {
                _query = store.QueryAsync(value =>
                {
                    if (!ReferenceEquals(identity, _runningIdentity))
                    {
                        return;
                    }

                    _report = value;
                    _reportIdentity = _queryIdentity;
                    _isReportOld = false;
                    _table ??= new ResourceArtifactTable();
                    _table.SetData(value);
                    _reportSummary = "Status=" + value.Status + " / SizeComplete=" + value.IsSizeComplete + " / 已观测逻辑字节=" + value.ObservedBytes +
                        " / 候选字节=" + (value.CleanupCandidateBytes?.ToString() ?? "不可用（不是0）") + " / Entries=" + value.Entries.Count;
                    _reportNotes = "完整项目列表（App 由操作者填写，项目登记记录不验证 App）：\n" + string.Join("\n", value.Projects.Select(x => x.ProjectId + " / " + x.Platform + " / App=" + x.AppContentCompatibilityId)) +
                        "\n\n原始问题记录：\n" + string.Join("\n", value.Issues) + "\n\n受保护版本：\n" + string.Join("\n", value.ProtectedVersions.Select(x => x.DirectoryPath + " / " + x.Reasons)) +
                        "\n\n正在进行的操作：\n" + string.Join("\n", value.InFlightOperations) + "\n\n已准备更新的记录（只读）：\n" + string.Join("\n", value.PreparedUpdates.Select(x => x.RecordPath + "\n" +
                            string.Join("\n", x.Projects.Select(p => p.ProjectId + " / " + p.Platform + " / " + p.FromDigest + " → " + p.ToDigest))));
                }, steps, _cancellation.Token);
            }
            catch
            {
                Stop("查询未能启动，未生成新报告");
                throw;
            }
        }

        /// <summary>在编辑器主线程推进一次查询，完成或异常时收尾，不将旧报告改造成新结果。</summary>
        internal bool Tick()
        {
            if (_query == null)
            {
                return false;
            }

            try
            {
                if (!_query.MoveNext())
                {
                    StopCore();
                    _note = _report != null && !_isReportOld ? "查询已结束。" : "查询已结束，未收到新结果。";
                    _lifecycleDetail = "查询枚举器已结束并释放，结果状态来自实际查询回调。";
                }
            }
            catch (Exception failure)
            {
                _error = failure;
                StopCore();
                _note = _report == null ? "本次查询未生成结果。" : "本次查询未完成，当前显示上次结果。";
                _lifecycleDetail = "发生异常后已停止查询并释放枚举器；未收到新回调时，不将旧报告改为完成或取消。";
            }
            return true;
        }
        /// <summary>停止当前查询并标记保留报告为旧结果；未收到新回调时不伪造取消报告。</summary>
        internal void Stop(string reason)
        {
            if (_query != null)
            {
                _note = _report == null ? "查询已停止，尚无查询结果。" : "查询已停止，当前显示上次结果。";
                _lifecycleDetail = "停止原因：" + reason + "。已取消并释放查询枚举器；未收到新回调时，不将旧报告改为取消或完成，仍保留原查询参数。";
            }
            if (_report != null)
            {
                _isReportOld = true;
            }

            StopCore();
        }
        /// <summary>先使查询身份失效，再尝试取消和释放枚举器；保留首个异常及后续清理异常。</summary>
        private void StopCore()
        {
            var original = _query;
            _query = null;
            _runningIdentity = null;
            var source = _cancellation;
            _cancellation = null;
            try
            {
                source?.Cancel();
                (original as IDisposable)?.Dispose();
            }
            catch (Exception failure)
            {
                if (_error == null)
                {
                    _error = failure;
                }
                else
                {
                    _cleanupError = failure;
                }
            }
            finally
            {
                source?.Dispose();
            }
        }

        /// <summary>绘制参数与只读结果入口，参数变化时停止原查询，不自动扫描或寻找替代缓存根。</summary>
        internal void Draw(float width)
        {
            EditorGUILayout.BeginHorizontal(ResourceWorkbenchGui.Toolbar);
            _showResults = ResourceWorkbenchGui.Navigation(_showResults ? 1 : 0, new[] { "查询参数", "查询结果" }, false, GUILayout.Width(168)) == 1;
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(IsQuerying))
            {
                if (GUILayout.Button("开始只读查询", ResourceWorkbenchGui.ToolbarButton, GUILayout.Width(112)))
                {
                    try
                    {
                        Begin(_root, _projects.Select(x => new ResourceProjectMount(x.ProjectId, x.Platform, x.App)).ToArray(), _maxEntries);
                    }
                    catch (Exception failure)
                    {
                        _error = failure;
                    }
                }
            }

            using (new EditorGUI.DisabledScope(!IsQuerying))
            {
                if (GUILayout.Button("停止查询", ResourceWorkbenchGui.ToolbarButton, GUILayout.Width(76)))
                {
                    Stop("用户停止");
                }
            }

            EditorGUILayout.EndHorizontal();
            if (!_showResults)
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(0), GUILayout.ExpandHeight(true));
                DrawErrorDetails();
                GUILayout.Label("仅查询 YL ArtifactStore，不是全盘缓存。请提供应用的真实缓存根与完整项目列表。", ResourceWorkbenchGui.Secondary);
                EditorGUI.BeginChangeCheck();
                _root = ResourceWorkbenchGui.TextField("缓存根目录", _root, true, "填写应用传给 ResourceConfiguration.Create(mode, mounts, cacheRoot) 的 cacheRoot 绝对路径；不会从 persistentDataPath 或工作台来源目录推算。");
                _maxEntries = EditorGUI.IntField(EditorGUILayout.GetControlRect(true, ResourceWorkbenchGui.FieldHeight, ResourceWorkbenchGui.Field),
                    new GUIContent("扫描步数上限", "maxEntries，1～100000；限制扫描步骤，不是返回条目行数。"), _maxEntries, ResourceWorkbenchGui.Field);
                var remove = -1;
                for (var i = 0; i < _projects.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("项目 " + (i + 1), EditorStyles.boldLabel, GUILayout.Height(ResourceWorkbenchGui.BarHeight));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("移除", ResourceWorkbenchGui.Button, GUILayout.Width(60)))
                    {
                        remove = i;
                    }

                    EditorGUILayout.EndHorizontal();
                    _projects[i].ProjectId = ResourceWorkbenchGui.TextField("项目标识", _projects[i].ProjectId, help: "ResourceProjectMount.ProjectId：填写应用实际挂载的项目标识，不自动使用当前工作台项目。");
                    _projects[i].Platform = ResourceWorkbenchGui.TextField("平台", _projects[i].Platform, help: "ResourceProjectMount.Platform，应用明确配置的平台值，不按当前Editor平台猜测。");
                    _projects[i].App = ResourceWorkbenchGui.TextField("应用兼容标识", _projects[i].App, help: "ResourceProjectMount.AppContentCompatibilityId：由操作者填写。项目登记记录只核对 ProjectId 和 Platform，不验证 App。");
                }
                if (EditorGUI.EndChangeCheck())
                {
                    Stop("表单输入变化");
                }

                if (remove >= 0)
                {
                    _projects.RemoveAt(remove);
                    Stop("项目列表变化");
                }
                if (GUILayout.Button("添加项目", ResourceWorkbenchGui.Button, GUILayout.Width(132)))
                {
                    _projects.Add(new ProjectInput());
                    Stop("项目列表变化");
                }
                _showInputHelp = ResourceWorkbenchGui.Disclosure(_showInputHelp, "参数来源与查询范围");
                if (_showInputHelp)
                {
                    GUILayout.Label("请使用应用传给 ResourceConfiguration.Create(mode, mounts, cacheRoot) 的 cacheRoot 和完整 ResourceProjectMount 列表。本工具不会自动读取游戏配置、反射应用对象或扫描其他目录寻找参数。", ResourceWorkbenchGui.Secondary);
                    GUILayout.Label("App 由操作者填写，项目登记记录不验证 App。扫描步数上限 maxEntries 允许1～100000，不是结果行数。参数只在本次窗口会话保留，不写入资源项目或配方。", ResourceWorkbenchGui.Secondary);
                    GUILayout.Label("查询不初始化或挂载运行时，也不恢复、清理、删除、激活、创建缓存根或锁。", ResourceWorkbenchGui.Secondary);
                }
                EditorGUILayout.EndScrollView();
            }
            else
            {
                DrawReport(width);
            }

            if (_error != null || _note != null && (_report == null || _isReportOld || IsQuerying))
            {
                EditorGUI.LabelField(EditorGUILayout.GetControlRect(false, ResourceWorkbenchGui.RowHeight), new GUIContent(_error == null ? _note : "查询未完成，请展开错误详情。", "查询状态：" + _note + "\n" + _lifecycleDetail), ResourceWorkbenchGui.Secondary);
            }
        }

        /// <summary>展示同次观测的范围、时间、完整性和候选信息，旧报告不代表当前表单或磁盘状态。</summary>
        private void DrawReport(float width)
        {
            if (_report == null)
            {
                _resultPageScroll = EditorGUILayout.BeginScrollView(_resultPageScroll, GUILayout.MinHeight(0), GUILayout.ExpandHeight(true));
                DrawErrorDetails();
                GUILayout.Label(IsQuerying ? "正在查询，尚无结果。" : "尚无查询结果。填写明确的缓存根与项目列表后，开始只读查询。", ResourceWorkbenchGui.Secondary);
                EditorGUILayout.EndScrollView();
                return;
            }
            EditorGUILayout.BeginHorizontal();
            ResourceWorkbenchGui.Metric("结果状态", (_isReportOld ? "上次 / " : "") + StatusLabel(_report.Status), "Status: " + _report.Status + "；Ready 只表示查询完成，不保证 App 兼容、解密、签名或运行时加载检查通过。", 80);
            ResourceWorkbenchGui.Metric("空间观测", _report.IsSizeComplete ? "完整" : "不完整 / 总量未知", "IsSizeComplete；未知目录与链接目标不纳入完整观测，不完整字节不是0。", 80);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            ResourceWorkbenchGui.Metric("已观测字节", ResourceWorkbenchGui.Bytes(_report.ObservedBytes), "ObservedBytes: " + _report.ObservedBytes + "；普通文件逻辑长度之和，不是实际磁盘占用或原生内存。观测不完整时不代表总量。", 80);
            ResourceWorkbenchGui.Metric("候选字节", ResourceWorkbenchGui.Bytes(_report.CleanupCandidateBytes), "CleanupCandidateBytes；仅Ready提供，未知不是0；候选逻辑字节不保证可释放同等磁盘空间，也不是删除授权。", 80);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            EditorGUI.LabelField(EditorGUILayout.GetControlRect(false, ResourceWorkbenchGui.RowHeight), new GUIContent("YL缓存范围", "仅查询 YL ArtifactStore，不是全盘缓存。RootPath：" + _report.RootPath), new GUIContent(_report.RootPath, _report.RootPath));
            GUILayout.Label(new GUIContent(_report.Entries.Count + " 条目 / " + _report.ProtectedVersions.Count + " 保护版本", "Entries / ProtectedVersions，来自同次原报告。"), GUILayout.Width(168), GUILayout.Height(ResourceWorkbenchGui.RowHeight));
            EditorGUILayout.EndHorizontal();
            ResourceWorkbenchGui.Metric("观测时间（UTC）", _report.StartedUtc.ToString("MM-dd HH:mm:ss") + " 至 " + _report.CompletedUtc.ToString("MM-dd HH:mm:ss"),
                "StartedUtc: " + _report.StartedUtc.ToString("u") + "\nCompletedUtc: " + _report.CompletedUtc.ToString("u") + "\n一次有界观测，不是磁盘快照。", 116);
            var wide = width >= 950;
            EditorGUILayout.BeginHorizontal(ResourceWorkbenchGui.Toolbar);
            GUILayout.Label("筛选", GUILayout.Width(32), GUILayout.Height(ResourceWorkbenchGui.FieldHeight));
            _table.searchString = ResourceWorkbenchGui.SearchField(_table.searchString, "筛选本次查询：路径、类型、状态、保护信息");
            _table.AlternateRows = ResourceWorkbenchGui.ToolbarToggle(_table.AlternateRows, "底纹", 64);
            if (!wide)
            {
                _showCompactDetail = GUILayout.Toggle(_showCompactDetail, "详情", ResourceWorkbenchGui.ToolbarButton, GUILayout.Width(60));
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            if (wide || !_showCompactDetail)
            {
                _table.OnGUI(GUILayoutUtility.GetRect(0, 100000, 0, 100000, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)));
            }

            if (wide || _showCompactDetail)
            {
                EditorGUILayout.BeginVertical(wide ? new[] { GUILayout.Width(width * .44f), GUILayout.MinHeight(0), GUILayout.ExpandHeight(true) } : new[] { GUILayout.MinWidth(0), GUILayout.ExpandWidth(true), GUILayout.MinHeight(0), GUILayout.ExpandHeight(true) });
                _resultPageScroll = EditorGUILayout.BeginScrollView(_resultPageScroll, GUILayout.MinHeight(0), GUILayout.ExpandHeight(true));
                DrawErrorDetails();
                if (_isReportOld)
                {
                    GUILayout.Label("当前显示上次结果，保留原查询范围与时间，不代表当前表单或磁盘状态。", ResourceWorkbenchGui.Secondary);
                }

                if (_report.Issues.Count > 0)
                {
                    GUILayout.Label("本次查询记录了 " + _report.Issues.Count + " 项问题，请在技术详情中查看原始记录。", ResourceWorkbenchGui.Secondary);
                }

                var entry = _table.Selected;
                if (entry != null)
                {
                    GUILayout.Label("所选条目", EditorStyles.boldLabel);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.SelectableLabel(entry.Path, ResourceWorkbenchGui.Field, GUILayout.Height(ResourceWorkbenchGui.FieldHeight), GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("复制路径", ResourceWorkbenchGui.Button, GUILayout.Width(76)))
                    {
                        EditorGUIUtility.systemCopyBuffer = entry.Path;
                    }

                    EditorGUILayout.EndHorizontal();
                    ResourceWorkbenchGui.Metric("条目状态", ResourceArtifactTable.StateLabel(entry.State), "State: " + entry.State + "；CompleteRecord 只表示登记记录和文件集合完整，不表示 Bundle SHA256 或签名验证通过。");
                    ResourceWorkbenchGui.Metric("保护原因", ResourceArtifactTable.ProtectionLabel(entry.Protection), "Protection: " + entry.Protection + "；来自同次报告，多个保护原因可叠加。");
                    ResourceWorkbenchGui.Metric("逻辑字节", ResourceWorkbenchGui.Bytes(entry.ObservedBytes), "ObservedBytes: " + entry.ObservedBytes + "；不是磁盘分配量。");
                    ResourceWorkbenchGui.Metric("空间观测", entry.IsSizeComplete ? "完整" : "不完整 / 总量未知", "IsSizeComplete；已观测0不等于确认总量为0。");
                    ResourceWorkbenchGui.Metric("清理候选", _report.Status != ArtifactQueryStatus.Ready || !entry.IsSizeComplete ? "未确认" : entry.IsCleanupCandidate ? "是" : "否", "IsCleanupCandidate: " + entry.IsCleanupCandidate + "；候选不是删除授权，不保证可释放空间。");
                    ResourceWorkbenchGui.Metric("所属项目", entry.ProjectId ?? "未知", "ProjectId：记录未提供项目时显示未知。");
                    ResourceWorkbenchGui.Metric("平台", entry.Platform ?? "未知", "Platform：来自同次查询的记录。");
                    if (entry.Issues.Count > 0)
                    {
                        GUILayout.Label("此条目有 " + entry.Issues.Count + " 项问题，请展开技术详情。", ResourceWorkbenchGui.Secondary);
                    }
                }
                else
                {
                    GUILayout.Label("在条目列表选择一行查看状态、字节与保护原因。", ResourceWorkbenchGui.Secondary);
                }

                GUILayout.Label("候选为逻辑字节，不保证可释放同等磁盘空间；本页仅查询，不执行清理。", ResourceWorkbenchGui.Secondary);
                _showTechnical = ResourceWorkbenchGui.Disclosure(_showTechnical, "完整查询身份与技术详情");
                if (_showTechnical)
                {
                    var text = _reportIdentity + "\n\n" + _reportSummary + "\n\n" + _reportNotes + "\n\n" + _lifecycleDetail;
                    if (entry != null)
                    {
                        text += "\n\n所选条目技术字段：\nPath=" + entry.Path + "\nProjectId=" + entry.ProjectId + "\nPlatform=" + entry.Platform + "\nCatalogDigest=" + entry.CatalogDigest +
                        "\nKind=" + entry.Kind + "\nState=" + entry.State + "\nObservedBytes=" + entry.ObservedBytes + "\nIsSizeComplete=" + entry.IsSizeComplete + "\nIsCleanupCandidate=" + entry.IsCleanupCandidate + "\nProtection=" + entry.Protection +
                        "\n原始问题记录：\n" + string.Join("\n", entry.Issues);
                    }

                    EditorGUILayout.SelectableLabel(text, EditorStyles.wordWrappedLabel, GUILayout.Height(Mathf.Max(ResourceWorkbenchGui.RowHeight,
                        EditorStyles.wordWrappedLabel.CalcHeight(new GUIContent(text), Mathf.Max(240, wide ? width * .44f - 44 : width - 44)))));
                }
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>将查询终态映射为说明；查询完成不等于兼容、解密、签名或运行时加载通过。</summary>
        private static string StatusLabel(ArtifactQueryStatus value) => value switch
        {
            ArtifactQueryStatus.Ready => "查询完成",
            ArtifactQueryStatus.NotConfigured => "未配置",
            ArtifactQueryStatus.Missing => "根目录不存在",
            ArtifactQueryStatus.Busy => "缓存使用中",
            ArtifactQueryStatus.RecoveryRequired => "需要恢复",
            ArtifactQueryStatus.Invalid => "记录无效",
            ArtifactQueryStatus.Unreadable => "不可读取",
            ArtifactQueryStatus.Incomplete => "扫描未完成",
            ArtifactQueryStatus.Changed => "观测期间已变化",
            ArtifactQueryStatus.Canceled => "已取消",
            _ => "未知状态（" + value + "）"
        };
        /// <summary>按需分别展示查询失败与收尾异常，避免后者覆盖首因。</summary>
        private void DrawErrorDetails()
        {
            if (_error == null)
            {
                return;
            }

            EditorGUILayout.HelpBox("本次磁盘缓存查询未完成，请查看错误详情；下方可能仍显示上次查询结果。", MessageType.Error);
            _showErrorDetails = ResourceWorkbenchGui.Disclosure(_showErrorDetails, "查询错误详情");
            if (!_showErrorDetails)
            {
                return;
            }

            GUILayout.Label(ResourceEditorText.FailureDetails(_error), ResourceWorkbenchGui.Secondary);
            if (_cleanupError != null)
            {
                GUILayout.Label("结束查询时另有异常：\n" + ResourceEditorText.FailureDetails(_cleanupError), ResourceWorkbenchGui.Secondary);
            }
        }
        /// <summary>窗口关闭或重载时停止本视图查询，不删除缓存或改写已有报告状态。</summary>
        public void Dispose() => Stop("关闭或重载");
    }
}
