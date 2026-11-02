using UnityEditor;
using YLFramework.Buff;
using YLFramework.Logging;
using YLFramework.Mission;
using YLFramework.Skill;

namespace YLFramework.Editor.Domain
{
    /// <summary>编辑器领域系统的退出清理入口，在离开运行模式时收尾运行时状态，不创建宿主或保存资产。</summary>
    internal static class DomainSessionLifecycle
    {
        /// <summary>编辑器加载时去重安装运行模式监听，不主动启动领域系统。</summary>
        [InitializeOnLoadMethod]
        private static void Install()
        {
            EditorApplication.playModeStateChanged -= OnStateChanged;
            EditorApplication.playModeStateChanged += OnStateChanged;
        }

        /// <summary>仅响应退出运行模式，依次关闭技能、Buff、任务，最后关闭日志会话。</summary>
        private static void OnStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingPlayMode)
            {
                return;
            }

            // 逐层 finally 让前序关停抛错时后续系统仍能释放，日志保持到其他领域清理结束。
            try
            {
                try
                {
                    SkillManager.Shutdown();
                }
                finally
                {
                    try
                    {
                        BuffManager.Shutdown();
                    }
                    finally
                    {
                        MissionManager.Shutdown();
                    }
                }
            }
            finally
            {
                LoggerManager.Disable();
            }
        }
    }
}
