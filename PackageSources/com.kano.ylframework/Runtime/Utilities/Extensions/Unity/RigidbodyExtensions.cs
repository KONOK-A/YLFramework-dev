using System;
using UnityEngine;

namespace YLFramework.Extensions
{
    /// <summary>
    /// 根据世界空间目标和恒定重力求解抛体初速度，并可将结果写入三维或二维刚体。
    /// </summary>
    public static class RigidbodyExtensions
    {
        /// <summary>
        /// 在恒定重力、无阻力及无运动约束的假设下，计算以指定 Y 初速度到达目标的速度。
        /// 使用 useGravity 与 Physics.gravity，取最晚的正到达时间（通常为下落段）。
        /// 零重力可按直线求解；无正到达时间/时间不唯一或结果不可表示时抛 InvalidOperationException。
        /// 非有限输入抛 ArgumentException，本方法不改变刚体。
        /// </summary>
        /// <param name="rigidbody">提供世界起点和重力开关的有效刚体。</param>
        /// <param name="upSpeed">沿世界 Y 轴的初速度，可为负值。</param>
        /// <param name="targetPosition">世界空间目标位置。</param>
        public static Vector3 ParabolicMotionVelocity(this Rigidbody rigidbody, float upSpeed, Vector3 targetPosition)
        {
            if (rigidbody == null)
            {
                throw new ArgumentNullException(nameof(rigidbody));
            }

            return CalculateVelocity(rigidbody.position, upSpeed, targetPosition,
                rigidbody.useGravity ? Physics.gravity : Vector3.zero);
        }

        /// <summary>计算成功后才设置刚体速度；不可达或非法输入时保留原速度。</summary>
        /// <param name="rigidbody">接收世界空间初速度的有效刚体。</param>
        /// <param name="upSpeed">沿世界 Y 轴的初速度。</param>
        /// <param name="targetPosition">世界空间目标位置。</param>
        public static void ParabolicMotion(this Rigidbody rigidbody, float upSpeed, Vector3 targetPosition)
        {
            Vector3 velocity = rigidbody.ParabolicMotionVelocity(upSpeed, targetPosition);
#if UNITY_6000_0_OR_NEWER
            rigidbody.linearVelocity = velocity;
#else
            rigidbody.velocity = velocity;
#endif
        }

        /// <summary>
        /// 二维恒定重力、无阻力及无运动约束的速度计算；使用 Physics2D.gravity * gravityScale（保留方向）。
        /// 忽略目标 Z，取最晚正到达时间；异常与零重力契约同 Rigidbody 重载，不改变刚体。
        /// </summary>
        /// <param name="rigidbody">提供世界起点和 gravityScale 的有效二维刚体。</param>
        /// <param name="upSpeed">沿世界 Y 轴的初速度，可为负值。</param>
        /// <param name="targetPosition">世界空间目标位置，仅使用 x/y。</param>
        public static Vector2 ParabolicMotionVelocity(this Rigidbody2D rigidbody, float upSpeed, Vector3 targetPosition)
        {
            if (rigidbody == null)
            {
                throw new ArgumentNullException(nameof(rigidbody));
            }

            Vector2 acceleration = Physics2D.gravity * rigidbody.gravityScale;
            return CalculateVelocity(rigidbody.position, upSpeed,
                new Vector3(targetPosition.x, targetPosition.y, 0), acceleration);
        }

        /// <summary>计算成功后才设置二维刚体速度；失败时保留原速度。</summary>
        /// <param name="rigidbody">接收世界空间初速度的有效二维刚体。</param>
        /// <param name="upSpeed">沿世界 Y 轴的初速度。</param>
        /// <param name="targetPosition">世界空间目标位置，仅使用 x/y。</param>
        public static void ParabolicMotion(this Rigidbody2D rigidbody, float upSpeed, Vector3 targetPosition)
        {
            Vector2 velocity = rigidbody.ParabolicMotionVelocity(upSpeed, targetPosition);
#if UNITY_6000_0_OR_NEWER
            rigidbody.linearVelocity = velocity;
#else
            rigidbody.velocity = velocity;
#endif
        }

        /// <summary>用竖直方程确定最晚正到达时间，再求解横向初速度，并拒绝非有限或不可达的结果。</summary>
        private static Vector3 CalculateVelocity(Vector3 origin, float upSpeed, Vector3 target, Vector3 acceleration)
        {
            if (!Finite(origin) || !Finite(target) || !Finite(acceleration) || float.IsNaN(upSpeed) || float.IsInfinity(upSpeed))
            {
                throw new ArgumentException("位置、速度和有效重力必须是有限值。");
            }

            double height = (double)target.y - origin.y;
            double time;
            if (acceleration.y == 0)
            {
                if (upSpeed == 0)
                {
                    throw new InvalidOperationException("零竖直速度与零竖直重力无法确定正到达时间。");
                }

                time = height / upSpeed;
            }
            else
            {
                double discriminant = (double)upSpeed * upSpeed + 2.0 * acceleration.y * height;
                if (discriminant < 0)
                {
                    throw new InvalidOperationException("目标高度不可达。");
                }

                double root = Math.Sqrt(discriminant);
                // 稳定求根，避免极小高度差时 -v + sqrt(v*v + 2*a*h) 的消减。
                double q = -upSpeed - (upSpeed >= 0 ? root : -root);
                double first = q / acceleration.y;
                double second = q == 0 ? 0 : -2 * height / q;
                time = Math.Max(first, second);
            }
            if (time <= 0 || double.IsNaN(time) || double.IsInfinity(time))
            {
                throw new InvalidOperationException("目标没有正到达时间。");
            }

            double x = ((double)target.x - origin.x) / time - 0.5 * acceleration.x * time;
            double z = ((double)target.z - origin.z) / time - 0.5 * acceleration.z * time;
            var velocity = new Vector3((float)x, upSpeed, (float)z);
            if (!Finite(velocity))
            {
                throw new InvalidOperationException("所需速度无法表示为有限 float。");
            }

            return velocity;
        }

        /// <summary>判断向量的三个分量是否都既非 NaN 也非无穷大。</summary>
        private static bool Finite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
            && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
