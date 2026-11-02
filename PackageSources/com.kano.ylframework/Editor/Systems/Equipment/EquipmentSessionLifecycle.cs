using UnityEditor;
using UnityEngine;
using YLFramework.Equipment;

namespace YLFramework.Editor.Equipment
{
    /// <summary>在退出 PlayMode 或程序集重载前关闭装备管理器，补充 Editor 的生命周期收尾。</summary>
    [InitializeOnLoad]
    internal static class EquipmentSessionLifecycle
    {
        /// <summary>注册 PlayMode 状态变化与程序集重载前的关闭回调。</summary>
        static EquipmentSessionLifecycle()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode)
                {
                    Close();
                }
            };
            AssemblyReloadEvents.beforeAssemblyReload += Close;
        }
        /// <summary>请求装备管理器关闭，清理异常写入日志而不阻止编辑器生命周期继续。</summary>
        private static void Close()
        {
            try
            {
                EquipmentManager.Shutdown();
            }
            catch (System.Exception error)
            {
                Debug.LogException(error);
            }
        }
    }
}
