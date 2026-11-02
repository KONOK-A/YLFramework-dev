using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using YLFramework.Singletons;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YLFramework.Http
{
    /// <summary>主线程轻量 HTTP 入口；每笔请求独占传输，不管理认证、重试或共享资源生产者。</summary>
    /// <remarks>
    /// 入口立即创建并尝试启动请求，原生超时初始为 5 秒，发送前钩子可调整。
    /// 参数、构造和配置错误记入返回请求；无有效执行会话时返回失效请求，不遗留待完成的传输。
    /// </remarks>
    public class HttpManager
    {
        private static readonly HashSet<SendWebRequest> _pendingRequests = new();
        private static bool _isEnding;

        /// <summary>安装执行会话重置和退出监听，使在途请求不会跨会话继续运行。</summary>
        static HttpManager()
        {
            SingletonSessionState.RegisterSessionReset(ResetSession);
            Application.quitting += OnExecutionEnded;
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
#endif
        }

        /// <summary>阻止实例化，所有请求均通过静态入口创建。</summary>
        private HttpManager()
        {
        }

        /// <summary>发送前同步配置。不得持有或释放借用的原生请求/Handler；异常使本请求失败。</summary>
        /// <remarks>
        /// 订阅者应在自己的生命周期结束时解除订阅；HTTP 会话重置时清空订阅。
        /// 初始及回调返回时附着的 Handler 归 HTTP；同一回调内创建并再次替换的中间 Handler 由配置者释放。
        /// </remarks>
        public static event Action<UnityWebRequest> PreWebRequest;

        /// <summary>原生完成后的同步借读观察（含协议/网络错误），第二参数为未缩放耗时（秒）。</summary>
        /// <remarks>
        /// Status 与响应快照已提交，原生对象仅在回调期间借用，不得持有或释放；取消和构造失败不触发此事件。
        /// 观察异常逐个报告，不改结果；基类 IsDone 及完成通知在资源释放后才更新，完成订阅应读取请求快照。
        /// </remarks>
        public static event Action<UnityWebRequest, float> PostWebRequest;

        /// <summary>当前 HTTP 会话是否仍接受执行，不代表任一请求已经成功启动。</summary>
        internal static bool CanRun => !_isEnding && SingletonSessionState.CanRunExecution;
        /// <summary>登记请求，以便执行会话结束时收束仍在途的工作。</summary>
        internal static void Register(SendWebRequest request) => _pendingRequests.Add(request);
        /// <summary>在通知外部完成前移除请求的在途身份。</summary>
        internal static void Unregister(SendWebRequest request) => _pendingRequests.Remove(request);
        /// <summary>取得本轮发送前配置委托，由请求负责顺序调用。</summary>
        internal static Action<UnityWebRequest> Before => PreWebRequest;
        /// <summary>取得本轮原生完成观察委托，由请求隔离各订阅的异常。</summary>
        internal static Action<UnityWebRequest, float> After => PostWebRequest;

        /// <summary>先关闭接受入口，再对在途集合快照逐个提交失效终态。</summary>
        private static void OnExecutionEnded()
        {
            _isEnding = true;
            // 请求自行先撤销在途身份再通知；通知重入不能向正在退出的 HTTP 会话增加工作。
            foreach (var request in new List<SendWebRequest>(_pendingRequests))
            {
                request.Invalidate();
            }
        }

        /// <summary>收尾旧请求并清空静态钩子，再允许新执行会话接收请求。</summary>
        private static void ResetSession()
        {
            OnExecutionEnded();
            PreWebRequest = null;
            PostWebRequest = null;
            _isEnding = false;
        }

#if UNITY_EDITOR
        /// <summary>在退出编辑或运行阶段时关闭旧 HTTP 会话，避免原生请求滞留到下一轮。</summary>
        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.ExitingEditMode ||
                state == PlayModeStateChange.EnteredEditMode)
            {
                OnExecutionEnded();
            }
        }
#endif

        /// <summary>GET 参数保留原 query/fragment，键和值分别编码；null 值按空值，数值使用 InvariantCulture。</summary>
        /// <param name="url">绝对 HTTP 或 HTTPS 地址。</param>
        /// <param name="param">追加的查询参数；null 或空字典不追加。</param>
        /// <param name="headers">发送前设置的请求头；允许为 null。</param>
        public static SendWebRequest Get(string url, Dictionary<string, object> param, Dictionary<string, string> headers)
            => Create(operation => Build(operation, QueryUrl(url, param), UnityWebRequest.kHttpVerbGET, null, null), headers);

        /// <summary>以 UTF-8 application/x-www-form-urlencoded 提交表单；null 字段值按空字符串。</summary>
        /// <param name="url">绝对 HTTP 或 HTTPS 地址。</param>
        /// <param name="param">表单字段；null 表示空表单。</param>
        /// <param name="headers">附加请求头，可覆盖初始 Content-Type；允许为 null。</param>
        public static SendWebRequest PostForm(string url, Dictionary<string, string> param, Dictionary<string, string> headers)
            => Create(operation =>
            {
                var body = new StringBuilder();
                if (param != null)
                {
                    foreach (var item in param)
                    {
                        if (body.Length != 0)
                        {
                            body.Append('&');
                        }

                        body.Append(UnityWebRequest.EscapeURL(item.Key, Encoding.UTF8)).Append('=')
                            .Append(UnityWebRequest.EscapeURL(item.Value ?? string.Empty, Encoding.UTF8));
                    }
                }

                return Build(operation, url, UnityWebRequest.kHttpVerbPOST, body.ToString(), "application/x-www-form-urlencoded; charset=utf-8");
            }, headers);

        /// <summary>将字典序列化为 JSON（null 字典为 JSON null），以 UTF-8 提交。</summary>
        /// <param name="url">绝对 HTTP 或 HTTPS 地址。</param>
        /// <param name="param">待序列化的请求体字典。</param>
        /// <param name="headers">附加请求头，可覆盖初始 Content-Type；允许为 null。</param>
        public static SendWebRequest PostBody(string url, Dictionary<string, object> param, Dictionary<string, string> headers)
            => Create(operation => Build(operation, url, UnityWebRequest.kHttpVerbPOST, JsonConvert.SerializeObject(param), "application/json; charset=utf-8"), headers);

        /// <summary>原样提交 JSON 字符串；null 字符串为参数错误，不进行 JSON 内容校验。</summary>
        /// <param name="url">绝对 HTTP 或 HTTPS 地址。</param>
        /// <param name="jsonParam">待按 UTF-8 编码的原始请求体，不能为 null。</param>
        /// <param name="headers">附加请求头，可覆盖初始 Content-Type；允许为 null。</param>
        public static SendWebRequest PostBody(string url, string jsonParam, Dictionary<string, string> headers)
            => Create(operation => Build(operation, url, UnityWebRequest.kHttpVerbPOST,
                jsonParam ?? throw new ArgumentNullException(nameof(jsonParam)), "application/json; charset=utf-8"), headers);

        /// <summary>以 PUT 提交字典的 UTF-8 JSON。</summary>
        /// <param name="url">绝对 HTTP 或 HTTPS 地址。</param>
        /// <param name="param">待序列化的请求体字典；null 序列化为 JSON null。</param>
        /// <param name="headers">附加请求头，可覆盖初始 Content-Type；允许为 null。</param>
        public static SendWebRequest Put(string url, Dictionary<string, object> param, Dictionary<string, string> headers)
            => Create(operation => Build(operation, url, UnityWebRequest.kHttpVerbPUT, JsonConvert.SerializeObject(param), "application/json; charset=utf-8"), headers);

        /// <summary>以 DELETE 提交字典的 UTF-8 JSON。</summary>
        /// <param name="url">绝对 HTTP 或 HTTPS 地址。</param>
        /// <param name="param">待序列化的请求体字典；null 序列化为 JSON null。</param>
        /// <param name="headers">附加请求头，可覆盖初始 Content-Type；允许为 null。</param>
        public static SendWebRequest Delete(string url, Dictionary<string, object> param, Dictionary<string, string> headers)
            => Create(operation => Build(operation, url, UnityWebRequest.kHttpVerbDELETE, JsonConvert.SerializeObject(param), "application/json; charset=utf-8"), headers);

        /// <summary>在片段标识之前追加编码后的查询项，保留地址原有查询和片段。</summary>
        private static string QueryUrl(string url, Dictionary<string, object> parameters)
        {
            if (parameters == null || parameters.Count == 0)
            {
                return url;
            }

            if (url == null)
            {
                throw new ArgumentNullException(nameof(url));
            }

            var fragmentIndex = url.IndexOf('#');
            var fragment = fragmentIndex >= 0 ? url.Substring(fragmentIndex) : string.Empty;
            var address = fragmentIndex >= 0 ? url.Substring(0, fragmentIndex) : url;
            var builder = new StringBuilder(address);
            var hasQuery = address.IndexOf('?') >= 0;
            foreach (var item in parameters)
            {
                if (!hasQuery)
                {
                    builder.Append('?');
                }
                else if (builder[builder.Length - 1] != '?' && builder[builder.Length - 1] != '&')
                {
                    builder.Append('&');
                }

                builder.Append(Uri.EscapeDataString(item.Key)).Append('=')
                    .Append(Uri.EscapeDataString(Convert.ToString(item.Value, CultureInfo.InvariantCulture) ?? string.Empty));
                hasQuery = true;
            }
            return builder.Append(fragment).ToString();
        }

        /// <summary>校验地址后创建传输并立即移交所有权，按需配置独占下载及上传 Handler。</summary>
        private static UnityWebRequest Build(SendWebRequest operation, string url, string method, string body, string contentType)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                throw new ArgumentException("HTTP requires an absolute http/https URI.", nameof(url));
            }

            var request = new UnityWebRequest(url, method);
            operation.TakeOwnership(request);
            request.downloadHandler = operation.Own(new DownloadHandlerBuffer());
            if (body != null)
            {
                request.uploadHandler = operation.Own(new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)));
                request.SetRequestHeader("Content-Type", contentType);
                request.useHttpContinue = false;
            }
            return request;
        }

        /// <summary>统一设置初始超时与请求头后启动，将构造和配置异常转为已结束的失败请求。</summary>
        private static SendWebRequest Create(Func<SendWebRequest, UnityWebRequest> factory, Dictionary<string, string> headers)
        {
            var operation = new SendWebRequest();
            try
            {
                var request = factory(operation);
                request.timeout = 5;
                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        request.SetRequestHeader(header.Key, header.Value);
                    }
                }

                operation.Start();
            }
            catch (Exception exception)
            {
                operation.Fail(exception);
            }
            return operation;
        }

        /// <summary>内部发送入口接管全部原生资源，不允许外部复用同一传输。</summary>
        internal static SendWebRequest SendWebRequest(UnityWebRequest request)
            => Create(operation =>
            {
                operation.TakeOwnership(request);
                return request;
            }, null);
    }
}
