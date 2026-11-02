namespace YLFramework.Compression
{
    /// <summary>
    /// 解压配额选项。各限制为 0 表示不限制，负数非法（解压入口抛 <see cref="System.ArgumentOutOfRangeException"/>）；
    /// 恰好等于上限允许，超过即拒绝。用于批量解压的预扫描与复制限制，不改变 zip 解析。
    /// 网络来源（热更、下载）建议使用 <see cref="ForNetworkDownloads"/> 预设。
    /// </summary>
    /// <remarks>只对带配额的批量解压入口生效，不约束单条目提取、字节数组读取或条目流；配额不提供来源认证。</remarks>
    public sealed class UnzipOptions
    {
        /// <summary>
        /// 文件元数据总大小及复制阶段累计处理字节数上限；跳过的已存在文件按元数据计入，0 = 不限制。
        /// </summary>
        public long MaxTotalUncompressedBytes
        {
            get; set;
        }

        /// <summary>
        /// 单条目解压后字节数上限；0 = 不限制。
        /// </summary>
        public long MaxEntryUncompressedBytes
        {
            get; set;
        }

        /// <summary>
        /// 条目总数上限（含目录条目）；0 = 不限制。
        /// </summary>
        public int MaxEntryCount
        {
            get; set;
        }

        /// <summary>
        /// 返回网络下载来源的推荐预设新实例：总量 4 GiB / 单条目 1 GiB / 10 万条目，可由调用方独立调整。
        /// </summary>
        public static UnzipOptions ForNetworkDownloads()
        {
            return new UnzipOptions
            {
                MaxTotalUncompressedBytes = 4L * 1024 * 1024 * 1024,
                MaxEntryUncompressedBytes = 1L * 1024 * 1024 * 1024,
                MaxEntryCount = 100000,
            };
        }
    }
}
