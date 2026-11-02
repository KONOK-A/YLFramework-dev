using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace YLFramework.Overlap
{
    /// <summary>按局部中心偏移、缩放后的尺寸和世界 Z 轴旋转检测二维矩形重叠。</summary>
    public class OverlapBox2D : OverlapBase2D
    {

        #region 字段
        /// <summary>检测中心相对对象原点的局部偏移。</summary>
        [SerializeField]
        protected Vector2 offset;
        /// <summary>应用世界缩放前的矩形完整尺寸，查询时逐轴取缩放结果的绝对值。</summary>
        [SerializeField]
        protected Vector2 size = Vector2.one;

        #endregion


        #region 属性

        /// <summary>检测中心的局部偏移，转换到世界坐标时包含对象的缩放和旋转。</summary>
        public Vector2 Offset
        {
            get
            {
                return offset;
            }
            set
            {
                offset = value;
            }
        }

        /// <summary>未缩放的矩形完整尺寸，不是半尺寸；赋值本身不校验范围。</summary>
        public Vector2 Size
        {
            get
            {
                return size;
            }
            set
            {
                size = value;
            }
        }

        #endregion


        #region 方法

        /// <summary>检测当前位置的矩形重叠并更新进入退出结果，不沿移动方向进行投射。</summary>
        public override void CheckOverlap()
        {
            if (!TryBeginCheck())
            {
                return;
            }

            try
            {
                Vector3 center = transform.localToWorldMatrix.MultiplyPoint(offset);


                ClearResults();

                Vector2 s = size * transform.lossyScale;

                s.Set(Mathf.Abs(s.x), Mathf.Abs(s.y));

#if UNITY_6000_0_OR_NEWER
                ContactFilter2D contactFilter = CreateLegacyFilter(layerMask.value, float.NegativeInfinity, float.PositiveInfinity);
                int count = Physics2D.defaultPhysicsScene.OverlapBox(center, s, transform.eulerAngles.z, contactFilter, ResultBuffer);
#else
                int count = Physics2D.OverlapBoxNonAlloc(center, s, transform.eulerAngles.z, ResultBuffer, layerMask.value);
#endif

                CheckCapacity(count);
                for (int i = 0; i < count && !IsCheckInvalidated; i++)
                {
                    if (!ResultBuffer[i] || ResultBuffer[i].isTrigger)
                    {
                        continue;
                    }

                    AddResults(ResultBuffer[i], center);
                }

                OverlapEnd();
            }
            finally
            {
                EndCheck();
            }

        }

        #endregion


    }

}
