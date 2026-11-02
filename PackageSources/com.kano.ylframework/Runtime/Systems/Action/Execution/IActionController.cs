namespace YLFramework.Actions
{
    /// <summary>控制一次动作执行的进度和更新时间源，通常由 Start 返回。</summary>
    /// <remarks>完成或取消后由执行器回收，调用方不应继续操作旧控制器引用。</remarks>
    public interface IActionController
    {
        /// <summary>控制器绑定的动作分配标识，必须与 Action 匹配。</summary>
        ulong ActionId
        {
            get; set;
        }
        /// <summary>本次执行绑定的动作。</summary>
        IAction Action
        {
            get; set;
        }
        /// <summary>后续更新使用的缩放或非缩放时间增量。</summary>
        ActionUpdateModes UpdateMode
        {
            get; set;
        }
        /// <summary>是否暂停当前匹配动作的更新。</summary>
        bool IsPaused
        {
            get; set;
        }
        /// <summary>重置仍匹配的动作进度，不重新调度或分配动作。</summary>
        void Reset();
        /// <summary>清理仍匹配的动作，不触发正常完成通知；不立即回收控制器。</summary>
        void Deinit();
        /// <summary>将控制器归还对象池；由执行器管理，调用后旧引用失效。</summary>
        void Recycle();
    }

    /// <summary>使用对象池的标准动作控制器，保存本次执行的动作绑定。</summary>
    public class ActionController : IActionController
    {
        private static readonly ActionPool<IActionController> _pool = new ActionPool<IActionController>(
            () => new ActionController(), controller =>
            {
                controller.UpdateMode = ActionUpdateModes.ScaledDeltaTime;
                controller.ActionId = 0;
                controller.Action = null;
            }, 50);

        /// <inheritdoc />
        public ulong ActionId
        {
            get; set;
        }
        /// <inheritdoc />
        public IAction Action
        {
            get; set;
        }
        /// <inheritdoc />
        public ActionUpdateModes UpdateMode
        {
            get; set;
        }
        private bool _isRecycled;
        // 每次从池分配时递增：同一控制器对象被复用时，旧租约里的代次不再匹配。
        internal uint Generation
        {
            get; private set;
        }
        // 有效绑定判定：控制器未回收、动作非空且动作标识与控制器记录一致。
        private bool HasCurrentAction => !_isRecycled && Action != null && Action.ActionId == ActionId;

        /// <inheritdoc />
        public bool IsPaused
        {
            get => HasCurrentAction && Action.IsPaused;
            set
            {
                if (HasCurrentAction)
                {
                    Action.IsPaused = value;
                }
            }
        }

        /// <inheritdoc />
        public void Reset()
        {
            if (HasCurrentAction)
            {
                Action.Reset();
            }
        }

        /// <summary>取得新使用周期的控制器，调用方或执行器需设置 Action 和 ActionId。</summary>
        /// <returns>尚未绑定动作的池控制器。</returns>
        public static IActionController Allocate()
        {
            var controller = (ActionController)_pool.Allocate();
            controller._isRecycled = false;
            controller.Generation++;
            return controller;
        }

        /// <inheritdoc />
        public void Deinit()
        {
            if (HasCurrentAction)
            {
                Action.Deinit();
            }
        }

        /// <inheritdoc />
        public void Recycle()
        {
            if (_isRecycled)
            {
                return;
            }

            _isRecycled = true;
            _pool.Recycle(this);
        }
    }

    /// <summary>动作控制器的更新时间设置扩展。</summary>
    public static class IActionControllerExtensions
    {
        /// <summary>将后续更新切换为非缩放时间，不改变动作的更新频率。</summary>
        /// <param name="self">当前执行的控制器。</param>
        /// <returns>原控制器，便于链式设置。</returns>
        public static IActionController IgnoreTimeScale(this IActionController self)
        {
            self.UpdateMode = ActionUpdateModes.UnscaledDeltaTime;
            return self;
        }
    }
}
