using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace YLFramework.Resource
{
    /// <summary>每段先认证再交付的独占可定位视图；native 卸载前不能释放。</summary>
    /// <remarks>
    /// 以锁串行处理原生读取线程的流访问，读取路径不访问 Unity 对象或应用密钥来源。
    /// 打开只认证首尾段，后续按需认证；完整明文长度与 SHA-256 比对须显式调用 ValidateAll。
    /// </remarks>
    internal sealed class ResourceDecryptStream : Stream
    {
        internal const long DataBufferBytes = ResourceBundleEnvelope.DataBufferBytes;
        internal const int NativeReadBufferSize = ResourceBundleEnvelope.NativeReadBufferSize;
        private readonly object _gate = new();
        private readonly Stream _input;
        private readonly bool _leaveOpen;
        private readonly ResourceBundleEnvelopeHeader _envelope;
        private readonly long _deliveryLength;
        private readonly byte[] _tinkHeader = new byte[40];
        private readonly byte[] _nonce = new byte[12];
        private readonly byte[] _cipher = new byte[ResourceBundleEnvelope.SegmentSize];
        private readonly byte[][] _plain = { new byte[ResourceBundleEnvelope.PlainCapacity], new byte[ResourceBundleEnvelope.PlainCapacity] };
        private readonly long[] _indices = { -1, -1 };
        private readonly int[] _lengths = new int[2];
        private CngSegmentAead _aead;
        private long _position;
        private int _nextVictim;
        private Exception _fault;
        private Exception _releaseFault;
        private Action<Exception> _faultNotification;
        private bool _hasNotifiedFault;
        private bool _isDisposed;
        private bool _isInputReleased;
        internal Exception Fault
        {
            get
            {
                lock (_gate)
                {
                    return _fault;
                }
            }
        }
        internal Exception ReleaseFault
        {
            get
            {
                lock (_gate)
                {
                    return _releaseFault;
                }
            }
        }
        internal long CngKeyObjectBytes
        {
            get
            {
                lock (_gate)
                {
                    return _aead?.KeyObjectBytes ?? 0;
                }
            }
        }
        internal bool IsCleanupComplete
        {
            get
            {
                lock (_gate)
                {
                    return _isDisposed && _aead == null && (_leaveOpen || _isInputReleased);
                }
            }
        }

        /// <summary>由创建输入的主体安装唯一故障接收器；只允许有界入队，不得等待主线程或访问 Unity、应用密钥来源。</summary>
        internal void SetFaultNotification(Action<Exception> notification)
        {
            if (notification == null)
            {
                throw new ArgumentNullException(nameof(notification));
            }

            lock (_gate)
            {
                if (_isDisposed)
                {
                    throw new ObjectDisposedException(nameof(ResourceDecryptStream));
                }

                if (_faultNotification != null)
                {
                    throw new InvalidOperationException("The content input already has a fault notification owner.");
                }

                _faultNotification = notification;
                NotifyFault();
            }
        }

        /// <summary>派生文件密钥、建立 CNG 后端并认证首尾段；构造失败不接管调用方输入流。</summary>
        internal ResourceDecryptStream(Stream input, ResourceBundleEnvelopeHeader envelope, byte[] key,
            bool leaveOpen, CancellationToken token)
        {
            this._input = input;
            this._envelope = envelope;
            this._leaveOpen = leaveOpen;
            _deliveryLength = input.Length;
            byte[] fileKey = null;
            try
            {
                token.ThrowIfCancellationRequested();
                input.Position = envelope.EncodedPrefix.Length;
                ResourceBundleEnvelope.ReadExact(input, _tinkHeader, 0, _tinkHeader.Length, token);
                if (_tinkHeader[0] != 40)
                {
                    throw new InvalidDataException("Expected AES256 RAW header length 40.");
                }

                fileKey = ResourceBundleEnvelope.DeriveKey(key, envelope.EncodedPrefix, _tinkHeader);
                _aead = new CngSegmentAead(fileKey);
                Array.Clear(fileKey, 0, fileKey.Length);
                GetSegment(0, token);
                if (envelope.SegmentCount > 1)
                {
                    GetSegment(envelope.SegmentCount - 1, token);
                }

                CheckLength();
                token.ThrowIfCancellationRequested();
            }
            catch (Exception error)
            {
                ClearData();
                if (_aead != null)
                {
                    try
                    {
                        _aead.Dispose();
                        _aead = null;
                    }
                    catch (Exception cleanup)
                    {
                        throw new IOException("Content open and native cleanup failed.", new AggregateException(error, cleanup));
                    }
                }
                if (error is OperationCanceledException)
                {
                    throw;
                }

                throw new IOException("Resource content open/authentication failed.", error);
            }
            finally
            {
                if (fileKey != null)
                {
                    Array.Clear(fileKey, 0, fileKey.Length);
                }
            }
        }

        /// <summary>仅在未释放且未锁定内容故障时允许读取。</summary>
        public override bool CanRead
        {
            get
            {
                lock (_gate)
                {
                    return !_isDisposed && _fault == null;
                }
            }
        }
        /// <summary>仅在未释放且未锁定内容故障时允许改变逻辑位置。</summary>
        public override bool CanSeek
        {
            get
            {
                lock (_gate)
                {
                    return !_isDisposed && _fault == null;
                }
            }
        }
        /// <summary>解密视图只读，始终不支持写入。</summary>
        public override bool CanWrite => false;
        /// <summary>封装声明的明文字节长度；本属性不扫描全部内容。</summary>
        public override long Length
        {
            get
            {
                lock (_gate)
                {
                    RequireUsable();
                    return _envelope.PlainLength;
                }
            }
        }
        /// <summary>明文视图的逻辑游标；设置位置不立即读取或认证对应段。</summary>
        public override long Position
        {
            get
            {
                lock (_gate)
                {
                    RequireUsable();
                    return _position;
                }
            }
            set => Seek(value, SeekOrigin.Begin);
        }

        /// <summary>按需认证完整段后复制明文，遇 EOF 返回实际读取量；内容错误永久锁定视图。</summary>
        /// <remarks>跨段读取若后段失败，先前已认证段可能已写入目标缓冲，整次调用仍抛出错误。</remarks>
        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                RequireUsable();
                if (buffer == null)
                {
                    throw new ArgumentNullException(nameof(buffer));
                }

                if (offset < 0 || count < 0)
                {
                    throw new ArgumentOutOfRangeException();
                }

                if (offset > buffer.Length - count)
                {
                    throw new ArgumentException("Read outside destination bounds.");
                }

                if (count == 0)
                {
                    return 0;
                }

                try
                {
                    CheckLength();
                    int total = 0;
                    while (total < count && _position < _envelope.PlainLength)
                    {
                        long index = (_position + 40) / ResourceBundleEnvelope.PlainCapacity;
                        long start = index == 0 ? 0 : index * ResourceBundleEnvelope.PlainCapacity - 40;
                        int within = (int)(_position - start);
                        int slot = GetSegment(index);
                        int take = Math.Min(count - total, _lengths[slot] - within);
                        if (take <= 0)
                        {
                            throw new InvalidDataException("Invalid authenticated segment mapping.");
                        }
                        // 槽的 index 只在完整 tag 成功后发布；坏段的任何字节均不复制给调用方。
                        Buffer.BlockCopy(_plain[slot], within, buffer, offset + total, take);
                        _position += take;
                        total += take;
                    }
                    return total;
                }
                catch (Exception error)
                {
                    throw Latch(error);
                }
            }
        }

        /// <summary>从已认证段读取一个明文字节并推进游标；达到明文末尾返回 -1。</summary>
        public override int ReadByte()
        {
            lock (_gate)
            {
                RequireUsable();
                try
                {
                    CheckLength();
                    if (_position >= _envelope.PlainLength)
                    {
                        return -1;
                    }

                    long index = (_position + 40) / ResourceBundleEnvelope.PlainCapacity;
                    long start = index == 0 ? 0 : index * ResourceBundleEnvelope.PlainCapacity - 40;
                    int slot = GetSegment(index);
                    int value = _plain[slot][(int)(_position - start)];
                    ++_position;
                    return value;
                }
                catch (Exception error)
                {
                    throw Latch(error);
                }
            }
        }

        /// <summary>按明文长度计算逻辑位置，拒绝负数与溢出，允许定位到末尾之后而不读取内容。</summary>
        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (_gate)
            {
                RequireUsable();
                long basis;
                switch (origin)
                {
                    case SeekOrigin.Begin:
                        basis = 0;
                        break;
                    case SeekOrigin.Current:
                        basis = _position;
                        break;
                    case SeekOrigin.End:
                        basis = _envelope.PlainLength;
                        break;
                    default:
                        throw new ArgumentException("Invalid seek origin.", nameof(origin));
                }
                long target;
                try
                {
                    target = checked(basis + offset);
                }
                catch (OverflowException error)
                {
                    throw new IOException("Seek offset overflow.", error);
                }
                if (target < 0)
                {
                    throw new IOException("Cannot seek before the beginning.");
                }

                _position = target;
                return target;
            }
        }

        /// <summary>认证全部段并核对明文总长度与 SHA-256；使用独立遍历，不推进逻辑游标。</summary>
        internal void ValidateAll(CancellationToken token)
        {
            lock (_gate)
            {
                RequireUsable();
                try
                {
                    using var hash = SHA256.Create();
                    long count = 0;
                    for (long index = 0; index < _envelope.SegmentCount; ++index)
                    {
                        token.ThrowIfCancellationRequested();
                        CheckLength();
                        int slot = GetSegment(index, token);
                        hash.TransformBlock(_plain[slot], 0, _lengths[slot], _plain[slot], 0);
                        count = checked(count + _lengths[slot]);
                    }
                    hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    if (count != _envelope.PlainLength || ResourceBundleEnvelope.Digest(hash.Hash) != _envelope.PlainSha256)
                    {
                        throw new InvalidDataException("Authenticated plaintext length/SHA-256 is inconsistent with its envelope.");
                    }

                    CheckLength();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    throw Latch(error);
                }
            }
        }

        /// <summary>复用两槽缓存或认证指定段，只有标签和输出长度都通过后才发布可读槽身份。</summary>
        private int GetSegment(long index, CancellationToken token = default)
        {
            for (int i = 0; i < _indices.Length; ++i)
            {
                if (_indices[i] == index)
                {
                    return i;
                }
            }

            if (index < 0 || index >= _envelope.SegmentCount)
            {
                throw new InvalidDataException("Invalid segment index.");
            }

            int slot = _nextVictim;
            _nextVictim = (_nextVictim + 1) % _plain.Length;
            _indices[slot] = -1;
            _lengths[slot] = 0;
            Array.Clear(_plain[slot], 0, _plain[slot].Length);
            long start = index == 0 ? 40 : checked(index * ResourceBundleEnvelope.SegmentSize);
            int cipherCount = (int)Math.Min(ResourceBundleEnvelope.SegmentSize - (index == 0 ? 40 : 0), _envelope.PayloadLength - start);
            if (cipherCount < 16)
            {
                throw new InvalidDataException("Incomplete authenticated segment.");
            }

            _input.Position = checked(_envelope.EncodedPrefix.Length + start);
            ResourceBundleEnvelope.ReadExact(_input, _cipher, 0, cipherCount, token);
            ResourceBundleEnvelope.SetNonce(_tinkHeader, index, index == _envelope.SegmentCount - 1, _nonce);
            int written = _aead.Decrypt(_cipher, cipherCount, _nonce, _plain[slot]);
            if (written != cipherCount - 16)
            {
                throw new CryptographicException("Unexpected authenticated plaintext size.");
            }

            _lengths[slot] = written;
            _indices[slot] = index;
            return slot;
        }

        /// <summary>检查底层交付长度未改变；不重新计算密文摘要，也不检测所有同长度改写。</summary>
        private void CheckLength()
        {
            if (_input.Length != _deliveryLength)
            {
                throw new InvalidDataException("Ciphertext length changed after authenticated open.");
            }
        }

        /// <summary>拒绝已释放或已永久锁定故障的视图，不通过后续读取自动恢复。</summary>
        private void RequireUsable()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(ResourceDecryptStream));
            }

            if (_fault != null)
            {
                throw new IOException("Resource content stream is permanently faulted.", _fault);
            }
        }

        /// <summary>保留首次内容错误、清除数据缓存并尝试关闭 CNG 后端，再通知所有者拒绝后续接纳。</summary>
        private IOException Latch(Exception error)
        {
            if (_fault == null)
            {
                _fault = error;
            }

            ClearData();
            if (_aead != null)
            {
                try
                {
                    _aead.Dispose();
                    _aead = null;
                }
                catch (Exception cleanup)
                {
                    _releaseFault = cleanup;
                }
            }

            NotifyFault();
            return new IOException("Resource content read failed; state is permanently faulted.", _fault);
        }

        /// <summary>在存在故障及接收器时最多通知一次；回调在当前持锁线程同步执行。</summary>
        private void NotifyFault()
        {
            if (_fault == null || _faultNotification == null || _hasNotifiedFault)
            {
                return;
            }

            _hasNotifiedFault = true;
            _faultNotification(_fault);
        }

        /// <summary>清除密文、明文和 nonce 缓冲，并使所有缓存槽身份失效。</summary>
        private void ClearData()
        {
            Array.Clear(_cipher, 0, _cipher.Length);
            Array.Clear(_nonce, 0, _nonce.Length);
            for (int i = 0; i < _plain.Length; ++i)
            {
                Array.Clear(_plain[i], 0, _plain[i].Length);
                _indices[i] = -1;
                _lengths[i] = 0;
            }
        }

        /// <summary>使视图失效并清除缓存，依序释放 CNG 与自有输入；释放失败保留原因并向外传播。</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_gate)
                {
                    _isDisposed = true;
                    ClearData();
                    try
                    {
                        if (_aead != null)
                        {
                            _aead.Dispose();
                            _aead = null;
                        }
                        if (!_leaveOpen && !_isInputReleased)
                        {
                            _input.Dispose();
                            _isInputReleased = true;
                        }
                    }
                    catch (Exception error)
                    {
                        _releaseFault = error;
                        throw;
                    }
                }
            }
            base.Dispose(disposing);
        }

        /// <summary>只检查视图仍可用；只读视图没有待刷出的写入内容。</summary>
        public override void Flush()
        {
            lock (_gate)
            {
                RequireUsable();
            }
        }
        /// <summary>始终拒绝修改明文长度。</summary>
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <summary>始终拒绝向解密视图写入。</summary>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
