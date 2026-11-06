
using UnityEngine;

namespace YLFramework.Overlap
{
    /// <summary>检测对象当前世界位置在 XY 平面上与哪些二维碰撞体重叠。</summary>
    public class OverlapPoint2D : OverlapBase2D
    {
        /// <summary>执行当前位置的点重叠查询，过滤触发器后更新进入退出结果。</summary>
        public override void CheckOverlap()
        {
            if (!TryBeginCheck())
            {
                return;
            }

            try
            {
                ClearResults();

                ContactFilter2D contactFilter = CreateLegacyFilter(layerMask.value, float.NegativeInfinity, float.PositiveInfinity);
                int count = OverlapQuery2D.RawPoint(Physics2D.defaultPhysicsScene, transform.position, contactFilter, ResultBuffer);
                CheckCapacity(count);
                for (int i = 0; i < count && !IsCheckInvalidated; i++)
                {
                    if (!ResultBuffer[i] || ResultBuffer[i].isTrigger)
                    {
                        continue;
                    }

                    AddResults(ResultBuffer[i], transform.position);
                }

                OverlapEnd();
            }
            finally
            {
                EndCheck();
            }
        }
    }

}
