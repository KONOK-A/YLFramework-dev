using UnityEngine;
using YLFramework;
using YLFramework.Singletons;

namespace YLFramework.Actions
{
    // 每次 Update 处理动作的延迟回收，不承载通用全局服务。
    internal sealed class ActionRecycleDriver : MonoSingletonHost<ActionRecycleDriver>
    {
        /// <summary>将自建回收驱动器挂到框架 Action 模块根节点下。</summary>
        protected override Transform HostParent => YLFrameworkRoot.Instance.GetModuleRoot("Action");

        // 测试与会话清理入口：尝试处理待回收项，即使失败也清空单例缓存引用。
        internal static void ResetSession()
        {
            try
            {
                ActionQueue.Drain();
            }
            finally
            {
                ResetHostReference();
            }
        }

        // 播放中首次产生回收项时创建驱动器；关停期间或当前不允许创建宿主时跳过，等下一次回收请求再试。
        internal static void EnsureCreated()
        {
            if (HasInstance || ActionKit.IsShuttingDown || !CanCreateHost())
            {
                return;
            }

            _ = Instance;
        }

        /// <summary>处理本轮已排队的回收项，回收异常向 Unity 调用栈传播。</summary>
        private void Update()
        {
            ActionQueue.Drain();
        }

        // 模块级释放：销毁所有驱动器实例并清空缓存引用，不处理队列内容（Shutdown 会先 Drain）。
        internal static void ReleaseAll()
        {
            foreach (var instance in FindObjectsByType<ActionRecycleDriver>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID))
            {
                instance.ReleaseHost();
            }
            ResetHostReference();
        }

        /// <summary>标记宿主退出并处理回收队列，不再等待后续 Update。</summary>
        protected override void OnApplicationQuit()
        {
            base.OnApplicationQuit();
            ActionQueue.Drain();
        }

        /// <summary>当前驱动器释放时兜底处理队列，非当前实例不接管回收工作。</summary>
        protected override void OnHostRelease()
        {
            if (!IsCurrentHost)
            {
                return;
            }
            // 被替换或销毁的驱动器在释放钩子里兜底清空队列，避免回收项无人处理。
            ActionQueue.Drain();
        }
    }
}
