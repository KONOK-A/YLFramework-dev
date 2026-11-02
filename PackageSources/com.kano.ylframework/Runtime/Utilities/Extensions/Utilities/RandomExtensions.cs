using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace YLFramework.Extensions
{

    /// <summary>
    /// 使用 Unity 全局随机状态进行百分之一粒度的概率判定。
    /// </summary>
    public static class RandomExtensions
    {

        /// <summary>
        /// 按概率返回 true；不大于 0 时恒为 false，不小于 1 时恒为 true。
        /// 区间内先用 Mathf.RoundToInt 将概率换算为整数百分比，再与 1..100 的随机整数比较。
        /// </summary>
        /// <param name="probability">期望触发概率，通常为 0..1。</param>
        /// <returns>本次随机判定是否触发。</returns>
        public static bool Random(float probability)
        {
            if (probability >= 1)
            {
                return true;
            }

            if (probability <= 0)
            {
                return false;
            }

            int p = Mathf.RoundToInt(probability * 100);
            return UnityEngine.Random.Range(1, 101) <= p;
        }
    }

}
