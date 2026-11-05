using System;
using UnityEngine;

namespace YLFramework.Character2D
{
    /// <summary>集中角色运动的重力积分、水平阻挡和投射尺寸计算。</summary>
    internal static class CharacterMotorMath
    {
        /// <summary>按固定步长积分竖直速度，并限制向下速度的大小。</summary>
        /// <param name="speed">当前带方向的竖直速度。</param>
        /// <param name="gravity">世界重力的竖直分量。</param>
        /// <param name="scale">组件采用的重力倍率。</param>
        /// <param name="fixedDeltaTime">本次积分的固定时间步长。</param>
        /// <param name="maxFallingVelocity">下落速度上限，计算时取绝对值。</param>
        internal static float IntegrateGravity(float speed, float gravity, float scale, float fixedDeltaTime, float maxFallingVelocity)
            => Math.Max(speed + gravity * scale * fixedDeltaTime, -Math.Abs(maxFallingVelocity));

        /// <summary>判断水平位移是否与接触法线水平分量反向，即朝接触面内移动。</summary>
        /// <param name="movement">位移的水平分量。</param>
        /// <param name="normal">接触法线的水平分量。</param>
        internal static bool BlocksHorizontal(float movement, float normal)
            => (movement > 0 && normal < 0) || (movement < 0 && normal > 0);

        /// <summary>逐轴缩放局部尺寸并取绝对值，不把旋转后的包围盒作为形状尺寸。</summary>
        /// <param name="localSize">碰撞体的局部尺寸。</param>
        /// <param name="scale">世界缩放的二维分量。</param>
        internal static Vector2 ScaledColliderSize(Vector2 localSize, Vector2 scale)
            => new Vector2(Math.Abs(localSize.x * scale.x), Math.Abs(localSize.y * scale.y));
    }
}
