using System.Collections.Generic;
using System.Threading.Tasks;

namespace YLFramework.Samples.HybridCLR.HotUpdate
{
    /// <summary>供 Entry 引用的热更支持程序集，提供版本及泛型、异步调用示例。</summary>
    public static class VersionInfo
    {
        /// <summary>补丁实验使用的版本文本，不表示基座或发布兼容性已通过。</summary>
        public static string Version => "v1";
        /// <summary>通过泛型列表调用展示跨热更程序集依赖。</summary>
        /// <typeparam name="T">样例列表的元素类型。</typeparam>
        /// <param name="items">已创建的样例列表。</param>
        /// <returns>列表当前元素数量。</returns>
        public static int Count<T>(List<T> items) => items.Count;
        /// <summary>经过一次异步让出后返回带当前版本的结果。</summary>
        /// <returns>最终产生版本标记文本的任务，不承诺特定线程执行。</returns>
        public static async Task<string> DescribeAsync()
        {
            await Task.Yield();
            return Version + "-async";
        }
    }
}
