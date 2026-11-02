using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.Resource
{
    /// <summary>协调构建选项、确认和窗口内报告；构建、发布及生成 IO 委托正式服务执行。</summary>
    internal sealed class ResourceBuildView
    {
        private ResourceBuildMap _preview;
        private bool _isPreviewStale;
        private ResourceEditorBuildReport _report;
        private ResourcePreflightResult _preflight;
        private ResourcePreflightResult _readiness;
        private bool _isReadinessDirty = true;
        private int _recipeRevision;
        private string _lastWindowApp;
        private string _recipeGuid;
        private ResourceBuildInvocation _effective;
        private Exception _effectiveError;
        private Vector2 _optionsScroll;
        private Vector2 _resultScroll;
        private bool _showCompactResult;
        private readonly ResourceAnalysisView _analysis = new();
        private bool _showAdvanced;
        private bool _showExecutionHelp;
        private bool _showEvidence;
        private bool _areAttemptsExpanded;
        private readonly ResourceDeliveryView _delivery = new();
        private Vector2 _artifactScroll;
        private int _artifact;
        // 仅保留本窗口最多 32 次尝试，重载后不从磁盘推造临时报告。
        private readonly List<ResourceEditorBuildReport> _attempts = new();
        private ResourceEditorBuildReport _projectedReport;
        private long _projectedRawBytes;
        private string _projectedCosts;
        private string _projectedChanges;
        private Dictionary<string, ResourceBuiltFile> _rawFiles;
        private Dictionary<string, ResourceBuiltFile> _deliveryFiles;
        private Dictionary<string, ResourceBundleChange> _bundleChanges;
        internal bool HasDefinitionChanged
        {
            get; set;
        }
        internal ResourceEditorBuildReport CurrentReport => _report;
        internal bool IsHistoricalReport => _report != null && !ReferenceEquals(_report,
            _attempts.LastOrDefault(x => (x.Build?.Map.ProjectGuid ?? x.ProjectGuid) == (_report.Build?.Map.ProjectGuid ?? _report.ProjectGuid)));
        internal bool IsExactReportStale => HasDefinitionChanged || IsHistoricalReport;
        /// <summary>窗口 App 变化时失效相关检查；配方生效时不让后台窗口值改变配方检查。</summary>
        private void TrackWindowApp(string value)
        {
            if (_lastWindowApp == value)
            {
                return;
            }

            _lastWindowApp = value;
            if (HasRecipe)
            {
                return;
            }

            _isReadinessDirty = true;
            _readiness = null;
            _preflight = null;
        }
        private ResourceBuildRecipe _recipe;
        internal bool HasRecipe => !string.IsNullOrEmpty(_recipeGuid);
        internal ResourceBuildRecipe Recipe => _recipe;
        /// <summary>保留所选配方 GUID 并失效执行检查，丢失配方时不回退到窗口选项。</summary>
        internal void SetRecipe(ResourceBuildRecipe value, string guid)
        {
            if (ReferenceEquals(_recipe, value) && _recipeGuid == guid)
            {
                return;
            }

            _recipe = value;
            _recipeGuid = guid;
            _isReadinessDirty = true;
            _readiness = null;
            _preflight = null;
            HasDefinitionChanged = true;
        }
        /// <summary>仅保存当前仍存在的明确配方，并安排重新计算静态检查。</summary>
        internal void SaveRecipe()
        {
            if (!HasRecipe || !_recipe)
            {
                throw new InvalidOperationException("所选构建配方已丢失。请解除选择或重新选择；不会改为保存其他配方。");
            }

            ResourceEditorOperations.SaveRecipe(_recipe);
            _isReadinessDirty = true;
        }

        /// <summary>清除当前项目的报告展示、分析和交付上下文，保留窗口内尝试历史供按项目查询。</summary>
        internal void Reset()
        {
            InvalidatePreview();
            _report = null;
            _delivery.Reset();
            _preview = null;
            _analysis.Reset();
            HasDefinitionChanged = false;
        }
        /// <summary>接收成功计算的当前定义预估并清除分析错误，不将其当作构建结果。</summary>
        internal void SetPreview(ResourceBuildMap value, ResourceProject project)
        {
            _preview = value;
            _isPreviewStale = false;
            _analysis.Error = null;
            _analysis.AtlasPending = null;
        }
        /// <summary>失效预检、预估及交付状态，保留旧结果以明确标记过期。</summary>
        internal void InvalidatePreview()
        {
            _preflight = null;
            _readiness = null;
            _isReadinessDirty = true;
            _isPreviewStale = true;
            HasDefinitionChanged = true;
            _delivery.Invalidate();
        }

        /// <summary>向分析页传递预估和实际报告，并分别保留两者的过期状态。</summary>
        internal void DrawPreview(ResourceProject project, float width, Action refresh, Action<ResourceBuildSource> locate) =>
            _analysis.Draw(project, _preview, _report, IsExactReportStale, _isPreviewStale, width, refresh, locate);
        /// <summary>记录预估失败或图集待准备原因，不覆盖已有成功报告。</summary>
        internal void SetPreviewError(Exception error, ResourceAtlasPlan pending = null)
        {
            _isPreviewStale = true;
            _analysis.Error = error;
            _analysis.AtlasPending = pending;
        }

        /// <summary>绘制有效执行选项与结果，把耗时检查及执行交给外层队列，并在执行前请求确认。</summary>
        internal void Draw(ResourceProject project, ref string app, ref bool generate, ref bool force, ref string directory, float width,
            Action<Func<string>> queue, Action<Func<string>> run, Action<ResourceBuildResult> rebind,
            Action<ResourceBuildRecipe> selectRecipe, Action<ResourceProject> switchProject, Action showAnalysis)
        {
            TrackWindowApp(app);
            if (_recipe && _recipeRevision != EditorUtility.GetDirtyCount(_recipe))
            {
                _recipeRevision = EditorUtility.GetDirtyCount(_recipe);
                _isReadinessDirty = true;
                _preflight = null;
                HasDefinitionChanged = true;
            }
            if (width < 1000)
            {
                _showCompactResult = ResourceWorkbenchGui.Navigation(_showCompactResult ? 1 : 0, new[] { "执行配置", "构建结果" }) == 1;
            }

            EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            var wide = width >= 1000;
            if (wide || !_showCompactResult)
            {
                EditorGUILayout.BeginVertical(wide ? new[] { GUILayout.Width(width * .46f), GUILayout.ExpandHeight(true) } : new[] { GUILayout.MinWidth(0), GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true) });
                _optionsScroll = EditorGUILayout.BeginScrollView(_optionsScroll, GUILayout.ExpandHeight(true));
                if (wide)
                {
                    GUILayout.Space(8);
                    GUILayout.Label("执行选项", EditorStyles.boldLabel);
                    GUILayout.Space(6);
                }
                EditorGUI.BeginChangeCheck();
                var picked = ResourceWorkbenchGui.ObjectField("构建配方（可选）", _recipe, "首次构建无需配方。配方用于保存和复用执行选项，不替代资源项目的分组、压缩或加密设置。");
                if (EditorGUI.EndChangeCheck())
                {
                    selectRecipe(picked);
                }

                if (HasRecipe)
                {
                    if (!_recipe)
                    {
                        EditorGUILayout.HelpBox("所选构建配方已丢失。请解除选择或重新选择配方。\n配方 GUID：" + _recipeGuid + "\n不会自动改用窗口选项。", MessageType.Error);
                        if (GUILayout.Button("解除丢失配方选择"))
                        {
                            selectRecipe(null);
                        }
                    }
                    else
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Label(EditorUtility.IsDirty(_recipe) ? "构建配方未保存 *" : "正在编辑所选配方", EditorStyles.boldLabel);
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("保存配方", ResourceWorkbenchGui.Button, GUILayout.Width(96)))
                        {
                            run(() =>
                            {
                                SaveRecipe();
                                return "所选构建配方已保存；未保存资源项目或场景。";
                            });
                        }

                        EditorGUILayout.EndHorizontal();
                        using (var serialized = new SerializedObject(_recipe))
                        {
                            if (ResourceBuildRecipeFields.Draw(serialized, false))
                            {
                                _isReadinessDirty = true;
                                _preflight = null;
                                HasDefinitionChanged = true;
                            }
                        }

                        if (_recipe.project != project)
                        {
                            EditorGUILayout.HelpBox("配方属于另一个资源项目，不能执行。请切换到配方所属项目，或解除配方选择。\n当前：" + Identity(project) + "\n配方：" + Identity(_recipe.project), MessageType.Error);
                            EditorGUILayout.BeginHorizontal();
                            using (new EditorGUI.DisabledScope(!_recipe.project))
                            {
                                if (GUILayout.Button("切换到配方所属项目", ResourceWorkbenchGui.Button))
                                {
                                    switchProject(_recipe.project);
                                }
                            }

                            if (GUILayout.Button("解除配方选择", ResourceWorkbenchGui.Button))
                            {
                                selectRecipe(null);
                            }

                            EditorGUILayout.EndHorizontal();
                        }
                        else if (GUILayout.Button("解除配方，使用窗口选项", ResourceWorkbenchGui.Button))
                        {
                            selectRecipe(null);
                        }
                    }
                }
                else
                {
                    EditorGUI.BeginChangeCheck();
                    app = ResourceWorkbenchGui.TextField("资源兼容标识（App）", app, true, "由应用约定的资源兼容标识，不是游戏版本号。构建与后续使用必须匹配，例如 app-v1（仅示例）。");
                    if (EditorGUI.EndChangeCheck())
                    {
                        _isReadinessDirty = true;
                        _preflight = null;
                    }
                    GUILayout.Label("不使用配方也可以构建。完成资源配置并保存后，直接点击“构建并本地发布”。", ResourceWorkbenchGui.Secondary);
                }
                if (!HasRecipe || _recipe)
                {
                    GUILayout.Space(6);
                    _showAdvanced = ResourceWorkbenchGui.Disclosure(_showAdvanced, "附加构建选项");
                    if (_showAdvanced)
                    {
                        using (new EditorGUI.IndentLevelScope())
                        {
                            if (HasRecipe)
                            {
                                using var serialized = new SerializedObject(_recipe);
                                if (ResourceBuildRecipeFields.Draw(serialized, advancedOnly: true))
                                {
                                    _isReadinessDirty = true;
                                    _preflight = null;
                                    HasDefinitionChanged = true;
                                }
                            }
                            else
                            {
                                EditorGUI.BeginChangeCheck();
                                force = ResourceWorkbenchGui.Toggle("强制重建", force, "不使用可跳过构建的缓存结果；不放宽验证。");
                                generate = ResourceWorkbenchGui.Toggle("生成资源地址常量", generate, "本地发布成功后，额外生成地址常量；仅构建不执行生成。");
                                if (generate)
                                {
                                    directory = ResourceWorkbenchGui.TextField("常量目标目录", directory, true);
                                }

                                if (EditorGUI.EndChangeCheck())
                                {
                                    _isReadinessDirty = true;
                                    _preflight = null;
                                }
                            }
                        }
                    }
                }
                var activeForce = HasRecipe ? _recipe && _recipe.forceRebuild : force;
                var activeGenerate = HasRecipe ? _recipe && _recipe.generateReferences : generate;
                if (!_showAdvanced && (activeForce || activeGenerate))
                {
                    EditorGUILayout.HelpBox("已启用：" + (activeForce ? "强制重建" : "") + (activeForce && activeGenerate ? "；" : "") +
                    (activeGenerate ? "发布后生成资源地址常量 → " + (HasRecipe ? _recipe.referenceDirectory : directory) : ""), MessageType.None);
                }

                if (activeGenerate)
                {
                    GUILayout.Label("地址常量面向当前平台所有已登记的发布项目，要求它们的 Unity 与 App 兼容标识一致。", ResourceWorkbenchGui.Secondary);
                }

                // 重绘只重算低成本静态门禁；密钥、依赖和输出的完整检查由显式操作触发。
                if (_isReadinessDirty)
                {
                    _effective = null;
                    _effectiveError = null;
                    try
                    {
                        if (HasRecipe && _recipe)
                        {
                            _effective = ResourceEditorOperations.ResolveRecipe(_recipe);
                        }
                    }
                    catch (Exception error)
                    {
                        _effectiveError = error;
                    }
                    _readiness = HasRecipe ? ResourceEditorOperations.InspectRecipePreflight(_recipe) : ResourceEditorOperations.CheckReadiness(project, app);
                    _isReadinessDirty = false;
                }
                GUILayout.Space(5);
                var policyProject = HasRecipe ? (_recipe ? _recipe.project : null) : project;
                GUILayout.Label("资源项目的构建策略", EditorStyles.boldLabel);
                if (policyProject)
                {
                    GUILayout.Label(policyProject.compression + " 压缩 · " + (policyProject.contentEncryption == YLFramework.Resource.ResourceContentEncryption.None ? "不加密 (None)" :
                    policyProject.contentEncryption + " 加密 · KeyId=" + policyProject.contentKeyId), EditorStyles.wordWrappedLabel);
                }
                else
                {
                    GUILayout.Label("配方指定的资源项目不可用；不会改用其他项目的构建设置。", ResourceWorkbenchGui.Secondary);
                }

                if (_effectiveError != null)
                {
                    EditorGUILayout.HelpBox("无法读取配方的执行选项，请展开执行说明查看错误原因。", MessageType.Error);
                }

                if (_readiness?.State == ResourcePreflightState.StaticBlocked)
                {
                    EditorGUILayout.HelpBox("开始前需要修正配置：\n" + ReadinessSummary(_readiness), MessageType.Error);
                }
                else if (_readiness != null)
                {
                    GUILayout.Label("配置基础检查通过；执行前仍会检查资源与输出。", ResourceWorkbenchGui.Secondary);
                }

                if (_preflight != null)
                {
                    EditorGUILayout.HelpBox(_preflight.State == ResourcePreflightState.Complete ? "资源检查完成，尚未构建或发布。执行时会再次核对。" :
                    _preflight.State == ResourcePreflightState.AtlasPreparationRequired ? "图集需要生成或更新。正式构建会先准备图集，再继续检查资源；也可以到‘资源配置 > 图集’先预览计划。" : "资源检查未通过，请展开执行说明查看错误原因。",
                    _preflight.Error != null ? MessageType.Error : _preflight.State == ResourcePreflightState.Complete ? MessageType.Info : MessageType.Warning);
                }

                _showExecutionHelp = ResourceWorkbenchGui.Disclosure(_showExecutionHelp, "执行说明与技术详情");
                if (_showExecutionHelp)
                {
                    GUILayout.Label("None 表示不加密，与压缩方式无关。加密构建只在显式检查或执行时读取外部密钥；KeyId 不是密钥。", ResourceWorkbenchGui.Secondary);
                    GUILayout.Label("只使用当前已激活且可用的平台。操作排队时可以取消；构建开始后须等待完成。", ResourceWorkbenchGui.Secondary);
                    if (_effective != null)
                    {
                        GUILayout.Label("本次配方参数：\n" + _effective, ResourceWorkbenchGui.Secondary);
                    }

                    if (_effectiveError != null)
                    {
                        GUILayout.Label(ResourceEditorText.FailureDetails(_effectiveError), ResourceWorkbenchGui.Secondary);
                    }

                    if (_readiness != null)
                    {
                        GUILayout.Label("配置检查详情：\n" + _readiness.Summary + (_readiness.Error == null ? "" : "\n" + ResourceEditorText.FailureDetails(_readiness.Error)), ResourceWorkbenchGui.Secondary);
                    }

                    if (_preflight != null)
                    {
                        GUILayout.Label("资源检查详情：\n" + _preflight.Summary + (_preflight.Error == null ? "" : "\n" + ResourceEditorText.FailureDetails(_preflight.Error)), ResourceWorkbenchGui.Secondary);
                    }
                }
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
            }
            if (wide)
            {
                var divider = GUILayoutUtility.GetRect(12, 12, 0, 100000, GUILayout.Width(12), GUILayout.ExpandHeight(true));
                EditorGUI.DrawRect(new Rect(divider.center.x, divider.y + 6, 1, Mathf.Max(0, divider.height - 12)), ResourceWorkbenchGui.Divider);
            }
            if (wide || _showCompactResult)
            {
                EditorGUILayout.BeginVertical(GUILayout.MinWidth(0), GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                _resultScroll = EditorGUILayout.BeginScrollView(_resultScroll, GUILayout.ExpandHeight(true));
                if (wide)
                {
                    GUILayout.Space(8);
                    GUILayout.Label("执行结果", EditorStyles.boldLabel);
                    GUILayout.Space(6);
                }
                if (_report == null)
                {
                    GUILayout.Label("构建结果会显示在这里。\n首次使用：添加资源 → 保存项目 → 构建并本地发布。\n本窗口的执行记录可在底部“输出”中查看；重载后不恢复临时构建报告。", ResourceWorkbenchGui.Secondary);
                }
                else
                {
                    DrawReport();
                    if (_report.Build != null && GUILayout.Button("查看这次构建的分包", ResourceWorkbenchGui.Button))
                    {
                        _analysis.UseExact();
                        showAnalysis();
                    }
                }
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
            var appValue = app;
            var generateValue = generate;
            var forceValue = force;
            var directoryValue = directory;
            var selectedRecipeGuid = _recipeGuid;
            var canExecute = _readiness?.State == ResourcePreflightState.StaticReady && (!HasRecipe || _recipe && _recipe.project == project);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!canExecute))
            {
                if (GUILayout.Button("检查资源", ResourceWorkbenchGui.Button, GUILayout.Width(124)))
                {
                    queue(() =>
                {
                    var checkedInput = ValidateInput(project, appValue, selectedRecipeGuid);
                    return ResourceEditorText.Preflight(checkedInput) + "\n检查详情：\n" + checkedInput.Summary;
                });
                }

                if (HasRecipe)
                {
                    if (ResourceWorkbenchGui.PrimaryAction(_effective?.Action == ResourceBuildAction.BuildAndPublish ? "按配方构建并本地发布..." : "按配方仅构建..."))
                    {
                        queue(() =>
                    {
                        var chosen = RequireMatchingRecipe(project, selectedRecipeGuid);
                        var checkedInput = ValidateInput(project, appValue, selectedRecipeGuid);
                        var frozen = ResourceEditorOperations.ResolveRecipe(chosen);
                        if (!ResourceOperationConfirmation.Confirm("确认执行构建配方", frozen + "\n\n" + checkedInput.Summary + "\n按配方中的执行操作运行；不部署、不上传、不切换运行版本。", "执行构建配方",
                            ResourceConfirmationSummary.Build(checkedInput, frozen.Action == ResourceBuildAction.BuildAndPublish ? "构建并本地发布" : "仅构建", frozen.ForceRebuild, frozen.GenerateReferences, frozen.ReferenceDirectory)))
                        {
                            return "构建配方执行已取消。";
                        }

                        chosen = RequireMatchingRecipe(project, selectedRecipeGuid);
                        return RecordReport(ResourceEditorOperations.ExecuteRecipe(chosen), rebind);
                    });
                    }
                }
                else
                {
                    if (GUILayout.Button("仅构建...", ResourceWorkbenchGui.Button, GUILayout.Width(104)))
                    {
                        queue(() =>
                    {
                        var checkedInput = ValidateInput(project, appValue, null);
                        if (!ResourceOperationConfirmation.Confirm("确认仅构建", checkedInput.Summary + "\nForceRebuild=" + forceValue + "\n本次不发布、不生成常量。", "仅构建",
                            ResourceConfirmationSummary.Build(checkedInput, "仅构建", forceValue, false, null)))
                        {
                            return "构建已取消。";
                        }

                        return RecordReport(ResourceEditorOperations.BuildOnly(project, appValue, forceValue), rebind);
                    });
                    }

                    if (ResourceWorkbenchGui.PrimaryAction("构建并本地发布..."))
                    {
                        queue(() =>
                    {
                        var checkedInput = ValidateInput(project, appValue, null);
                        if (!ResourceOperationConfirmation.Confirm("确认构建与本地发布", checkedInput.Summary + "\nForceRebuild=" + forceValue + "\n生成=" + generateValue + " / 目录=" + directoryValue, "构建并本地发布",
                            ResourceConfirmationSummary.Build(checkedInput, "构建并本地发布", forceValue, generateValue, directoryValue)))
                        {
                            return "已取消，未创建输出。";
                        }

                        return RecordReport(ResourceEditorOperations.BuildAndPublish(project, appValue, generateValue, directoryValue, forceValue), rebind);
                    });
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
            using (new EditorGUI.DisabledScope(_report?.Build == null || HasDefinitionChanged || !canExecute))
            {
                if (_report?.Build != null && (_report.Release == null || _showEvidence) && GUILayout.Button("发布这次构建（不重新构建）...", ResourceWorkbenchGui.Button))
                {
                    var input = _report.Build;
                    queue(() =>
                    {
                        var publicationOptions = string.IsNullOrEmpty(selectedRecipeGuid) ? null : ResourceEditorOperations.ResolveRecipe(RequireMatchingRecipe(project, selectedRecipeGuid));
                        if (publicationOptions != null && EditorUtility.IsDirty(_recipe))
                        {
                            throw new InvalidOperationException("请先保存所选构建配方。");
                        }

                        var publicationApp = publicationOptions?.AppContentCompatibilityId ?? appValue;
                        var publicationGenerate = publicationOptions?.GenerateReferences ?? generateValue;
                        var publicationDirectory = publicationOptions?.ReferenceDirectory ?? directoryValue;
                        if (string.IsNullOrEmpty(publicationApp))
                        {
                            throw new InvalidOperationException("尚未填写资源兼容标识（App）。请先修正配方，或解除配方选择后填写窗口选项。");
                        }

                        if (!ResourceOperationConfirmation.Confirm("确认发布这次构建", "资源项目=" + input.Map.ProjectId + "\n平台=" + input.Platform + "\n构建工作目录=" + input.WorkspacePath +
                            "\nInputFingerprint=" + input.InputFingerprint + "\nApp=" + publicationApp + "\n不重新构建；发布前重新验证源资源和工作目录，不部署、不上传、不切换运行版本。", "发布此构建", new ResourceConfirmationSummary
                            {
                                Fields = new[] { ("资源项目", input.Map.ProjectId), ("构建平台", input.Platform), ("资源兼容标识（App）", publicationApp),
                                    ("使用的构建", "本窗口当前选中的构建结果"), ("地址常量", publicationGenerate ? "发布后生成 → " + publicationDirectory : "不生成") },
                                Effects = new[] { "重新验证这次构建后，写入本地发布版本和发布索引。", "不重新构建，不部署、不上传，也不切换正在运行的版本。" },
                                Notice = "发布提交未确认时，请先恢复发布事务，不要直接重复发布。"
                            }, details:
                            new ResourceConfirmationDetails("ProjectId=" + input.Map.ProjectId + "\nProjectGuid=" + input.Map.ProjectGuid + "\nPlatform=" + input.Platform +
                                "\nApp=" + publicationApp + "\nInputFingerprint=" + input.InputFingerprint,
                                "Workspace=" + input.WorkspacePath + "\n地址常量目标=" + (publicationGenerate ? publicationDirectory : "不生成"),
                                "不重新构建；发布前重新验证源资源与 Workspace。\nGenerateReferences=" + publicationGenerate +
                                "\n发布事务未完成时，请使用“恢复本地发布事务”。不部署、不上传、不切换运行版本。")))
                        {
                            return "发布已取消。";
                        }

                        return RecordReport(ResourceEditorOperations.PublishBuild(input, publicationApp, publicationGenerate, publicationDirectory), rebind);
                    });
                }
            }
        }

        /// <summary>格式化项目标识、路径和 GUID，供配方归属冲突时核对真实目标。</summary>
        private static string Identity(ResourceProject project) => project ? project.projectId + " / " + AssetDatabase.GetAssetPath(project) + " / GUID=" +
            AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project)) : "[项目不可用]";

        /// <summary>重取排队时选择的配方并核对当前项目，拒绝选择变化或资产丢失后的旧请求。</summary>
        private ResourceBuildRecipe RequireMatchingRecipe(ResourceProject project, string guid)
        {
            if (string.IsNullOrEmpty(guid) || _recipeGuid != guid)
            {
                throw new InvalidOperationException("构建配方选择已变化，排队请求已失效。请按当前选择重新执行。");
            }

            var current = AssetDatabase.LoadAssetAtPath<ResourceBuildRecipe>(AssetDatabase.GUIDToAssetPath(guid));
            if (!current || current.project != project)
            {
                throw new InvalidOperationException("配方中的资源项目与当前工作台不匹配；未自动修改或切换项目。");
            }

            return current;
        }

        /// <summary>显式执行完整范围的只读预检并保留结论；图集待准备可返回，最终执行仍须复验。</summary>
        private ResourcePreflightResult ValidateInput(ResourceProject project, string app, string chosenRecipe)
        {
            _preflight = string.IsNullOrEmpty(chosenRecipe) ? ResourceEditorOperations.InspectPreflight(project, app) :
                ResourceEditorOperations.InspectRecipePreflight(RequireMatchingRecipe(project, chosenRecipe), true);
            if (_preflight.Error != null)
            {
                throw _preflight.Error;
            }

            return _preflight;
        }

        /// <summary>将交付页绘制委托给独立的版本选择和快照上下文。</summary>
        internal void DrawDelivery(ResourceProject project, ref string app, float width, Action<Func<string>> queue, Action<Func<string>> run) =>
            _delivery.Draw(project, ref app, width, queue, run);

        /// <summary>按项目列出窗口内尝试并允许查看旧报告，返回是否选中了记录。</summary>
        internal bool DrawAttempts(ResourceProject project = null)
        {
            var selected = false;
            var visible = project ? GetAttempts(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project))) : _attempts;
            if (visible.Count > 0)
            {
                _areAttemptsExpanded = EditorGUILayout.Foldout(_areAttemptsExpanded, "当前项目执行记录 " + visible.Count + " / 全窗口 " + _attempts.Count + "（最多32条，重载后清空）", true);
                if (_areAttemptsExpanded)
                {
                    for (var i = visible.Count - 1; i >= 0; i--)
                    {
                        var attempt = visible[i];
                        var state = attempt.Build == null ? "构建失败" : attempt.PublicationError != null ? "发布未确认" : attempt.Release == null ? "构建完成，未发布" :
                            attempt.GenerationError != null ? "已发布 / 常量失败" : "已发布";
                        if (GUILayout.Button("#" + (i + 1) + " " + ResourceEditorText.Operation(attempt.Operation) + " / " + state + " / " + attempt.StartedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss") +
                            " / " + (attempt.ProjectId ?? attempt.Build?.Map.ProjectId) + " / " + (attempt.Release?.Catalog.CatalogDigest ?? ""), EditorStyles.miniButton))
                        {
                            _report = attempt;
                            _artifact = 0;
                            selected = true;
                        }
                    }
                }
            }
            return selected;
        }

        /// <summary>按构建结果或失败报告的项目 GUID 筛选原始尝试，不重新读取发布状态。</summary>
        internal IReadOnlyList<ResourceEditorBuildReport> GetAttempts(string projectGuid) =>
            _attempts.Where(x => (x.Build?.Map.ProjectGuid ?? x.ProjectGuid) == projectGuid).ToArray();

        /// <summary>重绑项目并登记真实报告；发布结果交给交付页，源码确有变化时才请求 Unity 刷新。</summary>
        private string RecordReport(ResourceEditorBuildReport completed, Action<ResourceBuildResult> rebind)
        {
            rebind(completed.Build);
            _report = completed;
            _attempts.Add(completed);
            if (_attempts.Count > 32)
            {
                _attempts.RemoveAt(0);
            }

            if (completed.Build != null)
            {
                HasDefinitionChanged = false;
            }

            _delivery.OfferRelease(completed.Release);
            _artifact = 0;
            if (completed.HasGenerationChanged)
            {
                AssetDatabase.Refresh();
            }

            return completed.Summary;
        }

        /// <summary>分别呈现构建、发布和常量结果及原始测量，未知数据不冒充零或编译成功。</summary>
        private void DrawReport()
        {
            if (_report == null)
            {
                return;
            }

            ProjectReport();
            var previousColor = GUI.contentColor;
            GUI.contentColor = _report.Build == null ? new Color(1, .52f, .48f) : _report.PublicationError != null || _report.GenerationError != null ? new Color(1, .76f, .35f) : EditorGUIUtility.isProSkin ? new Color(.58f, .85f, .66f) : new Color(.12f, .43f, .24f);
            try
            {
                GUILayout.Label(Outcome(_report), ResourceWorkbenchGui.Title);
            }
            finally
            {
                GUI.contentColor = previousColor;
            }
            GUILayout.Label(_report.StartedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss") + " · 操作总耗时 " + ResourceWorkbenchGui.Duration(_report.ElapsedMilliseconds), ResourceWorkbenchGui.Secondary);
            GUILayout.Space(6);
            ResourceWorkbenchGui.Metric("资源构建", _report.Build == null ? "未完成" : "已完成");
            ResourceWorkbenchGui.Metric("本地发布", _report.Release != null ? "已发布" : _report.PublicationError != null ? "提交未确认" : "未执行");
            ResourceWorkbenchGui.Metric("资源地址常量", _report.GenerationError != null ? "生成失败" : !_report.IsGenerationRequested ? "未启用" : _report.Release == null ? "未执行" : _report.HasGenerationChanged ? "源码已更新，待 Unity 编译" : "源码未变化");
            var failure = _report.BuildError ?? _report.PublicationError ?? _report.GenerationError;
            if (failure != null)
            {
                EditorGUILayout.HelpBox(_report.Build == null ? "本次构建未完成。请展开技术详情查看错误原因。" : _report.PublicationError != null ?
                "构建已完成，但发布提交结果尚未确认。请先恢复发布事务，再读取发布索引。" : "发布已完成，但地址常量生成失败。请展开技术详情查看错误原因。", _report.Build == null ? MessageType.Error : MessageType.Warning);
            }

            if (_report.PublicationError != null)
            {
                GUILayout.Label("发布索引可能已经变化。请到“版本交付 > 恢复与其他”恢复发布事务，再读取发布索引；不要直接重复发布。", ResourceWorkbenchGui.Secondary);
            }
            else if (_report.Release != null && _report.GenerationError != null)
            {
                GUILayout.Label("发布版本仍有效，无需重发或回滚。处理上方问题后，可在“版本交付 > 恢复与其他”单独生成资源地址常量。", ResourceWorkbenchGui.Secondary);
            }
            else if (_report.Build == null)
            {
                GUILayout.Label("根据技术详情中的原因修正问题后，重新检查资源。已有发布版本不会因此失效。", ResourceWorkbenchGui.Secondary);
            }

            if (_report.Release != null)
            {
                ResourceWorkbenchGui.Metric("版本摘要", _report.Release.Catalog.CatalogDigest.Substring(0, 16), _report.Release.Catalog.CatalogDigest);
            }

            if (HasDefinitionChanged || IsHistoricalReport)
            {
                EditorGUILayout.HelpBox(IsHistoricalReport ? "当前选择的是较早的执行记录，不代表现在的配置。" : "配置或资源已变化。当前显示上次执行结果，请重新检查或构建。", MessageType.Warning);
            }

            _showEvidence = ResourceWorkbenchGui.Disclosure(_showEvidence, "技术详情、完整参数与产物");
            if (!_showEvidence)
            {
                return;
            }

            if (_report.BuildError != null)
            {
                GUILayout.Label("构建错误：\n" + ResourceEditorText.FailureDetails(_report.BuildError), ResourceWorkbenchGui.Secondary);
            }

            if (_report.PublicationError != null)
            {
                GUILayout.Label("发布错误：\n" + ResourceEditorText.FailureDetails(_report.PublicationError), ResourceWorkbenchGui.Secondary);
            }

            if (_report.GenerationError != null)
            {
                GUILayout.Label("常量生成错误：\n" + ResourceEditorText.FailureDetails(_report.GenerationError), ResourceWorkbenchGui.Secondary);
            }

            GUILayout.Label(_report.Invocation?.ToString() ?? "操作=" + _report.Operation + " / Project=" + _report.ProjectId + " / GUID=" + _report.ProjectGuid +
                "\n平台=" + _report.Platform + " / App=" + _report.AppContentCompatibilityId + " / force=" + _report.ForceRebuild +
                " / 生成=" + _report.IsGenerationRequested + " / 目录=" + _report.ReferenceDirectory, ResourceWorkbenchGui.Secondary);
            if (_report.Build == null)
            {
                return;
            }

            var build = _report.Build;
            GUILayout.Label("Manifest 已验证 " + build.Bundles.Count + " 个 Bundle · 原始构建文件 " + build.Files.Count + " 个 · 总字节 " + _projectedRawBytes +
                "\n预检 " + build.PreflightMilliseconds.ToString("F2", CultureInfo.InvariantCulture) + " ms · 构建 " + build.BuildMilliseconds.ToString("F2", CultureInfo.InvariantCulture) +
                " ms · 验证 " + build.ValidationMilliseconds.ToString("F2", CultureInfo.InvariantCulture) + " ms\n输入哈希读取 " + build.SourceHashBytes +
                " bytes · 产物哈希读取 " + build.ArtifactHashBytes + " bytes · 发布复制 " + (_report.Release == null ? "未执行 / 未确认" : _report.Release.CopiedBytes + " bytes"), EditorStyles.wordWrappedLabel);
            GUILayout.Label(_projectedCosts, EditorStyles.wordWrappedMiniLabel);
            GUILayout.Label("各项计时有重叠，不能相加。ContentEncoding 包含编码、复用和验证，不是纯 AES 耗时；原生内存未测量，不能由 IO 或字节量推算。", ResourceWorkbenchGui.Secondary);
            GUILayout.Label("交付编码 " + build.ContentEncryption + " · KeyId=" + (build.ContentKeyId ?? "无") + " · 总字节 " + build.DeliveryBytes +
                "\n新编码 " + build.EncodedDeliveryBytes + " bytes · 已验证复用 " + build.ReusedDeliveryBytes + " bytes · 编码/交付验证 " +
                build.ContentEncodingMilliseconds.ToString("F2", CultureInfo.InvariantCulture) + " ms", EditorStyles.wordWrappedMiniLabel);
            if (_report.Release != null)
            {
                GUILayout.Label("发布复制 IO " + _report.Release.CopyMilliseconds.ToString("F2") + " ms · 新 staging 验证 " + _report.Release.StagingValidationMilliseconds.ToString("F2") +
                    " ms · 重复版本=" + _report.Release.IsRepeat, EditorStyles.wordWrappedMiniLabel);
            }

            foreach (var reason in build.ExecutionReasons)
            {
                GUILayout.Label("执行原因（原始记录）：" + reason, EditorStyles.wordWrappedMiniLabel);
            }

            GUILayout.Label(_projectedChanges, EditorStyles.wordWrappedMiniLabel);
            if (build.CacheReuseBlockedBy.Count != 0)
            {
                EditorGUILayout.HelpBox(build.CacheReuseBlockedBy.Count + " 项构建回调的输入无法固定，因此本次仍需构建，不能直接复用整个构建结果。即使产物摘要相同也不跳过此检查。", MessageType.Warning);
            }

            EditorGUILayout.SelectableLabel("InputFingerprint=" + build.InputFingerprint + "\nBuildSettingsFingerprint=" + build.BuildSettingsFingerprint,
                EditorStyles.wordWrappedMiniLabel, GUILayout.Height(55));
            if (_report.Release != null)
            {
                EditorGUILayout.SelectableLabel("BuildId=" + _report.Release.Catalog.BuildId + "\nCatalogDigest=" + _report.Release.Catalog.CatalogDigest,
                    EditorStyles.wordWrappedMiniLabel, GUILayout.Height(55));
            }

            foreach (var callback in build.CacheReuseBlockedBy)
            {
                GUILayout.Label("构建回调：" + callback, EditorStyles.wordWrappedMiniLabel);
            }

            var rect = GUILayoutUtility.GetRect(200, 150, GUILayout.ExpandWidth(true));
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            _artifactScroll = GUI.BeginScrollView(rect, _artifactScroll, new Rect(0, 0, Mathf.Max(0, rect.width - 20), Math.Max(148, build.Bundles.Count * 24 + 4)));
            var first = Mathf.Max(0, (int)(_artifactScroll.y / 24));
            for (var i = first; i < Math.Min(build.Bundles.Count, first + 9); i++)
            {
                if (GUI.Toggle(new Rect(4, i * 24, rect.width - 28, 23), _artifact == i, build.Bundles[i].Name, EditorStyles.miniButton))
                {
                    _artifact = i;
                }
            }

            GUI.EndScrollView();
            if (build.Bundles.Count == 0)
            {
                return;
            }

            _artifact = Mathf.Clamp(_artifact, 0, build.Bundles.Count - 1);
            var item = build.Bundles[_artifact];
            var file = _rawFiles[item.Name];
            var delivery = _deliveryFiles[item.Name];
            var change = _bundleChanges[item.Name];
            var text = item.Name + "\nUnityHash=" + item.UnityHash + "\nRaw SHA256=" + file.Sha256 + " / bytes=" + file.Length +
                "\nDelivery SHA256=" + delivery.Sha256 + " / bytes=" + delivery.Length +
                "\nPlacement=" + item.Placement + " / source files=" + item.SourceFileCount + " / source bytes=" + item.SourceBytes +
                "\nManifest dependents=" + string.Join(", ", item.Dependents) +
                "\nPrevious SHA256=" + (change.PreviousSha256 ?? "无兼容基线") + "\n" + change.Reason + "\nManifest dependencies=" + string.Join(", ", item.Dependencies);
            EditorGUILayout.SelectableLabel(text, EditorStyles.wordWrappedMiniLabel, GUILayout.MinHeight(110));
        }

        /// <summary>为明确的空 App 错误提供字段提示，其他情况沿用正式预检结论。</summary>
        internal static string ReadinessSummary(ResourcePreflightResult value) => value != null && value.State == ResourcePreflightState.StaticBlocked &&
            string.IsNullOrWhiteSpace(value.AppContentCompatibilityId) && value.Error is ArgumentException error && error.ParamName == "projectId"
            ? "资源兼容标识（App）尚未填写。请先输入应用约定的稳定标识；它不是游戏版本号。原始检查信息可在执行说明中查看。"
            : value?.Error != null ? ResourceEditorText.Preflight(value) : value?.Summary ?? "";

        /// <summary>按各阶段已确认结果给出短结论，不将发布未确认解释为未写入。</summary>
        internal static string Outcome(ResourceEditorBuildReport value) => value.Build == null ? "构建未完成。" :
            value.PublicationError != null ? "构建完成，发布提交未确认。" : value.Release == null ? "构建完成，尚未发布。" :
            value.GenerationError != null ? "资源已发布，地址常量生成失败。" : "资源已构建并本地发布。";

        /// <summary>按报告引用缓存展示字典与汇总，避免每次重绘重复遍历构建制品。</summary>
        private void ProjectReport()
        {
            if (ReferenceEquals(_projectedReport, _report))
            {
                return;
            }

            _projectedReport = _report;
            if (_report?.Build == null)
            {
                return;
            }

            var build = _report.Build;
            _projectedRawBytes = build.Files.Sum(x => x.Length);
            _rawFiles = build.Files.ToDictionary(x => x.Name, StringComparer.Ordinal);
            _deliveryFiles = build.DeliveryFiles.ToDictionary(x => x.Name, StringComparer.Ordinal);
            _bundleChanges = build.BundleChanges.ToDictionary(x => x.Name, StringComparer.Ordinal);
            _projectedCosts = "构建总耗时 " + build.ElapsedMilliseconds.ToString("F2") + " ms · BuildPipeline " + (build.WasUnityBuildInvoked ? build.BuildPipelineMilliseconds.ToString("F2") + " ms" : "未调用") +
                "\n分包计算（含构建后复核） " + build.MapMilliseconds.ToString("F2") + " ms · 输入指纹（含构建后复核） " + build.InputFingerprintMilliseconds.ToString("F2") +
                " ms · 产物哈希 " + build.ArtifactHashMilliseconds.ToString("F2") + " ms · 原生读回 " + build.ReadbackMilliseconds.ToString("F2") +
                " ms\nBundle 文件字节 " + build.BundleFileBytes + " · 本地发布 " + (_report.PublicationMilliseconds?.ToString("F2") ?? "未执行") +
                " ms · 常量生成 " + (_report.GenerationMilliseconds?.ToString("F2") ?? "未执行") + " ms";
            _projectedChanges = "Bundle 内容对比：变化 " + build.BundleChanges.Count(x => x.ContentChanged == true) + " · 未变 " + build.BundleChanges.Count(x => x.ContentChanged == false) +
                " · 无可比基线 " + build.BundleChanges.Count(x => !x.ContentChanged.HasValue);
        }
    }
}
