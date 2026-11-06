using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using YLFramework.Extensions;

namespace YLFramework.Overlap
{

    /// <summary>可序列化的三维重叠结果事件，最近点沿用结果结构的二维坐标。</summary>
    [Serializable]
    public class OverlapEvent3D : UnityEvent<OverlapHit<Collider>>
    {
    }

    /// <summary>绑定三维碰撞体与结果事件，并使用三维碰撞矩阵初始化默认层掩码。</summary>
    /// <remarks>具体形状按层掩码查询后排除触发器，不自动排除检测器自身或子层级碰撞体。</remarks>
    public abstract class OverlapBase3D : OverlapBase<Collider, OverlapEvent3D>
    {
        /// <summary>按当前对象层在三维碰撞矩阵中的关系设置默认层掩码。</summary>
        protected override void Reset() => layerMask.PhysicsSetting(gameObject.layer);
    }
}
