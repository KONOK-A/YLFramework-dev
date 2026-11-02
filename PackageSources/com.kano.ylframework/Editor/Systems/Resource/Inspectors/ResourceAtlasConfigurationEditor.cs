using System;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.Resource
{
    /// <summary>编辑资源项目定义并显式登记自动图集范围，不因绘制或保存而执行构建。</summary>
    [CustomEditor(typeof(ResourceProject))]
    internal sealed class ResourceProjectInspector : UnityEditor.Editor
    {
        private string _folderName = "GeneratedAtlases";
        private string _generationParentGuid;
        private string _message;
        private bool _showAtlases;
        private Exception _failure;
        private bool _showTechnical;

        /// <summary>展示项目身份、来源与策略，仅通过明确操作保存项目或新建图集配置。</summary>
        public override void OnInspectorGUI()
        {
            var project = (ResourceProject)target;
            serializedObject.Update();
            GUILayout.Label("资源项目身份", EditorStyles.boldLabel);
            ResourceWorkbenchGui.Property(serializedObject.FindProperty("displayName"), new GUIContent("显示名称"));
            ResourceWorkbenchGui.Property(serializedObject.FindProperty("projectId"), new GUIContent("ProjectId"));
            GUILayout.Label("ProjectId 是运行时地址与输出归属，不是显示名称；修改不会迁移旧发布或缓存。", ResourceWorkbenchGui.Secondary);
            GUILayout.Space(10);
            var rootError = ResourceWorkbenchGui.ResourceDirectoriesFields(serializedObject);
            if (rootError != null)
            {
                _message = rootError;
            }

            GUILayout.Label("Group用于组织资源并设置分包方式。同一个Group可以包含不同资源目录中的文件。", ResourceWorkbenchGui.Secondary);
            ResourceWorkbenchGui.Property(serializedObject.FindProperty("groups"), new GUIContent("Group 与资源选择"));
            GUILayout.Space(10);
            GUILayout.Label("构建策略", EditorStyles.boldLabel);
            ResourceWorkbenchGui.Property(serializedObject.FindProperty("compression"), new GUIContent("Bundle 压缩"));
            ResourceWorkbenchGui.Property(serializedObject.FindProperty("contentEncryption"), new GUIContent("内容加密"));
            ResourceWorkbenchGui.Property(serializedObject.FindProperty("contentKeyId"), new GUIContent("KeyId（非密钥）"));
            GUILayout.Label("None 表示不加密，不影响压缩。加密密钥由外部显式提供，不保存在工程；轮换密钥需使用新的 KeyId。", ResourceWorkbenchGui.Secondary);
            ResourceWorkbenchGui.Property(serializedObject.FindProperty("automaticAtlases"), new GUIContent("自动图集配置"));
            serializedObject.ApplyModifiedProperties();
            ResourceWorkbenchGui.DirectoryValidation(project);
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating))
            {
                if (GUILayout.Button("保存资源项目", ResourceWorkbenchGui.Button))
                {
                    try
                    {
                        ResourceEditorOperations.SaveProject(project);
                        _message = null;
                        _failure = null;
                    }
                    catch (Exception error)
                    {
                        _failure = error;
                        _message = "资源项目未能保存。请检查资产是否可编辑，再重试；此操作不会保存Unity场景。";
                    }
                }
            }

            _showAtlases = ResourceWorkbenchGui.Disclosure(_showAtlases, "自动图集（可选）");
            if (_showAtlases && project.automaticAtlases)
            {
                ResourceWorkbenchGui.CopyLine("生成目录", ResourceProjectPaths.GeneratedDirectory(project), EditorGUIUtility.currentViewWidth - 32);
                EditorGUILayout.HelpBox("此目录保存自动生成的SpriteAtlas及其规则配置。构建会先生成并准备图集，再将输出加入指定Group；不会把整个生成目录当作普通来源收集。", MessageType.None);
                if (GUILayout.Button("编辑自动图集规则"))
                {
                    Selection.activeObject = project.automaticAtlases;
                }
            }
            else if (_showAtlases)
            {
                EditorGUILayout.HelpBox("不使用自动图集时，可以暂不设置生成目录。需要时先保存资源项目，再明确选择Assets中的已有父目录并新建子目录；不会接管父目录中的其他文件或已有同名目录。建议放在资源目录之外。", MessageType.None);
                var parent = AssetDatabase.LoadAssetAtPath<DefaultAsset>(AssetDatabase.GUIDToAssetPath(_generationParentGuid));
                parent = ResourceWorkbenchGui.ObjectField("生成目录的父目录", parent);
                _generationParentGuid = parent ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(parent)) : "";
                if (parent)
                {
                    ResourceWorkbenchGui.CopyLine("父目录路径", AssetDatabase.GetAssetPath(parent), EditorGUIUtility.currentViewWidth - 32);
                }

                _folderName = ResourceWorkbenchGui.TextField("新输出子目录", _folderName);
                using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating))
                {
                    if (GUILayout.Button("新建并登记生成目录"))
                    {
                        try
                        {
                            Selection.activeObject = ResourceAtlasGenerator.CreateConfiguration(project, AssetDatabase.GetAssetPath(parent), _folderName);
                            _message = null;
                            _failure = null;
                        }
                        catch (Exception error)
                        {
                            _failure = error;
                            _message = "生成目录未能登记。请先保存资源项目，选择有效父目录并使用尚不存在的子目录名称。未确认属于本次操作的文件不会被覆盖或删除。";
                        }
                    }
                }
            }
            if (_message != null)
            {
                EditorGUILayout.HelpBox(_message, MessageType.Error);
            }

            ResourceWorkbenchGui.FailureDetails(_failure, ref _showTechnical);
        }
    }

    /// <summary>编辑自有图集规则，分离计划预览、生成和失效输出删除。</summary>
    [CustomEditor(typeof(ResourceAtlasConfiguration))]
    internal sealed class ResourceAtlasConfigurationEditor : UnityEditor.Editor
    {
        private ResourceAtlasPlan _plan;
        private string _message;
        private MessageType _messageType;
        private Exception _failure;
        private bool _showTechnical;

        /// <summary>绘制规则和计划状态，生成与删除交由执行时重新验证归属的入口处理。</summary>
        public override void OnInspectorGUI()
        {
            var configuration = (ResourceAtlasConfiguration)target;
            var project = AssetDatabase.LoadAssetAtPath<ResourceProject>(AssetDatabase.GUIDToAssetPath(configuration.projectGuid));
            using (new EditorGUI.DisabledScope(true))
            {
                ResourceWorkbenchGui.ObjectField("所属资源项目", project);
            }

            EditorGUILayout.HelpBox("从已登记资源目录中选择Sprite纹理或文件夹，不能包含生成目录。规则ID用于保持输出身份；可以调整地址和设置，一条规则也可以生成多个图集。", MessageType.None);
            if (ResourceAtlasFields.Draw(serializedObject))
            {
                _plan = null;
                _message = null;
            }
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("保存规则", ResourceWorkbenchGui.Button))
                {
                    Run(() =>
                    {
                        ResourceAtlasFields.Save(project, configuration);
                        return "规则已保存；未生成或准备图集。";
                    });
                }

                if (GUILayout.Button("预览生成计划", ResourceWorkbenchGui.Button))
                {
                    Run(() =>
                    {
                        _plan = ResourceAtlasGenerator.Preview(project);
                        return "图集计划已计算，尚未生成或准备。下方显示各输出的状态和位置。";
                    });
                }

                EditorGUILayout.EndHorizontal();
                if (GUILayout.Button("生成 / 更新并准备图集"))
                {
                    Run(() =>
                {
                    ResourceAtlasGenerator.Generate(project);
                    _plan = ResourceAtlasGenerator.Preview(project);
                    return "图集已生成并准备。此操作没有构建或发布Bundle；正式构建会继续检查资源依赖和产物。";
                });
                }

                if (_plan != null)
                {
                    foreach (var change in _plan.Changes)
                    {
                        var status = change.Kind switch
                        {
                            ResourceAtlasChangeKind.Create => "待创建",
                            ResourceAtlasChangeKind.Update => "待更新",
                            ResourceAtlasChangeKind.Current => "当前有效",
                            _ => "已失效，不进入构建"
                        };
                        EditorGUILayout.LabelField(status + " / " + change.Address, EditorStyles.wordWrappedLabel);
                        EditorGUILayout.SelectableLabel(change.Path, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        if (change.Kind == ResourceAtlasChangeKind.Obsolete && GUILayout.Button("检查引用并删除失效输出：" + change.Key) &&
                            EditorUtility.DisplayDialog("删除已失效的自动图集？", change.Path + "\n只删除这个已登记输出，不删除源图片。执行前会重新检查文件记录和引用；仍被使用或已被外部修改时不会删除。", "检查并删除", "取消"))
                        {
                            Run(() =>
                            {
                                ResourceAtlasGenerator.DeleteObsolete(project, change.Key);
                                _plan = null;
                                return "已删除确认无引用的自有失效输出；源 Sprite/Texture 未修改。";
                            });
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
            if (_message != null)
            {
                EditorGUILayout.HelpBox(_message, _messageType);
            }

            ResourceWorkbenchGui.FailureDetails(_failure, ref _showTechnical);
        }

        /// <summary>展示显式图集操作结果，失败时清除旧计划并保留异常。</summary>
        private void Run(Func<string> action)
        {
            try
            {
                _message = action();
                _failure = null;
                _messageType = MessageType.Info;
            }
            catch (Exception error)
            {
                _plan = null;
                _failure = error;
                _message = "图集操作未完成。请查看原始错误，检查来源、规则及已登记输出后重试；不要手工覆盖未知文件。";
                _messageType = MessageType.Error;
            }
        }
    }
}
