using System;
using UnityEngine;

namespace YLFramework.Resource
{
    /// <summary>一次取得对应一次独立责任；只释放自己，不池化公开身份，不使用 finalizer。</summary>
    /// <typeparam name="T">要借读的 Unity 资源类型。</typeparam>
    /// <remarks>
    /// Lease（租约）表示对资源的一份持有责任，不是资源副本；同一资源可由多个租约共同持有。
    /// 资源读取和归还应在 Unity 主线程进行。保存 Asset 引用不会延长租约，使用结束后仍需主动 Dispose。
    /// 归还租约不保证立刻卸载底层资源，其他租约或缓存可能仍在持有它；不要自行销毁借读的资源。
    /// </remarks>
    /// <example>
    /// 假设 resources 是已启动的 ResourceSystem，address 指向 Texture2D；这里只在租约范围内读取纹理。
    /// <code>
    /// using (var lease = resources.Acquire&lt;Texture2D&gt;(address))
    /// {
    ///     Debug.Log(lease.Asset.width);
    /// }
    /// </code>
    /// </example>
    public sealed class AssetLease<T> : IDisposable, IAssetLeaseDiagnostics where T : UnityEngine.Object
    {
        // 共享关系：Lease A / Lease B -> 同一个 AssetRecord -> Unity 资源。
        private ResourceSystem.AssetRecord _record;
        // 实例缓存用 pin 保活来源 Prefab，在缓存收尾且实例/操作清空后归还，见 InstanceCache.Prune。
        private readonly bool _instancePin;
        private string _ownerLabel;
        // 这份租约双向链表供诊断遍历；释放判断使用计数和状态，不依赖链表。
        private IAssetLeaseDiagnostics _previous;
        private IAssetLeaseDiagnostics _next;
        /// <summary>当前持有的共享记录；归还本租约后为 null。</summary>
        internal ResourceSystem.AssetRecord Record => _record;
        /// <summary>取得本租约时使用的资源地址；归还后仍保留，便于定位来源。</summary>
        public ResourceAddress Address
        {
            get;
        }
        /// <summary>本租约是否已经归还；不代表底层会话或资源仍然有效，应结合 IsValid 判断。</summary>
        public bool IsDisposed => _record == null;
        /// <summary>当前是否仍持有记录，且所属会话未结束、Unity 资源也未被销毁。</summary>
        public bool IsValid => _record != null && _record.Owner.IsSessionActive && _record.Asset;

        /// <summary>调用方显式填写的真实责任说明；未填写时诊断保持未标注，不参与 retain/release。</summary>
        /// <remarks>读取标签不会延长资源寿命；写入要求本租约尚未归还，并且在 Unity 主线程执行。</remarks>
        /// <exception cref="ObjectDisposedException">向已经归还的租约写入标签。</exception>
        /// <exception cref="InvalidOperationException">不在 Unity 主线程写入标签。</exception>
        public string OwnerLabel
        {
            get => _ownerLabel;
            set
            {
                if (_record == null)
                {
                    throw new ObjectDisposedException(nameof(AssetLease<T>));
                }

                _record.Owner.CheckThread();
                _ownerLabel = value;
            }
        }

        IAssetLeaseDiagnostics IAssetLeaseDiagnostics.Previous
        {
            get => _previous; set => _previous = value;
        }
        IAssetLeaseDiagnostics IAssetLeaseDiagnostics.Next
        {
            get => _next; set => _next = value;
        }
        bool IAssetLeaseDiagnostics.IsInstancePin => _instancePin;

        /// <summary>借读对象，仅在本 Lease 有效期间使用；外部销毁和会话退出均明确拒绝。</summary>
        /// <exception cref="ObjectDisposedException">本租约已归还。</exception>
        /// <exception cref="InvalidOperationException">不在 Unity 主线程读取资源。</exception>
        /// <exception cref="ResourceException">所属资源会话已结束，或借读对象已被外部销毁。</exception>
        public T Asset
        {
            get
            {
                // 句柄未归还不代表资源会话仍有效，两层校验都通过后才能借读共享对象。
                if (_record == null)
                {
                    throw new ObjectDisposedException(nameof(AssetLease<T>));
                }

                _record.Owner.CheckThread();
                if (!_record.Owner.IsSessionActive)
                {
                    throw new ResourceException(ResourceError.SessionUnavailable, "The asset lease belongs to an ended session.", address: Address);
                }

                // Unity 对象的原生部分可能已被外部销毁，而 C# 包装引用仍在；用 Unity 的存活判断拒绝这种情况。
                if (!_record.Asset)
                {
                    throw new ResourceException(ResourceError.LoadFailed, "The leased Unity object was destroyed externally.", address: Address);
                }

                return (T)_record.Asset;
            }
        }

        /// <summary>为已加载记录增加一份持有责任并挂入诊断链表；仅由资源系统创建。</summary>
        /// <param name="record">资源系统管理的已加载共享记录。</param>
        /// <param name="address">本次取得租约时使用的资源地址。</param>
        /// <param name="instancePin">是否作为实例的内部保活责任，防止实例存活期间提前释放其来源资源。</param>
        internal AssetLease(ResourceSystem.AssetRecord record, ResourceAddress address, bool instancePin = false)
        {
            this._record = record;
            this._instancePin = instancePin;
            Address = address;
            // 例如两份普通租约加一份 pin：LeaseCount=3、InstancePins=1，pin 包含在总数内。
            record.LeaseCount++;
            if (instancePin)
            {
                record.InstancePins++;
            }

            // 头插：旧头 H <-> ... 变为 this <-> H <-> ...，再由 LeaseHead 指向新头。
            _next = record.LeaseHead;
            if (_next != null)
            {
                _next.Previous = this;
            }

            record.LeaseHead = this;
        }

        /// <summary>归还本租约的持有责任；已归还时直接返回，不重复扣减引用计数。</summary>
        /// <remarks>首次归还须在 Unity 主线程执行；是否实际卸载由资源系统结合其他持有者和缓存决定。</remarks>
        /// <exception cref="InvalidOperationException">尚未归还的租约在非 Unity 主线程释放。</exception>
        public void Dispose()
        {
            if (_record == null)
            {
                return;
            }

            _record.Owner.CheckThread();
            // 前后租约 P/N 直接相连：P <-> this <-> N 变为 P <-> N。
            // 表头没有 P，改由 LeaseHead 指向 N；尾节点没有 N，不需回连后继。
            var owned = _record;
            if (_previous == null)
            {
                owned.LeaseHead = _next;
            }
            else
            {
                _previous.Next = _next;
            }

            if (_next != null)
            {
                _next.Previous = _previous;
            }

            _previous = null;
            _next = null;
            // 先使本句柄失效，避免下方释放回调重入时重复扣减同一份责任。
            _record = null;
            // ReleaseLease 结算计数并判断缓存或释放；底层释放抛错也不会恢复本句柄及其链表位置。
            owned.Owner.ReleaseLease(owned, _instancePin);
        }
    }
}
