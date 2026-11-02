using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.UIElements;
using YLFramework.Editor.Help;
using YLFramework.Editor.Common;
using CompilationAssembly = UnityEditor.Compilation.Assembly;

namespace YLFramework.Editor.Core.Analyzers
{
    /// <summary>展示分析器接入证据并编辑业务叶子规则集的 Project Settings 页面。</summary>
    /// <remarks>本地覆盖、编译图与编译活动分别观察，均不被当作诊断规则已实际执行的证明。</remarks>
    internal sealed class AnalyzerRuleSettingsProvider : SettingsProvider
    {
        internal const string SettingsPath = "Project/YLFramework/Analyzers";
        internal const string MenuPath = "YLFramework/Core/分析器规则";
        private const string DllName = "YLFramework.Analyzers.dll";
        // 编辑意图与磁盘读取快照、待应用预览分开保存；再次编辑规则会使旧预览失效。
        private readonly Dictionary<string, string> _changes = new Dictionary<string, string>(StringComparer.Ordinal);
        private CompilationAssembly[] _assemblies = Array.Empty<CompilationAssembly>();
        private string[] _assemblyLabels = Array.Empty<string>();
        private string _root;
        private int _selected;
        private RuleSetPreview _loaded;
        private RuleSetPreview _preview;
        private string _scopeBlock;
        private string _message;
        private string _activity = "尚未观察到本页应用后的编译；实际规则执行未核实。";
        private bool _compileErrors;
        private Vector2 _scroll;
        private int _dllCount;
        // null 表示缺少可核实的证据，不能按 false 或接入成功展示。
        private bool? _hasLabel;
        private bool? _platformsDisabled;
        private bool? _explicitReference;
        private bool? _included;
        private string _dllDetails;
        private string _readError;

        /// <summary>配置设置页入口、标题与搜索关键词，不在构造时读取或修改规则文件。</summary>
        private AnalyzerRuleSettingsProvider() : base(SettingsPath, SettingsScope.Project)
        {
            label = "Analyzers";
            keywords = new HashSet<string>(AnalyzerRuleCatalog.Rules.Select(rule => rule.Id)
                .Concat(new[] { "YLFramework", "分析器", "ruleset", "Warning", "Error", "Inject", "Panel", "Audio" }));
        }

        /// <summary>供 Unity 设置页发现机制创建页面实例。</summary>
        [SettingsProvider]
        internal static SettingsProvider CreateProvider() => new AnalyzerRuleSettingsProvider();

        /// <summary>从框架菜单定位到分析器规则设置页。</summary>
        [MenuItem(MenuPath)]
        private static void Open() => ProjectSettingTools.OpenProjectSettings(SettingsPath);

        /// <summary>重新订阅当前页面所需的编译事件，并刷新程序集与配置快照。</summary>
        /// <param name="searchContext">Unity 传入的设置搜索上下文。</param>
        /// <param name="rootElement">Unity 提供的根节点；本页使用 OnGUI 绘制。</param>
        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            OnDeactivate();
            CompilationPipeline.compilationStarted += CompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += AssemblyFinished;
            CompilationPipeline.compilationFinished += CompilationFinished;
            Refresh();
        }

        /// <summary>解除本页面的编译事件订阅；重复调用不会累积监听。</summary>
        public override void OnDeactivate()
        {
            CompilationPipeline.compilationStarted -= CompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= AssemblyFinished;
            CompilationPipeline.compilationFinished -= CompilationFinished;
        }

        /// <summary>绘制接入状态、本地覆盖与显式预览/应用入口，不因重绘自动写入配置。</summary>
        /// <param name="searchContext">Unity 传入的设置搜索文本；本页不据此改变规则集合。</param>
        public override void OnGUI(string searchContext)
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("YL 专属规则", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("默认 Warning，不改变 Runtime 契约。选择业务程序集、调整规则、预览后再应用；本页不修改全局 Default.ruleset。", MessageType.Info);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("刷新接入与配置") && ConfirmDiscard())
            {
                Refresh();
            }
            if (GUILayout.Button("Core 帮助"))
            {
                HelpWindow.Open("core");
            }
            EditorGUILayout.EndHorizontal();
            if (_assemblies.Length == 0)
            {
                EditorGUILayout.HelpBox("当前未发现引用 YLFramework.Runtime 的程序集。编译图与实际规则执行均未核实。", MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }
            var selected = EditorGUILayout.Popup("目标程序集", _selected, _assemblyLabels);
            if (selected != _selected && ConfirmDiscard())
            {
                _selected = selected;
                LoadSelection();
            }
            EditorGUILayout.HelpBox(_readError ?? DescribeConnection(_dllCount, _hasLabel, _platformsDisabled,
                _explicitReference, _included, EditorApplication.isCompiling, EditorUtility.scriptCompilationFailed || _compileErrors), MessageType.Info);
            EditorGUILayout.LabelField(_activity, EditorStyles.wordWrappedLabel);
            if (!string.IsNullOrEmpty(_dllDetails))
            {
                EditorGUILayout.SelectableLabel(_dllDetails, EditorStyles.wordWrappedLabel, GUILayout.MinHeight(64));
            }
            var observed = _assemblies[_selected].compilerOptions.RoslynAnalyzerRulesetPath;
            EditorGUILayout.LabelField("编译图规则集", string.IsNullOrEmpty(observed) ? "未发现或尚未刷新" : observed);
            EditorGUILayout.LabelField("最终生效级别", "未核实；本页显示本地覆盖与继承来源，不模拟编译器全部策略。", EditorStyles.wordWrappedLabel);
            if (_loaded != null && !string.IsNullOrEmpty(_loaded.TargetPath))
            {
                EditorGUILayout.SelectableLabel(_loaded.TargetPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("定位规则集 / 所在目录"))
                {
                    EditorUtility.RevealInFinder(File.Exists(_loaded.TargetPath) ? _loaded.TargetPath : Path.GetDirectoryName(_loaded.TargetPath));
                }
                var includes = _loaded.ObservedFiles.Keys.Where(path => path.EndsWith(".ruleset", StringComparison.OrdinalIgnoreCase) &&
                    !AnalyzerRuleSetFile.PathComparer.Equals(path, _loaded.TargetPath)).ToArray();
                if (includes.Length != 0)
                {
                    EditorGUILayout.LabelField("已读取继承", string.Join("\n", includes), EditorStyles.wordWrappedLabel);
                }
            }
            var block = _scopeBlock ?? _loaded?.BlockedReason;
            if (block != null)
            {
                EditorGUILayout.HelpBox("只读：" + block, MessageType.Warning);
            }
            using (new EditorGUI.DisabledScope(block != null || _loaded == null))
            {
                foreach (var rule in AnalyzerRuleCatalog.Rules)
                {
                    DrawRule(rule);
                }
                if (GUILayout.Button("预览更改"))
                {
                    _preview = AnalyzerRuleSetFile.Preview(_root, _assemblies[_selected].name,
                        CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(_assemblies[_selected].name), _changes);
                    _message = _preview.BlockedReason ?? (_preview.HasChanges ? "请检查下面的原文与新内容，再明确应用。" : "没有需要写入的更改。");
                }
            }
            if (!string.IsNullOrEmpty(_message))
            {
                EditorGUILayout.HelpBox(_message, MessageType.Info);
            }
            if (_preview != null && _preview.HasChanges)
            {
                EditorGUILayout.LabelField("原文", EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(_preview.OriginalText, EditorStyles.textArea, GUILayout.MinHeight(65));
                EditorGUILayout.LabelField("将写入", EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(_preview.ProposedText, EditorStyles.textArea, GUILayout.MinHeight(100));
            }
            using (new EditorGUI.DisabledScope(_preview == null || !_preview.CanApply || block != null ||
                EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer))
            {
                if (GUILayout.Button("应用预览并重新编译"))
                {
                    ApplyPreview();
                }
            }
            EditorGUILayout.EndScrollView();
        }

        /// <summary>编辑单 ID 的待应用意图；保留已有非常用级别，修改后作废旧预览。</summary>
        private void DrawRule(AnalyzerRuleInfo rule)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(rule.Id + "  " + rule.Title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(rule.Description, EditorStyles.wordWrappedLabel);
            _loaded.LocalActions.TryGetValue(rule.Id, out var original);
            var action = _changes.TryGetValue(rule.Id, out var desired) ? desired : original;
            var choices = new[] { "继承（移除本地覆盖）", "Warning", "Error", "关闭 (None)", "保留当前值：" + original };
            var current = action == null ? 0 : action == "Warning" ? 1 : action == "Error" ? 2 : action == "None" ? 3 : 4;
            var selected = EditorGUILayout.Popup("本地配置", current, choices);
            if (selected != current)
            {
                if (selected == 4)
                {
                    _changes.Remove(rule.Id);
                }
                else
                {
                    _changes[rule.Id] = selected == 0 ? null : selected == 1 ? "Warning" : selected == 2 ? "Error" : "None";
                }
                _preview = null;
                _message = "修改尚未应用，请先重新预览。";
            }
            if (GUILayout.Button("本地文档 " + rule.Id))
            {
                var document = global::YLFramework.Editor.Common.FrameworkPackagePaths.FullPath(
                    typeof(AnalyzerRuleSettingsProvider).Assembly, "Documentation~/Modules/Attributes.md");
                if (File.Exists(document))
                {
                    Application.OpenURL(new Uri(document).AbsoluteUri + "#" + rule.DocumentationAnchor);
                }
            }
            EditorGUILayout.EndVertical();
        }

        /// <summary>重新读取引用 Runtime 的编译图，尽量保留目标程序集，再加载其配置与接入信息。</summary>
        private void Refresh()
        {
            _root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var name = _assemblies.Length == 0 ? null : _assemblies[_selected].name;
            _assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor).Where(assembly =>
                assembly.name == "YLFramework.Runtime" || assembly.assemblyReferences.Any(reference => reference.name == "YLFramework.Runtime"))
                .OrderBy(assembly => assembly.name, StringComparer.Ordinal).ToArray();
            _assemblyLabels = _assemblies.Select(assembly => assembly.name).ToArray();
            _selected = Array.FindIndex(_assemblies, assembly => assembly.name == name);
            if (_selected < 0)
            {
                _selected = Array.FindIndex(_assemblies, assembly => ConfigurationBlock(_root, assembly.name,
                    CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name), true, assembly.sourceFiles) == null);
            }
            _selected = Math.Max(0, _selected);
            LoadSelection();
        }

        /// <summary>丢弃上一目标的编辑状态，只读加载当前目标的规则集和可配置边界。</summary>
        private void LoadSelection()
        {
            _changes.Clear();
            _preview = null;
            _loaded = null;
            _message = null;
            _scopeBlock = null;
            if (_assemblies.Length != 0)
            {
                var assembly = _assemblies[_selected];
                var definition = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name);
                _scopeBlock = ConfigurationBlock(_root, assembly.name, definition,
                    assembly.assemblyReferences.Any(reference => reference.name == "YLFramework.Runtime"), assembly.sourceFiles);
                _loaded = AnalyzerRuleSetFile.Preview(_root, assembly.name, definition, _changes);
            }
            RefreshConnection();
        }

        /// <summary>只读检查唯一 DLL、导入设置和当前编译图；无法判定的项目保留未知状态。</summary>
        private void RefreshConnection()
        {
            _dllCount = 0;
            _hasLabel = _platformsDisabled = _explicitReference = _included = null;
            _dllDetails = null;
            _readError = null;
            try
            {
                var paths = AssetDatabase.FindAssets("YLFramework.Analyzers").Select(AssetDatabase.GUIDToAssetPath)
                    .Where(path => Path.GetFileName(path) == DllName).Distinct(StringComparer.Ordinal).ToArray();
                _dllCount = paths.Length;
                if (paths.Length != 1)
                {
                    return;
                }
                var path = paths[0];
                var full = FullPath(_root, path);
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset != null)
                {
                    _hasLabel = AssetDatabase.GetLabels(asset).Contains("RoslynAnalyzer", StringComparer.Ordinal);
                }
                var importer = AssetImporter.GetAtPath(path) as PluginImporter;
                if (importer != null)
                {
                    var serialized = new SerializedObject(importer);
                    var explicitReference = serialized.FindProperty("m_IsExplicitlyReferenced");
                    _explicitReference = explicitReference?.propertyType == SerializedPropertyType.Boolean ? explicitReference.boolValue : (bool?)null;
                    var platforms = serialized.FindProperty("m_PlatformData");
                    if (platforms != null && platforms.isArray)
                    {
                        var disabled = !importer.GetCompatibleWithAnyPlatform() && !importer.GetCompatibleWithEditor();
                        var known = true;
                        for (var index = 0; index < platforms.arraySize; index++)
                        {
                            var enabled = platforms.GetArrayElementAtIndex(index).FindPropertyRelative("second.enabled");
                            if (enabled == null || enabled.propertyType != SerializedPropertyType.Boolean)
                            {
                                known = false;
                                continue;
                            }
                            disabled &= !enabled.boolValue;
                        }
                        _platformsDisabled = known ? disabled : (bool?)null;
                    }
                }
                var selected = _assemblies.Length == 0 ? null : CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                    .SingleOrDefault(assembly => assembly.name == _assemblies[_selected].name);
                if (selected?.compilerOptions.RoslynAnalyzerDllPaths != null)
                {
                    _included = selected.compilerOptions.RoslynAnalyzerDllPaths.Any(candidate =>
                        AnalyzerRuleSetFile.PathComparer.Equals(FullPath(_root, candidate), full));
                }
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(full);
                _dllDetails = path + "\nVersion: " + AssemblyName.GetAssemblyName(full).Version + "\nSHA256: " +
                    BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is BadImageFormatException)
            {
                _readError = "接入读取失败，实际生效未核实：" + exception.Message;
            }
        }

        /// <summary>确认 Error 门禁后应用既有预览，再导入规则文件并请求编译；不宣称规则已执行。</summary>
        private void ApplyPreview()
        {
            if (_changes.Any(change => change.Value == "Error") && !EditorUtility.DisplayDialog("启用 Error 门禁",
                "Error 可能阻止目标程序集编译。恢复方式：在此将该 ID 改为 Warning / 继承，或手动编辑显示的 ruleset 文件。是否应用？", "应用", "取消"))
            {
                return;
            }
            try
            {
                var path = _preview.TargetPath;
                AnalyzerRuleSetFile.Apply(_preview);
                LoadSelection();
                _activity = "已写入，等待编译；保存成功不代表分析器实际执行，当前仍未核实。";
                var relative = Path.GetRelativePath(_root, path).Replace('\\', '/');
                AssetDatabase.ImportAsset(relative, ImportAssetOptions.ForceUpdate);
                if (!EditorApplication.isCompiling)
                {
                    CompilationPipeline.RequestScriptCompilation();
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is InvalidOperationException)
            {
                _preview = null;
                _message = "未能应用，请刷新并重新预览：" + exception.Message;
            }
        }

        /// <summary>刷新或切换目标前确认可丢弃尚未应用的编辑意图。</summary>
        private bool ConfirmDiscard() => _changes.Count == 0 || EditorUtility.DisplayDialog("尚未应用的规则修改",
            "刷新或切换目标会丢弃尚未应用的修改。继续？", "丢弃并继续", "取消");

        /// <summary>开始观察新一轮编译活动，重置错误标记并刷新页面。</summary>
        private void CompilationStarted(object context)
        {
            _compileErrors = false;
            _activity = "等待编译完成；实际规则执行未核实。";
            Repaint();
        }

        /// <summary>汇总本轮任意程序集的编译错误，不把零诊断等同于规则执行成功。</summary>
        private void AssemblyFinished(string path, CompilerMessage[] messages)
            => _compileErrors |= messages.Any(message => message.type == CompilerMessageType.Error);

        /// <summary>更新本轮编译观察结果，并重新读取 DLL 与编译图接入证据。</summary>
        private void CompilationFinished(object context)
        {
            _activity = _compileErrors ? "编译失败；实际规则执行未核实。" : "编译完成；零诊断也不能证明规则已执行，实际生效未核实。";
            RefreshConnection();
            Repaint();
        }

        /// <summary>将已知或未知的接入证据组合成状态说明，与实际规则执行结论保持区分。</summary>
        internal static string DescribeConnection(int dllCount, bool? label, bool? platforms, bool? explicitReference,
            bool? included, bool compiling, bool failed)
        {
            string reason;
            if (dllCount == 0)
            {
                reason = "未找到分析器 DLL";
            }
            else if (dllCount != 1)
            {
                reason = "存在多份分析器 DLL";
            }
            else if (label != true)
            {
                reason = "RoslynAnalyzer 标签缺失或无法核实";
            }
            else if (platforms != true)
            {
                reason = "导入平台未全部禁用或无法核实";
            }
            else if (explicitReference != true)
            {
                reason = "Runtime 自动引用未关闭或无法核实";
            }
            else if (included != true)
            {
                reason = "当前编译图未包含分析器或无法核实";
            }
            else if (compiling)
            {
                reason = "等待编译完成";
            }
            else if (failed)
            {
                reason = "编译失败";
            }
            else
            {
                reason = "接入结构检查通过";
            }
            return reason + "；实际规则执行未核实，保存成功或零诊断不是执行证明。";
        }

        /// <summary>限定直接引用 Runtime 且包含 Assets 业务源码的可配置目标；返回 null 表示未发现范围阻断。</summary>
        internal static string ConfigurationBlock(string root, string name, string definition, bool referencesRuntime, IEnumerable<string> sources)
        {
            if (!referencesRuntime)
            {
                return "没有直接引用 YLFramework.Runtime，作用域未确认。";
            }
            if (definition == null && !AnalyzerRuleSetFile.IsPredefined(name))
            {
                return "未知预定义程序集。";
            }
            if (definition != null && definition.Replace('\\', '/').StartsWith("Packages/", StringComparison.Ordinal))
            {
                return "Packages、框架自有或工程外程序集只读。";
            }
            var assets = FullPath(root, "Assets") + Path.DirectorySeparatorChar;
            var framework = FullPath(root, "Assets/YLFramework") + Path.DirectorySeparatorChar;
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (definition != null && (!FullPath(root, definition).StartsWith(assets, comparison) ||
                FullPath(root, definition).StartsWith(framework, comparison)))
            {
                return "Packages、框架自有或工程外程序集只读。";
            }
            if (sources == null || !sources.Any(source => !source.Replace('\\', '/').StartsWith("Packages/", StringComparison.Ordinal) &&
                FullPath(root, source).StartsWith(assets, comparison) &&
                !FullPath(root, source).StartsWith(framework, comparison)))
            {
                return "没有可配置的 Assets 业务源码。";
            }
            return null;
        }

        /// <summary>包虚拟路径按真实安装身份解析，业务相对路径才以指定工程根为基准。</summary>
        private static string FullPath(string root, string path)
        {
            var assetPath = path.Replace('\\', '/');
            if (!Path.IsPathRooted(path) && assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                if (package == null)
                {
                    throw new IOException("Installed package identity is unavailable: " + assetPath);
                }
                var relative = assetPath.Substring(package.assetPath.Length).TrimStart('/');
                return Path.GetFullPath(Path.Combine(package.resolvedPath, relative));
            }
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        }
    }
}
