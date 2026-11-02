using System;
using UnityEngine;

namespace YLFramework.Resource
{
    /// <summary>具体来源的内部边界，不作为面向消费者的插件注册或路由平台。</summary>
    /// <remarks>由 ResourceSystem 在 Unity 主线程调用；每次生产结果由共享记录接管，来源不得另建消费者租约表。</remarks>
    internal interface IResourceSource
    {
        /// <summary>初始化时用于精确路由的唯一来源身份。</summary>
        ResourceSourceId Id
        {
            get;
        }
        /// <summary>是否允许同步生产；不表示在途异步操作可以同步等待。</summary>
        bool SupportsSynchronousLoad
        {
            get;
        }
        /// <summary>校验条目的来源专用语法与能力边界，不在此交付资源。</summary>
        void Validate(ResourceCatalogEntry entry);
        /// <summary>同步生产资产与可选保活责任，交给调用方统一结算。</summary>
        ResourceSourceResult Load(ResourceCatalogEntry entry, Type type);
        /// <summary>创建独占原生完成观察的操作；不能因消费者取消就丢弃未完成的收尾责任。</summary>
        ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type);
        /// <summary>执行本来源拥有的资产释放策略；额外保活对象由记录另行 Dispose。</summary>
        void Release(UnityEngine.Object asset);
    }

    /// <summary>一次来源生产的资产与附加保活责任；值的复制本身不增加引用计数。</summary>
    internal readonly struct ResourceSourceResult
    {
        /// <summary>生产出的 Unity 对象；失败时也可能需要调用来源释放已有对象。</summary>
        internal readonly UnityEngine.Object Asset;
        /// <summary>须与资产记录一起归还的额外责任，例如 Bundle 图租约。</summary>
        internal readonly IDisposable Retention;
        /// <summary>封装待交割结果，不在此增加或归还任何持有责任。</summary>
        internal ResourceSourceResult(UnityEngine.Object asset, IDisposable retention = null)
        {
            Asset = asset;
            Retention = retention;
        }
    }

    /// <summary>生产者独占原生观察；未完成时不得 Dispose。取得结果后由唯一记录负责调用来源 Release 并归还附加保活责任。</summary>
    internal abstract class ResourceSourceOperation : IDisposable
    {
        private UnityEngine.Object _asset;
        private IDisposable _retention;
        /// <summary>是否已经发布成功结果或错误；与操作观察器是否释放是不同状态。</summary>
        public bool IsDone
        {
            get; private set;
        }
        /// <summary>完成后的操作观察器是否已释放。</summary>
        public bool IsDisposed
        {
            get; private set;
        }
        /// <summary>来源生产失败的原因；成功或尚未失败时为 null。</summary>
        public Exception Error
        {
            get; private set;
        }
        /// <summary>当前阶段是否支持停止本来源拥有的工作，默认不支持原生取消。</summary>
        public virtual bool SupportsCancellation => false;
        /// <summary>展示用生产进度，默认仅区分未完成与已完成。</summary>
        public virtual float Progress => IsDone ? 1 : 0;
        /// <summary>首次完成时通知并清空当前观察者；不为完成后的新订阅回放。</summary>
        public event Action Completed;

        /// <summary>首次发布资产、错误和附加责任，再同步通知观察者；重复完成不替调用方清理新传入的责任。</summary>
        protected void Complete(UnityEngine.Object value, Exception error, IDisposable ownedRetention = null)
        {
            if (IsDone)
            {
                return;
            }

            _asset = value;
            _retention = ownedRetention;
            Error = error;
            IsDone = true;
            var callback = Completed;
            Completed = null;
            callback?.Invoke();
        }

        /// <summary>将已完成且未释放操作的暂存结果移出并清空，由共享记录负责后续释放。</summary>
        internal ResourceSourceResult TakeResult()
        {
            if (!IsDone || IsDisposed)
            {
                throw new InvalidOperationException("The source operation has no transferable result.");
            }

            var value = new ResourceSourceResult(_asset, _retention);
            _asset = null;
            _retention = null;
            return value;
        }

        /// <summary>默认不取消原生工作；可取消来源只结束自有 IO，仍须通过 Complete 交割收尾。</summary>
        public virtual void Cancel()
        {
        }

        /// <summary>释放已完成操作的观察状态；调用方须先接管结果，本方法不调用来源 Release 或释放保活对象。</summary>
        public virtual void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            if (!IsDone)
            {
                throw new InvalidOperationException("An unfinished native producer must retain its cleanup owner.");
            }

            IsDisposed = true;
            _asset = null;
            _retention = null;
            Completed = null;
        }
    }
}
