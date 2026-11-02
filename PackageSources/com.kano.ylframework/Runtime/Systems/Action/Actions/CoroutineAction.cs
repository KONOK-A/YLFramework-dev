using System;
using System.Collections;

namespace YLFramework.Actions
{
    // 把 Unity 协程包装为动作：在模块全局宿主上运行，协程自然结束时标记完成，取消或 Reset 时停止协程。
    internal class CoroutineAction : IAction
    {
        private static ActionPool<CoroutineAction> _pool =
            new ActionPool<CoroutineAction>(() => new CoroutineAction(), null, 10, action =>
            {
                action.ActionId = ActionKit.IdGenerator++;
                action.IsDeinitialized = false;
                try
                {
                    action.Reset();
                }
                catch
                {
                    action.IsDeinitialized = true;
                    action._coroutineGetter = null;
                    throw;
                }
            });

        private Func<IEnumerator> _coroutineGetter = null;
        private CompletionSlot _completion;
        private ActionGlobalHost _host;
        private UnityEngine.Coroutine _coroutine;

        // 完成与取消都通过独立的槽对象转发：槽先断开与动作的引用，晚到的协程完成就不会影响已复用或已取消的动作。
        private sealed class CompletionSlot
        {
            /// <summary>仍可接收完成通知的动作；断开后忽略迟到通知。</summary>
            public CoroutineAction Target;

            /// <summary>撤销宿主取消登记，断开完成槽并将仍绑定的动作标记为正常完成。</summary>
            public void Finish()
            {
                var target = Target;
                if (target == null)
                {
                    return;
                }

                if (target._host)
                {
                    target._host.UnRegisterCoroutineCancellation(Cancel);
                }

                Target = null;
                target._completion = null;
                target._host = null;
                target._coroutine = null;
                target.Status = ActionStatus.Finished;
            }

            /// <summary>取消仍绑定的动作，由其清理逻辑停止所属协程。</summary>
            public void Cancel() => Target?.Deinit();
        }

        /// <summary>仅供对象池创建协程动作，枚举器工厂在 Allocate 时绑定。</summary>
        private CoroutineAction()
        {
        }

        /// <summary>分配协程动作并保存工厂，不在分配时创建枚举器或启动协程。</summary>
        /// <param name="coroutineGetter">每次启动时取得枚举器的函数。</param>
        public static CoroutineAction Allocate(Func<IEnumerator> coroutineGetter)
        {
            var coroutineAction = _pool.Allocate();
            coroutineAction._coroutineGetter = coroutineGetter;
            return coroutineAction;
        }

        /// <inheritdoc />
        public bool IsPaused
        {
            get; set;
        }
        /// <summary>清除工厂并停止所属协程，安排延迟回收；停止失败时延迟丢弃动作。</summary>
        public void Deinit()
        {
            if (!IsDeinitialized)
            {
                IsDeinitialized = true;
                _coroutineGetter = null;
                var cleaned = false;
                try
                {
                    StopRunningCoroutine();
                    cleaned = true;
                }
                finally
                {
                    ActionQueue.AddCallback(new ActionQueueRecycleCallback<CoroutineAction>(_pool, this, discard: !cleaned));
                }
            }
        }

        /// <summary>停止旧协程并重置执行状态，保留工厂供下一轮启动。</summary>
        public void Reset()
        {
            StopRunningCoroutine();
            IsPaused = false;
            Status = ActionStatus.NotStart;
        }

        /// <summary>先断开旧完成回调，再停止本动作的协程，避免晚到的完成影响复用对象。</summary>
        private void StopRunningCoroutine()
        {
            if (_completion != null)
            {
                if (_host)
                {
                    _host.UnRegisterCoroutineCancellation(_completion.Cancel);
                }

                _completion.Target = null;
                _completion = null;
            }
            var host = _host;
            var coroutine = _coroutine;
            _host = null;
            _coroutine = null;
            if (host && coroutine != null)
            {
                host.StopCoroutine(coroutine);
            }
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
        /// <summary>取得全局宿主，登记本轮取消回调并启动工厂产生的协程。</summary>
        public void OnStart()
        {
            var completion = new CompletionSlot { Target = this };
            _completion = completion;
            var host = ActionGlobalHost.Instance;
            _host = host;
            try
            {
                if (!host)
                {
                    throw new InvalidOperationException("ActionKit coroutine host is unavailable.");
                }
                // 先登记取消再启动协程，宿主释放时才能停止尚未结束的协程并清理动作。
                host.RegisterCoroutineCancellation(completion.Cancel);
                var routine = _coroutineGetter();
                if (completion.Target == null)
                {
                    return;
                }

                var coroutine = host.StartActionCoroutine(routine, completion.Finish);
                if (completion.Target != null)
                {
                    _coroutine = coroutine;
                }
                else if (host && coroutine != null)
                {
                    // 首步执行可能已取消动作，此时 StartCoroutine 才返回句柄，仍需停止该协程。
                    host.StopCoroutine(coroutine);
                }
            }
            catch
            {
                completion.Target?.Deinit();
                throw;
            }
        }

        /// <summary>不主动推进枚举器，协程由 Unity 在全局宿主上推进。</summary>
        /// <param name="dt">本次时间增量，协程动作不使用。</param>
        public void OnExecute(float dt)
        {
        }

        /// <summary>不追加结束处理，正常结束时已由完成槽断开本轮绑定。</summary>
        public void OnFinish()
        {
        }
    }

    /// <summary>
    /// 将协程工厂或已有枚举器接入动作组合。
    /// </summary>
    /// <remarks>取消或 Reset 会停止当前协程；暂停动作不会暂停协程。</remarks>
    public static class CoroutineExtension
    {
        /// <summary>
        /// 追加协程动作，在每次启动时调用工厂创建枚举器。
        /// </summary>
        /// <param name="self">要追加协程动作的父组合。</param>
        /// <param name="coroutineGetter">每次启动时调用的协程工厂。</param>
        /// <returns>父组合，可继续追加动作。</returns>
        public static ISequence Coroutine(this ISequence self, Func<IEnumerator> coroutineGetter)
        {
            return self.Append(CoroutineAction.Allocate(coroutineGetter));
        }

        /// <summary>
        /// 包装已有枚举器，不创建新枚举器；Reset 不会使枚举器回到起点。
        /// </summary>
        /// <param name="self">被包装的原枚举器。</param>
        /// <returns>使用原枚举器的协程动作。</returns>
        public static IAction ToAction(this IEnumerator self)
        {
            return CoroutineAction.Allocate(() => self);
        }
    }
}
