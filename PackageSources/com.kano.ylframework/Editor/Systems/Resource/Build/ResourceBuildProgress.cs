using System;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.Resource
{
    /// <summary>正式资源操作实际到达的同步观察边界，不代表成功比例。</summary>
    public enum ResourceBuildPhase
    {
        /// <summary>开始执行预检。</summary>
        PreflightStarted,
        /// <summary>开始准备图集。</summary>
        AtlasPreparationStarted,
        /// <summary>开始计算显式分包。</summary>
        MapStarted,
        /// <summary>开始收集输入指纹。</summary>
        FingerprintStarted,
        /// <summary>即将调用 Unity 同步构建。</summary>
        NativeBuildStarted,
        /// <summary>开始读取并验证磁盘产物。</summary>
        ReadbackStarted,
        /// <summary>开始生成或复用交付编码。</summary>
        ContentEncodingStarted,
        /// <summary>工作区已通过本次构建验证，不表示已发布。</summary>
        WorkspaceVerified,
        /// <summary>开始本地发布，不表示 Index 已提交。</summary>
        PublicationStarted,
        /// <summary>发布后开始生成资源地址常量。</summary>
        ReferenceGenerationStarted,
        /// <summary>本次执行结束，是否成功须读取对应报告。</summary>
        AttemptFinished
    }

    /// <summary>已实际到达的同步边界，不提供控制权、百分比、取消或测试故障注入。</summary>
    public sealed class ResourceBuildStage
    {
        /// <summary>实际到达的观察边界。</summary>
        public ResourceBuildPhase Phase
        {
            get;
        }
        /// <summary>该次操作的项目配置 GUID，未知时可为空。</summary>
        public string ProjectGuid
        {
            get;
        }
        /// <summary>该次操作的项目技术标识，未知时可为空。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>该次操作明确使用的平台名称。</summary>
        public string Platform
        {
            get;
        }
        /// <summary>创建此次通知的 UTC 时间。</summary>
        public DateTime AtUtc { get; } = DateTime.UtcNow;
        /// <summary>复制观察边界及其项目和平台身份。</summary>
        internal ResourceBuildStage(ResourceBuildPhase phase, string guid, string project, string platform)
        {
            Phase = phase;
            ProjectGuid = guid;
            ProjectId = project;
            Platform = platform;
        }
    }

    /// <summary>主线程只读观察。订阅者自行解绑；异常隔离，观察回调内执行重入明确拒绝。</summary>
    public static class ResourceBuildProgress
    {
        /// <summary>同步通知已到达的边界；订阅者负责解绑，其异常不会中止资源操作。</summary>
        public static event Action<ResourceBuildStage> StageReached;
        [ThreadStatic] private static bool _isObserving;
        /// <summary>为观察边界提供编辑器展示名称。</summary>
        internal static string PhaseName(ResourceBuildPhase value) => value switch
        {
            ResourceBuildPhase.PreflightStarted => "检查资源和输出",
            ResourceBuildPhase.AtlasPreparationStarted => "准备图集",
            ResourceBuildPhase.MapStarted => "计算分包",
            ResourceBuildPhase.FingerprintStarted => "计算输入指纹",
            ResourceBuildPhase.NativeBuildStarted => "调用 Unity 构建",
            ResourceBuildPhase.ReadbackStarted => "读回并验证构建结果",
            ResourceBuildPhase.ContentEncodingStarted => "处理交付编码",
            ResourceBuildPhase.WorkspaceVerified => "构建工作目录验证完成",
            ResourceBuildPhase.PublicationStarted => "本地发布",
            ResourceBuildPhase.ReferenceGenerationStarted => "生成资源地址常量",
            ResourceBuildPhase.AttemptFinished => "本次执行已结束",
            _ => "未知步骤（" + value + "）"
        };
        /// <summary>拒绝观察回调在当前线程内重入正式执行入口。</summary>
        internal static void RejectReentry()
        {
            if (_isObserving)
            {
                throw new InvalidOperationException("A Resource stage observer cannot reenter execution.");
            }
        }
        /// <summary>从当前项目提取身份并发出观察通知。</summary>
        internal static void Notify(ResourceBuildPhase phase, ResourceProject project, BuildTarget target)
        {
            if (StageReached == null || _isObserving)
            {
                return;
            }

            Notify(phase, project ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project)) : null,
                project ? project.projectId : null, target.ToString());
        }
        /// <summary>逐个调用订阅者并隔离异常，在回调期间阻止同线程执行重入。</summary>
        internal static void Notify(ResourceBuildPhase phase, string guid, string project, string platform)
        {
            var handlers = StageReached;
            if (handlers == null || _isObserving)
            {
                return;
            }

            var stage = new ResourceBuildStage(phase, guid, project, platform);
            _isObserving = true;
            try
            {
                foreach (Action<ResourceBuildStage> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(stage);
                    }
                    catch (Exception)
                    {
                        Debug.LogWarning("资源构建进度回调发生异常；构建流程将继续，并照常验证输入和输出。");
                    }
                }
            }
            finally
            {
                _isObserving = false;
            }
        }
    }
}
