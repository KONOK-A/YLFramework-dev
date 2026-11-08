namespace YLFramework.Core
{

    /// <summary>由单个 Module 按具体类型唯一持有的业务逻辑对象，可在解除注册后再次注册。</summary>
    public abstract class Controller
    {
        internal bool IsInitializing;
        internal bool IsExiting;

        #region 属性

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
