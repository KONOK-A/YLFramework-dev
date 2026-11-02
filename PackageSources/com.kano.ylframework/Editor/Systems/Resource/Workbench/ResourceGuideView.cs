using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.Resource
{
    /// <summary>提供入门任务导航和正式文档、样例源码入口，不执行样例或代填应用配置。</summary>
    internal sealed class ResourceGuideView
    {
        private Vector2 _scroll;
        private string _error;
        private bool _showTechnical;
        private bool _showReport;
        internal const string BatchExampleHelp = "无窗口入口是可复用的调用样例，不是通用CI工具；请按Unity工程的构建环境配置使用。";
        internal static readonly (string Label, string Path)[] References =
        {
            ("打开 Resource 使用文档", "Documentation~/Modules/Resource.md"),
            ("导入 Resource Startup 样例（Package Manager）", "Samples~/ResourceStartup"),
            ("定位构建配方 Inspector 源码", "Editor/Systems/Resource/Inspectors/ResourceBuildRecipeEditor.cs"),
            ("定位内置部署实现", "Editor/Systems/Resource/Publishing/ResourceBuiltInDeployer.cs"),
            ("定位运行时显式配置入口", "Runtime/Systems/Resource/ResourceConfiguration.cs")
        };
        /// <summary>绘制入门流程、技术引用及本窗口报告入口，页面跳转不自动触发资源操作。</summary>
        internal void Draw(ResourceEditorBuildReport report, Action showBuild, Action<int> navigate = null)
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(0), GUILayout.ExpandHeight(true));
            GUILayout.Label("从一个资源项目开始", ResourceWorkbenchGui.Title);
            GUILayout.Label("一个资源项目可以包含多个组和多个来源目录。首次使用可选择 None（不加密），无需先创建构建配方。", ResourceWorkbenchGui.Secondary);
            ResourceWorkbenchGui.Metric("1. 选择或创建项目", "使用顶部项目选择器。已有项目直接选择，新项目在选定位置创建配置。", "ResourceProject 是资源配置，不是 Unity 工程；不会接管已有输出。", 156);
            ResourceWorkbenchGui.Metric("2. 登记来源目录", "在项目设置登记已有目录及各自的地址前缀，无需搬动资源；同时核对项目标识。", "目录使用 Unity GUID 定位，地址前缀需明确填写。旧资源根按空前缀继续使用；来源目录不是磁盘缓存根。", 156);
            ResourceWorkbenchGui.Metric("3. 添加组与资源", "在资源配置页创建组，再添加或拖入文件、目录。登记来源目录本身不会选择其中的资源。", "Group 的选择项决定构建内容和打包方式；最终 Bundle 还可能因场景、共享依赖或循环依赖调整。", 156);
            ResourceWorkbenchGui.Metric("4. 保存配置", "点击顶部保存项目；图集规则或配方使用各自的保存入口。执行前也需处理未保存场景，切页不会自动保存。", "保存项目不会连带保存场景、其他项目或独立配置资产。", 156);
            ResourceWorkbenchGui.Metric("5. 构建并本地发布", "在构建页确认平台与应用兼容标识，再执行构建并本地发布。", "无需构建配方；None 不需要内容密钥。应用兼容标识由应用约定，不是游戏版本号。", 156);
            ResourceWorkbenchGui.Metric("6. 查看交付版本", "在交付页读取已发布版本，核对本次版本身份与结果。", "本地发布不是签发、远程部署或运行时激活。", 156);
            if (navigate != null)
            {
                GUILayout.Space(8);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("前往资源配置", ResourceWorkbenchGui.Button))
                {
                    navigate(0);
                }

                if (ResourceWorkbenchGui.PrimaryAction("前往构建发布"))
                {
                    navigate(2);
                }

                if (GUILayout.Button("查看发布版本", ResourceWorkbenchGui.Button))
                {
                    navigate(3);
                }

                EditorGUILayout.EndHorizontal();
                GUILayout.Label("这些入口只切换页面，不会自动保存、构建或发布。", ResourceWorkbenchGui.Secondary);
            }
            GUILayout.Space(8);
            GUILayout.Label("按需使用", ResourceWorkbenchGui.Title);
            GUILayout.Label("需要时再使用图集、分包分析、地址常量、构建配方、签发、部署或诊断，不把它们作为入门前置。", ResourceWorkbenchGui.Secondary);
            Link(References[0].Label, References[0].Path);
            if (report != null)
            {
                _showReport = ResourceWorkbenchGui.Disclosure(_showReport, "本窗口构建报告");
                if (_showReport)
                {
                    ResourceWorkbenchGui.Metric("操作", ResourceEditorText.Operation(report.Operation), "本窗口保留的执行记录；重载后不从磁盘恢复临时报告。");
                    ResourceWorkbenchGui.Metric("资源项目", report.ProjectId, "ProjectId：本次执行使用的项目标识。");
                    ResourceWorkbenchGui.Metric("开始时间（UTC）", report.StartedAtUtc.ToString("u"), "StartedAtUtc：本次执行开始时间。");
                    ResourceWorkbenchGui.Metric("目标平台", report.Platform, "Platform：本次执行使用的平台。");
                    ResourceWorkbenchGui.Metric("应用兼容标识", report.AppContentCompatibilityId, "AppContentCompatibilityId：本次执行使用的资源兼容标识。");
                    if (GUILayout.Button("定位此窗口构建报告", ResourceWorkbenchGui.Button))
                    {
                        showBuild?.Invoke();
                    }
                }
            }
            _showTechnical = ResourceWorkbenchGui.Disclosure(_showTechnical, "技术入口");
            if (_showTechnical)
            {
                GUILayout.Label("以下入口只打开文档或定位源码，不运行样例、不创建资源、不填写游戏配置，也不执行构建、签发或部署。", ResourceWorkbenchGui.Secondary);
                GUILayout.Label(BatchExampleHelp, ResourceWorkbenchGui.Secondary);
                for (var i = 1; i < References.Length; i++)
                {
                    Link(References[i].Label, References[i].Path);
                }

                if (report != null)
                {
                    GUILayout.Label(report.Invocation?.ToString() ?? "ProjectGuid=" + report.ProjectGuid + " / Platform=" + report.Platform + " / App=" + report.AppContentCompatibilityId, ResourceWorkbenchGui.Secondary);
                }
            }
            if (_error != null)
            {
                EditorGUILayout.HelpBox(_error, MessageType.Error);
            }

            EditorGUILayout.EndScrollView();
        }
        /// <summary>仅在指定文件真实存在时定位并打开它，缺失时报告原路径而不猜替代位置。</summary>
        private void Link(string label, string path)
        {
            if (!GUILayout.Button(new GUIContent(label, path), ResourceWorkbenchGui.Button))
            {
                return;
            }

            if (path.StartsWith("Samples~/", StringComparison.Ordinal))
            {
                UnityEditor.PackageManager.UI.Window.Open("com.kano.ylframework");
                _error = null;
                return;
            }
            var fullPath = global::YLFramework.Editor.Common.FrameworkPackagePaths.FullPath(typeof(ResourceGuideView).Assembly, path);
            if (!File.Exists(fullPath))
            {
                _error = "参考文件不可用：" + path + "；未查找未知替代位置。";
                return;
            }
            if (path.StartsWith("Documentation~/", StringComparison.Ordinal))
            {
                Application.OpenURL(new Uri(fullPath).AbsoluteUri);
            }
            else
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(global::YLFramework.Editor.Common.FrameworkPackagePaths.AssetPath(typeof(ResourceGuideView).Assembly, path));
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
                AssetDatabase.OpenAsset(asset);
            }
            _error = null;
        }
    }
}
