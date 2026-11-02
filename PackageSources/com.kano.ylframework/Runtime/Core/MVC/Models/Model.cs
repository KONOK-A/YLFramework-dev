using System;
using UnityEngine;
using YLFramework.Core.RoslynAnalyzers;

namespace YLFramework.Core
{

    /// <summary>由单个 Module 按具体类型与业务 ID 持有的数据对象，不随 View 关闭而退出。</summary>
    [ProtectedHierarchy(typeof(MonoBehaviour), typeof(View))]
    public abstract class Model
    {
        private long _id = -1;
        internal bool IsInitializing;
        internal bool IsExiting;
        [NonSerialized] internal object Registration;

        #region 属性

        /// <summary>
        /// 业务ID；-1表示注册时自动分配。未注册时可赋值，注册期间不可改变。
        /// </summary>
        public long Id
        {
            get => _id;
            set
            {
                if (Module != null && value != _id)
                {
                    throw new InvalidOperationException("Cannot change the ID of a registered Model.");
                }

                _id = value;
            }
        }

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
        /// 每次有效注册时调用一次；期间可查询自身，但注册尚未提交完成。
        /// </summary>  
        public virtual void OnInit()
        {
        }

        /// <summary>
        /// 正常移除或初始化失败回滚时调用；此时归属仍有效，之后无论是否抛错均解除注册。
        /// </summary>
        public virtual void OnExit()
        {
        }

        #endregion


    }

}
