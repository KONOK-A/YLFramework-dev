using System;

namespace YLFramework.Core
{

    /// <summary>已停用的 Controller 移除策略；当前生命周期统一由所属 Module 管理。</summary>
    [Obsolete("已过时", true)]
    public enum ControllerType
    {
        /// <summary>
        /// 普通类型
        /// </summary>
        Normal,
        /// <summary>
        /// 旧版不可移除标记，当前管理器不再依据此值决定退出行为。
        /// </summary>
        CannotRemove
    }

    /// <summary>由单个 Module 按具体类型唯一持有的业务逻辑对象，可在解除注册后再次注册。</summary>
    public abstract class Controller
    {
        internal bool IsInitializing;
        internal bool IsExiting;

        #region 属性

        /// <summary>旧版移除策略入口，已禁止使用，不参与当前注册与退出判定。</summary>
        [Obsolete("已过时", true)]
        public virtual ControllerType Type => ControllerType.Normal;

        /// <summary>
        /// 拥有者；初始化及退出回调期间有效，移除或初始化失败后为null。
        /// </summary>
        public Module Module
        {
            get; internal set;
        }

        #endregion

        #region 方法
        /// <summary>
        /// 每次有效注册时触发一次；期间可查询自身，但注册尚未提交完成。
        /// </summary>
        public virtual void OnInit()
        {
        }

        /// <summary>
        /// 正常移除或初始化失败回滚时触发；此时归属仍有效，返回或抛错后均解除注册。
        /// </summary>
        public virtual void OnExit()
        {
        }

        #endregion

    }

}
