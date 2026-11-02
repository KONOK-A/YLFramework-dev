using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>读取现有资源会话和磁盘查询快照；采样不初始化服务，显式历史设置按钮才改变历史容量。</summary>
    internal sealed class ResourceDiagnosticsView : IDisposable
    {
        private ResourceDiagnosticsReport _report;
        // 只识别上次采样的实例，不因窗口保留诊断而延长业务服务的存活期。
        private WeakReference<ResourceSystem> _sampledSystem;
        private bool _automatic;
        private bool _isVisible;
        private int _panel;
        private bool _showCompactDetail;
        private int _subjectTag;
        private string _context;
        private readonly ResourceStoreQueryView _store = new();
        private readonly ResourceGuideView _guide = new();
        private double _nextSample;
        private int _section;
        private int _selected;
        private string _filter = "";
        private string _error;
        private Exception _failure;
        private bool _showErrorDetails;
        private Vector2 _listScroll;
        private Vector2 _detailScroll;
        private Vector2 _runtimePageScroll;
        private (string Title, string Detail)[] _rows = Array.Empty<(string, string)>();
        private Action[] _rowDetails = Array.Empty<Action>();
        private bool _areRowsDirty;
        private bool _showRuntimeOptions;
        private bool _showTechnicalDetail;
        private GUIStyle _rowStyle;
        private bool _isRowDark;
        private static readonly string[] _sections = { "项目", "资源", "Bundle", "实例缓存", "场景", "更新与恢复", "请求历史", "数据预算", "会话计数" };

        /// <summary>分发运行诊断、磁盘查询和入门导航，切换来源时停止后台诊断工作。</summary>
        internal void Draw(float width = 1100, ResourceEditorBuildReport build = null, Action showBuild = null, Action<int> navigate = null)
        {
            var next = ResourceWorkbenchGui.Navigation(_panel, new[] { "运行诊断", "磁盘缓存查询（只读）", "开始使用" });
            if (next != _panel)
            {
                _automatic = false;
                _store.Stop("切换诊断来源");
                _panel = next;
            }
            if (_panel == 0)
            {
                DrawRuntime(width);
            }
            else if (_panel == 1)
            {
                _store.Draw(width);
            }
            else
            {
                _guide.Draw(build, showBuild, navigate);
            }
        }
        /// <summary>停止自动采样和磁盘查询后切到入门导航，保留已有结果。</summary>
        internal void ShowGuide()
        {
            _automatic = false;
            _store.Stop("打开开始使用");
            _panel = 2;
        }
        /// <summary>更新页面可见性，隐藏时停止自动采样及查询，再显示时不自动重开。</summary>
        internal void SetVisible(bool value)
        {
            if (!value)
            {
                _automatic = false;
                _store.Stop("离页、关闭或转换");
            }
            _isVisible = value;
        }
        /// <summary>工作台项目切换时暂停诊断工作；已取得的运行报告仍是全会话而非单项目数据。</summary>
        internal void SetContext(string value)
        {
            if (_context == value)
            {
                return;
            }

            _automatic = false;
            _store.Stop("工作台所选资源项目变化");
            _context = value;
        }
        /// <summary>以弱引用和引用身份确认当前服务仍是上次采样对象，不用项目标识猜测会话连续性。</summary>
        private bool SameSubject(ResourceSystem value) => _sampledSystem != null && _sampledSystem.TryGetTarget(out var sampled) && ReferenceEquals(value, sampled);

        /// <summary>按当前宽度选择运行诊断的双栏或紧凑布局。</summary>
        private void DrawRuntime(float width)
        {
            DrawRuntimeContent(width, width < 950);
        }

        /// <summary>展示同次会话采样，提供显式采样和历史容量设置；历史开关会清空原缓冲而非补录请求。</summary>
        private void DrawRuntimeContent(float width, bool compact)
        {
            var system = ResourceSystem.Current;
            EditorGUILayout.BeginHorizontal(ResourceWorkbenchGui.Toolbar);
            using (new EditorGUI.DisabledScope(system == null || !system.IsAvailable))
            {
                if (GUILayout.Button("采样 / 刷新", ResourceWorkbenchGui.ToolbarButton, GUILayout.Width(96)))
                {
                    Capture();
                }

                var enabled = ResourceWorkbenchGui.ToolbarToggle(_automatic, "自动采样", 96, "每5秒采样；默认关闭，离页、隐藏、关闭或运行状态转换后暂停，不自动重开。");
                if (enabled != _automatic)
                {
                    _automatic = enabled;
                    if (_automatic)
                    {
                        Capture();
                    }
                }
            }
            GUILayout.FlexibleSpace();
            _showRuntimeOptions = ResourceWorkbenchGui.ToolbarToggle(_showRuntimeOptions, "采样与历史设置", 132);
            EditorGUILayout.EndHorizontal();
            if (_showRuntimeOptions || _report == null)
            {
                _runtimePageScroll = EditorGUILayout.BeginScrollView(_runtimePageScroll, GUILayout.MinHeight(0), GUILayout.ExpandHeight(true));
                DrawError();
                if (system == null || !system.IsAvailable)
                {
                    GUILayout.Label("ResourceSystem 尚未初始化或当前不可用。由应用完成初始化后，再点击采样。", ResourceWorkbenchGui.Secondary);
                }
                else if (_report == null)
                {
                    GUILayout.Label("点击“采样 / 刷新”查看运行会话。采样不会加载资源、激活版本或改变资源使用权。", ResourceWorkbenchGui.Secondary);
                }

                GUILayout.Label("统计范围为整个 ResourceSystem 会话，不随工作台当前项目筛选。", ResourceWorkbenchGui.Secondary);
                if (_showRuntimeOptions)
                {
                    ResourceWorkbenchGui.Metric("自动采样", _automatic ? "已开启，每5秒" : "已关闭", "每5秒采样；离开页面、隐藏或关闭窗口、切换运行状态后暂停。默认关闭。");
                    ResourceWorkbenchGui.Metric("请求历史容量", _report?.HistoryCapacity.ToString() ?? "尚未采样", "HistoryCapacity，来自最后一次采样；只读查看不更改应用的历史设置。");
                    using (new EditorGUI.DisabledScope(system == null || !system.IsAvailable))
                    {
                        EditorGUILayout.BeginHorizontal();
                        if (GUILayout.Button("启用历史（128条）", ResourceWorkbenchGui.Button))
                        {
                            system.ConfigureDiagnosticsHistory(128);
                            Capture();
                        }
                        if (GUILayout.Button("关闭历史", ResourceWorkbenchGui.Button))
                        {
                            system.ConfigureDiagnosticsHistory(0);
                            Capture();
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    GUILayout.Label("显式启用或关闭历史会清空原缓冲，不回填旧请求。离页或关闭工作台只暂停采样，不改变应用历史容量，也不取消业务请求。", ResourceWorkbenchGui.Secondary);
                    if (_report != null)
                    {
                        ResourceWorkbenchGui.Metric("采样时间（UTC）", _report.CapturedAtUtc.ToString("u", CultureInfo.InvariantCulture), "CapturedAtUtc，完整报告中的同次采样时间。");
                        ResourceWorkbenchGui.Metric("服务状态", RuntimeState(_report.State), "State：来自本次采样，不是实时状态。");
                        ResourceWorkbenchGui.Metric("窗口采样标记", "#" + _subjectTag, "仅供此窗口区分不同 ResourceSystem 实例，不是 SessionId。");
                    }
                }
                EditorGUILayout.EndScrollView();
                return;
            }
            var summary = _report.Summary;
            var status = (system == null || !system.IsAvailable || !SameSubject(system) ? "上次采样" : "会话采样") + "  " + _report.CapturedAtUtc.ToString("HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) + "  |  " + RuntimeState(_report.State);
            if (!string.IsNullOrEmpty(_error))
            {
                status = _error;
            }

            EditorGUI.LabelField(EditorGUILayout.GetControlRect(false, ResourceWorkbenchGui.RowHeight), new GUIContent(status,
                 _error + "\n采样时间（CapturedAtUtc）：" + _report.CapturedAtUtc.ToString("u", CultureInfo.InvariantCulture) + "\n全会话统计，不随工作台项目筛选。\n资源记录：" + summary.RecordCount + "\nBundle 记录：" + summary.BundleRecordCount + "\n租约数：" + summary.LeaseCount + "\n历史容量：" + _report.HistoryCapacity), ResourceWorkbenchGui.Secondary);
            EditorGUILayout.BeginHorizontal(ResourceWorkbenchGui.Toolbar);
            var next = EditorGUILayout.Popup(_section, _sections, ResourceWorkbenchGui.Popup, GUILayout.Width(132), GUILayout.Height(ResourceWorkbenchGui.FieldHeight));
            if (next != _section)
            {
                _section = next;
                _selected = 0;
                _listScroll = Vector2.zero;
                _detailScroll = Vector2.zero;
                _showTechnicalDetail = false;
                _areRowsDirty = true;
            }
            GUILayout.Label(new GUIContent("筛选", "筛选同次报告中的身份、路径、原因或技术字段。"), GUILayout.Width(32), GUILayout.Height(ResourceWorkbenchGui.FieldHeight));
            EditorGUI.BeginChangeCheck();
            _filter = EditorGUI.TextField(EditorGUILayout.GetControlRect(false, ResourceWorkbenchGui.FieldHeight, ResourceWorkbenchGui.Field), _filter, ResourceWorkbenchGui.Field);
            if (EditorGUI.EndChangeCheck())
            {
                _areRowsDirty = true;
                _selected = 0;
                _listScroll = Vector2.zero;
            }
            if (_areRowsDirty)
            {
                BuildRows();
            }

            GUILayout.Label(_rows.Length + " 条", GUILayout.Width(56), GUILayout.Height(ResourceWorkbenchGui.FieldHeight));
            if (compact)
            {
                _showCompactDetail = GUILayout.Toggle(_showCompactDetail, "详情", ResourceWorkbenchGui.ToolbarButton, GUILayout.Width(60));
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            if (!compact || !_showCompactDetail)
            {
                var rect = GUILayoutUtility.GetRect(0, 100000, 0, 100000, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
                _listScroll = GUI.BeginScrollView(rect, _listScroll, new Rect(0, 0, Mathf.Max(0, rect.width - 20), Mathf.Max(0, _rows.Length * ResourceWorkbenchGui.RowHeight)));
                if (_rowStyle == null || _isRowDark != EditorGUIUtility.isProSkin)
                {
                    _isRowDark = EditorGUIUtility.isProSkin;
                    _rowStyle = new GUIStyle(EditorStyles.miniButton) { fixedHeight = ResourceWorkbenchGui.RowHeight, alignment = TextAnchor.MiddleLeft };
                }
                var first = Mathf.Max(0, (int)(_listScroll.y / ResourceWorkbenchGui.RowHeight));
                for (var i = first; i < Math.Min(_rows.Length, first + Mathf.CeilToInt(rect.height / ResourceWorkbenchGui.RowHeight) + 2); i++)
                {
                    if (GUI.Toggle(new Rect(4, i * ResourceWorkbenchGui.RowHeight, Mathf.Max(0, rect.width - 28), ResourceWorkbenchGui.RowHeight), _selected == i, new GUIContent(_rows[i].Title, _rows[i].Title), _rowStyle) && _selected != i)
                    {
                        _selected = i;
                        _detailScroll = Vector2.zero;
                    }
                }

                if (_rows.Length == 0)
                {
                    GUI.Label(new Rect(8, 4, Mathf.Max(0, rect.width - 28), ResourceWorkbenchGui.RowHeight), EmptyMessage(), ResourceWorkbenchGui.Secondary);
                }

                GUI.EndScrollView();
            }
            if (!compact || _showCompactDetail)
            {
                EditorGUILayout.BeginVertical(!compact ? new[] { GUILayout.Width(width * .44f), GUILayout.MinHeight(0), GUILayout.ExpandHeight(true) } : new[] { GUILayout.MinWidth(0), GUILayout.ExpandWidth(true), GUILayout.MinHeight(0), GUILayout.ExpandHeight(true) });
                _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll, GUILayout.MinHeight(0), GUILayout.ExpandHeight(true));
                DrawError();
                if (_rows.Length == 0)
                {
                    GUILayout.Label(EmptyMessage(), ResourceWorkbenchGui.Secondary);
                }
                else
                {
                    _selected = Mathf.Clamp(_selected, 0, _rows.Length - 1);
                    GUILayout.Label(_rows[_selected].Title, EditorStyles.wordWrappedLabel);
                    if (_selected < _rowDetails.Length)
                    {
                        _rowDetails[_selected]?.Invoke();
                    }

                    _showTechnicalDetail = ResourceWorkbenchGui.Disclosure(_showTechnicalDetail, "技术字段与完整身份");
                    if (_showTechnicalDetail)
                    {
                        EditorGUILayout.SelectableLabel(_rows[_selected].Detail, EditorStyles.wordWrappedLabel,
                            GUILayout.Height(Mathf.Max(ResourceWorkbenchGui.RowHeight, EditorStyles.wordWrappedLabel.CalcHeight(new GUIContent(_rows[_selected].Detail), Math.Max(240, !compact ? width * .44f - 44 : width - 44)))));
                    }
                }
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>区分历史未启用与本次报告没有匹配项，避免将缺失历史解释为没有请求。</summary>
        private string EmptyMessage() => _section == 6 && _report.HistoryCapacity == 0 ? "请求历史未启用；可在采样与历史设置中开启。" : "本次快照没有匹配记录。";

        /// <summary>将诊断字段转换为指标文本，缺失值明确保留为未知。</summary>
        private static void Metric(string label, object value, string help) => ResourceWorkbenchGui.Metric(label, value?.ToString() ?? "未知", help);
        /// <summary>为报告中的确定布尔值提供统一显示，不推断缺失字段。</summary>
        private static string YesNo(bool value) => value ? "是" : "否";

        /// <summary>从当前可用服务读取一次报告并记录采样对象，失败时保留旧报告，不主动初始化服务。</summary>
        private void Capture()
        {
            var system = ResourceSystem.Current;
            if (system == null || !system.IsAvailable)
            {
                _failure = null;
                _error = "服务当前不可用，未执行初始化或扫描。";
                return;
            }
            try
            {
                if (!SameSubject(system))
                {
                    _subjectTag++;
                }
                _report = system.GetDiagnosticsReport();
                _sampledSystem = new WeakReference<ResourceSystem>(system);
                _error = null;
                _failure = null;
                _areRowsDirty = true;
                _nextSample = EditorApplication.timeSinceStartup + 5;
            }
            catch (Exception exception)
            {
                _failure = exception;
                _error = "采样未完成，请查看错误详情；当前仍保留上次结果。";
            }
        }

        /// <summary>展示采样失败提示及可选原始异常，不覆盖原快照中的业务状态。</summary>
        private void DrawError()
        {
            if (string.IsNullOrEmpty(_error))
            {
                return;
            }

            EditorGUILayout.HelpBox(_error, MessageType.Error);
            if (_failure == null)
            {
                return;
            }

            _showErrorDetails = ResourceWorkbenchGui.Disclosure(_showErrorDetails, "采样错误详情");
            if (_showErrorDetails)
            {
                GUILayout.Label(ResourceEditorText.FailureDetails(_failure), ResourceWorkbenchGui.Secondary);
            }
        }

        /// <summary>仅在可见时推进磁盘查询或每五秒采样；服务实例变化后关闭自动采样。</summary>
        internal bool Tick()
        {
            if (!_isVisible)
            {
                return false;
            }

            if (_panel == 1)
            {
                return _store.Tick();
            }

            if (_sampledSystem != null && !SameSubject(ResourceSystem.Current))
            {
                _automatic = false;
                return false;
            }
            if (_panel != 0 || !_automatic || EditorApplication.timeSinceStartup < _nextSample)
            {
                return false;
            }

            _nextSample = EditorApplication.timeSinceStartup + 5;
            if (ResourceSystem.Current == null || !ResourceSystem.Current.IsAvailable)
            {
                return false;
            }

            Capture();
            return true;
        }

        /// <summary>从同一报告投影并筛选分类行和详情，分开呈现逻辑字节、预算、声明量与进程计数。</summary>
        private void BuildRows()
        {
            var result = new List<(string, string)>();
            var details = new List<Action>();
            // 标题、技术字段和结构化详情保持同次报告来源，过滤只决定可见行。
            void Add(string title, string detail, Action draw = null)
            {
                if (_filter.Length == 0 || title.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0 || detail.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.Add((title, detail));
                    details.Add(draw);
                }
            }
            switch (_section)
            {
                case 0:
                    foreach (var value in _report.Projects)
                    {
                        Add(value.ProjectId + "  [" + (value.IsActivationBlocked ? "激活受阻" : "已挂载") + "]",
                              "Project: " + value.ProjectId + "\nSnapshot: " + value.SnapshotId + "\nCatalogDigest: " + (value.CatalogDigest ?? "内存资源目录") +
                              "\nBuildId: " + (value.BuildId ?? "不适用") + "\nSchema: " + (value.SchemaVersion?.ToString() ?? "不适用") +
                              "\nPlatform: " + (value.Platform ?? "不适用") + "\nUnity: " + (value.UnityCompatibility ?? "不适用") + "\nApp: " + (value.AppContentCompatibilityId ?? "不适用") +
                             "\nActivationBlocked: " + value.IsActivationBlocked + "\nSources: " + string.Join(", ", value.Sources), () =>
                             {
                                 Metric("资源项目", value.ProjectId, "ProjectId，同次报告中的挂载项目，不是工作台当前项目筛选。");
                                 Metric("激活受阻", YesNo(value.IsActivationBlocked), "ActivationBlocked：该项目是否阻止版本激活。");
                                 Metric("构建版本", value.BuildId ?? "不适用", "BuildId，内存目录可能没有交付版本。");
                                 Metric("目标平台", value.Platform ?? "不适用", "Platform：本次采样中的项目平台。");
                                 Metric("应用兼容标识", value.AppContentCompatibilityId ?? "不适用", "AppContentCompatibilityId：挂载版本的资源兼容标识。");
                                 Metric("资源来源", string.Join("、", value.Sources.Select(SourceName)), "Sources：本项目使用的资源加载来源。");
                             });
                    }

                    break;
                case 1:
                    foreach (var value in _report.Assets)
                    {
                        Add(value.Address + "  [" + (value.IsLoading ? "加载中" : value.IsImageIdle ? "图片闲置" : "就绪") + "]",
                            "Canonical: " + value.Address + "\nAssetId: " + value.AssetId + "\nSnapshot: " + value.SnapshotId + "\nCatalogDigest: " + value.CatalogDigest +
                            "\nSource: " + value.Source + "\nLocator: " + value.Locator + "\nType: " + value.DeclaredType?.FullName +
                             "\nProducer: " + value.IsLoading + " / progress=" + value.Progress.ToString("P0") + " / cancellation=" + value.CancellationRequested +
                             "\nImageQueued=" + value.IsQueued + " / ImageCacheEligible=" + value.IsImageCacheEligible +
                            "\nLeases / instance pins / waiters: " + value.LeaseCount + " / " + value.InstancePins + " / " + value.WaiterCount +
                            "\nObject alive: " + value.IsObjectAlive + " / instanceID=" + value.ObjectInstanceId +
                            "\nImage idle: " + value.IsImageIdle + " / estimate bytes=" + value.ImageBytes + " / idleSince=" + value.IdleSince +
                              "\nOwners:\n" + string.Join("\n", value.Owners.Select(x => "  " + (x.IsLabeled ? x.OwnerLabel : "未知（未标记租约）") + " | " + x.Address + " | requested=" + x.RequestedAddress + " | IsLabeled=" + x.IsLabeled + " | IsInstancePin=" + x.IsInstancePin)), () =>
                              {
                                  Metric("资源地址", value.Address, "Address，规范地址；RequestedAddress 见完整技术字段。");
                                  Metric("资源来源", SourceName(value.Source), "Source：资源的加载来源。");
                                  Metric("加载进度", value.Progress.ToString("P0"), "Progress，来自同次采样。");
                                  Metric("持有租约", value.LeaseCount, "LeaseCount：实际记录的租约数量，不推测未标记的持有者。");
                                  Metric("实例固定 / 等待者", value.InstancePins + " / " + value.WaiterCount, "InstancePins / WaiterCount：实例固定引用数与等待加载结果的请求数。");
                                  Metric("Unity对象存活", YesNo(value.IsObjectAlive), "ObjectAlive，采样时的 Unity 对象存活状态。");
                                  Metric("图片闲置", YesNo(value.IsImageIdle), "IsImageIdle：图片是否已进入闲置缓存。");
                                  Metric("图片缓存估算", ResourceWorkbenchGui.Bytes(value.ImageBytes), "ImageBytes：图片缓存估计值，不是原生内存测量，不与交付字节相加为进程总内存。");
                                  Metric("持有者", value.Owners.Count == 0 ? "无记录" : string.Join("\n", value.Owners.Select(x => (x.IsLabeled ? x.OwnerLabel : "未知（未标记租约）") + (x.IsInstancePin ? " / 实例固定" : ""))), "Owners：未标记的持有者显示为未知；完整地址见技术字段。");
                              });
                    }

                    break;
                case 2:
                    foreach (var value in _report.Bundles)
                    {
                        Add(value.BundleId + "  [" + StateName(value.State) + "]",
                            "Project: " + value.ProjectId + "\nCatalogDigest: " + value.CatalogDigest + "\nBundle: " + value.BundleId + "\nNative: " + value.NativeName +
                             "\nState: " + value.State + " / native pending=" + value.IsNativePending + " / object alive=" + value.IsObjectAlive +
                             "\nContentWorkPending=" + value.IsContentWorkPending + " / IsEncrypted=" + value.IsEncrypted + " / ContentFaulted=" + value.IsContentFaulted + " / CleanupFailed=" + value.HasContentCleanupFailed + " / Progress=" + value.Progress +
                            "\nRootPins / DependencyPins / Waiters: " + value.RootPins + " / " + value.DependencyPins + " / " + value.WaiterCount +
                            "\nFileBytes (compressed): " + value.FileBytes + "\n为何不能卸载:\n" + string.Join("\n", value.RetentionReasons.Select(x =>
                                "  " + x.Kind + " ×" + x.Count + " | " + (x.OwnerLabel ?? "未知（未标记）") + " | " + x.Address + " | parent=" + x.ParentBundleId + "\n  " + x.Detail)) +
                             (value.Cause == null ? "" : "\nCause: " + value.Cause), () =>
                             {
                                 Metric("所属项目", value.ProjectId, "ProjectId：此 Bundle 所属的资源项目。");
                                 Metric("Bundle 状态", StateName(value.State), "State：来自本次采样的加载状态。");
                                 Metric("根固定 / 依赖固定", value.RootPins + " / " + value.DependencyPins, "RootPins / DependencyPins，真实图根及依赖入边数量，不把 native pending 计入 RootPins。");
                                 Metric("等待者", value.WaiterCount, "WaiterCount：仍在等待此包结果的请求数量。");
                                 Metric("交付文件字节", ResourceWorkbenchGui.Bytes(value.FileBytes), "FileBytes：Catalog 中的交付文件长度，可能为密文，不是原生内存。");
                                 Metric("原生操作未完成", YesNo(value.IsNativePending), "NativePending：Unity 原生加载是否尚未结束。");
                                 Metric("内容处理未完成", YesNo(value.IsContentWorkPending), "ContentWorkPending：验证、读取或解密等工作是否尚未结束。");
                                 Metric("保留原因", value.RetentionReasons.Count == 0 ? "无记录" : string.Join("\n", value.RetentionReasons.Select(x => RetentionName(x.Kind) + " ×" + x.Count + " / " + (x.OwnerLabel ?? "未标记持有者"))), "RetentionReasons：完整持有者、资源地址、父 Bundle 和原始原因见技术字段；原生操作未完成项的计数为0，不计入固定引用数量。");
                                 if (value.Cause != null)
                                 {
                                     Metric("异常原因", "已记录异常，请展开技术详情", "Cause：技术字段保留原始异常说明。");
                                 }
                             });
                    }

                    break;
                case 3:
                    foreach (var value in _report.InstanceCaches)
                    {
                        Add(value.Address + (value.IsRetired ? "  [已退役]" : "  [实例缓存]"),
                            "Address: " + value.Address + "\nSnapshot: " + value.SnapshotId + "\nCatalogDigest: " + value.CatalogDigest + "\nPolicy: " + value.Policy +
                            "\nScene handle: " + value.SceneHandle + " / retired=" + value.IsRetired + "\nActive / Idle / Destroy pending: " + value.ActiveCount + " / " + value.IdleCount + " / " + value.DestroyPendingCount +
                             "\nDead awaiting prune: " + value.DeadAwaitingPruneCount + "\nTransitions / Operations: " + value.TransitionCount + " / " + value.OperationCount, () =>
                             {
                                 Metric("资源地址", value.Address, "Address：缓存对应的资源地址。");
                                 Metric("缓存策略", value.Policy == InstanceCachePolicy.SceneScoped ? "随场景释放" : "跨场景保留", "Policy：实例缓存的生命周期策略。");
                                 Metric("场景句柄", value.SceneHandle, "SceneHandle：缓存关联的场景句柄。");
                                 Metric("已退役", YesNo(value.IsRetired), "IsRetired：缓存是否已停止接受新实例。");
                                 Metric("活动 / 闲置实例", value.ActiveCount + " / " + value.IdleCount, "ActiveCount / IdleCount：正在使用的实例数与缓存中的闲置实例数。");
                                 Metric("等待销毁", value.DestroyPendingCount, "DestroyPendingCount：已安排销毁、尚未完成的实例数。");
                                 Metric("失活待清理", value.DeadAwaitingPruneCount, "DeadAwaitingPruneCount：已失活、等待清理记录的实例数。");
                                 Metric("转换 / 操作", value.TransitionCount + " / " + value.OperationCount, "TransitionCount / OperationCount：缓存记录的状态转换数与操作数。");
                             });
                    }

                    break;
                case 4:
                    foreach (var value in _report.Scenes)
                    {
                        Add(value.Address + "  [" + StateName(value.State) + "]",
                            "Address: " + value.Address + "\nPath: " + value.Path + "\nSnapshot: " + value.SnapshotId + "\nCatalogDigest: " + value.CatalogDigest +
                             "\nHandle: " + (value.Handle?.ToString() ?? "不适用") + " / sequence=" + value.LoadSequence + "\nState: " + value.State +
                            "\nNative pending: " + value.IsNativePending + " / loaded=" + value.IsLoaded + " / ambiguous=" + value.IsAmbiguous +
                              "\nWaiting activation: " + value.IsWaitingForActivation + " / request=" + (value.RequestStatus?.ToString() ?? "不适用") +
                              "\nHasRecord=" + value.HasRecord + " / NativeStarted=" + value.HasNativeStarted + " / NativeCompleted=" + value.IsNativeCompleted, () =>
                              {
                                  Metric("场景地址", value.Address, "Address：本次采样中的场景地址。");
                                  Metric("场景路径", value.Path, "Path：场景文件路径。");
                                  Metric("状态", StateName(value.State), "State：来自本次采样的场景状态。");
                                  Metric("场景句柄", value.Handle?.ToString() ?? "不适用", "Handle，未知或不适用不补0。");
                                  Metric("已加载", YesNo(value.IsLoaded), "IsLoaded：采样时场景是否已加载。");
                                  Metric("等待激活", YesNo(value.IsWaitingForActivation), "WaitingForActivation：场景是否正在等待激活。");
                                  Metric("原生操作未完成", YesNo(value.IsNativePending), "NativePending：Unity 场景加载或卸载是否尚未结束。");
                                  Metric("场景匹配有歧义", YesNo(value.IsAmbiguous), "IsAmbiguous：是否无法唯一确认对应的场景实例。");
                                  Metric("请求状态", ResourceEditorText.RequestStatus(value.RequestStatus), "RequestStatus：此场景关联的请求状态。");
                              });
                    }

                    break;
                case 5:
                    foreach (var value in _report.Updates)
                    {
                        Add(StateName(value.Phase) + "  " + value.Progress.ToString("P0"),
                            "Phase: " + value.Phase + "\nWaiters: " + value.WaiterCount + "\nProgress: " + value.Progress.ToString("P1") +
                            "\nTransferred / expected bytes: " + (value.TransferredBytes?.ToString() ?? "未测 / 不适用") + " / " + (value.ExpectedBytes?.ToString() ?? "未知") +
                            "\nRetries: " + (value.RetryCount?.ToString() ?? "不适用") + " / waiting=" + value.IsWaitingToRetry +
                             "\nDuration ms: " + (value.DurationMilliseconds?.ToString("F2") ?? "未测 / 不适用") + "\nActivation projects: " + string.Join(", ", value.ActivationProjects), () =>
                             {
                                 Metric("更新阶段", StateName(value.Phase), "Phase：本次采样中的更新阶段。");
                                 Metric("进度", value.Progress.ToString("P1"), "Progress：本次采样记录的进度。");
                                 Metric("等待者", value.WaiterCount, "WaiterCount：等待更新结果的请求数量。");
                                 Metric("已传输 / 预期字节", ResourceWorkbenchGui.Bytes(value.TransferredBytes) + " / " + ResourceWorkbenchGui.Bytes(value.ExpectedBytes), "TransferredBytes / ExpectedBytes，未测或不适用保持未知，不补0。");
                                 Metric("重试次数", value.RetryCount?.ToString() ?? "不适用", "RetryCount：当前传输已进行的重试次数。");
                                 Metric("等待重试", YesNo(value.IsWaitingToRetry), "WaitingToRetry：是否尚在等待下次传输重试。");
                                 Metric("耗时", ResourceWorkbenchGui.Duration(value.DurationMilliseconds), "DurationMilliseconds，未提供测量时不补0。");
                                 Metric("激活项目", string.Join(", ", value.ActivationProjects), "ActivationProjects：当前参与版本激活的项目。");
                             });
                    }

                    foreach (var note in _report.RecoveryNotes)
                    {
                        Add("恢复记录", note, () => Metric("恢复记录", "请展开技术详情查看原始说明", "RecoveryNotes：来自同次报告，查看记录不会执行恢复。"));
                    }

                    break;
                case 6:
                    foreach (var value in _report.History.Reverse())
                    {
                        Add(value.AtUtc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + EventName(value.Kind) + " " + ResourceEditorText.RequestStatus(value.Status) + " " + value.Address,
                            "UTC: " + value.AtUtc.ToString("u") + "\nKind: " + value.Kind + "\nStatus: " + value.Status + "\nAddress: " + value.Address +
                             "\nHasDuration: " + value.HasDuration + "\nDuration ms: " + (value.HasDuration ? value.DurationMilliseconds.ToString("F2") : "未测 / 不适用") + "\nCause: " + (value.Cause ?? "无"), () =>
                             {
                                 Metric("发生时间（UTC）", value.AtUtc.ToString("u", CultureInfo.InvariantCulture), "AtUtc：此事件的记录时间。");
                                 Metric("事件类型", EventName(value.Kind), "Kind：历史记录的事件类型。");
                                 Metric("请求状态", ResourceEditorText.RequestStatus(value.Status), "Status：此事件关联的请求状态。");
                                 Metric("资源地址", value.Address?.ToString() ?? "不适用", "Address：此事件关联的资源地址。");
                                 Metric("耗时", ResourceWorkbenchGui.Duration(value.HasDuration ? value.DurationMilliseconds : (double?)null), "HasDuration / DurationMilliseconds；未测量不显示为0。");
                                 Metric("异常原因", value.Cause == null ? "无" : "已记录异常，请展开技术详情", "Cause：技术字段保留原始异常说明。");
                             });
                    }

                    break;
                case 7:
                    var content = _report.Content;
                    Add("本会话数据预算", "Limit=" + content.BufferLimitBytes + "\nAllocated=" + content.AllocatedBufferBytes +
                        "\nReserved=" + content.ReservedBufferBytes + "\nPeak=" + content.PeakBufferBytes + "\nLiveInputs=" + content.LiveInputs + " / WorkCount=" + content.WorkCount +
                         "\nRetainedCleanupOwners=" + content.RetainedCleanupOwners + "\n分配与预留不重复计数；峰值是本 ResourceSystem 会话中两者合计的观测峰值，不是 Profiler 分配计数或进程总内存。", () =>
                        {
                            Metric("预算上限", ResourceWorkbenchGui.Bytes(content.BufferLimitBytes), "BufferLimitBytes：同次报告中此 ResourceSystem 会话的数据缓冲预算。");
                            Metric("已分配", ResourceWorkbenchGui.Bytes(content.AllocatedBufferBytes), "AllocatedBufferBytes，不与 ReservedBufferBytes 双计。");
                            Metric("已预留", ResourceWorkbenchGui.Bytes(content.ReservedBufferBytes), "ReservedBufferBytes：已预留但尚未分配的缓冲字节。");
                            Metric("观测峰值", ResourceWorkbenchGui.Bytes(content.PeakBufferBytes), "PeakBufferBytes，已分配与预留合计的观测峰值，不是进程总内存或 Profiler 分配计数。");
                            Metric("存活输入 / 工作", content.LiveInputs + " / " + content.WorkCount, "LiveInputs / WorkCount：仍存活的内容输入数与处理工作数。");
                            Metric("待清理持有者", content.RetainedCleanupOwners, "RetainedCleanupOwners：仍需保留清理责任的内容处理对象数量。");
                            GUILayout.Label("这是本会话显式数据预算，不是进程总内存。", ResourceWorkbenchGui.Secondary);
                        });
                    Add("Unity读取缓冲声明", "NativeReadBuffer=" + content.DeclaredNativeReadBufferBytes + "\n这是存活输入声明的Unity读取buffer，不与数据预算或CNG成本相加为总内存。", () =>
                    {
                        Metric("声明读取缓冲", ResourceWorkbenchGui.Bytes(content.DeclaredNativeReadBufferBytes), "DeclaredNativeReadBufferBytes：存活输入声明的 Unity 读取缓冲，不是原生内存测量。");
                        GUILayout.Label("声明量不与数据预算或CNG成本相加为总内存。", ResourceWorkbenchGui.Secondary);
                    });
                    Add("进程CNG成本", "CNG Providers=" + content.ProcessCngProviderCount + "\nCNG Key owners=" + content.ProcessCngKeyCount +
                         "\nCNG Key-object逻辑字节=" + content.ProcessCngKeyObjectBytes + "\n累计释放失败=" + content.ProcessCngReleaseFailures + "\n不代表整个密码模块的原生内存；零只表示尚未记录到释放失败。", () =>
                        {
                            Metric("Provider 数量", content.ProcessCngProviderCount, "ProcessCngProviderCount：本进程 CNG 适配器计数，可能包含正在结束的其他会话。");
                            Metric("密钥持有者", content.ProcessCngKeyCount, "ProcessCngKeyCount：本进程 CNG 密钥对象持有者数量。");
                            Metric("密钥对象逻辑字节", ResourceWorkbenchGui.Bytes(content.ProcessCngKeyObjectBytes), "ProcessCngKeyObjectBytes：不代表整个密码模块的原生内存。");
                            Metric("累计释放失败", content.ProcessCngReleaseFailures, "ProcessCngReleaseFailures：零表示尚未记录到释放失败，不表示所有清理路径均无问题。");
                            GUILayout.Label("这些是本进程 CNG 适配器的计数，可能包含正在结束的其他会话，不与会话缓冲预算相加。", ResourceWorkbenchGui.Secondary);
                        });
                    break;
                case 8:
                    var counters = _report.Summary;
                    Add("全会话计数", "RecordCount=" + counters.RecordCount + "\nProducerCount=" + counters.ProducerCount + "\nWaiterCount=" + counters.WaiterCount +
                        "\nLeaseCount=" + counters.LeaseCount + "\nBundleRecordCount=" + counters.BundleRecordCount + "\nBundleRootPinCount=" + counters.BundleRootPinCount +
                        "\nBundleDependencyPinCount=" + counters.BundleDependencyPinCount + "\nRunningBundleCount=" + counters.RunningBundleCount + "\nQueuedBundleCount=" + counters.QueuedBundleCount +
                        "\nNativeBundleLoadCount=" + counters.NativeBundleLoadCount + "\nNativeBundleUnloadCount=" + counters.NativeBundleUnloadCount, () =>
                        {
                            Metric("资源记录", counters.RecordCount, "Summary.RecordCount，全会话，不随工作台当前项目筛选。");
                            Metric("进行中的加载", counters.ProducerCount, "ProducerCount：正在处理资源加载的工作数量。");
                            Metric("等待者 / 租约", counters.WaiterCount + " / " + counters.LeaseCount, "WaiterCount / LeaseCount：等待资源的请求数与当前持有的资源租约数。");
                            Metric("Bundle记录", counters.BundleRecordCount, "BundleRecordCount：本会话中仍保留的 Bundle 记录数。");
                            Metric("根固定 / 依赖固定", counters.BundleRootPinCount + " / " + counters.BundleDependencyPinCount, "BundleRootPinCount / BundleDependencyPinCount：根引用与包间依赖引用的数量。");
                            Metric("运行 / 排队Bundle", counters.RunningBundleCount + " / " + counters.QueuedBundleCount, "RunningBundleCount / QueuedBundleCount：正在加载与等待调度的 Bundle 数量。");
                            Metric("原生加载 / 卸载", counters.NativeBundleLoadCount + " / " + counters.NativeBundleUnloadCount, "NativeBundleLoadCount / NativeBundleUnloadCount，原累计计数，不是耗时或内存测量。");
                        });
                    break;
            }
            _rows = result.ToArray();
            _rowDetails = details.ToArray();
            _areRowsDirty = false;
        }

        /// <summary>显示报告记录的服务状态，未知枚举值不当作可用状态。</summary>
        private static string RuntimeState(ResourceSystemState value) => value switch
        {
            ResourceSystemState.Initializing => "初始化中",
            ResourceSystemState.Ready => "可用",
            ResourceSystemState.ShuttingDown => "关闭中",
            ResourceSystemState.Closed => "已关闭",
            ResourceSystemState.Failed => "失败",
            ResourceSystemState.Invalidated => "已失效",
            _ => "未知状态（" + value + "）"
        };

        /// <summary>解释资源、场景和更新记录的阶段名称，未识别阶段保留原值。</summary>
        private static string StateName(string value) => value switch
        {
            "Waiting" => "等待依赖",
            "Queued" => "排队中",
            "Loading" => "加载中",
            "Ready" => "就绪",
            "Failed" => "失败",
            "Released" => "已释放",
            "AwaitingIdentity" => "等待确认场景",
            "NativeLoading" => "Unity 正在加载",
            "Preparing" => "准备中",
            "Unloading" => "卸载中",
            "Loaded" => "已加载",
            "AwaitingCleanup" => "等待清理",
            "Checking" => "检查更新",
            "RetryDelay" => "等待重试",
            "Downloading" => "下载中",
            "ContentIO" => "读取与验证内容",
            "Activation" => "激活版本",
            "LocalVersionTransaction" => "处理本地版本事务",
            _ => "未知状态（" + value + "）"
        };

        /// <summary>将正式来源身份映射为可读名称，不改写加载来源。</summary>
        private static string SourceName(ResourceSourceId value) => value.Value switch
        {
            "resources" => "Unity Resources",
            "asset-bundle" => "AssetBundle",
            "local-texture" => "本地图片",
            "url-texture" => "网络图片",
            "editor" => "编辑器资源",
            _ => "其他来源（" + value + "）"
        };

        /// <summary>解释报告实际记录的保留原因，不根据计数推造未标记持有者。</summary>
        private static string RetentionName(string value) => value switch
        {
            "Asset" => "资源租约与实例",
            "SceneRecord" => "已加载场景",
            "SceneLoad" => "场景加载请求",
            "ScenePreparing" => "场景准备",
            "GroupPreload" => "分组预加载",
            "PublicBundleLease" => "显式 Bundle 租约",
            "AssetProducer" => "资源加载工作",
            "ParentDependency" => "其他包的依赖",
            "ContentIO" => "内容读取与验证",
            "NativePending" => "Unity 原生操作未完成",
            "UnknownInternal" => "未标记的内部持有者",
            _ => "其他原因（" + value + "）"
        };

        /// <summary>展示请求历史事件种类，未知事件仍保留原始标识。</summary>
        private static string EventName(string value) => value switch
        {
            "RequestCompleted" => "请求结束",
            "LateResult" => "请求结束后返回的结果",
            "ProducerFailed" => "加载工作失败",
            _ => "其他事件（" + value + "）"
        };

        /// <summary>停止本视图诊断并清除快照引用，不关闭业务服务、取消业务请求或改变历史容量。</summary>
        public void Dispose()
        {
            SetVisible(false);
            _store.Dispose();
            _report = null;
            _sampledSystem = null;
            _failure = null;
            _rows = Array.Empty<(string, string)>();
            _rowDetails = Array.Empty<Action>();
        }
    }
}
