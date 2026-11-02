using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Extensions
{

    /// <summary>
    /// 根据分量候选比例和距离容差，估算二维向量在线段上的插值位置。
    /// </summary>
    public class Vector2Extensions
    {

        /// <summary>
        /// 依次从 x、y 分量求裁剪至 0..1 的候选比例，返回首个大于 0 且重建点距 current 不超过 0.01 的比例。
        /// 不做线段投影；未找到候选时返回 0，因此不能区分起点与不匹配的输入。
        /// </summary>
        /// <param name="from">插值起点。</param>
        /// <param name="to">插值终点。</param>
        /// <param name="current">待匹配的向量，须与端点使用相同坐标空间。</param>
        /// <returns>首个通过距离校验的正比例，或未匹配时的 0。</returns>
        public static float InverseLerp(Vector2 from, Vector2 to, Vector2 current)
        {
            float lerp = Mathf.InverseLerp(from.x, to.x, current.x);

            if (lerp > 0 && lerp <= 1)
            {
                if (Vector2.Distance(current, Vector2.Lerp(from, to, lerp)) <= 0.01f)
                {
                    return lerp;
                }
            }

            lerp = Mathf.InverseLerp(from.y, to.y, current.y);

            if (lerp > 0 && lerp <= 1)
            {
                if (Vector2.Distance(current, Vector2.Lerp(from, to, lerp)) <= 0.01f)
                {
                    return lerp;
                }
            }

            return 0;
        }

    }
}
