using System;

namespace YLFramework.Resource
{
    /// <summary>项目的交付字节编码；不改变Catalog v1或资源加载所有权。</summary>
    public enum ResourceContentEncryption
    {
        /// <summary>保持原始Bundle文件路径，不执行内容解密。</summary>
        None,
        /// <summary>YL封装的Tink分段格式；当前仅支持Windows x64 CNG后端。</summary>
        Aes256GcmHkdfSegmentedV1
    }

    /// <summary>应用拥有密钥来源；只向调用方拥有的 32 字节缓冲复制指定密钥。false 表示缺失。</summary>
    public interface IResourceContentKeyProvider
    {
        /// <summary>在Unity主线程复制指定项目/KeyId的32字节key；不在native Stream或IO worker回调。</summary>
        /// <param name="projectId">明确选择的项目身份。</param>
        /// <param name="keyId">不可静默重新解释的key版本标识。</param>
        /// <param name="destination">框架拥有且会清除的确切32字节目标；不得持有此缓冲。</param>
        /// <returns>找到并完成复制返回true，缺失返回false；异常文本不得含秘密。</returns>
        bool TryCopyKey(string projectId, string keyId, byte[] destination);
    }

    /// <summary>启动时冻结的解密配置；不获取密钥、不预分配预算、不接管 provider 的生命周期。</summary>
    public sealed class ResourceDecryptionOptions
    {
        /// <summary>默认显式数据缓冲预算128MiB，不预分配整笔额度。</summary>
        public const long DefaultMaxBufferedBytes = 128L * 1024 * 1024;
        /// <summary>应用拥有的来源；框架不接管其鉴权、发放或生命周期。</summary>
        public IResourceContentKeyProvider KeyProvider
        {
            get;
        }
        /// <summary>共享输入及在途预留的数据字节上限；不含native读取缓冲、CNG句柄或进程总内存。</summary>
        public long MaxBufferedBytes
        {
            get;
        }

        /// <summary>冻结provider引用和正数预算；构造本身不取key、不初始化或创建Store。</summary>
        /// <param name="provider">应用负责生命周期的key来源。</param>
        /// <param name="maxBufferedBytes">显式数据预算；不足须释放责任后显式重试，不自动扩容/驱逐。</param>
        public ResourceDecryptionOptions(IResourceContentKeyProvider provider, long maxBufferedBytes = DefaultMaxBufferedBytes)
        {
            KeyProvider = provider ?? throw new ArgumentNullException(nameof(provider));
            if (maxBufferedBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBufferedBytes));
            }

            MaxBufferedBytes = maxBufferedBytes;
        }
    }
}
