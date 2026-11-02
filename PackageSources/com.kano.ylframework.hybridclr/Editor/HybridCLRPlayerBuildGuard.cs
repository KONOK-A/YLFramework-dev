using System;
using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace YLFramework.Integrations.HybridCLR.Editor
{
    /// <summary>只校验本管线的显式上下文；无 profile 的普通构建不安装、不生成、不改设置。</summary>
    public sealed class HybridCLRPlayerBuildGuard : IPreprocessBuildWithReport
    {
        private static HybridCLRBuildProfile _profile;
        private static bool _isActive;
        private static bool _scriptsOnly;
        private static string _fingerprint;
        /// <summary>在较早的预构建回调中检查本管线显式上下文。</summary>
        public int callbackOrder => -1000;

        // 建立唯一的剥离或正式 Player 构建作用域，并固定进入时的全局设置指纹。
        internal static IDisposable Enter(HybridCLRBuildProfile selected, bool strip)
        {
            if (_isActive)
            {
                throw new InvalidOperationException("Nested HybridCLR Player builds are not supported.");
            }

            _fingerprint = HybridCLRBuildPipeline.SettingsFingerprint(selected);
            _profile = selected;
            _scriptsOnly = strip;
            _isActive = true;
            return new Scope();
        }

        /// <summary>仅在显式作用域内复核环境、目标、构建模式与设置指纹，普通构建无操作。</summary>
        /// <param name="report">Unity 当前构建报告；无显式配置时不读取。</param>
        public void OnPreprocessBuild(BuildReport report)
        {
            if (!_isActive)
            {
                return;
            }
            if (!_profile)
            {
                throw new BuildFailedException("The active HybridCLR build profile was unloaded; this is not an ordinary unscoped build.");
            }

            HybridCLRBuildPipeline.ValidateEnvironment(_profile);
#if UNITY_EDITOR_WIN
            if (!_scriptsOnly)
            {
                HybridCLRBuildPipeline.ValidateExecutableBuildMode(UnityEditor.EditorUserBuildSettings.buildScriptsOnly,
                UnityEditor.WindowsStandalone.UserBuildSettings.createSolution);
            }
#endif
            if (report.summary.platform != _profile.recipe.target ||
                ((report.summary.options & UnityEditor.BuildOptions.Development) != 0) != _profile.development ||
                UnityEditor.EditorUserBuildSettings.buildScriptsOnly != _scriptsOnly ||
                _fingerprint != HybridCLRBuildPipeline.SettingsFingerprint(_profile))
            {
                throw new BuildFailedException("The explicit HybridCLR Player build context or native settings changed.");
            }
        }

        /// <summary>退出后清除本管线构建上下文，不修改外部 Player 设置。</summary>
        private sealed class Scope : IDisposable
        {
            private bool _isDisposed;
            /// <summary>释放显式上下文，后续普通构建不再受本次配置约束。</summary>
            public void Dispose()
            {
                if (_isDisposed)
                {
                    return;
                }
                _isDisposed = true;
                _profile = null;
                _fingerprint = null;
                _scriptsOnly = false;
                _isActive = false;
            }
        }
    }
}
