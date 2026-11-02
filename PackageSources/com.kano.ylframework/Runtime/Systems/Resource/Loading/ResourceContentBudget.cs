using System;

namespace YLFramework.Resource
{
    /// <summary>预算随原 BundleRecord/input 归属；等待者和 native 并发槽不是驻留计量单位。</summary>
    internal sealed class ResourceContentBudget
    {
        private readonly object _gate = new();
        internal long MaximumBytes
        {
            get;
        }
        internal long AllocatedBytes
        {
            get; private set;
        }
        internal long ReservedBytes
        {
            get; private set;
        }
        internal long PeakBytes
        {
            get; private set;
        }
        internal int LiveInputs
        {
            get; private set;
        }

        /// <summary>建立具有正字节上限的内容缓冲预算。</summary>
        internal ResourceContentBudget(long maximumBytes)
        {
            if (maximumBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            MaximumBytes = maximumBytes;
        }

        /// <summary>在同一锁内采样已分配、预留、峰值及活跃输入计数。</summary>
        internal (long Allocated, long Reserved, long Peak, int Inputs) Capture()
        {
            lock (_gate)
            {
                return (AllocatedBytes, ReservedBytes, PeakBytes, LiveInputs);
            }
        }

        /// <summary>原子预留指定数量的固定大小输入预算；不足时不部分占用。</summary>
        internal ResourceContentReservation[] Reserve(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (count == 0)
            {
                return Array.Empty<ResourceContentReservation>();
            }

            lock (_gate)
            {
                var bytes = checked((long)count * ResourceDecryptStream.DataBufferBytes);
                var used = checked(AllocatedBytes + ReservedBytes);
                if (bytes > MaximumBytes - used)
                {
                    throw new ResourceException(ResourceError.ContentBufferUnavailable,
                        $"Content buffers require {bytes} additional bytes; allocated={AllocatedBytes}, reserved={ReservedBytes}, limit={MaximumBytes}. Release owned resources and explicitly retry.");
                }

                var tickets = new ResourceContentReservation[count];
                for (var i = 0; i < count; i++)
                {
                    tickets[i] = new ResourceContentReservation(this);
                }

                ReservedBytes += bytes;
                PeakBytes = Math.Max(PeakBytes, used + bytes);
                return tickets;
            }
        }

        /// <summary>将未分配票据的预留额度转为已分配额度并登记活跃输入。</summary>
        internal void Allocate(ResourceContentReservation ticket)
        {
            lock (_gate)
            {
                if (ticket.State != 0)
                {
                    throw new InvalidOperationException("A content reservation can only be allocated once.");
                }

                ticket.State = 1;
                ReservedBytes -= ResourceDecryptStream.DataBufferBytes;
                AllocatedBytes += ResourceDecryptStream.DataBufferBytes;
                LiveInputs++;
            }
        }

        /// <summary>按票据当前状态归还预留或已分配额度，重复归还不重复扣账。</summary>
        internal void Release(ResourceContentReservation ticket)
        {
            lock (_gate)
            {
                if (ticket.State == 2)
                {
                    return;
                }

                if (ticket.State == 0)
                {
                    ReservedBytes -= ResourceDecryptStream.DataBufferBytes;
                }
                else
                {
                    AllocatedBytes -= ResourceDecryptStream.DataBufferBytes;
                    LiveInputs--;
                }
                ticket.State = 2;
            }
        }

        /// <summary>将尚未释放的已分配票据退回预留态，供同一输入责任重试。</summary>
        internal void PrepareRetry(ResourceContentReservation ticket)
        {
            lock (_gate)
            {
                if (ticket.State == 2)
                {
                    throw new ObjectDisposedException(nameof(ResourceContentReservation));
                }

                if (ticket.State == 0)
                {
                    return;
                }

                AllocatedBytes -= ResourceDecryptStream.DataBufferBytes;
                ReservedBytes += ResourceDecryptStream.DataBufferBytes;
                LiveInputs--;
                ticket.State = 0;
            }
        }
    }

    /// <summary>一份固定大小内容缓冲的预算票据；状态转换由所属预算加锁结算。</summary>
    internal sealed class ResourceContentReservation : IDisposable
    {
        private readonly ResourceContentBudget _budget;
        internal int State;
        /// <summary>关联预算所有者，初始状态为已预留但尚未分配。</summary>
        internal ResourceContentReservation(ResourceContentBudget budget) => this._budget = budget;
        /// <summary>将本票据的预留额度单次转为活跃输入额度。</summary>
        internal void Allocate() => _budget.Allocate(this);
        /// <summary>保留本票据额度，撤销本次分配以准备重试。</summary>
        internal void PrepareRetry() => _budget.PrepareRetry(this);
        /// <summary>幂等归还本票据占用的额度，不负责关闭实际输入流。</summary>
        public void Dispose() => _budget.Release(this);
    }
}
