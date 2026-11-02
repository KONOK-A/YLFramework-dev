using System;
using UnityEngine;

namespace YLFramework.Extensions
{
    /// <summary>
    /// 对有限非零四元数进行归一化，并按等价旋转的最短角距离计算比例。
    /// </summary>
    public class QuaternionExtensions
    {

        /// <summary>
        /// 返回 current 到 from 的最短旋转角与 from/to 最短角的比值，不限制在 0..1。
        /// 接受有限非零 Quaternion 并归一化；q/-q 等价，同端点返回 0。
        /// 这是角距离比，不判断 current 是否位于同一插值弧上；非法旋转抛 ArgumentException，
        /// 无法表示为有限 float 的比例抛 InvalidOperationException。
        /// </summary>
        /// <param name="from">作为角距离起点的旋转。</param>
        /// <param name="to">确定分母角距离的终点旋转。</param>
        /// <param name="current">待计算相对角距离的旋转。</param>
        /// <returns>起点到 current 的最短角距离除以起终点角距离；端点等价时返回 0。</returns>
        public static float InverseLerp(Quaternion from, Quaternion to, Quaternion current)
        {
            from = NormalizeRotation(from, nameof(from));
            to = NormalizeRotation(to, nameof(to));
            current = NormalizeRotation(current, nameof(current));
            double totalAngle = ShortestAngle(from, to);
            if (totalAngle == 0)
            {
                return 0;
            }

            double ratio = ShortestAngle(from, current) / totalAngle;
            if (ratio > float.MaxValue)
            {
                throw new InvalidOperationException("角距离比例无法表示为有限 float。");
            }

            return (float)ratio;
        }

        /// <summary>计算两个单位四元数最短旋转角的四分之一，供求比例时消去公共因子。</summary>
        private static double ShortestAngle(Quaternion from, Quaternion to)
        {
            double dot = (double)from.x * to.x + (double)from.y * to.y
                + (double)from.z * to.z + (double)from.w * to.w;
            double sign = dot < 0 ? -1 : 1;
            double dx = from.x - sign * to.x, dy = from.y - sign * to.y;
            double dz = from.z - sign * to.z, dw = from.w - sign * to.w;
            double sx = from.x + sign * to.x, sy = from.y + sign * to.y;
            double sz = from.z + sign * to.z, sw = from.w + sign * to.w;
            // atan2(差长, 和长) 保留小角度精度，避免 acos 点积死区。
            return Math.Atan2(Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw),
                Math.Sqrt(sx * sx + sy * sy + sz * sz + sw * sw));
        }

        /// <summary>校验四元数有限且非零并返回单位四元数，失败时用指定参数名抛出异常。</summary>
        private static Quaternion NormalizeRotation(Quaternion value, string parameter)
        {
            double lengthSquared = (double)value.x * value.x + (double)value.y * value.y
                + (double)value.z * value.z + (double)value.w * value.w;
            if (lengthSquared == 0 || double.IsNaN(lengthSquared) || double.IsInfinity(lengthSquared))
            {
                throw new ArgumentException("旋转必须是有限非零 Quaternion。", parameter);
            }

            double length = Math.Sqrt(lengthSquared);
            return new Quaternion((float)(value.x / length), (float)(value.y / length),
                (float)(value.z / length), (float)(value.w / length));
        }

    }

}
