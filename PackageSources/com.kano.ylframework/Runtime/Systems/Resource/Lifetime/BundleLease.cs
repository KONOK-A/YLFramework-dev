using System;
using UnityEngine;

namespace YLFramework.Resource
{
    /// <summary>仅描述创建这一次真实租约的调用来源；不参与图引用、生产或释放判断。</summary>
    internal readonly struct BundleLeaseOrigin
    {
        internal readonly ResourceAddress? Address;
        internal readonly string GroupId;
        internal readonly long? SceneSequence;
        /// <summary>保存调用来源标签，不新增 Bundle 图责任。</summary>
        internal BundleLeaseOrigin(ResourceAddress? address = null, string groupId = null, long? sceneSequence = null)
        {
            Address = address;
            GroupId = groupId;
            SceneSequence = sceneSequence;
        }
    }

    /// <summary>显式 Bundle 图根责任。native 包仅供在有效 Lease 内借读，不得自行 Unload；资产消费者优先使用 AssetLease。</summary>
    /// <remarks>调用方直接启动的 native 操作不进入 Resource 的内部操作账本，必须自行持有本 Lease 到操作及使用结束。</remarks>
    public sealed class BundleLease : IDisposable
    {
        private ResourceSystem.BundleRecord _record;
        internal readonly bool IsPublic;
        internal BundleLease Previous;
        internal BundleLease Next;
        internal ResourceSystem.BundleRecord Record => _record;
        internal readonly BundleLeaseOrigin Origin;
        private string _ownerLabel;
        /// <summary>创建本租约的目录项目标识，归还后仍可读取。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>本图根的逻辑 Bundle 标识，归还后仍可读取。</summary>
        public string BundleId
        {
            get;
        }
        /// <summary>本租约是否已归还，不代表其他图根或依赖入边也已释放。</summary>
        public bool IsDisposed => _record == null;
        /// <summary>是否仍持有图根、资源会话有效且原生 Bundle 存活。</summary>
        public bool IsValid => _record != null && _record.Owner.IsSessionActive && _record.Bundle;
        /// <summary>真实持有者的可读说明，不影响 Bundle 图根计数；null 表示未标注。</summary>
        /// <exception cref="ObjectDisposedException">向已归还租约写入标签。</exception>
        /// <exception cref="InvalidOperationException">不在 Unity 主线程写入标签。</exception>
        public string OwnerLabel
        {
            get => _ownerLabel;
            set
            {
                if (_record == null)
                {
                    throw new ObjectDisposedException(nameof(BundleLease));
                }

                _record.Owner.CheckThread();
                _ownerLabel = value;
            }
        }
        /// <summary>在本租约及资源会话有效时借读原生 Bundle，不转移卸载权。</summary>
        /// <exception cref="ObjectDisposedException">本租约已归还。</exception>
        /// <exception cref="InvalidOperationException">不在 Unity 主线程读取。</exception>
        /// <exception cref="ResourceException">资源会话结束，或 Bundle 未就绪、已被外部卸载。</exception>
        public AssetBundle Bundle
        {
            get
            {
                if (_record == null)
                {
                    throw new ObjectDisposedException(nameof(BundleLease));
                }

                _record.Owner.CheckThread();
                if (!_record.Owner.IsSessionActive)
                {
                    throw new ResourceException(ResourceError.SessionUnavailable, "The Bundle lease belongs to an ended session.");
                }

                if (!_record.Bundle)
                {
                    throw new ResourceException(ResourceError.LoadFailed, "The leased Bundle is not ready or was unloaded externally.");
                }

                return _record.Bundle;
            }
        }
        /// <summary>增加一份图根引用并登记诊断身份，区分公共持有和系统内部保活。</summary>
        internal BundleLease(ResourceSystem.BundleRecord record, bool publicLease, BundleLeaseOrigin origin = default)
        {
            this._record = record;
            IsPublic = publicLease;
            Origin = origin;
            ProjectId = record.Snapshot.Document.ProjectId;
            BundleId = record.Descriptor.LogicalId;
            record.References++;
            record.RootPins++;
            record.Owner.RegisterBundleDiagnostics(this);
        }
        /// <summary>单次归还本图根责任；实际回收还须等待其他引用和在途操作结束。</summary>
        /// <remarks>先移走本租约身份，再结算引用，避免重入重复归还。</remarks>
        /// <exception cref="InvalidOperationException">尚未归还时在非 Unity 主线程调用。</exception>
        public void Dispose()
        {
            if (_record == null)
            {
                return;
            }

            _record.Owner.CheckThread();
            var owned = _record;
            owned.Owner.UnregisterBundleDiagnostics(this);
            _record = null;
            owned.Owner.ReleaseBundle(this, owned);
        }
    }
}
