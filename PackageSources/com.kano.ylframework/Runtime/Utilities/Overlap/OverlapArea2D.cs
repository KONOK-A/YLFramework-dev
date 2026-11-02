using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Overlap
{
    /// <summary>二维矩形区域对角点的坐标来源。</summary>
    public enum OverlapAreaMode
    {

        /// <summary>将配置坐标视为检测器局部坐标，查询前转换为世界坐标。</summary>
        Position,
        /// <summary>直接读取所列 Transform 的当前世界位置。</summary>
        Transform
    }

    /// <summary>将相邻点两两作为轴对齐矩形的对角点，合并二维重叠结果。</summary>
    /// <remarks>不是多边形轮廓检测，也不会自动闭合首尾；节点集合直接可写，检测回调中修改会影响后续遍历。</remarks>
    public class OverlapArea2D : OverlapBase2D
    {

        #region 字段

        [global::UnityEngine.Serialization.FormerlySerializedAs("_mode")]
        [SerializeField]
        [Tooltip("检测坐标方式")]
        private OverlapAreaMode mode = OverlapAreaMode.Position;

        /// <summary>供坐标模式读取的局部节点列表，相邻节点组成一个检测矩形。</summary>
        [SerializeField]
        [Tooltip("检测区域坐标")]
        protected List<Vector3> positions = new List<Vector3>();

        /// <summary>供 Transform 模式读取的节点列表，包含空节点的相邻区域会跳过。</summary>
        [SerializeField]
        [Tooltip("检测区域位置")]
        protected List<Transform> transforms = new List<Transform>();


        #endregion


        #region 属性

        /// <summary>当前查询采用的节点来源，修改后不自动转换已有节点数据。</summary>
        public OverlapAreaMode Mode
        {
            get
            {
                return mode;
            }

            set
            {
                mode = value;
            }

        }

        /// <summary>直接可写的局部坐标节点列表，不是独立副本。</summary>
        public List<Vector3> Positions => positions;

        /// <summary>直接可写的 Transform 节点列表，查询时读取当前世界位置。</summary>
        public List<Transform> Transforms => transforms;

        #endregion


        #region 方法

        /// <summary>查询所有有效相邻矩形并按碰撞体去重；没有有效区域时仍结算旧结果的退出通知。</summary>
        public override void CheckOverlap()
        {
            if (!TryBeginCheck())
            {
                return;
            }

            try
            {
                ClearResults();

                switch (Mode)
                {
                    case OverlapAreaMode.Position:

                        if (positions == null || positions.Count < 2)
                        {
                            break;
                        }

                        for (int i = 0; i < positions.Count - 1 && !IsCheckInvalidated; i++)
                        {
                            Vector3 start = transform.localToWorldMatrix.MultiplyPoint(positions[i]);
                            Vector3 to = transform.localToWorldMatrix.MultiplyPoint(positions[i + 1]);

#if UNITY_6000_0_OR_NEWER
                            ContactFilter2D contactFilter = CreateLegacyFilter(layerMask.value, float.NegativeInfinity, float.PositiveInfinity);
                            int count = Physics2D.defaultPhysicsScene.OverlapArea(start, to, contactFilter, ResultBuffer);
#else
                            int count = Physics2D.OverlapAreaNonAlloc(start, to, ResultBuffer, layerMask.value);
#endif

                            CheckCapacity(count);
                            for (int j = 0; j < count && !IsCheckInvalidated; j++)
                            {
                                if (!ResultBuffer[j] || ResultBuffer[j].isTrigger)
                                {
                                    continue;
                                }

                                AddResults(ResultBuffer[j], Vector3.Lerp(start, to, 0.5f));
                            }

                        }

                        break;
                    case OverlapAreaMode.Transform:


                        if (transforms == null || transforms.Count < 2)
                        {
                            break;
                        }

                        for (int i = 0; i < transforms.Count - 1 && !IsCheckInvalidated; i++)
                        {
                            if (transforms[i] == null || transforms[i + 1] == null)
                            {
                                continue;
                            }

                            Vector3 start = transforms[i].position;
                            Vector3 to = transforms[i + 1].position;

#if UNITY_6000_0_OR_NEWER
                            ContactFilter2D contactFilter = CreateLegacyFilter(layerMask.value, float.NegativeInfinity, float.PositiveInfinity);
                            int count = Physics2D.defaultPhysicsScene.OverlapArea(start, to, contactFilter, ResultBuffer);

#else
                            int count = Physics2D.OverlapAreaNonAlloc(start, to, ResultBuffer, layerMask.value);
#endif

                            CheckCapacity(count);
                            for (int j = 0; j < count && !IsCheckInvalidated; j++)
                            {
                                if (!ResultBuffer[j] || ResultBuffer[j].isTrigger)
                                {
                                    continue;
                                }

                                AddResults(ResultBuffer[j], Vector3.Lerp(start, to, 0.5f));
                            }
                        }

                        break;
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
