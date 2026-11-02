using System;

namespace YLFramework.Core
{
    /// <summary>Core 生命周期通知使用的固定事件键，发布与订阅均通过共享 EventBus.Default 路由。</summary>
    public class CoreEvents
    {
        /// <summary>
        /// 已加载 View 会话完成索引撤销及实例收尾后广播，参数签名为 Action&lt;View&gt;。
        /// 回调可复用同一组件，收到的引用不保证仍处于刚退出时的状态。
        /// </summary>
        public const string ViewRemoved = "ON_REMOVE_VIEW";
    }

}
