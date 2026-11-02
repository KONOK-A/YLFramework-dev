using UnityEngine;

namespace YLFramework.Extensions
{
    /// <summary>
    /// Behaviour 启停扩展。属于强制变更类方法，对 null / 已销毁对象不做 no-op 处理。
    /// </summary>
    public static class MonoBehaviourExtension
    {
        /// <summary>
        /// 设置 Behaviour 的 enabled 状态，默认启用，返回原对象以支持链式调用。
        /// </summary>
        /// <param name="selfBehaviour">目标 Behaviour。</param>
        /// <param name="enable">是否启用。</param>
        public static T Enable<T>(this T selfBehaviour, bool enable = true) where T : Behaviour
        {
            selfBehaviour.enabled = enable;
            return selfBehaviour;
        }

        /// <summary>
        /// 禁用 Behaviour，返回原对象以支持链式调用。
        /// </summary>
        /// <param name="selfBehaviour">目标 Behaviour。</param>
        public static T Disable<T>(this T selfBehaviour) where T : Behaviour
        {
            selfBehaviour.enabled = false;
            return selfBehaviour;
        }
    }
}
