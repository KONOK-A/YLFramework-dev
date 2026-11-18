using System;
using System.Threading;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace YLFramework.Resource
{
    /// <summary>只持有一份明确取得的官方引用；共享操作的返回值必须通过 Observe 独立增持。</summary>
    internal sealed class AddressablesHandleOwner<T> : IDisposable
    {
        private readonly ResourceManager _manager;
        private readonly int _threadId;
        private AsyncOperationHandle<T> _handle;
        private bool _disposed;
        private bool _released;

        private AddressablesHandleOwner(ResourceManager manager, AsyncOperationHandle<T> handle)
        {
            Validate(manager, handle);
            _manager = manager;
            _handle = handle;
            _threadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>接管调用者已经独立取得的一份引用；该调用者不能再归还同一责任。</summary>
        internal static AddressablesHandleOwner<T> Take(ResourceManager manager, AsyncOperationHandle<T> handle)
        {
            return new AddressablesHandleOwner<T>(manager, handle);
        }

        /// <summary>对外部或自动释放的共享操作独立增持，不接管返回值本身的释放责任。</summary>
        internal static AddressablesHandleOwner<T> Observe(ResourceManager manager, AsyncOperationHandle<T> handle)
        {
            Validate(manager, handle);
            return new AddressablesHandleOwner<T>(manager, manager.Acquire(handle));
        }

        /// <summary>只借用此句柄观察或订阅；复制它不产生引用，调用者不得 Release。</summary>
        internal AsyncOperationHandle<T> BorrowedHandle
        {
            get
            {
                CheckThread();
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(AddressablesHandleOwner<T>));
                }
                return _handle;
            }
        }

        /// <summary>调用者已退出，但官方操作尚未完成，本方引用仍由排空责任持有。</summary>
        internal bool IsDraining => _disposed && !_released;

        /// <summary>仅增持自己的活跃引用，不复制任何业务所有者身份。</summary>
        internal AddressablesHandleOwner<T> Retain() => Observe(_manager, BorrowedHandle);

        public void Dispose()
        {
            CheckThread();
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_handle.IsDone)
            {
                Release();
            }
            else
            {
                _handle.Completed += Drain;
            }
        }

        private void Drain(AsyncOperationHandle<T> completed)
        {
            CheckThread();
            if (!_released)
            {
                Release();
            }
        }

        private void Release()
        {
            var handle = _handle;
            _released = true;
            _handle = default;
            handle.Completed -= Drain;
            _manager.Release(handle);
        }

        private static void Validate(ResourceManager manager, AsyncOperationHandle<T> handle)
        {
            if (manager == null)
            {
                throw new ArgumentNullException(nameof(manager));
            }
            if (!handle.IsValid())
            {
                throw new ArgumentException("A live independently owned or observable Addressables handle is required.", nameof(handle));
            }
        }

        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _threadId)
            {
                throw new InvalidOperationException("Addressables reference ownership must be settled on its accepting Unity thread.");
            }
        }
    }
}
