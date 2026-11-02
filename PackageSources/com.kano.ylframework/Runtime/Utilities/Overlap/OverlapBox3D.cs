using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Overlap
{

    /// <summary>以对象世界位置为中心，按世界旋转和缩放检测三维盒体重叠。</summary>
    public class OverlapBox3D : OverlapBase3D
    {
        #region 字段


        [SerializeField]
        private Vector3 size = Vector3.one;

        #endregion

        #region 属性

        /// <summary>未缩放的盒体完整尺寸；查询时逐轴缩放、取绝对值后减半作为半尺寸。</summary>
        public Vector3 Size
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



        /// <summary>检测当前位置的旋转盒体重叠，过滤触发器后更新进入退出结果。</summary>
        public override void CheckOverlap()
        {
            if (!TryBeginCheck())
            {
                return;
            }

            try
            {
                Vector3 lossy = transform.lossyScale;
                Vector3 s = new Vector3(Mathf.Abs(size.x * lossy.x * 0.5f), Mathf.Abs(size.y * lossy.y * 0.5f), Mathf.Abs(size.z * lossy.z * 0.5f));

                int count = Physics.OverlapBoxNonAlloc(transform.position, s, ResultBuffer, transform.rotation, layerMask.value);
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
