using System;
using System.IO;
using System.Threading;

namespace YLFramework.Resource
{
    /// <summary>由原 BundleRecord 持有；只有确认 native 卸载后才允许释放流和预算。</summary>
    internal sealed class ResourceBundleInput : IDisposable
    {
        private readonly object _gate = new();
        private Stream _stream;
        private IDisposable _reservation;
        private Exception _releaseFault;
        private bool _isStreamReleased;
        /// <summary>供原生加载借用的原始或解密流；释放输入后拒绝访问。</summary>
        internal Stream Stream
        {
            get
            {
                lock (_gate)
                {
                    return _stream ?? throw new ObjectDisposedException(nameof(ResourceBundleInput));
                }
            }
        }
        /// <summary>本输入是否使用分段认证解密视图，而非原始 UnityFS 文件。</summary>
        internal bool IsEncrypted
        {
            get;
        }
        /// <summary>流引用与数据预算是否均已归还；仅开始释放不代表完成。</summary>
        internal bool IsDisposed
        {
            get
            {
                lock (_gate)
                {
                    return _stream == null && _reservation == null;
                }
            }
        }
        /// <summary>加密视图约定的数据缓冲字节数；不包括 CNG 对象或 Unity 原生读取缓冲。</summary>
        internal long DataBufferBytes => IsEncrypted ? ResourceDecryptStream.DataBufferBytes : 0;
        /// <summary>当前解密后端报告的 CNG 密钥对象字节数，不是进程内存总量。</summary>
        internal long CngKeyObjectBytes
        {
            get
            {
                lock (_gate)
                {
                    return _stream is ResourceDecryptStream decrypt ? decrypt.CngKeyObjectBytes : 0;
                }
            }
        }
        /// <summary>解密视图已锁定的读取或认证错误；原始输入不在此提供故障状态。</summary>
        internal Exception Fault
        {
            get
            {
                lock (_gate)
                {
                    return _stream is ResourceDecryptStream decrypt ? decrypt.Fault : null;
                }
            }
        }
        /// <summary>本输入或其解密视图记录的释放错误，供所有者保留未完成清理责任。</summary>
        internal Exception ReleaseFault
        {
            get
            {
                lock (_gate)
                {
                    return _releaseFault ?? (_stream is ResourceDecryptStream decrypt ? decrypt.ReleaseFault : null);
                }
            }
        }

        /// <summary>接管已成功构造的流及可选预算责任，不在构造器中重新校验内容。</summary>
        private ResourceBundleInput(Stream stream, bool encrypted, IDisposable reservation)
        {
            this._stream = stream;
            this._reservation = reservation;
            IsEncrypted = encrypted;
        }

        /// <summary>向加密视图安装故障通知；通知可能在原生读取线程执行，不能调用 Unity 或应用密钥来源。</summary>
        internal void SetFaultNotification(Action<Exception> notification)
        {
            if (notification == null)
            {
                throw new ArgumentNullException(nameof(notification));
            }

            lock (_gate)
            {
                if (_stream == null)
                {
                    throw new ObjectDisposedException(nameof(ResourceBundleInput));
                }

                if (_stream is ResourceDecryptStream decrypt)
                {
                    decrypt.SetFaultNotification(notification);
                }
            }
        }

        /// <summary>在调用方已校验交付长度与摘要的文件流上识别编码，并建立原始或认证解密输入。</summary>
        internal static ResourceBundleInput Open(FileStream verified, ResourceBundleContext context, byte[] key,
            IDisposable reservation = null, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var header = ResourceBundleEnvelope.Inspect(verified, context);
            if (header == null)
            {
                verified.Position = 0;
                return new ResourceBundleInput(verified, false, reservation);
            }
            // 构造失败时 verified 和 reservation 均仍归调用方，不能提前归还共享图的预算。
            var decrypt = ResourceBundleEnvelope.OpenDecrypt(verified, context, key, false, token);
            return new ResourceBundleInput(decrypt, true, reservation);
        }

        /// <summary>由所有者确认已无原生使用后调用，先释放流再归还预算；失败保留剩余责任并向外抛出。</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_stream == null && _reservation == null)
                {
                    return;
                }

                try
                {
                    if (!_isStreamReleased)
                    {
                        _stream.Dispose();
                        _isStreamReleased = true;
                    }
                    if (_reservation != null)
                    {
                        _reservation.Dispose();
                        _reservation = null;
                    }
                    _stream = null;
                }
                catch (Exception error)
                {
                    _releaseFault = error;
                    throw;
                }
            }
        }
    }
}
