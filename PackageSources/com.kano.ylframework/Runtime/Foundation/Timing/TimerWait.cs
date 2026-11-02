using UnityEngine;

namespace YLFramework.Timing
{
    /// <summary>
    /// 仅观察一次精确 run 的 Coroutine 等待器，不拥有取消权限。仅主线程使用。
    /// 首次观察到终态后保存结果，不再保留 run；仅 Completed 表示 Timer 正常完成。
    /// </summary>
    /// <remarks>
    /// 复用槽在观察前启动新代次时，旧 run 呈现 Invalidated，而不是跟随新 run。
    /// 未观察到的历史完成结果无法从复用槽恢复；需要保留结果时应在重启前观察。
    /// </remarks>
    public sealed class TimerWait : CustomYieldInstruction
    {
        private TimerHandle _handle;
        private TimerState _status;
        private bool _isFinished;

        /// <summary>捕获精确句柄并立即观察一次，使已经结束的任务直接保存终态快照。</summary>
        internal TimerWait(TimerHandle handle)
        {
            _handle = handle;
            Observe();
        }

        /// <summary>未结束时为当前捕获 run 的状态，结束后为首次观察到的终态快照。</summary>
        public TimerState Status
        {
            get
            {
                Observe();
                return _status;
            }
        }
        /// <summary>是否已观察到终态；末轮 Executing 仍为 false。</summary>
        public bool IsFinished
        {
            get
            {
                Observe();
                return _isFinished;
            }
        }
        /// <summary>仅正常 Completed 为 true；取消、故障、失效均为 false。</summary>
        public bool IsCompleted => Status == TimerState.Completed;
        /// <summary>供 Unity 协程等待首次终态观察，不推进或取消计时任务，也不重新抛出任务异常。</summary>
        public override bool keepWaiting => !IsFinished;

        /// <summary>更新捕获代次的观察状态，首次进入终态后固定结果并释放对任务存储的引用。</summary>
        private void Observe()
        {
            if (_isFinished)
            {
                return;
            }

            _status = _handle.Status;
            _isFinished = _status == TimerState.Completed || _status == TimerState.Cancelled ||
                _status == TimerState.Faulted || _status == TimerState.Invalidated;
            if (_isFinished)
            {
                _handle = default;
            }
        }
    }
}
