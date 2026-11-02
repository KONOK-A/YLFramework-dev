using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;

namespace YLFramework.Integrations.HybridCLR.Editor
{
    /// <summary>所有副作用仅由按钮显式发起；选中、绘制、打开窗口不保存或生成。</summary>
    public sealed class HybridCLRWindow : EditorWindow
    {
        [SerializeField] private HybridCLRBuildProfile profile;
        [SerializeField] private string playerPath;
        [SerializeField] private int page;
        private static readonly string[] _pages = { "配置", "构建发布", "诊断" };
        private Vector2 _scroll;
        private string _officialStatus;
        private string _officialError;
        private string[] _configuredNames = Array.Empty<string>();
        private string _assemblyError;
        private bool _showGlobalAssemblies;
        private UnityEngine.Object _assemblyAsset;
        private HybridCLRProfileInspection _inspection;
        private bool _isInspectionStale;
        private string _inspectionTime;
        private HybridCLRBuildReport _report;
        private string _reportTitle;
        private string _reportContext;
        private bool[] _expandedStages = Array.Empty<bool>();
        private string _notice;
        private MessageType _noticeType;
        private Exception _failure;
        private bool _showFailure;
        private Action _pendingAction;
        private bool _shouldRestartLayout;
        private GUIStyle _titleStyle;
        private GUIStyle _secondaryStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _cardStyle;
        private GUIStyle _rawStyle;
        private GUIStyle _navigationStyle;
        private Rect _navigationBounds;
        private bool _isDarkStyle;

        private static bool IsEditorBusy => EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer;

        // 初始化窗口尺寸及只读状态，并订阅配置撤销通知。
        private void OnEnable()
        {
            page = Mathf.Clamp(page, 0, _pages.Length - 1);
            minSize = new Vector2(540, 440);
            ReadOfficialStatus();
            Undo.undoRedoPerformed += ConfigurationChanged;
        }

        // 解绑撤销通知并放弃尚未执行的界面动作。
        private void OnDisable()
        {
            Undo.undoRedoPerformed -= ConfigurationChanged;
            _pendingAction = null;
        }

        // 将工程资产变化反映到配置检查快照的过期状态。
        private void OnProjectChange() => ConfigurationChanged();

        // 标记已有检查过期并重绘，不因变化自动重跑检查或保存。
        private void ConfigurationChanged()
        {
            if (_inspection != null)
            {
                _isInspectionStale = true;
            }

            Repaint();
        }

        // 只读取官方安装状态和全局登记名单，失败分别保留诊断。
        private void ReadOfficialStatus()
        {
            try
            {
                var installer = new global::HybridCLR.Installer.InstallerController();
                _officialStatus = "官方包 " + installer.PackageVersion + "  /  本地运行时 " +
                    (installer.HasInstalledHybridCLR() ? installer.InstalledLibil2cppVersion : "未安装");
                _officialError = null;
            }
            catch (Exception error) when (!IsExitGui(error))
            {
                _officialStatus = "官方安装状态暂不可用";
                _officialError = error.ToString();
            }
            try
            {
                _configuredNames = HybridCLRBuildPipeline.ConfiguredNames();
                _assemblyError = null;
            }
            catch (Exception error) when (!IsExitGui(error))
            {
                _configuredNames = Array.Empty<string>();
                _assemblyError = error.Message;
            }
        }

        // 通过菜单打开集成窗口，不触发安装或构建。
        [MenuItem("YLFramework/Integrations/HybridCLR")]
        private static void Open() => GetWindow<HybridCLRWindow>("HybridCLR");

        // 绘制当前任务页并在布局作用域退出后执行用户明确选择的动作。
        private void OnGUI()
        {
            EnsureStyles();
            _pendingAction = null;
            _shouldRestartLayout = false;
            var previousLabelWidth = EditorGUIUtility.labelWidth;
            var previousWideMode = EditorGUIUtility.wideMode;
            try
            {
                EditorGUIUtility.labelWidth = 128;
                EditorGUIUtility.wideMode = true;
                using (var scrolling = new EditorGUILayout.ScrollViewScope(_scroll))
                {
                    _scroll = scrolling.scrollPosition;
                    using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(0)))
                    {
                        GUILayout.Space(6);
                        DrawHeader();
                        DrawProfileBar();
                        DrawNavigation();
                        if (!string.IsNullOrEmpty(_notice))
                        {
                            EditorGUILayout.HelpBox(_notice, _noticeType);
                            if (page != 2)
                            {
                                ActionButton("查看诊断", () => ChangePage(2), 90);
                            }
                        }
                        switch (page)
                        {
                            case 0:
                                DrawConfiguration();
                                break;
                            case 1:
                                DrawBuild();
                                break;
                            case 2:
                                DrawDiagnostics();
                                break;
                        }
                        GUILayout.Space(8);
                    }
                }
            }
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
                EditorGUIUtility.wideMode = previousWideMode;
            }

            // 操作在所有 layout scope 与 SerializedObject 释放后执行，避免导入或切页打断布局。
            var action = _pendingAction;
            _pendingAction = null;
            if (action != null)
            {
                Run(action);
            }

            if (action != null || _shouldRestartLayout)
            {
                GUIUtility.ExitGUI();
            }
        }

        // 显示官方状态及显式设置、安装器和刷新入口。
        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("HybridCLR", _titleStyle, GUILayout.MinWidth(0));
                GUILayout.FlexibleSpace();
                ActionButton("官方设置", () => SettingsService.OpenProjectSettings("Project/HybridCLR Settings"), 76);
                using (new EditorGUI.DisabledScope(IsEditorBusy))
                {
                    ActionButton("安装器", OpenInstaller, 62);
                    ActionButton("刷新状态", ReadOfficialStatus, 76);
                }
            }
            GUILayout.Label(new GUIContent(_officialStatus ?? "尚未读取官方状态", _officialError), _secondaryStyle, GUILayout.MinWidth(0));
            if (_officialError != null)
            {
                EditorGUILayout.HelpBox("读取安装状态失败。可通过官方设置或安装器手动检查，窗口不会自动安装。", MessageType.Warning);
            }
        }

        // 显示配置选择和独立新建、保存按钮，以及所引用资产的保存状态。
        private void DrawProfileBar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var next = (HybridCLRBuildProfile)EditorGUILayout.ObjectField("构建配置", profile, typeof(HybridCLRBuildProfile), false, GUILayout.MinWidth(0));
                if (next != profile)
                {
                    _pendingAction = () => SelectProfile(next);
                }

                using (new EditorGUI.DisabledScope(IsEditorBusy))
                {
                    ActionButton("新建", CreateProfile, 50);
                    using (new EditorGUI.DisabledScope(!profile))
                    {
                        ActionButton("保存", SaveProfile, 50);
                    }
                }
            }
            if (profile)
            {
                var recipe = profile.recipe;
                var project = recipe ? recipe.project : null;
                GUILayout.Label("Profile：" + AssetState(profile) + "  /  Recipe：" + AssetState(recipe) + "  /  Project：" + AssetState(project), _secondaryStyle, GUILayout.MinWidth(0));
            }
            GUILayout.Space(6);
        }

        // 根据窗口宽度绘制任务导航，切页动作留待布局结束执行。
        private void DrawNavigation()
        {
            page = Mathf.Clamp(page, 0, _pages.Length - 1);
            var next = page;
            if (position.width < 680)
            {
                next = EditorGUILayout.Popup("任务", page, _pages, GUILayout.Height(26));
            }
            else
            {
                _navigationBounds = GUILayoutUtility.GetRect(GUIContent.none, _navigationStyle, GUILayout.Height(30), GUILayout.ExpandWidth(true));
                const float Gap = 4;
                var width = (_navigationBounds.width - Gap * (_pages.Length - 1)) / _pages.Length;
                for (var index = 0; index < _pages.Length; index++)
                {
                    var rect = new Rect(_navigationBounds.x + index * (width + Gap), _navigationBounds.y, width, _navigationBounds.height);
                    var selected = page == index;
                    if (GUI.Toggle(rect, selected, _pages[index], _navigationStyle) && !selected)
                    {
                        next = index;
                    }

                    if (selected && Event.current.type == EventType.Repaint)
                    {
                        EditorGUI.DrawRect(new Rect(rect.x + 4, rect.yMax - 3, rect.width - 8, 2), new Color(.2f, .6f, .85f));
                    }
                }
            }
            if (next != page)
            {
                _pendingAction = () => ChangePage(next);
            }

            GUILayout.Space(8);
        }

        // 未选择配置时显示创建引导，通知调用方停止绘制配置相关内容。
        private bool DrawProfileGuide()
        {
            if (profile)
            {
                return false;
            }

            using (new EditorGUILayout.VerticalScope(_cardStyle))
            {
                CardHeading("从构建配置开始", "选择已有 Profile，或新建一组自有 Profile、Resource Recipe 与 Resource Project。不会创建演示内容，也不会改写官方程序集设置。");
                using (new EditorGUI.DisabledScope(IsEditorBusy))
                {
                    ActionButton("新建构建配置", CreateProfile, 140);
                }
            }
            return true;
        }

        // 通过可撤销的序列化编辑展示配置、入口及资源关联操作，不隐式保存。
        private void DrawConfiguration()
        {
            if (DrawProfileGuide())
            {
                return;
            }

            var displayedRecipe = profile.recipe;
            using (new EditorGUILayout.VerticalScope(_cardStyle))
            {
                CardHeading("构建配置", "编辑只产生可撤销的资产修改；顶部“保存”只保存当前 Profile、Recipe 与 Project，不保存其他资产。");
                using (new EditorGUI.DisabledScope(IsEditorBusy))
                using (var serialized = new SerializedObject(profile))
                {
                    serialized.Update();
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.PropertyField(serialized.FindProperty("recipe"), new GUIContent("Resource Recipe"), GUILayout.MinWidth(0));
                        var recipe = serialized.FindProperty("recipe").objectReferenceValue;
                        using (new EditorGUI.DisabledScope(!recipe))
                        {
                            ActionButton("Inspector", () => EditorUtility.OpenPropertyEditor(recipe), 76);
                        }
                    }
                    EditorGUILayout.PropertyField(serialized.FindProperty("development"), new GUIContent("Development 构建"));
                    DrawDirectoryProperty(serialized, "GeneratedAssetDirectory", "生成资源目录", true);
                    DrawDirectoryProperty(serialized, "BaselineDirectory", "不可变基座目录", false);
                    EditorGUILayout.PropertyField(serialized.FindProperty("baseProfile"), new GUIContent("共享基座 Profile", "留空由本配置构建新基座；只允许一层绑定，目录与兼容身份需和基座拥有者一致，不会自动覆盖。绑定后仅发布项目补丁，不重新生成原生代码。"));
                    if (serialized.ApplyModifiedProperties())
                    {
                        ConfigurationChanged();
                        _shouldRestartLayout = profile.recipe != displayedRecipe;
                    }
                }
                GUILayout.Label("生成目录使用 Assets 下的自有路径。新建基座必须填写尚不存在的目录；选择已有目录适用于读取冻结基座。", _secondaryStyle, GUILayout.MinWidth(0));
                DrawResourceIdentity(displayedRecipe);
            }
            GUILayout.Space(6);
            DrawAssemblies();
            GUILayout.Space(6);
            using (new EditorGUILayout.VerticalScope(_cardStyle))
            {
                CardHeading("资源关联与检查", "生成目录变更后需显式重新关联。关联只修改当前 Resource Project，可撤销，不生成 DLL、不安装运行时，也不隐式保存。");
                using (new EditorGUI.DisabledScope(IsEditorBusy || !displayedRecipe || !displayedRecipe.project))
                using (new EditorGUILayout.HorizontalScope())
                {
                    ActionButton("关联生成资源", MapGeneratedResources, 126);
                    ActionButton("检查配置", InspectProfile, 100);
                }
                GUILayout.Label("“检查配置”解析项目入口与依赖闭包，不要求当前工程使用 IL2CPP。原生环境检查在“构建发布”页。", _secondaryStyle, GUILayout.MinWidth(0));
                DrawInspection();
            }
        }

        // 只读展示 Recipe 提供的项目、目标和兼容身份，避免另建身份来源。
        private void DrawResourceIdentity(ResourceBuildRecipe recipe)
        {
            GUILayout.Space(6);
            if (!recipe)
            {
                EditorGUILayout.HelpBox("请为当前 Profile 选择 Resource Recipe。", MessageType.Warning);
                return;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField("Resource Project", recipe.project, typeof(ResourceProject), false, GUILayout.MinWidth(0));
                }

                using (new EditorGUI.DisabledScope(!recipe.project))
                {
                    ActionButton("Inspector", () => EditorUtility.OpenPropertyEditor(recipe.project), 76);
                }
            }
            if (!recipe.project)
            {
                EditorGUILayout.HelpBox("Recipe 尚未引用 Resource Project，请在其 Inspector 中配置。", MessageType.Warning);
            }
            else
            {
                Metric("项目身份", recipe.project.projectId);
            }

            Metric("目标平台", recipe.target.ToString());
            Metric("兼容身份", recipe.appContentCompatibilityId);
            GUILayout.Label("兼容身份来自 Recipe；修改冻结基座的身份或原生要求后，应新建基座，不能作为普通补丁发布。", _secondaryStyle, GUILayout.MinWidth(0));
        }

        // 绘制指定序列化目录字段，并提供延后执行的路径选择动作。
        private void DrawDirectoryProperty(SerializedObject serialized, string propertyName, string label, bool assetsOnly)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var property = serialized.FindProperty(propertyName);
                EditorGUILayout.PropertyField(property, new GUIContent(label), GUILayout.MinWidth(0));
                var current = property.stringValue;
                ActionButton("选择", () => PickDirectory(propertyName, current, label, assetsOnly), 50);
            }
        }

        // 分开展示只读全局登记与可编辑项目入口，空集合明确表示使用全部。
        private void DrawAssemblies()
        {
            using (new EditorGUILayout.VerticalScope(_cardStyle))
            {
                CardHeading("项目程序集", "官方集合决定全局热更登记；本项目只选择入口，依赖由检查与构建自动包含。这里从不写入 HybridCLRSettings。");
                _showGlobalAssemblies = EditorGUILayout.Foldout(_showGlobalAssemblies, "官方登记集合（" + _configuredNames.Length + "，只读）", true);
                if (!string.IsNullOrEmpty(_assemblyError))
                {
                    EditorGUILayout.HelpBox("官方集合尚不可用，请在官方设置中登记后刷新状态。\n" + _assemblyError, MessageType.Warning);
                }

                if (_showGlobalAssemblies)
                {
                    foreach (var name in _configuredNames)
                    {
                        GUILayout.Label(name, _bodyStyle, GUILayout.MinWidth(0));
                    }

                    GUILayout.Label("此处为上次读取的全局名单；修改官方设置后，点击顶部“刷新状态”。", _secondaryStyle, GUILayout.MinWidth(0));
                }
                var roots = profile.hotUpdateAssemblies ?? Array.Empty<string>();
                var singleRoot = roots.Distinct(StringComparer.Ordinal).Count() <= 1;
                using (new EditorGUI.DisabledScope(IsEditorBusy))
                {
                    if (roots.Length == 0)
                    {
                        EditorGUILayout.HelpBox("当前使用官方全部程序集。空入口数组表示“全部”，不是“未选择”。", MessageType.Info);
                        using (new EditorGUI.DisabledScope(_configuredNames.Length == 0))
                        {
                            ActionButton("选择项目入口", () => WriteRoots(new[] { _configuredNames[0] }), 126);
                        }
                    }
                    else
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Label("项目入口（" + roots.Length + "）", EditorStyles.boldLabel, GUILayout.MinWidth(0));
                            GUILayout.FlexibleSpace();
                            ActionButton("使用全部", () => WriteRoots(Array.Empty<string>()), 90);
                        }
                        foreach (var name in _configuredNames)
                        {
                            var selected = roots.Contains(name, StringComparer.Ordinal);
                            using (new EditorGUI.DisabledScope(selected && singleRoot))
                            {
                                var next = EditorGUILayout.ToggleLeft(new GUIContent(name, name), selected, _bodyStyle, GUILayout.MinHeight(22), GUILayout.MinWidth(0));
                                if (next != selected)
                                {
                                    _pendingAction = () => ToggleRoot(name, next);
                                }
                            }
                        }
                        foreach (var name in roots.Where(name => !_configuredNames.Contains(name, StringComparer.Ordinal)))
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Label((string.IsNullOrEmpty(name) ? "（空入口）" : name) + "  [未在官方登记]", _bodyStyle, GUILayout.MinWidth(0));
                                using (new EditorGUI.DisabledScope(singleRoot))
                                {
                                    ActionButton("移除", () => ToggleRoot(name, false), 50);
                                }
                            }
                        }
                        GUILayout.Label("可多选入口。最后一个入口不能直接取消；如需恢复全局集合，请明确点击“使用全部”。", _secondaryStyle, GUILayout.MinWidth(0));
                        if (roots.Any(name => !_configuredNames.Contains(name, StringComparer.Ordinal)))
                        {
                            EditorGUILayout.HelpBox("存在尚未登记的项目入口。请在官方设置中自行登记，再刷新并检查配置；窗口不会代为登记。", MessageType.Warning);
                        }
                    }
                    GUILayout.Space(6);
                    _assemblyAsset = EditorGUILayout.ObjectField("asmdef / DLL 资产", _assemblyAsset, typeof(UnityEngine.Object), false, GUILayout.MinWidth(0));
                    using (new EditorGUI.DisabledScope(!_assemblyAsset))
                    {
                        ActionButton("添加为项目入口", AddAssemblyAsset, 140);
                    }
                }
            }
        }

        // 展示原生检查、新基座、补丁和部署入口，绑定共享基座时禁用新建基座。
        private void DrawBuild()
        {
            if (DrawProfileGuide())
            {
                return;
            }

            using (new EditorGUILayout.VerticalScope(_cardStyle))
            {
                CardHeading("构建前检查", "仅支持已激活的 Windows64、IL2CPP 与 .NET Framework，官方包和本地运行时须匹配 9.0.3。窗口不切换后端、平台或安装工具链。");
                DrawReadiness();
                using (new EditorGUI.DisabledScope(IsEditorBusy || !profile.recipe || !profile.recipe.project))
                using (new EditorGUILayout.HorizontalScope())
                {
                    ActionButton("检查配置", InspectProfile, 100);
                    ActionButton("检查原生构建环境", ValidateNative, 158);
                }
            }
            GUILayout.Space(6);
            var sharedBase = HasSharedBase();
            using (new EditorGUILayout.VerticalScope(_cardStyle))
            {
                CardHeading("新基座", "依次生成、发布、部署并构建原生 Player，成功后冻结基座。需要启用的构建场景、全新的 Player 输出目录与尚不存在的基座目录，不覆盖已有 Player。");
                using (new EditorGUILayout.HorizontalScope())
                {
                    playerPath = EditorGUILayout.TextField("Player (.exe)", playerPath ?? "", GUILayout.MinWidth(0));
                    ActionButton("选择", PickPlayerPath, 50);
                }
                if (sharedBase)
                {
                    EditorGUILayout.HelpBox("当前 Profile 引用了共享基座，只能基于它发布项目补丁。新基座需使用未引用 BaseProfile 的配置。", MessageType.Info);
                }

                using (new EditorGUI.DisabledScope(!CanRunNative() || sharedBase || string.IsNullOrWhiteSpace(playerPath)))
                {
                    ActionButton("构建新基座", BuildBase, 126);
                }
            }
            GUILayout.Space(6);
            using (new EditorGUILayout.VerticalScope(_cardStyle))
            {
                CardHeading("发布补丁", "针对已冻结基座发布当前项目的程序集和资源，不重新生成原生基座。新增程序集、AOT 元数据或原生桥接要求可能需要新基座，最终以兼容检查为准。");
                using (new EditorGUI.DisabledScope(!CanRunNative()))
                {
                    ActionButton("发布项目补丁", PublishPatch, 126);
                }
            }
            GUILayout.Space(6);
            using (new EditorGUILayout.VerticalScope(_cardStyle))
            {
                CardHeading("部署内置资源", "把当前 Recipe 对应的已发布版本部署到 StreamingAssets。此操作会改变工程内置内容，不会替你发布新版本，也不会自动构建 Player。");
                using (new EditorGUI.DisabledScope(!CanRunNative()))
                {
                    ActionButton("部署当前发布版本", Deploy, 158);
                }
            }
            GUILayout.Label("签名与解密密钥由应用通过 headless API 提供，不在窗口或资产中保存。", _secondaryStyle, GUILayout.MinWidth(0));
        }

        // 展示 Editor 忙碌、编译错误和配置保存门禁，不把按钮可用当作原生验证通过。
        private void DrawReadiness()
        {
            if (IsEditorBusy)
            {
                EditorGUILayout.HelpBox("编辑器正在编译、导入、进入/运行 Play Mode 或构建 Player，原生操作暂不可用。", MessageType.Warning);
            }
            else if (EditorUtility.scriptCompilationFailed)
            {
                EditorGUILayout.HelpBox("请先修复现有脚本编译错误，再执行原生操作。", MessageType.Warning);
            }

            if (!profile.recipe || !profile.recipe.project)
            {
                EditorGUILayout.HelpBox("请先在配置页引用完整的 Resource Recipe 与 Project。", MessageType.Warning);
            }
            else if (!AreAssetsSaved())
            {
                EditorGUILayout.HelpBox("当前 Profile、Recipe 或 Project 尚未保存。请先点击顶部“保存”，构建操作不会隐式保存。", MessageType.Warning);
            }
            else
            {
                GUILayout.Label("Profile、Recipe 与 Project 已保存；环境与基座兼容性仍需显式检查。", _secondaryStyle, GUILayout.MinWidth(0));
            }
        }

        // 显示检查快照、原始异常及实际阶段结果，不为未执行步骤推断成功。
        private void DrawDiagnostics()
        {
            using (new EditorGUILayout.VerticalScope(_cardStyle))
            {
                CardHeading("配置诊断", "显示最近一次显式检查的结果。修改配置、官方登记或程序集后请重新检查；配置通过不代表原生环境或补丁兼容性通过。");
                using (new EditorGUI.DisabledScope(IsEditorBusy || !profile || !profile.recipe || !profile.recipe.project))
                {
                    ActionButton("检查配置", InspectProfile, 100);
                }

                DrawInspection();
            }
            if (_failure != null)
            {
                GUILayout.Space(6);
                using (new EditorGUILayout.VerticalScope(_cardStyle))
                {
                    EditorGUILayout.HelpBox("操作失败：" + _failure.Message, MessageType.Error);
                    _showFailure = EditorGUILayout.Foldout(_showFailure, "原始异常", true);
                    if (_showFailure)
                    {
                        Copyable("异常详情", _failure.ToString(), true);
                    }
                }
            }
            GUILayout.Space(6);
            using (new EditorGUILayout.VerticalScope(_cardStyle))
            {
                CardHeading("构建与发布记录", "仅显示实际返回的阶段；同步操作不展示估算进度。未出现的后续阶段未执行，不表示成功。");
                if (_report == null)
                {
                    GUILayout.Label("尚无原生检查、构建、发布或部署报告。", _secondaryStyle, GUILayout.MinWidth(0));
                    return;
                }
                GUILayout.Label(_reportTitle, EditorStyles.boldLabel);
                GUILayout.Label(_reportContext, _secondaryStyle, GUILayout.MinWidth(0));
                var failed = _report.Stages.FirstOrDefault(stage => !stage.HasSucceeded);
                if (failed != null)
                {
                    EditorGUILayout.HelpBox("失败阶段：" + StageName(failed.Name) + "\n" + failed.Error.Message, MessageType.Error);
                }
                else
                {
                    EditorGUILayout.HelpBox(_report.HasSucceeded ? "所有已执行阶段通过。" : "没有已完成的阶段。", _report.HasSucceeded ? MessageType.Info : MessageType.Warning);
                }

                ActionButton("复制完整报告", () => EditorGUIUtility.systemCopyBuffer = _report.Summary, 126);
                for (var index = 0; index < _report.Stages.Count; index++)
                {
                    var stage = _report.Stages[index];
                    _expandedStages[index] = EditorGUILayout.Foldout(_expandedStages[index], (stage.HasSucceeded ? "通过  " : "失败  ") + StageName(stage.Name), true);
                    if (!_expandedStages[index])
                    {
                        continue;
                    }

                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        GUILayout.Label("阶段标识：" + stage.Name, _secondaryStyle, GUILayout.MinWidth(0));
                        if (stage.Error != null)
                        {
                            Copyable("原始异常", stage.Error.ToString(), true);
                        }

                        foreach (var diagnostic in stage.Diagnostics)
                        {
                            Copyable("阶段诊断", diagnostic, true);
                        }

                        if (stage.HasSucceeded && stage.Diagnostics.Count == 0)
                        {
                            GUILayout.Label("该阶段已完成，无额外诊断。", _secondaryStyle);
                        }
                    }
                }
                if (!string.IsNullOrEmpty(_report.BaselineDirectory))
                {
                    Copyable("冻结基座", _report.BaselineDirectory);
                }

                if (_report.Manifest != null)
                {
                    Copyable("代码包身份", _report.Manifest.CodePackageId);
                }

                if (_report.Release != null)
                {
                    Copyable("资源目录摘要", _report.Release.Catalog.CatalogDigest);
                }

                if (_report.Deployment != null)
                {
                    Copyable("部署目录", _report.Deployment.DirectoryPath);
                }
            }
        }

        // 展示最近一次显式配置检查及其过期提示、依赖和警告。
        private void DrawInspection()
        {
            if (_inspection == null)
            {
                GUILayout.Label("尚未检查配置。检查后在此展示项目程序集、自动依赖和错误警告。", _secondaryStyle, GUILayout.MinWidth(0));
                return;
            }
            GUILayout.Space(6);
            GUILayout.Label("上次检查 " + _inspectionTime + (_isInspectionStale ? "，配置已变化，请重新检查。" : "（配置快照）"), _secondaryStyle, GUILayout.MinWidth(0));
            if (_inspection.HasSucceeded)
            {
                EditorGUILayout.HelpBox(_isInspectionStale ? "上次配置检查通过，当前配置需重新检查。" : "配置检查通过，不要求 IL2CPP。", _isInspectionStale ? MessageType.Warning : MessageType.Info);
            }

            foreach (var error in _inspection.Errors)
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }

            foreach (var warning in _inspection.Warnings)
            {
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
            }

            Copyable("解析后的项目程序集（含依赖）", _inspection.Assemblies.Length == 0 ? "无" : string.Join("\n", _inspection.Assemblies));
            Copyable("自动包含的依赖", _inspection.Dependencies.Length == 0 ? "无" : string.Join("\n", _inspection.Dependencies));
        }

        // 切换当前配置并清除旧诊断快照，不保存或修改被选择资产。
        private void SelectProfile(HybridCLRBuildProfile value)
        {
            profile = value;
            _assemblyAsset = null;
            _inspection = null;
            _report = null;
            _failure = null;
            _notice = null;
            _scroll = Vector2.zero;
        }

        // 进入任务页时从页首展示，避免沿用上一页的滚动偏移。
        private void ChangePage(int value)
        {
            page = value;
            _scroll = Vector2.zero;
        }

        // 经用户选址创建独立配置组，并将新 Profile 设为当前配置。
        private void CreateProfile()
        {
            RequireIdleEditor();
            var path = EditorUtility.SaveFilePanelInProject("新建 HybridCLR 构建配置", "HybridCLRBuildProfile", "asset", "创建独立的 Profile、Recipe、Project 与自有生成目录，不创建示例。");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            SelectProfile(HybridCLRProfileTools.Create(path));
            page = 0;
            Notify("已创建并保存 Profile、Recipe 与 Project，已关联其自有生成目录。请按项目需求选择程序集入口并检查配置。");
        }

        // 仅保存当前 Project、Recipe 和 Profile，并确认未残留脏标记。
        private void SaveProfile()
        {
            RequireIdleEditor();
            RequireProfile(false);
            AssetDatabase.SaveAssetIfDirty(profile.recipe.project);
            AssetDatabase.SaveAssetIfDirty(profile.recipe);
            AssetDatabase.SaveAssetIfDirty(profile);
            if (!AreAssetsSaved())
            {
                throw new IOException("部分资产仍有未保存修改，请检查文件写入权限与 Unity Console。");
            }

            Notify("已逐一保存当前 Project、Recipe 与 Profile，未保存其他资产。");
        }

        // 显式关联生成资源并使检查过期，将保存决定留给用户。
        private void MapGeneratedResources()
        {
            RequireIdleEditor();
            RequireProfile(false);
            HybridCLRProfileTools.MapGeneratedResources(profile);
            ConfigurationChanged();
            Notify("已关联生成资源，仅修改 Resource Project，可撤销。请点击顶部“保存”保存修改。", MessageType.Warning);
        }

        // 显式刷新官方状态与配置快照，并转到诊断页展示结果。
        private void InspectProfile()
        {
            RequireIdleEditor();
            RequireProfile(false);
            ReadOfficialStatus();
            _inspection = HybridCLRProfileTools.Inspect(profile);
            _isInspectionStale = false;
            _inspectionTime = DateTime.Now.ToString("HH:mm:ss");
            Notify(_inspection.HasSucceeded ? "配置检查通过；未执行原生环境验证或构建。" : "配置检查未通过，请处理下方错误。", _inspection.HasSucceeded ? MessageType.Info : MessageType.Error);
            ChangePage(2);
        }

        // 在已保存配置上执行严格原生环境预检，不生成代码或 Player。
        private void ValidateNative()
        {
            RequireProfile(true);
            SetReport("原生构建环境检查", HybridCLRBuildPipeline.Validate(profile));
            ReadOfficialStatus();
        }

        // 经确认后把精确场景与 Player 输出选项交给新基座管线。
        private void BuildBase()
        {
            RequireProfile(true);
            if (HasSharedBase())
            {
                throw new InvalidOperationException("引用共享基座的 Profile 不能构建新基座。");
            }

            if (string.IsNullOrWhiteSpace(playerPath))
            {
                throw new InvalidOperationException("请先选择新的 Player .exe 输出位置。");
            }

            if (!EditorUtility.DisplayDialog("构建新基座", "将生成原生桥接和热更资源、发布并部署资源、构建 Windows Player，最后冻结不可变基座。\n\nPlayer：" + playerPath + "\n基座：" + profile.baselineDirectory + "\n\n不会隐式保存资产或覆盖已有 Player；失败可能留下已完成阶段的产物。", "构建新基座", "取消"))
            {
                return;
            }

            SetReport("构建新基座", HybridCLRBuildPipeline.BuildBasePlayer(profile, new BuildPlayerOptions
            {
                target = profile.recipe.target,
                targetGroup = BuildTargetGroup.Standalone,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                locationPathName = playerPath,
                scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray(),
                options = profile.development ? BuildOptions.Development : BuildOptions.None
            }));
        }

        // 经确认后按冻结基座发布项目补丁，不自动部署。
        private void PublishPatch()
        {
            RequireProfile(true);
            if (!EditorUtility.DisplayDialog("发布项目补丁", "将以冻结基座检查兼容性，并更新当前 Resource Project 的发布版本。不重新生成或构建原生基座，不自动部署。\n\n项目：" + profile.recipe.project.projectId + "\n兼容身份：" + profile.recipe.appContentCompatibilityId, "发布补丁", "取消"))
            {
                return;
            }

            SetReport("发布项目补丁", HybridCLRBuildPipeline.PublishPatch(profile));
        }

        // 经确认并完成预检后部署当前 Recipe 的已发布版本，原样记录部署错误。
        private void Deploy()
        {
            RequireProfile(true);
            if (!EditorUtility.DisplayDialog("部署当前发布版本", "将修改 StreamingAssets 中该项目的内置资源。请确认使用当前 Recipe、平台与兼容身份对应的已发布版本。\n\n项目：" + profile.recipe.project.projectId + "\n平台：" + profile.recipe.target + "\n兼容身份：" + profile.recipe.appContentCompatibilityId, "部署内置资源", "取消"))
            {
                return;
            }

            var result = HybridCLRBuildPipeline.Validate(profile);
            if (result.HasSucceeded)
            {
                try
                {
                    var release = ResourcePublisher.ReadCurrent(profile.recipe.project, profile.recipe.target, profile.recipe.appContentCompatibilityId);
                    if (release == null)
                    {
                        throw new InvalidOperationException("当前 Recipe 尚无对应的已发布版本，请先发布。");
                    }

                    result.Release = release;
                    result.Deployment = ResourceBuiltInDeployer.Deploy(ResourceBuiltInDeployer.Preflight(release));
                    result.Add("Deploy");
                }
                catch (Exception error) when (!IsExitGui(error))
                {
                    result.Add("Deploy", error);
                }
            }
            SetReport("部署内置资源", result);
        }

        // 只打开官方 Installer 菜单，安装行为仍需用户在官方界面发起。
        private void OpenInstaller()
        {
            RequireIdleEditor();
            if (!EditorApplication.ExecuteMenuItem("HybridCLR/Installer..."))
            {
                throw new InvalidOperationException("官方 Installer 菜单不可用，请检查官方包是否已导入。");
            }
        }

        // 修改单个项目入口，拒绝用取消最后一项隐式恢复官方全量。
        private void ToggleRoot(string name, bool selected)
        {
            var roots = profile.hotUpdateAssemblies ?? Array.Empty<string>();
            var next = selected ? roots.Append(name).Distinct(StringComparer.Ordinal).ToArray() : roots.Where(root => root != name).ToArray();
            if (!selected && next.Length == 0)
            {
                throw new InvalidOperationException("不能移除最后一个入口。请点击“使用全部”明确恢复官方全部集合。");
            }

            WriteRoots(next);
        }

        // 通过 SerializedObject 写入项目入口数组，使改动支持 Undo 且不隐式保存。
        private void WriteRoots(string[] roots)
        {
            RequireIdleEditor();
            using (var serialized = new SerializedObject(profile))
            {
                serialized.Update();
                var property = serialized.FindProperty("hotUpdateAssemblies");
                property.arraySize = roots.Length;
                for (var index = 0; index < roots.Length; index++)
                {
                    property.GetArrayElementAtIndex(index).stringValue = roots[index];
                }

                if (serialized.ApplyModifiedProperties())
                {
                    ConfigurationChanged();
                }
            }
        }

        /// <summary>仅用于读取所选 asmdef 名称的最小 JSON 数据结构。</summary>
        [Serializable]
        private sealed class AssemblyDefinition
        {
            /// <summary>asmdef 文件声明的程序集名称。</summary>
            public string name = "";
        }

        // 从明确选择的 asmdef 或托管 DLL 取得入口名称，不自动登记官方全局集合。
        private void AddAssemblyAsset()
        {
            RequireIdleEditor();
            var path = AssetDatabase.GetAssetPath(_assemblyAsset);
            string name;
            if (string.Equals(Path.GetExtension(path), ".asmdef", StringComparison.OrdinalIgnoreCase))
            {
                name = JsonUtility.FromJson<AssemblyDefinition>(File.ReadAllText(path))?.name;
            }
            else if (string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                name = System.Reflection.AssemblyName.GetAssemblyName(Path.GetFullPath(path)).Name;
            }
            else
            {
                throw new InvalidOperationException("请选择现有的 .asmdef 或托管 .dll 资产。");
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidDataException("所选资产没有有效的程序集名称。");
            }

            WriteRoots((profile.hotUpdateAssemblies ?? Array.Empty<string>()).Append(name).Distinct(StringComparer.Ordinal).ToArray());
            _assemblyAsset = null;
            var registered = _configuredNames.Contains(name, StringComparer.Ordinal);
            Notify(registered ? "已添加项目入口：" + name + "。请保存并检查配置以解析依赖。" : "已添加项目入口：" + name + "，但尚未在官方集合登记。请自行登记后刷新并检查配置，窗口未修改官方设置。", registered ? MessageType.Info : MessageType.Warning);
        }

        // 经用户选址写入目录字段；生成资源目录必须位于当前 Assets 下。
        private void PickDirectory(string propertyName, string current, string label, bool assetsOnly)
        {
            RequireIdleEditor();
            var path = EditorUtility.OpenFolderPanel("选择" + label, DialogDirectory(current), "");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (assetsOnly)
            {
                path = FileUtil.GetProjectRelativePath(path).Replace('\\', '/');
                if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("生成资源目录必须是当前工程 Assets 下的自有子目录。");
                }
            }
            using (var serialized = new SerializedObject(profile))
            {
                serialized.Update();
                serialized.FindProperty(propertyName).stringValue = path;
                if (serialized.ApplyModifiedProperties())
                {
                    ConfigurationChanged();
                }
            }
        }

        // 仅选择 Windows Player 输出路径，实际新目录门禁由构建管线复核。
        private void PickPlayerPath()
        {
            var path = EditorUtility.SaveFilePanel("选择新的 Windows Player", DialogDirectory(playerPath), string.IsNullOrWhiteSpace(playerPath) ? "Game" : Path.GetFileNameWithoutExtension(playerPath), "exe");
            if (!string.IsNullOrEmpty(path))
            {
                playerPath = path;
            }
        }

        // 为选择对话框找到最近存在的祖先目录，缺省回到工程根。
        private static string DialogDirectory(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var directory = Path.GetFullPath(path);
                while (!string.IsNullOrEmpty(directory))
                {
                    if (Directory.Exists(directory))
                    {
                        return directory;
                    }

                    directory = Path.GetDirectoryName(directory);
                }
            }
            return Path.GetDirectoryName(Application.dataPath);
        }

        // 判断当前配置是否显式绑定其他基座拥有者。
        private bool HasSharedBase() => profile && profile.baseProfile;

        // 提供界面的最低执行门禁，完整原生环境仍由管线验证。
        private bool CanRunNative() => !IsEditorBusy && !EditorUtility.scriptCompilationFailed && AreAssetsSaved();

        // 确认当前配置组均为已保存且未修改的资产。
        private bool AreAssetsSaved() => profile && profile.recipe && profile.recipe.project &&
            IsSaved(profile) && IsSaved(profile.recipe) && IsSaved(profile.recipe.project);

        // 以 Unity 存活语义、资产路径和脏标记判断保存状态。
        private static bool IsSaved(UnityEngine.Object asset) => asset && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(asset)) && !EditorUtility.IsDirty(asset);

        // 为配置栏区分未引用、未落盘、已修改和已保存状态。
        private static string AssetState(UnityEngine.Object asset) => !asset ? "未引用" :
            string.IsNullOrEmpty(AssetDatabase.GetAssetPath(asset)) ? "未保存为资产" : EditorUtility.IsDirty(asset) ? "有未保存修改" : "已保存";

        // 要求完整落盘的配置引用，原生动作还须通过空闲、编译和无脏修改门禁。
        private void RequireProfile(bool requireClean)
        {
            if (!profile)
            {
                throw new InvalidOperationException("请先选择或新建构建配置。");
            }

            if (!profile.recipe)
            {
                throw new InvalidOperationException("当前 Profile 尚未引用 Resource Recipe。");
            }

            if (!profile.recipe.project)
            {
                throw new InvalidOperationException("当前 Recipe 尚未引用 Resource Project。");
            }

            if (new UnityEngine.Object[] { profile, profile.recipe, profile.recipe.project }.Any(asset => string.IsNullOrEmpty(AssetDatabase.GetAssetPath(asset))))
            {
                throw new InvalidOperationException("Profile、Recipe 与 Project 都必须先保存为工程资产。");
            }

            if (!requireClean)
            {
                return;
            }

            RequireIdleEditor();
            if (EditorUtility.scriptCompilationFailed)
            {
                throw new InvalidOperationException("请先修复现有脚本编译错误。");
            }

            if (!AreAssetsSaved())
            {
                throw new InvalidOperationException("Profile、Recipe 或 Project 有未保存修改。请先点击顶部“保存”。");
            }
        }

        // 拒绝在编译、导入、Play 或 Player 构建期间执行显式动作。
        private static void RequireIdleEditor()
        {
            if (IsEditorBusy)
            {
                throw new InvalidOperationException("请等待编译、导入、Play Mode 或 Player 构建结束后再执行此操作。");
            }
        }

        // 保存实际管线报告及配置上下文，默认展开失败阶段并转到诊断页。
        private void SetReport(string operation, HybridCLRBuildReport value)
        {
            _report = value;
            _reportTitle = operation;
            _reportContext = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  /  " + AssetDatabase.GetAssetPath(profile);
            _expandedStages = value.Stages.Select(stage => !stage.HasSucceeded).ToArray();
            var failed = value.Stages.FirstOrDefault(stage => !stage.HasSucceeded);
            Notify(value.HasSucceeded ? operation + "完成，共 " + value.Stages.Count + " 个已执行阶段。" : operation + "未完成" + (failed == null ? "。" : "，失败阶段：" + StageName(failed.Name) + "。"), value.HasSucceeded ? MessageType.Info : MessageType.Error);
            ChangePage(2);
        }

        // 更新操作提示并清除上一次未捕获动作异常的展示状态。
        private void Notify(string text, MessageType type = MessageType.Info)
        {
            _notice = text;
            _noticeType = type;
            _failure = null;
            _showFailure = false;
        }

        // 任务卡片的统一标题区。
        private void CardHeading(string title, string description)
        {
            GUILayout.Label(title, EditorStyles.boldLabel);
            GUILayout.Label(description, _secondaryStyle, GUILayout.MinWidth(0));
            GUILayout.Space(6);
        }

        // 绘制只读身份指标，空值明确显示未设置。
        private void Metric(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, GUILayout.Width(124));
                GUILayout.Label(string.IsNullOrEmpty(value) ? "未设置" : value, _bodyStyle, GUILayout.MinWidth(0));
            }
        }

        // 展示可选择的诊断文本，并将复制动作留待布局结束。
        private void Copyable(string label, string value, bool raw = false)
        {
            value = value ?? "";
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, EditorStyles.boldLabel, GUILayout.MinWidth(0));
                GUILayout.FlexibleSpace();
                var text = value;
                ActionButton("复制", () => EditorGUIUtility.systemCopyBuffer = text, 50);
            }
            var style = raw ? _rawStyle : _bodyStyle;
            EditorGUILayout.SelectableLabel(value, style, GUILayout.MinWidth(0), GUILayout.Height(Mathf.Max(22, style.CalcHeight(new GUIContent(value), Mathf.Max(180, position.width - 70)))));
        }

        // 将点击转换为单个待执行动作，绘制期间不直接运行副作用。
        private void ActionButton(string label, Action action, float width)
        {
            if (GUILayout.Button(label, _buttonStyle, GUILayout.Width(width)))
            {
                _pendingAction = action;
            }
        }

        // 按当前明暗主题复用或重建窗口局部样式。
        private void EnsureStyles()
        {
            if (_titleStyle != null && _isDarkStyle == EditorGUIUtility.isProSkin)
            {
                return;
            }

            _isDarkStyle = EditorGUIUtility.isProSkin;
            _titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 15, alignment = TextAnchor.MiddleLeft, fixedHeight = 28 };
            _bodyStyle = new GUIStyle(EditorStyles.wordWrappedLabel) { stretchWidth = true };
            _secondaryStyle = new GUIStyle(_bodyStyle);
            _secondaryStyle.normal.textColor = _isDarkStyle ? new Color(.70f, .72f, .74f) : new Color(.34f, .36f, .38f);
            _buttonStyle = new GUIStyle(GUI.skin.button) { fixedHeight = 26, alignment = TextAnchor.MiddleCenter };
            _cardStyle = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(12, 12, 10, 10) };
            _rawStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
            _navigationStyle = new GUIStyle(GUI.skin.button)
            {
                fixedHeight = 30,
                fixedWidth = 0,
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                fontStyle = FontStyle.Bold
            };
        }

        // 将已知阶段标识映射为界面名称，未知标识原样保留。
        private static string StageName(string name)
        {
            switch (name)
            {
                case "Preflight":
                    return "原生环境与输入检查";
                case "PlayerPreflight":
                    return "Player 输出与场景检查";
                case "Compile":
                    return "编译热更程序集";
                case "Il2CppDef":
                    return "生成 IL2CPP 定义";
                case "LinkXml":
                    return "生成裁剪保留配置";
                case "EmptyMethodBridge":
                    return "生成初始原生桥接";
                case "StripAot":
                    return "裁剪基座 AOT 程序集";
                case "FullMethodBridge":
                    return "生成完整原生桥接";
                case "Manifest":
                    return "生成热更清单";
                case "PayloadCommit":
                    return "写入自有生成资源";
                case "ResourcePublish":
                    return "构建并发布资源";
                case "Deploy":
                    return "部署内置资源";
                case "Player":
                    return "构建原生 Player";
                case "Baseline":
                    return "冻结不可变基座";
                case "ReadBaseline":
                    return "读取冻结基座";
                case "PatchCompatibility":
                    return "检查补丁兼容性";
                default:
                    return name;
            }
        }

        // 沿内部异常识别 IMGUI 退出信号，避免把正常布局退出当作操作失败。
        private static bool IsExitGui(Exception error)
        {
            while (error != null)
            {
                if (error is ExitGUIException)
                {
                    return true;
                }

                error = error.InnerException;
            }
            return false;
        }

        // 执行布局后的动作并展示普通异常，保留 ExitGUI 控制流。
        private void Run(Action operation)
        {
            try
            {
                operation();
            }
            catch (Exception error) when (!IsExitGui(error))
            {
                _failure = error;
                _showFailure = true;
                _notice = "操作未完成：" + error.Message;
                _noticeType = MessageType.Error;
                ChangePage(2);
            }
            Repaint();
        }
    }
}
