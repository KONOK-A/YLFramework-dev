namespace YLFramework.Resource
{
    /// <summary>仅供 Legacy 作者数据读取的原序列化值，不表示运行时支持加密交付。</summary>
    public enum ResourceContentEncryption
    {
        /// <summary>保持原始Bundle文件路径，不执行内容解密。</summary>
        None,
        /// <summary>YL封装的Tink分段格式；当前仅支持Windows x64 CNG后端。</summary>
        Aes256GcmHkdfSegmentedV1
    }
}
