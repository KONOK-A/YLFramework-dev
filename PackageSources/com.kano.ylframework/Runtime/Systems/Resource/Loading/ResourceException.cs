using System;

namespace YLFramework.Resource
{
    /// <summary>资源域可区分的失败原因，用于请求终态与异常诊断。</summary>
    public enum ResourceError
    {
        /// <summary>显式配置或请求选项不满足约束。</summary>
        InvalidConfiguration,
        /// <summary>已有另一配置对象正在初始化或提供资源服务。</summary>
        ConfigurationConflict,
        /// <summary>传入地址或定位字符串格式无效。</summary>
        InvalidAddress,
        /// <summary>冻结目录中不存在请求的项目、地址或其他资源标识。</summary>
        AddressNotFound,
        /// <summary>请求类型、目录声明类型或实际来源结果不兼容。</summary>
        TypeMismatch,
        /// <summary>请求依赖的资源来源当前不可用。</summary>
        SourceUnavailable,
        /// <summary>来源、平台或资源种类不支持所请求的操作。</summary>
        Unsupported,
        /// <summary>相关生产或回收仍在途，当前入口不能同步取得或交付。</summary>
        InProgress,
        /// <summary>加载、结果交割或资源清理失败，详细原因保留在异常链中。</summary>
        LoadFailed,
        /// <summary>消费者或其内部工作已取消。</summary>
        Canceled,
        /// <summary>资源系统未就绪或所属资源会话已经失效。</summary>
        SessionUnavailable,
        /// <summary>所有权、版本操作或生命周期屏障暂时阻止本次操作。</summary>
        Busy,
        /// <summary>同路径并发场景无法唯一归属到本次原生加载。</summary>
        SceneIdentityAmbiguous,
        /// <summary>场景身份已过期、已卸载或加载后未存活。</summary>
        SceneUnloaded,
        /// <summary>场景激活门控超过期限，失败后仍须放行原生队列。</summary>
        ActivationTimedOut,
        /// <summary>版本操作依赖的源目录或持久化基线已经变化。</summary>
        StaleBase,
        /// <summary>资源版本准备、提交或恢复失败。</summary>
        UpdateFailed,
        /// <summary>应用未提供当前明确选择的内容key，不应因此隐式重下载或切换版本。</summary>
        ContentKeyUnavailable,
        /// <summary>当前密文认证失败，拒绝交付未经认证的字节。</summary>
        ContentAuthenticationFailed,
        /// <summary>内容编码或封装不受支持，不回退为明文输入。</summary>
        ContentFormatUnsupported,
        /// <summary>显式数据预算不足，须归还自有责任后显式重试。</summary>
        ContentBufferUnavailable
    }

    /// <summary>资源域失败，保留原始 cause 与调用地址/来源，不使用 null 伪装取得成功。</summary>
    public sealed class ResourceException : Exception
    {
        /// <summary>供调用方区分失败原因的资源错误类别。</summary>
        public ResourceError Kind
        {
            get;
        }
        /// <summary>发生失败的调用地址；不涉及具体资产或无法确定时为 null。</summary>
        public ResourceAddress? Address
        {
            get;
        }
        /// <summary>发生失败的资源来源标识；有意保留既有 API，不等同于基类异常的诊断来源字符串。</summary>
        public new ResourceSourceId? Source
        {
            get;
        }
        /// <summary>创建保留资源域上下文和原始异常链的错误。</summary>
        /// <param name="kind">资源域失败类别。</param>
        /// <param name="message">面向诊断的错误说明。</param>
        /// <param name="cause">原始原因，可为 null。</param>
        /// <param name="address">相关资源地址，可为 null。</param>
        /// <param name="source">相关来源标识，可为 null。</param>
        public ResourceException(ResourceError kind, string message, Exception cause = null,
            ResourceAddress? address = null, ResourceSourceId? source = null) : base(message, cause)
        {
            Kind = kind;
            Address = address;
            Source = source;
        }
    }
}
