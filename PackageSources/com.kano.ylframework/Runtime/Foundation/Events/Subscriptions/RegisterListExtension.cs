namespace YLFramework.Events
{
    /// <summary>
    /// <see cref="IUnregisterList"/> 的扩展：批量收集与统一注销。
    /// </summary>
    public static class RegisterListExtension
    {
        /// <summary>
        /// 把一个注销句柄加入集合。
        /// </summary>
        /// <param name="self">要加入的注销句柄。</param>
        /// <param name="owner">目标集合。</param>
        public static void AddToUnregisterList(this IUnregister self, IUnregisterList owner)
        {
            owner.UnregisterList.Add(self);
        }

        /// <summary>
        /// 注销集合内全部句柄并清空集合。
        /// 采用“快照 + 先清空再执行”的方式：重复调用幂等，回调中增删句柄安全，
        /// 回调中新增的句柄留到下一次调用处理。
        /// 若某个句柄抛异常，异常立即向上传播，本次快照中剩余句柄不再注销；
        /// 集合已被清空，不会在下次调用时重复注销旧句柄。
        /// </summary>
        /// <param name="self">目标集合。</param>
        public static void UnregisterAll(this IUnregisterList self)
        {
            var list = self.UnregisterList;
            if (list.Count == 0)
            {
                return;
            }

            var snapshot = SubscriptionCleanup.DetachAll(list);
            foreach (var subscription in snapshot)
            {
                subscription?.Unregister();
            }
        }

        /// <summary>
        /// 尝试注销本轮全部句柄，保留失败项及回调中新加入项，最后统一报告异常。
        /// 与旧 UnregisterAll 的 fail-fast 行为不同，供必要所有权清理显式使用。
        /// </summary>
        public static void UnregisterAllRetainingFailures(this IUnregisterList self)
        {
            SubscriptionCleanup.ReleaseAll(self.UnregisterList, token => token?.Unregister());
        }
    }
}
