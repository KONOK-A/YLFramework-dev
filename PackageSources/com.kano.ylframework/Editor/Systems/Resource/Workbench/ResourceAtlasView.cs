using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.U2D;

namespace YLFramework.Editor.Resource
{
    /// <summary>仅共享公开字段绘制；操作按钮由Inspector/Workbench各自拥有。</summary>
    internal static class ResourceAtlasFields
    {
        /// <summary>通过序列化属性编辑全部或指定规则，并应用可撤销修改；不保存或生成图集。</summary>
        internal static bool Draw(SerializedObject serialized, int rule = -1, bool textureSettings = true)
        {
            serialized.Update();
            var rules = serialized.FindProperty("rules");
            if (rule < 0)
            {
                rules.isExpanded = ResourceWorkbenchGui.Disclosure(rules.isExpanded, "图集规则（" + rules.arraySize + " 项）");
                if (rules.isExpanded)
                {
                    using var indent = new EditorGUI.IndentLevelScope();
                    var size = EditorGUI.IntField(EditorGUILayout.GetControlRect(true, ResourceWorkbenchGui.FieldHeight), new GUIContent("规则数量"), rules.arraySize, ResourceWorkbenchGui.Field);
                    if (size != rules.arraySize)
                    {
                        rules.arraySize = Math.Max(0, size);
                    }

                    for (var i = 0; i < rules.arraySize; i++)
                    {
                        var entry = rules.GetArrayElementAtIndex(i);
                        var id = entry.FindPropertyRelative("id").stringValue;
                        entry.isExpanded = ResourceWorkbenchGui.Disclosure(entry.isExpanded, string.IsNullOrEmpty(id) ? "未命名规则 " + (i + 1) : id);
                        if (entry.isExpanded)
                        {
                            DrawRule(entry, textureSettings);
                        }
                    }
                }
            }
            else if (rule < rules.arraySize)
            {
                DrawRule(rules.GetArrayElementAtIndex(rule), textureSettings);
            }

            return serialized.ApplyModifiedProperties();
        }

        /// <summary>按字段职责组织规则编辑，将输出组织方式与 Bundle 分包设置分开呈现。</summary>
        private static void DrawRule(SerializedProperty entry, bool textureSettings)
        {
            var field = entry.Copy();
            var end = field.GetEndProperty();
            if (!field.NextVisible(true))
            {
                return;
            }

            do
            {
                if (SerializedProperty.EqualContents(field, end))
                {
                    break;
                }

                var texture = field.name == "maxTextureSize" || field.name == "readable" || field.name == "mipMaps" || field.name == "srgb" || field.name == "filterMode" || field.name == "compression";
                if (texture && !textureSettings)
                {
                    continue;
                }

                if (field.name == "id")
                {
                    GUILayout.Label("基本信息", EditorStyles.boldLabel);
                }

                if (field.name == "sources")
                {
                    GUILayout.Space(8);
                    GUILayout.Label("输入资源", EditorStyles.boldLabel);
                    DrawSources(field);
                    continue;
                }
                if (field.name == "allowRotation")
                {
                    GUILayout.Space(8);
                    GUILayout.Label("打包设置", EditorStyles.boldLabel);
                }
                if (field.name == "maxTextureSize")
                {
                    GUILayout.Space(8);
                    GUILayout.Label("纹理设置", EditorStyles.boldLabel);
                }
                GUI.SetNextControlName("Resource.Atlas." + field.name);
                if (field.name == "grouping")
                {
                    field.enumValueIndex = EditorGUI.Popup(EditorGUILayout.GetControlRect(true, ResourceWorkbenchGui.FieldHeight), new GUIContent("图集输出组织", "仅决定图集输出资产的组织，不改变目标 Group 的 Bundle 分包。"), field.enumValueIndex,
                        new[] { new GUIContent("聚合为一个图集 (Together)"), new GUIContent("按实际父目录 (PerFolder)") }, ResourceWorkbenchGui.Popup);
                    if (field.enumValueIndex == (int)ResourceAtlasGrouping.Together)
                    {
                        GUILayout.Label("Together：本规则收集的 Sprite 纹理聚合为一个图集资产，使用下方完整输出地址。", ResourceWorkbenchGui.Secondary);
                    }
                    else if (field.enumValueIndex == (int)ResourceAtlasGrouping.PerFolder)
                    {
                        GUILayout.Label("PerFolder：按 Sprite 纹理的实际父目录分别生成图集，递归子目录各自输出。地址依次拼接规则地址前缀、来源目录的地址前缀和目录内相对路径；完整地址见生成计划。", ResourceWorkbenchGui.Secondary);
                    }

                    GUILayout.Label("一条规则可生成多个图集资产；这不决定 Bundle 分包，分包仍使用目标组的打包设置。", ResourceWorkbenchGui.Secondary);
                    continue;
                }
                var label = field.name switch
                {
                    "id" => "规则 ID",
                    "groupId" => "目标分组",
                    "address" => entry.FindPropertyRelative("grouping").enumValueIndex == (int)ResourceAtlasGrouping.PerFolder ? "输出地址前缀" : "输出地址",
                    "allowRotation" => "允许旋转",
                    "tightPacking" => "紧密打包",
                    "padding" => "边距（像素）",
                    "maxTextureSize" => "最大纹理尺寸",
                    "readable" => "CPU 可读",
                    "mipMaps" => "生成 MipMaps",
                    "srgb" => "sRGB",
                    "filterMode" => "过滤方式",
                    "compression" => "纹理压缩",
                    _ => field.displayName
                };
                var help = field.name switch
                {
                    "id" => "规则的稳定标识，用于关联生成记录；同一配置中不能重复。",
                    "groupId" => "图集输出加入此 Group，不会因此把所有源纹理登记为可直接加载的资源。",
                    "address" => "图集的加载地址；按目录输出时用作地址前缀，预览计划可查看完整结果。",
                    "allowRotation" => "允许图集打包时旋转 Sprite。",
                    "tightPacking" => "按 Sprite 轮廓紧密排列；不改变源纹理的导入设置。",
                    "padding" => "Sprite 之间预留的像素间距。",
                    "maxTextureSize" => "生成图集纹理的最大尺寸。",
                    "readable" => "允许 CPU 读取图集纹理；会增加内存开销。",
                    "mipMaps" => "为图集纹理生成多级纹理。",
                    "srgb" => "将图集纹理作为 sRGB 颜色数据处理。",
                    "filterMode" => "图集纹理的采样过滤方式。",
                    "compression" => "图集纹理的压缩设置，不是 Bundle 压缩。",
                    _ => field.tooltip
                };
                ResourceWorkbenchGui.Property(field, new GUIContent(label, help));
            } while (field.NextVisible(false));
        }

        /// <summary>编辑规则来源引用数组，移除引用不删除源文件，实际范围由生成服务复核。</summary>
        private static void DrawSources(SerializedProperty sources)
        {
            GUILayout.Label(new GUIContent("来源文件 / 目录（" + sources.arraySize + " 项）", "选择已登记来源目录内的 Sprite 纹理或文件夹；文件夹会递归收集。"), GUILayout.Height(ResourceWorkbenchGui.FieldHeight));
            var remove = -1;
            for (var i = 0; i < sources.arraySize; i++)
            {
                var source = sources.GetArrayElementAtIndex(i);
                EditorGUILayout.BeginHorizontal();
                GUI.SetNextControlName("Resource.Atlas." + source.propertyPath);
                EditorGUI.PropertyField(EditorGUILayout.GetControlRect(false, ResourceWorkbenchGui.FieldHeight, GUILayout.MinWidth(0)), source, GUIContent.none, false);
                if (GUILayout.Button(new GUIContent("移除", "只移除此来源数组项，不删除源文件、目录或其 meta；支持 Undo。"), ResourceWorkbenchGui.Button, GUILayout.Width(44), GUILayout.Height(ResourceWorkbenchGui.FieldHeight)))
                {
                    remove = i;
                }

                EditorGUILayout.EndHorizontal();
            }
            var add = GUILayout.Button(new GUIContent("添加来源", "添加一个空槽，再选择已登记来源目录内的 Sprite 纹理或文件夹。"), ResourceWorkbenchGui.Button, GUILayout.Width(88), GUILayout.Height(ResourceWorkbenchGui.FieldHeight));
            GUILayout.Label("来源目录可以递归收集 Sprite，但不能包含图集生成目录。移除来源不会删除资产；修改后请保存规则。", ResourceWorkbenchGui.Secondary);
            if (remove >= 0)
            {
                var size = sources.arraySize;
                sources.DeleteArrayElementAtIndex(remove);
                // Unity 的对象数组可能先清空引用；再删除空槽，确保一次移除就是减少一项。
                if (sources.arraySize == size)
                {
                    sources.DeleteArrayElementAtIndex(remove);
                }
            }
            else if (add)
            {
                var index = sources.arraySize++;
                sources.GetArrayElementAtIndex(index).objectReferenceValue = null;
            }
        }

        /// <summary>复核项目登记和可选预期配置后，仅保存该可编辑规则资产，不保存项目或副本。</summary>
        internal static void Save(ResourceProject project, ResourceAtlasConfiguration expected = null)
        {
            ResourceEditorOperations.RequireEditableProject(project);
            if (!ResourceAtlasGenerator.IsConfigured(project))
            {
                throw new InvalidOperationException("没有已登记的规则资产，不能保存规则。");
            }

            var configuration = project.automaticAtlases;
            if (!ReferenceEquals(expected, null) && (!expected || expected != configuration))
            {
                throw new InvalidOperationException("正在编辑的图集配置与此项目登记的配置不同；未保存当前配置或其他副本。");
            }

            var path = AssetDatabase.GetAssetPath(configuration);
            ResourceBuildOperation.ValidateAssetPath(path);
            ResourceBuildOperation.RejectLinks(path + ".meta");
            if (!AssetDatabase.IsOpenForEdit(configuration))
            {
                throw new InvalidOperationException("规则资产不可编辑。");
            }

            AssetDatabase.SaveAssetIfDirty(configuration);
        }
    }

    /// <summary>协调图集规则草稿、只读计划及显式生成操作，旧计划保留但必须标记过期。</summary>
    internal sealed class ResourceAtlasView
    {
        private ResourceAtlasPlan _plan;
        private bool _isStale;
        private Exception _error;
        private bool _showErrorDetails;
        private DateTime _calculatedAt;
        private int _tab;
        private int _rule;
        private int _selected;
        private bool _showCompactEditor;
        private bool _showTextureSettings;
        private bool _showDanger;
        private int _drawnRule = -1;
        private string _folder = "GeneratedAtlases";
        private DefaultAsset _generationParent;
        private float _listWidth;
        private Vector2 _listScroll;
        private Vector2 _detailScroll;
        private Vector2 _sourceScroll;
        private ResourceBuildAsset[] _explicitAtlases;
        /// <summary>将已有生成计划或手动图集列表标记过期，不立即读取或生成。</summary>
        internal void Invalidate()
        {
            if (_plan != null || _explicitAtlases != null)
            {
                _isStale = true;
            }
        }
        /// <summary>清除当前项目的计划、手动图集和来源选择上下文。</summary>
        internal void Reset()
        {
            _plan = null;
            _explicitAtlases = null;
            _error = null;
            _generationParent = null;
            _rule = _selected = _tab = 0;
            _isStale = false;
        }
        /// <summary>从分包来源定位自动图集规则，并在窄窗口显示规则编辑区。</summary>
        internal void SelectRule(int index)
        {
            _tab = 0;
            _rule = index;
            _showCompactEditor = true;
        }

        /// <summary>组织自动与手动图集页面，读取计划和写入准备使用不同的显式操作入口。</summary>
        internal void Draw(ResourceProject project, float width, Action changed, Action<Func<string>> run, Action<Func<string>> queue)
        {
            _tab = ResourceWorkbenchGui.Navigation(_tab, new[] { "自动图集", "手动图集" });
            bool configured;
            try
            {
                configured = ResourceAtlasGenerator.IsConfigured(project);
            }
            catch (Exception failure)
            {
                _error = failure;
                EditorGUILayout.HelpBox("无法读取图集配置，请查看错误详情并检查项目登记的规则资产。", MessageType.Error);
                _showErrorDetails = ResourceWorkbenchGui.Disclosure(_showErrorDetails, "图集配置错误详情");
                if (_showErrorDetails)
                {
                    GUILayout.Label(ResourceEditorText.FailureDetails(_error), ResourceWorkbenchGui.Secondary);
                }

                GUILayout.FlexibleSpace();
                return;
            }
            if (_tab == 0 && !configured)
            {
                EditorGUILayout.HelpBox("自动图集是可选功能。启用时单独新建并登记生成目录和规则资产，不接管已有目录；其他资源无需搬入该目录。", MessageType.None);
                _generationParent = ResourceWorkbenchGui.ObjectField("生成目录的父目录", _generationParent, "选择 Assets 下已存在的文件夹，只在其中新建图集子目录；不会接管父目录或已有内容。建议选择来源目录之外的位置。");
                var parentPath = _generationParent ? AssetDatabase.GetAssetPath(_generationParent) : "";
                var parentValid = AssetDatabase.IsValidFolder(parentPath) && (parentPath == "Assets" || parentPath.StartsWith("Assets/", StringComparison.Ordinal)) &&
                    !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(parentPath)) &&
                    !ResourceProjectPaths.Contains("Assets/StreamingAssets", parentPath);
                var configurationDirectory = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(project))?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(configurationDirectory) && project.sourceDirectories != null &&
                    !project.sourceDirectories.Any(x => x == null || string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(x.guid)) || ResourceProjectPaths.Contains(AssetDatabase.GUIDToAssetPath(x.guid), configurationDirectory)))
                {
                    ResourceWorkbenchGui.CopyLine("建议的父目录", configurationDirectory, Math.Max(200, width - 24));
                }

                if (_generationParent && !parentValid)
                {
                    EditorGUILayout.HelpBox("请选择Assets或其下已有的文件夹，不能使用文件、Packages或StreamingAssets。", MessageType.Warning);
                }

                _folder = ResourceWorkbenchGui.TextField("新输出子目录", _folder, false, "新建图集生成目录，名称不能与已有目录冲突；不会移动已有资源。");
                if (parentValid)
                {
                    ResourceWorkbenchGui.CopyLine("将新建目录", parentPath + "/" + _folder, Math.Max(200, width - 24));
                }
                else
                {
                    GUILayout.Label("选择父目录后才可创建；选择文件夹本身不会写入或登记任何内容。", ResourceWorkbenchGui.Secondary);
                }

                using (new EditorGUI.DisabledScope(!parentValid))
                {
                    if (GUILayout.Button("创建图集规则与输出范围...", ResourceWorkbenchGui.Button, GUILayout.Width(230)))
                    {
                        var name = _folder;
                        var parentGuid = AssetDatabase.AssetPathToGUID(parentPath);
                        queue(() =>
                        {
                            if (AssetDatabase.GUIDToAssetPath(parentGuid) != parentPath)
                            {
                                throw new InvalidOperationException("生成目录的父文件夹已移动或变化，请重新选择后创建。");
                            }

                            ResourceAtlasGenerator.CreateConfiguration(project, parentPath, name);
                            changed();
                            return "已创建并登记图集生成目录和规则资产。接下来添加规则并保存；尚未生成图集。";
                        });
                    }
                }

                GUILayout.FlexibleSpace();
                return;
            }
            if (_tab == 0)
            {
                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(true))
                {
                    ResourceWorkbenchGui.ObjectField("图集规则资产", project.automaticAtlases);
                }

                if (GUILayout.Button("定位", ResourceWorkbenchGui.Button, GUILayout.Width(48)))
                {
                    Selection.activeObject = project.automaticAtlases;
                    EditorGUIUtility.PingObject(project.automaticAtlases);
                }
                EditorGUILayout.EndHorizontal();
                if (EditorUtility.IsDirty(project.automaticAtlases))
                {
                    GUILayout.Label("规则未保存 *  /  Ctrl+S仅保存此规则资产", EditorStyles.boldLabel);
                }
            }
            GUILayout.Label(_isStale ? "生成计划或图集列表已过期，请重新读取；当前仍保留上次结果。" : _calculatedAt == default ? "尚未读取图集计划或列表。" : "最近读取 " + _calculatedAt.ToLocalTime().ToString("HH:mm:ss"), ResourceWorkbenchGui.Secondary);
            if (width < 900)
            {
                _showCompactEditor = ResourceWorkbenchGui.Navigation(_showCompactEditor ? 1 : 0, new[] { "生成计划", "编辑规则 / 输出详情" }) == 1;
            }

            var wide = width >= 900;
            var maxListWidth = Math.Max(260, width - 426);
            var displayedListWidth = Mathf.Clamp(_listWidth > 0 ? _listWidth : width * .33f, 260, maxListWidth);
            var detailWidth = wide ? width - displayedListWidth - 6 : width;
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            if (wide || !_showCompactEditor)
            {
                DrawList(GUILayoutUtility.GetRect(wide ? displayedListWidth : width, wide ? displayedListWidth : width, 100, 100000, GUILayout.Width(wide ? displayedListWidth : width), GUILayout.ExpandHeight(true)));
            }

            if (wide)
            {
                var split = GUILayoutUtility.GetRect(6, 6, 100, 100000, GUILayout.Width(6), GUILayout.ExpandHeight(true));
                var dragging = Event.current.type == EventType.MouseDrag && GUIUtility.hotControl != 0;
                EditorGUI.BeginChangeCheck();
                ResourceWorkbenchGui.Splitter(split, ref displayedListWidth, 260, maxListWidth);
                // 只记录本分隔条实际消费的拖动；窗口缩小或普通重绘只钳制显示。
                if (EditorGUI.EndChangeCheck() && dragging && Event.current.type == EventType.Used)
                {
                    _listWidth = displayedListWidth;
                }
            }
            if (wide || _showCompactEditor)
            {
                EditorGUILayout.BeginVertical(GUILayout.Width(detailWidth), GUILayout.ExpandHeight(true));
                _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll, GUILayout.ExpandHeight(true));
                if (_tab == 0)
                {
                    DrawRules(project, changed, run);
                }
                else
                {
                    DrawExplicitDetails(project, detailWidth, run);
                }

                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
            if (_error != null)
            {
                EditorGUILayout.HelpBox("图集操作未完成。请查看错误详情；旧计划和旧输出不代表本次操作成功。", MessageType.Error);
                _showErrorDetails = ResourceWorkbenchGui.Disclosure(_showErrorDetails, "图集操作错误详情");
                if (_showErrorDetails)
                {
                    GUILayout.Label(ResourceEditorText.FailureDetails(_error), ResourceWorkbenchGui.Secondary);
                }
            }
            EditorGUILayout.BeginHorizontal();
            if (_tab == 0)
            {
                if (GUILayout.Button("保存规则", ResourceWorkbenchGui.Button, GUILayout.Width(88)))
                {
                    run(() =>
                    {
                        ResourceAtlasFields.Save(project);
                        return "图集规则已保存；未保存资源项目或场景。";
                    });
                }

                if (GUILayout.Button("预览生成计划", ResourceWorkbenchGui.Button, GUILayout.Width(124)))
                {
                    queue(() => ReadPlan(project));
                }

                if (ResourceWorkbenchGui.PrimaryAction("生成 / 更新并准备", GUILayout.Width(154)))
                {
                    queue(() =>
                {
                    try
                    {
                        var executed = ResourceAtlasGenerator.Generate(project);
                        changed();
                        ReadPlan(project);
                        return "自动图集检查与处理完成。正式构建前仍会重新验证输入。\n处理详情：\n" + executed;
                    }
                    catch (Exception failure)
                    {
                        _error = failure;
                        _isStale = true;
                        throw;
                    }
                });
                }
            }
            else
            {
                if (GUILayout.Button("读取手动图集", GUILayout.Height(26)))
                {
                    queue(() =>
                {
                    try
                    {
                        _explicitAtlases = ResourceBuildMap.Create(project).Assets.Where(x => x.AssetType == typeof(SpriteAtlas) && x.Source?.AtlasRuleId == null).ToArray();
                        _isStale = false;
                        _calculatedAt = DateTime.UtcNow;
                        _error = null;
                        return "已读取本项目选中的手动图集；未准备、生成或构建。";
                    }
                    catch (Exception failure)
                    {
                        _error = failure;
                        _isStale = true;
                        throw;
                    }
                });
                }

                if (GUILayout.Button("准备本项目图集", GUILayout.Height(26)))
                {
                    queue(() =>
                {
                    var count = ResourceEditorOperations.PrepareAtlases(project);
                    changed();
                    return "图集准备完成，共 " + count + " 个。已先处理自动图集，再准备所选手动图集；未构建或发布。";
                });
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.Label(_tab == 0 ? "失效输出会保留，需要检查引用后手动删除；不会自动清理。" :
                "“准备本项目图集”也会生成或更新自动图集，不是只读操作。", ResourceWorkbenchGui.Secondary);
        }

        /// <summary>只读计算自动图集计划并记录时间，失败时保留旧结果但标记过期。</summary>
        private string ReadPlan(ResourceProject project)
        {
            try
            {
                _plan = ResourceAtlasGenerator.Preview(project);
                _calculatedAt = DateTime.UtcNow;
                _isStale = false;
                _error = null;
                _selected = Mathf.Clamp(_selected, 0, Math.Max(0, _plan.Changes.Count - 1));
                return "图集生成计划已读取；未生成或准备。\n计划详情：\n" + _plan;
            }
            catch (Exception failure)
            {
                _error = failure;
                _isStale = true;
                throw;
            }
        }

        /// <summary>按当前页虚拟化显示计划输出或手动图集，输出选择不改规则选择。</summary>
        private void DrawList(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            var count = _tab == 0 ? _plan?.Changes.Count ?? 0 : _explicitAtlases?.Length ?? 0;
            GUI.Label(new Rect(rect.x + 8, rect.y + 4, rect.width - 16, 22), _tab == 0 ? "状态 / 地址 / 分组" : "手动图集 / 地址", EditorStyles.boldLabel);
            var inner = new Rect(rect.x + 2, rect.y + 28, rect.width - 4, Math.Max(1, rect.height - 30));
            _listScroll = GUI.BeginScrollView(inner, _listScroll, new Rect(0, 0, Math.Max(0, inner.width - 18), Math.Max(inner.height, count * 44)));
            var first = Math.Max(0, (int)(_listScroll.y / 44));
            for (var i = first; i < Math.Min(count, first + Mathf.CeilToInt(inner.height / 44) + 1); i++)
            {
                string label, path;
                if (_tab == 0)
                {
                    var item = _plan.Changes[i];
                    label = Status(item.Kind) + "  " + item.Address + "\n" + (item.GroupId ?? "不进入构建") + " / " + item.Key;
                    path = item.Path;
                }
                else
                {
                    var item = _explicitAtlases[i];
                    label = System.IO.Path.GetFileName(item.SourcePath) + "\n" + item.Address.Path;
                    path = item.SourcePath;
                }
                if (ResourceWorkbenchGui.SelectableRow(new Rect(4, i * 44, Math.Max(20, inner.width - 26), 42), _selected == i, new GUIContent(label, path)))
                {
                    _selected = i;
                }
            }
            if (count == 0)
            {
                GUI.Label(new Rect(6, 4, Math.Max(20, inner.width - 20), 44), _tab == 0 ?
                _plan == null ? "尚未读取生成计划。\n点击下方“预览生成计划”。" : "当前计划没有图集输出。\n添加规则和来源后重新预览。" :
                _explicitAtlases == null ? "尚未读取手动图集。\n点击下方“读取手动图集”。" : "本次读取没有选中的手动图集。", EditorStyles.wordWrappedLabel);
            }

            GUI.EndScrollView();
        }

        /// <summary>可撤销地编辑规则草稿，并展示所选输出及独立的失效输出删除入口。</summary>
        private void DrawRules(ResourceProject project, Action changed, Action<Func<string>> run)
        {
            var config = project.automaticAtlases;
            var rules = config.rules;
            EditorGUILayout.BeginHorizontal();
            if (rules != null && rules.Count > 0)
            {
                _rule = EditorGUILayout.Popup("正在编辑的规则", Mathf.Clamp(_rule, 0, rules.Count - 1), rules.Select(x => x?.id ?? "[空规则]").ToArray(), ResourceWorkbenchGui.Popup, GUILayout.Height(ResourceWorkbenchGui.FieldHeight));
            }

            if (GUILayout.Button("添加规则", ResourceWorkbenchGui.Button, GUILayout.Width(76)))
            {
                run(() =>
            {
                var id = "rule-1";
                var n = 1;
                while (config.rules.Any(x => x != null && x.id == id))
                {
                    id = "rule-" + ++n;
                }

                Undo.RecordObject(config, "添加自动图集规则");
                config.rules.Add(new ResourceAtlasRule { id = id, groupId = project.groups.FirstOrDefault()?.id, address = "atlases/" + id });
                EditorUtility.SetDirty(config);
                _rule = config.rules.Count - 1;
                changed();
                return "规则已添加，请配置来源文件 / 目录并保存；未生成输出。";
            });
            }

            EditorGUILayout.EndHorizontal();
            if (rules != null && rules.Count > 0)
            {
                using var serialized = new SerializedObject(config);
                if (_drawnRule != _rule)
                {
                    serialized.FindProperty("rules").GetArrayElementAtIndex(_rule).FindPropertyRelative("sources").isExpanded = true;
                    _drawnRule = _rule;
                }
                if (ResourceAtlasFields.Draw(serialized, _rule, _showTextureSettings))
                {
                    changed();
                }

                _showTextureSettings = ResourceWorkbenchGui.Disclosure(_showTextureSettings, "纹理设置");
                if (!_showTextureSettings)
                {
                    var current = rules[_rule];
                    GUILayout.Label("最大尺寸 " + current.maxTextureSize + " · " + current.filterMode + " · " + current.compression +
                        "\nCPU 可读 " + (current.readable ? "开" : "关") + " · MipMaps " + (current.mipMaps ? "开" : "关") + " · sRGB " + (current.srgb ? "开" : "关"), ResourceWorkbenchGui.Secondary);
                }
                _showDanger = ResourceWorkbenchGui.Disclosure(_showDanger, "移除规则（不会删除生成资产）");
                if (_showDanger && GUILayout.Button("移除此规则定义", ResourceWorkbenchGui.Button))
                {
                    run(() =>
                {
                    Undo.RecordObject(config, "移除自动图集规则");
                    config.rules.RemoveAt(_rule);
                    _rule = Math.Max(0, _rule - 1);
                    _drawnRule = -1;
                    EditorUtility.SetDirty(config);
                    changed();
                    return "规则已移除，可撤销。旧输出仍保留；保存并重新预览后可查看失效输出。";
                });
                }
            }
            if (_plan == null || _plan.Changes.Count == 0)
            {
                return;
            }

            _selected = Mathf.Clamp(_selected, 0, _plan.Changes.Count - 1);
            var output = _plan.Changes[_selected];
            GUILayout.Space(12);
            GUILayout.Label("所选输出 · " + Status(output.Kind), EditorStyles.boldLabel);
            GUILayout.Label(output.Rule == null ? "原规则已不存在，此输出不会进入新构建。" : "来源规则：" + output.Rule.id + "。一条规则可以生成多个输出；选择输出不会更改上方正在编辑的规则。", ResourceWorkbenchGui.Secondary);
            if (output.Rule != null && rules.Count(x => x?.id == output.Rule.id) == 1 && GUILayout.Button("编辑此输出的来源规则", ResourceWorkbenchGui.Button))
            {
                _rule = rules.FindIndex(x => x?.id == output.Rule.id);
            }

            ResourceWorkbenchGui.CopyLine("输出路径", output.Path, 360);
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(output.Guid) || AssetDatabase.GUIDToAssetPath(output.Guid) != output.Path))
            {
                if (GUILayout.Button("定位已存在输出"))
                {
                    EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(output.Path));
                }
            }

            GUILayout.Label("源纹理（不展示具体由哪个 Prefab 引入）", EditorStyles.boldLabel);
            var sourceRect = GUILayoutUtility.GetRect(80, Mathf.Clamp(output.Sources.Count * 26, 26, 104), GUILayout.ExpandWidth(true));
            _sourceScroll = GUI.BeginScrollView(sourceRect, _sourceScroll, new Rect(0, 0, Math.Max(20, sourceRect.width - 18), Math.Max(sourceRect.height, output.Sources.Count * 26)));
            var first = Math.Max(0, (int)(_sourceScroll.y / 26));
            for (var i = first; i < Math.Min(output.Sources.Count, first + 5); i++)
            {
                GUI.Label(new Rect(2, i * 26, Math.Max(20, sourceRect.width - 22), 26), new GUIContent(System.IO.Path.GetFileName(output.Sources[i]), output.Sources[i]));
            }

            GUI.EndScrollView();
            if (output.Kind == ResourceAtlasChangeKind.Obsolete && GUILayout.Button("检查引用并删除失效输出..."))
            {
                var key = output.Key;
                var path = output.Path;
                if (ResourceOperationConfirmation.Confirm("删除失效生成图集", path + "\nKey=" + key + "\n只删除此项目登记的输出及 .meta。执行前重新检查生成记录、文件和引用；不会删除源资源，遇到冲突时停止。", "检查并删除图集", new ResourceConfirmationSummary
                {
                    Fields = new[] { ("资源项目", ProjectId: project.projectId), ("删除对象", "已失效的图集输出及配套 .meta"), ("输出路径", path) },
                    Effects = new[] { "重新检查生成记录、文件和引用后，删除这一项失效输出。", "不删除源纹理，不移除其他规则，也不重新构建或发布。" },
                    Notice = "输出仍被引用或已经被外部修改时将停止；删除后不会自动恢复。"
                }, details:
                    new ResourceConfirmationDetails("ProjectId=" + project.projectId + "\nProjectGuid=" + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project)) + "\nKey=" + key,
                        "登记输出=" + path + "\n配套元数据=" + path + ".meta",
                        "只删除本项目登记的失效输出。重新检查生成记录、内容、meta 和引用，遇到冲突时停止。不会删除源资源，不生成、不构建，也不自动恢复或重试。")))
                {
                    run(() =>
                    {
                        // 删除是生成服务的真实资产操作，不属于上方规则草稿的 Undo；确认后仍重查归属、内容和引用。
                        ResourceAtlasGenerator.DeleteObsolete(project, key);
                        changed();
                        ReadPlan(project);
                        return "已检查引用并删除本项目的失效图集输出；未删除源资产。";
                    });
                }
            }
        }

        /// <summary>展示手动图集的解析身份，打开 Inspector 前重查来源定义及 GUID 对应路径。</summary>
        private void DrawExplicitDetails(ResourceProject project, float width, Action<Func<string>> run)
        {
            if (_explicitAtlases == null || _explicitAtlases.Length == 0)
            {
                GUILayout.Label("读取并选择手动图集后，可查看来源与使用要求。", ResourceWorkbenchGui.Secondary);
                return;
            }
            _selected = Mathf.Clamp(_selected, 0, _explicitAtlases.Length - 1);
            var item = _explicitAtlases[_selected];
            ResourceWorkbenchGui.CopyLine("图集路径", item.SourcePath, width);
            ResourceWorkbenchGui.CopyLine("主资产地址", item.Address.ToString(), width);
            GUILayout.Label("读取时已检查来源目录、非 Variant、Include in Build 关闭及 Sprite 名称唯一。配置变化后请重读；准备和构建时仍会重新检查。", ResourceWorkbenchGui.Secondary);
            if (GUILayout.Button("在 Inspector 编辑图集"))
            {
                run(() =>
            {
                ResourceBuildSourceLocator.Resolve(project, item.Source);
                if (AssetDatabase.GUIDToAssetPath(item.Guid) != item.SourcePath)
                {
                    throw new InvalidOperationException("手动图集的资产或路径已变化，请重新读取。");
                }

                Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(item.SourcePath);
                return "已核对图集资产并在 Inspector 中选中；未保存或准备图集。";
            });
            }
        }

        /// <summary>将计划中的输出变化种类转换为展示状态，不表示本次已完成写入。</summary>
        private static string Status(ResourceAtlasChangeKind kind) => kind switch
        {
            ResourceAtlasChangeKind.Create => "待创建 (Create)",
            ResourceAtlasChangeKind.Update => "待更新 (Update)",
            ResourceAtlasChangeKind.Current => "当前有效 (Current)",
            _ => "已失效 (Obsolete)"
        };
    }
}
