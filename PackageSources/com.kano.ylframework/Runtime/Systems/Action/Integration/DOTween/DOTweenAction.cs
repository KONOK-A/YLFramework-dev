using System;
using System.Threading;
using DG.Tweening;
using YLFramework.Actions;

namespace YLFramework.Integrations.DOTween
{
    /// <summary>把一个独占持有的 Tween 桥接到 ActionKit 动作生命周期。</summary>
    /// <remarks>
    /// 绑定或启动必须使用 Unity 主线程及原始 UnitySynchronizationContext；Tween 由 DOTween 驱动，不由动作时间增量推进。
    /// Tween 正常完成才算动作完成；未完成的 Kill(false) 或归属标记丢失会取消动作。
    /// 取消不触发正常完成；仅向 Unity 上下文延迟投递 Kill(false)，不保证立即终止或终止已失去归属的外部 Tween。
    /// </remarks>
    public class DOTweenAction : IAction, IActionExecutionVersion
    {
        private static readonly ActionPool<DOTweenAction> _pool = new ActionPool<DOTweenAction>(() => new DOTweenAction(), null, 10);
        private Func<Tween> _tweenGetter;
        private TweenLease _lease;
        private ulong _executionVersion;
        private bool _isPaused;
        private bool _isBound;
        private bool _hasBoundStarted;
        private static int _mainThread;
        ulong IActionExecutionVersion.ExecutionVersion => _executionVersion;

        // 记录当前运行会话的 Unity 主线程标识。
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void CaptureMainThread() => _mainThread = Thread.CurrentThread.ManagedThreadId;
#if UNITY_EDITOR
        // 在编辑器加载时补充尚未进入运行会话的线程标识。
        [UnityEditor.InitializeOnLoadMethod]
        private static void CaptureEditorThread() => CaptureMainThread();
#endif

        // 校验当前线程及 Unity 上下文类型，返回延迟清理所需的上下文。
        private static SynchronizationContext RequireContext()
        {
            var context = SynchronizationContext.Current;
            var type = context?.GetType();
            if (Thread.CurrentThread.ManagedThreadId != _mainThread || type == null ||
                type.FullName != "UnityEngine.UnitySynchronizationContext" ||
                type.Assembly != typeof(UnityEngine.Object).Assembly || !type.IsSealed)
            {
                throw new InvalidOperationException("DOTween actions require the Unity main thread and its original UnitySynchronizationContext.");
            }

            return context;
        }

        // 仅供动作池创建实例，调用方通过分配入口取得使用权。
        private DOTweenAction()
        {
        }

        /// <inheritdoc cref="OnFinish" />
        /// <summary>分配动作，启动时才调用 Tween 工厂。</summary>
        /// <param name="tweenGetter">每次启动取得新的独立根 Tween 的非空工厂；动作接管本次执行与取消，不复用已 Kill 的实例。</param>
        /// <returns>尚未启动的池化动作；释放后不得继续使用原引用。</returns>
        public static DOTweenAction Allocate(Func<Tween> tweenGetter)
        {
            if (tweenGetter == null)
            {
                throw new ArgumentNullException(nameof(tweenGetter));
            }

            var action = _pool.Allocate();
            action.ActionId = ActionKit.IdGenerator++;
            action.IsDeinitialized = false;
            action.Reset();
            action._isBound = false;
            action._hasBoundStarted = false;
            action._tweenGetter = tweenGetter;
            return action;
        }

        /// <inheritdoc cref="OnFinish" />
        /// <summary>读取或设置暂停标记，只恢复由此动作暂停的 Tween，不接管外部暂停。</summary>
        public bool IsPaused
        {
            get => _isPaused;
            set
            {
                _isPaused = value;
                _lease?.SetPaused(value);
            }
        }
        /// <inheritdoc cref="OnFinish" />
        /// <summary>读取或设置动作已释放的标记。</summary>
        public bool IsDeinitialized
        {
            get; set;
        }
        /// <inheritdoc cref="OnFinish" />
        /// <summary>读取或设置当前分配的动作标识，用于隔离旧 Tween 回调。</summary>
        public ulong ActionId
        {
            get; set;
        }
        /// <inheritdoc cref="OnFinish" />
        /// <summary>读取或设置动作执行状态。</summary>
        public ActionStatus Status
        {
            get; set;
        }

        /// <inheritdoc cref="OnFinish" />
        /// <summary>立即恢复 NotStart 并保留 getter；首次启动前保留原预绑定，启动后 Reset 再 Start 会取消旧绑定，物理 Kill 通过 Post 延后。</summary>
        public void Reset()
        {
            _executionVersion++;
            Status = ActionStatus.NotStart;
            _isPaused = false;
            // 组合动作在首次启动前会重置子动作；保留原绑定，不重新占用 Tween。
            if (_isBound && !_hasBoundStarted && _lease != null)
            {
                _lease.RebaseVersion(_executionVersion);
                return;
            }
            Release(true);
        }

        /// <inheritdoc cref="OnFinish" />
        /// <summary>立即标记 IsDeinitialized、清空 getter 并排队回收，不触发正常完成；物理 Kill 通过 Post 延后。</summary>
        public void Deinit()
        {
            if (IsDeinitialized)
            {
                return;
            }

            IsDeinitialized = true;
            _tweenGetter = null;
            try
            {
                Release(true);
            }
            finally
            {
                ActionQueue.AddCallback(new ActionQueueRecycleCallback<DOTweenAction>(_pool, this));
            }
        }

        // 断开动作与当前租约的关联，并按终止方式关闭租约。
        private void Release(bool kill)
        {
            var lease = _lease;
            _lease = null;
            lease?.Close(kill);
        }

        /// <inheritdoc cref="OnFinish" />
        /// <summary>首先校验真实 Unity 主线程与原始上下文，再调用工厂或只复用预绑定的原生命周期并从头播放。</summary>
        public void OnStart()
        {
            var context = RequireContext();
            if (_isBound)
            {
                var bound = _lease;
                if (bound == null || !IsCurrent(bound))
                {
                    Deinit();
                    return;
                }
                _hasBoundStarted = true;
                try
                {
                    bound.Restart();
                    if (IsCurrent(bound))
                    {
                        bound.SetPaused(_isPaused);
                    }
                }
                catch (Exception original)
                {
                    if (IsCurrent(bound))
                    {
                        try
                        {
                            Deinit();
                        }
                        catch (Exception cleanup)
                        {
                            throw new AggregateException(original, cleanup);
                        }
                    }
                    throw;
                }
                return;
            }
            var lease = new TweenLease(this, context);
            _lease = lease;
            try
            {
                // 每次执行只持有工厂返回的一个 Tween；Reset 后应为新执行提供新 Tween。
                lease.Attach(_tweenGetter());
                if (!IsCurrent(lease))
                {
                    return;
                }

                lease.Restart();
                if (IsCurrent(lease))
                {
                    lease.SetPaused(_isPaused);
                }
            }
            catch (Exception original)
            {
                if (IsCurrent(lease))
                {
                    try
                    {
                        Deinit();
                    }
                    catch (Exception cleanup)
                    {
                        throw new AggregateException(original, cleanup);
                    }
                }
                throw;
            }
        }

        /// <inheritdoc cref="OnFinish" />
        /// <summary>检查 Tween 的完成或取消状态，不用 dt 推进播放。</summary>
        /// <param name="dt">执行器提供的秒数增量，此桥接不使用该值。</param>
        public void OnExecute(float dt) => _lease?.Poll();
        /// <summary>正常完成时只解除自身 hooks，不 Kill Tween，保留 getter 到 Deinit。</summary>
        /// <remarks>
        /// 仅接管独立根 Tween 或 DG.Tweening.Sequence 本身，不支持内部子 Tween。DOTween 推进时钟，IgnoreTimeScale 不自动改变 Tween clock。
        /// 绑定和启动要求真实主线程及原始 UnitySynchronizationContext，核对完整类型名、程序集身份、sealed 与主线程 ID；不支持自定义同步 Post 或人工 pump，错误上下文不调用 factory、不 Pause Tween。
        /// ToAction 在 Pause 前预绑定同一生命周期；首次启动前 Reset 保留原绑定，启动后 Reset 不重新认领。外部 Pause 只是等待，Resume 只恢复动作自己发起的暂停。
        /// Deinit 立即生效，Reset 立即恢复 NotStart；物理 Kill(false) 通过 Post 延后，pending marker 阻止重认领，回调在实际主线程复核 ownership 后才清理，不能干扰其他 owner。
        /// Shutdown 不保证同步物理 Kill；Domain Reload 或 Post 未执行时可能没有物理清理。ownership 无法证实时不保证物理清理，不对外部 source owner 承诺终止。
        /// onComplete/onKill 是 multicast，即使 safeMode 也不能承诺逐 callback 异常隔离；适配不能完全区分先完成与先 Kill 的所有外部改写，前序 delegate 抛错可能漏掉完成或取消信号。
        /// </remarks>
        public void OnFinish() => Release(false);

        // 同时核对分配标识、执行版本和租约引用，隔离重入及池复用后的旧通知。
        private bool IsCurrent(TweenLease lease) => !IsDeinitialized && ActionId == lease.ActionId &&
            _executionVersion == lease.Version && ReferenceEquals(_lease, lease);

        // 为已有 Tween 建立首次执行的持有关系，并立即暂停它。
        internal static DOTweenAction Bind(Tween tween)
        {
            var context = RequireContext();
            ValidateTween(tween);
            var action = Allocate(() => tween);
            action._isBound = true;
            var lease = new TweenLease(action, context);
            action._lease = lease;
            try
            {
                lease.Attach(tween);
                tween.Pause();
            }
            catch
            {
                action.Deinit();
                throw;
            }
            return action;
        }

        // 拒绝空、已终止或仍被其他动作租约占用的 Tween。
        internal static void ValidateTween(Tween tween)
        {
            if (tween == null)
            {
                throw new ArgumentNullException(nameof(tween));
            }

            if (!tween.IsActive())
            {
                throw new InvalidOperationException("A killed tween cannot be wrapped; create a new tween in the factory.");
            }

            if (tween.onKill == null)
            {
                return;
            }

            foreach (var callback in tween.onKill.GetInvocationList())
            {
                if (callback.Target is TweenLease)
                {
                    throw new InvalidOperationException("The tween is already owned by an active ActionKit execution.");
                }
            }
        }

        /// <summary>保存一次动作执行与 Tween 的持有标记、通知和延迟清理上下文。</summary>
        private sealed class TweenLease
        {
            internal readonly ulong ActionId;
            internal ulong Version
            {
                get; private set;
            }
            internal bool IsClosed;
            private DOTweenAction _target;
            private Tween _tween;
            private readonly TweenCallback _completedCallback;
            private readonly TweenCallback _killedCallback;
            private TweenCallback _verifiedKillCallback;
            private bool _isCompleted;
            private bool _isKilled;
            private bool _isPausedByAction;
            private readonly SynchronizationContext _context;

            // 捕获本次分配及执行版本，建立只通知该次执行的回调。
            internal TweenLease(DOTweenAction target, SynchronizationContext context)
            {
                _target = target;
                ActionId = target.ActionId;
                Version = target._executionVersion;
                _completedCallback = Completed;
                _killedCallback = Killed;
                _context = context;
            }

            // 首次启动前重置组合动作时，将原绑定同步到新的执行版本。
            internal void RebaseVersion(ulong version) => Version = version;

            // 附加自身回调；工厂返回前若已关闭租约，则接收后安排清理而不启动。
            internal void Attach(Tween tween)
            {
                ValidateTween(tween);
                _tween = tween;
                tween.onComplete += _completedCallback;
                tween.onKill += _killedCallback;
                if (IsClosed)
                {
                    _tween = null;
                    ScheduleKill(tween);
                }
            }

            // 以自身 onKill 委托确认当前活跃 Tween 仍保留本租约的持有标记。
            private bool HasMarker(Tween tween)
            {
                if (tween == null || !tween.IsActive() || tween.onKill == null)
                {
                    return false;
                }
                // 缓存已验证的委托引用，避免每帧分配调用列表。
                if (ReferenceEquals(tween.onKill, _verifiedKillCallback))
                {
                    return true;
                }

                if (Array.IndexOf(tween.onKill.GetInvocationList(), _killedCallback) < 0)
                {
                    return false;
                }

                _verifiedKillCallback = tween.onKill;
                return true;
            }
            // 只有未关闭且持有标记仍有效的租约可以操作 Tween。
            private bool Owns(Tween tween) => !IsClosed && HasMarker(tween);

            // 从头播放仍持有的 Tween，并复核同步回调后的终态。
            internal void Restart()
            {
                if (Owns(_tween))
                {
                    _isCompleted = false;
                    _tween.Restart();
                }
                Poll();
            }

            // 只转发自身可负责的暂停与恢复，不撤销外部暂停。
            internal void SetPaused(bool paused)
            {
                var tween = _tween;
                if (!Owns(tween))
                {
                    return;
                }

                if (paused)
                {
                    if (!tween.IsPlaying())
                    {
                        return;
                    }

                    _isPausedByAction = true;
                    tween.Pause();
                }
                else if (_isPausedByAction)
                {
                    _isPausedByAction = false;
                    tween.Play();
                }
            }

            // 记录正常完成通知并尝试交付给当前动作执行。
            private void Completed()
            {
                if (IsClosed)
                {
                    return;
                }

                _isCompleted = true;
                Deliver();
            }

            // 在终止回调中保留可观察的完成结果，再解除 Tween 引用并交付终态。
            private void Killed()
            {
                if (IsClosed)
                {
                    return;
                }

                var tween = _tween;
                // DOTween 1.2.825 在仍活跃时调用 onKill，随后重置完成状态与回调字段。
                if (Owns(tween) && tween.IsComplete())
                {
                    _isCompleted = true;
                }

                _tween = null;
                _isKilled = true;
                Deliver();
            }

            // 只向仍匹配的执行交付完成或取消，预绑定尚未启动时不交付成功。
            private void Deliver()
            {
                var target = _target;
                if (target == null || !target.IsCurrent(this))
                {
                    return;
                }

                if (target._isBound && !target._hasBoundStarted)
                {
                    if (_isKilled)
                    {
                        target.Deinit();
                    }

                    return;
                }
                if (_isCompleted)
                {
                    target.Finish();
                }
                else if (_isKilled)
                {
                    target.Deinit();
                }
            }

            // 在回调被覆盖等情况下补查完成状态与持有标记。
            internal void Poll()
            {
                if (IsClosed)
                {
                    return;
                }

                if (!_isCompleted && !_isKilled)
                {
                    var tween = _tween;
                    if (!Owns(tween))
                    {
                        _isKilled = true;
                    }
                    else if (tween.IsComplete())
                    {
                        _isCompleted = true;
                    }
                }
                Deliver();
            }

            // 关闭通知通道，对仍持有的 Tween 选择延迟终止或仅退订自身回调。
            internal void Close(bool kill)
            {
                if (IsClosed)
                {
                    return;
                }

                var tween = _tween;
                var owned = Owns(tween);
                IsClosed = true;
                _target = null;
                _tween = null;
                if (!owned)
                {
                    return; // 归属标记丢失时，对象可能已被回收并交给其他持有者。
                }

                if (kill)
                {
                    ScheduleKill(tween);
                }
                else
                {
                    tween.onComplete -= _completedCallback;
                    tween.onKill -= _killedCallback;
                }
            }

            // 投递带归属复核的清理，避免误终止已被 DOTween 回收复用的对象。
            private void ScheduleKill(Tween tween)
            {
                // 保留占用标记，等 Unity 离开当前 DOTween 回调栈后再清理。
                _context.Post(_ =>
                {
                    if (Thread.CurrentThread.ManagedThreadId != _mainThread || !HasMarker(tween))
                    {
                        return;
                    }

                    foreach (var callback in tween.onKill.GetInvocationList())
                    {
                        if (callback.Target is TweenLease other && !ReferenceEquals(other, this))
                        {
                            return;
                        }
                    }

                    tween.onComplete -= _completedCallback;
                    tween.onKill -= _killedCallback;
                    tween.Kill(false);
                }, null);
            }
        }
    }

    /// <summary>DOTween 与 ActionKit 动作的桥接扩展。</summary>
    public static class ActionKitDOTweenExtension
    {
        /// <inheritdoc cref="DOTweenAction.OnFinish" />
        /// <summary>在 Pause 前预绑定已有独立根 Tween，动作首次启动时从头播放。</summary>
        /// <param name="self">有效且未被其他动作持有的 Tween；首次启动后 Reset 后不可重新认领原生命周期。</param>
        /// <returns>尚未启动的单次绑定动作，首次启动后 Reset 后再 Start 取消，不重新认领原 Tween。</returns>
        public static IAction ToAction(this Tween self)
        {
            return DOTweenAction.Bind(self);
        }

        /// <inheritdoc cref="DOTweenAction.OnFinish" />
        /// <summary>将启动时创建 Tween 的动作追加到序列，并返回原序列。</summary>
        /// <param name="self">接收动作的非空序列。</param>
        /// <param name="tweenGetter">每次执行时创建新的独占根 Tween 的非空工厂；播放时间模式仍由 DOTween 控制。</param>
        /// <returns>追加动作后的原序列。</returns>
        public static ISequence DOTween(this ISequence self, Func<Tween> tweenGetter)
        {
            if (self == null)
            {
                throw new ArgumentNullException(nameof(self));
            }

            return self.Append(DOTweenAction.Allocate(tweenGetter));
        }
    }
}
