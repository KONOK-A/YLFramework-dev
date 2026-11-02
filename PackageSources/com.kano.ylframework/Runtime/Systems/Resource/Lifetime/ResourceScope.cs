using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace YLFramework.Resource
{
    /// <summary>只聚合自己接管的请求与租约，不建立缓存或第二份资源加载权威。</summary>
    /// <remarks>在所属资源系统的 Unity 主线程使用；跟踪请求不自动接管其已领取结果，须显式通过本域领取或交入。</remarks>
    public sealed class ResourceScope : IDisposable
    {
        private readonly ResourceSystem _system;
        private readonly HashSet<IDisposable> _requests = new(ReferenceComparer.Instance);
        private readonly HashSet<IDisposable> _leases = new(ReferenceComparer.Instance);
        /// <summary>本域是否已经关闭；关闭后拒绝继续接管责任。</summary>
        public bool IsDisposed
        {
            get; private set;
        }
        /// <summary>将本域绑定到创建它的资源系统，不取得额外资源引用。</summary>
        internal ResourceScope(ResourceSystem system) => this._system = system;

        /// <summary>按对象身份去重，避免不同责任对象因值相等而被合并。</summary>
        private sealed class ReferenceComparer : IEqualityComparer<IDisposable>
        {
            internal static readonly ReferenceComparer Instance = new();
            /// <summary>判断两个责任对象是否为同一引用。</summary>
            /// <param name="left">第一个责任对象。</param>
            /// <param name="right">第二个责任对象。</param>
            /// <returns>两个引用指向同一对象时为 true。</returns>
            public bool Equals(IDisposable left, IDisposable right) => ReferenceEquals(left, right);
            /// <summary>读取不受对象自定义相等规则影响的身份哈希。</summary>
            /// <param name="value">要读取身份的责任对象。</param>
            /// <returns>该对象的引用身份哈希。</returns>
            public int GetHashCode(IDisposable value) => RuntimeHelpers.GetHashCode(value);
        }

        /// <summary>同步取得资产租约并交由本域管理。</summary>
        /// <typeparam name="T">要借读的 Unity 资源类型。</typeparam>
        /// <param name="address">资源目录中的地址。</param>
        /// <returns>本域持有的租约；调用方借用，不新增持有责任。</returns>
        public AssetLease<T> Acquire<T>(ResourceAddress address) where T : UnityEngine.Object
        {
            CheckAvailable();
            var lease = _system.Acquire<T>(address);
            if (IsDisposed)
            {
                lease.Dispose();
                throw new ObjectDisposedException(nameof(ResourceScope));
            }
            return Own(lease);
        }

        /// <summary>发起资产请求并跟踪其等待及未领取结果；领取到本域须使用本域的 TakeResult。</summary>
        /// <typeparam name="T">要借读的 Unity 资源类型。</typeparam>
        /// <param name="address">资源目录中的地址。</param>
        /// <returns>由本域跟踪的独立消费者请求。</returns>
        public ResourceRequest<AssetLease<T>> AcquireAsync<T>(ResourceAddress address) where T : UnityEngine.Object
        {
            CheckAvailable();
            var request = _system.AcquireAsync<T>(address);
            if (IsDisposed)
            {
                request.Dispose();
                throw new ObjectDisposedException(nameof(ResourceScope));
            }
            return Track(request);
        }

        /// <summary>显式接管一个等待/未领取结果。失败时不接管传入对象。</summary>
        /// <typeparam name="T">请求成功时交付的结果类型。</typeparam>
        /// <param name="request">调用方移交给本域的请求。</param>
        /// <returns>原请求，不创建额外等待者。</returns>
        public ResourceRequest<T> Track<T>(ResourceRequest<T> request)
        {
            CheckAvailable();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            _requests.Add(request);
            return request;
        }

        /// <summary>调用方将自己拥有的 Lease 交给 Scope；不是新增 retain。</summary>
        /// <typeparam name="T">实现 IDisposable 的责任类型。</typeparam>
        /// <param name="lease">由调用方移交释放责任的对象。</param>
        /// <returns>原责任对象，随后由本域统一归还。</returns>
        public T Own<T>(T lease) where T : IDisposable
        {
            CheckAvailable();
            if (lease == null)
            {
                throw new ArgumentNullException(nameof(lease));
            }

            _leases.Add(lease);
            return lease;
        }

        /// <summary>领取已登记请求的结果并立即纳入本域；随后关闭 Request 不释放已接管的结果。</summary>
        /// <typeparam name="T">实现 IDisposable 的请求结果类型。</typeparam>
        /// <param name="request">已经由本域跟踪且成功完成的请求。</param>
        /// <returns>本域持有的结果，调用方不再拥有独立于本域的释放责任。</returns>
        public T TakeResult<T>(ResourceRequest<T> request) where T : IDisposable
        {
            CheckAvailable();
            if (request == null || !_requests.Contains(request))
            {
                throw new ArgumentException("The request is not owned by this scope.", nameof(request));
            }

            var lease = request.TakeResult();
            try
            {
                _leases.Add(lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
            _requests.Remove(request);
            request.Dispose();
            return lease;
        }

        /// <summary>检查所属线程和本域关闭状态，不替代资源系统自身的会话检查。</summary>
        private void CheckAvailable()
        {
            _system.CheckThread();
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(ResourceScope));
            }
        }

        /// <summary>关闭本域，先释放请求，再归还已接管租约；重复调用不重复清理。</summary>
        /// <remarks>先移走全部责任再执行清理，防止重入重复归还；所有成员均尝试清理后汇总异常。</remarks>
        /// <exception cref="AggregateException">一个或多个自有责任清理失败。</exception>
        /// <exception cref="InvalidOperationException">未在 Unity 主线程调用。</exception>
        public void Dispose()
        {
            _system.CheckThread();
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            var pending = new List<IDisposable>(_requests);
            var held = new List<IDisposable>(_leases);
            _requests.Clear();
            _leases.Clear();
            List<Exception> errors = null;
            foreach (var request in pending)
            {
                Release(request, ref errors);
            }

            foreach (var lease in held)
            {
                Release(lease, ref errors);
            }

            if (errors != null)
            {
                throw new AggregateException("Resource scope cleanup failed.", errors);
            }
        }

        /// <summary>归还一个责任并收集异常，使其他自有责任仍有清理机会。</summary>
        private static void Release(IDisposable value, ref List<Exception> errors)
        {
            try
            {
                value.Dispose();
            }
            catch (Exception error)
            {
                (errors ??= new List<Exception>()).Add(error);
            }
        }
    }
}
