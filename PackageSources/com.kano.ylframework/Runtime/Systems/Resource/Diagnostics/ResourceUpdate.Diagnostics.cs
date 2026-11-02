using System.Linq;

namespace YLFramework.Resource
{
    /// <remarks>更新诊断从当前远端枚举栈复制阶段，不推进网络、取消消费者或延长作业寿命。</remarks>
    public sealed partial class ResourceSystem
    {
        /// <remarks>作业无活动 GET 时只报告检查或准备阶段，不伪造传输字节、重试次数或耗时。</remarks>
        private sealed partial class RemoteJob<T>
        {
            /// <summary>复制最内层活动 GET 的观测，或返回作业级阶段及当前等待者数。</summary>
            public ResourceUpdateDiagnostics CaptureDiagnostics()
            {
                var transfer = _stack.OfType<ResourceTransfer>().FirstOrDefault();
                return transfer == null ? new ResourceUpdateDiagnostics(_key == "check" ? "Checking" : "Preparing", _waiters.Count,
                    0, null, null, null, false, null) : transfer.CaptureDiagnostics(_waiters.Count);
            }
        }
    }

    /// <remarks>传输诊断包含本次 GET 的分片偏移与重试观测，不代表整个更新包的累计网络流量。</remarks>
    internal sealed partial class ResourceTransfer
    {
        /// <summary>复制当前尝试、退避等待、已写字节及单调计时器耗时，不推进传输。</summary>
        internal ResourceUpdateDiagnostics CaptureDiagnostics(int waiters)
        {
            var waiting = _request == null && !_isDone && _clock.Elapsed.TotalSeconds < _retryAt;
            return new ResourceUpdateDiagnostics(waiting ? "RetryDelay" : "Downloading", waiters, Progress,
                _sink?.Written ?? _part?.Length ?? 0, _length > 0 ? _length : (long?)null,
                System.Math.Max(0, _attempt - 1), waiting, _clock.Elapsed.TotalMilliseconds);
        }
    }
}
