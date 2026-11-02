using System;

namespace YLFramework.Actions
{
    /// <summary>
    /// 配置自定义动作的回调与数据，并由调用方主动标记完成。
    /// </summary>
    /// <typeparam name="TData">自定义数据的类型。</typeparam>
    public interface ICustomApi<TData>
    {
        /// <summary>
        /// 当前租约的数据；Reset 保留，Deinit 重置为默认值，不负责 Dispose。
        /// </summary>
        TData Data
        {
            get; set;
        }

        /// <summary>
        /// 设置启动回调，重复设置会替换先前回调。
        /// </summary>
        /// <param name="onStart">每次启动时执行的回调。</param>
        /// <returns>当前配置接口。</returns>
        ICustomApi<TData> OnStart(Action onStart);

        /// <summary>
        /// 设置接收本次 dt 的执行回调，重复设置会替换先前回调。
        /// </summary>
        /// <param name="onExecute">启动后未暂停执行时调用，参数为本次时间步长。</param>
        /// <returns>当前配置接口。</returns>
        ICustomApi<TData> OnExecute(Action<float> onExecute);

        /// <summary>
        /// 设置正常完成回调；取消或 Deinit 不触发，重复设置会替换先前回调。
        /// </summary>
        /// <param name="onFinish">执行流程检测到完成时调用的回调。</param>
        /// <returns>当前配置接口。</returns>
        ICustomApi<TData> OnFinish(Action onFinish);

        /// <summary>
        /// 仅标记完成，由执行流程调用结束回调，不立即回收。
        /// </summary>
        void Finish();
    }

    // 生命周期完全由配置回调和调用方 Finish 驱动：OnStart/OnExecute 转发给回调，是否完成由回调内部决定。
    internal class Custom<TData> : IAction, ICustomApi<TData>
    {
        /// <inheritdoc />
        public TData Data
        {
            get; set;
        }

        /// <summary>每次启动时同步调用的业务回调，重新配置会替换旧值。</summary>
        protected Action StartCallback;
        /// <summary>启动后未暂停执行时调用的业务回调，接收本次 dt。</summary>
        protected Action<float> ExecuteCallback;
        /// <summary>执行流程检测到正常完成时调用的业务回调，Deinit 不调用。</summary>
        protected Action FinishCallback;

        private static ActionPool<Custom<TData>> _pool =
            new ActionPool<Custom<TData>>(() => new Custom<TData>(), null, 10);

        /// <summary>供泛型池或派生动作创建实例，不在构造时执行配置回调。</summary>
        protected Custom()
        {
        }

        /// <summary>取得使用指定数据类型的空配置动作并分配新标识，等待调用方配置及启动。</summary>
        public static Custom<TData> Allocate()
        {
            var custom = _pool.Allocate();
            custom.ActionId = ActionKit.IdGenerator++;
            custom.IsDeinitialized = false;
            custom.Reset();
            return custom;
        }

        /// <inheritdoc />
        public bool IsPaused
        {
            get; set;
        }

        /// <summary>清空数据和全部回调并安排延迟回收，不对用户数据调用 Dispose。</summary>
        public virtual void Deinit()
        {
            if (!IsDeinitialized)
            {
                IsDeinitialized = true;
                StartCallback = null;
                ExecuteCallback = null;
                FinishCallback = null;
                Data = default;

                ActionQueue.AddCallback(new ActionQueueRecycleCallback<Custom<TData>>(_pool, this));
            }
        }

        /// <summary>重置执行阶段及暂停标记，保留数据和生命周期回调供下一轮使用。</summary>
        public void Reset()
        {
            IsPaused = false;
            Status = ActionStatus.NotStart;
        }

        /// <inheritdoc />
        public bool IsDeinitialized
        {
            get; set;
        }
        /// <inheritdoc />
        public ulong ActionId
        {
            get; set;
        }
        /// <inheritdoc />
        public ActionStatus Status
        {
            get; set;
        }

        /// <summary>同步调用已配置的启动回调；是否完成由业务自行标记。</summary>
        public void OnStart()
        {
            StartCallback?.Invoke();
        }

        /// <summary>将本次时间增量同步转交给业务执行回调。</summary>
        /// <param name="dt">本次时间增量，单位为秒。</param>
        public void OnExecute(float dt)
        {
            ExecuteCallback?.Invoke(dt);
        }

        /// <summary>同步调用已配置的正常完成回调，异常直接向外传播。</summary>
        public void OnFinish()
        {
            FinishCallback?.Invoke();
        }

        /// <inheritdoc />
        public ICustomApi<TData> OnStart(Action onStart)
        {
            StartCallback = onStart;
            return this;
        }

        /// <inheritdoc />
        public ICustomApi<TData> OnExecute(Action<float> onExecute)
        {
            ExecuteCallback = onExecute;
            return this;
        }

        /// <inheritdoc />
        public ICustomApi<TData> OnFinish(Action onFinish)
        {
            FinishCallback = onFinish;
            return this;
        }

        /// <inheritdoc />
        public void Finish()
        {
            Status = ActionStatus.Finished;
        }
    }

    // Custom 是 Custom<object> 的封闭派生类，需要独立池来分配和回收自身类型（基类泛型池只会创建 Custom<object>）。
    internal class Custom : Custom<object>
    {
        private static ActionPool<Custom> _pool =
            new ActionPool<Custom>(() => new Custom(), null, 10);

        /// <summary>供非泛型专用池创建实例，使分配与回收使用同一个派生类型池。</summary>
        protected Custom()
        {
        }

        /// <summary>从非泛型专用池取得使用 object 数据的动作，并分配新标识。</summary>
        public new static Custom Allocate()
        {
            var custom = _pool.Allocate();
            custom.ActionId = ActionKit.IdGenerator++;
            custom.IsDeinitialized = false;
            custom.Reset();
            return custom;
        }

        /// <summary>清空数据与回调，并将当前派生实例延迟归还非泛型专用池。</summary>
        public override void Deinit()
        {
            if (!IsDeinitialized)
            {
                IsDeinitialized = true;

                StartCallback = null;
                ExecuteCallback = null;
                FinishCallback = null;
                Data = default;

                ActionQueue.AddCallback(new ActionQueueRecycleCallback<Custom>(_pool, this));
            }
        }
    }

    /// <summary>
    /// 向组合追加带生命周期回调和用户数据的自定义动作。
    /// </summary>
    public static class CustomExtension
    {
        /// <summary>
        /// 立即配置并追加使用 object 数据的自定义动作，需要主动调用配置接口的 Finish 完成。
        /// </summary>
        /// <param name="self">要追加自定义动作的父组合。</param>
        /// <param name="onCustomSetting">追加前立即执行的配置回调。</param>
        /// <returns>父组合，可继续追加动作。</returns>
        public static ISequence Custom(this ISequence self, Action<ICustomApi<object>> onCustomSetting)
        {
            var custom = ActionKit.Custom(onCustomSetting);
            return self.Append(custom);
        }

        /// <summary>
        /// 立即配置并追加使用指定数据类型的自定义动作，需要主动调用配置接口的 Finish 完成。
        /// </summary>
        /// <typeparam name="TData">自定义数据的类型。</typeparam>
        /// <param name="self">要追加自定义动作的父组合。</param>
        /// <param name="onCustomSetting">追加前立即执行的配置回调。</param>
        /// <returns>父组合，可继续追加动作。</returns>
        public static ISequence Custom<TData>(this ISequence self, Action<ICustomApi<TData>> onCustomSetting)
        {
            var custom = ActionKit.Custom(onCustomSetting);
            return self.Append(custom);
        }
    }
}
