using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using YLFramework.Extensions;

namespace YLFramework.Overlap
{

    /// <summary>可序列化的二维重叠结果事件，传递碰撞体与最近点记录。</summary>
    [Serializable]
    public class OverlapEvent2D : UnityEvent<OverlapHit<Collider2D>>
    {
    }

    /// <summary>绑定二维碰撞体与结果事件，并提供二维查询过滤器。</summary>
    /// <remarks>具体形状按层掩码查询后排除触发器，不自动排除检测器自身或子层级碰撞体。</remarks>
    public abstract class OverlapBase2D : OverlapBase<Collider2D, OverlapEvent2D>
    {
        /// <summary>按当前对象层在二维碰撞矩阵中的关系设置默认层掩码。</summary>
        protected override void Reset() => layerMask.Physics2DSetting(gameObject.layer);


        /// <summary>按层掩码、深度及全局触发器查询开关构造二维查询过滤器。</summary>
        /// <param name="layerMask">允许查询的层位掩码。</param>
        /// <param name="minDepth">允许的最小深度。</param>
        /// <param name="maxDepth">允许的最大深度。</param>
        protected ContactFilter2D CreateLegacyFilter(int layerMask, float minDepth, float maxDepth)
        {
            ContactFilter2D result = default(ContactFilter2D);
            result.useTriggers = Physics2D.queriesHitTriggers;
            result.SetLayerMask(layerMask);
            result.SetDepth(minDepth, maxDepth);
            return result;
        }

    }
}
