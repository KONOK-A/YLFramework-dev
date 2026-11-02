using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEngine.Networking;

namespace YLFramework.Resource
{
    /// <summary>单个有界GET。由ResourceSystem现有主线程tick驱动；Dispose同步撤销私有UWR和文件句柄。</summary>
    internal sealed partial class ResourceTransfer : IEnumerator, IDisposable
    {
        private readonly ResourceRemoteSource _source;
        private readonly Uri _uri;
        private readonly long _length;
        private readonly string _digest;
        private readonly long _maximum;
        private readonly ArtifactStore.DownloadPart _part;
        private readonly bool _deferPartVerification;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Random _random = new();
        private UnityWebRequest _request;
        private UnityWebRequestAsyncOperation _native;
        private Sink _sink;
        private int _attempt;
        private double _started;
        private double _retryAt;
        private bool _isDone;
        internal byte[] Bytes
        {
            get; private set;
        }
        internal float Progress => _length <= 0 ? 0 : (float)(_sink?.Written ?? _part?.Length ?? 0) / _length;
        /// <summary>当前无嵌套等待对象；推进者按主线程 tick 继续轮询。</summary>
        public object Current => null;

        /// <summary>绑定来源根内的有界 GET、可选摘要及分片责任，尚不发送请求。</summary>
        internal ResourceTransfer(ResourceRemoteSource source, Uri uri, long length = 0, string digest = null, long maximum = ResourceReleaseSignature.MaximumBytes,
            ArtifactStore.DownloadPart part = null, bool deferPartVerification = false)
        {
            this._source = source;
            this._uri = uri;
            this._length = length;
            this._digest = digest;
            this._maximum = length > 0 ? length : maximum;
            this._part = part;
            this._deferPartVerification = deferPartVerification;
            if (uri.Scheme != "https" || !source.Endpoint.IsBaseOf(uri) || maximum < 1 || length < 0)
            {
                throw new ArgumentException("An owned bounded HTTPS transfer is required.");
            }
        }

        /// <summary>推进请求、超时和有界重试，按传输模式执行响应检查并在失败时抛出错误。</summary>
        /// <remarks>分片 hash 可按配置延后到内容 IO；传输结束本身不证明版本可挂载或已获签名授权。</remarks>
        public bool MoveNext()
        {
            if (_isDone)
            {
                return false;
            }

            if (_request == null)
            {
                if (_clock.Elapsed.TotalSeconds < _retryAt)
                {
                    return true;
                }

                if (_part != null && _part.Length == _length)
                {
                    if (_deferPartVerification)
                    {
                        _isDone = true;
                        return false;
                    }
                    try
                    {
                        _part.Verify();
                        _isDone = true;
                        return false;
                    }
                    catch (InvalidDataException)
                    {
                        _part.Reset();
                    }
                }
                Start();
            }
            if (!_native.isDone)
            {
                if (_clock.Elapsed.TotalSeconds - _started < _source.TimeoutSeconds && _clock.Elapsed.TotalSeconds - _sink.LastData < _source.IdleTimeoutSeconds)
                {
                    return true;
                }

                _request.Abort();
                return Failure(new IOException("Release GET timed out without completing."), true);
            }
            _sink.Close();
            if (_sink.Error is ResourceRangeException rangeError)
            {
                _part?.Reset();
                return Failure(rangeError, true);
            }
            if (_sink.Error != null)
            {
                var error = _sink.Error;
                Close();
                _isDone = true;
                throw error;
            }
            if (_request.responseCode == 416 && _part != null)
            {
                _part.Reset();
                return Failure(new ResourceRangeException(), true);
            }
            // HTTP 错误只为连接中断、408、429 及 5xx 开放重试；完整性错误不因此获得重试资格。
            if (_request.result != UnityWebRequest.Result.Success || _request.responseCode != 200 && _request.responseCode != 206)
            {
                return Failure(new IOException("Release GET failed (HTTP " + _request.responseCode + ", " + _request.result + ")."),
                    _request.result == UnityWebRequest.Result.ConnectionError && (_request.responseCode == 0 || _request.responseCode == 200 || _request.responseCode == 206) ||
                    _request.responseCode == 408 || _request.responseCode == 429 || _request.responseCode >= 500 && _request.responseCode <= 599);
            }

            try
            {
                _sink.ValidateHeaders(); // 空body也不得绕过响应合同。
                _sink.Close();
                if (_length > 0 && _sink.Written != _length || _sink.Written == 0)
                {
                    throw new IOException("Release GET ended with an incomplete length.");
                }

                if (_part != null)
                {
                    if (!_deferPartVerification)
                    {
                        _part.Verify();
                    }
                }
                else
                {
                    Bytes = _sink.Bytes();
                    if (_digest != null && ResourceCatalogCodec.Digest(Bytes) != _digest)
                    {
                        throw new InvalidDataException("Release response digest mismatch.");
                    }
                }
                _isDone = true;
                Close();
                return false;
            }
            catch (ResourceRangeException error)
            {
                _sink.Close();
                _part?.Reset();
                return Failure(error, true);
            }
            catch (InvalidDataException)
            {
                Close();
                _part?.Reset();
                _isDone = true;
                throw;
            }
            catch (IOException error)
            {
                return Failure(error, true);
            }
        }

        // 仅在本地分片具备强 ETag 时请求 Range/If-Range；禁用重定向并要求 identity 字节表示。
        private void Start()
        {
            _attempt++;
            var offset = _part?.Length ?? 0;
            var validator = _part?.Validator;
            if (offset > 0 && !ResourceDownloadResponse.StrongETag(validator))
            {
                _part.Reset();
                offset = 0;
            }
            _request = new UnityWebRequest(_uri.AbsoluteUri, "GET") { redirectLimit = 0, timeout = _source.TimeoutSeconds };
            try
            {
                _request.certificateHandler = _source.CreateCertificateHandler();
                _request.SetRequestHeader("Accept-Encoding", "identity");
                if (offset > 0)
                {
                    _request.SetRequestHeader("Range", "bytes=" + offset.ToString(CultureInfo.InvariantCulture) + "-");
                    _request.SetRequestHeader("If-Range", validator);
                }
                _sink = new Sink(_request, _length, _maximum, offset, validator, _part, _clock);
                _request.downloadHandler = _sink;
                _started = _clock.Elapsed.TotalSeconds;
                _native = _request.SendWebRequest();
            }
            catch
            {
                Close();
                throw;
            }
        }

        // 关闭当前尝试，再按退避、抖动及有界 Retry-After 排定重试；耗尽次数则终止。
        private bool Failure(Exception error, bool retry)
        {
            var delay = Math.Min(8, .25 * Math.Pow(2, _attempt - 1)) + _random.NextDouble() * .1;
            var retryAfter = _request?.GetResponseHeader("Retry-After");
            if (ResourceDownloadResponse.Number(retryAfter, out var seconds))
            {
                delay = Math.Max(delay, Math.Min(10, seconds));
            }
            else if (DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            {
                delay = Math.Max(delay, Math.Min(10, Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds)));
            }

            Close();
            if (!retry || _attempt >= _source.Attempts)
            {
                _isDone = true;
                throw error;
            }
            _retryAt = _clock.Elapsed.TotalSeconds + delay;
            return true;
        }

        // 撤销尚未完成的私有请求并关闭接收流，释放本次尝试的 UWR 资源。
        private void Close()
        {
            try
            {
                if (_request != null && _native != null && !_native.isDone)
                {
                    _request.Abort();
                }

                _sink?.Close();
            }
            finally
            {
                _request?.Dispose();
                _request = null;
                _native = null;
                _sink = null;
            }
        }
        /// <summary>终止推进并同步关闭当前私有请求与写入流，不删除已归属的可重用分片。</summary>
        public void Dispose()
        {
            _isDone = true;
            Close();
        }
        /// <summary>不支持复位已绑定的传输；调用会抛出 NotSupportedException。</summary>
        public void Reset() => throw new NotSupportedException();

        /// <summary>在响应头确认后将有界数据写入内存或分片，保存回调中产生的 IO 错误。</summary>
        private sealed class Sink : DownloadHandlerScript
        {
            private readonly UnityWebRequest _request;
            private readonly long _expected;
            private readonly long _maximum;
            private readonly long _offset;
            private readonly string _validator;
            private readonly ArtifactStore.DownloadPart _part;
            private readonly Stopwatch _clock;
            private Stream _output;
            private readonly MemoryStream _memory;
            private bool _hasValidatedHeaders;
            private long _discarded;
            internal Exception Error
            {
                get; private set;
            }
            internal long Written
            {
                get; private set;
            }
            internal double LastData
            {
                get; private set;
            }

            /// <summary>保存本次响应合同，并为非分片传输分配私有内存接收区。</summary>
            internal Sink(UnityWebRequest request, long expected, long maximum, long offset, string validator, ArtifactStore.DownloadPart part, Stopwatch clock)
                : base(new byte[64 * 1024])
            {
                this._request = request;
                this._expected = expected;
                this._maximum = maximum;
                this._offset = offset;
                this._validator = validator;
                this._part = part;
                this._clock = clock;
                if (part == null)
                {
                    _memory = new MemoryStream();
                }

                LastData = clock.Elapsed.TotalSeconds;
            }

            /// <summary>首次写入前确认完整响应或续传合同，再打开对应偏移的接收流。</summary>
            internal void ValidateHeaders()
            {
                if (_hasValidatedHeaders)
                {
                    return;
                }

                var start = ResourceDownloadResponse.Offset(_request.responseCode, _offset, _expected, _validator, _request.GetResponseHeader);
                var size = _request.GetResponseHeader("Content-Length");
                if (size != null && (!ResourceDownloadResponse.Number(size, out var count) || count > _maximum - start))
                {
                    throw new InvalidDataException("Release response exceeds its byte limit.");
                }

                _output = _part == null ? _memory : _part.OpenWrite(start, _request.GetResponseHeader("ETag"));
                Written = start;
                _hasValidatedHeaders = true;
            }

            /// <summary>接收并限制响应字节；非成功状态仅有界丢弃，写入失败则保存错误并停止接收。</summary>
            /// <param name="data">Unity 提供的本次数据缓冲。</param>
            /// <param name="dataLength">本次缓冲中的有效字节数。</param>
            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                try
                {
                    LastData = _clock.Elapsed.TotalSeconds;
                    if (_request.responseCode != 200 && _request.responseCode != 206)
                    {
                        _discarded += dataLength;
                        return _discarded <= ResourceReleaseSignature.MaximumBytes;
                    }
                    ValidateHeaders();
                    if (dataLength < 0 || dataLength > _maximum - Written)
                    {
                        throw new InvalidDataException("Release response exceeded its byte limit.");
                    }

                    _output.Write(data, 0, dataLength);
                    Written += dataLength;
                    return true;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException)
                {
                    Error = error;
                    return false;
                }
            }

            /// <summary>复制内存模式已收到的响应体；分片模式不使用此入口。</summary>
            internal byte[] Bytes() => _memory.ToArray();
            /// <summary>持久刷新并关闭文件接收流，内存响应保留到传输读取。</summary>
            internal void Close()
            {
                if (_output == null || ReferenceEquals(_output, _memory))
                {
                    return;
                }

                try
                {
                    ((FileStream)_output).Flush(true);
                }
                finally
                {
                    _output.Dispose();
                    _output = null;
                }
            }
        }
    }
}
