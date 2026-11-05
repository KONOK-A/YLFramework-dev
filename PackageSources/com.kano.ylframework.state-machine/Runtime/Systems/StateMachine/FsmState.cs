
using UnityEngine;

namespace YLFramework.StateMachine
{
    /// <summary>
    /// 状态脚本的普通 C# 基类，由节点反射创建并转发生命周期，不是 Unity 组件。
    /// </summary>
    public abstract class FsmState
    {

        #region 字段
        /// <summary>
        /// 驱动当前实例的组件，在脚本构造完成后、首次回调前赋值。
        /// </summary>
        public FsmController Controller;

        /// <summary>
        /// 此脚本所属的运行时节点；并非整个控制器唯一的当前状态。
        /// </summary>
        public FsmStateNode CurrentStateInfo;

        /// <summary>
        /// 节点创建时从控制器取得的共享数据引用，后续修改控制器属性不会自动重新绑定。
        /// </summary>
        public object UserData;

        /// <summary>
        /// 最近一次同父层切换记录的来源状态名，首次进入时可能为空。
        /// </summary>
        public string LastState;

        /// <summary>
        /// 退出回调使用的目标状态名；关闭实例没有新目标时，不应依赖该值表示本次去向。
        /// </summary>
        public string NextState;

        #endregion


        #region 属性

        /// <summary>取得宿主组件的 Transform；控制器不存在时返回 null。</summary>
        public Transform Transform => Controller != null ? Controller.transform : null;

        #endregion

        #region 方法

        /// <summary>
        /// 此脚本首次被进入流程访问时调用，先于它的 OnEnter；同一节点实例中最多调用一次。
        /// </summary>
        /// <remarks>调用前即记录已创建；回调抛错或重入都不会撤销记录，尚未轮到的脚本留待下次进入。</remarks>
        public virtual void OnCreate()
        {

        }

        /// <summary>
        /// 每次进入此节点时按脚本配置顺序调用；前序回调使节点退出或重新进入时中断旧轮次。
        /// </summary>
        public virtual void OnEnter()
        {

        }

        /// <summary>
        /// 节点退出时对已调用过 OnCreate 的脚本执行，即使其 OnEnter 尚未执行完成。
        /// </summary>
        public virtual void OnExit()
        {
        }

        /// <summary>
        /// 宿主 Update 阶段向仍在运行的节点转发逻辑更新；并非 Unity 自动调用此普通对象。
        /// </summary>
        public virtual void OnUpdate()
        {
        }

        /// <summary>
        /// 宿主 FixedUpdate 阶段向仍在运行的节点转发固定步长逻辑更新。
        /// </summary>
        public virtual void OnFixedUpdate()
        {
        }

        /// <summary>
        /// 宿主 LateUpdate 阶段向仍在运行的节点转发后置逻辑更新。
        /// </summary>
        public virtual void OnLateUpdate()
        {
        }
        #endregion
    }

}
