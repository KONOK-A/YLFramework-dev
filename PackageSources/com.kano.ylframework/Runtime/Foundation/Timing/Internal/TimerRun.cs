using System;

namespace YLFramework.Timing.Internal
{
    /// <summary>保存一次计时任务的状态、队列位置和取消责任，供调度器与精确句柄共享。</summary>
    /// <remarks>
    /// 普通任务不池化，终态保留观察数据但清除调度归属及用户委托。
    /// 显式复用槽通过更换代次隔离旧句柄和旧回调栈的收尾。
    /// </remarks>
    internal sealed class TimerRun
    {
        internal TimerScheduler Owner;
        internal TimerScope Scope;
        internal TimerGameObjectBinding Binding;
        internal string Name;
        internal bool NeedsAnchor;
        internal long EarliestFrame;
        internal ulong Generation;
        internal ulong ExecutingGeneration;
        // 仅 Executing 的非末轮使用：请求回调退出后的暂停，不提前改写当前执行状态。
        internal bool PauseNext;
        internal TimerState State;
        internal TimerClock Clock;
        internal Action Callback;
        internal Action<long> TickCallback;
        internal Action<TimerCancellationReason> OnCancelled;
        internal long ExecutionCount;
        internal int? RemainingCount;
        internal double IntervalSeconds;
        internal long IntervalFrames;
        internal double DueSeconds;
        internal long DueFrame;
        internal double FrozenSeconds;
        internal long FrozenFrames;
        internal ulong Order;
        internal TimerHeap Heap;
        internal int HeapIndex;
        internal int PendingIndex = -1;
    }
}
