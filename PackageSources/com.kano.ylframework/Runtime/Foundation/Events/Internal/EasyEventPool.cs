using YLFramework.Pooling;
using YLFramework.Singletons;

namespace YLFramework.Events.Internal
{
    /// <summary>
    /// EasyEvent 系列共用的对象池入口（内部实现，不对外暴露）。
    /// 池实例不存在（首次使用 / Session 重置 / 手动 Dispose 后）时按需初始化配置，
    /// 避免静态状态跨会话残留导致池按默认容量运行。
    /// </summary>
    internal static class EasyEventPool<TEvent> where TEvent : class, IOfPool, new()
    {
        /// <summary>
        /// 从池中分配事件实例，必要时先初始化池。
        /// </summary>
        /// <param name="prewarmCount">池初始化时的预热数量。</param>
        public static TEvent Allocate(int prewarmCount)
        {
            EnsureInitialized(prewarmCount);
            return SafeObjectPool<TEvent>.Instance.Allocate();
        }

        /// <summary>
        /// 归还由当前类型池取得的事件。外部 new 与旧池借用不被新池接纳，
        /// 旧持有者应归还原 Safe 池；只实现接口不意味着实例事件自动池化。
        /// </summary>
        /// <param name="obj">待回收的事件实例。</param>
        /// <param name="prewarmCount">池初始化时的预热数量。</param>
        public static void Recycle(TEvent obj, int prewarmCount)
        {
            EnsureInitialized(prewarmCount);
            SafeObjectPool<TEvent>.Instance.Recycle(obj);
        }

        /// <summary>仅在当前类型的池入口不存在时配置容量和预热数量，避免覆盖存活池的状态。</summary>
        private static void EnsureInitialized(int prewarmCount)
        {
            if (!SingletonProperty<SafeObjectPool<TEvent>>.HasInstance)
            {
                SafeObjectPool<TEvent>.Instance.Init(50, prewarmCount);
            }
        }
    }
}
