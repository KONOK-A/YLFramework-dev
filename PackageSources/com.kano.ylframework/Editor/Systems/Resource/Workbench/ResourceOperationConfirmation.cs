using UnityEditor;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>把正式操作和状态映射为界面说明，不参与状态判断或执行校验。</summary>
    internal static class ResourceEditorText
    {
        /// <summary>将已记录的操作标识转换为展示名称。</summary>
        internal static string Operation(string value) => value switch
        {
            "BuildOnly" => "仅构建",
            "BuildAndPublish" => "构建并本地发布",
            "ExecuteRecipe" => "执行构建配方",
            "PublishVerifiedBuild" => "发布已有构建",
            _ => "资源操作"
        };

        /// <summary>描述预检实际覆盖的范围，不将静态通过表述为完整验证。</summary>
        internal static string Preflight(ResourcePreflightResult value) => value?.State switch
        {
            ResourcePreflightState.StaticReady => "配置基础检查通过；资源、密钥和输出将在执行前继续检查。",
            ResourcePreflightState.Complete => "资源检查完成，尚未构建或发布；执行前会再次核对。",
            ResourcePreflightState.AtlasPreparationRequired => "图集需要生成或更新；正式构建会先准备图集，再继续检查资源。",
            ResourcePreflightState.StaticBlocked => "配置检查未通过，请查看技术详情中的原因后修正配置。",
            ResourcePreflightState.Failed => "资源检查未完成，请查看技术详情中的错误原因。",
            _ => "尚未检查资源。"
        };

        /// <summary>保留原始异常及调用信息供技术详情展示，不改写失败原因。</summary>
        internal static string FailureDetails(System.Exception error) => error == null ? "" : "原始异常与调用信息：\n" + error;

        /// <summary>展示请求状态，缺失状态保持为不适用而非补成成功。</summary>
        internal static string RequestStatus(ResourceRequestStatus? value) => value switch
        {
            ResourceRequestStatus.Pending => "进行中",
            ResourceRequestStatus.Succeeded => "已完成",
            ResourceRequestStatus.Failed => "失败",
            ResourceRequestStatus.Canceled => "已取消",
            ResourceRequestStatus.Invalidated => "已失效",
            null => "不适用",
            _ => "未知状态（" + value + "）"
        };
    }

    /// <summary>确认窗口中的身份、文件范围和执行约束文本，不承载操作回调。</summary>
    internal sealed class ResourceConfirmationDetails
    {
        internal readonly string Identity;
        internal readonly string Paths;
        internal readonly string Execution;

        /// <summary>保存调用方提供的完整说明，不自行探测或验证文件系统。</summary>
        internal ResourceConfirmationDetails(string identity, string paths, string execution)
        {
            Identity = identity;
            Paths = paths;
            Execution = execution;
        }
    }

    /// <summary>由操作入口提供的确认摘要；内容是告知材料，不是最终安全凭据。</summary>
    internal sealed class ResourceConfirmationSummary
    {
        internal (string Label, string Value)[] Fields = System.Array.Empty<(string, string)>();
        internal string[] Effects = System.Array.Empty<string>();
        internal string Notice;
        internal ResourceConfirmationDetails Details;

        /// <summary>按本次预检身份及所选构建选项组织影响说明，不执行构建或输出路径预检。</summary>
        internal static ResourceConfirmationSummary Build(ResourcePreflightResult input, string action, bool force, bool generate, string directory) => new()
        {
            Fields = new[] { ("资源项目", input.ProjectId), ("构建平台", input.Platform), ("资源兼容标识（App）", input.AppContentCompatibilityId),
                ("本次操作", action), ("资源检查", ResourceEditorText.Preflight(input)),
                ("强制重建", force ? "启用" : "关闭"), ("地址常量生成", generate ? "启用 → " + directory : "不执行") },
            Effects = new[] { "写入本次构建工作目录；发布操作还会写入本地发布目录和发布索引。", "不会上传资源、部署内置资源或切换正在运行的资源版本。" },
            Notice = "构建开始后不能交互取消。请核对本次项目、操作及已启用选项。",
            Details = new ResourceConfirmationDetails("ProjectId=" + input.ProjectId + "\nProjectGuid=" + input.ProjectGuid + "\nPlatform=" + input.Platform +
                "\nUnity=" + Application.unityVersion + "\nApp=" + input.AppContentCompatibilityId,
                "Project配置=" + AssetDatabase.GUIDToAssetPath(input.ProjectGuid) + "\n地址常量目标=" + (generate ? directory : "不生成") +
                "\n构建工作目录将在执行时按本次输入确定；本次检查未提供该路径。",
                "操作=" + action + "\nForceRebuild=" + force + "\nGenerateReferences=" + generate + "\n资源预检=" + input.State +
                "\n执行前会重新验证输入。发布事务未完成时，请使用“恢复本地发布事务”。不会部署、上传或切换运行版本。")
        };
    }

    /// <summary>仅确认完整只读范围，不持有操作回调；执行仍归原队列和正式服务。</summary>
    internal sealed class ResourceOperationConfirmation : EditorWindow
    {
        internal string Message;
        internal string Positive;
        internal bool IsAccepted;
        internal ResourceConfirmationSummary Summary;
        internal ResourceConfirmationDetails Details;
        private Vector2 _scroll;
        private bool _showTechnical;
        private bool _showRawScope;

        /// <summary>同步显示模态说明并返回用户是否接受，退出时销毁临时窗口；不执行或授权绕过后续复验。</summary>
        internal static bool Confirm(string title, string message, string positive, ResourceConfirmationSummary summary = null, ResourceConfirmationDetails details = null)
        {
            var window = CreateInstance<ResourceOperationConfirmation>();
            window.titleContent = new GUIContent(title);
            window.Message = message;
            window.Positive = positive;
            window.Summary = summary;
            window.Details = details;
            var main = EditorGUIUtility.GetMainWindowPosition();
            window.minSize = new Vector2(540, 320);
            window.position = new Rect(main.center.x - 360, main.center.y - 260, 720, 520);
            try
            {
                window.ShowModalUtility();
                return window.IsAccepted;
            }
            finally
            {
                if (window)
                {
                    DestroyImmediate(window);
                }
            }
        }

        /// <summary>展示摘要与可复制详情，仅确认按钮设置接受结果，关闭和 Esc 保持取消。</summary>
        private void OnGUI()
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                Event.current.Use();
                Close();
                return;
            }
            GUILayout.Space(10);
            GUILayout.Label(Positive ?? "确认操作", ResourceWorkbenchGui.Title);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (Summary != null)
            {
                foreach (var row in Summary.Fields)
                {
                    ResourceWorkbenchGui.Metric(row.Label, row.Value, row.Value, 160);
                }

                GUILayout.Space(10);
                GUILayout.Label("本次影响", EditorStyles.boldLabel);
                foreach (var effect in Summary.Effects)
                {
                    GUILayout.Label(effect, EditorStyles.wordWrappedLabel);
                    GUILayout.Space(4);
                }
                if (!string.IsNullOrEmpty(Summary.Notice))
                {
                    EditorGUILayout.HelpBox(Summary.Notice, MessageType.Warning);
                }
            }
            var details = Details ?? Summary?.Details;
            if (Summary != null || details != null)
            {
                _showTechnical = ResourceWorkbenchGui.Disclosure(_showTechnical, "技术详情：项目、版本与文件路径");
            }

            if (_showTechnical && details != null)
            {
                DrawSection("项目与版本", details.Identity);
                DrawSection("目录与文件", details.Paths);
                DrawSection("执行与恢复", details.Execution);
                _showRawScope = ResourceWorkbenchGui.Disclosure(_showRawScope, "查看完整操作说明（复制内容）");
            }
            var text = Message ?? "";
            if (Summary == null && (!_showTechnical || details == null) || _showTechnical && (details == null || _showRawScope))
            {
                var height = EditorStyles.wordWrappedLabel.CalcHeight(new GUIContent(text), Mathf.Max(200, position.width - 36));
                EditorGUILayout.SelectableLabel(text, EditorStyles.wordWrappedLabel, GUILayout.MinWidth(0), GUILayout.Height(Mathf.Max(40, height + 8)));
            }
            EditorGUILayout.EndScrollView();
            GUILayout.Label("确认后仍会重新核对输入。关闭或 Esc 等同取消。", ResourceWorkbenchGui.Secondary);
            EditorGUILayout.BeginHorizontal(GUILayout.Height(34));
            if (GUILayout.Button("复制完整说明", ResourceWorkbenchGui.Button, GUILayout.Width(120)))
            {
                EditorGUIUtility.systemCopyBuffer = text;
            }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("取消", ResourceWorkbenchGui.Button, GUILayout.Width(100)))
            {
                Close();
            }

            if (ResourceWorkbenchGui.PrimaryAction(Positive ?? "确认", GUILayout.Width(164)))
            {
                IsAccepted = true;
                Close();
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(4);
        }

        /// <summary>按当前窗口宽度显示可选择的完整技术说明，避免长路径只剩截断值。</summary>
        private void DrawSection(string title, string value)
        {
            GUILayout.Space(8);
            GUILayout.Label(title, EditorStyles.boldLabel);
            var content = new GUIContent(value ?? "");
            var height = EditorStyles.wordWrappedLabel.CalcHeight(content, Mathf.Max(200, position.width - 40));
            EditorGUILayout.SelectableLabel(content.text, EditorStyles.wordWrappedLabel, GUILayout.MinWidth(0), GUILayout.Height(Mathf.Max(22, height + 4)));
        }
    }
}
