using System;

namespace YLFramework.Resource
{
    /// <summary>图片的有界输入与闲置策略。缓存预算是无 mip RGBA 像素估算，不是 Profiler native memory。</summary>
    public sealed class ResourceImageOptions
    {
        internal static readonly ResourceImageOptions Default = new();
        /// <summary>单张图片允许接收的编码字节上限，不等同于解码后纹理内存。</summary>
        public int MaximumEncodedBytes
        {
            get;
        }
        /// <summary>图片头声明的宽高乘积上限；解码前还须满足设备最大纹理尺寸。</summary>
        public int MaximumPixels
        {
            get;
        }
        /// <summary>无人持有的图片缓存可用 RGBA 像素估算总字节数，零表示不保留闲置图片。</summary>
        public long IdleCapacityBytes
        {
            get;
        }
        /// <summary>无人持有的图片缓存数量上限，不包含仍有租约的图片。</summary>
        public int IdleCapacity
        {
            get;
        }
        /// <summary>图片从无人持有起可闲置的最长时长，由资源系统清理检查时执行淘汰。</summary>
        public TimeSpan IdleTimeToLive
        {
            get;
        }
        /// <summary>本地/URL图片异步producer的并发上限；同图等待者不额外占槽。</summary>
        public int MaximumConcurrentLoads
        {
            get;
        }

        /// <summary>冻结并校验编码大小、像素数、闲置预算与异步并发边界，不预分配图片或缓存。</summary>
        public ResourceImageOptions(int maximumEncodedBytes = 32 * 1024 * 1024, int maximumPixels = 16 * 1024 * 1024,
            long idleCapacityBytes = 32 * 1024 * 1024, TimeSpan? idleTimeToLive = null, int idleCapacity = 128, int maximumConcurrentLoads = 4)
        {
            var ttl = idleTimeToLive ?? TimeSpan.FromSeconds(30);
            if (maximumEncodedBytes < 24 || maximumEncodedBytes > 128 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumEncodedBytes));
            }

            if (maximumPixels <= 0 || maximumPixels > 64 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumPixels));
            }

            if (idleCapacityBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(idleCapacityBytes));
            }

            if (idleCapacity < 0 || idleCapacity > 16384)
            {
                throw new ArgumentOutOfRangeException(nameof(idleCapacity));
            }

            if (ttl < TimeSpan.Zero || ttl > TimeSpan.FromDays(1))
            {
                throw new ArgumentOutOfRangeException(nameof(idleTimeToLive));
            }

            if (maximumConcurrentLoads < 1 || maximumConcurrentLoads > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumConcurrentLoads));
            }

            MaximumEncodedBytes = maximumEncodedBytes;
            MaximumPixels = maximumPixels;
            IdleCapacityBytes = idleCapacityBytes;
            IdleCapacity = idleCapacity;
            IdleTimeToLive = ttl;
            MaximumConcurrentLoads = maximumConcurrentLoads;
        }
    }
}
