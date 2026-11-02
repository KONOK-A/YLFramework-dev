using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Extensions
{
    /// <summary>
    /// 按轴对齐包围盒的最小、最大坐标判断完整包含关系。
    /// </summary>
    public static class BoundsExtensions
    {

        /// <summary>
        /// 判断 bounds 的三个轴向范围是否都落在 b 内，允许边界重合；不执行坐标空间转换。
        /// </summary>
        /// <param name="b">作为外部范围的包围盒。</param>
        /// <param name="bounds">待包含的包围盒，须与 b 使用相同坐标空间。</param>
        /// <returns>各轴最小、最大坐标均满足包含比较时为 true。</returns>
        public static bool Contains(this Bounds b, Bounds bounds)
        {
            if (b.min.x <= bounds.min.x && b.min.y <= bounds.min.y && b.min.z <= bounds.min.z &&
                b.max.x >= bounds.max.x && b.max.y >= bounds.max.y && b.max.z >= bounds.max.z)
            {
                return true;
            }

            return false;
        }

    }

}
