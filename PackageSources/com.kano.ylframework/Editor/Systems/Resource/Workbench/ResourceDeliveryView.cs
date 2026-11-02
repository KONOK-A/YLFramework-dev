using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>浏览明确登记的发布快照并协调显式交付动作，不以发布索引冒充运行时版本。</summary>
    internal sealed class ResourceDeliveryView
    {
        private ResourceReleaseTable _table;
        private IReadOnlyList<ResourceRelease> _releases = Array.Empty<ResourceRelease>();
        private ResourceReleaseIdentity _selected;
        private ResourceRelease _selectedRelease;
        private ResourceRelease _current;
        // 历史列表、发布索引和所选版本分别失效，读取其中一项不能顺带验证另外两项。
        private bool _hasReadHistory;
        private bool _hasReadIndex;
        private bool _isHistoryStale;
        private bool _isIndexStale;
        private bool _isSelectionStale;
        private DateTime _historyAt;
        private DateTime _indexAt;
        private string _search = "";
        private int _tab;
        private bool _showCompactDetail;
        private string _exportParent = "";
        private string _generationDirectory = ResourceReferenceOutput.DirectoryPath;
        private Exception _error;
        private bool _showErrorDetails;
        private string _result;
        private bool _isIndexResult;
        private bool _hasIndexFailed;
        private string _selectedFacts;
        private (string Name, string Size, string Sha)[] _files = Array.Empty<(string, string, string)>();
        private Vector2 _detailsScroll;
        private Vector2 _filesScroll;
        private Vector2 _operationScroll;
        private bool _showFiles;
        private bool _showTechnical;
        private bool _showExportSettings;
        private bool _showDeploymentDetails;
        private ResourceBuiltInPlan _plan;
        private ResourceReleaseIdentity _planIdentity;
        private string _scope;
        private ResourceBuiltInDeployment _deployment;

        /// <summary>清除项目相关的版本、索引、部署计划和结果上下文，不处理磁盘事务。</summary>
        internal void Reset()
        {
            _table = null;
            _releases = Array.Empty<ResourceRelease>();
            _selected = null;
            _selectedRelease = null;
            _current = null;
            _plan = null;
            _planIdentity = null;
            _hasReadHistory = _hasReadIndex = _isHistoryStale = _isIndexStale = _isSelectionStale = _hasIndexFailed = _isIndexResult = false;
            _error = null;
            _result = _scope = null;
            _deployment = null;
            _files = Array.Empty<(string, string, string)>();
            _selectedFacts = null;
            _tab = 0;
            _search = "";
        }
        /// <summary>将已读取快照标记过期并丢弃部署计划，保留旧数据供查看。</summary>
        internal void Invalidate()
        {
            _isHistoryStale = _hasReadHistory;
            _isIndexStale = _hasReadIndex;
            _isSelectionStale = _selected != null;
            _plan = null;
            _planIdentity = null;
        }
        /// <summary>将新确认的发布结果设为选择，旧历史与索引仍待独立重读。</summary>
        internal void OfferRelease(ResourceRelease release)
        {
            if (release == null)
            {
                return;
            }

            _table?.SetSelection(Array.Empty<int>());
            _isHistoryStale = _hasReadHistory;
            _isIndexStale = _hasReadIndex;
            Select(release);
            _isSelectionStale = false;
        }
        /// <summary>捕获精确版本身份和文件事实，同时丢弃上一选择的部署计划。</summary>
        private void Select(ResourceRelease release)
        {
            _selectedRelease = release;
            _selected = new ResourceReleaseIdentity(release);
            _plan = null;
            _planIdentity = null;
            _scope = null;
            _detailsScroll = _filesScroll = Vector2.zero;
            var catalog = release.Catalog;
            _selectedFacts = "Bundle 数=" + catalog.Bundles.Count + " / 主、子资源记录=" + catalog.Assets.Count + " / 分组=" + catalog.Groups.Count +
                "\nBundle 交付文件逻辑字节=" + catalog.Bundles.Sum(x => x.Length) + " / 支持文件逻辑字节=" + catalog.SupportFiles.Sum(x => x.Length) +
                "\n历史版本不提供本次发布的复制量和耗时测量。原始构建文件量和原生内存不能由交付版本推算。";
            _files = catalog.Bundles.Select(x => (x.RelativeFile, x.Length.ToString(), x.Sha256))
                .Concat(catalog.SupportFiles.Select(x => (x.RelativeFile, x.Length.ToString(), x.Sha256)))
                .Concat(new[] { ("resource-catalog.json", "已验证目录记录", catalog.CatalogDigest), (".ylr-complete.json", "已验证完成记录", "完成记录不替代 Bundle SHA256、签名或运行时加载检查。") }).ToArray();
        }

        /// <summary>登记本次历史读取时间并重绑表格，仅当原所选版本在此次历史中验证过才更新其状态。</summary>
        private void ApplyHistory(IReadOnlyList<ResourceRelease> read)
        {
            _releases = read;
            _hasReadHistory = true;
            _historyAt = DateTime.UtcNow;
            _isHistoryStale = false;
            _table ??= new ResourceReleaseTable();
            _table.SetData(_releases, _hasReadIndex && !_isIndexStale ? _current?.Catalog.CatalogDigest ?? "" : null);
            var verified = _selected == null ? null : _releases.FirstOrDefault(x => x.Catalog.CatalogDigest == _selected.CatalogDigest);
            if (verified != null)
            {
                Select(verified);
                _isSelectionStale = false;
                _table.Select(verified.Catalog.CatalogDigest);
            }
        }

        /// <summary>更新发布索引快照及比较标记，仅在摘要相同时更新所选版本的验证状态。</summary>
        private void ApplyCurrent(ResourceRelease read)
        {
            _current = read;
            _hasReadIndex = true;
            _indexAt = DateTime.UtcNow;
            _isIndexStale = _hasIndexFailed = false;
            if (_table != null)
            {
                _table.SetData(_releases, _current?.Catalog.CatalogDigest ?? "");
            }

            if (_current != null && _selected?.CatalogDigest == _current.Catalog.CatalogDigest)
            {
                Select(_current);
                _isSelectionStale = false;
            }
            if (_selected != null)
            {
                _table?.Select(_selected.CatalogDigest);
            }
        }

        /// <summary>组织版本、内置部署和恢复页面，App 变化时使旧快照失效但不自动重读。</summary>
        internal void Draw(ResourceProject project, ref string app, float width, Action<Func<string>> queue, Action<Func<string>> run)
        {
            EditorGUI.BeginChangeCheck();
            app = ResourceWorkbenchGui.TextField("资源兼容标识（App）", app, true, "读取发布索引和后续交付所使用的应用兼容标识，不是游戏版本号。");
            if (EditorGUI.EndChangeCheck())
            {
                Invalidate();
            }

            _tab = ResourceWorkbenchGui.Navigation(_tab, new[] { "发布版本", "内置部署", "恢复与其他" });
            var appValue = app;
            if (_isHistoryStale || _isIndexStale || _isSelectionStale)
            {
                EditorGUILayout.HelpBox("以下结果需要重新读取：" + (_isHistoryStale ? "已登记版本；" : "") + (_isIndexStale ? "发布索引；" : "") +
                (_isSelectionStale ? "所选版本。" : "") + "历史和发布索引是独立快照，刷新一项不会验证另一项。", MessageType.Warning);
            }

            if (_error != null)
            {
                EditorGUILayout.HelpBox("交付操作未完成。请展开错误详情；当前保留的旧结果不能代表本次操作成功。需要恢复时，请使用对应的恢复入口。", MessageType.Error);
            }

            if (_tab == 0)
            {
                DrawHistory(project, appValue, width, queue, run);
            }
            else if (_tab == 1)
            {
                DrawDeployment(project, width, queue);
            }
            else
            {
                DrawRecovery(project, appValue, queue);
            }
        }

        /// <summary>显式读取历史或索引，并以精确所选版本协调导出、签发和部署导航。</summary>
        private void DrawHistory(ResourceProject project, string app, float width, Action<Func<string>> queue, Action<Func<string>> run)
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("读取已登记版本", ResourceWorkbenchGui.Button, GUILayout.Width(140)))
            {
                queue(() => Execute(() =>
            {
                var read = ResourcePublisher.ReadHistory(project, EditorUserBuildSettings.activeBuildTarget);
                ApplyHistory(read);
                return "已读取并验证 " + read.Count + " 个已登记版本。未扫描其他目录、恢复事务、修改发布索引或切换运行版本。";
            }));
            }

            if (GUILayout.Button("读取当前发布索引", ResourceWorkbenchGui.Button, GUILayout.Width(152)))
            {
                queue(() => Execute(() =>
            {
                ApplyCurrent(ResourcePublisher.ReadCurrent(project, EditorUserBuildSettings.activeBuildTarget, app));
                return _current == null ? "当前平台和 App 没有已登记的当前发布版本；这不是正在运行的版本查询。" : "已读取并验证当前发布索引，版本摘要：" + _current.Catalog.CatalogDigest;
            }, true));
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.Label("已登记版本：" + (_hasReadHistory ? _releases.Count + " 个 · 读取于 " + _historyAt.ToLocalTime().ToString("HH:mm:ss") : "尚未读取") +
                "　发布索引：" + (_hasReadIndex ? _current == null ? "无当前发布" : _current.Catalog.CatalogDigest.Substring(0, 16) : "尚未读取") + (_hasReadIndex ? " · " + _indexAt.ToLocalTime().ToString("HH:mm:ss") : ""), ResourceWorkbenchGui.Secondary);
            EditorGUILayout.BeginHorizontal(ResourceWorkbenchGui.Toolbar);
            var text = ResourceWorkbenchGui.SearchField(_search, "筛选版本：摘要、App、Unity、索引状态", GUILayout.MinWidth(80));
            if (text != _search)
            {
                _search = text;
                if (_table != null)
                {
                    _table.searchString = _search;
                }
            }
            if (width < 950 && GUILayout.Button(_showCompactDetail ? "版本列表" : "所选详情", EditorStyles.toolbarButton, GUILayout.Width(76)))
            {
                _showCompactDetail = !_showCompactDetail;
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            var wide = width >= 950;
            if (wide || !_showCompactDetail)
            {
                if (_table != null && _releases.Count > 0)
                {
                    _table.OnGUI(GUILayoutUtility.GetRect(0, 100000, 100, 100000, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)));
                }
                else
                {
                    EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                    GUILayout.Label(_hasReadHistory ? "没有已登记的发布版本。先到构建页执行“构建并本地发布”。" : "点击“读取已登记版本”，然后选择要导出、签发或内置部署的版本。发布索引的当前版本不等于正在运行的版本。", ResourceWorkbenchGui.Secondary);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndVertical();
                }
            }
            var chosen = _table?.Selected;
            if (chosen != null && chosen.Identity.CatalogDigest != _selected?.CatalogDigest)
            {
                Select(chosen.Release);
                _isSelectionStale = _isHistoryStale;
            }
            if (wide || _showCompactDetail)
            {
                EditorGUILayout.BeginVertical(GUILayout.Width(wide ? width * .46f : width), GUILayout.ExpandHeight(true));
                _detailsScroll = EditorGUILayout.BeginScrollView(_detailsScroll, GUILayout.ExpandHeight(true));
                DrawErrorDetails();
                if (_selected == null)
                {
                    GUILayout.Label("从列表选择一个版本。这里只浏览已登记版本，不会改变发布索引或正在运行的资源。", ResourceWorkbenchGui.Secondary);
                }
                else
                {
                    DrawIdentity(width * (wide ? .45f : 1));
                    ResourceWorkbenchGui.Metric("交付内容", _selectedRelease.Catalog.Bundles.Count + " 个 Bundle · " + _selectedRelease.Catalog.Assets.Count + " 条资源记录");
                    _showFiles = ResourceWorkbenchGui.Disclosure(_showFiles, "交付文件与内容摘要");
                    if (_showFiles)
                    {
                        DrawFiles();
                    }

                    _showExportSettings = ResourceWorkbenchGui.Disclosure(_showExportSettings, "导出位置（未填时会提示选择）");
                    if (_showExportSettings)
                    {
                        _exportParent = ResourceWorkbenchGui.TextField("工程外现有父目录", _exportParent, true);
                        GUILayout.Label("在所选父目录下新增以完整版本摘要命名的目录，不覆盖已有内容。", ResourceWorkbenchGui.Secondary);
                    }
                    if (!_showExportSettings && !string.IsNullOrEmpty(_exportParent))
                    {
                        ResourceWorkbenchGui.Metric("已选导出父目录", Path.GetFileName(_exportParent), _exportParent);
                    }

                    using (new EditorGUI.DisabledScope(_isSelectionStale))
                    {
                        if (GUILayout.Button("导出所选版本...", ResourceWorkbenchGui.Button))
                        {
                            var identity = _selected;
                            var archive = _selectedRelease;
                            var parent = _exportParent;
                            queue(() => Execute(() =>
                            {
                                var source = identity.ResolveForArchive(archive);
                                if (string.IsNullOrEmpty(parent))
                                {
                                    parent = EditorUtility.OpenFolderPanel("工程外新导出目录的父目录", "", "");
                                    if (string.IsNullOrEmpty(parent))
                                    {
                                        return "导出已取消，未创建目录。";
                                    }
                                }
                                var output = ExportDirectory(identity, parent);
                                if (!ResourceOperationConfirmation.Confirm("导出所选完整版本", identity + "\n新增本地目录=" + output +
                                    "\n导出前重新验证完整版本。只新增目录，不覆盖、合并、同步或上传。失败时保留本次部分输出供检查。", "导出并验证", new ResourceConfirmationSummary
                                    {
                                        Fields = new[] { ("资源项目", identity.ProjectId), ("平台", identity.Platform), ("资源兼容标识（App）", identity.App),
                                            ("版本摘要", identity.CatalogDigest.Substring(0, 16)), ("新增目录", output) },
                                        Effects = new[] { "复制并验证所选版本的完整交付文件。", "只新增目标目录，不覆盖、合并、同步或上传，也不切换运行版本。" },
                                        Notice = "失败时会保留本次部分输出供检查，不自动删除或重试。"
                                    }, details:
                                    new ResourceConfirmationDetails(identity.ToString(), "只读来源=" + source.DirectoryPath + "\n新增本地目录=" + output,
                                        "导出前重新验证完整版本。只新增，不覆盖、合并、同步或上传，也不切换运行版本。失败时保留本次部分输出供检查，不自动重试或恢复。")))
                                {
                                    return "导出已取消。";
                                }

                                source = identity.ResolveForArchive(archive);
                                var watch = System.Diagnostics.Stopwatch.StartNew();
                                var bytes = ResourceEditorOperations.ExportRelease(source, output);
                                _exportParent = parent;
                                return "选定版本导出并完整验证成功：" + output + "\n复制逻辑字节=" + bytes + " / 复制及验证混合耗时=" + watch.Elapsed.TotalMilliseconds.ToString("F2") + "ms；未上传。";
                            }));
                        }
                        if (GUILayout.Button("签发所选版本...", ResourceWorkbenchGui.Button))
                        {
                            var identity = _selected;
                            run(() =>
                            {
                                ResourceReleaseSigningWindow.OpenSelected(project, _table?.Selected?.Release ?? identity.Resolve());
                                return "已打开所选版本的签发窗口；尚未读取 PFX、签名或导出，签发前会重新验证版本。";
                            });
                        }
                        if (GUILayout.Button("检查此版本的内置部署", ResourceWorkbenchGui.Button))
                        {
                            _tab = 1;
                        }
                    }
                }
                GUILayout.Label(DescribeIndex(_hasReadIndex, _isIndexStale, _hasIndexFailed, _isSelectionStale, _current?.Catalog.CatalogDigest, _selected?.CatalogDigest), ResourceWorkbenchGui.Secondary);
                if (_error == null && !_isIndexResult && !string.IsNullOrEmpty(_result))
                {
                    EditorGUILayout.HelpBox(_result, MessageType.Info);
                }

                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>区分只读部署条件检查和实际部署；确认前重取计划，正式服务执行时还会复验。</summary>
        private void DrawDeployment(ResourceProject project, float width, Action<Func<string>> queue)
        {
            _operationScroll = EditorGUILayout.BeginScrollView(_operationScroll, GUILayout.ExpandHeight(true));
            DrawErrorDetails();
            if (_selected == null)
            {
                GUILayout.Label("先到“发布版本”读取已登记版本，再选择要部署的版本。", ResourceWorkbenchGui.Secondary);
            }
            else
            {
                DrawIdentity(width);
                using (new EditorGUI.DisabledScope(_isSelectionStale))
                {
                    if (GUILayout.Button("检查部署条件（只读）", ResourceWorkbenchGui.Button, GUILayout.Width(220)))
                    {
                        var identity = _selected;
                        queue(() => Execute(() =>
                        {
                            _plan = ResourceBuiltInDeployer.Preflight(identity.Resolve());
                            _planIdentity = identity;
                            _scope = ResourceBuiltInScope.Describe(_plan);
                            return "部署条件检查完成；未创建目录、工作区或锁，也未部署。执行前会重新检查。\n部署详情：\n" + _scope;
                        }));
                    }
                }

                if (_plan != null)
                {
                    ResourceWorkbenchGui.Metric("部署检查", _plan.IsRepeat ? "相同版本已部署，执行前仍会复验" : "可以部署，执行前仍会复验");
                    ResourceWorkbenchGui.Metric("资源文件", _plan.Files.Count + " 个 · " + ResourceWorkbenchGui.Bytes(_plan.PayloadBytes), _plan.PayloadBytes + " B");
                    GUILayout.Label("涉及内置版本与控制文件、Assets 外的暂存/锁/恢复记录，以及新目录 .meta 和 Unity 导入。不会切换正在运行的资源版本。", ResourceWorkbenchGui.Secondary);
                    _showDeploymentDetails = ResourceWorkbenchGui.Disclosure(_showDeploymentDetails, "完整部署范围与准确路径");
                    if (_showDeploymentDetails)
                    {
                        ResourceWorkbenchGui.CopyLine("部署范围", _scope, width - 28);
                    }
                }
                if (_deployment != null)
                {
                    EditorGUILayout.HelpBox("最近已确认的内置部署结果（不是运行版本，本次失败时仍保留）：\n" + _deployment.RelativeDirectory +
                    "\nCatalogDigest=" + _deployment.Snapshot.Document.CatalogDigest + "\nIsRepeat=" + _deployment.IsRepeat + " / 实际复制逻辑字节=" + _deployment.CopiedBytes, MessageType.Info);
                }
            }
            if (_error == null && !string.IsNullOrEmpty(_result))
            {
                EditorGUILayout.HelpBox(_result, MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
            using (new EditorGUI.DisabledScope(_isSelectionStale || _plan == null || _selected == null || _planIdentity?.CatalogDigest != _selected.CatalogDigest))
            {
                if (ResourceWorkbenchGui.PrimaryAction("部署内置资源..."))
                {
                    var identity = _planIdentity;
                    queue(() => Execute(() =>
                    {
                        var currentPlan = ResourceBuiltInDeployer.Preflight(identity.Resolve());
                        var currentScope = ResourceBuiltInScope.Describe(currentPlan);
                        if (!ResourceOperationConfirmation.Confirm("部署内置资源", currentScope, "部署内置资源", ResourceBuiltInScope.Summarize(project, currentPlan)))
                        {
                            return "内置部署已取消。";
                        }

                        _deployment = ResourceBuiltInDeployer.Deploy(currentPlan);
                        _plan = null;
                        _planIdentity = null;
                        return "内置资源已部署并验证：" + _deployment.RelativeDirectory + "\n是否重复部署=" + _deployment.IsRepeat +
                            " / 复制逻辑字节=" + _deployment.CopiedBytes + "。未修改本地发布索引，也未切换运行版本或最近可用版本。";
                    }));
                }
            }
        }

        /// <summary>按当前项目与平台提供两类独立事务恢复及发布集合常量生成，不以历史选择推定恢复目标。</summary>
        private void DrawRecovery(ResourceProject project, string app, Action<Func<string>> queue)
        {
            _operationScroll = EditorGUILayout.BeginScrollView(_operationScroll, GUILayout.ExpandHeight(true));
            DrawErrorDetails();
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project));
            var target = EditorUserBuildSettings.activeBuildTarget;
            GUILayout.Label("恢复用于处理本项目在当前平台上的未完成事务，不以列表中的所选版本作为恢复目标。发布恢复和内置部署恢复是不同操作。", ResourceWorkbenchGui.Secondary);
            ResourceWorkbenchGui.CopyLine("项目配置 GUID", guid, 650);
            using (new EditorGUI.DisabledScope(EditorUtility.IsDirty(project)))
            {
                if (GUILayout.Button("恢复本地发布事务...", ResourceWorkbenchGui.Button))
                {
                    queue(() => Execute(() =>
                {
                    var path = Path.Combine(ResourceBuildMap.Absolute("AssetBundles/YLFramework"), project.projectId, target.ToString());
                    if (!ResourceOperationConfirmation.Confirm("恢复本地发布事务", "ProjectId=" + project.projectId + " / GUID=" + guid + "\nPlatform=" + target + " / App=" + app +
                        "\n本地发布目录=" + path + "\n根据项目登记、事务日志和发布索引恢复：确认已经提交的索引，或处理登记的暂存目录。遇到冲突时停止，不手工改写控制记录。" +
                        "\n不会恢复内置部署、删除历史或切换运行版本。提交未确认时，发布索引可能已经变化。", "恢复本地发布", new ResourceConfirmationSummary
                        {
                            Fields = new[] { ("资源项目", ProjectId: project.projectId), ("平台", target.ToString()), ("资源兼容标识（App）", app), ("恢复对象", "当前项目在此平台上的发布事务"), ("发布目录", path) },
                            Effects = new[] { "按事务记录确认已提交的索引，或处理已登记的暂存目录。", "不删除发布历史，不恢复内置部署，也不切换运行版本。" },
                            Notice = "实际处理的版本由事务记录决定，与历史列表的当前选择无关。恢复后请重新读取发布索引。"
                        }, details:
                        new ResourceConfirmationDetails("ProjectId=" + project.projectId + "\nProjectGuid=" + guid + "\nPlatform=" + target + "\nApp=" + app,
                            "本地发布目录=" + path + "\n按 owner / journal / Index 处理已登记的 stage，不扫描其他目录。",
                            "ResourcePublisher.Recover 按事务记录恢复，实际处理的版本与列表选择无关。不直接改写控制记录、不删除历史，也不切换运行版本。提交未确认时，发布索引可能已经变化。")))
                    {
                        return "本地发布恢复已取消。";
                    }

                    var recovered = ResourcePublisher.Recover(project, target, app);
                    Invalidate();
                    return recovered == null ? "发布恢复已结束，未确认当前发布版本。请重新读取已登记版本和发布索引。" : "发布恢复结果已验证，版本摘要=" + recovered.Catalog.CatalogDigest + "。请重新读取已登记版本和发布索引。";
                }));
                }

                if (GUILayout.Button("恢复内置部署事务...", ResourceWorkbenchGui.Button))
                {
                    queue(() => Execute(() =>
                {
                    var workspace = ResourceBuiltInDeployer.GetWorkspacePath(guid, target.ToString());
                    if (!ResourceOperationConfirmation.Confirm("恢复内置部署事务", "ProjectId=" + project.projectId + " / GUID=" + guid + "\nPlatform=" + target +
                        "\n部署工作目录=" + workspace + "\nylr-owner.json~ / deploy.lock / journal.json / journal.json.next / 已登记的私有 stage。" +
                        "\n确认已经完整安装的版本，或只清理本事务登记的暂存文件。必要时触发 AssetDatabase 导入；项目、内容或日志冲突时停止。" +
                        "\n事务中的版本可能不是当前列表所选版本。不会覆盖或删除旧部署，不修改发布索引，也不切换运行版本或最近可用版本。", "恢复内置部署", new ResourceConfirmationSummary
                        {
                            Fields = new[] { ("资源项目", ProjectId: project.projectId), ("平台", target.ToString()), ("恢复对象", "当前项目在此平台上的内置部署事务"), ("工作目录", workspace) },
                            Effects = new[] { "确认已完整安装的版本，或只清理此事务登记的暂存文件。必要时会触发 Unity 导入。", "不覆盖或删除旧部署，不修改本地发布索引，也不切换运行版本。" },
                            Notice = "实际处理的版本由部署事务记录决定，与历史列表的当前选择无关。"
                        }, details:
                        new ResourceConfirmationDetails("ProjectId=" + project.projectId + "\nProjectGuid=" + guid + "\nPlatform=" + target,
                            "部署工作目录=" + workspace + "\nylr-owner.json~ / deploy.lock / journal.json / journal.json.next / 已登记的私有 stage。",
                            "ResourceBuiltInDeployer.Recover 确认已完整安装的版本，或只清理登记的 stage；必要时触发 AssetDatabase 导入。实际 journal 版本可能不是列表所选版本。记录或内容冲突时停止，不覆盖旧部署、不修改发布索引，也不切换运行版本或最近可用版本。")))
                    {
                        return "内置部署恢复已取消。";
                    }

                    var recovered = ResourceBuiltInDeployer.Recover(project, target);
                    _deployment = recovered;
                    Invalidate();
                    return recovered == null ? "内置部署恢复已结束，未确认新的完整安装版本。请重新检查部署条件后再部署；此结果不表示发布历史为空。" :
                        "内置部署恢复已确认完整安装，版本摘要=" + recovered.Snapshot.Document.CatalogDigest + "。未切换运行版本。";
                }));
                }
            }
            GUILayout.Space(12);
            GUILayout.Label("生成资源地址常量（不重新发布）", EditorStyles.boldLabel);
            if (GUILayout.Button("签发发布索引当前版本（不是所选历史）...", ResourceWorkbenchGui.Button))
            {
                ResourceReleaseSigningWindow.Open(project, app);
            }

            _generationDirectory = ResourceWorkbenchGui.TextField("常量目标目录", _generationDirectory, true);
            if (GUILayout.Button("检查已发布项目集合并生成...", ResourceWorkbenchGui.Button))
            {
                var directory = _generationDirectory;
                queue(() => Execute(() =>
                {
                    var catalogs = ResourceEditorOperations.PublishedCatalogs(app);
                    if (!ResourceOperationConfirmation.Confirm("确认生成资源地址常量", "App=" + app + "\n全部匹配的已登记项目=" + string.Join(", ", catalogs.Select(x => x.ProjectId)) +
                        "\n目标=" + directory + "\n检查生成记录与程序集后生成常量，保留现有 .meta。源码更新后需等待 Unity 编译；不重新发布资源。", "生成常量", new ResourceConfirmationSummary
                        {
                            Fields = new[] { ("平台", target.ToString()), ("资源兼容标识（App）", app), ("包含项目", string.Join("、", catalogs.Select(x => x.ProjectId))), ("源码目标目录", directory) },
                            Effects = new[] { "按当前平台和 App 匹配的全部已发布项目生成地址常量，保留现有 .meta。", "不重新构建、发布或部署资源；不会覆盖未知或手写文件。" },
                            Notice = "生成源码不代表 Unity 编译已通过，请等待并检查编译结果。"
                        }, details:
                        new ResourceConfirmationDetails("App=" + app + "\n全部匹配的项目 / CatalogDigest：\n" + string.Join("\n", catalogs.Select(x => x.ProjectId + " / " + x.CatalogDigest)),
                            "常量目标=" + directory + "\n保留现有 .meta。",
                            "检查现有生成记录和程序集后生成；不会覆盖未知或手写文件。源码更新不代表 Unity 编译已通过。不重新构建、发布、部署或切换运行版本，也不自动恢复或重发。")))
                    {
                        return "生成已取消。";
                    }

                    var changed = ResourceCodeGenerator.GenerateReferences(catalogs, directory);
                    if (changed)
                    {
                        AssetDatabase.Refresh();
                    }

                    return changed ? "常量源码已更新；待Unity实际编译。未重发资源。" : "常量源码未变化，未重写或重发资源。";
                }));
            }
            if (_error == null && _result != null)
            {
                EditorGUILayout.HelpBox(_result, MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>展示所选快照的完整版本身份，短摘要仅作概览而不参与匹配。</summary>
        private void DrawIdentity(float width)
        {
            GUILayout.Label("所选版本", EditorStyles.boldLabel);
            ResourceWorkbenchGui.Metric("版本摘要", _selected.CatalogDigest.Substring(0, 16), _selected.CatalogDigest);
            ResourceWorkbenchGui.Metric("平台", _selected.Platform);
            ResourceWorkbenchGui.Metric("资源兼容标识（App）", _selected.App);
            _showTechnical = ResourceWorkbenchGui.Disclosure(_showTechnical, "技术详情：完整版本与路径");
            if (!_showTechnical)
            {
                return;
            }

            ResourceWorkbenchGui.CopyLine("ProjectId / GUID", _selected.ProjectId + " / " + _selected.ProjectGuid, Math.Max(200, width - 20));
            ResourceWorkbenchGui.CopyLine("完整 CatalogDigest", _selected.CatalogDigest, Math.Max(200, width - 20));
            GUILayout.Label(_selected.ToString(), ResourceWorkbenchGui.Secondary);
            GUILayout.Label(_selectedFacts, ResourceWorkbenchGui.Secondary);
        }
        /// <summary>虚拟化显示所选版本的文件量和摘要，复用选择时投影的原始事实。</summary>
        private void DrawFiles()
        {
            var rect = GUILayoutUtility.GetRect(80, 144, GUILayout.ExpandWidth(true));
            _filesScroll = GUI.BeginScrollView(rect, _filesScroll, new Rect(0, 0, Math.Max(20, rect.width - 18), Math.Max(144, _files.Length * 24)));
            var first = Math.Max(0, (int)(_filesScroll.y / 24));
            for (var i = first; i < Math.Min(_files.Length, first + 7); i++)
            {
                GUI.Label(new Rect(3, i * 24, Math.Max(20, rect.width - 24), 22), new GUIContent(_files[i].Name + " / " + _files[i].Size, _files[i].Name + "\n" + _files[i].Sha));
            }

            GUI.EndScrollView();
        }
        /// <summary>按读取、失败和过期状态描述索引关系；未知时不猜测当前版本或运行版本。</summary>
        internal static string DescribeIndex(bool read, bool stale, bool failed, bool selectedStale, string currentDigest, string selectedDigest)
        {
            if (failed)
            {
                return "发布索引读取失败；旧快照不能代表本次读取结果。";
            }

            if (!read)
            {
                return "发布索引尚未读取；所选版本与当前发布索引的关系未知。";
            }

            if (stale)
            {
                return "发布索引结果已过期，请重新读取。";
            }

            if (selectedStale)
            {
                return "所选版本需要重新验证，暂不判断它与发布索引的关系。";
            }

            if (currentDigest == null)
            {
                return "本次读取没有当前发布版本；这不是正在运行的版本查询。";
            }

            if (selectedDigest == null)
            {
                return "发布索引已读取，选择一个版本后可比较；发布索引不代表正在运行的版本。";
            }

            return selectedDigest == currentDigest ? "发布索引当前指向所选版本；这不代表应用正在使用它。" :
                "发布索引当前未指向所选版本；这不代表应用正在使用哪个版本。";
        }

        /// <summary>登记操作输出；异常时标记旧快照过期并继续上抛，不承诺撤销已经发生的 IO。</summary>
        private string Execute(Func<string> operation, bool readingIndex = false)
        {
            try
            {
                var text = operation();
                _error = null;
                _result = text;
                _isIndexResult = readingIndex;
                return text;
            }
            catch (Exception failure)
            {
                if (readingIndex)
                {
                    _hasIndexFailed = true;
                }

                Invalidate();
                _error = failure;
                throw;
            }
        }

        /// <summary>按需展示本次交付操作的原始异常，旧成功结果仍与失败提示分开保留。</summary>
        private void DrawErrorDetails()
        {
            if (_error == null)
            {
                return;
            }

            _showErrorDetails = ResourceWorkbenchGui.Disclosure(_showErrorDetails, "交付操作错误详情");
            if (_showErrorDetails)
            {
                GUILayout.Label(ResourceEditorText.FailureDetails(_error), ResourceWorkbenchGui.Secondary);
            }
        }

        /// <summary>将绝对父路径与完整版本摘要组合为导出路径；工程外归属、链接和占用由导出服务校验。</summary>
        internal static string ExportDirectory(ResourceReleaseIdentity identity, string parent)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            if (string.IsNullOrEmpty(parent) || !Path.IsPathFullyQualified(parent))
            {
                throw new InvalidOperationException("请选择明确工程外现有父目录的绝对路径。");
            }

            return Path.Combine(Path.GetFullPath(parent), identity.CatalogDigest);
        }
    }
}
