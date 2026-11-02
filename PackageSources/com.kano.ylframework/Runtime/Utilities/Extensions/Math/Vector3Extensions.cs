using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Extensions
{
    /// <summary>
    /// 提供三维向量的容差反插值和绕指定轴旋转的数值运算。
    /// </summary>
    public static class Vector3Extensions
    {

        /// <summary>
        /// 依次从 x、y、z 分量求裁剪至 0..1 的候选比例，返回首个大于 0 且重建点距 current 不超过 0.01 的比例。
        /// 不做线段投影；未找到候选时返回 0，因此不能区分起点与不匹配的输入。
        /// </summary>
        /// <param name="from">插值起点。</param>
        /// <param name="to">插值终点。</param>
        /// <param name="current">待匹配的向量，须与端点使用相同坐标空间。</param>
        /// <returns>首个通过距离校验的正比例，或未匹配时的 0。</returns>
        public static float InverseLerp(Vector3 from, Vector3 to, Vector3 current)
        {
            float lerp = Mathf.InverseLerp(from.x, to.x, current.x);

            if (lerp > 0 && lerp <= 1)
            {
                if (Vector3.Distance(current, Vector3.Lerp(from, to, lerp)) <= 0.01f)
                {
                    return lerp;
                }
            }

            lerp = Mathf.InverseLerp(from.y, to.y, current.y);

            if (lerp > 0 && lerp <= 1)
            {
                if (Vector3.Distance(current, Vector3.Lerp(from, to, lerp)) <= 0.01f)
                {
                    return lerp;
                }
            }

            lerp = Mathf.InverseLerp(from.z, to.z, current.z);

            if (lerp > 0 && lerp <= 1)
            {
                if (Vector3.Distance(current, Vector3.Lerp(from, to, lerp)) <= 0.01f)
                {
                    return lerp;
                }
            }

            return 0;
        }


        /// <summary>
        /// 将 direction 绕 axis 旋转指定角度，返回旋转后的向量；不修改 Transform，也不归一化输入向量。
        /// </summary>
        /// <param name="direction">待旋转的向量。</param>
        /// <param name="axis">与 direction 使用相同坐标空间的旋转轴，交给 Quaternion.AngleAxis 处理。</param>
        /// <param name="angle">旋转角度，单位为度。</param>
        /// <returns>旋转结果，不改变 direction 的原值。</returns>
        public static Vector3 Rotate(Vector3 direction, Vector3 axis, float angle)
        {
            Quaternion q = Quaternion.AngleAxis(angle, axis);
            return q * direction;
        }

    }

}
