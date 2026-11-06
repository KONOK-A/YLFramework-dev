
using UnityEngine;

namespace YLFramework.Overlap
{
    /// <summary>以对象世界位置为球心，按 XYZ 世界缩放的最大绝对分量缩放半径后检测三维重叠。</summary>
    public class OverlapSphere3D : OverlapBase3D
    {

        #region 字段

        [SerializeField]
        [Tooltip("半径")]
        private float radius = 0.5f;

        #endregion

        #region 属性

        /// <summary>应用世界缩放前的球体半径，赋值本身不校验范围。</summary>
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

        /// <summary>检测当前位置的球体重叠，过滤触发器后更新进入退出结果。</summary>
        public override void CheckOverlap()
        {
            if (!TryBeginCheck())
            {
                return;
            }

            try
            {
                Vector3 scale = transform.lossyScale;
                float r = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)) * radius;
                int count = Physics.OverlapSphereNonAlloc(transform.position, r, ResultBuffer, layerMask.value);

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
