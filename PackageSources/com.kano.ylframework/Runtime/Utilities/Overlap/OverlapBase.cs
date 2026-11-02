using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace YLFramework.Overlap
{

    /// <summary>自动重叠检测在 Update 中累计时间的方式。</summary>
    public enum OverlapUpdateMode
    {
        /// <summary>使用受时间缩放影响的帧时间。</summary>
        [Tooltip("普通模式")]
        Normal,
        /// <summary>使用不受时间缩放影响的帧时间。</summary>
        [Tooltip("忽略时间缩放")]
        UnscaledTime
    }

    /// <summary>保存检测到的碰撞体及查询参考点对应的最近点。</summary>
    /// <typeparam name="T">碰撞体类型。</typeparam>
    [Serializable]
    public struct OverlapHit<T>
    {
        /// <summary>本条结果的碰撞体引用，保存结果不会延长其生命周期。</summary>
        public T collider;
        /// <summary>碰撞体到查询参考点的最近点；即使查询三维碰撞体也只保存 XY 分量。</summary>
        public Vector2 point;
    }




    /// <summary>提供定时查询、结果去重及进入退出通知，具体几何检测由派生类实现。</summary>
    /// <typeparam name="T">检测结果使用的碰撞体类型。</typeparam>
    /// <typeparam name="Event">接收单条检测结果的 UnityEvent 类型。</typeparam>
    /// <remarks>同一实例的回调重入查询会被拒绝；其他实例有独立缓冲区。结果是检测时刻的记录而非持续有效的接触。</remarks>
    public abstract class OverlapBase<T, Event> : MonoBehaviour where Event : UnityEvent<OverlapHit<T>>
    {

        #region 查询状态

        /// <summary>本实例复用的物理查询缓冲区，仅本次返回数量内有效，EndCheck 会清空引用。</summary>
        internal protected readonly T[] ResultBuffer = new T[100];
        private readonly OverlapQueryCycle _queryCycle = new OverlapQueryCycle();
        /// <summary>本轮是否因停用而失效；派生查询应据此停止收集结果及发送后续通知。</summary>
        protected bool IsCheckInvalidated => _queryCycle.IsInvalidated;

        #endregion


        #region 字段

        /// <summary>具体物理查询使用的层位掩码。</summary>
        [SerializeField]
        protected LayerMask layerMask;

        /// <summary>自动检测的目标每秒次数，实际执行受帧率限制。</summary>
        [Range(0.01f, 100)]
        [SerializeField]
        [Tooltip("每秒检测次数(默认:10)")]
        protected float detectionNumber = 10;

        private float _timer;

        /// <summary>自动检测累计帧时间的来源。</summary>
        [SerializeField]
        [Tooltip("更新模式")]
        protected OverlapUpdateMode updateModel;


        /// <summary>是否允许 Update 自动调度查询，不代表此刻存在正在执行的查询。</summary>
        protected bool IsQueryRunning = false;

        /// <summary>是否在 Awake 中打开自动查询开关。</summary>
        [SerializeField]
        [Tooltip("默认运行")]
        protected bool runOnAwake = true;

        /// <summary>上轮结果的可写比较列表，用于本轮进入退出判定，不是独立长期快照。</summary>
        protected List<OverlapHit<T>> LastResults = new List<OverlapHit<T>>();

        #endregion

        #region 属性

        /// <summary>物理查询允许的层位掩码。</summary>
        public LayerMask LayerMask
        {
            get
            {
                return layerMask;
            }
            set
            {
                layerMask = value;
            }
        }

        /// <summary>自动检测目标频率；通过此属性赋值时限制在每秒 0.01 到 100 次之间。</summary>
        public float DetectionNumber
        {
            get
            {
                return detectionNumber;
            }
            set
            {
                detectionNumber = Mathf.Clamp(value, 0.01f, 100);
            }
        }

        /// <summary>自动检测累计帧时间时采用的缩放模式。</summary>
        public OverlapUpdateMode UpdateMode
        {
            get
            {
                return updateModel;
            }
            set
            {
                updateModel = value;
            }
        }

        /// <summary>是否允许自动检测；关闭不清空结果、不取消已进入的查询，也不阻止手动查询。</summary>
        public bool IsRunning
        {
            get
            {
                return IsQueryRunning;
            }
            set
            {
                IsQueryRunning = value;
            }
        }

        /// <summary>直接可写的当前结果列表，下次查询会清空重填，停用时清空，不是只读快照。</summary>
        /// <remarks>进入回调触发时本轮列表可能尚未填完；外部修改会影响后续去重和进入退出比较。</remarks>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Results")]
        [Tooltip("检测结果集合")]
        [HideInInspector]
        public List<OverlapHit<T>> results = new List<OverlapHit<T>>();

        #endregion

        #region 生命周期

        /// <summary>按默认运行配置开启自动检测。</summary>
        protected virtual void Awake()
        {
            if (runOnAwake)
            {
                IsQueryRunning = true;
            }
        }

        /// <summary>使本轮查询失效并清除计时和两轮结果，不合成退出通知，也不改变自动运行开关。</summary>
        protected virtual void OnDisable()
        {
            _queryCycle.Invalidate();
            _timer = 0;
            results.Clear();
            LastResults.Clear();
        }


        /// <summary>按选定帧时间累计查询间隔，累计时间超过阈值时查询一次并清零计时。</summary>
        protected virtual void Update()
        {
            if (!IsQueryRunning)
            {
                return;
            }

            switch (updateModel)
            {
                case OverlapUpdateMode.Normal:
                    _timer += Time.deltaTime;
                    break;
                case OverlapUpdateMode.UnscaledTime:
                    _timer += Time.unscaledDeltaTime;
                    break;
            }

            if (_timer > 1.0f / detectionNumber)
            {
                CheckOverlap();
                _timer = 0;
            }

        }

        #endregion

        #region 方法

        /// <summary>立即执行一次具体重叠检测，不受 IsRunning 自动调度开关限制。</summary>
        public abstract void CheckOverlap();

        /// <summary>尝试开始本实例查询，拒绝尚未结束的同实例重入，成功时重置失效标记。</summary>
        /// <returns>是否取得本轮查询的执行资格；此处不检查组件启用状态。</returns>
        protected bool TryBeginCheck() => _queryCycle.TryBegin();

        /// <summary>清空物理缓冲区并结束重入保护，派生类应在 finally 中调用。</summary>
        protected void EndCheck()
        {
            Array.Clear(ResultBuffer, 0, ResultBuffer.Length);
            _queryCycle.End();
        }

        /// <summary>在 Editor 或 Development Build 中提示可能截断；每次停用重置提示次数。</summary>
        /// <param name="count">物理查询实际写入缓冲区的数量，达到容量只表示可能截断。</param>
        protected void CheckCapacity(int count)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_queryCycle.ReportCapacity(count, ResultBuffer.Length))
            {
                Debug.LogWarning($"{GetType().Name}: query reached capacity {ResultBuffer.Length}; results may be truncated (可能截断).", this);
            }
#endif
        }

        #endregion


        #region 事件

        /// <summary>新碰撞体加入本轮结果且不在上轮结果中时触发，参数为新记录。</summary>
        public Event onColliderEnter;

        /// <summary>上轮碰撞体未出现在本轮结果中时触发，参数沿用旧记录，引用可能已经失效。</summary>
        public Event onColliderExit;

        #endregion


        #region 生命周期

        /// <summary>为派生组件提供编辑器重置配置的入口，基类不修改配置。</summary>
        protected virtual void Reset()
        {
        }

        #endregion


        #region 方法

        /// <summary>将当前结果复制到上轮比较列表，再清空当前结果以开始收集。</summary>
        protected void ClearResults()
        {
            LastResults.Clear();
            LastResults.AddRange(results);
            results.Clear();
        }

        /// <summary>跳过失效查询、已销毁对象和重复项，记录最近点并按需触发进入通知。</summary>
        /// <param name="collider">已经过派生查询过滤的碰撞体。</param>
        /// <param name="origin">世界坐标查询参考点，用于计算碰撞体最近点而非投射命中点。</param>
        protected void AddResults(T collider, Vector3 origin)
        {
            if (IsCheckInvalidated || (collider is UnityEngine.Object unityObject && !unityObject))
            {
                return;
            }

            if (Contains(results, collider))
            {
                return;
            }

            OverlapHit<T> hit = new OverlapHit<T>();
            hit.collider = collider;

            if (collider is Collider2D)
            {
                Collider2D collider2D = collider as Collider2D;
                hit.point = collider2D.ClosestPoint(origin);
            }
            else if (collider is Collider)
            {
                Collider collider3D = collider as Collider;
                hit.point = collider3D.ClosestPoint(origin);
            }

            results.Add(hit);

            if (!Contains(LastResults, collider))
            {
                onColliderEnter?.Invoke(hit);
            }
        }


        /// <summary>比较上轮与本轮结果并发送缺失项的退出通知，停用使查询失效后停止继续通知。</summary>
        protected void OverlapEnd()
        {
            for (int i = 0; i < LastResults.Count && !IsCheckInvalidated; i++)
            {
                var item = LastResults[i];
                if (!Contains(results, item.collider))
                {
                    onColliderExit?.Invoke(item);
                }
            }
        }


        /// <summary>使用 T 的默认相等比较器，检查列表中是否已有同一碰撞体。</summary>
        /// <param name="list">待检查的结果列表。</param>
        /// <param name="collider">待查找的碰撞体。</param>
        protected bool Contains(List<OverlapHit<T>> list, T collider)
        {
            foreach (var item in list)
            {
                if (EqualityComparer<T>.Default.Equals(item.collider, collider))
                {
                    return true;
                }
            }
            return false;
        }

        #endregion


    }
}
