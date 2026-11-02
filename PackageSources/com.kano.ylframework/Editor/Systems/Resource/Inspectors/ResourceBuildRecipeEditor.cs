using System;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.Resource
{
    /// <summary>供 Inspector 与工作台共用的配方序列化字段绘制。</summary>
    internal static class ResourceBuildRecipeFields
    {
        /// <summary>按基础或高级选项筛选并应用字段编辑，返回是否发生序列化修改。</summary>
        internal static bool Draw(SerializedObject serialized, bool advanced = true, bool advancedOnly = false)
        {
            serialized.Update();
            var field = serialized.GetIterator();
            if (field.NextVisible(true))
            {
                do
                {
                    if (field.name == "m_Script")
                    {
                        continue;
                    }

                    var additional = field.name == "forceRebuild" || field.name == "generateReferences" || field.name == "referenceDirectory";
                    if (additional && !advanced || !additional && advancedOnly)
                    {
                        continue;
                    }

                    GUI.SetNextControlName("Resource.Recipe." + field.name);
                    var label = field.name switch
                    {
                        "project" => "资源项目",
                        "target" => "构建平台",
                        "action" => "执行操作",
                        "appContentCompatibilityId" => "资源兼容标识（App）",
                        "forceRebuild" => "强制重建",
                        "generateReferences" => "生成资源地址常量",
                        "referenceDirectory" => "常量目标目录",
                        _ => field.displayName
                    };
                    var help = field.name switch
                    {
                        "project" => "此配方使用的资源项目；资源选择、压缩和加密设置来自该项目。",
                        "target" => "执行时必须已切换到此平台，并安装对应构建模块；不会自动切换平台。",
                        "action" => "选择仅构建，或在构建后本地发布；不部署、不上传。",
                        "appContentCompatibilityId" => "应用约定的稳定资源兼容标识，不是游戏版本号。",
                        "forceRebuild" => "不直接复用可跳过构建的缓存结果；仍会验证输入和产物。",
                        "generateReferences" => "只用于构建并本地发布；生成失败不会撤销已经发布的版本。",
                        "referenceDirectory" => "Assets 下的常量源码目标目录；不会覆盖未知或手写文件。",
                        _ => field.tooltip
                    };
                    if (field.name == "action")
                    {
                        var action = EditorGUI.Popup(EditorGUILayout.GetControlRect(true, ResourceWorkbenchGui.FieldHeight), new GUIContent(label, help), field.enumValueIndex,
                            new[] { new GUIContent("仅构建 (BuildOnly)"), new GUIContent("构建并本地发布 (BuildAndPublish)") }, ResourceWorkbenchGui.Popup);
                        if (action != field.enumValueIndex)
                        {
                            field.enumValueIndex = action;
                        }
                    }
                    else
                    {
                        ResourceWorkbenchGui.Property(field, new GUIContent(label, help));
                    }
                } while (field.NextVisible(false));
            }

            return serialized.ApplyModifiedProperties();
        }
    }

    /// <summary>提供配方的独立保存、只读解析和经确认的正式执行入口。</summary>
    [CustomEditor(typeof(ResourceBuildRecipe))]
    public sealed class ResourceBuildRecipeEditor : UnityEditor.Editor
    {
        private string _status;
        private Exception _error;
        private ResourceEditorBuildReport _report;
        private bool _showTechnical;

        /// <summary>绘制配方与分阶段执行结果，不把生成失败视为已发布版本回滚。</summary>
        public override void OnInspectorGUI()
        {
            ResourceBuildRecipeFields.Draw(serializedObject);
            EditorGUILayout.HelpBox("配方只保存执行选项，不改变资源项目的分组、压缩或加密设置。查看和保存不会构建；需要加密时，密钥由调用方或配置的进程环境提供。", MessageType.Info);
            var recipe = (ResourceBuildRecipe)target;
            if (GUILayout.Button("保存所选配方"))
            {
                Run(() =>
                {
                    ResourceEditorOperations.SaveRecipe(recipe);
                    return "配方已保存；未保存其他资产或场景。";
                });
            }

            if (GUILayout.Button("查看执行选项（不读取密钥）"))
            {
                Run(() => "执行选项已读取；未构建或发布。\n参数详情：\n" + ResourceEditorOperations.ResolveRecipe(recipe));
            }

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating))
            {
                if (GUILayout.Button("执行已保存配方"))
                {
                    Run(() =>
                {
                    var input = ResourceEditorOperations.ResolveRecipe(recipe);
                    if (!ResourceOperationConfirmation.Confirm("确认执行资源配方", input + "\n按配方执行构建或本地发布；不部署、不上传、不签发。", "执行配方", new ResourceConfirmationSummary
                    {
                        Fields = new[] { ("资源项目", input.ProjectId), ("构建平台", input.Target.ToString()), ("资源兼容标识（App）", input.AppContentCompatibilityId),
                                ("执行操作", ResourceEditorText.Operation(input.Action.ToString())), ("强制重建", input.ForceRebuild ? "启用" : "关闭"),
                                ("资源地址常量", input.GenerateReferences ? "发布后生成 → " + input.ReferenceDirectory : "不生成") },
                        Effects = new[] { "写入构建工作目录；本地发布还会写入发布版本和发布索引。", "不会部署、上传、签发或切换正在运行的资源版本。" },
                        Notice = "只执行已保存的配方。开始后暂时不能取消，执行前仍会检查资源和输出。",
                        Details = new ResourceConfirmationDetails(input.ToString(), "配方=" + input.RecipePath + "\n常量目录=" + (input.ReferenceDirectory ?? "不生成"),
                                "执行时重新检查已保存的配置、资源、密钥和输出。发布提交未确认时，请使用“恢复本地发布事务”，不要直接重复发布。")
                    }))
                    {
                        return "已取消，未开始执行。";
                    }

                    _report = Execute(recipe);
                    return ResourceBuildView.Outcome(_report);
                });
                }
            }

            if (_status != null)
            {
                EditorGUILayout.HelpBox(_status, _error != null || _report?.BuildError != null ? MessageType.Error :
                _report?.PublicationError != null || _report?.GenerationError != null ? MessageType.Warning : MessageType.Info);
            }

            if (_error != null || _report != null)
            {
                _showTechnical = ResourceWorkbenchGui.Disclosure(_showTechnical, "配方执行技术详情");
                if (_showTechnical)
                {
                    if (_error != null)
                    {
                        GUILayout.Label(ResourceEditorText.FailureDetails(_error), ResourceWorkbenchGui.Secondary);
                    }

                    if (_report != null)
                    {
                        GUILayout.Label("执行记录：\n" + _report.Summary, ResourceWorkbenchGui.Secondary);
                        if (_report.BuildError != null)
                        {
                            GUILayout.Label(ResourceEditorText.FailureDetails(_report.BuildError), ResourceWorkbenchGui.Secondary);
                        }

                        if (_report.PublicationError != null)
                        {
                            GUILayout.Label(ResourceEditorText.FailureDetails(_report.PublicationError), ResourceWorkbenchGui.Secondary);
                        }

                        if (_report.GenerationError != null)
                        {
                            GUILayout.Label(ResourceEditorText.FailureDetails(_report.GenerationError), ResourceWorkbenchGui.Secondary);
                        }
                    }
                }
            }
        }

        // Inspector 与脚本调用不各自解析窗口字段。
        internal static ResourceEditorBuildReport Execute(ResourceBuildRecipe recipe) => ResourceEditorOperations.ExecuteRecipe(recipe);

        /// <summary>清除上次展示状态，执行显式操作并保留本次异常供技术详情查看。</summary>
        private void Run(Func<string> operation)
        {
            _error = null;
            _report = null;
            try
            {
                _status = operation();
            }
            catch (Exception failure)
            {
                _error = failure;
                _status = "配方操作未完成，请展开技术详情查看错误原因。";
            }
        }
    }
}
