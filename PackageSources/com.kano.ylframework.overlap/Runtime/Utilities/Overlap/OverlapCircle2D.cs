
using UnityEngine;

namespace YLFramework.Overlap
{
    /// <summary>以对象世界位置为圆心，按 XY 世界缩放的最大绝对分量缩放半径后检测重叠。</summary>
    public class OverlapCircle2D : OverlapBase2D
    {
        #region 字段

        [SerializeField]
        private float radius = 0.5f;

        #endregion


        #region 属性
        /// <summary>应用世界缩放前的圆形半径，赋值本身不校验范围。</summary>
        public float Radius
        {
            get
            {
                return radius;
            }
            set
            {
                radius = value;
            }

        }

        #endregion



        /// <summary>检测当前位置的圆形重叠，过滤触发器后更新进入退出结果。</summary>
        public override void CheckOverlap()
        {
            if (!TryBeginCheck())
            {
                return;
            }

            try
            {
                Vector3 s = transform.lossyScale;
                float scale = Mathf.Abs(s.x) > Mathf.Abs(s.y) ? Mathf.Abs(s.x) : Mathf.Abs(s.y);

#if UNITY_6000_0_OR_NEWER
                ContactFilter2D contactFilter = CreateLegacyFilter(layerMask.value, float.NegativeInfinity, float.PositiveInfinity);
                int count = Physics2D.defaultPhysicsScene.OverlapCircle(transform.position, radius * scale, contactFilter, ResultBuffer);
#else
                int count = Physics2D.OverlapCircleNonAlloc(transform.position, radius * scale, ResultBuffer, layerMask.value);
#endif


                ClearResults();
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
