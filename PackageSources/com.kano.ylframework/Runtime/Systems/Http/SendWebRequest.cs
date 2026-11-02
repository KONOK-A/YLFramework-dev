using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using YLFramework.Singletons;
using CoroutineStarter = YLFramework.Async.CoroutineStarter;

namespace YLFramework.Http
{
    /// <summary>HTTP 请求的业务状态；Pending 之外的值均为不可再改写的终态。</summary>
    public enum HttpRequestStatus
    {
        /// <summary>尚未结束，不能据此读取最终响应。</summary>
        Pending,
        /// <summary>原生传输成功，响应快照已提交。</summary>
        Succeeded,
        /// <summary>构造、配置、发送或响应处理失败，也包含原生协议与网络错误。</summary>
        Failed,
        /// <summary>由调用方取消，仅停止本请求拥有的传输。</summary>
        Canceled,
        /// <summary>执行会话不可用或无法取得协程宿主，请求不再继续。</summary>
        Invalidated
    }

    /// <summary>主线程 HTTP 请求，独占原生传输；在接受线程上终态后 Abort 无副作用，协程等待及完成订阅沿用 Foundation。</summary>
    /// <remarks>完成订阅执行前先释放传输及接管的 Handler；IsDone 只表示结束，成功与否应检查 Status。</remarks>
    public sealed class SendWebRequest : YLFramework.Async.CustomAsyncOperation<SendWebRequest>
    {
        /// <summary>当前业务状态；先提交终态，再执行后置观察、资源释放和基类完成通知。</summary>
        public HttpRequestStatus Status
        {
            get; private set;
        }
        /// <summary>构造、配置、发送或响应快照处理的原始异常；协议/网络错误由 Error 和 NativeResult 表达。</summary>
        public Exception Exception
        {
            get; private set;
        }
        /// <summary>已提交快照中的原生结果；未取得快照时仍为默认值，不能替代 Status 判断终态。</summary>
        public UnityWebRequest.Result NativeResult
        {
            get; private set;
        }
        /// <summary>已提交快照中的 HTTP 状态码；未取得响应时为 0。</summary>
        public long ResponseCode
        {
            get; private set;
        }
        /// <summary>独立响应快照；协议失败也保留可读内容，取消/未发送时可能为 null。</summary>
        public string Text
        {
            get; private set;
        }
        /// <summary>从下载 Handler 复制的响应字节；释放传输后仍可读取，但返回数组本身允许调用方修改。</summary>
        public byte[] Data
        {
            get; private set;
        }
        /// <summary>脱离原生请求的只读响应头快照；未取得响应头时为 null。</summary>
        public IReadOnlyDictionary<string, string> ResponseHeaders
        {
            get; private set;
        }
        /// <summary>从创建请求到提交终态的未缩放耗时，单位为秒；进行中不实时更新。</summary>
        public float ElapsedSeconds
        {
            get; private set;
        }

        private UnityWebRequest _transport;
        private UnityWebRequestAsyncOperation _nativeOperation;
        private Coroutine _coroutine;
        private readonly HashSet<IDisposable> _handlers = new();
        private readonly object _session = SingletonSessionState.ExecutionSession;
        private readonly int _threadId = Thread.CurrentThread.ManagedThreadId;
        private readonly double _startedAt = Time.realtimeSinceStartupAsDouble;
        private bool _isTerminal;

        /// <summary>捕获接受线程与执行会话，并登记在途身份，便于退出时统一收尾。</summary>
        internal SendWebRequest() => HttpManager.Register(this);

        /// <summary>接管待释放的 Handler；若请求已经结束，立即释放传入对象并拒绝继续配置。</summary>
        internal T Own<T>(T handler) where T : IDisposable
        {
            if (_isTerminal)
            {
                handler.Dispose();
                throw new InvalidOperationException("HTTP request has already ended.");
            }
            _handlers.Add(handler);
            return handler;
        }

        /// <summary>接管原生传输及其当前 Handler；请求已经结束时释放传入传输并拒绝接管。</summary>
        internal void TakeOwnership(UnityWebRequest request)
        {
            if (_isTerminal)
            {
                request?.Dispose();
                throw new InvalidOperationException("HTTP request has already ended.");
            }
            _transport = request ?? throw new ArgumentNullException(nameof(request));
            CaptureHandlers();
        }

        /// <summary>记住当前附着的 Handler，使配置替换后的旧对象仍能在收尾时释放。</summary>
        private void CaptureHandlers()
        {
            if (_transport.downloadHandler != null)
            {
                _handlers.Add(_transport.downloadHandler);
            }

            if (_transport.uploadHandler != null)
            {
                _handlers.Add(_transport.uploadHandler);
            }

            if (_transport.certificateHandler != null)
            {
                _handlers.Add(_transport.certificateHandler);
            }
        }

        /// <summary>本请求是否仍属于可接受工作的当前执行会话。</summary>
        private bool IsCurrent => ReferenceEquals(_session, SingletonSessionState.ExecutionSession) && HttpManager.CanRun;

        /// <summary>在当前会话启动发送协程；会话或宿主不可用时转入失效终态。</summary>
        internal void Start()
        {
            if (!IsCurrent)
            {
                Invalidate();
                return;
            }
            var handle = CoroutineStarter.Start(Run());
            // 同步首步可取消/退出/完成；不能把返回的旧句柄重新挂到已收尾请求。
            if (_isTerminal)
            {
                CoroutineStarter.Stop(handle);
            }
            else if (handle == null)
            {
                Invalidate();
            }
            else
            {
                _coroutine = handle;
            }
        }

        /// <summary>顺序执行发送前配置并发起原生传输，将配置与发送异常收束为请求失败。</summary>
        private IEnumerator Run()
        {
            Exception failure = null;
            try
            {
                var before = HttpManager.Before;
                if (before != null)
                {
                    foreach (Action<UnityWebRequest> callback in before.GetInvocationList())
                    {
                        if (_isTerminal || !IsCurrent)
                        {
                            break;
                        }

                        try
                        {
                            callback(_transport);
                        }
                        finally
                        {
                            if (!_isTerminal)
                            {
                                CaptureHandlers();
                            }
                        }
                    }
                }

                if (!_isTerminal && IsCurrent)
                {
                    _nativeOperation = _transport.SendWebRequest();
                    // 原生完成独立于协程宿主；宿主停止等待不能遗失 HTTP 的资源收尾。
                    _nativeOperation.completed += OnNativeCompleted;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            if (failure != null)
            {
                Fail(failure);
            }

            if (_isTerminal)
            {
                yield break;
            }

            if (!IsCurrent)
            {
                Invalidate();
                yield break;
            }
            yield return _nativeOperation;
            OnNativeCompleted(_nativeOperation);
        }

        /// <summary>读取原生结果到局部快照，确认未被重入取消后再一次提交响应与终态。</summary>
        private void OnNativeCompleted(AsyncOperation operation)
        {
            if (_isTerminal)
            {
                return;
            }

            if (!IsCurrent)
            {
                Invalidate();
                return;
            }
            try
            {
                var request = _transport;
                var result = request.result;
                var code = request.responseCode;
                var responseHeaders = request.GetResponseHeaders();
                var nativeError = request.error;
                var handler = request.downloadHandler;
                string text = null;
                byte[] data = null;
                if (handler != null)
                {
                    // 自定义 Handler 的虚方法可同步取消/退出，不能边读取边写入可观察结果。
                    text = handler.text;
                    if (_isTerminal)
                    {
                        return;
                    }

                    if (!IsCurrent)
                    {
                        Invalidate();
                        return;
                    }
                    data = handler.data;
                    if (_isTerminal)
                    {
                        return;
                    }

                    if (!IsCurrent)
                    {
                        Invalidate();
                        return;
                    }
                    if (data != null)
                    {
                        data = (byte[])data.Clone();
                    }
                }
                NativeResult = result;
                ResponseCode = code;
                ResponseHeaders = responseHeaders == null ? null : new ReadOnlyDictionary<string, string>(responseHeaders);
                Text = text;
                Data = data;
                Finish(result == UnityWebRequest.Result.Success ? HttpRequestStatus.Succeeded : HttpRequestStatus.Failed,
                    nativeError, null, true);
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        /// <summary>保存原始异常并结束请求；异常自身无法格式化时仍保留可用错误描述。</summary>
        internal void Fail(Exception exception)
        {
            if (_isTerminal)
            {
                return;
            }

            string message;
            try
            {
                message = exception.ToString();
            }
            catch
            {
                message = exception.GetType().FullName + " (exception formatting failed).";
            }
            Finish(HttpRequestStatus.Failed, message, exception, false);
        }
        /// <summary>结束不再属于有效执行会话的请求，跳过原生完成后的观察钩子。</summary>
        internal void Invalidate() => Finish(HttpRequestStatus.Invalidated, "HTTP execution session is unavailable.", null, false);

        /// <summary>只取消自己拥有的 HTTP 传输；不保证撤销服务器已执行的副作用。</summary>
        /// <remarks>必须在创建请求的接受线程调用，包括请求已结束时；同线程重复取消不改写既有终态。</remarks>
        /// <exception cref="InvalidOperationException">调用线程不是请求的接受线程。</exception>
        public override void Abort()
        {
            if (Thread.CurrentThread.ManagedThreadId != _threadId)
            {
                throw new InvalidOperationException("HTTP Abort must run on its accepting thread.");
            }

            Finish(HttpRequestStatus.Canceled, "request abort!", null, false);
        }

        /// <summary>仅首次提交终态并撤销在途身份，按需借读观察后释放原生资源，最后通知基类完成。</summary>
        private void Finish(HttpRequestStatus status, string error, Exception exception, bool observe)
        {
            if (_isTerminal)
            {
                return;
            }

            _isTerminal = true;
            Status = status;
            Exception = exception;
            ElapsedSeconds = (float)(Time.realtimeSinceStartupAsDouble - _startedAt);
            HttpManager.Unregister(this);
            if (_nativeOperation != null)
            {
                _nativeOperation.completed -= OnNativeCompleted;
            }

            var handle = _coroutine;
            _coroutine = null;
            CoroutineStarter.Stop(handle);
            try
            {
                if (observe)
                {
                    var after = HttpManager.After;
                    if (after != null)
                    {
                        foreach (Action<UnityWebRequest, float> callback in after.GetInvocationList())
                        {
                            try
                            {
                                callback(_transport, ElapsedSeconds);
                            }
                            catch (Exception notificationError)
                            {
                                Debug.LogException(notificationError);
                            }
                            finally
                            {
                                CaptureHandlers();
                            }
                        }
                    }
                }
            }
            finally
            {
                ReleaseTransport();
                // 基类保留旧事件先于注册回调的历史多播/中断语义；异常只报告，不反写 HTTP 终态。
                try
                {
                    Complete(error);
                }
                catch (Exception notificationError)
                {
                    Debug.LogException(notificationError);
                }
            }
        }

        /// <summary>停止未完成传输并释放请求及所有接管过的 Handler，逐项报告释放异常。</summary>
        private void ReleaseTransport()
        {
            if (_transport != null)
            {
                CaptureHandlers();
            }

            var request = _transport;
            _transport = null;
            _nativeOperation = null;
            if (request != null)
            {
                try
                {
                    if (!request.isDone)
                    {
                        request.Abort();
                    }
                }
                catch (Exception cleanupError)
                {
                    Debug.LogException(cleanupError);
                }
                finally
                {
                    // 显式释放所有接管过的 Handler，包含前置配置替换的旧对象；忽略外部修改的 Dispose 标志。
                    request.disposeDownloadHandlerOnDispose = false;
                    request.disposeUploadHandlerOnDispose = false;
                    request.disposeCertificateHandlerOnDispose = false;
                    try
                    {
                        request.Dispose();
                    }
                    catch (Exception cleanupError)
                    {
                        Debug.LogException(cleanupError);
                    }
                }
            }
            foreach (var handler in _handlers)
            {
                try
                {
                    handler.Dispose();
                }
                catch (Exception cleanupError)
                {
                    Debug.LogException(cleanupError);
                }
            }
            _handlers.Clear();
        }
    }
}
