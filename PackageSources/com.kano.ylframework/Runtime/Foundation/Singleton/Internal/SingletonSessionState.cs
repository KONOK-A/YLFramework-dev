using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YLFramework.Singletons
{
    /// <summary>
    /// Singleton 模块的运行期会话状态。
    /// 每次进入 Play 时重置属于单次 Play Session 的静态缓存，
    /// 保证 Domain Reload disabled 时与会重新加载域的行为一致。
    /// </summary>
    internal static class SingletonSessionState
    {
        private static readonly List<Action> _sessionResets = new();
        private static bool _isApplicationQuitting;
        private static bool _isExecutionStopping;
        /// <summary>基础执行设施的会话身份；仅新 Play 会话初始化时更换，不代表资源系统的会话。</summary>
        internal static object ExecutionSession { get; private set; } = new();

        /// <summary>当前是否允许基础执行宿主启动或推进工作；不改变其他领域单例的创建契约。</summary>
        internal static bool CanRunExecution => Application.isPlaying &&
#if UNITY_EDITOR
            // 请求退出后 isPlaying 暂时仍为 true；不依赖 ExitingPlayMode 监听的注册顺序。
            EditorApplication.isPlayingOrWillChangePlaymode &&
#endif
            !_isExecutionStopping && !_isApplicationQuitting;

        /// <summary>关闭当前执行门禁，不更换 ExecutionSession；由新会话初始化在重置完成后重新开放。</summary>
        internal static void StopExecution() => _isExecutionStopping = true;

#if UNITY_EDITOR
        /// <summary>以先退订后订阅的方式安装 Play 状态监听，避免静态订阅跨会话累积。</summary>
        [InitializeOnLoadMethod]
        private static void InstallExecutionGate()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>在进入或退出 Play 的过渡期关闭执行门禁，阻止旧宿主继续工作。</summary>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.ExitingEditMode ||
                state == PlayModeStateChange.EnteredEditMode)
            {
                StopExecution();
            }
        }
#endif

        /// <summary>
        /// 是否处于退出流程中，退出阶段禁止再创建 MonoBehaviour 单例对象
        /// </summary>
        public static bool IsApplicationQuitting => _isApplicationQuitting;

        /// <summary>
        /// 注册每次进入 Play 时需要重置的会话状态
        /// </summary>
        /// <remarks>注册表跨会话保留；重置时同步依次调用，不隔离回调异常。</remarks>
        public static void RegisterSessionReset(Action reset)
        {
            if (reset == null)
            {
                return;
            }

            lock (_sessionResets)
            {
                _sessionResets.Add(reset);
            }
        }

        /// <summary>
        /// 标记应用进入退出流程
        /// </summary>
        public static void MarkApplicationQuitting()
        {
            _isApplicationQuitting = true;
            StopExecution();
        }

        /// <summary>关闭门禁并更换执行会话身份，清理已登记的静态状态后再允许新会话执行。</summary>
        /// <remarks>任一重置回调抛异常都会中断本次初始化，门禁保持关闭。</remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForNewSession()
        {
            StopExecution();
            ExecutionSession = new object();
            _isApplicationQuitting = false;
#if UNITY_EDITOR
            InstallExecutionGate();
#endif

            // 禁用 Domain Reload 时静态事件订阅会残留，先减后加保证只有一份。
            Application.quitting -= OnApplicationQuitting;
            Application.quitting += OnApplicationQuitting;

            lock (_sessionResets)
            {
                for (var i = 0; i < _sessionResets.Count; i++)
                {
                    _sessionResets[i]?.Invoke();
                }
            }
            _isExecutionStopping = false;
        }

        /// <summary>将 Unity 的退出通知转为全局退出标记并关闭执行门禁。</summary>
        private static void OnApplicationQuitting()
        {
            MarkApplicationQuitting();
        }
    }
}
