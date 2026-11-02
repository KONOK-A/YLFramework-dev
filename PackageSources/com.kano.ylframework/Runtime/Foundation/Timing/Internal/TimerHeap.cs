using System;

namespace YLFramework.Timing.Internal
{
    /// <summary>单一计时域的索引最小堆，按到期值、登记序号稳定排序，支持定位移除任务。</summary>
    /// <remarks>从 YL Tools/Timing/Internal/BinaryHeap 收窄，保留从 1 开始的索引及局部修复，并检查任务归属。</remarks>
    internal sealed class TimerHeap
    {
        private readonly TimerClock _clock;
        private TimerRun[] _array = new TimerRun[10];
        /// <summary>当前在堆中的任务数量，不包含数组索引零的空位。</summary>
        internal int Count
        {
            get; private set;
        }
        /// <summary>最早到期的任务；空堆返回 null，不移除元素。</summary>
        internal TimerRun Top => Count == 0 ? null : _array[1];

        /// <summary>创建仅接收指定计时域任务的空堆。</summary>
        internal TimerHeap(TimerClock clock) => _clock = clock;

        /// <summary>接收未入堆且时钟匹配的任务，建立归属并上移至正确位置。</summary>
        internal void Insert(TimerRun run)
        {
            if (run.Clock != _clock || run.Heap != null || run.HeapIndex != 0)
            {
                throw new InvalidOperationException("Timer run already belongs to a queue or uses another clock.");
            }

            if (Count + 1 >= _array.Length)
            {
                Array.Resize(ref _array, checked(Math.Max(_array.Length + 4, (int)Math.Round(_array.Length * 1.6))));
            }

            run.Heap = this;
            _array[++Count] = run;
            PercolateUp(Count);
        }

        /// <summary>取出并解除最早任务的堆归属；空堆返回 null。</summary>
        internal TimerRun Pop()
        {
            var run = Top;
            if (run != null)
            {
                Remove(run);
            }

            return run;
        }

        /// <summary>核对任务归属与索引后定位移除，以尾节点补位并修复堆顺序。</summary>
        internal void Remove(TimerRun run)
        {
            var index = run.HeapIndex;
            if (!ReferenceEquals(run.Heap, this) || index < 1 || index > Count || !ReferenceEquals(_array[index], run))
            {
                throw new InvalidOperationException("Timer heap ownership does not match its index.");
            }

            run.HeapIndex = 0;
            run.Heap = null;
            var last = _array[Count];
            _array[Count--] = null;
            if (index > Count)
            {
                return;
            }

            _array[index] = last;
            last.HeapIndex = index;
            var parent = index >> 1;
            if (parent > 0 && ComesBefore(last, _array[parent]))
            {
                PercolateUp(index);
            }
            else
            {
                PercolateDown(index);
            }
        }

        /// <summary>按当前计时域的到期值比较任务，同值时优先登记序号较小的任务。</summary>
        private bool ComesBefore(TimerRun a, TimerRun b)
        {
            var compare = _clock == TimerClock.RenderedFrame ? a.DueFrame.CompareTo(b.DueFrame) : a.DueSeconds.CompareTo(b.DueSeconds);
            return compare < 0 || compare == 0 && a.Order < b.Order;
        }

        /// <summary>向父节点方向修复指定任务的位置，并同步维护所有受影响任务的索引。</summary>
        private void PercolateUp(int index)
        {
            var run = _array[index];
            var parent = index >> 1;
            while (parent >= 1 && ComesBefore(run, _array[parent]))
            {
                _array[index] = _array[parent];
                _array[index].HeapIndex = index;
                index = parent;
                parent = index >> 1;
            }
            _array[index] = run;
            run.HeapIndex = index;
        }

        /// <summary>沿较早到期的子节点下移指定任务，并同步维护所有受影响任务的索引。</summary>
        private void PercolateDown(int index)
        {
            var run = _array[index];
            var child = index << 1;
            while (child <= Count)
            {
                if (child < Count && ComesBefore(_array[child + 1], _array[child]))
                {
                    child++;
                }

                if (!ComesBefore(_array[child], run))
                {
                    break;
                }

                _array[index] = _array[child];
                _array[index].HeapIndex = index;
                index = child;
                child = index << 1;
            }
            _array[index] = run;
            run.HeapIndex = index;
        }
    }
}
