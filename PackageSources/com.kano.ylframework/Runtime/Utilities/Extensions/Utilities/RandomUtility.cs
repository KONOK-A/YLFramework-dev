using UnityEngine;

namespace YLFramework.Extensions
{
    /// <summary>
    /// 随机工具，基于 <see cref="UnityEngine.Random"/> 的全局随机状态。
    /// </summary>
    public static class RandomUtility
    {
        /// <summary>
        /// 按概率返回 true。p 为 0~1 的概率：p ≤ 0 恒为 false，p ≥ 1 恒为 true；
        /// 内部使用 [0, 100) 的整数随机数，实际触发概率按 1% 粒度离散（如 p=0.005 实际为 1%）。
        /// </summary>
        /// <param name="p">触发概率。</param>
        public static bool Percent(float p)
        {
            return Random.Range(0, 100) < p * 100;
        }

        /// <summary>
        /// 等概率返回参数中的一个。args 为空时抛 <see cref="System.IndexOutOfRangeException"/>。
        /// </summary>
        /// <param name="args">候选集合。</param>
        public static T Choose<T>(params T[] args)
        {
            return args[Random.Range(0, args.Length)];
        }
    }
}
