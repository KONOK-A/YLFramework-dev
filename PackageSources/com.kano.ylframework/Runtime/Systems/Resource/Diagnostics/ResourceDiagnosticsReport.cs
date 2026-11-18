using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace YLFramework.Resource
{
    /// <summary>YL 业务责任的值快照，不保活资源，也不伪称知道官方总引用数或 native 内存。</summary>
    public sealed class ResourceDiagnosticsReport
    {
        /// <summary>本次值快照的 UTC 时间。</summary>
        public DateTime CapturedAtUtc
        {
            get;
        }
        /// <summary>本方会话状态。</summary>
        public ResourceSystemState State
        {
            get;
        }
        /// <summary>本方仍登记的租约数，不是官方总引用数。</summary>
        public int LeaseCount => Leases.Count;
        /// <summary>本方仍登记的 Scope 数。</summary>
        public int ScopeCount => Scopes.Count;
        /// <summary>尚未结束交割或观察的业务请求数。</summary>
        public int RequestCount => Requests.Count;
        /// <summary>本方逐帧工作数，不代表全部官方 operation。</summary>
        public int PendingWorkCount
        {
            get;
        }
        /// <summary>消费者已退出但仍等待官方完成的本方引用数。</summary>
        public int DrainingHandleCount => DrainingHandles.Count;
        /// <summary>无对象引用的租约明细。</summary>
        public IReadOnlyList<ResourceLeaseDiagnostics> Leases
        {
            get;
        }
        /// <summary>无对象引用的 Scope 明细。</summary>
        public IReadOnlyList<ResourceScopeDiagnostics> Scopes
        {
            get;
        }
        /// <summary>Tracked business requests, including results that still await transfer.</summary>
        public IReadOnlyList<ResourceRequestDiagnostics> Requests
        {
            get;
        }
        /// <summary>Owned Prefab pools, including retired pools with pending cleanup.</summary>
        public IReadOnlyList<ResourcePoolDiagnostics> Pools
        {
            get;
        }
        /// <summary>Independently owned load and unload intents, not all Unity scenes.</summary>
        public IReadOnlyList<ResourceSceneDiagnostics> Scenes
        {
            get;
        }
        /// <summary>Owned handle references waiting for official completion after the caller left.</summary>
        public IReadOnlyList<ResourceDrainDiagnostics> DrainingHandles
        {
            get;
        }
        /// <summary>本方未掩盖的清理错误。</summary>
        public IReadOnlyList<string> Errors
        {
            get;
        }

        internal ResourceDiagnosticsReport(ResourceSystemState state, int work,
            IEnumerable<ResourceLeaseDiagnostics> leases, IEnumerable<ResourceScopeDiagnostics> scopes, IEnumerable<string> errors,
            IEnumerable<ResourceRequestDiagnostics> requests, IEnumerable<ResourcePoolDiagnostics> pools,
            IEnumerable<ResourceSceneDiagnostics> scenes, IEnumerable<ResourceDrainDiagnostics> drains)
        {
            CapturedAtUtc = DateTime.UtcNow;
            State = state;
            PendingWorkCount = work;
            Leases = Copy(leases);
            Scopes = Copy(scopes);
            Errors = Copy(errors);
            Requests = Copy(requests);
            Pools = Copy(pools);
            Scenes = Copy(scenes);
            DrainingHandles = Copy(drains);
        }

        internal static ReadOnlyCollection<T> Copy<T>(IEnumerable<T> values) => new List<T>(values ?? Array.Empty<T>()).AsReadOnly();
    }

    /// <summary>Detached identity and latest outcome of one tracked request.</summary>
    public readonly struct ResourceRequestDiagnostics
    {
        public int Id
        {
            get;
        }
        public string Operation
        {
            get;
        }
        public string Key
        {
            get;
        }
        public string Owner
        {
            get;
        }
        public ResourceRequestStatus Status
        {
            get;
        }
        public string Error
        {
            get;
        }

        internal ResourceRequestDiagnostics(int id, (string Operation, string Key, string Owner) identity, ResourceRequestStatus status, string error)
        {
            Id = id;
            Operation = identity.Operation;
            Key = identity.Key;
            Owner = identity.Owner;
            Status = status;
            Error = error;
        }
    }

    /// <summary>Live idle and checked-out instances, and work that still protects the Prefab pin.</summary>
    public readonly struct ResourcePoolDiagnostics
    {
        public string Key
        {
            get;
        }
        public InstanceCachePolicy Policy
        {
            get;
        }
        public string Scene
        {
            get;
        }
        public bool Retired
        {
            get;
        }
        public int IdleCount
        {
            get;
        }
        public int ActiveCount
        {
            get;
        }
        public int CleanupCount
        {
            get;
        }
        public int Transitions
        {
            get;
        }

        internal ResourcePoolDiagnostics(string key, InstanceCachePolicy policy, string scene, bool retired, int idle, int active, int cleanup, int transitions)
        {
            Key = key;
            Policy = policy;
            Scene = scene;
            Retired = retired;
            IdleCount = idle;
            ActiveCount = active;
            CleanupCount = cleanup;
            Transitions = transitions;
        }
    }

    /// <summary>A YL scene load sequence and native state, with no retained Scene or load handle.</summary>
    public readonly struct ResourceSceneDiagnostics
    {
        public long Sequence
        {
            get;
        }
        public string Key
        {
            get;
        }
        public string Owner
        {
            get;
        }
        public string State
        {
            get;
        }
        public string Scene
        {
            get;
        }
        public bool Loaded
        {
            get;
        }
        public string Error
        {
            get;
        }

        internal ResourceSceneDiagnostics(long sequence, string key, string owner, string state, string scene, bool loaded, string error)
        {
            Sequence = sequence;
            Key = key;
            Owner = owner;
            State = state;
            Scene = scene;
            Loaded = loaded;
            Error = error;
        }
    }

    /// <summary>An abandoned wait that still has one owned official reference to return.</summary>
    public readonly struct ResourceDrainDiagnostics
    {
        public string Operation
        {
            get;
        }
        public string Key
        {
            get;
        }
        public string Owner
        {
            get;
        }

        internal ResourceDrainDiagnostics(string operation, string key, string owner)
        {
            Operation = operation;
            Key = key;
            Owner = owner;
        }
    }

    /// <summary>一份本方租约的责任身份，不包含 Unity 对象或官方 Handle。</summary>
    public readonly struct ResourceLeaseDiagnostics
    {
        /// <summary>当前会话内的责任 ID。</summary>
        public int Id
        {
            get;
        }
        /// <summary>租约类别。</summary>
        public string Kind
        {
            get;
        }
        /// <summary>取得时冻结的键。</summary>
        public string Key
        {
            get;
        }
        /// <summary>Scope 或独立业务持有者标签。</summary>
        public string Owner
        {
            get;
        }
        /// <summary>是否为池或在途排空的内部保活。</summary>
        public bool Internal
        {
            get;
        }
        /// <summary>是否已经发起归还。</summary>
        public bool Disposed
        {
            get;
        }
        /// <summary>是否真正结束本份责任。</summary>
        public bool Settled
        {
            get;
        }
        internal ResourceLeaseDiagnostics(int id, string kind, string key, string owner, bool internalPin, bool disposed, bool settled)
        {
            Id = id;
            Kind = kind;
            Key = key;
            Owner = owner;
            Internal = internalPin;
            Disposed = disposed;
            Settled = settled;
        }
    }

    /// <summary>业务 Scope 的关闭与持有信息，不代表资源已经物理卸载。</summary>
    public readonly struct ResourceScopeDiagnostics
    {
        /// <summary>业务标签。</summary>
        public string Owner
        {
            get;
        }
        /// <summary>是否已关闭新工作入口。</summary>
        public bool Closing
        {
            get;
        }
        /// <summary>尚未结算的本 Scope 租约数。</summary>
        public int LeaseCount
        {
            get;
        }
        /// <summary>最近的关闭错误，成功时为空。</summary>
        public string Error
        {
            get;
        }
        internal ResourceScopeDiagnostics(string owner, bool closing, int leases, string error)
        {
            Owner = owner;
            Closing = closing;
            LeaseCount = leases;
            Error = error;
        }
    }
}
