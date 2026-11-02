#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;


namespace YLFramework.Foundation
{
    /// <summary>
    /// 为 Runtime 调用方提供兼容 Editor 与 Player 的运行状态查询。
    /// </summary>
    public class ApplicationTool
    {

        /// <summary>
        /// 在 Editor 中返回 EditorApplication.isPlayingOrWillChangePlaymode；在 Player 中始终返回 true。
        /// </summary>
        /// <remarks>
        /// 用于退出 Play 时的销毁守卫；它也包含即将进入 Play 的状态，不等同于 Application.isPlaying。
        /// </remarks>
        public static bool IsPlaying
        {
            get
            {
#if UNITY_EDITOR
                return EditorApplication.isPlayingOrWillChangePlaymode;  // 如果是编辑器模式 使用这个数据
#else
                return true; // 如果是非编辑器模式一定是true
#endif  
            }

        }

    }
}
