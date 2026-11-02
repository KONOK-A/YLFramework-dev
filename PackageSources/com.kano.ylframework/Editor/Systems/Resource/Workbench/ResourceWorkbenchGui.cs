using System;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace YLFramework.Editor.Resource
{
    /// <summary>跨窗口重载恢复选择所需的定义身份，不以临时 TreeView 行号替代资产身份。</summary>
    [Serializable]
    internal sealed class ResourceWorkbenchRowState
    {
        /// <summary>选择所属项目配置的 GUID。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("ProjectGuid")]
        public string projectGuid;
        /// <summary>选择所属分组的技术标识。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Group")]
        public string group;
        /// <summary>已登记主文件或目录的 GUID。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Guid")]
        public string guid;
        /// <summary>选择时的分包设置与定义序列化内容，用于拒绝恢复已变化条目。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Definition")]
        public string definition;
        /// <summary>选择时显示的地址，恢复时须与当前行一致。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Address")]
        public string address;
    }

    /// <summary>工作台窗口自身的布局与选择记忆，不写入 ResourceProject 或运行时配置。</summary>
    [Serializable]
    internal sealed class ResourceWorkbenchLayout
    {
        /// <summary>分组栏请求宽度，由绘制时约束到可用范围。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("GroupWidth")]
        public float groupWidth = 180;
        /// <summary>详情栏请求宽度。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("DetailWidth")]
        public float detailWidth = 340;
        /// <summary>宽窗口是否显示分组栏，不影响分组定义。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("ShowGroups")]
        public bool showGroups = true;
        /// <summary>宽窗口是否显示条目详情。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("ShowDetails")]
        public bool showDetails = true;
        /// <summary>窄窗口是否用详情替代列表。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("CompactDetails")]
        public bool compactDetails;
        /// <summary>当前是否显示项目设置页。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Settings")]
        public bool settings;
        /// <summary>底部完整输出是否展开。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Output")]
        public bool output;
        /// <summary>用户请求的输出高度，显示钳制不必覆盖此值。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("OutputHeight")]
        public float outputHeight;
        /// <summary>当前任务页索引。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Task")]
        public int task;
        /// <summary>资源表的列宽、可见性和排序记忆。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Columns")]
        public MultiColumnHeaderState columns;
        /// <summary>保存行选择时的项目 GUID。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("SelectionProjectGuid")]
        public string selectionProjectGuid;
        /// <summary>保存选择时的分组标识；为空表示全部分组视图。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("SelectionGroup")]
        public string selectionGroup;
        /// <summary>主资源表的搜索条件。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("EntrySearch")]
        public string entrySearch;
        /// <summary>须经当前定义身份复核才能恢复的行选择。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("SelectedRows")]
        public ResourceWorkbenchRowState[] selectedRows;
    }

    /// <summary>共享工作台绘制样式和字段控件，目录缓存仅供显示，不代替正式操作校验。</summary>
    internal static class ResourceWorkbenchGui
    {
        /// <summary>一次目录显示解析的结果或异常，随项目语义身份和编辑器修订失效。</summary>
        private sealed class DirectoryDisplay
        {
            internal int Revision = -1;
            internal string Identity;
            internal ResourceProjectPaths Paths;
            internal Exception Error;
        }
        // 弱键避免显示缓存永久持有项目资产；共享订阅属于编辑器域，不随单个窗口开关。
        private static readonly ConditionalWeakTable<ResourceProject, DirectoryDisplay> _directoryDisplays = new();
        private static int _directoryRevision;

        /// <summary>为域内共享目录显示缓存订阅资产变化与 Undo 失效通知。</summary>
        static ResourceWorkbenchGui()
        {
            EditorApplication.projectChanged += InvalidateDirectoryDisplay;
            Undo.undoRedoPerformed += InvalidateDirectoryDisplay;
        }

        /// <summary>推进显示修订号，让下一次访问重新解析而非立即全量扫描。</summary>
        private static void InvalidateDirectoryDisplay() => _directoryRevision++;

        /// <summary>按项目语义身份与修订复用目录显示解析，也保留同次解析异常避免逐帧重复工作。</summary>
        internal static ResourceProjectPaths DisplayPaths(ResourceProject project)
        {
            var display = _directoryDisplays.GetValue(project, _ => new DirectoryDisplay());
            var identity = project.projectId + "\n" + ResourceProjectPaths.SemanticIdentity(project);
            if (display.Revision != _directoryRevision || display.Identity != identity)
            {
                display.Revision = _directoryRevision;
                display.Identity = identity;
                display.Paths = null;
                display.Error = null;
                try
                {
                    display.Paths = ResourceProjectPaths.Resolve(project);
                }
                catch (Exception error)
                {
                    display.Error = error;
                }
            }
            if (display.Error != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(display.Error).Throw();
            }

            return display.Paths;
        }
        private static bool _isDark;
        private static GUIStyle _title;
        private static GUIStyle _secondary;
        private static GUIStyle _project;
        private static GUIStyle _text;
        private static GUIStyle _objectPath;
        private static GUIStyle _identity;
        private static GUIStyle _field;
        private static GUIStyle _popup;
        private static GUIStyle _button;
        private static GUIStyle _toolbar;
        private static GUIStyle _toolbarButton;
        private static GUIStyle _foldout;
        private static GUIStyle _menu;
        private static GUIStyle _navigation;
        private static GUIStyle _taskNavigation;
        private static GUIStyle _row;
        private static GUIStyle _selectedRow;
        private static GUIStyle _objectBar;
        private static GUIStyle _searchHint;
        internal const float FieldHeight = 26;
        internal const float RowHeight = 26;
        internal const float BarHeight = 30;
        internal static GUIStyle Title
        {
            get
            {
                EnsureStyles();
                return _title;
            }
        }
        internal static GUIStyle Secondary
        {
            get
            {
                EnsureStyles();
                return _secondary;
            }
        }
        internal static GUIStyle Project
        {
            get
            {
                EnsureStyles();
                return _project;
            }
        }
        internal static GUIStyle ObjectPath
        {
            get
            {
                EnsureStyles();
                return _objectPath;
            }
        }
        internal static GUIStyle Identity
        {
            get
            {
                EnsureStyles();
                return _identity;
            }
        }
        internal static GUIStyle Field
        {
            get
            {
                EnsureStyles();
                return _field;
            }
        }
        internal static GUIStyle Popup
        {
            get
            {
                EnsureStyles();
                return _popup;
            }
        }
        internal static GUIStyle Button
        {
            get
            {
                EnsureStyles();
                return _button;
            }
        }
        internal static GUIStyle Toolbar
        {
            get
            {
                EnsureStyles();
                return _toolbar;
            }
        }
        internal static GUIStyle ToolbarButton
        {
            get
            {
                EnsureStyles();
                return _toolbarButton;
            }
        }
        internal static GUIStyle Foldout
        {
            get
            {
                EnsureStyles();
                return _foldout;
            }
        }
        internal static GUIStyle Menu
        {
            get
            {
                EnsureStyles();
                return _menu;
            }
        }
        internal static GUIStyle NavigationStyle
        {
            get
            {
                EnsureStyles();
                return _navigation;
            }
        }
        internal static GUIStyle ObjectBar
        {
            get
            {
                EnsureStyles();
                return _objectBar;
            }
        }
        internal static Color Accent => EditorGUIUtility.isProSkin ? new Color(.34f, .65f, .95f) : new Color(.12f, .39f, .68f);
        internal static Color Divider => EditorGUIUtility.isProSkin ? new Color(.13f, .13f, .13f) : new Color(.65f, .65f, .65f);

        /// <summary>按当前编辑器明暗主题延迟创建或重建共享样式。</summary>
        private static void EnsureStyles()
        {
            if (_title != null && _isDark == EditorGUIUtility.isProSkin)
            {
                return;
            }

            _isDark = EditorGUIUtility.isProSkin;
            _title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14, alignment = TextAnchor.MiddleLeft, wordWrap = true };
            _secondary = new GUIStyle(EditorStyles.label) { wordWrap = true };
            _secondary.normal.textColor = _isDark ? new Color(.7f, .7f, .7f) : new Color(.33f, .33f, .33f);
            _searchHint = new GUIStyle(_secondary) { wordWrap = false, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(6, 6, 0, 0) };
            _project = new GUIStyle(EditorStyles.toolbarDropDown) { fontStyle = FontStyle.Bold, fixedHeight = BarHeight, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            _identity = new GUIStyle(_secondary)
            {
                wordWrap = false,
                clipping = TextClipping.Clip,
                alignment = _project.alignment,
                font = _project.font,
                fontSize = _project.fontSize,
                fixedHeight = _project.fixedHeight,
                contentOffset = _project.contentOffset,
                margin = new RectOffset(_project.margin.left, _project.margin.right, _project.margin.top, _project.margin.bottom),
                padding = new RectOffset(0, 0, _project.padding.top, _project.padding.bottom)
            };
            _text = new GUIStyle(EditorStyles.textField) { wordWrap = true };
            _objectPath = new GUIStyle(EditorStyles.objectField) { margin = new RectOffset(), clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft, fixedHeight = FieldHeight };
            _field = new GUIStyle(EditorStyles.textField) { fixedHeight = FieldHeight, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(5, 5, 3, 3) };
            _popup = new GUIStyle(EditorStyles.popup) { fixedHeight = FieldHeight, alignment = TextAnchor.MiddleLeft };
            _button = new GUIStyle(GUI.skin.button) { fixedHeight = BarHeight, alignment = TextAnchor.MiddleCenter };
            _objectBar = new GUIStyle(EditorStyles.toolbar) { fixedHeight = BarHeight };
            _toolbar = new GUIStyle(GUIStyle.none) { fixedHeight = BarHeight, padding = new RectOffset(3, 3, 0, 0) };
            _toolbarButton = new GUIStyle(EditorStyles.toolbarButton) { fixedHeight = BarHeight, alignment = TextAnchor.MiddleCenter };
            _foldout = new GUIStyle(EditorStyles.foldout) { fixedHeight = FieldHeight, alignment = TextAnchor.MiddleLeft };
            _menu = new GUIStyle(EditorStyles.toolbarDropDown) { fixedHeight = BarHeight, alignment = TextAnchor.MiddleLeft };
            _navigation = new GUIStyle(EditorStyles.label) { name = "Resource.Navigation", fixedHeight = BarHeight, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(8, 8, 0, 0), clipping = TextClipping.Clip };
            _navigation.normal.textColor = EditorGUIUtility.isProSkin ? new Color(.77f, .79f, .82f) : new Color(.27f, .29f, .32f);
            _navigation.onNormal.textColor = _navigation.onHover.textColor = _navigation.onActive.textColor = EditorGUIUtility.isProSkin ? Color.white : Accent;
            _taskNavigation = new GUIStyle(_navigation) { fixedHeight = 34, fontSize = 13 };
            _row = new GUIStyle(EditorStyles.label) { fixedHeight = RowHeight, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(10, 4, 0, 0), clipping = TextClipping.Clip };
            _selectedRow = new GUIStyle(_row) { fontStyle = FontStyle.Bold };
            _selectedRow.normal.textColor = _selectedRow.onNormal.textColor = EditorGUIUtility.isProSkin ? Color.white : new Color(.08f, .25f, .43f);
        }

        /// <summary>绘制带当前项强调的导航条，并返回用户选择。</summary>
        internal static int Navigation(int selected, string[] labels, bool tasks = false, params GUILayoutOption[] options)
        {
            EnsureStyles();
            var style = tasks ? _taskNavigation : _navigation;
            var rect = GUILayoutUtility.GetRect(GUIContent.none, style, options.Length == 0 ? new[] { GUILayout.MinWidth(0), GUILayout.ExpandWidth(true), GUILayout.Height(style.fixedHeight) } : options);
            var surface = EditorGUIUtility.isProSkin ? new Color(.16f, .17f, .18f) : new Color(.88f, .89f, .90f);
            EditorGUI.DrawRect(rect, surface);
            if (selected >= 0 && selected < labels.Length)
            {
                var active = new Rect(rect.x + rect.width * selected / labels.Length, rect.y, rect.width / labels.Length, rect.height);
                EditorGUI.DrawRect(active, Color.Lerp(surface, Accent, tasks ? .12f : .22f));
                EditorGUI.DrawRect(new Rect(active.x + 6, active.yMax - 3, active.width - 12, 3), GUI.enabled ? Accent : Divider);
            }
            return GUI.Toolbar(rect, selected, labels, style);
        }

        /// <summary>以统一工具栏尺寸绘制带提示的开关。</summary>
        internal static bool ToolbarToggle(bool selected, string label, float width, string help = null) =>
            EditorGUI.ToggleLeft(EditorGUILayout.GetControlRect(false, FieldHeight, GUILayout.Width(width)), new GUIContent(label, help), selected);

        /// <summary>绘制选中底色与标记，并返回该行的选择状态。</summary>
        internal static bool SelectableRow(Rect rect, bool selected, GUIContent content)
        {
            EnsureStyles();
            if (selected)
            {
                var surface = EditorGUIUtility.isProSkin ? new Color(.16f, .17f, .18f) : new Color(.94f, .94f, .94f);
                EditorGUI.DrawRect(rect, Color.Lerp(surface, Accent, .25f));
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, 3, rect.height), Accent);
            }
            return GUI.Toggle(rect, selected, content, selected ? _selectedRow : _row);
        }

        /// <summary>临时强调主要动作按钮，退出后恢复调用方的背景色。</summary>
        internal static bool PrimaryAction(string label, params GUILayoutOption[] options)
        {
            var previous = GUI.backgroundColor;
            GUI.backgroundColor = EditorGUIUtility.isProSkin ? new Color(.52f, 1.15f, 1.75f) : new Color(.55f, .78f, 1f);
            try
            {
                return GUILayout.Button(label, Button, options);
            }
            finally
            {
                GUI.backgroundColor = previous;
            }
        }

        /// <summary>绘制统一高度的即时或延迟提交文本框，不保存其所属资产。</summary>
        internal static string TextField(string label, string value, bool delayed = false, string help = null)
        {
            var rect = EditorGUILayout.GetControlRect(true, FieldHeight, Field);
            return delayed ? EditorGUI.DelayedTextField(rect, new GUIContent(label, help), value, Field) : EditorGUI.TextField(rect, new GUIContent(label, help), value, Field);
        }

        /// <summary>绘制搜索输入及空值占位提示，提示文本不成为实际搜索条件。</summary>
        internal static string SearchField(string value, string hint, params GUILayoutOption[] options)
        {
            var rect = EditorGUILayout.GetControlRect(false, FieldHeight, Field, options);
            var next = EditorGUI.TextField(rect, value ?? "", Field);
            if (string.IsNullOrEmpty(next) && Event.current.type == EventType.Repaint)
            {
                GUI.Label(rect, new GUIContent(hint, hint), _searchHint);
            }

            return next;
        }

        /// <summary>绘制仅接受项目资产、不接受场景对象的引用选择框。</summary>
        internal static T ObjectField<T>(string label, T value, string help = null) where T : UnityEngine.Object =>
            (T)EditorGUI.ObjectField(EditorGUILayout.GetControlRect(true, FieldHeight), new GUIContent(label, help), value, typeof(T), false);

        /// <summary>绘制带标签与提示的普通布尔字段。</summary>
        internal static bool Toggle(string label, bool value, string help = null) =>
            EditorGUI.Toggle(EditorGUILayout.GetControlRect(true, FieldHeight), new GUIContent(label, help), value);

        /// <summary>绘制标签可点击的折叠入口，仅返回展开状态。</summary>
        internal static bool Disclosure(bool value, string label) => EditorGUI.Foldout(EditorGUILayout.GetControlRect(true, FieldHeight), value, label, true, Foldout);

        /// <summary>编辑单个序列化属性并递归绘制复合项；应用修改与保存仍由调用方负责。</summary>
        internal static void Property(SerializedProperty property, GUIContent label = null)
        {
            label ??= new GUIContent(PropertyLabel(property), property.tooltip);
            if (property.propertyType == SerializedPropertyType.Generic)
            {
                property.isExpanded = EditorGUI.Foldout(EditorGUILayout.GetControlRect(true, FieldHeight), property.isExpanded, label, true, Foldout);
                if (!property.isExpanded)
                {
                    return;
                }

                EditorGUI.indentLevel++;
                var child = property.Copy();
                var end = child.GetEndProperty();
                if (child.NextVisible(true))
                {
                    do
                    {
                        if (SerializedProperty.EqualContents(child, end))
                        {
                            break;
                        }

                        Property(child);
                    } while (child.NextVisible(false));
                }

                EditorGUI.indentLevel--;
                return;
            }
            var rect = EditorGUILayout.GetControlRect(true, FieldHeight);
            switch (property.propertyType)
            {
                case SerializedPropertyType.String:
                    property.stringValue = EditorGUI.TextField(rect, label, property.stringValue, Field);
                    break;
                case SerializedPropertyType.Integer:
                    property.intValue = EditorGUI.IntField(rect, label, property.intValue, Field);
                    break;
                case SerializedPropertyType.ArraySize:
                    property.intValue = Math.Max(0, EditorGUI.IntField(rect, label, property.intValue, Field));
                    break;
                case SerializedPropertyType.Float:
                    property.floatValue = EditorGUI.FloatField(rect, label, property.floatValue, Field);
                    break;
                case SerializedPropertyType.Enum:
                    var values = string.Join(",", property.enumNames);
                    var names = values == "Together,PerAsset" ? new[] { "合并分包 (Together)", "逐资源分包 (PerAsset)" } :
                        values == "Lz4,Lzma,Uncompressed" ? new[] { "LZ4压缩", "LZMA压缩", "不压缩 (Uncompressed)" } :
                        values == "None,Aes256GcmHkdfSegmentedV1" ? new[] { "不加密 (None)", "分段加密 (Aes256GcmHkdfSegmentedV1)" } : property.enumDisplayNames;
                    property.enumValueIndex = EditorGUI.Popup(rect, label, property.enumValueIndex, Array.ConvertAll(names, x => new GUIContent(x)), Popup);
                    break;
                default:
                    EditorGUI.PropertyField(rect, property, label, false);
                    break;
            }
        }

        /// <summary>为资源定义字段和数组项生成可读标签，不改变序列化字段名。</summary>
        private static string PropertyLabel(SerializedProperty property)
        {
            if (property.displayName.StartsWith("Element ", StringComparison.Ordinal) && int.TryParse(property.displayName.Substring(8), out var index))
            {
                return "第" + (index + 1) + "项";
            }

            return property.name switch
            {
                "size" => "数量",
                "id" => "Group ID",
                "packing" => "分包方式",
                "selections" => "资源条目",
                "guid" => "资源GUID",
                "address" => "加载地址",
                "aliases" => "地址别名",
                "extensions" => "扩展名筛选",
                _ => property.displayName
            };
        }

        /// <summary>成对显示指标名称和原值，缺失值明确显示为未知。</summary>
        internal static void Metric(string label, string value, string help = null, float labelWidth = 146)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(label, help), GUILayout.Width(labelWidth), GUILayout.Height(22));
            GUILayout.Label(new GUIContent(value ?? "未知", help), EditorStyles.wordWrappedLabel, GUILayout.MinWidth(0));
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>按二进制单位展示逻辑字节，未知值不补成零。</summary>
        internal static string Bytes(long? value) => !value.HasValue ? "未知" : value.Value < 1024 ? value + " B" :
            value.Value < 1024 * 1024 ? (value.Value / 1024d).ToString("F1") + " KiB" : (value.Value / (1024d * 1024)).ToString("F2") + " MiB";
        /// <summary>将毫秒测量格式化为毫秒或秒，未测量状态保持独立。</summary>
        internal static string Duration(double? value) => !value.HasValue ? "未测量" : value.Value < 1000 ? value.Value.ToString("F1") + " ms" : (value.Value / 1000).ToString("F2") + " s";

        /// <summary>按 GUID 显示和选择来源文件夹，仅做 UI 准入提示；正式目录校验仍由服务执行。</summary>
        internal static string ResourceRootField(SerializedProperty root, string label = "资源目录")
        {
            var path = AssetDatabase.GUIDToAssetPath(root.stringValue);
            var folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(path);
            EditorGUILayout.BeginHorizontal();
            var rect = EditorGUILayout.GetControlRect(true, FieldHeight, GUILayout.MinWidth(0));
            var next = (DefaultAsset)EditorGUI.ObjectField(rect, new GUIContent(label, path), folder, typeof(DefaultAsset), false);
            if (folder && Event.current.type == EventType.Repaint)
            {
                var value = EditorGUI.IndentedRect(rect);
                value.xMin += EditorGUIUtility.labelWidth;
                value.xMax -= 20;
                ObjectPath.Draw(value, new GUIContent(path, AssetDatabase.GetCachedIcon(path), path), false, false, false, false);
            }
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(path)))
            {
                if (GUILayout.Button(new GUIContent("复制", "复制完整Assets路径"), Button, GUILayout.Width(44), GUILayout.Height(FieldHeight)))
                {
                    EditorGUIUtility.systemCopyBuffer = path;
                }
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(root.stringValue)))
            {
                if (GUILayout.Button(new GUIContent("GUID", "复制目录GUID：" + root.stringValue), Button, GUILayout.Width(44), GUILayout.Height(FieldHeight)))
                {
                    EditorGUIUtility.systemCopyBuffer = root.stringValue;
                }
            }

            EditorGUILayout.EndHorizontal();
            if (!folder)
            {
                GUILayout.Label(string.IsNullOrEmpty(root.stringValue) ? "尚未选择文件夹。" : "目录GUID无法解析为现有文件夹：" + root.stringValue, Secondary);
            }
            else
            {
                var pathRect = GUILayoutUtility.GetRect(new GUIContent(path), Secondary, GUILayout.ExpandWidth(true));
                EditorGUI.SelectableLabel(pathRect, path, Secondary);
            }
            if (next == folder)
            {
                return null;
            }

            var selected = AssetDatabase.GetAssetPath(next);
            if (next && (!AssetDatabase.IsValidFolder(selected) || !selected.StartsWith("Assets/", StringComparison.Ordinal) || selected == "Assets/StreamingAssets" || selected.StartsWith("Assets/StreamingAssets/", StringComparison.Ordinal)))
            {
                return "请选择Assets下的真实子目录，不能使用StreamingAssets或Packages。";
            }

            root.stringValue = next ? AssetDatabase.AssetPathToGUID(selected) : "";
            return null;
        }

        /// <summary>编辑来源目录登记及地址前缀，不移动文件，也不自动移除受影响的资源条目。</summary>
        internal static string ResourceDirectoriesFields(SerializedObject serialized)
        {
            var directories = serialized.FindProperty("sourceDirectories");
            var count = directories.arraySize;
            string error = null;
            GUILayout.Label("资源目录（" + count + "）", EditorStyles.boldLabel);
            GUILayout.Label("选择范围", EditorStyles.boldLabel);
            GUILayout.Label("添加本资源项目会使用的文件夹，可以添加多个，无需搬动资源。这里只设置选择范围；哪些文件或文件夹参与构建，仍在Group中选择。", Secondary);
            GUILayout.Space(6);
            GUILayout.Label("地址前缀", EditorStyles.boldLabel);
            GUILayout.Label("用于区分不同目录中的同名资源。默认加载地址由“前缀 + 目录内相对路径”组成；前缀可留空。", Secondary);
            GUILayout.Label("例如：前缀为audio，文件为Music/bgm.ogg，默认地址就是audio/Music/bgm.ogg。", Secondary);
            GUILayout.Label("修改前缀会改变下次构建的默认地址；手工填写的地址和历史发布不受影响。", Secondary);
            for (var index = 0; index < count; index++)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                var guid = directories.GetArrayElementAtIndex(index).FindPropertyRelative("guid");
                error = ResourceRootField(guid) ?? error;
                EditorGUILayout.BeginHorizontal();
                Property(directories.GetArrayElementAtIndex(index).FindPropertyRelative("addressPrefix"), new GUIContent("地址前缀", "留空使用目录内相对路径。修改会改变下次构建的默认地址，不会改写历史发布；显式地址不受影响。"));
                if (GUILayout.Button(new GUIContent("移除", "只移除目录登记，不删除源文件或Group中的资源条目；受影响条目会阻止构建。"), Button, GUILayout.Width(52)))
                {
                    directories.DeleteArrayElementAtIndex(index);
                    count--;
                    index--;
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
            if (GUILayout.Button("添加资源目录", Button))
            {
                var item = directories.GetArrayElementAtIndex(directories.arraySize++);
                item.FindPropertyRelative("guid").stringValue = "";
                item.FindPropertyRelative("addressPrefix").stringValue = "";
            }
            GUILayout.Label("同一目录不能重复登记，目录之间也不能互相包含。移除或替换只修改配置，可撤销；不会自动移除资源条目。", Secondary);
            return error;
        }

        /// <summary>只读提示目录解析问题和失去来源范围的登记项，不修复或保存配置。</summary>
        internal static void DirectoryValidation(ResourceProject project)
        {
            try
            {
                var paths = DisplayPaths(project);
                var affected = new System.Collections.Generic.List<string>();
                foreach (var group in project.groups)
                {
                    foreach (var selection in group.selections)
                    {
                        var path = AssetDatabase.GUIDToAssetPath(selection.guid);
                        if (!paths.ContainsSource(path))
                        {
                            affected.Add("Group ‘" + group.id + "’：" + (string.IsNullOrEmpty(path) ? selection.guid : path));
                        }
                    }
                }

                if (project.automaticAtlases)
                {
                    foreach (var rule in project.automaticAtlases.rules)
                    {
                        foreach (var source in rule.sources ?? Array.Empty<UnityEngine.Object>())
                        {
                            var path = AssetDatabase.GetAssetPath(source);
                            if (!paths.ContainsSource(path))
                            {
                                affected.Add("图集规则 ‘" + rule.id + "’：" + (string.IsNullOrEmpty(path) ? "来源缺失" : path));
                            }
                        }
                    }
                }

                if (affected.Count != 0)
                {
                    EditorGUILayout.HelpBox("以下条目不在已登记的资源目录中，构建将被阻止。请恢复目录登记或明确调整条目；源文件未被删除。\n" + string.Join("\n", affected), MessageType.Error);
                }
            }
            catch (Exception error) when (error is InvalidOperationException || error is System.IO.IOException || error is UnauthorizedAccessException)
            {
                EditorGUILayout.HelpBox("目录配置无法使用，构建将被阻止。请检查下列原因并修正配置；源文件不会因此被删除。\n" + error.Message, MessageType.Error);
            }
        }

        /// <summary>按需展开可复制的原始异常，保留未展开时的紧凑布局。</summary>
        internal static void FailureDetails(Exception error, ref bool expanded)
        {
            if (error == null)
            {
                return;
            }

            expanded = Disclosure(expanded, "技术详情：查看原始错误");
            if (expanded)
            {
                CopyLine("原始异常", ResourceEditorText.FailureDetails(error), Mathf.Max(160, EditorGUIUtility.currentViewWidth - 32));
            }
        }

        /// <summary>计算可用输出高度，不改写用户请求值，避免窗口缩放丢失布局偏好。</summary>
        internal static float GetOutputHeight(float requested, float windowHeight, bool hasOutput) =>
            Mathf.Clamp(requested > 0 ? requested : hasOutput ? 140 : 56, 48, Mathf.Max(48, Mathf.Min(360, windowHeight - 360)));

        /// <summary>在分隔条捕获鼠标期间更新请求输出高度，结束拖动后释放控件捕获。</summary>
        internal static void HeightSplitter(Rect rect, ref float requested, float displayed, float windowHeight)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.center.y, rect.width, 1), Divider);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeVertical);
            var id = GUIUtility.GetControlID(FocusType.Passive, rect);
            var input = Event.current;
            if (input.type == EventType.MouseDown && input.button == 0 && rect.Contains(input.mousePosition))
            {
                requested = displayed;
                GUIUtility.hotControl = id;
                input.Use();
            }
            else if (GUIUtility.hotControl == id && input.type == EventType.MouseDrag)
            {
                requested = GetOutputHeight(requested - input.delta.y, windowHeight, true);
                GUI.changed = true;
                input.Use();
            }
            else if (GUIUtility.hotControl == id && input.type == EventType.MouseUp)
            {
                GUIUtility.hotControl = 0;
                input.Use();
            }
        }

        /// <summary>换行显示完整值并提供原值复制，不把显示截断或标签混入剪贴板。</summary>
        internal static void CopyLine(string label, string value, float width)
        {
            EnsureStyles();
            value ??= "";
            GUILayout.Label(label, EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal(GUILayout.MinWidth(0), GUILayout.MaxWidth(width));
            EditorGUILayout.SelectableLabel(value, _text, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true), GUILayout.Height(Mathf.Max(FieldHeight, _text.CalcHeight(new GUIContent(value), Mathf.Max(90, width - 62)))));
            if (GUILayout.Button(new GUIContent("复制", "复制完整原值，不包含显示省略号"), Button, GUILayout.Width(44), GUILayout.Height(FieldHeight)))
            {
                EditorGUIUtility.systemCopyBuffer = value;
            }

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>处理横向分隔条拖动，在指定边界和方向内调整栏宽。</summary>
        internal static void Splitter(Rect rect, ref float value, float min, float max, int direction = 1)
        {
            EditorGUI.DrawRect(new Rect(rect.center.x, rect.y, 1, rect.height), Divider);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeHorizontal);
            var id = GUIUtility.GetControlID(FocusType.Passive, rect);
            var input = Event.current;
            if (input.type == EventType.MouseDown && input.button == 0 && rect.Contains(input.mousePosition))
            {
                GUIUtility.hotControl = id;
                input.Use();
            }
            else if (GUIUtility.hotControl == id && input.type == EventType.MouseDrag)
            {
                value = Mathf.Clamp(value + input.delta.x * direction, min, max);
                GUI.changed = true;
                input.Use();
            }
            else if (GUIUtility.hotControl == id && input.type == EventType.MouseUp)
            {
                GUIUtility.hotControl = 0;
                input.Use();
            }
        }
    }
}
