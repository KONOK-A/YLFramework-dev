using System.Collections.Generic;
using System.Linq;

namespace YLFramework.Resource
{
    public sealed partial class ResourceSystem
    {
        private Queue<ResourceDiagnosticsReport> _diagnosticHistory;
        /// <summary>只复制 YL 当前责任，调用不会初始化、加载或增加任何官方引用。</summary>
        /// <returns>与运行中对象断开的只读值快照。</returns>
        public ResourceDiagnosticsReport GetDiagnostics()
        {
            CheckThread();
            var report = new ResourceDiagnosticsReport(State, _work.Count,
                _leases.Select(lease =>
                {
                    _leaseInfo.TryGetValue(lease, out var info);
                    return new ResourceLeaseDiagnostics(info.Id, lease.GetType().Name, info.Key,
                        lease.Scope?.OwnerLabel ?? info.Owner, _internalLeases.Contains(lease), lease.IsDisposed, lease.IsSettled);
                }),
                _scopes.Select(scope => new ResourceScopeDiagnostics(scope.OwnerLabel, scope.IsDisposed,
                     _leases.Count(lease => ReferenceEquals(lease.Scope, scope)), scope.CloseError?.ToString())),
                _cleanupErrors.Select(error => error.ToString()), _requests.Select(request => request.CaptureDiagnostics()),
                _instanceCaches.Select(cache => cache.CaptureDiagnostics()), _sceneLeases.Select(scene => scene.CaptureDiagnostics()),
                _drains.Select(drain => drain.Details));
            if (Configuration.DiagnosticHistoryCapacity > 0)
            {
                _diagnosticHistory ??= new Queue<ResourceDiagnosticsReport>();
                _diagnosticHistory.Enqueue(report);
                while (_diagnosticHistory.Count > Configuration.DiagnosticHistoryCapacity)
                    _diagnosticHistory.Dequeue();
            }
            return report;
        }

        /// <summary>取得显式查询产生的有界历史；不会自动采样、加载或增加资源引用。</summary>
        /// <returns>无 Unity 对象或官方 Handle 的历史值快照。</returns>
        public IReadOnlyList<ResourceDiagnosticsReport> GetDiagnosticsHistory()
        {
            CheckThread();
            return _diagnosticHistory?.ToArray() ?? System.Array.Empty<ResourceDiagnosticsReport>();
        }

        /// <summary>只清理本方诊断历史，不影响资源或官方缓存。</summary>
        public void ClearDiagnosticsHistory()
        {
            CheckThread();
            _diagnosticHistory?.Clear();
        }
    }
}
